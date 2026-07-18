import sys
sys.path.append(r"C:\Program Files\Blender Foundation\Blender 5.1\5.1\scripts\addons_core")
from io_scene_fbx import parse_fbx

def dec(x): return x.decode(errors='replace') if isinstance(x, bytes) else str(x)

for f in sys.argv[sys.argv.index("--")+1:]:
    print("=====", f.split("/")[-1])
    root, ver = parse_fbx.parse(f)
    unit = None; clusters = []
    def walk(elem, ctx):
        name = dec(elem.id)
        if name == "P" and elem.props and dec(elem.props[0]) == "UnitScaleFactor":
            vals = [float(x) for x in elem.props[4:] if isinstance(x,(int,float))]
            print("  UnitScaleFactor:", vals)
        if name == "Deformer" and elem.props and len(elem.props) > 2 and dec(elem.props[2]) == "Cluster":
            cname = dec(elem.props[1])
            for c in elem.elems:
                if dec(c.id) == "Transform" and c.props:
                    m = c.props[0]
                    try:
                        arr = list(m)
                        # scale magnitude of first basis vector of the 4x4 (row-major 16 floats)
                        import math
                        sx = math.sqrt(arr[0]**2 + arr[1]**2 + arr[2]**2)
                        clusters.append((cname[:40], round(sx,4)))
                    except Exception as e:
                        clusters.append((cname[:40], "err"))
        for c in elem.elems:
            walk(c, ctx)
    walk(root, None)
    seen = {}
    for n, s in clusters: seen.setdefault(s, []).append(n)
    for s, names in sorted(seen.items(), key=lambda kv: str(kv[0])):
        print("  cluster bind scale %s : %d bones (e.g. %s)" % (s, len(names), names[0]))
