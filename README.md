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
- **Fire-on-attack — a model that animates when the unit *fires*.** Tick **Fire on attack** and the baked clip plays
  **once, on the combat action**, not on a loop: the model rests, then plays a single pass the moment the unit attacks and
  returns to rest. Proven with a **howitzer whose barrel elevates only when it bombards** — the plugin hooks Humankind's
  own combat event bus, matches the firing unit to the injected model, and triggers one playthrough.
- **Multiple static models live**, no new code each: a **Zeppelin**, an **LCAC Hovercraft**, a fully-textured **USS
  Zumwalt stealth cruiser**, and a **RAH-66 Comanche** helicopter — correct orientation, correct skin, at the waterline.
- **Borrow the donor's animation — including *multiple* moving parts.** A model rides a donor unit's rig; injection can't
  *remove* a donor's animated sub-part (a rotor), but you can turn that into a feature: **strip your model's own rotor(s)**
  (see below) and the **donor's spinning rotor shows through**. The donor helicopter has *two* rotor bones (`Helix` main +
  `Helix_back` tail), so stripping both the Comanche's main *and* tail rotor gives it a spinning main rotor **and** a spinning
  shrouded fantail — two borrowed animations on one static model. Or give the model **its own** clip.
- **First-instance rotor fix.** The engine draws the *first* borrowed-rotor pawn of a model, at the moment it's **created**,
  with its rotor ~1 unit low (a spawn race — every later instance is fine). Ticking **Re-spawn after load** makes the plugin
  watch for any such unit appearing — on a save-load, built in a city, or dev-spawned — and near-instantly re-run the game's
  own pawn rebuild (`PresentationUnit.UpdatePawns`) on it, a presentation-only refresh (no unit touched) that clears the low
  rotor. Applied to every instance as it appears (one brief flicker each) so a buggy one is never missed. Opt-in per model;
  the re-spawn delay is tunable in the plugin cfg (`Factory/RespawnDelayFrames`, default 1) for slower machines.
- **Freeze the donor's motion.** A *static* model riding an animated ground/hover donor inherits the donor's idle/move bob;
  **Freeze donor animation** pins the donor's pose so a rigid model (an airship) holds still while it still glides
  tile-to-tile — applied across *every* instance the same way animated models are (descriptor-matched + skeleton-forced).
- **Strip parts of your model at bake time.** A "Strip parts" field deletes named objects (+ children) from the source
  mesh before baking — the mirror of Hide-donor, on *your* model. Drop a helicopter's own rotor, a crew figure, a weapon
  pod… Name-Pick reads objects straight from the GLB/glTF. Proven removing the Comanche's rotor blades.
- **Heavy / single-sided / multi-material meshes, handled** — a built-in vertex reducer, a winding fix + double-sided
  fallback for CAD "sketch" meshes, height-based UVs, and an N-material atlas packer. Formats: GLB / glTF / OBJ / FBX /
  `.blend`.
- **Correct, isolated textures.** Custom skins map right-side-up out of the box (the glTF-V-top vs OBJ/Unity-V-bottom
  convention is reconciled during OBJ import, and off-tile UVs — a skin mapped into the V 1→2 tile relying on wrap — are
  shifted back into range so they don't collapse to a flat smear), and each model gets a private `FxOutputLayer` so its
  skin never bleeds onto the donor.
- **Tune the skin, shrink the bundle.** Bake-time **Albedo brightness / saturation** lift a dark or washed-out skin (the
  injection ships *flat* albedo — donor PBR neutralized — so a shiny/dark source reads muddy without this); a **Keep black**
  toggle preserves an intentionally black material (a glass canopy); and **Atlas size** (256–2048, default 512) + DXT1
  compression keep each shipped skin ~0.1–2 MB. Bake *inputs* live in `Assets/FactorySource/` — out of the shipped mod, so
  the licensed source models are never redistributed.
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
Model files aren't committed — download each per its license, point the Factory's **Model file** at it, and bake (the
converter extracts it into `Assets/FactorySource/<name>/`, which stays out of the shipped mod). Authors + licenses in
[**CREDITS.md**](CREDITS.md) (CC-BY requires attribution).

## Config
The plugin reads one JSON file — `<Humankind>\BepInEx\config\enc_models.json` (one entry per model: pawn description,
skeleton + atlas GUIDs, transform, shading flags; animated entries add `clip` + `animated`/`animClip`/`animateBones`). The
Factory writes it and auto-detects the path; the field-by-field breakdown is in the [Factory Manual](docs/Factory-Manual.md).
The plugin's own cfg (`…\community.humankind.encaccessproof.cfg`) — press **F8** in-game for a scan/feedback window.

## Docs
- **Use it:** [**Factory-Manual.md**](docs/Factory-Manual.md) — step-by-step guide (every field, the static + animated
  workflows, a troubleshooting table). Start here if you just want to add a model.
- **Everything it does:** [Capabilities.md](docs/Capabilities.md) — the full capability list + known limitations.
- **Build it:** [Building.md](docs/Building.md) — plugin build steps + the Blender dependency.
- **Review / roadmap:** [Framework-Review.md](docs/Framework-Review.md) — verified code-review findings
  (prioritized) and the hardening order toward package-readiness.
- **Fire-on-attack:** [Firing-On-Attack.md](docs/Firing-On-Attack.md) — how a model plays its clip on the unit's combat
  action (Humankind's `SimulationEvent` bus), the one-shot pose trigger, and the animated-bake 100× scale fix. **Built.**

## Toward a Unity package
Goal: ship the Factory as a distributable Unity package. **Done:** zero-config path auto-detection, self-contained
converter (no .NET dependency), one consolidated injection path, full multi-material GLB support, **one-click animated
import** (own rig + clip, Pick-driven fields), bake-time skin controls (albedo brightness/saturation, keep-black),
configurable atlas size + bundle slimming (source models kept out of the shipped mod, DXT1 atlases), a static-model
**donor-animation freeze**, and **fire-on-attack** (a model's clip triggered once by the unit's combat action). **Remaining:** neutral naming (drop "ENC" → `HumankindModelFactory`), package scaffolding
(`package.json` / asmdef / LICENSE), single-DLL plugin packaging, and an install guide + quickstart. Editor scripts are
mirrored in `baker/` (the ENCReload mod repo tracks only its `Databases`).
