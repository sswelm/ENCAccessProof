# Humankind Asset Framework (HAF)

**Give any Humankind unit — or district, pawn prop, or projectile — your own 3D model, texture, and sound. No
executable patching, no per-model code.** *(Formerly **ENC Access Proof**.)*

HAF augments [Humankind](https://www.games2gether.com/amplitude-studios/humankind) with custom assets. You **bake**
an ordinary model (`.glb` / `.fbx` / `.obj` / `.blend`) in the **HAF Authoring Tools** — a suite of Unity editor
windows: the **Model Factory** (the model itself), the **Animation Lab** (clips + behaviors), the **District
Factory**, **Prop Lab**, **Projectile Lab**, and **Unit Retexture / Unit Sound** — and a data-driven BepInEx plugin
**injects** the result onto the live game: correct geometry, correct texture, **its own animation**, and movement
audio, all driven by a JSON registry. Adding a model is just baking it — there is no code to write per model.

**It's multi-mod by design.** The runtime merges asset **packs** from many mods at once, so any modder ships their own
config + assets and joins *without editing anyone else's files*. **ENC** is the reference pack (a set of modern-era
units); a stranger's pack loads and merges right alongside it, with conflicts detected and reported. The aim is to make a
custom Humankind unit something **anyone willing to take some effort** can build — and, with luck, spark a little
renaissance in Humankind modding. See [**Multi-Mod.md**](docs/Multi-Mod.md).

Custom units ride the game's own GPU-instanced renderer, so **instances are free** — the cost is the number of distinct
model *types* loaded, not units on screen.

> **Two halves, one contract.** The **HAF Authoring Tools** (bake, in the Unity editor) and a **runtime plugin**
> (inject, in the game) talk only through a small JSON pack registry — so the tooling and the injector stay fully
> decoupled, and the registry is the public API other mods build against. *("Universal Model Factory" names one
> window of the suite — the historical first one; in-editor the whole suite lives under `Tools ▸ HAF`.)*

## The four injection axes

**HAF lets mods add custom visuals in four places: units, districts, props, and projectiles.** Each axis is proven
in-game with a shipped example:

| Axis | What it replaces | Proven with | Deep dive |
|---|---|---|---|
| **Units** | A unit's whole 3D model — static or **animated**, with per-model runtime behaviors | Ships, aircraft, a folding/firing howitzer, a **62-bone humanoid soldier that idles standing and RUNS while moving** | [Factory-Manual.md](docs/Factory-Manual.md) · [Animated-Models.md](docs/Animated-Models.md) |
| **Districts** | A district's on-map building, scoped to **one tile** | A nuclear-plant Breeder Reactor | [District-Visuals.md](docs/District-Visuals.md) |
| **Pawn props** | Weapons & gear on a pawn's **attachment slots** — no whole-model replacement | Slingers carrying an actual sling | [Pawn-Props.md](docs/Pawn-Props.md) |
| **Projectiles** | The **munition mesh** a unit fires | An anti-tank unit launching a kamikaze FPV drone | [Projectiles.md](docs/Projectiles.md) |

**Animation, audio, and retexturing are cross-cutting features** that ride these axes: a unit model can carry its own
baked animation ([Animated-Models.md](docs/Animated-Models.md)), engine or custom WAV movement sound, and a
runtime-hot-loaded skin or tint ([Capabilities.md](docs/Capabilities.md)) — all from the same JSON registry, no code.

## What works (proven in-game, in detail)
- **STATE-DRIVEN characters — idle / run / after-move / combat stance / attack fire (2026-07-19).** The Combine
  soldier **idles standing, RUNS while moving, holds a weapon-raised COMBAT STANCE while its army is locked in a
  battle, and fires its ATTACK animation when it actually shoots** — five clips per model, switched live by the
  runtime (a ~20×/s state poll + per-pawn pose selection on the proven Pose0 slot; priority attack > move >
  after-move > combat > idle). The attack trigger is a hook on the game's own per-pawn ranged-fire sequence, so
  every battle volley animates the exact shooting pawn; an **Attack repeats** knob loops a short recoil-pop clip
  into sustained automatic fire (the soldier's 0.17s `shootAR2s` × 18 ≈ 3 s of fire, runtime-only — no re-bake).
  Configured entirely in the Animation Lab: a **State-driven** toggle with **Idle / Movement / After-movement /
  Attack / Combat-idle** clip pickers; all roles bake against **one shared skeleton** in a single Blender pass
  (every clip rebaked against the primary clip's frame-0 rest — per-role rests would displace the non-primary
  clips; single-frame stance clips are auto-padded so Unity's importer can't drop them). The bake-side war story:
  Blender's bone rename only syncs the *assigned* action's curve paths, so dormant role clips exported as frozen
  statues until the paths were patched explicitly — caught by byte-level pose-data analysis, fixed, and guarded by
  a tool-version cache-buster.
- **Animated custom models — a first, one-click.** A quadcopter drone injected onto a land unit renders full-size,
  textured, and **spins its own propellers from its own baked animation** — for any number of instances. Tick
  **Animated**, press Bake.
- **A HUMANOID character — a full 62-bone rigged soldier (2026-07-18/19).** The Combine soldier replaces a vehicle
  unit: right-sized, standing, head on his shoulders, **turning with its movement**, idling on his own baked clip —
  the first true *character* through the pipeline (props and machines came first). Getting him there built the
  **raw-rig conversion**: auto-rigged models whose clips *assemble the body from a scrambled rest via location keys*
  (which Amplitude, rotation-only, can never play) are now **rest-normalized and visually re-baked** at bake time —
  the assembled pose becomes the rest, the whole clip is re-derived as pure rotations (in-bake verified to ~1e-4),
  the export folds units/rotation/scale into the data, collapses no-op roots, and renames bones topologically
  (Amplitude sorts alphabetically and requires parents before children). A **litmus rig** (12-deep chain of cubes,
  `Tools/make_litmus.py`) proved the runtime renders clean rigs perfectly. Also discovered en route: the game turns
  pawns through a procedural **bone-rotation layer** — the plugin clears it only for artillery models and ignores
  vehicle donors' phantom wheel-spin slots. *(With the clean rig, the unit's fired-drone
  projectile also displays again during attacks — the fully working unit: stand, turn, idle, launch.)*
- **The Animation Lab — animation authoring in its own dialog (2026-07-18).** `Tools ▸ HAF ▸ Animation Lab` docks as
  a tab beside the Factory: the Factory owns the *model* (file, transform, size, shading), the Lab owns the
  *animation* (clip + bone-filter pickers, fire-on-attack, deploy-on-stop + recoil, and **Save (no bake)** for
  runtime flags). Settings are mutually exclusive between the windows and **enforced at bake time** — each window
  rebases on the freshest registry entry and writes only its own fields, so stale copies can't clobber each other.
  Geometry re-processing is **automatic** (the Blender step re-runs exactly when one of its inputs changed); the old
  "Reuse extracted" checkbox is now purely **"Keep extracted texture (hand-edits)"**.
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
- **Unit movement audio — engine sounds & custom WAVs.** Injected/retextured units are silent on move (the game's per-ship
  engine sound rides an audio-service path our re-loaded units never fire). The plugin restores it — playing the game's own
  sound **by name** (works from the *first* unit, no capture; F8 **Dump Sound Catalog** lists all ~845 event names) — or
  **any custom WAV you drop in**, as a **Start (spool-up) → Travel (loop) → Stop (spool-down)** sequence with per-clip
  volume, driven by a dedicated **Unit Sound** editor window (with in-editor ▶ preview). Runtime-only, no bake.
- **Retexture / recolour without a bake.** A separate **Unit Retexture** window reskins an existing unit at runtime —
  a hot-loaded PNG or a live Desaturate + RGB adjust on its own atlas — isolated per unit, free on the vertex budget.
- **Multi-mod — merge packs from many authors.** The runtime is a **Humankind Asset Framework** host, not just ENC's
  loader: it merges ENC's base registry with any number of third-party **packs** dropped in `BepInEx/config/haf_packs/`,
  so a modder augments their own units with a custom model / texture / sound by shipping just a config file + assets — **no
  ENC edits, no code**. Pack resolution is **enforced** (2026-07-19): duplicate `modId`s rejected, `dependsOn` validated,
  load order topologically sorted over `dependsOn`/`loadAfter` (cycles broken loudly), **declared `overrides` replace** the
  targeted entry, and an undeclared same-pawn clash stays first-loaded-wins, logged loud — no silent overrides. Every
  load writes a `haf_load_report.txt` with the resolution decisions. See [**Multi-Mod.md**](docs/Multi-Mod.md).

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

## Technology stack

| Layer | Technology |
|---|---|
| Runtime plugin (this repo) | **BepInEx 5.4** plugin in C#, targeting **.NET Framework 4.7.1** (the game's Mono runtime); builds with just the .NET SDK (`dotnet build`, no Unity needed) |
| Game patching | **Harmony** (`0Harmony`) runtime patches against the game's **`Amplitude.Mercury`** assemblies — no executable modification |
| Registry parsing | **Newtonsoft.Json** (shipped with the game, via mod.io) — `UnityEngine.JsonUtility` silently returns empty objects under the game's Mono runtime |
| Editor tooling ([ENCReload](https://github.com/sswelm/ENCReload)) | **Unity 2021.3.1f1** (Humankind's own engine version) + the **official Amplitude modding SDK**, which bakes the native `Skeleton` / `ClipCollection` / mesh / atlas assets; editor scripts mirrored here in `baker/` |
| `glbconv` converter | Standalone C# console app on **.NET 8** (self-contained single-file exe — adopters need no .NET install), built on **SharpGLTF** |
| Model-prep scripts | **Python** run headless inside **Blender** (`blender -b --python …`, `bpy` API) — rigging, decimation, clip extraction |
| Editor ↔ runtime contract | A plain **JSON** registry (`enc_models.json`) — the only thing the two halves share |

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

## License
All code, scripts, and docs in this repo are **MIT** ([LICENSE](LICENSE)) — fork the plugin, vendor the injection
path, build on it. Two things the MIT grant does *not* cover, because they aren't ours: the game internals the docs
describe (decompiled `Amplitude.*` code remains Amplitude Studios' property; the required game DLLs are gitignored
and must be copied from your own install), and any 3D model you bake (each stays under its own license — see
[CREDITS.md](CREDITS.md)). The ENC mod's own game-data content lives in the
[ENCReload](https://github.com/sswelm/ENCReload) repo and is all-rights-reserved there.

## Config
The plugin reads `<Humankind>\BepInEx\config\enc_models.json` — ENC's base **pack** (one entry per model: pawn description,
skeleton + atlas GUIDs, transform, shading flags; animated entries add `clip` + `animated`/`animClip`/`animateBones`), now
wrapped with pack metadata (`schemaVersion`/`modId`). It then merges any additional packs in `BepInEx/config/haf_packs/*.json`
and writes a `haf_load_report.txt` of what loaded. The Factory writes ENC's registry and auto-detects the path; the
field-by-field breakdown is in the [Factory Manual](docs/Factory-Manual.md) and the pack format in [Multi-Mod.md](docs/Multi-Mod.md).
The plugin's own cfg (`…\community.humankind.haf.cfg`) — press **F8** in-game for a scan/feedback window.

## Docs
- **Can HAF import my model?** [**Animated-Models.md**](docs/Animated-Models.md) — the plain-language answer, in
  three levels: clean rigs, rigid-part machines (artillery), and full character rigs incl. messy auto-rigs (yes,
  humanoids work — the public "anything moving is not possible" consensus is outdated). Start here for animation.
- **Use it:** [**Factory-Manual.md**](docs/Factory-Manual.md) — step-by-step guide (every field, the static + animated
  workflows, a troubleshooting table). Start here if you just want to add a model. §16 is the dedicated
  **animated-model conversion guide**: what Amplitude's animation system can play, the automatic raw-rig conversion
  (rest normalization, rotation-only rebake, bone ordering, unit-clean export), and the symptom→cause map.
- **Ship your own pack:** [**Multi-Mod.md**](docs/Multi-Mod.md) — the HAF pack format, the `haf_packs/` drop folder,
  how packs merge + conflict rules, and the load report. Read this to add assets without touching ENC. Template:
  [haf-pack.example.json](docs/haf-pack.example.json).
- **Everything it does:** [Capabilities.md](docs/Capabilities.md) — the full capability list + known limitations.
- **How animation actually runs:** [Animated-Runtime.md](docs/Animated-Runtime.md) — the decompiled runtime
  architecture: clip registration, the per-frame pose hook, the GPU pose math and its encoding formats, the
  engine contracts (rotation-only, scale-1, name-ordered bones), multi-instance handling, and the verification
  toolkit. The runtime companion to Factory-Manual §16.
- **Build it:** [Building.md](docs/Building.md) — plugin build steps + the Blender dependency.
- **Review / roadmap:** [Framework-Review.md](docs/Framework-Review.md) — verified code-review findings
  (prioritized) and the hardening order toward package-readiness.
- **Fire-on-attack:** [Firing-On-Attack.md](docs/Firing-On-Attack.md) — how a model plays its clip on the unit's combat
  action (Humankind's `SimulationEvent` bus), the one-shot pose trigger, and the per-model animated-bake scale toggle. **Built.**
- **District visuals:** [District-Visuals.md](docs/District-Visuals.md) — the second injection axis: a custom static
  model on a **single district tile** (render + fit-the-GPU-buffer + scope-to-one-tile). **Working, with its own
  pipeline**: the District Factory window (Tools ▸ HAF ▸ District Factory) bakes model → bone-free FxMesh → an
  `enc_districts.json` registry entry the plugin reads (any number of districts at once).
- **Pawn props:** [Pawn-Props.md](docs/Pawn-Props.md) — the third injection axis: custom **weapons & gear on pawn
  attachment slots** (Slingers finally carry a sling). The Prop Lab window (Tools ▸ HAF ▸ Prop Lab) bakes model →
  fragment + MeshCollection; the plugin registers the collection at `AnimationLoad`. **Working (experimental).**
- **Projectiles:** [Projectiles.md](docs/Projectiles.md) — the fourth injection axis: a custom model as a unit's
  **fired munition** (a Humvee launches a kamikaze drone). The Projectile Lab window (Tools ▸ HAF ▸ Projectile Lab)
  clones a mesh-donor projectile's trail drawer with our FxMesh swapped in (+ an optional explosive *impact donor*);
  wired via the unit's `Projectile` field (data), no plugin needed. **Working** — one clean drone per launch from a
  single-pawn (vehicle) base; the firing-count "wave" mechanic (`ceil(defendersToKill / pawns)`) is fully decompiled.
  Skin colour is model/UV-dependent (swap the model to reroll it); launch audio is the one open item.
- **Learn from others:** [Ecosystem-Survey.md](docs/Ecosystem-Survey.md) — every Humankind BepInEx plugin on GitHub, what
  problem each solves, and the techniques worth borrowing (lifecycle anchors, order-bus sequencing, save extensions, …).

## Toward a Unity package
Goal: ship the Factory as a distributable Unity package. **Done:** zero-config path auto-detection, self-contained
converter (no .NET dependency), one consolidated injection path, full multi-material GLB support, **one-click animated
import** (own rig + clip, Pick-driven fields), bake-time skin controls (albedo brightness/saturation, keep-black),
configurable atlas size + bundle slimming (source models kept out of the shipped mod, DXT1 atlases), a static-model
**donor-animation freeze**, **fire-on-attack** (a model's clip triggered once by the unit's combat action), a
**multi-mod pack loader** (the runtime merges packs from many authors out of `haf_packs/`, so ENC is just one mod of many),
and an **MIT license** on all code. **Remaining:** neutral naming (drop "ENC" → `HumankindModelFactory`), package scaffolding
(`package.json` / asmdef), single-DLL plugin packaging, and an install guide + quickstart. Editor scripts are
mirrored in `baker/` (the ENCReload mod repo tracks only its `Databases`).
