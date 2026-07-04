# Universal Model Factory — User Manual

How to put your own 3D model onto a Humankind unit, step by step. This is the practical guide; for the *why* and the
engine internals see [Scaling-ManyModels-And-Scoping.md](Scaling-ManyModels-And-Scoping.md).

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
- **Reduce to ~tris (0 = off)** — quadric-decimate a heavy model to about this many triangles (via Blender) to fit the
  engine's shared mesh buffer (~25k per model is the practical ceiling). It's a **ceiling, not a quota**: a model already
  under it passes through untouched. No Blender? Use **Convert grid** instead (below).
- **Hide donor meshes** *(Pick)* — comma-separated name substrings of the **donor unit's extra parts** to hide (e.g. a
  leftover rotor). **Pick** reads the donor fragment names the plugin logged to `BepInEx\LogOutput.log` — so **launch the
  game once** with the model injected, then Pick. Runtime-only (takes effect on reload, no re-bake). *Can't* hide a
  donor's animated sub-parts (a rotor, spinning wheels) — those are baked at pawn spawn; pick a cleaner donor instead.
- **Convert grid** — GLB/glTF only. `0` = faithful (keeps UV seams — use for textured models). `>0` = vertex-cluster
  decimation **without Blender** (coarser; averages UVs across seams, so only for heavy *untextured* meshes).

### Texture / import
- **Reuse extracted files** — skip re-importing/re-slimming and reuse what's already in the resource folder. Tick it to
  keep a **hand-edited texture** across a re-bake, or to iterate on **Size/transform** fast. **Untick it** if you changed
  the Model file, the Clip, or the Animate-only-bones — otherwise the slim step is skipped and your change is ignored.

### Bake / Reset
- **Bake** runs the pipeline and writes the registry. **Reset** clears the form.

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

## 7. Troubleshooting

| Symptom | Cause → fix |
|---|---|
| **Model invisible / see-through** | Single-sided/CAD mesh (backface-culled) → **Winding fix** or **Double-sided**. Or it overflowed the shared buffer → lower **Reduce to ~tris**. |
| **Model tiny (a speck) or huge** | **Size** is the world length — set it to what looks right; the Console logs the scale. |
| **Change didn't show in-game** | You didn't **rebuild the mod** (§6). Or **Reuse extracted** was on and skipped the re-slim after you changed Clip/bones/model — untick it. |
| **Animated toggle greyed out** | The model has no animation the probe can see (OBJ, or a glTF with no `animations`). Use a rigged glb/fbx. FBX/.blend can't be probed cheaply, so the toggle stays enabled — type the clip/bones by hand. |
| **"No clips readable from this model"** | Clip/Bone Pick works for glTF/GLB only. For FBX/.blend, type the clip name and bone prefixes manually. |
| **Animated model plays the wrong motion** (parts assemble/explode) | You baked the wrong clip → set **Clip name** to the loop (e.g. `hover`), not `exploded_view`. |
| **Animated model tears apart / arms fly out** | The clip source wasn't isolated (an extra FBX in the folder → a multi-clip collection). Fixed in the current Factory (it bakes into a per-model `anim/` subfolder). Re-bake; if it persists, remove stray `.fbx` from the resource folder. |
| **~1s stall each loop** | A padded frozen tail in the clip. The Factory auto-clamps the frame range now — re-bake. |
| **Body wobbles / "unbalanced flywheel"** | The clip animates the whole body → set **Animate only bones** to just the spinning group (e.g. `prop`). |
| **A donor part shows through** (rotor, extra mesh) | **Hide donor meshes → Pick** it (after one launch so it's logged). If it's an *animated* donor sub-part, it can't be hidden — pick a cleaner donor. |
| **Bake fails: "needs Blender"** | Install Blender or set its path in **Settings**. For static decimation without Blender, use **Convert grid** instead of Reduce-to-tris. |
| **Texture looks stale in the editor** | A Unity texture-residency quirk after a multi-material bake — open the source textures in the Project view and back. The in-game result is correct. |
| **Multi-material GLB loses its material split** | Use **FBX** for multi-material models; GLB is fine for single-material. |

---

## 8. Where things land

- **Baked assets:** `Assets/Resources/<name>_Skeleton.asset`, `_Atlas.asset`, `_Mat.mat`, `_ModelMesh.asset`
  (static); animated adds `_Clips.asset` and a `<name>/anim/<name>_anim.fbx`. The imported model + albedo sit in
  `Assets/Resources/<name>/`.
- **Registry:** `<Humankind>\BepInEx\config\enc_models.json` — one entry per model (pawn description, `skel`/`atlas` GUIDs,
  transform, flags; animated adds `clip` + `animated`/`animClip`/`animateBones`).
- **Runtime log:** `<Humankind>\BepInEx\LogOutput.log` — `[Uni] …` lines show what was injected (and the donor fragment
  names the Hide-donor Pick reads).
