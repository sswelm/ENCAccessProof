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
# Usage: blender -b --python rig_anim.py -- <input> <output.fbx> <targetTris> [bonePrefixesCSV] [clipName] [albedoOut.png] [keepMats] [rotXdeg,rotYdeg,rotZdeg] [convertRig]
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
# EXPLICIT conversion switch (argv[8], "1"/"0"). Selects the RAW-RIG CONVERSION path: no-op root collapse, topological
# bone rename, rotation/scale fold into the data, clean-unit export (global_scale 0.01). It used to be inferred from
# rotation != 0 (the soldier shipped with a 360,0,0 identity trick; a rotation edit on a legacy model silently
# rerouted its bake) — the flag makes rotation just a rotation again. Absent arg = the old inference, so old callers
# keep their exact behavior.
if len(argv) > 8 and argv[8].strip() in ("0", "1"):
    convert_rig = argv[8].strip() == "1"
else:
    convert_rig = any(abs(v) > 1e-4 for v in rig_rot)
print("RIGANIM conversion path: %s" % ("ON (raw-rig convert)" if convert_rig else "off (legacy byte-identical)"))
# STATE-DRIVEN roles (argv[9], optional; Phase 2 2026-07-19): "role=clipName;role=clipName" (e.g.
# "move=Skel|a_RunN;after=Skel|Settle"). Each role exports the SAME prepared rig with that role's clip to a sibling
# folder anim_<role>/ next to the primary output — one FBX per folder, so each ClipCollection scan sees exactly one
# clip. CRITICAL for the conversion path: every role's clip must be rebaked against ONE shared rest (the PRIMARY
# clip's frame-0 pose) — converting roles in separate Blender runs would derive a DIFFERENT rest per clip and the
# non-primary clips would play rigidly displaced on the primary-baked skeleton (the torn-head failure, reborn).
role_specs = []   # ordered [(role, clipName)]
if len(argv) > 9 and argv[9].strip():
    for _pair in argv[9].split(";"):
        _pair = _pair.strip()
        if _pair and "=" in _pair:
            _r, _cn = _pair.split("=", 1)
            if _r.strip() and _cn.strip():
                role_specs.append((_r.strip(), _cn.strip()))
if role_specs:
    print("RIGANIM state roles: %s" % ", ".join("%s='%s'" % rc for rc in role_specs))

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

# BONE-PARENT -> SKIN-WEIGHT CONVERSION (mech finding 2026-07-20): many downloaded mech/vehicle rigs never SKIN their
# meshes (vertex weights); they RIGIDLY HANG each part off a bone via Blender bone-parenting (parent_type='BONE'),
# often through intermediate empties or parent meshes (bone -> empty -> mesh, or bone -> mesh -> child mesh). Blender
# animates that fine, but our pipeline JOINS all meshes into ONE skinned mesh and rebinds via VERTEX WEIGHTS —
# bone-parenting carries NO weights, so the join drops every part's bone and all verts fall to bone #0 (Unity warns
# "N verts with no weight -> assigned to bone #0"); the whole model then collapses onto the root bone in-game — it
# lies flat and the arms fling up. Fix: BEFORE the join, convert each part's bone-parenting into a FULL-WEIGHT vertex
# group on its governing bone + an armature modifier, so the join preserves per-part bone binding. Walk the parent
# chain to find the governing bone (a child hung off a bone-parented mesh follows that same bone). BIND AT REST so the
# armature deform is identity there — otherwise the modifier double-applies the current pose on top of the parented
# position. Gated to convertRig (properly skinned rigs need none of this); a no-op when nothing is bone-parented.
if convert_rig:
    def _governing_bone(o):
        cur = o
        while cur is not None and cur.parent is not None:
            if cur.parent_type == 'BONE' and cur.parent.type == 'ARMATURE' and cur.parent_bone:
                return cur.parent_bone
            cur = cur.parent
        return None
    _bp = [o for o in bpy.context.scene.objects if o.type == 'MESH' and _governing_bone(o) is not None]
    if _bp:
        _root_bone = next((b.name for b in arm.data.bones if b.parent is None), None)
        _prev_pp = arm.data.pose_position
        arm.data.pose_position = 'REST'                        # bind against rest: bone delta is identity there
        bpy.context.view_layer.update()
        _plan = [(o, _governing_bone(o), o.matrix_world.copy()) for o in _bp]   # capture bone + rest world BEFORE reparenting
        _bound = 0
        for _o, _bone, _mw in _plan:
            _tgt = _bone if arm.data.bones.get(_bone) else _root_bone
            if _tgt is None:
                continue
            _o.parent = arm                                   # re-home onto the armature OBJECT (not the bone)
            _o.parent_type = 'OBJECT'
            _o.parent_bone = ''
            _o.matrix_parent_inverse = Matrix()               # clear the old bone-parent inverse ...
            _o.matrix_world = _mw                             # ... then pin the rest-pose world position exactly
            _vg = _o.vertex_groups.get(_tgt) or _o.vertex_groups.new(name=_tgt)
            _vg.add(list(range(len(_o.data.vertices))), 1.0, 'REPLACE')
            if not any(md.type == 'ARMATURE' for md in _o.modifiers):
                _am = _o.modifiers.new("Armature", 'ARMATURE'); _am.object = arm
            _bound += 1
        arm.data.pose_position = _prev_pp
        bpy.context.view_layer.update()
        print("RIGANIM bone-parent->skin: bound %d rigidly-hung mesh(es) full-weight to their governing bones (was: no weights -> bone #0)" % _bound)

# FLATTEN WRAPPER EMPTIES (mech finding 2026-07-20): glTF/FBX sources often wrap the rig in a parent empty (a
# "group"/scene-root) carrying a NON-IDENTITY scale — the Light Assault Mech's was 0.010. convertRig's later
# transform_apply only bakes an object's OWN transform, never an inherited parent scale, so that wrapper survived
# to the export as a scaled root node. Unity folds it into the mesh, but Amplitude's skeleton import reads bind
# poses WITHOUT it → the skeleton sits ~100× off the mesh and every rigid single-bone vert flings into a "wing".
# Un-parent the rig from any EMPTY (KEEP_TRANSFORM bakes the empty's transform onto the object, where convertRig's
# transform_apply then folds it into the data) and delete the empties → identity export nodes. Gated to convertRig
# so the legacy byte-identical path is untouched; a no-op for rigs without wrapper empties (the soldier).
if convert_rig:
    _wrapped = [o for o in bpy.context.scene.objects if o.type in ('MESH', 'ARMATURE') and o.parent is not None and o.parent.type == 'EMPTY']
    for _o in _wrapped:
        _mw = _o.matrix_world.copy()
        _o.parent = None
        _o.matrix_world = _mw
    _empties = [o for o in list(bpy.data.objects) if o.type == 'EMPTY']
    for _e in _empties:
        bpy.data.objects.remove(_e, do_unlink=True)
    if _empties:
        print("RIGANIM flattened %d wrapper empt%s; rig reparented to root for identity export nodes" % (len(_empties), "y" if len(_empties) == 1 else "ies"))

if not arm.animation_data:
    arm.animation_data_create()
def assign_action(a):
    arm.animation_data.action = a
    try:
        if getattr(a, "slots", None):
            arm.animation_data.action_slot = a.slots[0]   # Blender 5.x slotted actions
    except Exception as e:
        print("RIGANIM slot warn:", e)

# BONE CAP (mech finding, 2026-07-20): Amplitude's GPU crowd-skinning caps at 256 bones; verts weighted to a bone
# index >255 get garbage transforms and stretch into huge "wing" spikes in-game (invisible in Blender, which has no
# limit). Detailed mech/robot rigs blow past it (the Light Assault Mech had 332 bones; 4084 verts / 5.6% flung).
# Fix: merge LEAF bones (no surviving children) into their nearest surviving ancestor — transferring their skin
# weights so the geometry follows the parent limb — prioritizing mechanical DETAIL (pistons/tubes/_end/targets),
# until under a safe budget. No-op for rigs already under it (soldier 62, howitzer ~27), so proven models are
# byte-identical. The important limb/gun bones are never leaves, so the animation is preserved.
_BONE_LIMIT = 240
if len(arm.data.bones) > _BONE_LIMIT:
    _skmesh = [m for m in bpy.data.objects if m.type == 'MESH' and m.find_armature() == arm]
    # which bones actually deform the mesh? Only those need to survive with a low index. The rest — IK targets,
    # _end markers, control bones — carry NO skin weight, so removing them NEVER moves a vertex (unlike transferring
    # weights, which corrupted the bind shape: a first attempt flung 350 verts). Remove zero-weight LEAF bones
    # iteratively (a leaf has no children, so deleting it can't break a weighted descendant's hierarchy or its
    # animation); each pass may expose new zero-weight leaves as their children go. Stops when under the cap or when
    # only weighted bones + their zero-weight ANCESTORS remain (those are load-bearing and kept).
    _weighted = set()
    for _m in _skmesh:
        _vgn = {vg.index: vg.name for vg in _m.vertex_groups}
        for _v in _m.data.vertices:
            for _g in _v.groups:
                if _g.weight > 0.001:
                    _weighted.add(_vgn.get(_g.group))
    _removed = 0
    bpy.context.view_layer.objects.active = arm
    while len(arm.data.bones) > _BONE_LIMIT:
        _leaves = [b.name for b in arm.data.bones if len(b.children) == 0 and b.name not in _weighted]
        if not _leaves:
            break                                     # only weighted bones + their load-bearing ancestors remain
        bpy.ops.object.mode_set(mode='EDIT')
        for _n in _leaves:                            # remove every zero-weight leaf this pass (all safe to delete)
            _eb = arm.data.edit_bones.get(_n)
            if _eb:
                arm.data.edit_bones.remove(_eb); _removed += 1
        bpy.ops.object.mode_set(mode='OBJECT')        # a parent may become a new zero-weight leaf next pass
    print("RIGANIM bone-cap: removed %d zero-weight leaf bones -> %d bones (Amplitude 256-bone GPU limit; weighted bones untouched)" % (_removed, len(arm.data.bones)))

# CLIP SLICING (howitzer migration take 2, 2026-07-19): a clip name may carry a FRAME RANGE — "deploy[0..180]" —
# and optionally a SPEED STEP — "deploy[179..0/3]" = every 3rd source frame, so the slice plays 3× faster (BAKE-ONLY
# pacing: a 7.5 s authored fold outlasts a one-tile map move; at /3 it completes in 2.5 s — the runtime deliberately
# plays clips at face value, no speed knobs). The slice is synthesized as a NEW action by sampling the SOURCE
# action's evaluated pose basis per frame INSIDE THIS session (the same pipeline that provably bakes the source
# right) — the Blender import→export round-trip retrofit (add_role_clips.py) altered the clip-vs-rest relationship
# and baked flipped/inverse in-game; never round-trip a converted GLB. start>end = REVERSED (a fold from an
# unfold); single frame = padded to 2 identical frames (a held stance; 0-length clips can be dropped by importers).
_slice_re = __import__("re").compile(r"^(.*)\[(\d+)\.\.(\d+)(?:/(\d+))?\]$")
def resolve_clip(spec, tag):
    m = _slice_re.match(spec.strip())
    if not m:
        a = bpy.data.actions.get(spec)
        if a is None:
            print("RIGANIM ERROR: clip '%s' (%s) not found. Available: %s" % (spec, tag, [a.name for a in bpy.data.actions]))
            sys.exit(1)
        return a
    src_name, f0, f1 = m.group(1), int(m.group(2)), int(m.group(3))
    step = max(1, int(m.group(4))) if m.group(4) else 1
    src = bpy.data.actions.get(src_name)
    if src is None:
        print("RIGANIM ERROR: slice source '%s' (%s) not found. Available: %s" % (src_name, tag, [a.name for a in bpy.data.actions]))
        sys.exit(1)
    frames = list(range(f0, f1 + 1, step)) if f1 >= f0 else list(range(f0, f1 - 1, -step))
    if frames[-1] != f1:
        frames.append(f1)                                # always land exactly on the end frame (the held pose)
    if len(frames) == 1:
        frames = frames * 2                              # held stance: 2 identical frames
    new_name = "%s_%s_%d_%d_%d" % (src_name, tag, f0, f1, step)
    old = bpy.data.actions.get(new_name)
    if old is not None:
        return old                                       # same slice requested by two roles -> share it
    # SCENE-STATE HYGIENE (the byte-gate finding, 2026-07-19): slicing SETS pose values on every bone; any channel
    # the PRIMARY action doesn't key then evaluates to those leftovers in the primary's own export — the sandbox
    # primary baked byte-identical to the proven legacy clip for ~100 frames and then diverged. Save every bone's
    # pose (and rotation mode) and RESTORE it before returning, so role processing is invisible to every other export.
    saved = {pb.name: (pb.location.copy(), pb.rotation_quaternion.copy(), pb.scale.copy(), pb.rotation_mode) for pb in arm.pose.bones}
    assign_action(src)
    for pb in arm.pose.bones:
        pb.rotation_mode = 'QUATERNION'
    snap = {}
    lo, hi = min(frames), max(frames)
    for f in range(lo, hi + 1):                          # snapshot the evaluated basis over the span once
        bpy.context.scene.frame_set(f)
        bpy.context.view_layer.update()
        snap[f] = {pb.name: pb.matrix_basis.decompose() for pb in arm.pose.bones}
    a = bpy.data.actions.new(new_name)
    assign_action(a)
    try: arm.animation_data.action_slot = a.slots.new(id_type='OBJECT', name=arm.name)
    except Exception: pass
    for i, f in enumerate(frames):
        for pb in arm.pose.bones:
            loc, rot, _s = snap[f][pb.name]
            pb.location = loc
            pb.rotation_quaternion = rot
            pb.keyframe_insert('location', frame=i)
            pb.keyframe_insert('rotation_quaternion', frame=i)
    for pb in arm.pose.bones:                            # restore the found pose exactly (mode LAST-set wins, so set it first)
        if pb.name in saved:
            l, q, s, mode = saved[pb.name]
            pb.rotation_mode = mode
            pb.location = l; pb.rotation_quaternion = q; pb.scale = s
    print("RIGANIM sliced '%s' -> '%s' (%d frames%s)" % (spec, new_name, len(frames), ", reversed" if f1 < f0 else ""))
    return a

# pick an action: the named clip (slice-aware) if given, else the one already on the armature, else the first
act = None
if clip_name:
    act = resolve_clip(clip_name, "primary")
if act is None and arm.animation_data and arm.animation_data.action:
    act = arm.animation_data.action
if act is None and len(bpy.data.actions):
    act = bpy.data.actions[0]
if act is None:
    print("RIGANIM ERROR: no animation action found in the model")
    sys.exit(1)
assign_action(act)
# resolve the state-role clips (slice-aware) BEFORE pruning actions (a role may share the primary clip)
role_acts = {}   # role -> action
for _r, _cn in role_specs:
    role_acts[_r] = resolve_clip(_cn, _r)
keep_names = set([act.name] + [a.name for a in role_acts.values()])
for a in list(bpy.data.actions):
    if a.name not in keep_names:
        try: bpy.data.actions.remove(a)
        except Exception: pass
all_acts = [act] + [a for a in dict.fromkeys(role_acts.values()) if a is not act]   # unique, primary first
print("RIGANIM action '%s'%s" % (act.name, (" + %d state clip(s)" % (len(all_acts) - 1)) if len(all_acts) > 1 else ""))

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

# OPTIONAL: keep animation only on bones matching a prefix (strip camera pans / root-bob that cause wobble).
# Applied to EVERY exported clip (primary + state roles) so no role can smuggle back a stripped bone.
if prefixes:
    def bone_of(dp):
        if 'pose.bones[' not in dp: return None
        return dp.split('["', 1)[1].split('"]', 1)[0]
    for _fa in all_acts:
        owners = all_fcurve_owners(_fa)   # snapshot: safe to remove from the owning collections while iterating this list
        avail = sorted({bone_of(fc.data_path) for _, fc in owners if bone_of(fc.data_path) is not None})
        kept = rem = 0
        for coll, fc in owners:
            b = bone_of(fc.data_path)
            if b is not None and any(b.startswith(p) for p in prefixes):
                kept += 1
            else:
                coll.remove(fc); rem += 1
        print("RIGANIM bone-filter %s on '%s': kept %d fcurves, removed %d" % (prefixes, _fa.name, kept, rem))
        # T6: if the prefix matched NOTHING, every fcurve was stripped -> a frozen 1-frame clip would bake and ship
        # with exit 0 (silent). Hard-fail instead, listing the animated bones so the prefix can be corrected.
        if kept == 0:
            print("RIGANIM ERROR: bone-filter %s matched no animated bone in '%s' — every fcurve was stripped, the clip would be frozen. Animated bones: %s" % (prefixes, _fa.name, avail))
            sys.exit(1)

# FOLD FRAME-0 BONE LOCATIONS INTO THE REST POSE — auto-rigged models (the Combine soldier) often park a bone's REST
# somewhere else and hold it in place with a constant corrective location key in every clip. Amplitude can't play
# location keys (see the strip below), so without this fold the correction is lost and the part sits rigidly displaced
# (the soldier's head). Fix: evaluate each location-keyed bone at the clip's first frame, convert that offset to
# armature space via the bone's rest orientation, and move the bone's REST (head+tail, whole subtree — pose location
# shifts descendants too, and offsets from nested keyed bones compose additively since they're pure translations).
# After the fold, frame-0 pose == rest, and the rotation-only clip plays around the corrected pivots.
_rest_applied = False   # set once armature_apply rewrites the rest — after that, a failure must abort the bake
try:
    _loc0 = {}
    for _coll, _fc in all_fcurve_owners(act):
        _dp = _fc.data_path
        if _dp.startswith("pose.bones") and _dp.endswith(".location"):
            _b = _dp.split('["', 1)[1].split('"]', 1)[0]
            _loc0.setdefault(_b, [0.0, 0.0, 0.0])[_fc.array_index] = _fc.evaluate(act.frame_range[0])
    if _loc0 and convert_rig:
        # REST NORMALIZATION + VISUAL REBAKE — CONVERSION PATH ONLY (gating decision 2026-07-19). This block rewrites
        # the rest pose and re-derives the whole clip: exactly the manipulation the legacy contract promises NOT to
        # do, and it used to run on location-key PRESENCE alone — so a legacy re-bake of any deploy_convert output
        # (nla.bake writes location keys on every bone) silently routed through it, and after the hard-fail hardening
        # a legacy model with location keys + shape keys ABORTED instead of baking. Legacy rigs have a sane rest by
        # definition (that's what makes them legacy); the fold at a sane rest is a near-no-op they don't need. The
        # location-STRIP below stays on BOTH paths deliberately: every verified legacy bake (drone, howitzer) went
        # through it, and un-stripping could re-introduce the drone's unscaled-translation wobble.
        # Auto-rigs can ship a SCRAMBLED rest pose that the clip's location keys
        # ASSEMBLE into the actual body every frame (the Combine soldier: frame-0 posed positions sit up to 91 units
        # from their rests on a 73-unit rig — the 129 location curves are structural, not decorative). Amplitude
        # can't play location keys, so the fix is to make the clip's FIRST VISUAL POSE the new rest and re-derive the
        # animation against it:
        #   1. at frame 0: apply the armature modifier on the meshes (bakes the assembled body as the new bind shape)
        #   2. Apply Pose As Rest on the armature (the assembled pose becomes the rest pose)
        #   3. re-add the armature modifiers (re-bind the assembled mesh to the assembled rest)
        #   4. re-bake the action with VISUAL KEYING (curves re-derived relative to the new rest — translations
        #      collapse to ~0, rotations stay true), then the location-strip below removes the residue.
        _fs0 = int(act.frame_range[0]); _fe0 = int(act.frame_range[1])
        # SNAPSHOT the true visual pose of every bone on every frame of EVERY exported clip FIRST (original rig +
        # original actions) — everything after this destroys the old reference frame. All clips share ONE canonical
        # rest (the PRIMARY clip's frame 0): a per-clip rest would displace every non-primary clip on the shared
        # skeleton (Phase 2, 2026-07-19).
        _snaps = {}   # action -> {frame -> {bone -> world matrix}}
        for _sa in all_acts:
            assign_action(_sa)
            _sfs = int(_sa.frame_range[0]); _sfe = int(_sa.frame_range[1])
            _snaps[_sa] = {}
            for _f in range(_sfs, _sfe + 1):
                bpy.context.scene.frame_set(_f)
                bpy.context.view_layer.update()
                _snaps[_sa][_f] = {pb.name: pb.matrix.copy() for pb in arm.pose.bones}
        _snap = _snaps[act]   # the primary clip's snapshots (rest source + residual check)
        assign_action(act)
        bpy.context.scene.frame_set(_fs0)
        bpy.context.view_layer.update()
        _rebind = []
        for _mo in [o for o in bpy.context.scene.objects if o.type == 'MESH']:
            for _md in [m for m in _mo.modifiers if m.type == 'ARMATURE']:
                bpy.ops.object.select_all(action='DESELECT')
                _mo.select_set(True); bpy.context.view_layer.objects.active = _mo
                # modifier_apply refuses multi-user mesh data ("Modifiers cannot be applied to multi-user data") —
                # instanced duplicates (wheels, blades) hit this. Single-user-ize first, same as prep_model.py does.
                if _mo.data.users > 1:
                    _mo.data = _mo.data.copy()
                try:
                    bpy.ops.object.modifier_apply(modifier=_md.name)
                    _rebind.append(_mo.name)
                except Exception as _e2:
                    # HARD-FAIL: continuing would let armature_apply below rewrite the rest pose underneath this
                    # mesh's still-old bind — a permanently deformed export shipped with exit 0 (review 2026-07-19).
                    print("RIGANIM ERROR: could not apply armature modifier on %s (%s) — aborting: a partial rest-fold would silently mis-bake the model" % (_mo.name, _e2))
                    sys.exit(1)
        bpy.ops.object.select_all(action='DESELECT')
        arm.select_set(True); bpy.context.view_layer.objects.active = arm
        bpy.ops.object.mode_set(mode='POSE')
        bpy.ops.pose.select_all(action='SELECT')
        bpy.ops.pose.armature_apply(selected=False)          # frame-0 visual pose -> new rest
        _rest_applied = True                                 # PAST THE POINT OF NO RETURN: the rest is rewritten
        bpy.ops.object.mode_set(mode='OBJECT')
        for _mn in _rebind:
            _mo = bpy.context.scene.objects.get(_mn)
            if _mo is not None:
                _nm = _mo.modifiers.new("Armature", 'ARMATURE'); _nm.object = arm
        # MANUAL visual rebake from the pre-apply snapshots (an nla.bake at this point would sample the old keys
        # double-applied on the new rest). New local basis per frame, pure matrix math:
        #   local_f(bone)  = parentWorld_f^-1 @ world_f          (armature-space snapshots)
        #   basis_f(bone)  = newRestLocal^-1 @ local_f           (Blender: poseLocal = restLocal @ basis)
        # Only the ROTATION of basis_f is written (Amplitude is rotation-only; the primary's frame-0 basis ==
        # identity by construction, so the rest IS the primary's first frame). EVERY clip (primary + state roles)
        # is rebaked against the SAME rest here — that shared reference is what lets all role ClipCollections play
        # on one baked skeleton.
        _parent_of = {b.name: (b.parent.name if b.parent else None) for b in arm.data.bones}
        _rest_local = {}
        for _b3 in arm.data.bones:
            if _b3.parent: _rest_local[_b3.name] = _b3.parent.matrix_local.inverted() @ _b3.matrix_local
            else: _rest_local[_b3.name] = _b3.matrix_local.copy()
        for pb in arm.pose.bones:
            pb.rotation_mode = 'QUATERNION'
        _rebaked = {}   # old action -> rebaked action
        for _oa in all_acts:
            _na = bpy.data.actions.new(_oa.name + "_rebaked")
            arm.animation_data.action = _na
            try: arm.animation_data.action_slot = _na.slots.new(id_type='OBJECT', name=arm.name)
            except Exception: pass
            _frames = sorted(_snaps[_oa].keys())
            # SINGLE-FRAME clip (a held stance like CombatIdle1, range 0..0): also key the same pose at frame+1 —
            # a zero-length animation can be dropped whole by Unity's FBX importer, which would fail the role's
            # ClipCollection bake downstream. Two identical frames = a valid (visually static) clip everywhere.
            if len(_frames) == 1:
                _frames = [_frames[0], _frames[0] + 1]
                _snaps[_oa][_frames[1]] = _snaps[_oa][_frames[0]]
                print("RIGANIM single-frame clip '%s' padded to 2 identical frames (stance)" % _oa.name)
            for _f in _frames:
                _world = _snaps[_oa][_f]
                for pb in arm.pose.bones:
                    _pn = _parent_of[pb.name]
                    _localf = (_world[_pn].inverted() @ _world[pb.name]) if _pn else _world[pb.name]
                    _basis = _rest_local[pb.name].inverted() @ _localf
                    pb.rotation_quaternion = _basis.to_quaternion()
                    pb.keyframe_insert("rotation_quaternion", frame=_f)
            _rebaked[_oa] = _na
        act = _rebaked[all_acts[0]]
        role_acts = {r: _rebaked[a] for r, a in role_acts.items()}
        all_acts = [act] + [a for a in dict.fromkeys(role_acts.values()) if a is not act]
        assign_action(act)
        # VERIFY: at frame 0 the evaluated PRIMARY pose must coincide with the rest
        bpy.context.scene.frame_set(_fs0)
        bpy.context.view_layer.update()
        _worst = 0.0
        for pb in arm.pose.bones:
            _d = (pb.matrix.translation - arm.data.bones[pb.name].matrix_local.translation).length
            if _d > _worst: _worst = _d
        print("RIGANIM rest-normalized + rebaked %d clip(s) x %d bones (%d meshes re-bound); primary frame-0 residual = %.6f (should be ~0)" % (len(all_acts), len(arm.pose.bones), len(_rebind), _worst))
except Exception as _e:
    try: bpy.ops.object.mode_set(mode='OBJECT')
    except Exception: pass
    # Failed BEFORE the rest rewrite: the rig is untouched, continuing with a plain location-strip is safe.
    # Failed AFTER (rest already rewritten, clip rebake incomplete): the export would pair the NEW rest with the OLD
    # (or a partial) clip — a silent mis-bake with exit 0. Fail the bake loudly instead (review 2026-07-19).
    if _rest_applied:
        print("RIGANIM ERROR: rest-fold failed AFTER the rest pose was rewritten (%s) — aborting: exporting now would ship a half-normalized rig" % _e)
        sys.exit(1)
    print("RIGANIM WARN: rest-fold failed (%s) — location keys will just be stripped" % _e)

# STRIP BONE-TRANSLATION CURVES — Amplitude clips are effectively ROTATION-ONLY: its clip bake reads translation keys
# at the FBX's NATIVE scale, bypassing the importer's unit conversion, so on a Fix-100x rig a 2 cm neck bob becomes
# ~2 world units and the head rips off in-game ("the movement gets exaggerated") while Unity's own preview plays the
# same FBX fine. Rotations are scale-free; bone REST offsets come from the (properly scaled) skeleton, so a
# rotation-driven rig looks identical without these curves. (This generalizes the drone's old wobble fix, which
# stripped to rotation-only 'prop' curves by hand.) DELIBERATELY UNGATED — runs on BOTH paths: every verified legacy
# bake went through it, and Amplitude can't play the keys anyway (the 2026-07-19 gating decision moved only the
# destructive rest-fold above behind the convert flag).
_locs = 0
for _sa in all_acts:
    for coll, fc in all_fcurve_owners(_sa):
        if fc.data_path.startswith("pose.bones") and fc.data_path.endswith(".location"):
            coll.remove(fc); _locs += 1
if _locs:
    print("RIGANIM stripped %d bone-LOCATION fcurves across %d clip(s) (Amplitude clips are rotation-only; translations bake unscaled)" % (_locs, len(all_acts)))

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
if convert_rig:
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
# Blender auto-syncs vertex groups + THE ASSIGNED action's fcurve paths on rename; the bone-filter above already ran
# on the ORIGINAL names. Gated to the CONVERSION path: legacy stays the byte-identical pipeline.
if convert_rig:
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
    # THE FROZEN-RUNNER FIX (2026-07-19): Blender's rename syncs fcurve data_paths ONLY for the action ASSIGNED at
    # rename time — every DORMANT state-role action kept its OLD bone names, its curves then targeted nonexistent
    # bones, evaluated to NOTHING at export, and the role FBX baked an 18-frame CONSTANT clip (in-game: the soldier
    # frozen mid-stride while moving; pose-data byte analysis showed all 63 curves constant). Patch every kept
    # action's paths explicitly — idempotent: only OLD names are in the map, already-synced paths pass through.
    _newname = {n: "b%03d_%s" % (i, n) for i, n in enumerate(_order)}
    _patched = 0
    for _pa in all_acts:
        for _coll2, _fc2 in all_fcurve_owners(_pa):
            _dp2 = _fc2.data_path
            if 'pose.bones["' in _dp2:
                _bn2 = _dp2.split('["', 1)[1].split('"]', 1)[0]
                if _bn2 in _newname:
                    _fc2.data_path = _dp2.replace('pose.bones["%s"]' % _bn2, 'pose.bones["%s"]' % _newname[_bn2])
                    _patched += 1
    if _patched:
        print("RIGANIM patched %d dormant fcurve path(s) onto the renamed bones (state-role clips)" % _patched)

# RIG ROTATION + TRANSFORM FOLD — CONVERSION path only (legacy = the EXACT old pipeline, byte-for-byte: no object
# fiddling, no fold — models that were correct before stay correct; the fold is world-preserving for Unity's mesh
# import but NOT for Amplitude's skeleton bake, so folding unconditionally flipped the previously-good howitzer
# upside-down in-game). On the conversion path: rotate the parent-less objects in world space (identity when rotation
# is 0,0,0 — the fold/strip still run, they are what makes the clean-unit export sound), strip OBJECT-level animation
# fcurves (a glTF often keys the armature NODE itself — they block transform_apply and re-assert the old orientation
# each frame), then transform_apply INTO THE DATA (vertices + bone rests, identity nodes) — object-level rotation
# alone is dropped downstream (proven in-game on the Combine soldier).
if convert_rig:
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
# - CONVERSION path: transform_apply normalized objects to scale 1, so pre-divide with
#   global_scale=0.01 -> net node scale 1, UnitScaleFactor 1, bind clusters 1 — the clean drone profile, by design.
# - LEGACY path: keep the exporter untouched, byte-identical output (the working models' contract).
gscale = 0.01 if convert_rig else 1.0
if gscale != 1.0:
    print("RIGANIM export global_scale=0.01 (cancels the exporter's m->cm x100 root scaling)")
# EXPORT — the primary clip to outp, then each STATE ROLE'S clip to a sibling anim_<role>/ folder (same prepared
# rig/mesh, only the assigned action + frame range differ; bake_anim_use_all_actions=False bakes the ACTIVE action
# only, so each FBX carries exactly one take and each ClipCollection folder scan sees exactly one clip).
def _export_one(_a, _o):
    assign_action(_a)
    _fs, _fe = [int(round(v)) for v in _a.frame_range]
    bpy.context.scene.frame_start = _fs
    bpy.context.scene.frame_end = _fe
    # PIN THE EXPORT-TIME POSE (2026-07-19): the FBX's node defaults — which downstream become the imported
    # prefab's default pose and thereby the REFERENCE the engine encodes clips against — used to be whatever
    # frame the last processing step happened to leave the scene on (arbitrary). Evaluate the clip's own first
    # frame before every export so the reference is deterministic: the primary's frame 0 = the travel/rest pose.
    bpy.context.scene.frame_set(_fs)
    bpy.context.view_layer.update()
    _d = os.path.dirname(_o)
    if _d and not os.path.isdir(_d):
        os.makedirs(_d)
    bpy.ops.export_scene.fbx(filepath=_o, use_selection=False, add_leaf_bones=False, global_scale=gscale,
                             bake_anim=True, bake_anim_use_all_actions=False,
                             bake_anim_use_nla_strips=False, object_types={'ARMATURE', 'MESH'})
    print("RIGANIM wrote %s ('%s', frames %d..%d)" % (_o, _a.name, _fs, _fe))
_export_one(act, outp)
for _r, _cn in role_specs:
    _ro = os.path.join(os.path.dirname(os.path.dirname(outp)), "anim_" + _r, os.path.basename(outp))
    _export_one(role_acts[_r], _ro)
