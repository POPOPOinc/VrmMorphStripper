using System;
using System.Collections.Generic;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
using UniJSON;
using UniVRM10;

namespace Popopo.VrmMorphStripper
{
    /// <summary>
    /// Removes unneeded expression morph target data from VRM 1.0 data before it is loaded into Unity.
    /// </summary>
    public static class Vrm10Stripper
    {
        private static readonly ExpressionKey[] DefaultExpressionAllowList =
        {
            ExpressionKey.Happy,
            ExpressionKey.Angry,
            ExpressionKey.Sad,
            ExpressionKey.Relaxed,
            ExpressionKey.Surprised,
            ExpressionKey.Aa,
            ExpressionKey.Ih,
            ExpressionKey.Ou,
            ExpressionKey.Ee,
            ExpressionKey.Oh,
            ExpressionKey.Blink,
            ExpressionKey.BlinkLeft,
            ExpressionKey.BlinkRight,
            ExpressionKey.LookUp,
            ExpressionKey.LookDown,
            ExpressionKey.LookLeft,
            ExpressionKey.LookRight,
            ExpressionKey.Neutral,
        };

        /// <summary>
        /// Removes morph target data that is not referenced by the retained expressions.
        /// This method only strips expression-related morph target data and modifies <paramref name="vrm10Data"/> in place.
        /// </summary>
        /// <param name="vrm10Data">The VRM 1.0 data to modify in place.</param>
        /// <param name="expressionAllowList">Expression keys to retain. When null, all preset expressions are retained and custom expressions are removed.</param>
        /// <param name="log">Optional callback that receives diagnostic messages.</param>
        public static void StripMorphTargets(
            Vrm10Data vrm10Data,
            IReadOnlyCollection<ExpressionKey>? expressionAllowList = null,
            Action<string>? log = null)
        {
            expressionAllowList ??= DefaultExpressionAllowList;

            var expressions = vrm10Data.VrmExtension?.Expressions;
            if (expressions == null) return;

            var gltf = vrm10Data.Data.GLTF;
            var allowExpressionKeys = new HashSet<ExpressionKey>(expressionAllowList, ExpressionKey.Comparer);

            // 1. Pick kept expressions.
            var collectedExpressions = new List<Expression>();
            CollectPreset(expressions.Preset, allowExpressionKeys, collectedExpressions);
            CollectCustom(expressions.Custom, allowExpressionKeys, collectedExpressions);

            // 2. Aggregate the morph indices that kept expressions actually reference.
            var morphIndexByMesh = new Dictionary<int, HashSet<int>>();
            foreach (var expression in collectedExpressions)
            {
                if (expression.MorphTargetBinds == null) continue;
                foreach (var bind in expression.MorphTargetBinds)
                {
                    if (!TryResolveMesh(gltf, bind, out var meshIndex, out var morphIndex)) continue;
                    if (!morphIndexByMesh.TryGetValue(meshIndex, out var set))
                    {
                        set = new HashSet<int>();
                        morphIndexByMesh[meshIndex] = set;
                    }
                    set.Add(morphIndex);
                }
            }

            // 3. Rewrite primitives.targets + extras.targetNames + mesh/node.weights for every mesh.
            //    Meshes with no entry in morphIndexByMesh have ALL targets dropped.
            var totalOriginal = 0;
            var totalKept = 0;
            long totalBytesDropped = 0;

            var remapPerMesh = new Dictionary<int, Dictionary<int, int>>();
            for (var meshIdx = 0; meshIdx < gltf.meshes.Count; meshIdx++)
            {
                var mesh = gltf.meshes[meshIdx];
                if (mesh.primitives is not { Count: > 0 }) continue;

                // glTF 2.0: all primitives in a mesh share the same morph target count & order.
                var originalMorphCount = mesh.primitives[0].targets.Count;
                if (originalMorphCount <= 0) continue;

                morphIndexByMesh.TryGetValue(meshIdx, out var morphIndices);

                var keepTargetIndices = new List<int>(originalMorphCount);
                var remap = new Dictionary<int, int>();
                for (var i = 0; i < originalMorphCount; i++)
                {
                    if (morphIndices != null && morphIndices.Contains(i))
                    {
                        remap[i] = keepTargetIndices.Count;
                        keepTargetIndices.Add(i);
                    }
                }
                remapPerMesh[meshIdx] = remap;

                var droppedCount = originalMorphCount - keepTargetIndices.Count;
                totalOriginal += originalMorphCount;
                totalKept += keepTargetIndices.Count;

                if (droppedCount > 0)
                {
                    long meshBytesDropped = 0;
                    var primTargets = mesh.primitives[0].targets;
                    for (var i = 0; i < originalMorphCount; i++)
                    {
                        if (remap.ContainsKey(i)) continue; // kept
                        meshBytesDropped += MorphAccessorByteSize(gltf, primTargets[i]);
                    }
                    totalBytesDropped += meshBytesDropped;
                    log?.Invoke($"[Vrm10Stripper] mesh[{meshIdx}] '{mesh.name}': {droppedCount}/{originalMorphCount} morphs dropped ({FormatBytes(meshBytesDropped)})");
                }

                if (keepTargetIndices.Count == originalMorphCount) continue; // nothing to drop

                // mesh.weights filter
                if (mesh.weights != null! && mesh.weights.Length == originalMorphCount)
                {
                    var newWeights = new float[keepTargetIndices.Count];
                    for (var k = 0; k < keepTargetIndices.Count; k++)
                    {
                        newWeights[k] = mesh.weights[keepTargetIndices[k]];
                    }
                    mesh.weights = newWeights;
                }

                // node.weights filter for all nodes referencing this mesh
                foreach (var node in gltf.nodes)
                {
                    if (node.mesh != meshIdx || node.weights == null!) continue;
                    if (node.weights.Length != originalMorphCount) continue;
                    var newNodeWeights = new float[keepTargetIndices.Count];
                    for (var k = 0; k < keepTargetIndices.Count; k++)
                    {
                        newNodeWeights[k] = node.weights[keepTargetIndices[k]];
                    }
                    node.weights = newNodeWeights;
                }

                StripPrimitiveTargets(mesh, keepTargetIndices);
                StripTargetNames(mesh, keepTargetIndices);
            }

            // 4. Remap kept expressions' MorphTargetBinds to the new indices.
            foreach (var expression in collectedExpressions)
            {
                if (expression.MorphTargetBinds == null) continue;
                var newBinds = new List<MorphTargetBind>(expression.MorphTargetBinds.Count);
                foreach (var bind in expression.MorphTargetBinds)
                {
                    if (!TryResolveMesh(gltf, bind, out var meshIdx, out var morphIdx)) continue;
                    if (!remapPerMesh.TryGetValue(meshIdx, out var remap)) continue;
                    if (!remap.TryGetValue(morphIdx, out var newIndex)) continue;
                    bind.Index = newIndex;
                    newBinds.Add(bind);
                }
                expression.MorphTargetBinds = newBinds;
            }

            // 5. Drop expressions not in the allow-list.
            var presetsDropped = NullOutDroppedPresets(expressions.Preset, allowExpressionKeys);
            var customDropped = RemoveDroppedCustom(expressions.Custom, allowExpressionKeys);

            var totalDropped = totalOriginal - totalKept;
            log?.Invoke($"[Vrm10Stripper] DONE: {totalDropped}/{totalOriginal} morphs dropped, {FormatBytes(totalBytesDropped)} saved (kept {totalKept}). Expressions dropped: {presetsDropped} preset + {customDropped} custom.");
        }

        static long MorphAccessorByteSize(glTF gltf, gltfMorphTarget t)
        {
            long size = 0;
            if (t.POSITION >= 0 && t.POSITION < gltf.accessors.Count)
                size += gltf.accessors[t.POSITION].CalcByteSize();
            if (t.NORMAL >= 0 && t.NORMAL < gltf.accessors.Count)
                size += gltf.accessors[t.NORMAL].CalcByteSize();
            if (t.TANGENT >= 0 && t.TANGENT < gltf.accessors.Count)
                size += gltf.accessors[t.TANGENT].CalcByteSize();
            return size;
        }

        static string FormatBytes(long bytes) => bytes switch
        {
            >= 1024L * 1024 => $"{bytes / 1024.0 / 1024.0:F2} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };

        static bool TryResolveMesh(glTF gltf, MorphTargetBind bind, out int meshIndex, out int morphIndex)
        {
            meshIndex = -1;
            morphIndex = -1;
            if (bind is not { Node: not null } || !bind.Index.HasValue) return false;

            var nodeIdx = bind.Node.Value;
            if (nodeIdx < 0 || nodeIdx >= gltf.nodes.Count) return false;

            var mesh = gltf.nodes[nodeIdx].mesh;
            if (mesh < 0 || mesh >= gltf.meshes.Count) return false;

            meshIndex = mesh;
            morphIndex = bind.Index.Value;
            return true;
        }

        static void StripPrimitiveTargets(glTFMesh mesh, List<int> keepTargetIndices)
        {
            foreach (var primitive in mesh.primitives)
            {
                if (primitive.targets.Count <= 0) continue;
                var newTargets = new List<gltfMorphTarget>(keepTargetIndices.Count);
                foreach (var i in keepTargetIndices)
                {
                    if (i < primitive.targets.Count) newTargets.Add(primitive.targets[i]);
                }
                primitive.targets = newTargets;
            }
        }

        static void StripTargetNames(glTFMesh mesh, List<int> keepTargetIndices)
        {
            // The auto-numbered fallback (no extras present) re-numbers itself off
            // the new primitives.targets count, so we only need to rewrite explicit
            // extras. Some exporters put targetNames on both mesh and primitive;
            // update every stored copy to keep them mutually consistent.
            if (mesh.extras is glTFExtensionImport meshExtras
                && TryReadStringList(meshExtras, gltf_mesh_extras_targetNames.ExtraNameUtf8, out var meshNames))
            {
                mesh.extras = ReplaceExtrasKey(mesh.extras, gltf_mesh_extras_targetNames.ExtraName,
                    FilterTargetNames(meshNames, keepTargetIndices));
            }

            foreach (var primitive in mesh.primitives)
            {
                if (primitive.extras is glTFExtensionImport primitiveExtras
                    && TryReadStringList(primitiveExtras, gltf_mesh_extras_targetNames.ExtraNameUtf8, out var primitiveNames))
                {
                    primitive.extras = ReplaceExtrasKey(primitive.extras, gltf_mesh_extras_targetNames.ExtraName,
                        FilterTargetNames(primitiveNames, keepTargetIndices));
                }
            }
        }

        static List<string> FilterTargetNames(List<string> names, List<int> keepTargetIndices)
        {
            var filtered = new List<string>(keepTargetIndices.Count);
            foreach (var i in keepTargetIndices)
            {
                filtered.Add(i < names.Count ? names[i] : i.ToString());
            }
            return filtered;
        }

        static bool TryReadStringList(glTFExtensionImport extras, Utf8String key, out List<string> values)
        {
            foreach (var kv in extras.ObjectItems())
            {
                if (kv.Key.GetUtf8String() == key)
                {
                    values = new List<string>();
                    if (kv.Value.Value.ValueType == ValueNodeType.Array)
                    {
                        foreach (var item in kv.Value.ArrayItems())
                        {
                            values.Add(item.GetString());
                        }
                    }
                    return true;
                }
            }
            values = null!;
            return false;
        }

        static glTFExtension ReplaceExtrasKey(glTFExtension original, string key, List<string> values)
        {
            var f = new JsonFormatter();
            f.BeginList();
            foreach (var value in values) f.Value(value);
            f.EndList();

            var newExport = new glTFExtensionExport();
            if (original is glTFExtensionImport import)
            {
                foreach (var kv in import.ObjectItems())
                {
                    var keyStr = kv.Key.GetString();
                    if (keyStr == key) continue;
                    newExport.Add(keyStr, kv.Value.Value.Bytes);
                }
            }
            newExport.Add(key, f.GetStore().Bytes);
            return newExport;
        }

        static void CollectPreset(Preset? preset, HashSet<ExpressionKey> allow, List<Expression> result)
        {
            if (preset == null) return;
            if (preset.Happy != null && allow.Contains(ExpressionKey.Happy)) result.Add(preset.Happy);
            if (preset.Angry != null && allow.Contains(ExpressionKey.Angry)) result.Add(preset.Angry);
            if (preset.Sad != null && allow.Contains(ExpressionKey.Sad)) result.Add(preset.Sad);
            if (preset.Relaxed != null && allow.Contains(ExpressionKey.Relaxed)) result.Add(preset.Relaxed);
            if (preset.Surprised != null && allow.Contains(ExpressionKey.Surprised)) result.Add(preset.Surprised);
            if (preset.Aa != null && allow.Contains(ExpressionKey.Aa)) result.Add(preset.Aa);
            if (preset.Ih != null && allow.Contains(ExpressionKey.Ih)) result.Add(preset.Ih);
            if (preset.Ou != null && allow.Contains(ExpressionKey.Ou)) result.Add(preset.Ou);
            if (preset.Ee != null && allow.Contains(ExpressionKey.Ee)) result.Add(preset.Ee);
            if (preset.Oh != null && allow.Contains(ExpressionKey.Oh)) result.Add(preset.Oh);
            if (preset.Blink != null && allow.Contains(ExpressionKey.Blink)) result.Add(preset.Blink);
            if (preset.BlinkLeft != null && allow.Contains(ExpressionKey.BlinkLeft)) result.Add(preset.BlinkLeft);
            if (preset.BlinkRight != null && allow.Contains(ExpressionKey.BlinkRight)) result.Add(preset.BlinkRight);
            if (preset.LookUp != null && allow.Contains(ExpressionKey.LookUp)) result.Add(preset.LookUp);
            if (preset.LookDown != null && allow.Contains(ExpressionKey.LookDown)) result.Add(preset.LookDown);
            if (preset.LookLeft != null && allow.Contains(ExpressionKey.LookLeft)) result.Add(preset.LookLeft);
            if (preset.LookRight != null && allow.Contains(ExpressionKey.LookRight)) result.Add(preset.LookRight);
            if (preset.Neutral != null && allow.Contains(ExpressionKey.Neutral)) result.Add(preset.Neutral);
        }

        static void CollectCustom(Dictionary<string, Expression>? custom, HashSet<ExpressionKey> allow, List<Expression> result)
        {
            if (custom == null) return;
            foreach (var kv in custom)
            {
                if (kv.Value == null) continue;
                if (allow.Contains(ExpressionKey.CreateCustom(kv.Key))) result.Add(kv.Value);
            }
        }

        static int NullOutDroppedPresets(Preset? preset, HashSet<ExpressionKey> allow)
        {
            if (preset == null) return 0;
            var n = 0;
            if (preset.Happy != null && !allow.Contains(ExpressionKey.Happy)) { preset.Happy = null; n++; }
            if (preset.Angry != null && !allow.Contains(ExpressionKey.Angry)) { preset.Angry = null; n++; }
            if (preset.Sad != null && !allow.Contains(ExpressionKey.Sad)) { preset.Sad = null; n++; }
            if (preset.Relaxed != null && !allow.Contains(ExpressionKey.Relaxed)) { preset.Relaxed = null; n++; }
            if (preset.Surprised != null && !allow.Contains(ExpressionKey.Surprised)) { preset.Surprised = null; n++; }
            if (preset.Aa != null && !allow.Contains(ExpressionKey.Aa)) { preset.Aa = null; n++; }
            if (preset.Ih != null && !allow.Contains(ExpressionKey.Ih)) { preset.Ih = null; n++; }
            if (preset.Ou != null && !allow.Contains(ExpressionKey.Ou)) { preset.Ou = null; n++; }
            if (preset.Ee != null && !allow.Contains(ExpressionKey.Ee)) { preset.Ee = null; n++; }
            if (preset.Oh != null && !allow.Contains(ExpressionKey.Oh)) { preset.Oh = null; n++; }
            if (preset.Blink != null && !allow.Contains(ExpressionKey.Blink)) { preset.Blink = null; n++; }
            if (preset.BlinkLeft != null && !allow.Contains(ExpressionKey.BlinkLeft)) { preset.BlinkLeft = null; n++; }
            if (preset.BlinkRight != null && !allow.Contains(ExpressionKey.BlinkRight)) { preset.BlinkRight = null; n++; }
            if (preset.LookUp != null && !allow.Contains(ExpressionKey.LookUp)) { preset.LookUp = null; n++; }
            if (preset.LookDown != null && !allow.Contains(ExpressionKey.LookDown)) { preset.LookDown = null; n++; }
            if (preset.LookLeft != null && !allow.Contains(ExpressionKey.LookLeft)) { preset.LookLeft = null; n++; }
            if (preset.LookRight != null && !allow.Contains(ExpressionKey.LookRight)) { preset.LookRight = null; n++; }
            if (preset.Neutral != null && !allow.Contains(ExpressionKey.Neutral)) { preset.Neutral = null; n++; }
            return n;
        }

        static int RemoveDroppedCustom(Dictionary<string, Expression>? custom, HashSet<ExpressionKey> allow)
        {
            if (custom == null || custom.Count == 0) return 0;
            List<string>? drop = null;
            foreach (var kv in custom)
            {
                if (!allow.Contains(ExpressionKey.CreateCustom(kv.Key)))
                {
                    (drop ??= new List<string>()).Add(kv.Key);
                }
            }
            if (drop != null)
            {
                foreach (var k in drop) custom.Remove(k);
                return drop.Count;
            }
            return 0;
        }
    }
}
