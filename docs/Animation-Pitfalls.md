# Animation Pitfalls — a field guide to the woes

Every trap in this document was hit for real, cost hours, and is now either fixed in the tooling or has a
one-field recipe answer. Read this BEFORE debugging an animated model that "looks wrong": the odds are your
problem is on this page, and the odds are the first three explanations you'll think of are not the cause.
(Case study behind almost every entry: migrating the M114 howitzer from the legacy fire/deploy behaviors to the
state-driven machine, 2026-07-19 — a day in which the model file was accused of corruption, three preview
renderers were rewritten, and the actual causes turned out to be four engine constraints nobody had written down.)

## The four laws

These are engine-level facts. They are not bugs, they cannot be patched away, and every recipe must respect them.

### Law 1 — The clip bake is ROTATION-ONLY
The engine's baked clips keep per-bone **rotation** and **discard translation** (`GetPoseTRS` forces
translation 0, scale 1). Any part whose source motion *slides* plays pivoting about the wrong point in-game.
- **Symptom:** a part sweeps through/into the model in-game (the M114's trail legs crossed inward) while every
  preview — Blender, the ▶ picker — looks perfect. Previews play full curves; the game doesn't.
- **Answer:** re-express the motion as pure rotation. For deploy-converted models that's one recipe field
  (**Leg spread scale** re-keys `*leg*` parts; the hidden far-pivot **RecoilArm** does it for barrel slides).
  For other rigs: author the motion rotationally, or accept the drift.

### Law 2 — A stance baked as the PRIMARY clip renders as REST
The primary clip defines the skeleton's **reference pose**; clip data encodes *against* it. Bake a 1–2-frame
stance as the primary and it encodes ~identity — in-game the unit shows the **rest pose** (usually the travel
pose) no matter what the stance was.
- **Symptom:** idle shows the travel/fold pose ("it forgot to deploy") although the stance clip previews
  correctly everywhere else. The byte tell: the baked `*_ClipsPoseData.bytes` rows are near-constant identity.
- **Answer:** the primary (Idle/reference clip) must be the **FULL source motion**; the stance goes in
  **Idle stance (override)** — a role clip, encoding real deltas against the full clip's reference (e.g.
  `deploy[179..180]`). This is why the Lab has two idle fields.

### Law 3 — Pacing is BAKED, never a runtime knob
The runtime plays every clip at its authored length (24 fps). A 7.5 s authored fold outlasts a one-tile map
move — the unit spends the whole trip in the fold's first frames ("it forgets to fold").
- **Answer:** the slice **speed step**: `deploy[179..0/6]` = every 6th frame = 6× faster (≈1.25 s). The step
  always lands exactly on the end frame. `/180` on a 180-frame motion degenerates to a 2-frame near-snap; an
  **empty** Pre-movement clip is a true instant snap (which is all the legacy howitzer ever did — its
  "instant fold" was the *absence* of a fold animation).

### Law 5 — Bone POSITIONS are pinned at the bind pose; only ROTATIONS animate
Stricter than Law 1, discovered by experiment (Arc-R scaling test): the engine keeps every bone at its
bind-pose position and plays only orientations through the hierarchy. Consequences:
- a whole-body lurch (the M114's carriage recoiling backward) cannot be expressed in a clip at all;
- the far-pivot "RecoilArm" arc — designed to fake a slide as rotation — renders as a **small in-place pitch**
  (the tube tilts by θ instead of swinging R·θ along the arc). The beloved legacy "kickback" was always exactly
  this modest pitch, read at map zoom; up close it looks like a nose-dip. Judge fire effects at PLAYING distance.
- the practical kick recipe: slam-only recoil range (`445..451` on the M114) + the palindrome **Return slow**
  (the same frames played backward slowed, gliding home). Whole-body motion would need a runtime ObjectSpace
  nudge (possible, unbuilt).

### Law 4 — What a preview shows is NOT what the game plays
Three different things can lie to you independently:
- a **custom editor renderer** can corrupt the view itself (two hand-rolled BakeMesh draw paths each mangled
  the M114 — un-mirrored legs, then giant parts — while the data was provably fine; the ▶ picker now renders
  the real instance through Unity's own pipeline for exactly this reason);
- the **▶ picker is a raw player** — it deliberately shows the source's FULL curves (translations included),
  so on translation-driven parts it will look *better* than the game (Law 1);
- **in-game is the only truth** for the final bake — and even there, remember the unit may be standing on a
  slope, mid-state, or showing a donor-layer artifact.

## The method: sandbox + gate

What finally broke the debugging loop was not a cleverer theory — it was a **measurement protocol**:

1. **Sandbox on a donor unit.** Never iterate on a shipping entry. Clone the recipe onto a throwaway unit
   (the SiegeHowitzersCar donor) so every failed bake costs nothing and the working entry stays as reference.
2. **Gate against the proven bake.** When a "should-be-identical" bake misbehaves, don't stare at the screen —
   **diff the artifacts**:
   - `Assets/Resources/<name>_ClipsPoseData.bytes` — byte-compare against the proven model's file. Identical
     prefix + divergence at frame N is a *location*, not a mystery (a scene-state leak was found at frame ~103
     this way).
   - slim FBXs — sample both in headless Blender and diff evaluated bone poses per frame (the gate scripts:
     load both, `frame_set` sweep, compare world bone heads + local quats; PASS = 0.0000). The whole Blender
     stage can be gated **without ever opening Unity or the game**.
3. **Change ONE delta per bake.** The sandbox failures compounded exactly when several knobs moved at once
   (settings drift + stale caches + new file). The gate tells you which delta mattered.
4. **Bytes over eyes.** Every "the file is corrupt!" accusation this day was wrong; every byte/pose diff was
   right. Measure chirality, don't eyeball crossed legs; measure pose rows, don't squint at stances.

## Symptom index

| Symptom (in-game unless said otherwise) | Cause | Fix |
| --- | --- | --- |
| Part sweeps through the model; previews fine | Law 1 (translation stripped) | re-key as rotation (legScale / RecoilArm) |
| Idle shows travel pose; stance previews fine | Law 2 (stance-as-primary) | full clip as Idle/reference; stance in Idle override |
| "Forgets to fold" — travels deployed | Law 3 (fold outlasts the move) | speed step `/6`…, or empty Pre-move = instant snap |
| Fold/unfold plays but glacially | Law 3 | speed step |
| Unit invisible | wrong animation id (invisible ⇒ id, frozen ⇒ constant data — the diagnostic dichotomy) | check `[Uni] clip` log lines resolve |
| Unit frozen mid-pose | constant clip data (hollow bake) | byte-check PoseData; re-slim (cache busters below) |
| Whole unit tiny/huge/floating | FBX unit scale | **Fix 100× oversize** per model |
| A **rotating** part (wheel) flings off / orbits in the air while the body sits still; idle fine, only *movement* flings | the m→cm ×100 export **sandwich** mangles rotating bones' TRS (and **Fix 100× ON re-creates it**) | **Convert raw rig ON** (cancels the ×100) **+ Fix 100× OFF** — see below |
| Baked skin scrambled on one part (wheel) | multi-material albedos missing — the animated path now generates them (glbconv) but a failed extraction falls back to a single atlas, loudly | check `[glbconv]` Console errors, re-bake |
| Preview (custom window) shows mirrored/giant parts | Law 4 (renderer bug) | render real instances (`AddSingleGO`), never hand-rolled BakeMesh draws |
| Settings revert / edits ignored after compile | stale window form (survives domain reload) | the Lab re-syncs on reload + **↻ Reload** button; registry file is the truth |
| A knob change bakes identical output | stale slim cache — **edits made through the Lab re-slim automatically**; edits made directly to the registry file behind an open window do NOT (the cache compares form vs file) | ↻ Reload first, or delete `anim*/…_anim.fbx` |
| Crossed/wrong limbs in a stance ROLE clip (historical) | role slicing leaked pose values into channels the primary doesn't key | fixed: slicing saves/restores all pose bones (`rig_anim.py`) |
| Same bake differs run to run (historical) | export-time pose was whatever frame the tool last touched — it becomes the engine's reference | fixed: every export pins the scene to the clip's first frame |
| Whole gun pitches/dives when firing (close zoom) | Law 5: the arc kick IS a pitch; it reads as a dive only nose-to-the-glass | judge at map zoom; tune via recoil range/Return slow |
| Attack plays stale/old animation after a recipe edit (historical) | Blender exits 0 even when the conversion script CRASHES — the baker reused the old converted GLB and recorded the bad args as success | fixed: success = the script's own final marker; reversed recoil ranges rejected with a clear error |
| Aim-layer suspicion during fire | the donor streams runaway angles (5000°+) — but at the INVALID bone index sentinel (0xFFFFFFFF): applied to nothing | exonerated; a throttled `[Aim]` log in ClearAimLayer shows what streams |
| Model collapses flat onto the root, limbs flung (mech) | rig has NO skin weights — parts rigidly bone-parented; the join drops the binding, all verts fall to bone #0 (Unity warns) | fixed: conversion path converts bone-parenting to full-weight vertex groups (`rig_anim.py`) |
| Skeleton ~100× off the mesh, rigid parts become a "wing" | wrapper empty with non-identity scale (mech: 0.010) survives export; Amplitude reads bind poses without it | fixed: conversion path flattens wrapper empties before `transform_apply` |
| Huge stretch spikes in-game, Blender preview fine (detailed rig) | over Amplitude's 256-bone GPU skinning cap (mech: 332 bones) — verts on bone index >255 get garbage | fixed: zero-weight leaf bones removed to ≤240 (weighted bones untouched) |

## The rotating-bone fling — the metre→centimetre sandwich

Case study: the Ehrhardt armored car (first custom **spinning-wheels** vehicle, 2026-07-24). Wheels attached and
still at idle, but the moment the movement clip **rotated** them they flew off and orbited through the air while
the hull stayed put. The same class of bug as the Combine soldier whose "head rode off his shoulders."

**Why:** Blender's FBX exporter writes metres→centimetres by scaling the ROOT objects **×100**. Unity compensates
with **0.01 in every skinned-mesh bindpose + a ×100 root** — a *sandwich* Amplitude's uniform-scale TRS
composition mangles on any bone that **rotates** (a static bone composes fine; a rotating one orbits about a
mis-scaled pivot). That is why **idle looked perfect** (0° rotation) and only movement flung. The ▶ picker and
Unity preview also look perfect — they use a clean import, not the sandwiched bake (Law 4).

**The cruel part:** the size fix and the fling fix pull in opposite directions on the *legacy* path.
- **Fix 100× oversize (`animUnitFix`) ON** → correct render size, **but keeps the sandwich** → wheels fling.
- **Fix 100× OFF** → no sandwich, **but the model bakes ~100× too big** ("too large to see").

**The answer is neither toggle — it's `convertRig`.** The conversion path exports with `global_scale=0.01`, which
**cancels the exporter's ×100** at the source (`rig_anim.py` ~L691-699): net node scale 1, UnitScaleFactor 1, bind
clusters 1 — the clean profile. So a rotating-bone rig bakes correct **and** grounded with:

> **Convert raw rig ON  +  Fix 100× oversize OFF.**

This **overturns** the old "convertRig OFF for clean purpose-made rigs" guidance: a purpose-made rig with rotating
bones (wheels, turret, propeller-on-bone) still needs convertRig ON, *unless* its source file happens to carry a
0.01 object scale that already cancels the ×100 (the ReconDrone's luck — which is why the drone bakes fine OFF).
When in doubt for a rig with any spinning part: **convertRig ON**.

**Grounding — the animated path has no automatic keel→z=0 (only the static path does), so a vehicle whose tyres
stick out below the hull sinks.** Two ways to sit it on the terrain:
- **Auto-ground (sit on terrain)** toggle — *the hands-free way.* The bake drops the model's lowest point (the
  tyre contact) to the skeleton origin (lift by `−minZ`). It's **self-correcting** (a raw file lifts fully, an
  already-grounded one lifts ~0 → can't double-apply) and **size-proof**: the shift is in model space, so the bake's
  `globalScale = size/longest` scales it automatically — change Size and it stays grounded. (An earlier attempt used
  a "wheels-on minus wheels-off" *protrusion* measure — a fixed lift that FLOATED an already-grounded file; keel→
  origin replaced it.) Verified on the Ehrhardt: model-space lift 0.671 × size-scale (4/6) ≈ 0.45 in-game, matching
  the hand-dialed 0.42. OFF for a flyer/hover model (it would be pinned to the ground).
- **Position offset Z (waterline)** — the manual/runtime knob, applied at **spawn by the plugin**
  (`ApplyPositionOffset`: `ObjectSpace.Translation.y += z`), the same one you use for drone/aircraft height. It's in
  **in-game units**, so it does NOT scale with Size (a value dialed at Size 4 is wrong at Size 5). Use it for hover
  height, or as a small fine-tune on top of Auto-ground — Save + relaunch, no re-bake.

## Turretize — aim a turret (or artillery barrel) at the target

The game already computes the aim and streams it as a HEADING angle into a `PawnEntry.BoneRotation0-3` slot
(`{SkeletonBoneIndex, AxisIndex, Angle}`) — but on an injected model that slot's `SkeletonBoneIndex` is the invalid
`0xFFFFFFFF` sentinel, so it drives nothing. **Turretize retargets that slot to your turret bone**, so the engine's
own aim math rotates it — no per-frame trig.

- **Setup (runtime, no re-bake):** Animation Lab → **Turret bone** = a bone-name substring (e.g. `Turret`; the
  plugin substring-matches it against the renamed `b###_<orig>` bones) → **Turret aim axis** → **Save (no bake)** +
  relaunch. Verified on the Ehrhardt armored car (first custom unit with an aiming turret).
- **THE gotcha — the axis is per-model, and the game's default reads as PITCH, not YAW.** The streamed channel is
  "axis 1 = up in the GAME's frame", but on your turret bone (after the convert rebake folds the rig) that lands on
  whatever local axis it lands on — so it usually tilts (pitches/rolls) instead of yawing. There are only THREE
  local axes: try **0 / 1 / 2** in the *Turret aim axis* dropdown until it turns the way you want. (Ehrhardt: axis
  **2** = yaw. axis **1** pitched up, axis **0** pitched down.)
- **Yaw for a turret, PITCH for a barrel — same feature.** The axis that's "wrong" (tilts) for a turret is exactly
  what a mechanized howitzer / artillery barrel needs to ELEVATE at range. One knob, two unit types.

## What the legacy howitzer really was (calibrate your expectations)

The "old functionality" everyone remembers was **one clip + two runtime tricks**: hold the full deploy clip at
normalized time 0.999 when idle (0.999, not 1.0 — `Repeat(1.0)` wraps to frame 0, the folded pose: the original
edge-overflow bug), and snap to frame 0 while moving. No stance clips, no fold animation, no state machine.
Recreating it state-driven therefore wasn't porting — it was building five clips through machinery the legacy
path never exercised, which is why "it worked before" was true and useless at the same time. The state-driven
equivalent that ends up matching it, entirely in data:

| Role | Clip | Why |
| --- | --- | --- |
| Idle / reference | `deploy` (full) | Law 2 — defines the reference pose |
| Idle stance (override) | `deploy[179..180]` | the deployed hold, as a role |
| Movement | `deploy[0..0]` | travel stance |
| Pre-movement | `deploy[179..0/12]` (or empty) | fast fold (empty = legacy instant snap) |
| After-movement | `deploy[0..179/3]` | the unfold |
| Attack | `deploy[180..250]` | the source's own recoil kick |
