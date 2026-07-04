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
# Usage: blender -b --python rig_anim.py -- <input> <output.fbx> <targetTris> [bonePrefixesCSV] [clipName] [albedoOut.png]
import bpy, sys, os

argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp, target = argv[0], argv[1], int(argv[2])
prefixes = [p.strip() for p in (argv[3] if len(argv) > 3 else "").split(",") if p.strip()]
clip_name = (argv[4] if len(argv) > 4 else "").strip()
albedo_out = argv[5] if len(argv) > 5 else ""

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
    kept = rem = 0
    for coll, fc in all_fcurve_owners(act):
        b = bone_of(fc.data_path)
        if b is not None and any(b.startswith(p) for p in prefixes):
            kept += 1
        else:
            coll.remove(fc); rem += 1
    print("RIGANIM bone-filter %s: kept %d fcurves, removed %d" % (prefixes, kept, rem))

# clamp scene frame range to the action's real range (else bake_anim pads a frozen tail -> ~1s stall per loop)
fs, fe = [int(round(v)) for v in act.frame_range]
bpy.context.scene.frame_start = fs
bpy.context.scene.frame_end = fe
print("RIGANIM frame range %d..%d" % (fs, fe))

# export the first material's base-colour image as the albedo PNG (for the Factory atlas)
if albedo_out:
    try:
        img = None
        for o in bpy.context.scene.objects:
            if o.type != 'MESH' or not o.data.materials: continue
            m = o.data.materials[0]
            if m and m.use_nodes:
                for n in m.node_tree.nodes:
                    if n.type == 'TEX_IMAGE' and n.image:
                        img = n.image; break
            if img: break
        if img:
            img.filepath_raw = albedo_out
            img.file_format = 'PNG'
            img.save()
            print("RIGANIM albedo ->", albedo_out)
        else:
            print("RIGANIM no albedo image found (model may be untextured)")
    except Exception as e:
        print("RIGANIM albedo export warn:", e)

# join to 1 mesh + 1 material + decimate (KEEP the armature + skin weights)
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
if not meshes:
    print("RIGANIM ERROR: no mesh to export"); sys.exit(1)
bpy.ops.object.select_all(action='DESELECT')
for o in meshes: o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1:
    bpy.ops.object.join()
joined = bpy.context.view_layer.objects.active
me = joined.data
while len(me.materials) > 1:
    me.materials.pop(index=len(me.materials) - 1)
for p in me.polygons:
    p.material_index = 0
total = sum(len(p.vertices) - 2 for p in me.polygons)
ratio = min(1.0, max(0.02, target / max(1, total)))
mdec = joined.modifiers.new("dec", 'DECIMATE')
mdec.decimate_type = 'COLLAPSE'; mdec.ratio = ratio; mdec.use_collapse_triangulate = True
bpy.ops.object.select_all(action='DESELECT'); joined.select_set(True); bpy.context.view_layer.objects.active = joined
bpy.ops.object.modifier_apply(modifier=mdec.name)
print("RIGANIM decimate %d -> %d tris (ratio %.3f)" % (total, sum(len(p.vertices) - 2 for p in me.polygons), ratio))

bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=outp, use_selection=False, add_leaf_bones=False,
                         bake_anim=True, bake_anim_use_all_actions=False,
                         bake_anim_use_nla_strips=False, object_types={'ARMATURE', 'MESH'})
print("RIGANIM wrote", outp)
