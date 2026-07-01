# ENC Access Proof — Universal custom-3D-model injection for Humankind

Inject **any** custom 3D model onto **any** live Humankind unit — correct geometry, correct texture, no executable
patching, and **zero per-model code**. Bake a model in the Unity editor tool; a data-driven BepInEx plugin renders it
in-game, driven entirely by a JSON registry. What started as a single procedural zeppelin is now the **Universal Model
Factory**.

## What works (proven in-game)
- **Three models live**, each added with no new code: a **Zeppelin**, an **LCAC Hovercraft**, and a fully-textured
  **USS Zumwalt stealth cruiser** (first textured naval-combat unit) — correct orientation, correct skin, sitting at
  the waterline.
- **Any number of materials.** A model with N materials (the Zeppelin has 4) is packed into one atlas and each
  sub-mesh's UVs are remapped into its rect — no per-model code, no material cap. Near-black UV dead-zones are filled
  neutral so unused texture regions never render as black patches.
- **Any format in:** GLB / glTF / OBJ / FBX, and **`.blend`** (auto-converted via an auto-detected Blender install).
- **Correct textures out of the box:** the GLB→OBJ converter flips V (glTF is V-top, OBJ/Unity V-bottom), so skins map
  right — the bug that made the Zumwalt's markings land on the superstructure, now fixed for every model.
- **Texture isolation:** each model gets a private `FxOutputLayer` clone, so its skin never bleeds onto the vanilla
  donor unit — proven on screen with a custom cruiser and its donor corvette side-by-side, each keeping its own skin.
- **Add a model = bake it.** The Factory writes the registry; the plugin picks it up on next launch.

## Zero-config adoption
Built to work on a stranger's machine, not just the author's:
- **Auto-detects the game.** No hardcoded paths — the Factory finds Humankind via Steam's library config (with a manual
  override in a Settings panel for odd layouts). Blender is auto-located too.
- **No .NET install needed.** The GLB/glTF converter ships as a self-contained single-file `glbconv.exe` that carries
  its own runtime — adopters don't need a .NET SDK or runtime.
- **One injection path.** A single `UniversalInject` drives every model from the registry (the old per-unit and
  content-merge patches are retired), so there's one code path to understand and one to trust.

## How it works
**Editor — the Universal Model Factory** (`baker/`, *Tools ▸ Universal Model Factory*): pick a target unit + a model
file, set rotation / position / size / normals, **Bake**. `UniversalBaker` imports it (copying sibling textures so
multi-material albedos resolve), packs one atlas across all materials, remaps UVs, and bakes an Amplitude `Skeleton` on
the proven vehicle rig; `ModelRegistry` writes `enc_models.json` into the auto-detected `BepInEx/config`.

**Runtime — `UniversalInject`** (`Patches/`): one patch, any number of models. Reads the registry, registers each baked
skeleton, and on `AddOn.Load` repoints the matching pawn onto it by **self-discovery** (reads the host's body-mesh name,
renames ours to match). The unit keeps its real bones + animation; our skin rides on the private layer clone.

*Founding insight (credit: CalmBreakfast): never inject a custom skeleton — it hangs the GPU skinning. Keep the unit's
real skeleton and swap only the mesh it draws.*

## Models & licenses
Model files aren't committed — download each per its license into `Assets/Resources/<name>/` and bake. Authors +
licenses in [**CREDITS.md**](CREDITS.md) (CC-BY requires attribution).

## Config
- **Registry:** `<Humankind>\BepInEx\config\enc_models.json` — the Factory auto-detects this path (Settings panel to
  override); the plugin reads it at launch.
- **Plugin cfg:** `…\community.humankind.encaccessproof.cfg` — F8 opens an in-game scan/feedback window.

## Known issues
- **Editor-only texture preview:** right after a multi-material bake, Unity may show the baked atlas stale until the
  source textures are touched (open them in the Project view, return to the model). The shipped/in-game result is
  correct — this is a Unity editor texture-residency quirk, not a bake defect.
- **Multi-material GLB:** the GLB→OBJ converter currently flattens materials into one group, so multi-material *GLB*
  loses its per-material split. Use **FBX** for multi-material models (fully supported); GLB is fine for single-material.

## Docs
Full write-ups in [`docs/`](docs/) — **start with [Scaling-ManyModels-And-Scoping.md](docs/Scaling-ManyModels-And-Scoping.md)**
(architecture, per-model recipes, texture scoping, `.blend` import, and the full debugging log incl. the UV V-flip root
cause). Also: [FBX-to-Humankind-Pipeline.md](docs/FBX-to-Humankind-Pipeline.md), [Custom3DInjection-Spec.md](docs/Custom3DInjection-Spec.md)
(decompiled pipeline), [Custom3DModels-Findings-Shareable.md](docs/Custom3DModels-Findings-Shareable.md), [UnitPreview-Findings.md](docs/UnitPreview-Findings.md).

## Toward a Unity package
Goal: ship the Factory as a distributable Unity package. **Done:** zero-config path auto-detection, self-contained
converter (no .NET dependency), one consolidated injection path, and full multi-material support. **Remaining:** neutral
naming (drop "ENC" → `HumankindModelFactory`), package scaffolding (`package.json` / asmdef / LICENSE), single-DLL plugin
packaging, an install guide + quickstart, and (optional) multi-material GLB. Editor scripts are mirrored in `baker/`
(the ENCReload mod repo tracks only its `Databases`).

## Build (plugin)
Needs the .NET SDK. Put a `References\` folder next to the `.csproj` with `BepInEx.dll` + `0Harmony.dll` (from
`BepInEx\core\`) and `UnityEngine*.dll` + `Amplitude.Mercury.Animation.dll` (from `Humankind_Data\Managed\`), then
`dotnet build -c Release` and copy `bin\Release\ENCAccessProof.dll` → `<Humankind>\BepInEx\plugins\`. **Blender** is
only needed for `.blend` import (auto-detected). The GLB path uses the **self-contained** `Tools/glbconv/glbconv.exe`
(no .NET install required); a `dotnet glbconv.dll` fallback exists for local dev.
