# Projectiles — a custom model as a unit's fired munition

**Status: PARTIAL SUCCESS (2026-07-17) — a custom mesh flies as a unit's projectile in-game.** The fourth injection
axis, after units, districts, and pawn props. Proven end-to-end: Humankind's Era-6 **Anti Tank FPV** now fires an FPV
**kamikaze drone** (a free Sketchfab model) instead of a cruise missile — it cruises nose-first, rolls upright, and
nose-dives onto the target.

"Partial" because the **skin is limited to the donor's** (a spear-brown, not the drone's own olive), and there is no
cheap recolor — see *Limits* below. Usable, not yet pretty.

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

## Limits (the "partial")

- **Skin = the donor's brown.** The drawer draws the mesh through its output-layer **atlas**, not the mesh's own
  texture (`KamikazeDrone_Atlas` ships but is unused). All mesh donors share one brown atlas, so brown is the only
  option. A uniform **tint won't recolor it** (shader ignores particle color for textured meshes). The *only* real
  recolor is building a **custom FX output-layer + atlas** holding our texture and pointing the drawer's `visualOutput`
  at it — a genuine investigation like the district-material work, **not done** (not worth it for a ~2s projectile).
- **No black.** Follows from the above.
- **Small-on-screen legibility.** A detailed 10k-vert drone reads muddy when miniaturized in flight. **Next step:** a
  simpler, chunkier model (or the older low-poly drone) would silhouette better at projectile scale.

Verdict: a working, reusable 4th axis (any model → any editable unit's projectile, with a real explosion), shipped brown
and legibility-limited. Good enough to ship as a proof; the skin is the open frontier.
