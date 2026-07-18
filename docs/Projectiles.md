# Projectiles — a custom model as a unit's fired munition

**Status: WORKING (2026-07-17, refined 2026-07-18) — a custom mesh flies as a unit's projectile in-game.** The fourth
injection axis, after units, districts, and pawn props. Proven end-to-end: a Humankind unit now fires a custom **FPV
kamikaze drone** (a free Sketchfab model) instead of its vanilla projectile — it cruises level with rotors up and
nose-dives onto the target. 2026-07-18 landed the big pieces: the **firing-count / wave mechanic** (fully decompiled), the
**single-pawn-vehicle** fix that gives *one clean drone per launch*, and the finding that the **skin colour is model/UV
dependent** (not a fixed "brown curse"). One open item: the **launch audio** plays a gunshot (see *Limits & open items*).

The skin still can't show the mesh's *own* texture (it wears whichever atlas region its UVs sample), but that's a
choose-the-model / choose-the-orientation knob, not a hard limit — see below.

Like pawn props, this rides the game's **own data path**: a unit's presentation pawn definition already names a
`Projectile` asset, and that field is ordinary moddable data. No plugin is required to wire it (a plugin fallback exists
for units you can't edit — below).

---

## How projectiles work (decompiled)

A `ProjectileAsset` (`Amplitude.Mercury.Data.World.ProjectileAsset`) has **no mesh field**. Its visuals are three HgFx
particle effects, plus flight params:

```
ProjectileAsset
  .speed / .slowingFactor / .missProbability / .missSpread
  .muzzle          (FxEvolverMaterial GUID)  — the launch flash
  .trail           (FxEvolverMaterial GUID)  — WHAT YOU SEE FLYING
  .defaultImpact   (FxEvolverMaterial GUID)  — hit effect
  .materialToImpact[] { MaterialName ("Ground"/"Metal"/"Wood"/"Leather"/"Plate"/"Shield"/…), Fx, Orientation }
                    — per-TARGET-SURFACE hit effect; the game matches the DEFENDER's material hash
```

HgFx is a GPU compute-particle system, **but one render mode is MESH particles**, backed by the *same* `FxMesh` +
content-layer machinery the unit/district/prop bakes already feed. The flying mesh lives on the **`trail`**:

```
trail → FxEvolverMaterialDrawer
          .mesh          [FakeAssetReference(FxMesh)]  ← the swappable leaf (our drone FxMesh)
          .visualOutput  (output layer + subshader)    ← the shader + ATLAS = the skin
          .texture0/1    (GUID KEYS into that atlas)   ← NOT free textures; GUIDToIndex → a UV rect
          .rotateAxis=z, .moveOption=Velocity          ← welds mesh +Z to the velocity vector
          .importSize, orientation/size tables, .color (uniform tint — ignored for textured meshes)
```

The drawer **auto-loads its `mesh` by GUID** at load (`ResolveDependencies → AsynchEnsureMeshLoaded(mesh) →
GetMeshIndex`), and an FxMesh in our built mod bundle resolves by GUID — so, unlike pawn props, **no runtime
registration is needed**. It's pure asset authoring.

**`speed` is consumed** (`presentationSubPawn.Projectile.Speed → GetProjectileSpeed()`, `*= battle timescale`), so drop
it (15 → 1–4) to actually *see* a short-range munition; watch at normal battle timescale.

---

## The donor-clone recipe (why we don't author from scratch)

Authoring a GPU compute evolver from scratch is a rabbit hole. Instead **clone a donor projectile's trail drawer and
swap only its `mesh`** — the same borrow-a-donor pattern as districts/props.

**Only a subset of projectiles are usable donors.** A donor's trail must be a `FxEvolverMaterialDrawer` with a
**non-null `mesh`**. A *sprite* drawer (mesh = null, renders `texture0` as a billboard) has **no mesh layer**, so our
injected mesh renders **nothing** in flight. Projectile Lab's Dump prints a **VERDICT** so you never waste a bake:

- `✓ USABLE MESH DONOR` — trail draws a real mesh (safe to bake)
- `✗ SPRITE DONOR` — trail is a billboard/particle; our mesh would be invisible

Survey (2026-07-17): **every modern/siege/naval projectile is a sprite** — CanonObusier, Torpedo, Mortar,
MissileCruise all `✗` (the devs use cheap sprites for fast projectiles no one watches closely). The **only mesh donors
are the thrown solids** — ThrownSpear, ThrownAxe — and *both share one mesh GUID and one brown atlas*, so there is no
darker/steel skin. ThrownAxe additionally **tumbles** end-over-end (its drawer spins the mesh), which a drone
shouldn't. **⇒ ThrownSpear is the best donor: brown but stable.**

### Impact donor (splitting the roles)

The **impacts are plain FX refs**, independent of whether the trail is a mesh or a sprite. So a mesh donor (visible
drone) can borrow an **explosive sprite donor's** impacts. Projectile Lab's *Impact donor GUID (opt.)* copies
`defaultImpact` + `materialToImpact` (+`muzzle`) from a second projectile. Best boom: **Mortar** (explosion +
`subEvolverAudio`) or **CanonObusier**. So the shippable drone = **ThrownSpear trail + Mortar impacts** = a stable
brown drone that **explodes** (instead of a spear-thunk).

---

## The editor side — Projectile Lab (`ProjectileBaker.cs`)

**Tools ▸ ENC ▸ Projectile Lab** authors the whole chain from a model file:

1. **Dump** a donor by GUID (accepts the Asset Picker's 32-hex form) — prints the VERDICT + the FX graph, and
   **auto-fills the donor field** on success.
2. **Bake munition** — static bake → bone-free rigid FxMesh (`DistrictBaker.BakeFxMesh`) → `CopySerialized`-clone the
   donor drawer with our `mesh` swapped in → clone the ProjectileAsset with `trail` → the clone (+ optional impact
   donor, + optional speed). Writes `Projectile_<name>.asset`, `<name>_TrailDrawer.asset`, `<name>_FxMesh`.
3. **Tint** — sets the drawer's `color`. **Does nothing for a textured mesh** (the shader samples the atlas directly and
   ignores particle color — verified twice); kept only for donors whose shader respects it.

Re-bakes use `WriteAssetKeepingGuid` (CopySerialized onto the existing asset) so the ProjectileAsset **keeps its GUID**
— the unit's Projectile reference survives a re-bake (delete+create would blank it → the unit fires nothing, an easy
red herring).

### Orientation

The drawer **welds mesh +Z to the velocity vector**, so whatever is on +Z leads travel — you **cannot** aim the nose
independently (forcing "nose-down" just knocks it off the flight axis → sideways/backwards flight). Tune roll/heading
with the FxMesh `ImportAngles` (draw-time, no re-bake; rebuild the bundle to see it). For the drone: **`(0, −90, −90)`**
= nose-first, rotors up, and it nose-dives on the trajectory's terminal descent for free. **Avoid `X = 90`** — it's the
Unity Z-X-Y **gimbal pole** (produces surprise 180° flips).

---

## Wiring it to a unit

**Inspector (data):** set the unit's presentation pawn definition **`Projectile`** field
(`PresentationPawnAbstractDefinition.Projectile`, a `ProjectileAssetReference`) to `Projectile_<name>` in the SDK Asset
Picker, rebuild the mod. ENC ships modded pawn defs, so it travels with the bundle. This is the whole wiring — scoped to
that one pawn def (only that unit changes).

**Plugin fallback (`[Projectiles] ProjectileOverrides`):** for units whose pawn def you can't edit, `Hk_ProjectileOverride`
(a postfix on `AnimationManager.AnimationLoad`) re-points the field by GUID at runtime:
`ProjectileOverrides = <pawnDefGuid>=<projectileGuid>;…` (each side four ints). Off by default (blank).

---

## Firing count — the "wave" mechanic (2026-07-18, decompiled)

When the projectile is a **slow, big MESH** (a drone) instead of a fast vanilla **sprite**, the game's normal multi-fire
becomes *visible* as a wall/swarm. The count is **combat-driven**, and it is NOT the animator, formation, or projectile:

- **One projectile per firing pawn**, gated by `pawnDef.Projectile != null` (Assembly-CSharp ~74410). A pawn whose def
  has `Projectile = None` fires nothing.
- **Waves = `ceil(defendersToKill / attackerPawnCount)`** (`UnitActionRangedFightSequence.StartUnitAction`). A wave = every
  soldier throws once; if the unit's damage kills more enemies than it has soldiers, it **loops** (`while(num2>0)`) and
  throws again until the kill count is met. So a 3-soldier squad that kills ~9 → **3 waves of 3** = the wall; a 9-soldier
  (or low-kill Era-1 spear thrower) → **1 wave**. Total drones ≈ `defendersToKill`.
- **Ruled out** (hours of it): the **Animator Override Controller** changes only the throw motion/sound (Rifle = semi-auto
  burst, Crossbow/Thrown = single throw, Long_Gun = *no drone*, Boomerang = wiggle, horseback = floating ghost-mounts);
  **Same Row Attack** is pawn↔target pairing (on for the spear thrower too); **formation rows** and **Prepared Attack
  Loop** don't touch it either. Soldiers *require* an override to fire (base humanoid animator has no attack clip); vehicles
  fire via their own animator (why a tank works with `Animator = None`).
- **`Force Ranged Multi Kill`** (pawn-def flag) collapses to ONE wave (`if(flag) return` skips the extra-wave loop) BUT the
  multi-kill path is an *instant kill* that **skips `FireProjectile` → no drone spawns**. So **no data combo gives one-wave +
  visible drone** — that split would need a plugin cap (patch out the `while(num2>0)` loop). *Designed, not built.*

**THE FIX THAT SHIPPED: use a SINGLE-PAWN (vehicle) unit as the base.** A car/tank/Humvee is 1 pawn with low kills →
`ceil(1/1) = 1` wave → **one clean drone**. Proven in-game (a Humvee launches a single kamikaze drone). Reskinning that
vehicle to a soldier model is then a pure Model Factory swap (the vehicle fire-logic launches the drone, so no throw
animation is needed).

Related data levers on the unit: `SpawnWeight` (default 10, `0` = never) = weighted-random which pawn def fills each
formation slot when several are listed — the lever to **mix** firing (`Projectile` set) + non-firing (`Projectile = None`)
pawns in one squad. Formation `Dummies[]` = soldier count (`Formation_Wedge_3` = 3 rows). Visual Affinity =
the empire's cultural art style, selecting which pawn set (`PawnDefinitionsPerVisualAffinities`) renders — appearance only,
no effect on the count.

---

## Limits & open items

- **Skin is NOT "cursed brown."** The drawer draws the mesh through its output-layer **atlas** (not the mesh's own
  texture), but the **colour = which atlas REGION the model's UVs sample**, and it further **shifts with orientation**
  (different faces get lit). The fpv drone's UVs rolled brown; `drone_clean.glb` rolled a grey/metallic region (tan at some
  angles). So **reroll the skin by swapping the model** (or nudging its UVs) — no custom atlas needed. A uniform **tint
  still does nothing** (shader ignores particle colour for textured meshes). A *chosen* colour would still need a custom FX
  atlas (not done).
- **Orientation follows the trajectory.** The drawer welds mesh-Z to velocity, so the drone's **pitch tracks the arc** —
  level at apex, **nose-down diving onto the target** (correct kamikaze behaviour, not a bug). ImportAngles only control
  **roll** (rotors up/down). Final for `drone_clean.glb` as a projectile: **`(0, 0, 180)`** (natural frame + one Z-roll).
  Avoid `X = ±90` (gimbal pole → surprise flips). Model legibility improved by swapping to the leaner `drone_clean.glb` at
  `size ~1`.
- **OPEN — launch audio:** the shot plays the *launching unit's weapon-fire sound* (a Humvee gunshot), not a drone whoosh.
  Fix path: check/clear the ProjectileAsset's `Muzzle`, else trace the Wwise event with the plugin's F8 **Audio Trace** and
  repoint it (there's a `drone-fly` wav to use). Not yet done.
- **OPEN — drone visual lost when the firing pawn has a CUSTOM ANIMATED visual (2026-07-18):** replacing the
  DroneSquadFPV Humvee with the injected Combine soldier kept the sim kill + attack/impact audio but the drone
  projectile **no longer displays**. `FireProjectile` rides the pawn's throw ANIMATION, and the plugin's pose hook
  stomps `Pose0` with our clip every frame, so the throw clip (and its spawn event) never plays. Designed fix: read
  the game's Pose0 before overriding — when it isn't the ordinary idle/move clip, pass the game's pose through (an
  attack window); belongs to the same work as the idle/move state machine. Until then: a unit whose pawn fires a
  visible projectile should keep a vanilla/borrowed visual, or accept the invisible shot.

## Projectile Lab additions (2026-07-18)

- **Dump prints a VERDICT** — `✓ USABLE MESH DONOR` (trail is a drawer with a non-null mesh) vs `✗ SPRITE DONOR` (our mesh
  would be invisible) — so you never waste a bake on a sprite donor. A successful dump **auto-fills the donor field**.
- **Impact donor** (`ProjectileOverrides` unrelated) — optional field copies `defaultImpact` + `materialToImpact` (+muzzle)
  from a *second* projectile, so a mesh donor (visible) can borrow an explosive sprite donor's boom (e.g. Mortar).
- **`WriteAssetKeepingGuid`** — re-bakes preserve the ProjectileAsset's GUID so the unit's Projectile reference never
  silently blanks.
- **Window persistence** — Projectile Lab and Prop Lab now `[SerializeField]` their form so they survive a domain reload
  (matching the Model Factory / Unit Retexture windows) instead of closing on every recompile.

Verdict: a working, reusable 4th axis (any model → any editable unit's projectile, with a real explosion), shipped brown
and legibility-limited. Good enough to ship as a proof; the skin is the open frontier.
