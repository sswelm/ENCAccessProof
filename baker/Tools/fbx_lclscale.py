import sys
sys.path.append(r"C:\Program Files\Blender Foundation\Blender 5.1\5.1\scripts\addons_core")
from io_scene_fbx import parse_fbx
def dec(x): return x.decode(errors='replace') if isinstance(x, bytes) else str(x)
for f in sys.argv[sys.argv.index("--")+1:]:
    print("=====", f.split("/")[-1])
    root, ver = parse_fbx.parse(f)
    def walk(elem, model):
        name = dec(elem.id)
        if name == "Model" and elem.props and len(elem.props) > 1:
            model = dec(elem.props[1])
        if name == "P" and elem.props and dec(elem.props[0]) == "Lcl Scaling":
            vals = [round(float(x),5) for x in elem.props[4:] if isinstance(x,(int,float))]
            if any(abs(v-1.0) > 1e-3 for v in vals):
                print("  %-44s Lcl Scaling %s" % (model[:44], vals))
        for c in elem.elems:
            walk(c, model)
    walk(root, "")
    print("  (end)")
