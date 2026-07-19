# What kinds of animated models can HAF import?

The short answer: **more than the community thinks is possible.** The public consensus is still "anything moving is
not possible" in Humankind modding — HAF has shipped a spinning-prop drone, a folding/firing howitzer, and a full
humanoid character (a raw Sketchfab auto-rig) that **idles standing and runs while moving** as working in-game units.

This page is the plain-language front door. If you just want to know whether *your* model can work, read this; the
deep technical treatment lives in [Factory-Manual.md §16](Factory-Manual.md) (how the conversion works) and
[Animated-Runtime.md](Animated-Runtime.md) (how the engine plays it back).

## The one engine rule that shapes everything

Humankind's animation engine plays **bone rotations only**. Clips that move bones by *position* (location keys)
don't survive; scale animation doesn't either. Everything HAF does for animated models is about getting your model's
motion expressed as pure rotations — automatically where possible.

## The three levels

### Level 1 — Clean, purpose-made rigs *(easiest: works out of the box)*

A model with a proper armature, a sane rest pose, and rotation-driven animation — typical of models authored by an
actual rigger, or anything you rig yourself.

- **Examples shipped:** the ReconDrone (spinning propeller loop).
- **What you do:** pick the model and the clip in the Factory / Animation Lab, Bake. Done.
- **Settings:** "Convert raw rig" **OFF**. "Fix 100× oversize" per model (tick it if the bake comes out ~100× too
  big — the Lab auto-suggests it for GLB files).

### Level 2 — Rigid-part animations *(vehicles, artillery, machines)*

Models animated by **moving separate parts** (nodes) rather than a skinned skeleton — a howitzer's folding trail
legs, landing gear, a crane, turrets. Very common for Sketchfab vehicles.

- **Examples shipped:** the TowedGunHowitzers — folds for travel, deploys when it stops, recoils when it bombards.
- **What you do:** run `Tools/deploy_convert.py` once (headless Blender) to turn the moving parts into a
  bone-per-part skinned armature, then bake like Level 1. The runtime behaviors (deploy-on-stop, fire-on-attack,
  recoil) are checkboxes in the Animation Lab — see [Firing-On-Attack.md](Firing-On-Attack.md).
- **One authoring rule:** motion that genuinely *slides* (a recoiling barrel) must be faked as rotation — the
  converter's hidden far-pivot "RecoilArm" does this for you.

### Level 3 — Full character rigs, including messy auto-rigs *(the breakthrough)*

Humanoids and creatures with real skeletons — including **auto-rigged downloads whose rest pose is scrambled** and
whose clips assemble the body every frame with location keys (typical of Sketchfab auto-rigs; these are unplayable
in the engine as-is, which is where the "not possible" consensus came from).

- **Examples shipped:** the Combine soldier (62-bone ValveBiped) — stands, turns with movement, **idles standing,
  runs while moving**, attacks.
- **What you do:** tick **"Convert raw rig"** in the Animation Lab and bake. The conversion is automatic: it makes
  the clip's first visual pose the new rest pose, re-derives the entire animation as pure rotations, renames bones
  so the engine's sorting can't scramble them, and exports unit-clean (usually with "Fix 100×" OFF).
- **If the result is mis-oriented:** add a Rotation and probe one axis at a time — and judge **in-game**, not in the
  preview (the preview's orientation is meaningless for animated models).

## State-driven characters (idle / run / after-move / combat / attack)

A character can play **different clips per state**: tick **"State-driven"** in the Animation Lab and pick
an **Idle clip** (plays standing), a **Movement clip** (loops while the unit travels — a run cycle), and optionally
an **After-movement clip** (played once on stopping, then back to Idle), an **Attack clip** (played when the unit
fires a ranged attack — the runtime hooks the game's own per-pawn fire sequence, so the exact shooting pawn
animates in battles and bombards alike), and a **Combat-idle clip** (a weapon-raised stance that replaces Idle
while the army is locked in a battle, from deployment to resolution — a single-frame pose clip works and is
auto-padded at bake time). Priority: attack > movement > after-move > combat-idle > idle.

Source clips are often authored as a single trigger-pull pop (the soldier's `shootAR2s` is 0.17 s) — the sim fires
once per attack, so at face value that's a blip. The **Attack repeats** slider replays the clip N times per trigger
(window = N × clip duration; 18 ≈ 3 s of sustained fire) and is **runtime-only**: change it, *Save (no bake)*,
rebuild the mod — no re-bake.

All clips come from the same model file and bake against **one shared skeleton** in a single pass — pick, bake,
done. This is what makes a humanoid read as a *unit* instead of a statue gliding across the map.

### Clip slicing — one long clip, many states

Any clip field accepts a **frame range**: `deploy[0..180]`. The slice is cut from the source clip at bake time —
no Blender work needed. `start > end` plays the segment **reversed** (a fold from an unfold); a single frame
(`deploy[180..180]`) becomes a **held stance**. Many downloadable models ship one long clip containing several
motions in sequence — slicing turns that single timeline into a full state set.

**Worked recipe — artillery on one clip** (a deploy timeline `0..180` with a recoil tail `180..250`):

| State | Clip spec | Meaning |
|---|---|---|
| Idle | `deploy[180..180]` | held deployed stance |
| Movement | `deploy[0..0]` | held folded/travel stance |
| Pre-movement | `deploy[180..0]` | folds when it starts moving (reversed) |
| After-movement | `deploy[0..180]` | unfolds when it stops |
| Attack | `deploy[180..250]` + Attack repeats 1 | the recoil kick on fire |

Plus **Clear aim layer (artillery) ON** — vehicle/artillery donors stream aim & wheel junk into the game's
procedural bone layer that must be cleared (characters leave it OFF; that layer carries their facing).

## What this unlocks

New infantry, animated creatures, robots, crewed artillery, fantasy units, nonstandard skeletons — anything whose
motion can be expressed (or re-expressed) as bone rotations.

## Current limits, honestly

- **Translation *motion* can't play** (engine rule). Sliding parts need the far-pivot rotation trick.
- **Multiple armatures in one file:** only the first is used.
- **Morph targets / shape keys** aren't supported on the conversion path.
- Keep models to a sensible triangle budget (the "Reduce to ~tris" field; ~24k default) — the whole roster shares
  one GPU pool ([Vertex-Budget.md](Vertex-Budget.md)).

## "Is it my model or the pipeline?"

Run `Tools ▸ HAF ▸ Tests ▸ Bake Conversion Gate Test (litmus)`: it synthesizes a known-good rig, bakes it through
the full pipeline, and verifies every engine invariant. If the litmus passes, the pipeline is fine — the problem is
in your model, and the symptom table in [Factory-Manual.md §16.5](Factory-Manual.md) maps what you see in-game to
what's wrong with the rig.
