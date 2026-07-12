# Universal Model Factory — User Manual

How to put your own 3D model onto a Humankind unit, step by step. This is the practical guide.

The Factory is a Unity editor window (**Tools ▸ Universal Model Factory**). You give it a model file and a target unit, set
a few options, press **Bake**, then rebuild the mod. The in-game plugin reads what you baked and renders it.

---

## 1. Prerequisites

- **The modding Unity project** open (the one with the Humankind SDK + the Factory editor scripts under
  `Assets/Scripts/Editor/`).
- **A model file**: `.glb`, `.gltf`, `.obj`, `.fbx`, or `.blend`. It must be **UV-mapped + textured** if you want a skin.
- **Blender** — required only for: **animated** models, **`.blend`** import, and **Reduce-to-tris** decimation. Static
  GLB/OBJ/FBX bakes need no Blender. It's auto-detected under `C:\Program Files\Blender Foundation`; if it's elsewhere,
  set the path in the Factory's **Settings** panel.
- **The game installed** (Steam auto-detected). The registry the plugin reads is written into `<Humankind>\BepInEx\config`.

---

## 2. Quick start (static model)

1. **Tools ▸ Universal Model Factory**.
2. **Pawn description → Pick** — choose the unit your model replaces (e.g. `Era6_Common_Hovercrafts_01`). A **Resource
   name** is suggested; keep or edit it.
3. **Model file → Browse** — pick your `.glb`/`.obj`/`.fbx`.
4. Set **Size** (world length of the longest axis) and, if needed, **Rotation** / **Position**.
5. **Bake.** Watch the Console for `[Factory] <name> DONE. skeleton=… atlas=…`.
6. **Rebuild the mod** (see §6) and relaunch.

That's the whole loop. Everything below is detail and the animated workflow.

---

## 3. The window, field by field

### Settings — game & Blender path (foldout at the top)
- **Game path** — auto-detected `<Humankind>\BepInEx\config` (where the registry is written). Override if detection
  misses your install. A `⚠` on the header means it wasn't found.
- **Blender** — the detected `blender.exe`, or `⚠ not detected`. Set the **Override** (or `EditorPrefs 'ENC.blenderPath'`)
  if Blender is elsewhere or only on `PATH`. Only matters for animated / `.blend` / Reduce-to-tris.

### 3D resource
- Dropdown of existing baked models (or `<New>`). Picking one **loads its settings** so you can re-bake with tweaks.
- **Refresh** re-reads the registry. **Remove** drops the selected entry from the registry (the baked assets stay in the
  project; the plugin just stops injecting it).

### Resource name / Pawn description / Model file
- **Resource name** — unique id; names all the baked assets (`<name>_Skeleton`, `<name>_Atlas`, …).
- **Pawn description** — the target unit. **Pick** opens a searchable list of every `PresentationPawnDefinition`.
- **Model file** — `.glb/.gltf/.obj/.fbx/.blend`. **Leave empty when re-baking** an existing resource to reuse the
  already-imported model with new settings (fast iteration).

### Animation
- **Animated (own rig + clip)** — bake the model's **own armature + animation** so it moves in-game (e.g. a drone's props
  spin). The toggle is **greyed out for models with no animation** (a cheap probe checks without Blender). See §5.
- **Clip name** *(Pick)* — which clip to bake when the model has several (a Sketchfab model often ships `hover`,
  `exploded_view`, …). **Pick** lists the clips read from the model. Empty = the model's first/assigned clip.
- **Animate only bones** *(Pick)* — comma-separated bone-name **prefixes** to keep animation on (e.g. `prop`). Strips
  everything else (camera pans, body bob) that would make the model wobble. **Pick** lists the bone prefixes with counts.
  Empty = keep the whole clip.
- **Fix 100× oversize (FBX unit scale)** — bake-time scale fix. Some rigged FBX exports embed a metre→centimetre unit
  scale that makes the model bake **~100× too big and float high** (fine in the preview, wrong in-game); tick this and the
  baker measures the FBX at its true scale then bakes with the unit scale on, so **Size means in-game units**. **Per-model**
  — some exports need it, some break with it: if a model bakes huge/floating, tick it; if ticking it makes the model
  **vanish or shrink to a speck**, untick it. (The drone bakes right **off**; the howitzer needs it **on**.) Re-bake after
  changing. Static path unaffected.
  - **Auto-prefilled on Browse:** when you pick a **GLB/glTF** model, the Factory reads its *true* size (POSITION accessor
    extent × node scale — what glbconv would report) and pre-ticks this for you: metre-scale models (≈≥0.1u) → **on**,
    tiny-authored models (a GLB with a small root node scale, e.g. a 0.0025u drone) → **off**. It's a best-effort guess you
    can override, and a status line shows what it chose. For **FBX / .blend / OBJ** (and `matrix`-transform glTF) the size
    can't be read cheaply, so it makes **no guess** and leaves the box as-is — set it by hand there.
- **Fire on attack (play once)** — play the baked clip **once when the unit attacks**, instead of looping. The model rests,
  then plays a single pass on the shot and returns to rest — e.g. a **howitzer barrel that elevates only when it bombards**.
  The plugin listens for the game's artillery-strike event, matches the firing unit to this model, and triggers one `0→1`
  playthrough. **Author the clip to start *and* end at rest** so the single pass looks clean. Leave **off** for a continuous
  loop (a drone's spinning prop). Animated models only; runtime flag (no re-bake to toggle — Bake just re-writes the
  registry). See §5 and [Firing-On-Attack.md](Firing-On-Attack.md).
- **Deploy when stopped** *(+ **Deployed pose time**, **Deploy speed**)* — **play the clip forward** when the unit stops (e.g.
  a howitzer that spreads its trail legs) and **snap folded** the instant it moves. **Author the clip** so frame 0 = travelling
  and the deployed pose sits at **Deployed pose time** (0..1; `1` = a real deploy clip's end). **Deploy speed** multiplies the
  ramp (1 = the clip's authored speed, 2 = twice as fast); folding on move is always instant. It's a *held state*, per-unit,
  driven by the unit's moving state — concurrency/AI-safe (only visible, our-model units are polled). Animated models only; runtime flag.
- **Recoil speed** *(with Deploy when stopped + Fire on attack)* — a deployed model can ALSO kick when it **bombards**, from the
  **same clip**: author the clip as `deploy [0 .. Deployed pose time]` then a `recoil tail [Deployed pose time .. 1]`; the tail
  plays once per shot (only the gun that fired) then returns to the deployed hold. **Recoil speed** multiplies the kick's playback
  speed (runtime; no re-bake to change). **Reality check:** the clip bake keeps **rotation only, not translation**, so a real
  sliding recoil can't be baked directly — `deploy_convert.py` fakes it with a far-pivot helper bone (FK-arc) that swings the tube
  through a near-straight arc. It reads as a backward kick with a slight swing; a perfectly straight glide isn't achievable.
  - **Rigid-part-animated source?** Many Maya/Sketchfab models animate *moving parts* (a turret, trail legs, landing gear) by
    node transforms, not skinning — the animated bake needs an armature. Run **`Tools/deploy_convert.py`** first:
    `blender -b -P Tools/deploy_convert.py -- in.glb out.glb [start end] [stripCsv] [readyFrame] [legScale] [barrelScale] [recoilSrcStart recoilSrcEnd step mag]`
    — it builds a bone-per-part skinned armature carrying the same motion, trims to the deploy sub-range, strips crew/props,
    **binds at the rest frame** (so the mesh isn't baked pre-posed), and optionally retargets the barrel to a `readyFrame`
    elevation (`barrelScale` > 1 exaggerates past the source's max), scales the leg spread (`legScale`; 1 = full, 0 =
    stay folded), and appends a **recoil-on-fire tail** (FK-arc kickback; `mag` scales the slide distance, 2 ≈ half the tube).
    Bake the result normally. Find the deploy sub-range + `readyFrame` (and the source recoil frames) by scrubbing the clip in Blender.
  - **The rest state holds deployed, folds instantly** — the runtime half (already built): the plugin detects travel by the
    unit's **render-position change** (not the game's `IsMoving`, whose wait-to-idle settle would drop the pose), so it folds
    the instant it moves and holds deployed at rest. Also bake **`deployPoseTime` ≤ 0.99** (never 1.0 — the pose sampler wraps
    exactly-1.0 back to frame 0 = folded; the plugin also clamps to 0.999 defensively). *(The static **preview** shows the
    folded bind pose, not the deployed one — judge deploy in-game.)*

### Transform
- **Rotation offset (XYZ)** — degrees, on top of the auto forward-alignment. Static models bake this into the mesh; for
  animated models the orientation comes from the model's own rig, so export it facing correctly.
- **Position offset (x, y, z = height)** — Static models bake it in (z = waterline; negative sinks a ship). For
  **animated** models it's applied as a **runtime world offset** — **z raises altitude** — so you can change it and just
  **relaunch, no re-bake**. This decouples altitude from Size (keep a small drone but fly it high).
- **Size (units)** — the world length of the model's longest axis. This is what you tune to make the model the right
  size next to other units. The Factory computes the scale for you; the Console logs it.

### Mesh / shading *(ignored in Animated mode)*
- **Normals** — `KeepModel` (artist's normals) / `Recalculate` (hard edges via the **Smoothing angle** slider) /
  `Faceted` (fully flat).
- **Height-based UVs** — override UVs with V = normalized height, so a vertical-gradient albedo maps by height (black
  skirt low, grey hull high). For untextured CAD models that just need a simple gradient skin.
- **Winding fix (CAD/convex)** — rewind faces outward so single-sided / CAD "sketch" meshes render instead of culling to
  invisible. Lightest fix; assumes a roughly convex hull (vehicles/ships).
- **Double-sided (single-sided/CAD)** — add a back face to every surface (heavier fallback for genuinely non-convex thin
  shells). Doubles the triangle count — and **halves the effective Reduce-to-tris** automatically.
- **Albedo brightness** / **Albedo saturation** — tone-correct the baked skin (both `1.0` = unchanged). The injection
  path ships a **flat albedo** — the donor's PBR normal/metallic/roughness maps are neutralized so its camo can't bleed
  onto your model — so a skin that relied on shiny metal, or a dark/washed-out texture, reads **muddy** in-game.
  **Brightness** multiplies RGB (>1 lifts a dark skin); **saturation** scales colour around per-pixel luminance
  (0 = greyscale, >1 = punchier). Baked into the atlas, so **re-bake to apply** (no re-import needed — quick to iterate).
  Cheaper and repeatable vs hand-editing the extracted albedo. Note: the Factory preview is dim, so judge the final
  amount **in-game** (brighter lighting) and dial back if over-warm.
- **Keep black (glass/cockpit)** — *multi-material models only.* By default the bake repaints near-black atlas regions
  neutral grey, to hide UV dead-zones and packing gaps that would otherwise render as black patches. That also flattens
  an **intentionally black material** (a glossy canopy, a dark cockpit) to grey. Tick this to keep true black on such a
  model. Off by default (existing behaviour). Re-bake to apply.
- **Atlas size** (256 / 512 / 1024 / 2048) — longest side of the baked atlas. It's DXT1-compressed into the shipped
  `_Atlas.asset`, so **smaller = smaller mod bundle**. A unit is ~80 px at map zoom and its info card uses your 2D
  portrait (not the model), so **512–1024 is ample** (256 for very simple units); pick 2048 only for a unit you zoom in
  on closely. Default 512. (Before this existed, atlases baked uncompressed at up to 4096×8192 — a single `_Atlas.asset`
  could be 128 MB. DXT1 sizes: 2048 ≈ 2 MB, 1024 ≈ 0.5 MB, 512 ≈ 0.1 MB, 256 ≈ 32 KB.) Re-bake to apply.
- **Reduce to ~tris (0 = off)** — quadric-decimate a heavy model to about this many triangles (via Blender) to fit the
  engine's shared mesh buffer (~25k per model is the practical ceiling). It's a **ceiling, not a quota**: a model already
  under it passes through untouched. No Blender? Use **Convert grid** instead (below).
- **Strip parts (names)** *(Pick)* — comma-separated object-name substrings to **DELETE from *your* model** before baking
  (each match takes its children too). The **mirror of Hide-donor**, but on your source mesh: use it to drop a part you
  don't want baked in — most importantly a **helicopter's own rotor(s)**, so the **donor's animated rotor spins through** in
  its place (also crew figures, weapon pods…). The donor helicopter animates *two* rotor bones (`Helix` main + `Helix_back`
  tail), so stripping **both** your main and tail rotor gets you a spinning main rotor *and* a spinning shrouded fantail.
  **Pick** reads the object names straight from the model file (GLB/glTF; for FBX/OBJ/.blend, type them by hand — open in
  Blender to see names). Case-insensitive substring match; needs Blender (it runs a delete-and-export step). Proven on the
  RAH-66 Comanche (`Cylinder06,Cylinder07` removed its main-rotor blades).
- **Hide donor meshes** *(Pick)* — comma-separated name substrings of the **donor unit's extra parts** to hide (e.g. a
  leftover rotor). **Pick** reads the donor fragment names the plugin logged to `BepInEx\LogOutput.log` — so **launch the
  game once** with the model injected, then Pick. Runtime-only (takes effect on reload, no re-bake). *Can't* hide a
  donor's animated sub-parts (a rotor, spinning wheels) — those are baked at pawn spawn; pick a cleaner donor instead.
- **Re-spawn after load (borrowed rotor fix)** — fixes a spawn quirk: the engine draws the **first** borrowed-rotor pawn of
  a model, *when it's created*, with its rotor ~1 unit low (every other instance is fine). Tick this and the plugin watches
  for any such unit appearing — on a save-load, built in a city, or dev-spawned — and near-instantly re-runs the game's own
  pawn rebuild on it, so the rotor comes out right every time (a brief one-time flicker as it rebuilds, no unit affected).
  **Tick ONLY** for models that borrow a donor's animated sub-part (a spinning rotor, i.e. you used **Strip parts** to drop
  your own rotor); pointless flicker on any other model. Runtime-only (no re-bake needed — it's a registry flag). The delay
  before the re-spawn is tunable in the plugin cfg (`Factory/RespawnDelayFrames`, default 1 frame) if a slow machine shows
  the low rotor briefly before it corrects.
- **Freeze donor animation** — stops the **donor's** idle/move animation from bobbing your **static** mesh. A borrowed mesh
  is skinned to a rig the donor animates, so if you ride a donor with a hover/idle wiggle (e.g. the Recon-Drone donor), a
  large rigid model like an airship visibly wobbles. Tick this and the plugin **pins every pawn pose's time to frame 0 each
  frame**, so the donor clip can't advance — the mesh holds rigid while the pawn **still glides tile-to-tile** (that motion
  is transform-driven, not animation). **Static models only** (an animated model plays its own baked clip — leave it off).
  Runtime-only (no re-bake — it's a registry flag; Bake just re-writes the registry). Confirm it took with the
  `[Uni] freeze: '<name>' donor pose time pinned` line in `BepInEx\LogOutput.log`. *Note:* it holds **frame 0** of the donor
  clip; if that frame isn't a neutral pose the model may rest at a slight static offset (no wobble, just held) — report it if
  so. It lives in **Runtime** (not the Animation section) because it's a runtime flag that acts on the *donor*, and the
  Animation section is disabled for the static models that use it.
- **Convert grid** — GLB/glTF only. `0` = faithful (keeps UV seams — use for textured models). `>0` = vertex-cluster
  decimation **without Blender** (coarser; averages UVs across seams, so only for heavy *untextured* meshes).

### Texture / import
- **Reuse extracted files** — skip re-importing/re-slimming and reuse what's already in the resource folder. Tick it to
  keep a **hand-edited texture** across a re-bake, or to iterate on **Size/transform** fast. **Untick it** if you changed
  the Model file, the Clip, or the Animate-only-bones — otherwise the slim step is skipped and your change is ignored.

### Bake / Reset
- **Bake** runs the pipeline and writes the registry. **Reset** clears the form.

### Preview panel
An interactive 3D preview is embedded near the bottom of the window (drag to orbit, scroll to zoom). It **auto-updates
after every Bake** and when you pick a resource from the **3D resource** dropdown — so you see the baked result *in the
window*, no hunting in the Project view. It renders the baked prefab: `<name>_Preview.prefab` for **animated** models (a
static, textured, upright copy of the injected mesh — not itself injected) or `<name>_Model.prefab` for **static** models.
Use it to judge geometry, skin, and — with the **Reduce to ~tris** field + the Console vert/tri count — to dial in the
lowest triangle count with no visible loss: drop the count → Bake → watch the model degrade live, then step back up.

---

## 4. Static model workflow

1. Pawn description (Pick) + Resource name.
2. Model file (Browse).
3. **Textured model?** leave **Convert grid = 0** (preserves UV seams). Untextured CAD model? consider **Height-based
   UVs** or a **Convert grid > 0**.
4. **Renders invisible / see-through in-game?** it's a single-sided/CAD mesh — enable **Winding fix**, or **Double-sided**
   for non-convex shells.
5. **Heavy model?** set **Reduce to ~tris** (default 24000). Overflowing the shared buffer drops geometry silently.
6. Set **Size** / **Rotation** / **Position**. **Bake** → rebuild mod → relaunch. Tweak and re-bake (Model file empty) as
   needed.

---

## 5. Animated model workflow

Your model must be **rigged with a skeletal animation** (an armature + at least one clip). glTF/GLB is easiest (the clip
and bone pickers read it directly).

1. **Pawn description** — choose a donor with **no animated sub-parts** and a full idle/move set (a land vehicle is ideal;
   an attack-helicopter donor forces its rotor onto your model). Resource name.
2. **Model file** — the rigged `.glb`/`.fbx`. If detection finds animation, the **Animated** toggle enables and you'll see
   *"Animation detected"*.
3. Tick **Animated**.
4. **Clip name → Pick →** choose the loop you want (e.g. `hover`). Not the exploded/assembly clips.
5. **Animate only bones → Pick →** choose the spinning group (e.g. `prop`). This keeps only those bones animated and
   freezes the rest, killing camera/body wobble. Leave empty for a fully-animated model (a walker, a turret).
6. **Size** — the drone is small; try `4`. The Console logs the computed Scale Factor.
7. Make sure **Blender is detected** (Settings). **Bake.** Watch for:
   ```
   [Factory] <name> FBX scale factor … (native longest … -> <Size> units)
   [Factory] <name> ANIMATED DONE. skeleton=… clip=… atlas=…
   ```
   A **non-zero `clip=`** means the animation baked correctly.
8. **Rebuild the mod** (§6) and relaunch. The model should render and play its clip at real speed.

**Iterating on an animated model:** changing **Size/transform** only? leave **Reuse extracted** ticked (it skips the slow
re-slim). Changing the **Clip**, **Animate-only-bones**, or the **Model file**? **untick Reuse extracted** so it re-slims.

---

## 6. After baking: rebuild the mod (don't skip this)

Baking writes assets into the Unity project + the registry, but the game loads your mod's **built AssetBundle**. Your baked
skeleton / clip / atlas **won't reach the game until you rebuild/export the mod** (your normal Humankind mod build step).
Then relaunch. This is the #1 "why didn't my change show up" cause.

---

## 7. Presentation tips — make the unit read well

A few *game-data* touches (in `Databases`, not the Factory) that make an injected model land better on the battlefield.
These are `PresentationUnitDefinition` / `UnitDefinition` edits — independent of the model bake, and they need a **mod
rebuild**.

**Formation = a swarm (visibility + presence).** The unit's `PresentationUnitDefinition` → **Presentation Formation
Definition** controls how many *dummy figures* are drawn for the one unit. Switch a recon drone to `Formation_Wedge_3` and
it renders as **three drones in a wedge** — far more visible on the map, and it reads as *less fragile* (a coordinated
swarm vs a lone scout). It composes for free with an injected **animated** model: every formation dummy is the same pawn
descriptor, so the plugin's multi-instance handling gives each one our skeleton + clip — all of them render and spin.
Tune spacing with **Dummy Offset Position / Angle**.

**Purely visual — not stronger.** Formation dummies are cosmetic; they add zero HP or damage. It's still one unit (one
health bar, one combat roll; the figures move / fight / die together). If you want it to *be* as sturdy as it looks, edit
the gameplay stats in its **`LandUnitDefinition`** (combat strength / health / defense) — a separate `Databases` edit.

**Altitude (animated only).** The registry `position.z` raises an animated model at runtime (§5) — set it high enough
that the swarm clears tall city buildings and stays above the terrain. Combined, formation + altitude make the unit read
clearly at **every zoom**: close-up spinning props, mid-range city-skyline flyover, and an identifiable banner on the
strategic map.

---

## 8. Troubleshooting

| Symptom | Cause → fix |
|---|---|
| **Model invisible / see-through** | Single-sided/CAD mesh (backface-culled) → **Winding fix** or **Double-sided**. Or it overflowed the shared buffer → lower **Reduce to ~tris**. |
| **Model tiny (a speck) or huge** | **Size** is the world length — set it to what looks right; the Console logs the scale. |
| **ANIMATED model bakes huge & floats high in the sky** (fine in the Factory *preview*, wrong only in-game) | The rig's FBX embeds a metre→centimetre unit scale the SDK skeleton over-applies → ~100× oversize. **Tick "Fix 100× oversize (FBX unit scale)"** (Animation section) and re-bake at the real Size — the baker measures the FBX at true scale then bakes with the unit scale on, so Size = in-game units. It's a **per-model** toggle (no universal rule: some exports need it, some break with it). |
| **ANIMATED model vanishes / shrinks to a speck after ticking "Fix 100× oversize"** | That model's FBX does **not** carry the metre→cm scale, so the fix over-shrinks it. **Untick "Fix 100× oversize"** and re-bake — most rigs (e.g. the drone) bake correctly with it off. |
| **Model looks dark / grey / washed-out in-game** | Expected for skins that relied on PBR shine or a dark texture — the injection path ships flat albedo (donor PBR neutralized). Raise **Albedo brightness** and/or **Albedo saturation** and re-bake. Judge the amount in-game, not in the dim preview. |
| **A black part (glass canopy, cockpit) renders grey in-game** (multi-material model) | The near-black→grey neutralize step (which hides UV dead-zones) is flattening an intentionally black material. Tick **Keep black (glass/cockpit)** and re-bake. |
| **Change didn't show in-game** | You didn't **rebuild the mod** (§6). Or **Reuse extracted** was on and skipped the re-slim after you changed Clip/bones/model — untick it. |
| **Animated toggle greyed out** | The model has no animation the probe can see (OBJ, or a glTF with no `animations`). Use a rigged glb/fbx. FBX/.blend can't be probed cheaply, so the toggle stays enabled — type the clip/bones by hand. |
| **"No clips readable from this model"** | Clip/Bone Pick works for glTF/GLB only. For FBX/.blend, type the clip name and bone prefixes manually. |
| **Animated model plays the wrong motion** (parts assemble/explode) | You baked the wrong clip → set **Clip name** to the loop (e.g. `hover`), not `exploded_view`. |
| **"Fire on attack" clip loops constantly** (won't rest) | The **Fire on attack** toggle isn't set on this model → tick it and rebuild the mod. If it *is* set, confirm the firing unit matches (the plugin logs `[Fire] *** OUR MODEL '<name>' FIRED` on bombard) — only artillery/bombard units raise the event. |
| **"Fire on attack" model never moves when it fires** | The clip must **start and end at rest** (the single pass returns to frame 0). Re-check the rig's keyframes. Also confirm `[Fire] *** OUR MODEL … FIRED` appears in `BepInEx\LogOutput.log` when it bombards — no log = the unit didn't raise the artillery event (it's melee/air, not a bombard). |
| **Animated model tears apart / arms fly out** | The clip source wasn't isolated (an extra FBX in the folder → a multi-clip collection). Fixed in the current Factory (it bakes into a per-model `anim/` subfolder). Re-bake; if it persists, remove stray `.fbx` from the resource folder. |
| **~1s stall each loop** | A padded frozen tail in the clip. The Factory auto-clamps the frame range now — re-bake. |
| **Body wobbles / "unbalanced flywheel"** | The clip animates the whole body → set **Animate only bones** to just the spinning group (e.g. `prop`). |
| **A donor part shows through** (rotor, extra mesh) | **Hide donor meshes → Pick** it (after one launch so it's logged). If it's an *animated* donor sub-part, it can't be hidden — pick a cleaner donor. |
| **Model bobs / wiggles / wobbles** (a rigid model on a hovering donor — e.g. an airship on the Recon-Drone donor) | Your static mesh is inheriting the **donor's** idle/move animation. Tick **Freeze donor animation** (Runtime section) — the plugin pins the donor's pose so the mesh holds rigid, and it still glides tile-to-tile. Static models only; no re-bake. Different from the "unbalanced flywheel" row (that's a model's *own* clip animating its whole body — use **Animate only bones**). |
| **First unit's borrowed rotor sits ~1 low** (after a load, or on a freshly built/spawned unit; other instances fine) | An engine spawn race on the first pawn of the model, at creation. Tick **Re-spawn after load** on that model — the plugin rebuilds the unit's pawns right after it renders and the rotor comes out right (tune `Factory/RespawnDelayFrames` in the plugin cfg if it's briefly visible). Registry flag, no re-bake. |
| **Bake fails: "needs Blender"** | Install Blender or set its path in **Settings**. For static decimation without Blender, use **Convert grid** instead of Reduce-to-tris. |
| **Re-baked static model is 90° off / tipped up in-game** (preview looks fine) | An older Factory shipped a **stale skeleton** on re-bake (the static outputs were overwritten in place, so the skeleton baked from cached geometry). Fixed now — the static path deletes its outputs and force-reimports before baking the skeleton, so a re-bake matches a first bake. Just **re-bake → rebuild → relaunch**. |
| **Texture looks stale in the editor** | A Unity texture-residency quirk after a multi-material bake — open the source textures in the Project view and back. The in-game result is correct. |
| **Multi-material GLB comes out untextured / grey** | Fixed — `glbconv` now emits `usemtl` groups + a `.mtl` (and solid-colour swatches for flat parts), so a multi-material GLB atlases like FBX. **Re-bake** an older GLB model to pick this up (it was baked before the converter preserved materials). Keep **Convert grid = 0** (faithful mode; material grouping only runs there). |
| **Baked skin is flat / missing in-game but looks perfect in Blender** (no error) | The model UV-maps into a **non-[0,1] tile** (e.g. the whole hull sits in V 1→2) and leans on the shader's texture **wrap** to repeat the skin. Blender wraps, so it looks right there; the atlas baker packs each texture into a fixed rect and **can't wrap**, so out-of-range UVs sample *outside* the rect and the skin vanishes. Fixed — `glbconv` now **integer-shifts** each island's UVs back into [0,1] before the V-flip (integer shift, so it never tears tile-crossing triangles). **Re-bake** (Reuse extracted OFF) to pick it up. Diagnose by checking the extracted `FactorySource/<name>/<name>.obj` `vt` range — if U/V aren't within [0,1], that was it. Note: genuine *repeat*-tiling (a small texture meant to span [0,N] and repeat N×) still isn't atlas-supportable — none of the shipped models need it. |
| **A part is missing in-game but present in the Factory preview** (a mast, antenna — typically small parts, no error anywhere) | **Shared vertex-buffer overflow.** All injected models + the game's own fx meshes share one ~100k-vertex GPU buffer; overflow is **silently truncated from the tail of the last-registered model's mesh** — so late-order parts of your newest model vanish while its body renders fine. The preview is immune (it renders the mesh asset directly). Diagnosed live: a 48k-vert bake lost its rotor mast; visible again after **Reduce to ~tris** brought it down. Watch the bake log's `verts=` line — stay well under ~25k verts per model (UV-seam splitting means verts ≈ 2× tris on textured models, so a 12000-tri target ≈ 24k verts). |
| **Re-bake has no effect in-game** (still the old shape/orientation; preview shows the new one) | You re-baked but didn't **rebuild the mod**. A re-bake keeps the **same skeleton GUID**, so the registry doesn't change and the game silently keeps rendering the old geometry from the previously built bundle — no error, nothing to see in the log. Every geometry change needs bake → **rebuild mod** → relaunch. (Quick sanity test: bake with a wild rotation offset — if the game doesn't tilt, your build isn't reaching it.) |
| **Registry gone after a game reinstall / "verify files"** | Open the Factory window — it auto-restores from the git-tracked backup and writes it back to `BepInEx\config` (console: `restored N model(s) from the project backup`). See §10 "Registry safety net". |
| **Source folder created with the wrong case** (e.g. `attackHelicopter/` not `AttackHelicopter/`) | Cosmetic, bake-time only. Windows/Unity is case-insensitive + case-preserving, so the folder inherits the spelling of any pre-existing differently-cased asset of that name (e.g. a vanilla `attackHelicopter512.png`). Baked assets, registry, and in-game loading are all correctly cased. Use a non-colliding `resourceName` (e.g. `AH1Cobra`) if you want the folder capitalised. |

> **Diagnostic tip — trust the atlas, not the preview.** The Factory preview lights the mesh with a Standard (PBR) shader, so a smooth hull reads dark/glossy even when the baked skin is light and correct — don't judge textures from it. To see the *actual* baked skin, select the `<name>_Atlas.asset` in the Project view and run **Tools ▸ ENC ▸ Export selected atlas to PNG** (writes to `C:/tmp` and logs the average RGB). For UV problems, `awk` the extracted OBJ's `vt` lines for the U/V range. Both beat staring at the preview.

---

## 9. Where things land

- **Baked assets (shipped):** `Assets/Resources/<name>_Skeleton.asset`, `_Atlas.asset`, `_Mat.mat`, `_ModelMesh.asset`
  (static); animated adds `_Clips.asset` and a `<name>/anim/<name>_anim.fbx`.
- **Bake inputs (NOT shipped):** the imported model + extracted OBJ/albedo sit in `Assets/FactorySource/<name>/`, kept out
  of the built mod so licensed source models aren't redistributed. Safe to delete to reclaim space (a re-bake re-extracts).
- **Registry:** `<Humankind>\BepInEx\config\enc_models.json` — one entry per model (pawn description, `skel`/`atlas` GUIDs,
  transform, flags; animated adds `clip` + `animated`/`animClip`/`animateBones`).
- **Registry backup (versioned):** `Assets/Databases/enc_models.backup.json` — a git-tracked shadow copy, rewritten on
  every Save/bake. See "Registry safety net" below.
- **Runtime log:** `<Humankind>\BepInEx\LogOutput.log` — `[Uni] …` lines show what was injected (and the donor fragment
  names the Hide-donor Pick reads).

## 10. Registry safety net

The registry is the one Factory artifact that lives in the *game* folder, so it's the one a game reinstall or a Steam
"verify files" can wipe. Three layers protect it — all automatic, nothing to configure:

- **Atomic writes.** Every Save fills a `.tmp` file and swaps it in (`File.Replace`). An interrupted or locked write can
  never leave a truncated registry. If the swap itself fails (antivirus / indexer / the running game holding the file),
  the bake status says **"Baked, but REGISTRY SAVE FAILED"** — the asset is baked; close the lock and re-bake to write
  the entry.
- **Corrupt-file guard.** If the registry exists but won't parse (a hand-edit typo, a half-written file), it is copied
  aside to `enc_models.json.corrupt.json` and **Save refuses to run** until you fix or delete the original — so a bake
  can never overwrite your unreadable-but-recoverable registry with a fresh empty list.
- **Versioned backup + auto-restore.** Every Save also writes `Assets/Databases/enc_models.backup.json` inside the mod
  repo (git-tracked → full history, survives anything that happens to the game folder). If the game registry is
  **missing** — fresh install, verified files — the next `Load()` restores it from this backup and writes it back to
  `BepInEx\config` automatically. Just **opening the Factory window** triggers this (the console logs
  `restored N model(s) from the project backup`); no re-bake needed. The restore covers the registry only — BepInEx,
  the plugin DLL, and the built mod (which carries the baked assets) come from your normal install/build steps.
