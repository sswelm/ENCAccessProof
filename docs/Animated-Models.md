# What kinds of animated models can HAF import?

The short answer: **more than the community thinks is possible.** The public consensus is still "anything moving is
not possible" in Humankind modding — HAF has shipped a spinning-prop drone, a folding/firing howitzer, and a full
humanoid character (a raw Sketchfab auto-rig) as working in-game units.

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

- **Examples shipped:** the Combine soldier (62-bone ValveBiped) — stands, turns with movement, idles, attacks.
- **What you do:** tick **"Convert raw rig"** in the Animation Lab and bake. The conversion is automatic: it makes
  the clip's first visual pose the new rest pose, re-derives the entire animation as pure rotations, renames bones
  so the engine's sorting can't scramble them, and exports unit-clean (usually with "Fix 100×" OFF).
- **If the result is mis-oriented:** add a Rotation and probe one axis at a time — and judge **in-game**, not in the
  preview (the preview's orientation is meaningless for animated models).

## What this unlocks

New infantry, animated creatures, robots, crewed artillery, fantasy units, nonstandard skeletons — anything whose
motion can be expressed (or re-expressed) as bone rotations.

## Current limits, honestly

- **One clip per model** plays in-game today (looped, or behavior-driven: fire-once / deploy). A state machine
  (idle vs run vs after-move) is designed and planned, not yet shipped — characters currently idle while moving.
- **Translation *motion* can't play** (engine rule). Sliding parts need the far-pivot rotation trick.
- **Multiple armatures in one file:** only the first is used.
- **Morph targets / shape keys** aren't supported on the conversion path.
- Keep models to a sensible triangle budget (the "Reduce to ~tris" field; ~24k default) — the whole roster shares
  one GPU pool ([Vertex-Budget.md](Vertex-Budget.md)).

## "Is it my model or the pipeline?"

Run `Tools ▸ ENC ▸ Tests ▸ Bake Conversion Gate Test (litmus)`: it synthesizes a known-good rig, bakes it through
the full pipeline, and verifies every engine invariant. If the litmus passes, the pipeline is fine — the problem is
in your model, and the symptom table in [Factory-Manual.md §16.5](Factory-Manual.md) maps what you see in-game to
what's wrong with the rig.
