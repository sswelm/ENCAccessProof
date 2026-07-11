# Capabilities — what the Factory does (proven in-game)

The full, detailed capability list. The README carries the highlights; this is the reference. For *how to use* these
see the [Factory Manual](Factory-Manual.md).

- **Animated custom models — a first, now one-click.** A **quadcopter drone** injected onto a land-vehicle unit renders
  full-size and textured **and spins its own propellers from its own baked animation** — no engine mod, no GPU-skinning
  hang. Tick **Animated** in the Factory and a single Bake does it all: Blender slims the rigged model (keep armature +
  chosen clip, strip to the spinning bones, auto-clamp the frame range), then it bakes an Amplitude `Skeleton` +
  `ClipCollection` + atlas and writes the registry; at runtime the clip is registered and a `PawnManager.AddPawnEntry`
  hook drives the pawn's pose onto it — normalized by clip duration so it plays at real speed. Works for **any number of
  instances**. Clip/bone/hide-donor fields are **Pick-driven** (read from the model's glTF + the plugin log).
- **Deploy-when-stopped — a model that reacts to *movement*.** Tick **Deploy when stopped** and the model **plays its deploy
  clip forward** when the unit stops (e.g. an M114 howitzer's trail legs spread + barrel elevates) and **snaps folded** while it
  travels — a per-unit *held state* driven by movement, not an event. It reuses the fire-on-attack sim→presentation bridge but
  triggers off the unit's **actual render-position change between polls** (real tile traversal), so it's concurrency/AI-safe
  (only *visible*, our-model units). **Rest holds deployed, folds instantly** — two hard-won details: (1) detect travel by
  position delta, NOT the game's `IsMoving`/`IsAnyPawnMoving` (the wait-to-idle/turn settle after stopping reads as "moving" and
  drops the deployed pose) — the settle doesn't move the tile, so a position check is instant to fold *and* settle-immune; (2)
  the pose sampler does `Mathf.Repeat(Time,1)`, so poseTime **exactly 1.0 wraps to 0.0 = the folded frame** — the deploy target
  is clamped to 0.999 (and bake `deployPoseTime` ≤ 0.99) so it holds the last real frame. **Gradual + tunable:** the
  deploy ramps at the clip's authored speed × a **Deploy speed** slider; **Deployed pose time** sets how far it opens (also the
  live barrel-angle knob when the clip is baked with an over-range elevation). **Real deploy clips from rigid-part-animated
  models:** `Tools/deploy_convert.py` converts a model animated by *moving parts* (node transforms, no skinning — common in
  Maya/Sketchfab exports) into a bone-per-part skinned armature the bake can consume: strips soft-skinned crew (they collapse the
  bake), retargets the trail-leg spread (scale) and barrel elevation (amplify past the source's max), and — critically — **binds
  the mesh at the rest frame** so it isn't baked pre-posed and double-deformed. Args: `in out start end strip readyFrame legScale
  barrelScale`. **Donor-aim override:** artillery donors aim their barrel via a procedural `PawnEntry.BoneRotation` layer that
  twisted the injected barrel; the pose hook zeros it so only our clip drives the skeleton. Mutually exclusive with fire-on-attack
  until multi-clip lands. *(Known limitation: the Factory's static **preview** shows the folded bind pose, not the deployed pose —
  judge the result in-game, or via the deployed-preview attempt on the `howitzer-deploy-wip` branch once its duplicate-mesh issue
  is fixed.)* See [Firing-On-Attack.md](Firing-On-Attack.md).
- **Fire-on-attack — a model that animates when the unit *fires*.** Tick **Fire on attack** and the baked clip plays
  **once, on the combat action**, instead of looping: the model rests, then plays a single pass the moment the unit
  attacks and returns to rest. Proven with a **howitzer whose barrel elevates only when it bombards**. The plugin
  subscribes to Humankind's own combat event bus (`SimulationEvent_ArtilleryStrikeStarted`), matches the firing unit's
  `UnitDefinition` to the injected model, and triggers a single `0→1` playthrough of its clip — re-entrant, so rapid fire
  restarts cleanly. Author the clip to start *and* end at rest. Extensible to bombers (`AirStrikeStarted`) and melee
  (`BattleStarted`) the same way. See [Firing-On-Attack.md](Firing-On-Attack.md).
- **Multiple static models live**, each added with no new code: a **Zeppelin**, an **LCAC Hovercraft**, and a
  fully-textured **USS Zumwalt stealth cruiser** (first textured naval-combat unit) — correct orientation, correct skin,
  sitting at the waterline.
- **Match the donor to your model.** A model rides a donor unit's skeleton + animation, so pick a donor whose *moving
  parts* match yours: a custom **helicopter** body (modelled rotor-less) borrows the donor's spinning rotor for free; a
  drone/ground model wants a donor with **no animated sub-parts and a full idle/move animation set** (a land vehicle is
  ideal). The one thing injection **can't** do: *remove* a donor's *animated* sub-part (a rotor, spinning wheels) — those
  are baked into the pawn at spawn. But it **can** give your model **its own** animation (see the animated bullet above),
  which overrides the donor's. And for a **static** model that only suffers the donor's *whole-body* idle/move bob (e.g. a
  rigid airship on a hovering drone donor), the **Freeze donor animation** runtime flag pins the donor's pose so the mesh
  holds rigid while still gliding tile-to-tile — no re-bake. Choose the donor accordingly; see the drone case study in the docs.
- **Any number of materials — GLB *and* FBX.** A model with N materials (the Zeppelin has 4; the AH-1 Cobra has **52**)
  is packed into one atlas and each sub-mesh's UVs are remapped into its rect — no per-model code, no material cap. The
  `glbconv` converter emits per-material `usemtl` groups + a `.mtl` (and an 8×8 solid-colour swatch for any flat,
  textureless material) so a **multi-material GLB keeps its per-material split**, just like FBX. Near-black UV dead-zones
  are filled neutral so unused texture regions never render as black patches.
- **Heavy or single-sided/CAD meshes, handled.** A built-in **vertex reducer** (Blender quadric decimation, per-object
  so thin parts survive) shrinks oversized models to fit the engine's shared mesh buffer. A **winding fix** rewinds
  faces outward so single-sided / CAD "sketch" meshes render single-sided instead of culling to invisible (e.g. a
  hovercraft skirt); a **double-sided** toggle is the heavier fallback for genuinely non-convex thin shells (a mixed
  model — convex hull + non-convex fans — can use both). And **height-based UVs** map a simple vertical-gradient albedo by
  height (black skirt low, grey hull high) so an untextured CAD model gets a usable skin without UV-unwrapping.
- **Know the ceiling.** Custom meshes share **one GPU buffer — ~100k vertices / ~250k indices (~83k triangles), 32-bit
  indices — across ALL injected models *and* the game's own fx meshes** (from the decompiled
  `FxComponentMeshContentManager`). Overflow is silently dropped (missing/see-through geometry); the reducer exists to
  stay under it, and double-sided counts twice. **Proven symptom** (AH-1, 2026-07-07): the overflow truncates the
  **tail of the last-registered model's mesh** — its body renders, its late-order small parts (rotor mast) silently
  vanish, while the editor preview (no shared buffer) looks perfect. Watch the bake log's `verts=` line; UV-seam
  splitting makes verts ≈ 2× tris on textured models, so budget tris accordingly (~12k tris ≈ ~24k verts).
- **Any format in:** GLB / glTF / OBJ / FBX, and **`.blend`** (auto-converted via an auto-detected Blender install).
- **Correct textures out of the box:** custom skins land right-side-up — the bug that put the Zumwalt's markings on the
  superstructure (a glTF-V-top vs OBJ/Unity-V-bottom mismatch) is fixed in `glbconv` by flipping V (`1 - v`) on OBJ
  write. `glbconv` also **normalizes non-[0,1] UV tiles**: a model that maps into a higher tile (e.g. the whole Zeppelin
  hull sits in V 1→2, relying on texture *wrap* to repeat its skin) has its UVs **integer-shifted** back into [0,1]
  before the flip — because the atlas packs each texture into a fixed rect and can't wrap, so un-shifted tiled UVs would
  sample outside the rect and the skin would vanish (fine in Blender, blank in-engine). Integer shift, so tile-crossing
  triangles never tear. Genuine *repeat*-tiling (a small texture spanning [0,N]) remains outside what an atlas can do.
- **Texture isolation:** each model gets a private `FxOutputLayer` clone, so its skin never bleeds onto the vanilla donor
  unit — proven on screen with a custom cruiser and its donor corvette side-by-side, each keeping its own skin.
- **Skin controls at bake time.** The injection ships a *flat* albedo (donor PBR — normal/metallic/roughness —
  neutralized so the donor's camo can't bleed through), which reads muddy for a source that leaned on shine or a dark
  texture. **Albedo brightness** and **Albedo saturation** sliders correct that into the baked atlas; a **Keep black**
  toggle preserves an intentionally black material (glass canopy) that the default near-black→grey dead-zone neutralize
  would otherwise flatten.
- **Small shipped bundle.** Bake *inputs* (the source model + extracted OBJ/albedos) live in `Assets/FactorySource/`,
  which is **not** part of the shipped mod — so licensed source models are never redistributed. The baked atlas is capped
  by a configurable **Atlas size** (256 / 512 / 1024 / 2048, default 512) and DXT1-compressed, so each shipped skin is
  ~0.1–2 MB (a big airship wants 1024; a small unit is fine at 512).
- **Freeze the donor's animation (static models).** A rigid model on an animated ground/hover donor inherits the donor's
  idle/move bob. The **Freeze donor animation** runtime flag pins the donor's pose so the mesh holds still while the pawn
  still glides tile-to-tile — matched across every instance the same way animated models are (descriptor + forced
  skeleton), so it holds for the 2nd, 3rd… unit, not just the first. Static models only; no re-bake.
- **Add a model = bake it.** The Factory writes the registry; the plugin picks it up on next launch.
- **The registry can't be lost.** Atomic writes (no truncation on an interrupted save), a corrupt-file guard (an
  unparseable registry is copied aside and never overwritten), and a **git-tracked versioned backup with
  auto-restore** — after a game reinstall or "verify files", just opening the Factory restores the registry into
  `BepInEx\config` automatically.

## Known limitations

- **Editor-only texture preview:** right after a multi-material bake, Unity may show the baked atlas stale until the
  source textures are touched (open them in the Project view, return to the model). The shipped/in-game result is
  correct — a Unity editor texture-residency quirk, not a bake defect.
- **Resource-name folder casing (cosmetic):** the source folder `Assets/FactorySource/<name>/` is case-insensitively matched
  by Windows/Unity — if a differently-cased asset with that name already exists (e.g. a vanilla `attackHelicopter512.png`
  portrait), a new `AttackHelicopter/` folder inherits the existing lowercase spelling. Bake-time only; the baked assets,
  the registry, and in-game loading (by GUID) are all correctly cased. Pick a non-colliding `resourceName` if it bothers you.
- **Flat-albedo lighting artifacts (cosmetic, inherent to the technique):** the injection neutralizes the donor's PBR maps
  (normal/metallic/roughness) so the donor's camo can't bleed onto the skin — but that also means an injected model doesn't
  get the full PBR response a vanilla unit does. Two visible consequences:
  - **Dark shadow side.** The face turned away from the (fixed) sun falls off to near-black instead of being lifted by fill,
    most obvious on big smooth surfaces (an airship flank). Nudge **Albedo brightness** up to soften it (lightens both sides).
  - **Grazing-angle shimmer.** On flat surfaces at a specific sun-relative heading (e.g. a ship's hull travelling *east*),
    the shading can shimmer/flicker as if effects fight — the neutralized surface interacting with the engine's
    reflection/depth passes at grazing angles. Confirmed **not** fixable from the mod side by the reachable levers: changing
    **Normals mode** and forcing the surface fully matte (roughness 1.0) both left it unchanged, so it's the engine's render
    passes, not our material. Diagnosing further needs a live GPU frame capture (RenderDoc). Worst on ships (they sit in the
    reflective water); subtle on round/small models. Treated as a technique limitation rather than a bug.
- **Animated bake unit scale (auto-prefilled per-model toggle):** some rigged FBX exports embed a Blender-metres→centimetres
  unit scale that makes the model bake ~100× too big and float high (fine in the Factory preview, wrong only in-game); others
  don't, and the two need *opposite* handling. There is **no single rule** — so it's a per-model checkbox, **Fix 100× oversize
  (FBX unit scale)**, that the Factory **auto-prefills** when you pick a GLB/glTF by reading the model's true size (accessor
  extent × node scale): metre-scale → on, tiny-authored (e.g. a 0.0025u drone with a 0.01 root node scale) → off. Best-effort
  and overridable; for FBX/.blend/OBJ (unreadable cheaply) it makes no guess and you set it by hand. On = measure the FBX at
  true scale then bake with the unit scale on, so Size = in-game units; off = normal import. The *static* path is unaffected.
