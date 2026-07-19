# Universal Model Factory — User Manual

How to put your own 3D model onto a Humankind unit, step by step. This is the practical guide.

The Factory is a Unity editor window (**Tools ▸ Universal Model Factory**). You give it a model file and a target unit, set
a few options, press **Bake**, then rebuild the mod. The in-game plugin reads what you baked and renders it.

> **Shipping a standalone pack?** The runtime is a multi-mod host — you can distribute your models as their own **pack**
> (a config file + assets) that merges on top of ENC without editing it. See [**Multi-Mod.md**](Multi-Mod.md) for the pack
> format and the `haf_packs/` drop folder.

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

### Animation *(summary only — settings live in the Animation Lab, §15)*
Since 2026-07-18 the Factory no longer edits animation settings itself. The two windows are **mutually exclusive in
their settings and work together**:

- For an **animated entry** this section shows a **read-only summary** — e.g. *ANIMATED — clip 'deploy',
  fire-on-attack, deploy-on-stop (pose 0.72, speed 4), recoil (speed 3)* — plus an **Edit in Animation Lab** button
  that opens the Lab (docked as a tab next to the Factory) with this entry loaded. **Bake in either window** — both go
  through the identical pipeline, and a Factory bake uses the animation settings as saved by the Lab.
- For a **fresh rigged model** (animation detected in the file, nothing configured yet): a hint + **Open Animation
  Lab** button; configure the clip there and it bakes as ANIMATED from then on.
- **Safety nets** (born from the "howitzers on their side" incident, where a lost `animated` flag silently re-baked the
  howitzer static — tipped over, no behaviors): the flag is **self-healed** from the entry's own config (a named clip,
  behaviors, bone filter, or a baked clip GUID ⇒ the entry *is* animated, whatever the stored bool says), and baking a
  rigged model **without** any animation config asks for confirmation before producing a static bake.

### Transform
- **Rotation offset (XYZ)** — degrees. Static models bake this into the mesh. For **animated** models (2026-07-18)
  the contract is: **`0,0,0` = the exact legacy pipeline** (no rig manipulation whatsoever — a model that renders
  correctly stays correct on every re-bake); **any non-zero value = the rotation is baked into the rig** in the
  Blender step (rotate + `transform_apply` into vertices + bone rests, object-level anim fcurves stripped) — the fix
  for raw glTF rigs that round-trip lying down (the Sketchfab Combine soldier ships a -90°X armature node and needs
  `90, 0, 0`). Nominal semantics: x ≈ stand-up pitch, y ≈ heading, z ≈ roll — but the mapping crosses multiple axis
  conversions, so **probe one axis at a time in 90° steps and judge IN-GAME ONLY**: ⚠ the embedded preview's
  orientation is meaningless for animated models (fixed display flips; it happened to match the soldier and
  contradicted the howitzer — chasing it re-baked a working gun onto its side, twice). Rotation needs the **Model
  file set**; the Blender step re-runs **automatically** when any of its settings changed (see the Reuse note in §5).
  `deploy_convert.py`-prepared rigs (the howitzer) are correct at `0,0,0` — never give them a rotation.
- **Position offset (x, y, z = height)** — Static models bake it in (z = waterline; negative sinks a ship). For
  **animated** models it's applied at **runtime, in the pawn's own frame** (2026-07-18): x = sideways, y = fore/aft,
  z = altitude (world-up). The planar part is rotated by the unit's facing each frame, so the nudge **turns with the
  unit** — same semantic as the static bake. (Before this it was added on fixed world axes, so the offset drifted
  around the model as the unit turned.) Use y to push the model clear of its crew pawns — the clearance now holds in
  every facing; the *sign* of "forward" depends on the rig, so if +y moves it the wrong way, flip it. Runtime =
  change it, **Save (no bake)** in the Animation Lab (or re-bake here), and just **relaunch** — no mod rebuild.
  This also decouples altitude from Size (keep a small drone but fly it high).
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
- **Material mode** (Auto / Single / Multi) — how a model with **more than one material** is textured. **Auto** packs a
  multi-material atlas when the model has >1 material, else one texture (right for most). **Single** forces one texture —
  correct for CLOSED models (tanks, planes) sharing a skin. **Multi** forces the multi-material atlas — needed for **OPEN
  kit** (a towed gun's wheels/legs/barrel each on their own material) where otherwise every part samples the wrong region
  (e.g. the wheel comes out scrambled). Costs atlas space. *(Works for both static and ANIMATED models now — the animated
  path was single-material only before.)* If a dark part (rubber tyre) shows light patches, also tick **Keep black**. Re-bake to apply.
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
- **Keep extracted texture (hand-edits)** *(formerly "Reuse extracted files")* — its ONLY effect on the **animated**
  path: protect a hand-edited extracted albedo from being regenerated. Geometry re-processing is **automatic** there —
  the Blender step re-runs exactly when one of its inputs changed (Rotation, Reduce-to-tris, Clip, Animate-only-bones,
  Material mode, Model file) and is skipped otherwise, so your changes always take effect with no checkbox management
  (the old "ticked box silently ignores your Rotation change" trap is gone). For **static** models it additionally
  reuses the extracted OBJ (skip re-import, fast iteration), as before.

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

> **Raw / auto-rigged download (Sketchfab etc.)?** Read **§16 — Converting an animated model** first: it explains the
> engine's hard constraints (rotation-only clips, scale-1 skeletons, name-ordered bones), the automatic conversion the
> Factory performs, and the troubleshooting map for floating heads, smearing parts, and wrong orientations.

1. **In the Factory: Pawn description** — choose a donor with **no animated sub-parts** and a full idle/move set (a land
   vehicle is ideal; an attack-helicopter donor forces its rotor onto your model). Resource name.
2. **Model file** — the rigged `.glb`/`.fbx`. When detection finds animation you'll see *"Animation detected"* with an
   **Open Animation Lab** button — press it (the Lab opens docked next to the Factory, pre-filled with this model).
3. **In the Animation Lab: Clip name → Pick →** choose the loop you want (e.g. `hover`). Not the exploded/assembly clips.
4. **Animate only bones → Pick →** choose the spinning group (e.g. `prop`). This keeps only those bones animated and
   freezes the rest, killing camera/body wobble. Leave empty for a fully-animated model (a walker, a turret).
5. **Size** (back in the Factory — it owns the model's transform/size) — the drone is small; try `4`. The Console logs
   the computed Scale Factor.
6. Make sure **Blender is detected** (Settings). **Bake** (either window). Watch for:
   ```
   [Factory] <name> FBX scale factor … (native longest … -> <Size> units)
   [Factory] <name> ANIMATED DONE. skeleton=… clip=… atlas=…
   ```
   A **non-zero `clip=`** means the animation baked correctly.
7. **Rebuild the mod** (§6) and relaunch. The model should render and play its clip at real speed.

**Iterating on an animated model (2026-07-18 — geometry caching is AUTOMATIC):** the Blender re-slim runs exactly when
one of its inputs changed — **Rotation, Reduce-to-tris, Clip, Animate-only-bones, Material mode, or the Model file** —
and is skipped otherwise (fast). You never manage this. The checkbox (now labelled **"Keep extracted texture
(hand-edits)"**) has ONE job on the animated path: protect a hand-edited extracted albedo from being regenerated when
the re-slim runs. (Earlier today it also gated the geometry, which made Rotation silently unresponsive while ticked —
that trap is gone.) For static models it additionally reuses the extracted OBJ, as before.

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
| **Change didn't show in-game** | You didn't **rebuild the mod** (§6) — baked assets only reach the game through the bundle; a registry-only change (runtime flags) needs just a relaunch. (Since 2026-07-18 the re-slim runs automatically when a Blender-step setting changed — the "checkbox swallowed my change" failure mode no longer exists.) |
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
| **Baked skin is flat / missing in-game but looks perfect in Blender** (no error) | The model UV-maps into a **non-[0,1] tile** (e.g. the whole hull sits in V 1→2) and leans on the shader's texture **wrap** to repeat the skin. Blender wraps, so it looks right there; the atlas baker packs each texture into a fixed rect and **can't wrap**, so out-of-range UVs sample *outside* the rect and the skin vanishes. Fixed — `glbconv` now **integer-shifts** each island's UVs back into [0,1] before the V-flip (integer shift, so it never tears tile-crossing triangles). **Re-bake** with **"Keep extracted texture" unticked** (static path: that also re-extracts the OBJ) to pick it up. Diagnose by checking the extracted `FactorySource/<name>/<name>.obj` `vt` range — if U/V aren't within [0,1], that was it. Note: genuine *repeat*-tiling (a small texture meant to span [0,N] and repeat N×) still isn't atlas-supportable — none of the shipped models need it. |
| **Multi-material model bakes all-grey (at 512) or near-black (Keep black on)** — camo/markings gone (no error) | Same non-[0,1] UV cause as the row above, but the materials each sit in a *different* tile, so `glbconv`'s single **global** shift can't gather them. The atlas remapper now also **folds per-vertex** (`u -= floor(u)`) when placing each sub-mesh into its rect, which does cover the per-material case. **Re-bake** to pick it up. (If you saw *grey* it was the near-black→grey neutralize masking the miss; *black* is the raw miss with **Keep black** on.) Proven on the AH-1 Cobra (51 materials, U 0→23). Diagnose per-material with `awk` grouping the OBJ's `vt` by `usemtl`. Once mapped correctly, remaining softness is just **Atlas size** — bump 512→1024/2048 for crisp markings. |
| **Bake FAILED: `IndexOutOfRangeException` in `MeshCollection.ImportMeshes`** (any animated model) | The animated **skeleton bake** (`Skeleton.Reimport`) reads **tangents** off the skinned mesh; with none, Amplitude indexes an empty tangent array and throws. Fixed: the animated path **always keeps tangents** (`importTangents = CalculateMikk`), regardless of material count — the tangent-strip size optimization is **static-path-only**. (It bit twice: the multi-material howitzer, then the single-material drone.) The baker now also dumps a `SKMESH … bones=/bindposes=/maxBoneIdxUsed=/tangents=` line before the bake and flags a bone-index mismatch, so the opaque crash becomes a readable cause. Related: the **weld pre-pass** is single-material **and single-bone** only (welding across bone-part seams corrupts skinning). If you hit this after a baker change, check the `SKMESH` line — `tangents=0` on an animated model is the tell. |
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

## 11. Regression guards (run before committing baker changes)

Bakes are manual and the roster is growing, so a baker change can silently break a model you don't happen to re-bake
until much later. Three guards catch that at the integration seam unit tests can't reach — run them after any change to the
baker, `rig_anim.py`, `glbconv`, or the registry schema.

- **Bake Smoke Test** — `Tools ▸ ENC ▸ Tests ▸ Bake Smoke Test (one per path)` (or `(ALL models)`). Bakes one representative per
  bake-path (`animated × material mode`) through the *same* config route as the Bake button and asserts each completes
  without throwing and produces non-empty `_Skeleton`/`_Atlas` (+ `_ModelMesh` for static). **Non-destructive**: it bakes
  `reuseExtracted=false` models under a throwaway `__smoketest__` name (your real assets + registry are untouched) and
  validates existing assets for `reuseExtracted=true` models (forcing a fresh extraction you never run gives false
  failures). It's SLOW (real Blender bakes) — a pre-commit check, not an every-save one. *This is not theoretical:* it
  caught a same-day tangent-strip regression that had broken every animated bake. **Known fidelity limit:** a throwaway
  bake can't regenerate an *animated multi-material* model's per-material albedos (they're keyed to the real name), so the
  howitzer's throwaway bake exercises its skeleton path, not its texture packing — the multi-material atlas code is
  covered instead by the *static* multi-material AttackHelicopter, whose albedos `glbconv` does regenerate. Texture-only
  entries (Unit Retexture `Retex_*`: no model file, nothing to bake by design) report `SKIP`, not a failure.
- **Bake Feature Test** — `Tools ▸ ENC ▸ Tests ▸ Bake Feature Test` (**Tier 1**) and `… (Tier 2 — Blender + animated)`. Complements
  the smoke test: where that proves models *bake*, this proves each baker *feature knob* does what it claims, by baking a
  fixture with one knob toggled at a time and asserting a feature-specific invariant on the baked mesh/atlas.
  **Tier 1** (fast, self-contained synthetic cube): `doubleSided` doubles the triangle count, Faceted unwelds
  (`vertexCount == triangles.Length`), `heightUV` maps V to height, `atlasMaxDim` caps the atlas, `size`/`positionOffset`
  land where configured, `albedoBrightness`/`albedoSaturation` change the atlas (best-effort pixel read — SKIP if the DXT1
  atlas isn't CPU-readable). **Tier 2** (slower, real Blender): `targetTris` decimates a *generated* high-poly grid,
  `stripParts` drops a *generated* named object, and the animated pipeline (`BuildAnimated` → `_Skeleton` + `_Clips`) is
  exercised by borrowing up to two rigged models from the registry (SKIP if none on disk — a rigged FBX can't be
  synthesized); since 2026-07-19 those fixtures clone the registry entry and route through `ConfigFor`, so
  `convertRig`/rotation carry and each model is tested on the pipeline it actually ships on. Both non-destructive
  (throwaway `__feat_*` names, cleaned up). Benign console noise during Tier 2: `ImportFBX Warnings: Can't import
  normals, because mesh 'default' doesn't have any` — the synthetic OBJ fixtures carry no normals and Unity
  recalculates them; shading is irrelevant to what these fixtures assert.
- **Conversion Gate Test** — `Tools ▸ ENC ▸ Tests ▸ Bake Conversion Gate Test (litmus)` and `… (registry converted models)`
  (2026-07-19). Asserts the raw-rig CONVERSION invariants the animated runtime silently requires — each was once
  violated and each cost hours of blind in-game debugging (the Combine-soldier campaign): every baked bone's
  **BindPose/Local scale == 1** (the file-scale sandwich), every bone's **ParentIndex < its own index**
  (alphabetical-sort/topology contract), every clip curve entry **rotation-only**, curve-entry count **== bone count**
  (the runtime addresses `animId + boneIndex`), and a real **FrameCount**. Two fixtures: the deterministic **litmus
  rig** (`Tools/make_litmus.py`, synthesized via Blender on demand — rigged fixtures CAN be synthesized now), and
  **the real converted rigs from the registry** (the Combine soldier's location-keyed ValveBiped: 62 bones, 342
  frames, the full rest-normalization — the strongest net; needs the source model files on disk, skips with a
  warning otherwise). Both bake under throwaway `__convgate__` names through the same `ConfigFor` route as the Bake
  button, then clean up. Slow (real Blender bakes) — run after touching `rig_anim.py` or `UniversalBaker`.
  Also since 2026-07-19: the **Smoke Test** selects an extra representative per animated path (legacy vs conversion,
  keyed on the `convertRig` flag since the 2026-07-18 gate refactor — they're different pipelines; tags
  `animated-legacy` / `animated-conv`) and requires the **`_Clips` asset + `_ClipsPoseData.bytes`** for animated
  models (an empty-clip bake used to pass silently). The full suite re-verified green after the gate refactor:
  smoke 14/14 (soldier fresh-baked as `animated-conv` via the flag at Rotation `0,0,0`; the legacy drone + howitzer
  byte-identical with the flag off) and ConvGate full-conversion PASS on the real soldier rig.
- **Schema parity** — `bash Tools/check_schema_parity.sh`. The registry is written by the baker (`ModelDef`, JsonUtility)
  and read by the plugin two ways (`ModelEntry` via Newtonsoft, plus a regex fallback) across two separate repos, kept in
  sync by hand. The guard makes drift loud: it asserts (1) the Newtonsoft and regex read paths read the **same** key set,
  (2) every read key is a field the baker writes (plugin ⊆ ModelDef, with an allowlist for deliberate plugin-only overrides
  like `scale`), and (3) each read cast's type matches ModelDef's declared type; bake-time-only fields are listed as INFO.
  Catches a silent rename/drop/type-change that would otherwise make a feature quietly default-off.

## 12. Texture-only reskins — the Unit Retexture window (no bake)

Sometimes the vanilla model is fine and only the *skin* is wrong — a Common copy that should look distinct from its
emblematic original, a colour test, a themed variant. For that there's a separate window — **Tools ▸ ENC ▸ Unit
Retexture** — that reskins an existing unit **without baking a model**: the vanilla mesh is kept, and the runtime plugin
paints your texture onto an **isolated clone** of the unit's output layer, so the original unit (and every other unit
sharing that layer) is untouched.

Everything is a plain registry entry (no assets, no mod rebuild). Section 3 of the window is **Replace / adjust skin**:

- **Replacement PNG** *(optional)* — `textureFile`: a PNG filename in `BepInEx\config\enc_skins\`. The plugin hot-loads
  it at runtime. Leave it empty to adjust the unit's OWN atlas (or, when editing an entry, to keep its current skin).
- **Adjustments** (applied on top of whichever skin above — the PNG *or* the own atlas):
  - **Desaturate** (0–1) — pull each pixel toward its brightness; 1 = full grey. Also neutralises the civ-colour tint.
  - **Red / Green / Blue** (−255…+255 each) — additive per-channel colour offset. Equal negatives = darken, equal
    positives = brighten, one channel = tint (e.g. "Desaturate 1 + Blue +40" = a cool steel-grey). All 0 = no change.

Proven on the grey corvette. (These sliders replace the earlier single grey/darken toggle.)

Workflow:

1. **Pawn** — pick the pawn descriptor (use the `_Common_..._01` copy, **not** the emblematic original — the isolation
   clone is what leaves the original untouched; they share an output layer). The entry name defaults to `Retex_<pawn>`.
2. **Download the skin to paint** — dump the unit atlases in-game first (**F8 window ▸ Dump Atlases**; files land in
   `BepInEx\config\enc_atlas_dump\`), then paint over the unit's PNG in any editor. It's the unit's real UV layout, so
   what you paint is what wraps.
3. **Replace** — point the window at your painted PNG and Apply: it copies the PNG into `config\enc_skins\` and writes
   the registry entry (or tick **Grey** for the desaturate mode). Relaunch/reload the game to see it.

Because the mesh is untouched this can't fix silhouettes — it's for palettes, markings, camo. And since the plugin
hot-loads from the game's config folder, iterating is repaint → overwrite the PNG → reload; no editor round-trip at all.

**Cost — essentially free on the vertex budget.** A reskin keeps the unit's existing mesh, so it adds **no vertices,
indices, or meshes** to the GPU pawn-layer buffer — that buffer is budgeted per distinct mesh *type* (instances are free;
see `Vertex-Budget.md`), and a reskin introduces no new type. It costs only an output-layer clone (a render slot) and one
texture. So unlike a **baked custom model** — which *does* add its own distinct mesh's verts — you can stack many reskinned
variants without approaching the mesh ceiling. (Applying this to *custom* models — one baked mesh, many textured variants
sharing it — is a natural extension: the plugin already dedups a shared skeleton's mesh on upload, but reusing one custom
skeleton across *different* base units still needs work on the per-donor rename; not built yet.)

## 13. Unit sounds — engine audio & the sound catalog

Injected/retextured units are **silent on move** by default: the per-ship engine sound (`Play_UNIT_Vehicles_<Type>_Start`
/`_Stop`) rides an audio-service path tied to the vanilla unit's move state, which our re-loaded units never trigger. (Full
diagnosis in the `unit-movement-audio-investigation` memory — the emitter, its Wwise registration and its 3D position are
all fine; only the *trigger* is missing.) The plugin restores the sound by firing it itself.

**Enable it** — tick **Engine sound on move** in the Unit Retexture window (or set `engineSound: true` in the registry).
The plugin then watches each of that unit's instances and, on a movement **start/stop** transition (render-position delta,
like deploy-on-stop), posts the engine event onto the pawn's `AudioEmitter`: a rev on departure, a settle on stop.

**Name the sound (works for the FIRST unit, no capture):** fill **Start event** / **Stop event** with Wwise event names.
The plugin posts them **by name** (`AkSoundEngine.PostEvent(name, emitterGuid)`), so a named sound plays for the very first
unit at load — no dependency on anything else having moved. Leave them blank and the plugin falls back to a handle
**auto-captured** from any same-family vehicle that moved this session (fine mid-game, but the first unit stays quiet until
then) — so **name the events for a shipping mod.**

**Extract every sound — the catalog:** F8 window ▸ **Dump Sound Catalog** writes every Wwise event name in the game (~800+)
to `BepInEx\config\enc_sound_catalog.txt`. Browse it, pick the right Start/Stop pair, paste them in. Examples:

| Unit family | Start event | Stop event |
|---|---|---|
| Modern warship (corvette, destroyer, stealth) | `Play_UNIT_Vehicles_StealthCorvette_Start` | `Play_UNIT_Vehicles_StealthCorvette_Stop` |
| Aircraft carrier | `Play_UNIT_Vehicles_AircraftCarrier_Start` | `Play_UNIT_Vehicles_AircraftCarrier_Stop` |
| Steam-era frigate | `Play_UNIT_Vehicles_SteamFrigate_Start` | `Play_UNIT_Vehicles_SteamFrigate_Stop` |
| Submarine | `Play_UNIT_Vehicles_Submarine_Start_Modern` (or `_Old`) | `Play_UNIT_Vehicles_Submarine_Stop_Modern` (or `_Old`) |
| Landing craft / hovercraft | `Play_UNIT_LandingCraft_Start` | `Play_UNIT_LandingCraft_Stop` |
| Helicopter | `Play_UNIT_Helicopter_Move` | `Play_UNIT_Helicopter_Stop` |
| Towed howitzer | `Play_UNIT_CanonObusier_Move_Start` | `Play_UNIT_CanonObusier_Move_Stop` |
| Wheeled gun / mortar | `Play_UNIT_Vehicles_Mortar_Move_Start` | `Play_UNIT_Vehicles_Mortar_Move_Stop` |
| AT gun | `Play_UNIT_AntiTankGun_Move_Start` | `Play_UNIT_AntiTankGun_Move_Stop` |

**Air units** (planes, zeppelins, drones) have **no** engine-loop event in the game — only takeoff/shoot — so they can't be given a continuous move sound this way; leave `engineSound` off for them.

It posts *any* named event, so this isn't limited to engines — attach any sound in the catalog to a unit's movement. (In the ENC mod, all the naval/ground/heli units are pre-configured with the events above; the three air units are left silent.)

**Audio diagnostics (F8 window), kept for future sound work:** **Dump Audio** logs each unit's emitter (registration,
position, idle/free-event state); **Audio Trace** toggles a live log of every sound the game posts (how the event names
were discovered — it patches the service sink `AudioManager.PostEvent`); **Play Audio (test)** posts the captured engine
handle onto the filtered units to confirm audibility.

**Limits (honest):** the Wwise-event path fires Start/Stop *accents* only (no sustained loop between) — for a continuous
engine/hover loop, use the **custom WAV** path (§14). The auto-capture fallback uses the last-seen vehicle Start, so a
**named** event is preferred; a shipped registry with names Just Works from the first unit, every launch.

## 14. Custom sound files & per-clip volume — the Unit Sound window

When the game has **no** suitable sound (drones, zeppelins) or you want a bespoke engine, drop in your own audio. **Tools ▸
ENC ▸ Unit Sound** is a dedicated dialog: pick a pawn, then assign up to three WAVs, each with its own volume —

- **Start** — a one-shot **spool-up** played on move-begin.
- **Travel** — **looped** while the unit moves (held off until the Start one-shot finishes, so it isn't masked).
- **Stop** — a one-shot **spool-down** played on move-end.

These play through Unity's own `AudioSource` (not Wwise), so **any** WAV works — no soundbank needed. Requirements: **16-bit
PCM WAV** (convert mp3/ogg first); **mono** for true 3-D positioning. Files are copied into `BepInEx\config\enc_sounds\` and
referenced by the registry (`soundStartFile`/`soundFile`/`soundStopFile` + `soundStartVolume`/`soundVolume`/`soundStopVolume`).
It writes onto the unit's *existing* registry entry, so a unit still has one entry. (A `Wwise engine event` option in the
same window covers the §13 game-sound path.)

**Volume is perceptual in the window** — the slider tracks *perceived loudness* (√ curve) and the `×N` label shows the real
linear amplitude stored (hearing is logarithmic, so e.g. slider 0.4 ≈ amplitude 0.16). **Seamless loops:** if a raw Travel
clip clicks at its wrap, crossfade a copy first (blend ~0.1–0.3 s of the tail into the head).

**Audition in the editor:** each clip row has a **▶** preview button that plays it at its set volume (with **■ Stop**), and
**Edit** on a unit in the "Units with audio" list loads its current Start/Travel/Stop files + volumes — so you can pick and
tune a sound without launching the game.

**Performance note:** the runtime audio driver polls only *our* units' sub-pawns and refreshes its scene lookup on a ~2 s
cache — never a per-frame `FindObjectsOfType` (an early version did, and it visibly cut FPS). If you extend it, keep any
full scene scan off the hot path.

---

## 15. The Animation Lab window — a model's animation, in one place

**Tools ▸ ENC ▸ Animation Lab** (2026-07-18). Docks as a **tab next to the Universal Model Factory**, so the pair
presents as one tabbed dialog. The design rule: **the Factory owns the MODEL** (identity, pawn, model file, transform,
size, geometry/shading, static runtime flags), **the Lab owns the ANIMATION** — every setting lives in exactly one of
the two windows, and jump buttons hand context across ("Edit in Animation Lab" in the Factory loads the entry here).
Both windows Bake through the identical pipeline (`ConfigFor → UniversalBaker.BuildAnimated → ModelRegistry.Upsert`),
so it does not matter where you press Bake.

**Edit existing** lists the **animated** entries only. The model identity (Resource / Target pawn / Model file) shows
**read-only** — change those in the Factory. Survives domain reloads (`[SerializeField]`, like the other Labs).

### Clip (bake-time — changing these needs a re-Bake)
- **Clip name** *(Pick)* — which clip to bake when the model has several (a Sketchfab model often ships `hover`,
  `exploded_view`, …). **Pick** lists the clips read from the model (glb/gltf). Empty = the model's first/assigned clip.
- **Animate only bones** *(Pick)* — comma-separated bone-name **prefixes** to keep animation on (e.g. `prop`). Strips
  everything else (camera pans, body bob) that would make the model wobble. Empty = keep the whole clip.
- **Fix 100× oversize (FBX unit scale)** — some rigged exports embed a metre→centimetre unit scale that makes the model
  bake ~100× too big and float high. Per-model: huge/floating → tick; vanishes when ticked → untick. (Drone off,
  howitzer on.) Auto-prefilled on the Factory's Browse for GLB/glTF.
- **Convert raw rig (auto-rigged models)** — the explicit PIPELINE switch (registry `convertRig`). ON = the raw-rig
  conversion of §16 (rest-normalize + visual rebake, root collapse, topological rename, clean-unit export — what made
  the Combine soldier work; usually paired with Fix-100× OFF). OFF = the byte-identical legacy pipeline for
  purpose-made rigs (drone, howitzer) — with it off, Rotation and every other setting bake exactly as they always
  did, so re-baking a working legacy model is safe. Old registries that used the rotation-triggered conversion
  (the `360,0,0` identity trick) migrate to the flag automatically on load.

### Behavior (runtime — **Save (no bake)** + game relaunch applies them, no re-bake, no mod rebuild)
- **Fire on attack (play once)** — play the baked clip **once when the unit attacks** instead of looping; rests at
  frame 0 otherwise (a howitzer barrel that elevates only when it bombards). Author the clip to start *and* end at
  rest. Off = continuous loop (a drone's spinning prop). See [Firing-On-Attack.md](Firing-On-Attack.md).
- **Deploy when stopped** *(+ **Deployed pose time**, **Deploy speed**)* — play the clip forward when the unit stops
  (spread the trail legs) and snap folded the instant it moves. Frame 0 = travelling; the deployed pose sits at
  Deployed pose time (0..1, keep ≤ 0.99 — exactly 1.0 wraps to frame 0). Deploy speed multiplies the ramp; folding is
  always instant. Held per-unit, driven by the unit's **render-position change** (settle-immune).
- **Recoil speed** *(needs both of the above)* — the deployed gun also kicks when it bombards, from the same clip's
  tail `[Deployed pose time .. 1]`; plays once per shot, only on the gun that fired. The recoil's *shape* is baked into
  the GLB by `Tools/deploy_convert.py` (run by hand before baking — see its full usage in §5's source-prep notes);
  this slider only scales playback speed.

### Buttons
- **Bake** — re-run the animated pipeline (Blender slim → skeleton + clip + atlas) with the settings above, then write
  the registry entry.
- **Save (no bake)** — write the registry entry only (assets untouched): the way to tweak Behavior flags/sliders.
  Relaunch the game to see it; the mod bundle is unchanged.
- **Remove** — drop the entry from the registry (baked assets stay on disk).

### Safety nets
The `animated` flag is **derived, not trusted**: an entry carrying animation config (named clip, behaviors, bone
filter, or a baked clip GUID) is re-marked ANIMATED automatically on load and before every bake. This ended the
"howitzers on their side" failure mode — a stale unticked checkbox once re-baked the howitzer STATIC, which strips the
clip + behaviors and bakes the (animated-path-ignored) Rotation offset into the mesh, shipping tipped-over guns. Baking
a rigged model that truly has no animation config still asks for confirmation first.

### Cross-window safety & the facing mechanism (2026-07-18)

**Enforced field ownership.** Bake/Save from EITHER window now rebases on the freshest registry entry and contributes
only the fields that window owns (Factory: model/transform/size/…; Lab: clip/bones/Fix-100×/behaviors). Before this,
whichever window held a stale copy silently clobbered the other's values at bake time — it cost three bakes on the
Combine soldier (a Factory bake dropped the Lab's Fix-100× → 100× giant; the Lab's stale copy then dropped rotation
AND size). You can now edit in one window and bake from the other safely.

**How the game turns pawns (found via the fixed-compass soldier).** The engine orients a pawn's visual through the
procedural **BoneRotation layer** (`PawnEntry.BoneRotation0-3`), not only `ObjectSpace.Rotation`. The plugin used to
zero that layer on every animated model (added so artillery aiming couldn't twist the howitzer's barrel) — which
pinned the soldier to one compass facing forever. It now clears the layer **only for models with the artillery
behaviors** (fire-on-attack / deploy-on-stop); everything else keeps the game's layers and turns normally.

**The raw-rig conversion (2026-07-18/19 — how the soldier's "torn head" was actually solved).** A Sketchfab/auto-rigged
model can ship a **scrambled rest pose that the clip's location keys ASSEMBLE into the body every frame** (the Combine
soldier's frame-0 posed bones sat up to 91 units from their rests on a 73-unit rig — 129 location curves were
structural, not decorative). Amplitude plays rotation-only clips, so such a rig can never work as-is. `rig_anim.py`
now performs, on the conversion path: **(a) REST NORMALIZATION** — snapshot every bone's visual matrix on every
frame, apply the armature modifier at frame 0 (the assembled body becomes the bind mesh), Apply-Pose-As-Rest (the
assembled pose becomes the rest), re-bind, then **re-derive the whole clip as pure rotations** against the new rest
(verified in-bake: `frame-0 residual = 0.000167`); **(b)** strip residual location curves; **(c)** collapse no-op root
bones; **(d)** topological bone rename (Amplitude sorts alphabetically, parents must precede children); **(e)** fold
rotation+scale into the data and export unit-clean (`global_scale=0.01`, no ×100 root `Lcl Scaling`, skeleton bakes
all-Scale-1). Verified end-to-end with a **litmus rig** (`Tools/make_litmus.py`: a 12-deep bone chain of colored
cubes — renders straight in-game, exonerating the runtime for clean rigs). Raw-FBX inspectors used for the diagnosis:
`Tools/fbx_binddump.py`, `Tools/fbx_lclscale.py`. **Gate refactor (2026-07-18):** the conversion is now selected by
the explicit **"Convert raw rig"** checkbox in the Animation Lab (registry field `convertRig`) — it originally
triggered on a non-zero Rotation, which made the Rotation field a hidden pipeline switch (the soldier shipped with a
`360,0,0` identity trick; a rotation edit on a legacy model silently rerouted its bake). Rotation is just a rotation
again (applied only on the conversion path); flag OFF remains the byte-identical legacy pipeline, and old registries
migrate automatically on load.

**RESOLVED with the clean rig (2026-07-19):** the "drone projectile invisible on attack" symptom disappeared once the
soldier's rig was properly converted — the corrupted skeleton state was evidently disrupting the attack presentation
too (mechanism not separately traced; if it regresses, the designed fix was a Pose0 pass-through window during the
attack). The temporary `[Uni][facing]` periodic log can be removed on the next plugin change.

---

## 16. Converting an animated model — from raw rig to Amplitude-ready

> **New here?** Read [**Animated-Models.md**](Animated-Models.md) first — the plain-language "what kinds of animated
> models can HAF import" page (three levels, what each needs, current limits). This section is the deep technical
> treatment behind it.

Everything learned getting the first humanoid (a raw Sketchfab ValveBiped) fully working in-game, distilled into the
practical guide. The Factory does the conversion **automatically** — this section explains what it does, how to drive
it, and how to read the safeguards. *(For the other half — how the game consumes the baked result every frame — see
[**Animated-Runtime.md**](Animated-Runtime.md).)*

### 16.1 What Amplitude's animation system can actually play

These are hard engine constraints (established by decompiling the bake — `ClipEntry.Reimport`,
`Skeleton.Reimport` — and the runtime — `AnimationManager.GetBoneTRS/ApplyPose/GetPoseTRS`):

| Constraint | Consequence for your model |
|---|---|
| **Clips are rotation-only** (translations are dropped or mis-scaled) | Any rig whose animation *moves* bones (location keys) must be converted |
| **Uniform scale, and effectively scale 1** | The skeleton must bake with every `BindPose`/`Local` scale = 1 — no unit-conversion "sandwiches" on nodes |
| **Bones are sorted ALPHABETICALLY, parents must sort before children** | Rigs whose names don't sort topologically (e.g. `_014` < `_02`) get scrambled chains |
| **Bounded chain depth** (CPU cap 15) | Pass-through root bones waste depth budget |
| **One clip per ClipCollection folder** | The bake isolates your FBX in `anim/` automatically |

Amplitude's own art pipeline satisfies all of these silently, by convention. Raw downloaded rigs usually violate
several at once — and the failures MASK each other, which is what makes hand-debugging brutal (it took five stacked
defects to get one soldier standing).

### 16.2 The three kinds of source rig

1. **Clean, authored rig** (our drone; anything from `deploy_convert.py`) — rotation-driven animation, sane rest
   pose. Bake with **"Convert raw rig" OFF** = the untouched legacy path. If it works, never touch it.
2. **Clean rig, wrong orientation** — rotation-driven but bakes lying down/facing wrong. Tick **"Convert raw rig"**
   and set a **Rotation** (probe one axis at a time in 90° steps, judge IN-GAME) — rotation is only applied on the
   conversion path.
3. **Auto-rigged / "assembled-by-animation" rig** (the Combine soldier; typical of Sketchfab auto-rigs) — the REST
   POSE IS NOT THE BODY: the clip's location keys assemble the model every frame. Diagnostic tell: lots of
   `pose.bones[...].location` curves, and body parts that float/detach in-game while Unity's own preview plays the
   FBX perfectly (Unity supports location keys; Amplitude doesn't). These NEED the conversion path — tick
   **"Convert raw rig"** in the Animation Lab (add a Rotation only if the converted bake comes out mis-oriented).

### 16.3 What the conversion path does (automatic, in this order)

Everything below runs inside the Bake's Blender step (`Tools/rig_anim.py`) whenever **"Convert raw rig"** is ticked
in the Animation Lab:

1. **Rest normalization + visual rebake** *(the key step for type-3 rigs; conversion-path ONLY since 2026-07-19)* —
   snapshots every bone's visual matrix on every frame, applies the armature modifier at frame 0 (the assembled body
   becomes the bind mesh), applies the frame-0 pose as the new rest, re-binds, then re-derives the ENTIRE clip as
   pure rotations against the new rest. Console proof: `RIGANIM rest-normalized + rebaked N frames x M bones ...
   frame-0 residual = 0.0001…` — the residual must be ~0. (It used to trigger on location-key *presence* even with
   the flag off, quietly reaching into legacy re-bakes of `deploy_convert.py` outputs; now the flag alone decides.)
2. **Location-curve strip** — whatever translations remain are removed (Amplitude can't play them anyway).
   **Runs on BOTH paths, deliberately**: every verified legacy bake (drone, howitzer) went through it, and
   un-stripping could re-introduce the drone's unscaled-translation wobble — so "legacy byte-identical" means
   *no rig manipulation* (no fold / rename / collapse / scale-fold), with this strip as the one shared step.
3. **No-op root collapse** — unanimated, unweighted, single-child root bones (`_rootJoint`) are deleted for depth.
4. **Topological bone rename** — every bone prefixed `b###_` in hierarchy order, so alphabetical = topological.
5. **Rotation + scale folded into the data** — your Rotation value and all node scales are baked into vertices +
   bone rests; nodes export as identity.
6. **Unit-clean export** — `global_scale=0.01` cancels the FBX exporter's m→cm ×100 root scaling, so the file carries
   no scale annotations (`UnitScaleFactor 1.0`, all bind clusters 1.0).

The Unity side then imports with `useFileScale` off (non-Fix-100× path), so the baked skeleton reads **`Scale: 1` on
every bone** — verify with a text editor on `Assets/Resources/<name>_Skeleton.asset` if in doubt.

### 16.4 The workflow, start to finish

1. **Factory**: pick the target pawn, Browse the model, set Size. **Animation Lab** (auto-opens via the jump button):
   pick the Clip. Bake once with "Convert raw rig" OFF — maybe you have a type-1 rig and you're done.
2. Rebuild the mod, look in-game (**the game is the only honest judge — the previews' orientation is meaningless for
   animated models**).
3. Wrong orientation or floating parts → tick **"Convert raw rig"** (+ a Rotation probe if it's an orientation
   problem: start `90,0,0`, one axis at a time) → Bake → check the console for the conversion lines and the
   **residual ≈ 0** → rebuild → judge in-game. Two or three probes typically suffice.
4. Watch the Console safeguards on every bake: the `RIGANIM` lines tell you exactly which steps ran, and the bake
   fails loudly rather than shipping a broken skeleton.

### 16.5 Troubleshooting map (symptoms → cause)

| In-game symptom | Cause | Fix |
|---|---|---|
| Model lies down / faces wrong, plays fine otherwise | Orientation (type 2) | Tick "Convert raw rig" + Rotation probe, one axis at a time |
| A body part (head, hands) floats rigidly detached; Unity preview plays the FBX fine | Type-3 rig: structural location keys, scrambled rest | The conversion path (tick "Convert raw rig") does it all |
| Parts smear/stretch, "movement looks exaggerated" | Translation keys playing unscaled | Same — conversion path |
| Whole model 100× too big | FBX unit scale | Fix-100× toggle (per-model; the conversion path usually makes it unnecessary) |
| Deep chains (fingers, head) scrambled, shallow parts fine | Bone-name sort order | Automatic (topological rename) on the conversion path |
| Model won't turn with movement | The plugin was clearing the game's facing layer | Fixed globally — cleared only for artillery models now |
| "Is it my model or the pipeline?" | — | Bake `Tools/make_litmus.py`'s chain-of-cubes rig; if it renders straight in-game, the pipeline is fine — it's the model |

### 16.6 Inspection tools (when you need to see for yourself)

- `Tools/fbx_binddump.py` — raw-parses an FBX: unit scale factor + every bind cluster's scale.
- `Tools/fbx_lclscale.py` — raw-parses an FBX: non-identity node scales (the ×100 root tell).
- `Tools/make_litmus.py` — generates the diagnostic chain rig.
- The baked assets are plain YAML: grep `<name>_Skeleton.asset` for `Scale:` values and parent ordering,
  `<name>_Clips.asset` for per-bone `EncodingFormat` (1 = rotation-only, the target).

Run any of them with `blender -b --python <script> -- <args>`.
