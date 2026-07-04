# ENC Access Proof — Universal custom-3D-model injection for Humankind

Inject **any** custom 3D model onto **any** live Humankind unit — correct geometry, correct texture, **its own
animation**, no executable patching, and **zero per-model code**. Bake a model in the Unity editor tool; a data-driven
BepInEx plugin renders it in-game, driven entirely by a JSON registry. What started as a single procedural zeppelin is
now the **Universal Model Factory** — and, as of the ReconDrone, the first **runtime-injected *animated* model** in
Humankind.

## What works (proven in-game)
- **Animated custom models — a first, now one-click.** A **quadcopter drone** injected onto a land-vehicle unit renders
  full-size and textured **and spins its own propellers from its own baked animation** — no engine mod, no GPU-skinning
  hang. Tick **Animated** in the Factory and a single Bake does it all: Blender slims the rigged model (keep armature +
  chosen clip, strip to the spinning bones, auto-clamp the frame range), then it bakes an Amplitude `Skeleton` +
  `ClipCollection` + atlas and writes the registry; at runtime the clip is registered and a `PawnManager.AddPawnEntry` hook
  drives the pawn's pose onto it — normalized by clip duration so it plays at real speed. Works for **any number of
  instances**. Clip/bone/hide-donor fields are **Pick-driven** (read from the model's glTF + the plugin log). Full recipe +
  the traps (incl. isolating the clip source so `SetFromDirectory` bakes exactly one clip) in [docs §12](docs/Scaling-ManyModels-And-Scoping.md).
- **Multiple static models live**, each added with no new code: a **Zeppelin**, an **LCAC Hovercraft**, and a
  fully-textured **USS Zumwalt stealth cruiser** (first textured naval-combat unit) — correct orientation, correct skin,
  sitting at the waterline.
- **Match the donor to your model.** A model rides a donor unit's skeleton + animation, so pick a donor whose *moving
  parts* match yours: a custom **helicopter** body (modelled rotor-less) borrows the donor's spinning rotor for free; a
  drone/ground model wants a donor with **no animated sub-parts and a full idle/move animation set** (a land vehicle is
  ideal). The one thing injection **can't** do: *remove* a donor's *animated* sub-part (a rotor, spinning wheels) — those
  are baked into the pawn at spawn. But it **can** give your model **its own** animation (see the animated-model bullet
  above), which overrides the donor's. Choose the donor accordingly; see the drone case study in the docs.
- **Any number of materials.** A model with N materials (the Zeppelin has 4) is packed into one atlas and each
  sub-mesh's UVs are remapped into its rect — no per-model code, no material cap. Near-black UV dead-zones are filled
  neutral so unused texture regions never render as black patches.
- **Heavy or single-sided/CAD meshes, handled.** A built-in **vertex reducer** (Blender quadric decimation, per-object
  so thin parts survive) shrinks oversized models to fit the engine's shared mesh buffer. A **winding fix** rewinds
  faces outward so single-sided / CAD "sketch" meshes render single-sided instead of culling to invisible (e.g. a
  hovercraft skirt) — the documented recipe, now in the Factory; a **double-sided** toggle is the heavier fallback for
  genuinely non-convex thin shells (a mixed model — convex hull + non-convex fans — can use both). And **height-based
  UVs** map a simple vertical-gradient albedo by height (black skirt low, grey hull high) so an untextured CAD model
  gets a usable skin without UV-unwrapping.
- **Know the ceiling.** Custom meshes share **one GPU buffer — ~100k vertices / ~250k indices (~83k triangles), 32-bit
  indices — across ALL injected models *and* the game's own fx meshes** (from the decompiled `FxComponentMeshContentManager`).
  Overflow is silently dropped (missing/see-through geometry); the reducer exists to stay under it, and double-sided
  counts twice.
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
renames ours to match). Our skin rides on a private layer clone. For **animated** models it also injects the model's
`ClipCollection` into the animation manager and, via a `PawnManager.AddPawnEntry` hook, drives the pawn's pose onto our
clip — matched by **pawn descriptor** and normalizing the skeleton id, so every instance plays it.

*Founding insight (credit: CalmBreakfast): for a static swap, keep the unit's real skeleton and swap only the mesh it
draws — injecting a mismatched skeleton hangs the GPU skinning. The animated-model work later showed the corollary: a
**properly baked** custom `Skeleton` + `ClipCollection` (built through the SDK's own tooling) can be injected and played
**without** the hang — the danger was malformed skeletons, not custom ones per se.*

## Models & licenses
Model files aren't committed — download each per its license into `Assets/Resources/<name>/` and bake. Authors +
licenses in [**CREDITS.md**](CREDITS.md) (CC-BY requires attribution).

## Config
- **Registry:** `<Humankind>\BepInEx\config\enc_models.json` — one entry per model (pawn description, skeleton + atlas
  GUIDs, transform, reducer/shading flags). Animated models add a **`"clip": [a,b,c,d]`** field (the baked
  `ClipCollection` GUID); static models leave it `[0,0,0,0]`. The Factory auto-detects the path (Settings panel to
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
