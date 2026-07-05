# ENC Access Proof — Universal custom-3D-model injection for Humankind

Inject **any** custom 3D model onto **any** live Humankind unit — correct geometry, correct texture, **its own
animation**, no executable patching, and **zero per-model code**. Bake a model in the Unity editor tool; a data-driven
BepInEx plugin renders it in-game, driven entirely by a JSON registry. What started as a single procedural zeppelin is
now the **Universal Model Factory** — and, as of the ReconDrone, the first **runtime-injected *animated* model** in
Humankind.

## What works (proven in-game)
- **Animated custom models — a first, one-click.** A quadcopter drone injected onto a land unit renders full-size,
  textured, and **spins its own propellers from its own baked animation** — for any number of instances. Tick
  **Animated**, press Bake.
- **Multiple static models live**, no new code each: a **Zeppelin**, an **LCAC Hovercraft**, a fully-textured **USS
  Zumwalt stealth cruiser**, and a **RAH-66 Comanche** helicopter — correct orientation, correct skin, at the waterline.
- **Borrow the donor's animation.** A model rides a donor unit's rig; injection can't *remove* a donor's animated sub-part
  (a rotor), but you can turn that into a feature: **strip your model's own rotor** (see below) and the **donor's spinning
  rotor shows through** — the Comanche flies with a spinning rotor it never had. Or give the model **its own** clip.
- **First-instance rotor fix.** The engine draws the *first* borrowed-rotor pawn of a model, at the moment it's **created**,
  with its rotor ~1 unit low (a spawn race — every later instance is fine). Ticking **Re-spawn after load** makes the plugin
  watch for any such unit appearing — on a save-load, built in a city, or dev-spawned — and near-instantly re-run the game's
  own pawn rebuild (`PresentationUnit.UpdatePawns`) on it, a presentation-only refresh (no unit touched) that clears the low
  rotor. Applied to every instance as it appears (one brief flicker each) so a buggy one is never missed. Opt-in per model;
  the re-spawn delay is tunable in the plugin cfg (`Factory/RespawnDelayFrames`, default 1) for slower machines.
- **Strip parts of your model at bake time.** A "Strip parts" field deletes named objects (+ children) from the source
  mesh before baking — the mirror of Hide-donor, on *your* model. Drop a helicopter's own rotor, a crew figure, a weapon
  pod… Name-Pick reads objects straight from the GLB/glTF. Proven removing the Comanche's rotor blades.
- **Heavy / single-sided / multi-material meshes, handled** — a built-in vertex reducer, a winding fix + double-sided
  fallback for CAD "sketch" meshes, height-based UVs, and an N-material atlas packer. Formats: GLB / glTF / OBJ / FBX /
  `.blend`.
- **Correct, isolated textures.** Custom skins map right-side-up out of the box (the glTF-V-top vs OBJ/Unity-V-bottom
  convention is reconciled during OBJ import), and each model gets a private `FxOutputLayer` so its skin never bleeds
  onto the donor.
- **Add a model = bake it.** The Factory writes the JSON registry; the plugin picks it up on next launch — no per-model code.

Full detail — the shared-buffer ceiling, texture flip, per-model isolation, limitations — in
[**Capabilities.md**](docs/Capabilities.md).

## How it works
**Editor — the Universal Model Factory** (`baker/`, *Tools ▸ Universal Model Factory*): pick a target unit + a model
file, set transform / size / shading, **Bake**. Static models bake an Amplitude `Skeleton` on the proven single-bone
vehicle rig + a packed atlas; ticking **Animated** takes a parallel path (`UniversalBaker.BuildAnimated`) that keeps the
model's **own armature + clip** (Blender slims it, then bakes `Skeleton` + `ClipCollection` + atlas, with the clip
isolated in a per-model `anim/` subfolder). `ModelRegistry` writes `enc_models.json` into the auto-detected `BepInEx/config`.

**Runtime — `UniversalInject`** (`Patches/`): one patch, any number of models. Reads the registry, registers each baked
skeleton, and on `AddOn.Load` repoints the matching pawn by **self-discovery** (reads the host's body-mesh name, renames
ours to match); the skin rides a private layer clone. For **animated** models it also injects the `ClipCollection` and,
via a `PawnManager.AddPawnEntry` hook, drives the pawn's pose onto the clip — matched by **pawn descriptor** and
normalizing the skeleton id, so every instance plays it.

*Founding insight (credit: CalmBreakfast): for a static swap, keep the unit's real skeleton and swap only the mesh —
injecting a mismatched skeleton hangs the GPU skinning. The animated work showed the corollary: a **properly baked**
custom `Skeleton` + `ClipCollection` (built through the SDK's own tooling) can be injected and played **without** the
hang — the danger was malformed skeletons, not custom ones per se.*

## Zero-config adoption
Built to work on a stranger's machine, not just the author's:
- **Auto-detects the game.** No hardcoded paths — the Factory finds Humankind via Steam's library config (with a manual
  override in a Settings panel for odd layouts). Blender is auto-located too.
- **No .NET install needed.** The GLB/glTF converter ships as a self-contained single-file `glbconv.exe` that carries its
  own runtime — adopters don't need a .NET SDK or runtime.
- **One injection path.** A single `UniversalInject` drives every model from the registry, so there's one code path to
  understand and one to trust.
- **Guided, not guessy.** Clip / bone / hide-donor fields are Pick-driven (read from the model + the plugin log); a
  Settings panel shows the detected Blender path with an in-UI override; every feature that needs Blender warns *before* a
  failed bake; and an **embedded interactive 3D preview** shows the baked model right in the window (auto-updates on Bake)
  so you can judge geometry/skin and dial in the vertex reduction live.

## Models & licenses
Model files aren't committed — download each per its license into `Assets/Resources/<name>/` and bake. Authors +
licenses in [**CREDITS.md**](CREDITS.md) (CC-BY requires attribution).

## Config
The plugin reads one JSON file — `<Humankind>\BepInEx\config\enc_models.json` (one entry per model: pawn description,
skeleton + atlas GUIDs, transform, shading flags; animated entries add `clip` + `animated`/`animClip`/`animateBones`). The
Factory writes it and auto-detects the path; the field-by-field breakdown is in the [Factory Manual](docs/Factory-Manual.md).
The plugin's own cfg (`…\community.humankind.encaccessproof.cfg`) — press **F8** in-game for a scan/feedback window.

## Docs
- **Use it:** [**Factory-Manual.md**](docs/Factory-Manual.md) — step-by-step guide (every field, the static + animated
  workflows, a troubleshooting table). Start here if you just want to add a model.
- **Everything it does:** [Capabilities.md](docs/Capabilities.md) — the full capability list + known limitations.
- **How it works:** [Scaling-ManyModels-And-Scoping.md](docs/Scaling-ManyModels-And-Scoping.md) — architecture, per-model
  recipes, texture scoping, and the full debugging log (UV V-flip root cause; animated pipeline in §12).
- **Build it:** [Building.md](docs/Building.md) — plugin build steps + the Blender dependency.
- **Review / roadmap:** [Framework-Review.md](docs/Framework-Review.md) — full code-review findings (prioritized,
  verified), cross-cutting themes, strengths, and the hardening + package-readiness fix order.
- Also: [FBX-to-Humankind-Pipeline.md](docs/FBX-to-Humankind-Pipeline.md), [Custom3DInjection-Spec.md](docs/Custom3DInjection-Spec.md)
  (decompiled pipeline), [Custom3DModels-Findings-Shareable.md](docs/Custom3DModels-Findings-Shareable.md), [UnitPreview-Findings.md](docs/UnitPreview-Findings.md).

## Toward a Unity package
Goal: ship the Factory as a distributable Unity package. **Done:** zero-config path auto-detection, self-contained
converter (no .NET dependency), one consolidated injection path, full multi-material support, and **one-click animated
import** (own rig + clip, Pick-driven fields). **Remaining:** neutral naming (drop "ENC" → `HumankindModelFactory`),
package scaffolding (`package.json` / asmdef / LICENSE), single-DLL plugin packaging, an install guide + quickstart, and
(optional) multi-material GLB. Editor scripts are mirrored in `baker/` (the ENCReload mod repo tracks only its `Databases`).
