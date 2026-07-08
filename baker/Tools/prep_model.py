# prep_model.py — Model Factory pre-bake prep, in ONE headless Blender session: import a model (glb/gltf/obj/fbx),
# optionally STRIP named objects (+ their children), optionally REDUCE (per-object quadric collapse) to ~targetTris,
# and export a single GLB the Factory then bakes. Combining strip + reduce into one process (vs the old two) saves a
# Blender startup + an intermediate GLB export/import round-trip — ~24% off a heavy model's Blender time.
#
# Strip: case-insensitive substring match on object names — e.g. a helicopter's own rotor so the donor's spinning
#        rotor shows through, or a crew figure / weapon pod. Empty list = keep everything.
# Reduce: pure quadric COLLAPSE (not planar dissolve — dissolve flattens gentle curves like a hovercraft skirt).
#        Per-object so thin parts survive. targetTris is a CEILING (ratio clamps to 1.0 — never adds geometry).
#        target <= 0 = skip reduction.
#
# Invoked by UniversalBaker.PrepViaBlender:
#   blender --background --python prep_model.py -- <in> <out.glb> <comma,substrings|empty> <targetTris|0>

import bpy, sys, os

argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp = argv[0], argv[1]
subs = [s.strip().lower() for s in (argv[2] if len(argv) > 2 else "").split(",") if s.strip()]
target = int(argv[3]) if len(argv) > 3 else 0

bpy.ops.wm.read_factory_settings(use_empty=True)

ext = os.path.splitext(inp)[1].lower()
if ext in (".glb", ".gltf"):
    bpy.ops.import_scene.gltf(filepath=inp)
elif ext == ".fbx":
    bpy.ops.import_scene.fbx(filepath=inp)
elif ext == ".obj":
    try:
        bpy.ops.wm.obj_import(filepath=inp)          # Blender 3.3+
    except Exception:
        bpy.ops.import_scene.obj(filepath=inp)       # legacy
else:
    print("PREP_ERR unsupported input extension '%s'" % ext); sys.exit(1)

# --- STRIP: remove matched objects + all descendants (so stripping a group empty takes the whole sub-tree) ---
if subs:
    victims = set()
    for o in list(bpy.data.objects):
        if any(s in o.name.lower() for s in subs):
            victims.add(o)
            for c in o.children_recursive:
                victims.add(c)
    removed = sorted(v.name for v in victims)
    for v in victims:
        bpy.data.objects.remove(v, do_unlink=True)
    print("PREP strip: removed %d object(s) for %s: %s" % (len(removed), subs, ", ".join(removed[:50])))
    if not removed:
        print("PREP WARNING: no object name matched %s — nothing was stripped (check the names)" % subs)

# --- REDUCE: per-object quadric collapse to ~target total triangles ---
# Count TRIANGLES, not polygons: a quad is 1 polygon but 2 triangles (an n-gon is n-2), and the decimate
# COLLAPSE ratio operates on the TRIANGULATED count (verified on Blender 5.1.2). Computing the ratio from
# len(polygons) made quad-topology sources (OBJ/FBX/.blend; GLB is always triangles) under-reduce by up to
# 2x — a 20k-quad model (40k real tris) with target 24000 got ratio 1.0 = NO reduction, blowing straight
# past the engine's shared vertex-buffer ceiling. The engine budget is triangles, so we count triangles.
def tri_count(me):
    return sum(len(p.vertices) - 2 for p in me.polygons)

if target > 0:
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    if not meshes:
        print("PREP_ERR no meshes to reduce"); sys.exit(1)
    total = sum(tri_count(o.data) for o in meshes)
    ratio = min(1.0, max(0.001, target / max(1, total)))
    before = after = 0
    for o in meshes:
        before += tri_count(o.data)
        # Instanced (multi-user) mesh data: modifier_apply refuses it ("Modifiers cannot be applied to multi-user
        # data" — verified on Blender 5.1.2), which aborted the whole bake; and applying through each user would
        # decimate the shared datablock once per user anyway. Give each object its own copy first. Linked duplicates
        # are common in downloaded models (wheels, missiles, rotor blades instanced from one mesh).
        if o.data.users > 1:
            o.data = o.data.copy()
        bpy.ops.object.select_all(action='DESELECT')
        o.select_set(True)
        bpy.context.view_layer.objects.active = o
        m = o.modifiers.new("dec", 'DECIMATE')
        m.decimate_type = 'COLLAPSE'
        m.ratio = ratio
        bpy.ops.object.modifier_apply(modifier=m.name)
        after += tri_count(o.data)
    print("PREP reduce: tris %d -> %d (target %d, ratio %.4f)" % (before, after, target, ratio))

bpy.ops.export_scene.gltf(filepath=outp, export_format='GLB', use_selection=False)
print("PREP wrote %s" % outp)
