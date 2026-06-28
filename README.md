# ENC Access Proof — custom 3D model injection for Humankind

A Humankind BepInEx (5 / Mono) plugin that **renders a custom 3D model on a unit in the live game** — no executable
patching. A procedurally-built zeppelin replaces the cruise-missile mesh the ENC zeppelin unit bombards with, keeping
the unit's real skeleton, animation and material. Long thought impossible for a mod; done here with a baked mesh +
this plugin.

## How it works (the working recipe)
The key insight (credit: CalmBreakfast) — **do not inject a custom skeleton** (that silently hangs the GPU skinning
compute). Keep the unit's REAL skeleton and only swap the **mesh** it draws.

1. Bake a mesh into an Amplitude `Skeleton`/`MeshCollection` asset in the Mod Editor (`baker/ZeppelinModel.cs`),
   ship it inert in the mod's `Resources`.
2. Harmony postfix on `AnimationManager.GetMeshCollection(Guid)` (`Patches/ZeppelinInjectPatch.cs`):
   - Load our baked collection by `Amplitude.Framework.Guid`.
   - **Reset its `loadingStatus` → NotLoaded** (it ships `Loaded`, which makes `LoadIFN` a no-op so the GPU upload
     never runs), then **`LoadIFN(...)`** → uploads the mesh to the GPU mesh-content manager and yields a valid
     `MeshIndex`.
   - When `GetMeshCollection` returns the target unit's skeleton, **repoint that real skeleton's
     `skinnedMeshInfos[0].MeshIndex` to our uploaded mesh** (struct-in-array → box/set/write-back). The unit keeps
     its bones/animation/material and draws our mesh.
   - **Reload-robust:** re-apply the swap on every call (the engine resets it on re-present/turn), and re-upload when
     the FX manager instance changes (a save load rebuilds it).

A custom **texture** works too, runtime-injected with no art: set our `Texture2D` on the unit material's `_MainTex`
and re-apply it each frame from `Plugin.Update` (to beat the async proxy-texture rebind). Every unit shares one shader
(`Amplitude/ParticleSkinnedMeshRender`), so the look is purely the material — see the texture section in
[docs/Custom3DInjection-Spec.md](docs/Custom3DInjection-Spec.md). So **mesh and texture are both custom**, entirely
via the plugin (a polished per-unit skin still wants real UVs + texture assets).

## Also proves (foundation)
- Plugin **loads** under BepInEx; a Harmony hook **fires in-game**.
- **Reads the live registry** (`AnimationManager.Content.MeshCollections`).
- **Reaches the configured mod's assets** — scans the `PresentationPawnDefinition` database for `AssetNameFilter`
  (e.g. finds `Era5_Common_Zeppelins_01` for ENC). F8 opens an in-game feedback window of the scan.

## Config file
Auto-created at `<Humankind>\BepInEx\config\community.humankind.encaccessproof.cfg`:
- `TargetMod` (default `ENCReload`) — the mod whose assets to access (label).
- `AssetNameFilter` (default `Zeppelin`) — substring that identifies that mod's assets.
- `ToggleWindowKey` (default `F8`) — opens/closes the feedback window.

## Feedback window
Press **F8** in-game → a draggable window shows the live scan results (registry counts + matching assets) with
**Re-scan** / **Clear** buttons. Auto-scans once when a game loads.

## Real model integration
The plugin renders a real third-party airship — **"Дирижабль HD" by MMD_SonicNewYear** ([Sketchfab](https://sketchfab.com/3d-models/hd-92734a2c283e4d889fecbb010aaf7822), **CC-BY**) — not just the procedural one. `baker/ZeppelinModel.cs` makes an arbitrary FBX engine-ready: combine all parts into one mesh, atlas the hull albedos + remap UVs, force the atlas opaque and paint over its near-black UV dead-zone, normalize scale/orientation, and fix the model's inconsistent winding (radial-outward) so it renders correctly single-sided. The plugin then loads the baked skeleton + atlas by GUID (`MeshIndex` swap for geometry, `_MainTex` for the skin). Model files aren't committed (download them per the CC-BY license into `Assets/Resources/Airship/`).

## Documentation
Full write-ups in [`docs/`](docs/):
- [**FBX-to-Humankind-Pipeline.md**](docs/FBX-to-Humankind-Pipeline.md) — *why it works*, end to end: the render
  pipeline, the runtime injection contract, the FBX→asset baking steps (with the *why* for each), and a **blueprint
  for a Unity package** that generates a Humankind asset from an FBX. **Start here for building tooling.**
- [**Custom3DInjection-Spec.md**](docs/Custom3DInjection-Spec.md) — the complete working recipe + the decompiled
  pipeline (AnimationManager / MeshCollection / Skeleton / PawnManager / AddOn / FragmentEntry) and how each blocker
  fell. Start here.
- [**Custom3DModels-Findings-Shareable.md**](docs/Custom3DModels-Findings-Shareable.md) — shareable findings & plan.
- [**UnitPreview-Findings.md**](docs/UnitPreview-Findings.md) — the full investigation log (data-mod limits, the
  skeleton-registry wall, the BepInEx route).

## Build
Needs only the .NET SDK. All DLLs are local (BepInEx isn't on nuget.org — same setup as GUI-Tools/shakee).

1. Create a `References\` folder next to the `.csproj` and copy in (from your install):
   - `<Humankind>\BepInEx\core\`: `BepInEx.dll`, `0Harmony.dll`
   - `<Humankind>\Humankind_Data\Managed\`: `UnityEngine.dll`, `UnityEngine.CoreModule.dll`,
     `UnityEngine.IMGUIModule.dll`, `UnityEngine.InputLegacyModule.dll`, `Amplitude.Mercury.Animation.dll`
     *(`UnityEngine.dll` — the monolithic facade — is required because BepInEx's `BaseUnityPlugin : MonoBehaviour`
     resolves through it.)*
2. `dotnet build -c Release`
3. Copy `bin\Release\ENCAccessProof.dll` → `<Humankind>\BepInEx\plugins\`
4. Launch HK, load a save, press **F8** (and/or check `BepInEx\LogOutput.log` for `[ENCProof]` lines).

## Dependencies
- BepInEx 5 + HarmonyX + Unity + Amplitude — all via the gitignored `References\` folder (copied from your
  install; not redistributable). `Microsoft.NETFramework.ReferenceAssemblies` (NuGet) only enables the net471 build.
