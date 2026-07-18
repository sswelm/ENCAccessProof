# rig_anim.py — prepare a RIGGED, ANIMATED model for the Universal Model Factory's animated path.
# Unlike mesh_reduce.py (which only decimates geometry), this KEEPS the model's armature + one animation clip so the
# Factory can bake an Amplitude Skeleton + ClipCollection from it. Steps:
#   1. import (glb/gltf/fbx/blend) and drop material-less junk meshes (stray icospheres etc.)
#   2. pick one action, assign it, delete the rest
#   3. OPTIONAL bone filter: keep animation only on bones whose name starts with one of the given prefixes
#      (e.g. "prop,rotor") — strips camera/body-bob curves that make a model wobble; empty = keep the whole clip
#   4. clamp the SCENE frame range to the action's real range — bake_anim otherwise pads frozen tail frames (default
#      1..250) that make the animation stall for ~1s each loop
#   5. export the first material's base-colour image as the albedo PNG (for the Factory's atlas)
#   6. join to 1 mesh + 1 material + quadric-decimate to ~target tris (KEEPING the armature + weights)
#   7. export a slim FBX with baked animation
# Usage: blender -b --python rig_anim.py -- <input> <output.fbx> <targetTris> [bonePrefixesCSV] [clipName] [albedoOut.png] [keepMats] [rotXdeg,rotYdeg,rotZdeg]
import bpy, bmesh, sys, os
from math import radians
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp, target = argv[0], argv[1], int(argv[2])
prefixes = [p.strip() for p in (argv[3] if len(argv) > 3 else "").split(",") if p.strip()]
clip_name = (argv[4] if len(argv) > 4 else "").strip()
albedo_out = argv[5] if len(argv) > 5 else ""
keep_materials = len(argv) > 6 and argv[6].strip() == "1"   # multi-material bake: keep the slots (submeshes) instead of collapsing to 1
# Optional rig ROTATION (degrees, registry semantics: x = pitch/stand-up, y = heading, z = roll). Some rigs round-trip
# glTF->Blender->FBX lying down or facing sideways (the Combine soldier bakes on his back); the game orients animated
# units by the RIG, so the fix must be baked into the rig here — the Factory's Rotation field is meaningless at runtime.
rig_rot = [0.0, 0.0, 0.0]
if len(argv) > 7 and argv[7].strip():
    try:
        rig_rot = [float(v) for v in argv[7].split(",")][:3] + [0.0] * max(0, 3 - len(argv[7].split(",")))
    except Exception:
        print("RIGANIM WARN: bad rotation arg '%s' — ignoring" % argv[7])

bpy.ops.wm.read_factory_settings(use_empty=True)
ext = os.path.splitext(inp)[1].lower()
if ext == ".fbx":
    bpy.ops.import_scene.fbx(filepath=inp)
elif ext == ".blend":
    bpy.ops.wm.open_mainfile(filepath=inp)
else:
    bpy.ops.import_scene.gltf(filepath=inp)   # .glb / .gltf

# drop material-less junk meshes (e.g. a gltf placeholder icosphere)
for o in [o for o in bpy.context.scene.objects if o.type == 'MESH' and len(o.data.materials) == 0]:
    bpy.data.objects.remove(o, do_unlink=True)

arm = next((o for o in bpy.context.scene.objects if o.type == 'ARMATURE'), None)
if arm is None:
    print("RIGANIM ERROR: no armature found — the model is not rigged, use the normal (static) bake instead")
    sys.exit(1)

# pick an action: the named clip if given, else the one already on the armature, else the first in the file
act = None
if clip_name:
    act = bpy.data.actions.get(clip_name)
    if act is None:
        print("RIGANIM ERROR: clip '%s' not found. Available: %s" % (clip_name, [a.name for a in bpy.data.actions]))
        sys.exit(1)
if act is None and arm.animation_data and arm.animation_data.action:
    act = arm.animation_data.action
if act is None and len(bpy.data.actions):
    act = bpy.data.actions[0]
if act is None:
    print("RIGANIM ERROR: no animation action found in the model")
    sys.exit(1)
if not arm.animation_data:
    arm.animation_data_create()
arm.animation_data.action = act
try:
    if getattr(act, "slots", None):
        arm.animation_data.action_slot = act.slots[0]   # Blender 5.x slotted actions
except Exception as e:
    print("RIGANIM slot warn:", e)
for a in list(bpy.data.actions):
    if a is not act:
        try: bpy.data.actions.remove(a)
        except Exception: pass
print("RIGANIM action '%s'" % act.name)

# all f-curves across the (Blender 5.x slotted) action, with their owning collection so we can remove them
def all_fcurve_owners(action):
    if getattr(action, "fcurves", None) is not None and len(action.fcurves):
        return [(action.fcurves, fc) for fc in list(action.fcurves)]
    out = []
    for layer in getattr(action, "layers", []):
        for strip in layer.strips:
            for cb in getattr(strip, "channelbags", []):
                for fc in list(cb.fcurves):
                    out.append((cb.fcurves, fc))
    return out

# OPTIONAL: keep animation only on bones matching a prefix (strip camera pans / root-bob that cause wobble)
if prefixes:
    def bone_of(dp):
        if 'pose.bones[' not in dp: return None
        return dp.split('["', 1)[1].split('"]', 1)[0]
    owners = all_fcurve_owners(act)   # snapshot: safe to remove from the owning collections while iterating this list
    avail = sorted({bone_of(fc.data_path) for _, fc in owners if bone_of(fc.data_path) is not None})
    kept = rem = 0
    for coll, fc in owners:
        b = bone_of(fc.data_path)
        if b is not None and any(b.startswith(p) for p in prefixes):
            kept += 1
        else:
            coll.remove(fc); rem += 1
    print("RIGANIM bone-filter %s: kept %d fcurves, removed %d" % (prefixes, kept, rem))
    # T6: if the prefix matched NOTHING, every fcurve was stripped -> a frozen 1-frame clip would bake and ship with
    # exit 0 (silent). Hard-fail instead, listing the animated bones so the prefix can be corrected.
    if kept == 0:
        print("RIGANIM ERROR: bone-filter %s matched no animated bone — every fcurve was stripped, the clip would be frozen. Animated bones: %s" % (prefixes, avail))
        sys.exit(1)

# FOLD FRAME-0 BONE LOCATIONS INTO THE REST POSE — auto-rigged models (the Combine soldier) often park a bone's REST
# somewhere else and hold it in place with a constant corrective location key in every clip. Amplitude can't play
# location keys (see the strip below), so without this fold the correction is lost and the part sits rigidly displaced
# (the soldier's head). Fix: evaluate each location-keyed bone at the clip's first frame, convert that offset to
# armature space via the bone's rest orientation, and move the bone's REST (head+tail, whole subtree — pose location
# shifts descendants too, and offsets from nested keyed bones compose additively since they're pure translations).
# After the fold, frame-0 pose == rest, and the rotation-only clip plays around the corrected pivots.
try:
    _loc0 = {}
    for _coll, _fc in all_fcurve_owners(act):
        _dp = _fc.data_path
        if _dp.startswith("pose.bones") and _dp.endswith(".location"):
            _b = _dp.split('["', 1)[1].split('"]', 1)[0]
            _loc0.setdefault(_b, [0.0, 0.0, 0.0])[_fc.array_index] = _fc.evaluate(act.frame_range[0])
    if _loc0:
        _rest3 = {b.name: b.matrix_local.to_3x3() for b in arm.data.bones}
        _kids = {b.name: [c.name for c in b.children] for b in arm.data.bones}
        bpy.ops.object.select_all(action='DESELECT')
        arm.select_set(True)
        bpy.context.view_layer.objects.active = arm
        bpy.ops.object.mode_set(mode='EDIT')
        _eb = arm.data.edit_bones
        _moved = 0
        for _bname, _l in _loc0.items():
            if _bname not in _eb or _bname not in _rest3: continue
            _off = _rest3[_bname] @ Vector(_l)     # bone-local frame-0 offset -> armature space
            if _off.length < 1e-9: continue
            _stack = [_bname]
            while _stack:
                _n = _stack.pop()
                if _n in _eb:
                    _eb[_n].head += _off; _eb[_n].tail += _off
                _stack.extend(_kids.get(_n, []))
            _moved += 1
        bpy.ops.object.mode_set(mode='OBJECT')
        if _moved:
            print("RIGANIM folded frame-0 locations into RESTS for %d bones (corrective offsets preserved)" % _moved)
except Exception as _e:
    try: bpy.ops.object.mode_set(mode='OBJECT')
    except Exception: pass
    print("RIGANIM WARN: rest-fold failed (%s) — location keys will just be stripped" % _e)

# STRIP BONE-TRANSLATION CURVES — Amplitude clips are effectively ROTATION-ONLY: its clip bake reads translation keys
# at the FBX's NATIVE scale, bypassing the importer's unit conversion, so on a Fix-100x rig a 2 cm neck bob becomes
# ~2 world units and the head rips off in-game ("the movement gets exaggerated") while Unity's own preview plays the
# same FBX fine. Rotations are scale-free; bone REST offsets come from the (properly scaled) skeleton, so a
# rotation-driven rig looks identical without these curves. (This generalizes the drone's old wobble fix, which
# stripped to rotation-only 'prop' curves by hand.)
_locs = 0
for coll, fc in all_fcurve_owners(act):
    if fc.data_path.startswith("pose.bones") and fc.data_path.endswith(".location"):
        coll.remove(fc); _locs += 1
if _locs:
    print("RIGANIM stripped %d bone-LOCATION fcurves (Amplitude clips are rotation-only; translations bake unscaled)" % _locs)

# clamp scene frame range to the action's real range (else bake_anim pads a frozen tail -> ~1s stall per loop)
fs, fe = [int(round(v)) for v in act.frame_range]
bpy.context.scene.frame_start = fs
bpy.context.scene.frame_end = fe
print("RIGANIM frame range %d..%d" % (fs, fe))

# export the base-colour image as the albedo PNG (for the Factory atlas). Trace the Principled BSDF's Base Color input
# back to the actual base-colour texture — NOT just the first TEX_IMAGE node. Node order is creation order, so in a PBR
# material the first image node can be a normal / roughness / metallic map, which would hand the atlas a purple normal
# map as "albedo" (garbled skin, no error). Fall back to any image node only when there's no Principled / it's unlinked.
def base_color_image(mat):
    if not (mat and mat.node_tree):   # node_tree is non-None exactly when the material has nodes (use_nodes removed in Blender 6.0)
        return None
    nt = mat.node_tree
    for n in nt.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            inp = n.inputs.get('Base Color')
            if inp and inp.is_linked:
                seen, stack = set(), [inp.links[0].from_node]   # walk upstream to the nearest image (through a mix/gamma node etc.)
                while stack:
                    node = stack.pop()
                    if node is None or node in seen: continue
                    seen.add(node)
                    if node.type == 'TEX_IMAGE' and node.image:
                        return node.image
                    for s in node.inputs:
                        if s.is_linked: stack.append(s.links[0].from_node)
            break
    for n in nt.nodes:                # fallback: no Principled, or Base Color unlinked -> any image (better than nothing)
        if n.type == 'TEX_IMAGE' and n.image:
            return n.image
    return None

if albedo_out:
    try:
        img = None
        for o in bpy.context.scene.objects:
            if o.type != 'MESH' or not o.data.materials: continue
            img = base_color_image(o.data.materials[0])
            if img: break
        if img:
            img.filepath_raw = albedo_out
            img.file_format = 'PNG'
            img.save()
            print("RIGANIM albedo ->", albedo_out, "(%s)" % img.name)
        else:
            print("RIGANIM no albedo image found (model may be untextured)")
    except Exception as e:
        print("RIGANIM albedo export warn:", e)

# join to 1 mesh + 1 material + decimate (KEEP the armature + skin weights)
# (material-less reference junk — e.g. a stray Icosphere — was already culled up top, right after import)
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
if not meshes:
    print("RIGANIM ERROR: no mesh to export"); sys.exit(1)
bpy.ops.object.select_all(action='DESELECT')
for o in meshes: o.select_set(True)
# join() keeps ONLY the active object's modifiers, so make the active one a mesh that HAS an armature modifier —
# scene-order meshes[0] can be a bone-parented prop with no armature modifier, and joining onto it would drop the skin
# binding and export the whole model rigid/frozen. Prefer a skinned mesh here; we also re-guarantee the modifier below.
active = next((o for o in meshes if any(md.type == 'ARMATURE' for md in o.modifiers)), meshes[0])
bpy.context.view_layer.objects.active = active
if len(meshes) > 1:
    bpy.ops.object.join()
joined = bpy.context.view_layer.objects.active
me = joined.data
# WELD FIRST. Many exports (this da-Vinci ribauldequin included) ship massively duplicated vertices —
# coincident but unmerged — so the mesh is disconnected "face soup": 76k verts that weld down to 19k
# (75% were dupes). Two problems fall out of that: (1) the vertex count looks huge and triggers brutal
# decimation it never actually needed, and (2) quadric COLLAPSE needs connected edge loops, so on soup
# it caves smooth cylinders (a cannon's barrels) into slivers while spoked parts survive. Merging by
# distance reconnects the surface: decimation (if still needed) is clean, and the honest vert count is a
# fraction of the raw one. Blender stores UVs per face-corner, so welding VERTICES preserves the UV seams.
# WELD only a SINGLE-material AND SINGLE-BONE mesh. Welding merges coincident verts, which corrupts anything that
# relies on them staying split: (a) MULTI-MATERIAL seams (the howitzer: 6 mats) and (b) MULTI-BONE skinning seams
# (the ReconDrone: a spinning 'prop' bone + body) — a merged vertex straddling two bones gives Amplitude's skeleton
# importer (MeshCollection.ImportMeshes) a bad index -> IndexOutOfRangeException at bake. len(vertex_groups) is the
# skinned-bone count. Only a truly simple 1-material / 1-bone mesh (a fragmented static-style rig) is safe to weld.
_nvg = len(joined.vertex_groups)
if len(me.materials) <= 1 and _nvg <= 1:
    _wb = bmesh.new(); _wb.from_mesh(me); _n0 = len(_wb.verts)
    bmesh.ops.remove_doubles(_wb, verts=_wb.verts, dist=1e-4)
    _n1 = len(_wb.verts); _wb.to_mesh(me); _wb.free()
    print("RIGANIM weld: %d -> %d verts (%.0f%% duplicates removed)" % (_n0, _n1, 100.0 * (1 - _n1 / max(_n0, 1))))
else:
    print("RIGANIM weld: SKIPPED (%d materials, %d bones -> preserve seams)" % (len(me.materials), _nvg))
if not keep_materials:                       # SINGLE-material path: collapse to one slot (the old default)
    while len(me.materials) > 1:
        me.materials.pop(index=len(me.materials) - 1)
    for p in me.polygons:
        p.material_index = 0
else:
    print("RIGANIM keeping %d material slots (multi-material)" % len(me.materials))
total = sum(len(p.vertices) - 2 for p in me.polygons)
ratio = min(1.0, max(0.02, target / max(1, total)))
mdec = joined.modifiers.new("dec", 'DECIMATE')
mdec.decimate_type = 'COLLAPSE'; mdec.ratio = ratio; mdec.use_collapse_triangulate = True
bpy.ops.object.select_all(action='DESELECT'); joined.select_set(True); bpy.context.view_layer.objects.active = joined
bpy.ops.object.modifier_apply(modifier=mdec.name)
print("RIGANIM decimate %d -> %d tris (ratio %.3f)" % (total, sum(len(p.vertices) - 2 for p in me.polygons), ratio))

# GUARANTEE the skin binding: whichever object won the join, the joined mesh keeps every source mesh's vertex groups
# (weights) regardless — so if the join dropped the armature modifier, re-adding one bound to `arm` fully restores
# skinning. Without this a model whose first mesh was a bone-parented prop exports rigid/frozen (T4).
if not any(md.type == 'ARMATURE' for md in joined.modifiers):
    _am = joined.modifiers.new("Armature", 'ARMATURE'); _am.object = arm
    print("RIGANIM re-bound armature modifier (join had dropped it)")

# BAKE-TIME RIG ROTATION: rotate every parent-less object (the armature root; children follow) in WORLD space, then
# APPLY the transform INTO THE DATA (vertices + bone rest matrices). Object-level rotation alone is NOT enough: a
# skinned mesh keeps its vertices in mesh/bind space, and both the Factory preview (raw mesh) and Amplitude's
# skeleton bake (SetPrefab/ImportMeshes) read the DATA, discarding object transforms — an object-only rotation
# looked applied in the log yet changed nothing in-game. Registry mapping: x -> Blender world X (pitch — stands a
# lying-on-its-back rig upright), y -> Blender world Z (heading), z -> Blender world Y (roll). Applying the SAME
# world rotation to armature rest bones AND mesh vertices keeps the skin binding aligned; bone-local animation
# curves are relative to the (now-rotated) rest pose, so the clip plays identically.
# COLLAPSE NO-OP ROOT BONES (depth reduction) — the runtime composes bone chains with a bounded depth (CPU cap 15,
# the GPU pass suspected lower): a raw rig's pass-through roots (the soldier's `_rootJoint`: no animation channels,
# no vertex weights, single child) burn depth for nothing and push the head/hand chains over the working range.
# Deleting them re-roots their child; Amplitude adds the armature object as one more level on top regardless.
if any(abs(v) > 1e-4 for v in rig_rot):
    _animated_bones = set()
    for _c2, _fc2 in all_fcurve_owners(act):
        if _fc2.data_path.startswith('pose.bones['):
            _animated_bones.add(_fc2.data_path.split('["', 1)[1].split('"]', 1)[0])
    _weighted = set()   # only groups with REAL weights protect a bone (glTF exports empty groups for every joint)
    for _o2 in bpy.context.scene.objects:
        if _o2.type == 'MESH':
            _gnames = [g.name for g in _o2.vertex_groups]
            for _v in _o2.data.vertices:
                for _ge in _v.groups:
                    if _ge.weight > 1e-6:
                        _weighted.add(_gnames[_ge.group])
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='EDIT')
    _removed = 0
    while True:
        _victim = None
        for _b2 in [b for b in arm.data.edit_bones if b.parent is None]:
            if len(_b2.children) == 1 and _b2.name not in _animated_bones and _b2.name not in _weighted:
                _victim = _b2; break
        if _victim is None:
            break
        arm.data.edit_bones.remove(_victim); _removed += 1
    bpy.ops.object.mode_set(mode='OBJECT')
    if _removed:
        print("RIGANIM collapsed %d no-op root bone(s) — every chain is now %d level(s) shallower" % (_removed, _removed))

# TOPOLOGICAL BONE RENAME — Amplitude's Skeleton bake SORTS BONES ALPHABETICALLY (decompiled BuildBoneEntry.Compare:
# roots first, then string.Compare on the name) and the runtime composes them in array order, ASSUMING PARENTS COME
# BEFORE CHILDREN. Amplitude's own rigs satisfy that by naming convention; a raw rig like the ValveBiped does NOT
# ('..._014' sorts before '..._02'), so the head/neck and forearm chains read their parents' garbage transforms and
# hang displaced in-game. Prefixing every bone with its breadth-first index makes alphabetical == topological.
# Blender auto-syncs vertex groups + fcurve paths on rename; the bone-filter above already ran on the ORIGINAL names.
# Gated to the rotation-set path: 0,0,0 stays the byte-identical legacy pipeline.
if any(abs(v) > 1e-4 for v in rig_rot):
    _order = []
    def _walkb(b):
        _order.append(b.name)
        for c in sorted(b.children, key=lambda x: x.name):
            _walkb(c)
    for _root in [b for b in arm.data.bones if b.parent is None]:
        _walkb(_root)
    for _i, _bname in enumerate(_order):
        arm.data.bones[_bname].name = "b%03d_%s" % (_i, _bname)
    print("RIGANIM bones renamed with topological prefixes (%d bones) — parents now sort before children" % len(_order))

# RIG ROTATION — ONLY when a rotation is requested (rotation 0,0,0 = the EXACT legacy pipeline, byte-for-byte: no
# object fiddling, no fold — models that were correct before stay correct; the fold is world-preserving for Unity's
# mesh import but NOT for Amplitude's skeleton bake, so folding unconditionally flipped the previously-good howitzer
# upside-down in-game). When a rotation IS set: rotate the parent-less objects in world space, strip OBJECT-level
# animation fcurves (a glTF often keys the armature NODE itself — they block transform_apply and re-assert the old
# orientation each frame), then transform_apply INTO THE DATA (vertices + bone rests, identity nodes) — object-level
# rotation alone is dropped downstream (proven in-game on the Combine soldier, which needs 90,0,0 to stand).
if any(abs(v) > 1e-4 for v in rig_rot):
    rot = (Matrix.Rotation(radians(rig_rot[1]), 4, 'Z') @
           Matrix.Rotation(radians(rig_rot[0]), 4, 'X') @
           Matrix.Rotation(radians(rig_rot[2]), 4, 'Y'))
    for o in bpy.context.scene.objects:
        if o.parent is None:
            o.matrix_world = rot @ o.matrix_world
    print("RIGANIM rig rotation: x=%s y=%s z=%s (deg, registry semantics)" % tuple(rig_rot))
    if arm.animation_data and arm.animation_data.action:
        _rm = 0
        for coll, fc in all_fcurve_owners(arm.animation_data.action):
            if not fc.data_path.startswith("pose.bones"):
                coll.remove(fc); _rm += 1
        if _rm: print("RIGANIM stripped %d OBJECT-level fcurves (non-bone) so the rig orientation can bake" % _rm)
    bpy.ops.object.select_all(action='SELECT')
    bpy.context.view_layer.objects.active = arm
    try:
        # ROTATION **AND SCALE** into the data. Decompiled Amplitude bake (ClipEntry.Reimport / Skeleton.Reimport):
        # the clip is sampled from SCENE NODE transforms but the skeleton's rest comes from MESH BINDPOSES, and the
        # pose TRS holds a single UNIFORM scale — FBX unit-scale compensation living on nodes desyncs the two sources
        # (constant rest deltas -> a rigidly displaced bone, the soldier's head). Folding scale leaves nothing on the
        # nodes to lose: the export is in true units, identity transforms everywhere.
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
        print("RIGANIM rig rotation+scale APPLIED TO DATA (identity nodes)")
    except Exception as e:
        print("RIGANIM WARN: transform_apply failed (%s) — rotation left object-level (may not survive the bake)" % e)

bpy.ops.object.select_all(action='SELECT')
# EXPORT SCALE (raw-FBX-parse evidence, fbx_lclscale/fbx_binddump): Blender's exporter writes meters->cm by scaling
# the ROOT OBJECTS x100 (`Lcl Scaling [100,100,100]`). Unity compensates with 0.01 in every skinned-mesh bindpose +
# a x100 root — a sandwich Amplitude's uniform-scale TRS composition mangles on deep bone chains (the Combine
# soldier's head rode off his shoulders). The proven ReconDrone file has NO node scaling only by luck: its glTF's
# tiny-authored 0.01 object scale exactly cancels the exporter's x100.
# - ROTATION/FOLD path (rotation set): transform_apply normalized objects to scale 1, so pre-divide with
#   global_scale=0.01 -> net node scale 1, UnitScaleFactor 1, bind clusters 1 — the clean drone profile, by design.
# - LEGACY path (rotation 0,0,0): keep the exporter untouched, byte-identical output (the working models' contract).
gscale = 0.01 if any(abs(v) > 1e-4 for v in rig_rot) else 1.0
if gscale != 1.0:
    print("RIGANIM export global_scale=0.01 (cancels the exporter's m->cm x100 root scaling)")
bpy.ops.export_scene.fbx(filepath=outp, use_selection=False, add_leaf_bones=False, global_scale=gscale,
                         bake_anim=True, bake_anim_use_all_actions=False,
                         bake_anim_use_nla_strips=False, object_types={'ARMATURE', 'MESH'})
print("RIGANIM wrote", outp)
