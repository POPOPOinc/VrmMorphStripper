using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Popopo.VrmMorphStripper;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
using UniGLTF.Utils;
using UniVRM10;
using UnityEngine;

namespace Popopo.VrmMorphStripper.Tests
{
    public class Vrm10StripperSampleModelTests
    {
        // Set this to an absolute path for a local VRM integration-test fixture.
        // Leave it empty in the published package; the tests will be skipped.
        private const string SampleModelPath = "";

        [Test]
        public void StripMorphTargets_StripsSampleModelAndPreservesBindIntegrity()
        {
            WithMigratedSample(vrm10Data =>
            {
                var originalTargetCount = CountMorphTargets(vrm10Data.Data.GLTF);
                var originalExpressionCount = EnumerateExpressions(vrm10Data.VrmExtension.Expressions).Count();
                var originalCustomExpressionCount = vrm10Data.VrmExtension.Expressions.Custom?.Count ?? 0;

                Vrm10Stripper.StripMorphTargets(vrm10Data);

                var strippedTargetCount = CountMorphTargets(vrm10Data.Data.GLTF);
                var strippedExpressionCount = EnumerateExpressions(vrm10Data.VrmExtension.Expressions).Count();

                Assert.That(originalTargetCount, Is.GreaterThan(0), "The fixture must contain morph targets.");
                Assert.That(originalExpressionCount, Is.GreaterThan(0), "The fixture must contain expressions.");
                Assert.That(originalCustomExpressionCount, Is.GreaterThan(0), "The fixture must contain custom expressions.");
                Assert.That(strippedTargetCount, Is.LessThan(originalTargetCount), "The default allow-list must remove unused morph targets from the fixture.");
                Assert.That(strippedExpressionCount, Is.LessThanOrEqualTo(originalExpressionCount));
                Assert.That(vrm10Data.VrmExtension.Expressions.Custom, Is.Empty, "The default allow-list must remove custom expressions.");

                AssertMorphTargetBindsAreValid(vrm10Data);
                AssertWeightsMatchTargetCounts(vrm10Data.Data.GLTF);
                AssertTargetNamesMatchTargetCounts(vrm10Data.Data.GLTF);
            });
        }

        [Test]
        public void StripMorphTargets_EmptyAllowListRemovesAllMorphTargets()
        {
            WithMigratedSample(vrm10Data =>
            {
                Vrm10Stripper.StripMorphTargets(vrm10Data, new ExpressionKey[0]);

                Assert.That(CountMorphTargets(vrm10Data.Data.GLTF), Is.Zero);
                Assert.That(EnumerateExpressions(vrm10Data.VrmExtension.Expressions), Is.Empty);
                AssertMorphTargetBindsAreValid(vrm10Data);
                AssertWeightsMatchTargetCounts(vrm10Data.Data.GLTF);
                AssertTargetNamesMatchTargetCounts(vrm10Data.Data.GLTF);
            });
        }

        [Test]
        public void StripMorphTargets_CustomAllowListRetainsOnlyRequestedExpression()
        {
            WithMigratedSample(vrm10Data =>
            {
                var custom = vrm10Data.VrmExtension.Expressions.Custom;
                var selected = custom.First(pair => pair.Value.MorphTargetBinds is { Count: > 0 });

                Vrm10Stripper.StripMorphTargets(vrm10Data, new[] { ExpressionKey.CreateCustom(selected.Key) });

                Assert.That(vrm10Data.VrmExtension.Expressions.Custom.Keys, Is.EquivalentTo(new[] { selected.Key }));
                Assert.That(CountMorphTargets(vrm10Data.Data.GLTF), Is.GreaterThan(0));
                AssertMorphTargetBindsAreValid(vrm10Data);
                AssertWeightsMatchTargetCounts(vrm10Data.Data.GLTF);
                AssertTargetNamesMatchTargetCounts(vrm10Data.Data.GLTF);
            });
        }

        [Test]
        public void StripMorphTargets_SharedMorphTargetIsRemappedForEveryExpression()
        {
            WithMigratedSample(vrm10Data =>
            {
                var custom = vrm10Data.VrmExtension.Expressions.Custom;
                var source = custom.First(pair => pair.Value.MorphTargetBinds is { Count: > 0 });
                var sourceBind = source.Value.MorphTargetBinds[0];
                const string sharedKey = "VrmMorphStripperSharedTest";
                custom.Add(sharedKey, new Expression
                {
                    MorphTargetBinds = new List<MorphTargetBind>
                    {
                        new()
                        {
                            Node = sourceBind.Node,
                            Index = sourceBind.Index,
                            Weight = sourceBind.Weight,
                        },
                    },
                });

                Vrm10Stripper.StripMorphTargets(vrm10Data, new[]
                {
                    ExpressionKey.CreateCustom(source.Key),
                    ExpressionKey.CreateCustom(sharedKey),
                });

                var retainedSourceBind = custom[source.Key].MorphTargetBinds[0];
                var retainedSharedBind = custom[sharedKey].MorphTargetBinds[0];
                Assert.That(custom.Keys, Is.EquivalentTo(new[] { source.Key, sharedKey }));
                Assert.That(retainedSharedBind.Node, Is.EqualTo(retainedSourceBind.Node));
                Assert.That(retainedSharedBind.Index, Is.EqualTo(retainedSourceBind.Index));
                AssertMorphTargetBindsAreValid(vrm10Data);
            });
        }

        [Test]
        public void StripMorphTargets_ImportedInstanceHasExpectedExpressionBreakdown()
        {
            WithMigratedSample(unstrippedVrm10Data =>
            {
                var originalCustomCount = unstrippedVrm10Data.VrmExtension.Expressions.Custom?.Count ?? 0;
                Assert.That(originalCustomCount, Is.GreaterThanOrEqualTo(50),
                    "The Perfect Sync fixture must contain at least 50 custom expressions before stripping.");

                WithImportedInstance(unstrippedVrm10Data, instance =>
                {
                    Assert.That(instance.Vrm.Expression.CustomClips.Count, Is.GreaterThanOrEqualTo(50),
                        "A normally imported Perfect Sync model must expose its large custom expression set.");
                });
            });

            WithMigratedSample(strippedVrm10Data =>
            {
                Vrm10Stripper.StripMorphTargets(strippedVrm10Data);
                var expectedExpressionCount = EnumerateExpressions(strippedVrm10Data.VrmExtension.Expressions).Count();

                WithImportedInstance(strippedVrm10Data, instance =>
                {
                    var importedExpressions = instance.Vrm.Expression;
                    Assert.That(importedExpressions.CustomClips, Is.Empty,
                        "The default allow-list must remove custom expressions from the imported instance.");
                    Assert.That(importedExpressions.Clips.Count(), Is.EqualTo(expectedExpressionCount),
                        "The imported instance must expose exactly the expressions retained in the stripped VRM data.");
                });
            });
        }

        [Test]
        public void StripMorphTargets_ImportedSkinnedMeshRenderersHaveFewerBlendShapes()
        {
            var unstrippedBlendShapeCount = 0;
            WithMigratedSample(unstrippedVrm10Data =>
            {
                WithImportedInstance(unstrippedVrm10Data, instance =>
                {
                    unstrippedBlendShapeCount = CountImportedBlendShapes(instance);
                    Assert.That(unstrippedBlendShapeCount, Is.GreaterThan(0),
                        "The Perfect Sync fixture must load at least one blend shape.");
                });
            });

            var strippedBlendShapeCount = 0;
            WithMigratedSample(strippedVrm10Data =>
            {
                Vrm10Stripper.StripMorphTargets(strippedVrm10Data);

                WithImportedInstance(strippedVrm10Data, instance =>
                {
                    strippedBlendShapeCount = CountImportedBlendShapes(instance);
                });
            });

            var result = $"Imported SkinnedMeshRenderer blend shapes: {unstrippedBlendShapeCount} -> {strippedBlendShapeCount}";
            TestContext.WriteLine(result);
            Debug.Log(result);
            Assert.That(strippedBlendShapeCount, Is.LessThan(unstrippedBlendShapeCount),
                "Stripping must reduce the blend shape count observed on imported SkinnedMeshRenderers.");
        }

        private static void WithMigratedSample(System.Action<Vrm10Data> test)
        {
            if (string.IsNullOrWhiteSpace(SampleModelPath))
            {
                Assert.Ignore("Set SampleModelPath to a local VRM fixture to run integration tests.");
            }

            var sampleModelPath = Path.GetFullPath(SampleModelPath);
            if (!File.Exists(sampleModelPath))
            {
                Assert.Ignore($"Integration-test fixture is not present: {sampleModelPath}");
            }

            using var data = new GlbFileParser(sampleModelPath).Parse();

            var vrm10Data = Vrm10Data.Parse(data);
            if (vrm10Data != null)
            {
                test(vrm10Data);
                return;
            }

            using var migrated = Vrm10Data.Migrate(data, out var migratedVrm10Data, out var migration);

            Assert.That(migrated, Is.Not.Null, $"The VRM 0.x fixture must migrate to VRM 1.0: {migration.Message}");
            Assert.That(migratedVrm10Data, Is.Not.Null);
            test(migratedVrm10Data);
        }

        private static void WithImportedInstance(Vrm10Data vrm10Data, System.Action<Vrm10Instance> test)
        {
            using var importer = new Vrm10Importer(vrm10Data);
            var runtimeGltfInstance = importer.LoadAsync(new ImmediateCaller()).Result;

            var instance = runtimeGltfInstance.GetComponent<Vrm10Instance>();
            Assert.That(instance, Is.Not.Null, "Vrm10Importer must create a Vrm10Instance.");

            try
            {
                test(instance);
            }
            finally
            {
                Object.DestroyImmediate(runtimeGltfInstance.Root);
            }
        }

        private static int CountImportedBlendShapes(Vrm10Instance instance)
        {
            return instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null)
                .Sum(renderer => renderer.sharedMesh.blendShapeCount);
        }

        private static int CountMorphTargets(glTF gltf)
        {
            return gltf.meshes.Sum(mesh => mesh.primitives.Sum(primitive => primitive.targets.Count));
        }

        private static IEnumerable<Expression> EnumerateExpressions(Expressions expressions)
        {
            if (expressions?.Preset != null)
            {
                foreach (var field in typeof(Preset).GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (field.GetValue(expressions.Preset) is Expression expression)
                    {
                        yield return expression;
                    }
                }
            }

            if (expressions?.Custom != null)
            {
                foreach (var expression in expressions.Custom.Values)
                {
                    if (expression != null)
                    {
                        yield return expression;
                    }
                }
            }
        }

        private static void AssertMorphTargetBindsAreValid(Vrm10Data vrm10Data)
        {
            var gltf = vrm10Data.Data.GLTF;
            foreach (var expression in EnumerateExpressions(vrm10Data.VrmExtension.Expressions))
            {
                if (expression.MorphTargetBinds == null) continue;

                foreach (var bind in expression.MorphTargetBinds)
                {
                    Assert.That(bind.Node, Is.Not.Null);
                    Assert.That(bind.Index, Is.Not.Null);

                    var nodeIndex = bind.Node.Value;
                    Assert.That(nodeIndex, Is.InRange(0, gltf.nodes.Count - 1));

                    var meshIndex = gltf.nodes[nodeIndex].mesh;
                    Assert.That(meshIndex, Is.InRange(0, gltf.meshes.Count - 1));

                    var mesh = gltf.meshes[meshIndex];
                    Assert.That(bind.Index.Value, Is.InRange(0, mesh.primitives[0].targets.Count - 1));
                }
            }
        }

        private static void AssertWeightsMatchTargetCounts(glTF gltf)
        {
            foreach (var mesh in gltf.meshes)
            {
                if (mesh.primitives.Count == 0) continue;
                var targetCount = mesh.primitives[0].targets.Count;

                if (mesh.weights != null)
                {
                    Assert.That(mesh.weights.Length, Is.EqualTo(targetCount));
                }

                foreach (var node in gltf.nodes.Where(node => node.mesh == gltf.meshes.IndexOf(mesh) && node.weights != null))
                {
                    Assert.That(node.weights.Length, Is.EqualTo(targetCount));
                }
            }
        }

        private static void AssertTargetNamesMatchTargetCounts(glTF gltf)
        {
            foreach (var mesh in gltf.meshes)
            {
                if (mesh.primitives.Count == 0) continue;
                if (!gltf_mesh_extras_targetNames.TryGet(mesh, out var targetNames)) continue;

                Assert.That(targetNames.Count, Is.EqualTo(mesh.primitives[0].targets.Count));
            }
        }
    }
}
