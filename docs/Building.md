# Building the plugin

For adopters who just want to *use* the Factory, no build is needed — grab a released `HumankindAssetFramework.dll`. Build only if
you're changing the plugin.

## Plugin (`HumankindAssetFramework.dll`)

Needs the **.NET SDK**. The project file is still named `ENCAccessProof.csproj` (historical; the ASSEMBLY it builds
is HAF-branded). Put a `References\` folder next to the `.csproj` containing:

- `BepInEx.dll` + `0Harmony.dll` — from `<Humankind>\BepInEx\core\`
- `UnityEngine*.dll` + `Amplitude.Mercury.Animation.dll` + `Newtonsoft.Json.dll` — from `<Humankind>\Humankind_Data\Managed\`
  (Newtonsoft is provided by the game at runtime — used for robust registry parsing, since `UnityEngine.JsonUtility`
  silently returns empty in the game's Mono runtime; `Private=false`, so it's not copied into the built plugin)

Then:

```
dotnet build -c Release
```

and copy `bin\Release\HumankindAssetFramework.dll` → `<Humankind>\BepInEx\plugins\`.

## Blender (optional dependency)

**Blender** is needed for `.blend` import, **animated-model import**, **Strip parts**, and Reduce-to-tris decimation —
auto-detected under `Program Files`, or point the Factory Settings override / `EditorPrefs 'ENC.blenderPath'` at
`blender.exe`. Static GLB/OBJ/FBX bakes with neither Strip nor Reduce need **no** Blender: the GLB path uses the
self-contained `Tools/glbconv/glbconv.exe` (no .NET install required, and its `Convert grid` option decimates without
Blender). A `dotnet glbconv.dll` fallback exists for local dev.

### Why Blender does the geometry work (design rationale)

The heavy geometry passes shell out to headless Blender (`Tools/prep_model.py`, `rig_anim.py`, `blend_export.py`)
rather than being written in C#. The scripts are Python, but **Python is only the remote control** — the actual
decimation/import/export executes inside Blender's C/C++ core:

- **Quadric edge-collapse decimation is hard to write well** (error quadrics, topology preservation, UV/normal
  attribute handling). Blender's Decimate modifier is a battle-tested implementation; the script that drives it is
  ~15 lines. The C# alternative that predates it — `glbconv`'s `Convert grid` vertex clustering — survives as the
  Blender-free fallback, but clustering averages UVs across seams (scrambles textured skins) and eats thin features,
  which is exactly why the COLLAPSE path was added.
- **Blender was already a hard dependency** for `.blend` conversion and the animated pipeline (armature slimming,
  clip baking, FBX export) — Strip/Reduce ride an install the modder already has for those.
- **Performance:** the language is irrelevant to speed here. The per-bake cost is Blender **process startup + model
  import/export round-trip** (measured: the vast majority of a ~33s prep pass on a 13.6 MB model), not execution.
  That's why strip+reduce were merged into ONE Blender session (one startup, one round-trip, ~24% faster) instead of
  optimizing any code.
- **Future (package-readiness):** the static pipeline could go fully C# — an in-process quadric simplifier (e.g. a
  UnityMeshSimplifier-class library) plus node-name filtering inside `glbconv` would eliminate both the Blender
  dependency for static models *and* the round-trip cost. Blender would remain only for what genuinely needs a DCC:
  `.blend` files and animation. Tracked in `Framework-Review.md`.

## Editor tooling (`baker/`)

The Model Factory editor scripts are mirrored in `baker/` (the live copies compile inside the ENCReload Unity
project, which tracks only its `Databases` in git). Drop them into a Unity project that has the Humankind SDK to get the
**Tools ▸ Model Factory** window.
