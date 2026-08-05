# VRM Morph Stripper

VRM Morph Stripper is a Unity runtime utility, built for use with
[UniVRM](https://github.com/vrm-c/UniVRM), for reducing the morph target data
in a parsed VRM 1.0 model before it is loaded into Unity. It operates on the
`Vrm10Data` produced by UniVRM's parser, retaining only the morph targets
referenced by the expressions you choose, remapping the retained bindings, and
removing unselected expressions.

Use it when an application only needs a limited set of facial expressions and
does not need to keep the rest of a model's morph target payload in memory.

This repository contains the distributable Unity package at
`Packages/VrmMorphStripper` and a minimal Unity project used for
development and verification.

## Requirements

- Unity 2022.3 LTS or later
- UniVRM 0.131.1
  - `com.vrmc.gltf`
  - `com.vrmc.vrm` (VRM 1.0)

## Install

Add UniVRM and this package to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.popopo.vrm-morph-stripper": "https://github.com/POPOPOinc/VrmMorphStripper.git?path=/Packages/VrmMorphStripper"
  }
}
```

The `path` query selects the directory in the Git repository that contains
this package's `package.json`. To pin a released version, append the Git ref
after the query, for example:

```text
https://github.com/POPOPOinc/VrmMorphStripper.git?path=/Packages/VrmMorphStripper#v0.1.0
```

## Sample

Parse a VRM file, migrate VRM 0.x files when necessary, strip the morph targets
before creating a Unity instance, and then load the resulting VRM 1.0 data with
UniVRM:

```csharp
using Popopo.VrmMorphStripper;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;

void LoadStrippedVrm(string vrmPath)
{
    using var sourceData = new GlbFileParser(vrmPath).Parse();

    // VRM 1.0 can be stripped directly.
    var vrm10Data = Vrm10Data.Parse(sourceData);
    if (vrm10Data != null)
    {
        StripAndLoad(vrm10Data);
        return;
    }

    // Migrate a VRM 0.x file before stripping it.
    using var migratedData = Vrm10Data.Migrate(sourceData, out vrm10Data, out var migration);
    if (migratedData == null || vrm10Data == null)
    {
        throw new System.ArgumentException($"Could not migrate the VRM file: {migration.Message}", nameof(vrmPath));
    }

    StripAndLoad(vrm10Data);
}

void StripAndLoad(Vrm10Data vrm10Data)
{
    Vrm10Stripper.StripMorphTargets(
        vrm10Data,
        new[]
        {
            ExpressionKey.Happy,
            ExpressionKey.Blink,
            ExpressionKey.Aa,
        });

    using var importer = new Vrm10Importer(vrm10Data);
    importer.LoadAsync(new ImmediateCaller()).Wait();
}
```

Pass `null` as the allow-list to retain all preset expressions. The method
modifies `Vrm10Data` in place. It only strips expression-related morph target
data; it does not optimize unrelated VRM or glTF content.

## Integration-test fixture

The package includes Editor integration tests that document the expected
stripping and import checks. No VRM model is distributed with this repository.
To run them locally, set `SampleModelPath` in
`Packages/VrmMorphStripper/Tests/Editor/Vrm10StripperSampleModelTests.cs` to
the absolute path of a local fixture. Leave it empty to skip these tests.

The fixture may be VRM 1.0 or VRM 0.x: VRM 1.0 is parsed directly and VRM 0.x
is migrated before the tests run. Choose a model with many custom expressions
and blend shapes, such as a Perfect Sync-compatible model. The tests check
that custom expressions are removed by the default allow-list, expression
bindings remain valid, and imported `SkinnedMeshRenderer` blend shape counts
decrease.

The development fixture used while preparing this package produced `124 -> 15`
imported blend shapes with UniVRM 0.131.1. This is a recorded reference result,
not a universal expected count for other models.

## License

MIT — see [LICENSE](LICENSE).
