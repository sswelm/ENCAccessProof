import bpy, math
bpy.ops.wm.read_factory_settings(use_empty=True)

# Armature: chain of 12 bones along +X, each 0.5 long, named chain-order-safe (a01..a12)
arm_data = bpy.data.armatures.new("LitmusArm")
arm = bpy.data.objects.new("LitmusArm", arm_data)
bpy.context.scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
N = 12; L = 0.5
prev = None
for i in range(N):
    eb = arm_data.edit_bones.new("a%02d" % (i+1))
    eb.head = (i*L, 0, 0); eb.tail = ((i+1)*L, 0, 0)
    if prev: eb.parent = prev; eb.use_connect = True
    prev = eb
bpy.ops.object.mode_set(mode='OBJECT')

# One cube per bone, skinned 100% to it; distinct hue per segment via vertex-color-free simple materials
mesh_objs = []
for i in range(N):
    bpy.ops.mesh.primitive_cube_add(size=0.4, location=(i*L + L/2, 0, 0))
    cube = bpy.context.active_object
    cube.name = "seg%02d" % (i+1)
    mat = bpy.data.materials.new("m%02d" % (i+1))
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    h = i / N
    import colorsys
    r,g,b = colorsys.hsv_to_rgb(h, 1, 1)
    bsdf.inputs["Base Color"].default_value = (r, g, b, 1)
    cube.data.materials.append(mat)
    vg = cube.vertex_groups.new(name="a%02d" % (i+1))
    vg.add(list(range(len(cube.data.vertices))), 1.0, 'REPLACE')
    mod = cube.modifiers.new("Armature", 'ARMATURE'); mod.object = arm
    cube.parent = arm
    mesh_objs.append(cube)

# Join cubes into ONE mesh (multi-material) so the pipeline treats it like a normal model
bpy.ops.object.select_all(action='DESELECT')
for o in mesh_objs: o.select_set(True)
bpy.context.view_layer.objects.active = mesh_objs[0]
bpy.ops.object.join()

# Minimal clip: bone a01 rocks a little so there IS an animation to bake
act = bpy.data.actions.new("litmus")
arm.animation_data_create(); arm.animation_data.action = act
try: arm.animation_data.action_slot = act.slots.new(id_type='OBJECT', name="LitmusArm")
except Exception: pass
pb = arm.pose.bones["a01"]
pb.rotation_mode = 'XYZ'
for f, ang in ((1, 0.0), (30, 0.15), (60, 0.0)):
    pb.rotation_euler = (0, 0, ang)
    pb.keyframe_insert("rotation_euler", frame=f)

bpy.ops.export_scene.gltf(filepath=r"C:\tmp\litmus.glb", export_yup=True)
print("LITMUS written")
