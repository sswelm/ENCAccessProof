// UniversalBaker.cs (ENC editor) — the ENC Model Factory's bake core. Takes any model file (.glb via the bundled
// converter, or .obj/.fbx natively) + ALL the knobs we discovered while getting models to work (rotation, position,
// size, normals mode, smoothing angle, conversion grid) and bakes an Amplitude Skeleton (+ atlas) for the runtime
// UniversalInject patch. Generalizes StealthCruiserModel; every "thing we did to make it work" is a parameter here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public enum NormalsMode { KeepModel = 0, Recalculate = 1, Faceted = 2 }

public struct BakeConfig
{
    public string resourceName;     // unique id — names all baked assets
    public string modelFile;        // .glb/.obj/.fbx; empty = reuse the OBJ already in the resource dir
    public string pawnDescription;  // target unit substring (registry only)
    public Vector3 rotationEuler;   // rotation offset on top of the auto longest-axis -> forward align
    public Vector3 positionOffset;  // x sway, y fore/aft, z waterline (− sinks)
    public float   size;            // world length of the model's longest axis
    public NormalsMode normals;     // KeepModel | Recalculate(smoothing) | Faceted
    public float   smoothingAngle;  // hard-edge threshold for Recalculate
    public int     convertGrid;     // GLB->OBJ: 0 = faithful (preserve UV seams), >0 = vertex-cluster decimate
    public bool    reuseExtracted;  // true = reuse the existing OBJ/albedo (skip re-import) — lets the modder hand-edit the extracted texture and keep it
    public bool    doubleSided;     // true = add a reversed back face to every triangle (single-sided/CAD repair) so backface-culled parts render in-game
    public bool    windingFix;      // true = rewind faces outward from the origin (documented CAD winding fix) so single-sided meshes render, no geometry doubling
    public bool    heightUV;        // true = override UVs with U=length, V=height so a vertical-gradient albedo maps by height (black skirt low, grey hull high)
    public int     targetTris;      // >0 = quadric-decimate the source to ~this many triangles (Blender) before baking, to fit the engine's shared vertex/index buffer
    public bool    animated;        // true = ANIMATED path: bake from the model's OWN armature + clip (Skeleton + ClipCollection), not the procedural vehicle rig
    public string  animClip;        // ANIMATED only: name of the clip to bake when the model has several (e.g. "hover"); empty = the assigned/first action
    public string  animateBones;    // ANIMATED only: comma-separated bone-name prefixes to keep animation on (e.g. "prop,rotor"); empty = keep the whole clip
}

public struct BakeResult
{
    public bool ok; public string error; public string skeletonGuid, atlasGuid, clipGuid; public Vector3 bbox;
}

public static class UniversalBaker
{
    public static BakeResult Build(BakeConfig cfg)
    {
        try { return BuildInner(cfg); }
        catch (Exception e) { Debug.LogError("[Factory] " + e); return new BakeResult { ok = false, error = e.Message }; }
    }

    // ANIMATED bake — a parallel pipeline that keeps the model's OWN armature + clip instead of the procedural
    // single-bone vehicle rig. Blender slims the rigged model (join+decimate, keep armature, clamp frame range, optional
    // bone strip, export albedo), Unity imports the FBX, then we bake an Amplitude Skeleton (from the FBX) and a
    // ClipCollection (from the folder) via the same reflected editor methods the SDK inspector uses. Returns skel + clip
    // + atlas guids for the registry ("clip" makes the runtime drive the pawn's pose onto our animation).
    public static BakeResult BuildAnimated(BakeConfig cfg)
    {
        try { return BuildAnimatedInner(cfg); }
        catch (Exception e) { Debug.LogError("[Factory] " + e); return new BakeResult { ok = false, error = e.Message }; }
    }

    static BakeResult BuildAnimatedInner(BakeConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.resourceName)) return Fail("resourceName is required");
        if (!BlenderAvailable()) return Fail("Animated import needs Blender installed (auto-detected, or set EditorPrefs 'ENC.blenderPath').");
        string name = cfg.resourceName;
        float size = cfg.size > 0f ? cfg.size : 5f;

        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        string resDir = "Assets/Resources/" + name;
        if (!AssetDatabase.IsValidFolder(resDir)) AssetDatabase.CreateFolder("Assets/Resources", name);
        // Bake the FBX into a DEDICATED subfolder that holds ONLY our clip. The ClipCollection's SetFromDirectory scans a
        // whole folder for animation FBX, so if we dropped it in resDir it would also pick up any OTHER fbx there (e.g. a
        // hand-made drone_rigged_slim.fbx) and bake a 2-clip collection — the runtime then resolves the wrong clip and the
        // model tears apart. Isolating the fbx makes the bake correct regardless of what else is in the resource folder.
        string animDir = resDir + "/anim";
        if (!AssetDatabase.IsValidFolder(animDir)) AssetDatabase.CreateFolder(resDir, "anim");
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string fsDir = Path.Combine(Application.dataPath, "Resources", name);
        Directory.CreateDirectory(fsDir);

        string fbxRel = animDir + "/" + name + "_anim.fbx";
        string fbxFull = Path.Combine(projRoot, fbxRel);

        // --- 1) Blender: slim the rigged model (keep armature + clip, clamp frame range, optional bone strip, albedo) ---
        if (!string.IsNullOrEmpty(cfg.modelFile) && (!cfg.reuseExtracted || !File.Exists(fbxFull)))
        {
            int target = cfg.targetTris > 0 ? cfg.targetTris : 12000;   // animated skins want to stay well under the shared buffer
            string albedoOut = Path.Combine(fsDir, name + "_albedo.png");
            if (!RigAnimViaBlender(cfg.modelFile, fbxFull, target, cfg.animateBones ?? "", cfg.animClip ?? "", albedoOut))
                return Fail("Blender animated slim failed (see console). Is the model rigged with the named animation clip?");
        }
        if (!File.Exists(fbxFull)) return Fail("no slim FBX at " + fbxRel + " — bake with a Model file first (Reuse extracted needs an existing one).");
        AssetDatabase.ImportAsset(fbxRel, ImportAssetOptions.ForceUpdate);

        // --- 2) import the FBX: Generic rig, import animation, scale so the longest axis ~= size ---
        var imp = AssetImporter.GetAtPath(fbxRel) as ModelImporter;
        if (imp == null) return Fail("could not get ModelImporter for " + fbxRel);
        imp.animationType = ModelImporterAnimationType.Generic;
        imp.importAnimation = true;
        imp.globalScale = 1f;
        imp.SaveAndReimport();
        var fbxGo = AssetDatabase.LoadAssetAtPath<GameObject>(fbxRel);
        float longest = MeasureLongestAxis(fbxGo);
        if (longest > 1e-4f)
        {
            imp.globalScale = size / longest;   // SDK skeleton uses the FBX native scale, so match `size` via Scale Factor
            imp.SaveAndReimport();
            fbxGo = AssetDatabase.LoadAssetAtPath<GameObject>(fbxRel);
            Debug.Log($"[Factory] {name} FBX scale factor {imp.globalScale:0.###} (native longest {longest:0.###} -> {size} units)");
        }
        if (fbxGo == null) return Fail("imported FBX has no GameObject");

        // --- 3) atlas from the exported albedo (same single-albedo path + Resources-root location as static) ---
        var atlas = BuildAtlas(resDir, name);
        string atlasPath = "Assets/Resources/" + name + "_Atlas.asset";
        AssetDatabase.DeleteAsset(atlasPath);
        AssetDatabase.CreateAsset(atlas, atlasPath);
        AssetDatabase.SaveAssets();

        // --- 4) bake Skeleton from the FBX's own armature + skinned mesh (SetPrefab + Reimport, as the SDK inspector does) ---
        var skelType = FindAmpType("Amplitude.Mercury.Animation.Skeleton");
        if (skelType == null) return Fail("Amplitude Skeleton type not found");
        string skelPath = "Assets/Resources/" + name + "_Skeleton.asset";
        AssetDatabase.DeleteAsset(skelPath);
        var skel = ScriptableObject.CreateInstance(skelType);
        AssetDatabase.CreateAsset(skel, skelPath);
        skelType.GetMethod("SetPrefab", new[] { typeof(GameObject) })?.Invoke(skel, new object[] { fbxGo });
        skelType.GetMethod("Reimport", Type.EmptyTypes)?.Invoke(skel, null);
        EditorUtility.SetDirty(skel);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        // --- 5) bake ClipCollection: set its skeleton guid, SetFromDirectory (populate clips), Reimport (bake poseData) ---
        var clipType = FindAmpType("Amplitude.Mercury.Animation.ClipCollection");
        if (clipType == null) return Fail("Amplitude ClipCollection type not found");
        string clipPath = "Assets/Resources/" + name + "_Clips.asset";
        AssetDatabase.DeleteAsset(clipPath);
        var clipColl = ScriptableObject.CreateInstance(clipType);
        AssetDatabase.CreateAsset(clipColl, clipPath);
        // the ClipCollection references its skeleton by Amplitude guid — set the private serialized field directly
        var adbType = FindAmpType("Amplitude.Framework.Asset.AssetDatabase");
        var skelGuidObj = adbType?.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) })?.Invoke(null, new object[] { skel });
        var skelField = clipType.GetField("skeleton", BindingFlags.NonPublic | BindingFlags.Instance);
        if (skelField == null || skelGuidObj == null) return Fail("could not bind ClipCollection.skeleton (field/guid missing)");
        skelField.SetValue(clipColl, skelGuidObj);
        clipType.GetMethod("SetFromDirectory", new[] { typeof(string) })?.Invoke(clipColl, new object[] { animDir });   // isolated folder = only OUR fbx -> exactly one clip
        clipType.GetMethod("Reimport", Type.EmptyTypes)?.Invoke(clipColl, null);                                       // bakes the pose data
        EditorUtility.SetDirty(clipColl);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        string skelGuid = AmplitudeGuid(skel), atlasGuid = AmplitudeGuid(atlas), clipGuid = AmplitudeGuid(clipColl);
        if (string.IsNullOrEmpty(clipGuid) || clipGuid == "0,0,0,0")
            Debug.LogWarning($"[Factory] {name}: ClipCollection guid looks empty — check the model actually has a clip.");
        Debug.Log($"[Factory] {name} ANIMATED DONE. skeleton={skelGuid} clip={clipGuid} atlas={atlasGuid}");
        return new BakeResult { ok = true, skeletonGuid = skelGuid, atlasGuid = atlasGuid, clipGuid = clipGuid, bbox = Vector3.zero };
    }

    // Longest axis of the FBX's combined mesh bounds (native scale), so we can compute the Scale Factor that hits `size`.
    static float MeasureLongestAxis(GameObject go)
    {
        if (go == null) return 0f;
        Bounds? b = null;
        foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            if (smr.sharedMesh != null) b = b == null ? smr.sharedMesh.bounds : Encapsulate(b.Value, smr.sharedMesh.bounds);
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            if (mf.sharedMesh != null) b = b == null ? mf.sharedMesh.bounds : Encapsulate(b.Value, mf.sharedMesh.bounds);
        if (b == null) return 0f;
        var s = b.Value.size;
        return Mathf.Max(s.x, Mathf.Max(s.y, s.z));
    }
    static Bounds Encapsulate(Bounds a, Bounds add) { a.Encapsulate(add); return a; }

    static Type FindAmpType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(t => t.FullName == fullName);

    // Blender: slim a rigged/animated model into a decimated FBX that keeps its armature + one clip (Tools/rig_anim.py).
    static bool RigAnimViaBlender(string src, string outFbx, int targetTris, string bonePrefixes, string clipName, string albedoOut)
    {
        string proj = Directory.GetParent(Application.dataPath).FullName;
        string script = Path.Combine(proj, "Tools", "rig_anim.py");
        if (!File.Exists(script)) { Debug.LogError("[Factory] bundled rig_anim.py missing: " + script); return false; }
        string blender = FindBlender();
        string args = $"--background --python \"{script}\" -- \"{src}\" \"{outFbx}\" {Mathf.Max(1, targetTris)} \"{bonePrefixes ?? ""}\" \"{clipName ?? ""}\" \"{albedoOut ?? ""}\"";
        var psi = new System.Diagnostics.ProcessStartInfo(blender, args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        try
        {
            if (File.Exists(outFbx)) File.Delete(outFbx);
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(o)) Debug.Log("[rig_anim] " + o.Trim());
                if (!string.IsNullOrWhiteSpace(e)) Debug.LogWarning("[rig_anim] " + e.Trim());
                if (p.ExitCode != 0 || !File.Exists(outFbx)) { Debug.LogError("[Factory] rig_anim produced no FBX (exit " + p.ExitCode + ")."); return false; }
                return true;
            }
        }
        catch (Exception ex) { Debug.LogError("[Factory] could not run Blender rig_anim ('" + blender + "'): " + ex.Message); return false; }
    }

    static BakeResult BuildInner(BakeConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.resourceName)) return Fail("resourceName is required");
        string name = cfg.resourceName;
        float size = cfg.size > 0f ? cfg.size : 5f;
        float smoothing = cfg.smoothingAngle > 0f ? cfg.smoothingAngle : 20f;

        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        string resDir = "Assets/Resources/" + name;
        if (!AssetDatabase.IsValidFolder(resDir)) AssetDatabase.CreateFolder("Assets/Resources", name);
        string objPath = resDir + "/" + name + ".obj";
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        bool haveObj = File.Exists(Path.Combine(projRoot, objPath)) || File.Exists(Path.Combine(projRoot, resDir + "/" + name + ".fbx"));

        // --- 0) (re)import the model. Skipped when "Reuse extracted files" is on AND files already exist, so manual
        //        OBJ/albedo edits survive a re-bake. Still imports on the first bake, or whenever reuse is off. ---
        if (!string.IsNullOrEmpty(cfg.modelFile) && (!cfg.reuseExtracted || !haveObj))
        {
            string srcFile = cfg.modelFile;
            // Optional VERTEX REDUCER: quadric-decimate a heavy model via Blender (per-object, so thin parts survive)
            // down to ~targetTris, producing a reduced GLB the normal path then bakes — no offline tooling needed.
            // targetTris is a CEILING, not a quota: a model already under it passes through unchanged (decimate ratio
            // clamps to 1.0 — never adds geometry). Double-sided doubles the baked geometry, so we HALVE the reduce
            // target when it's on — that way the field is a single "budget" the user sets once (default 24000, which
            // halves to 12000 under double-sided: the confirmed best-looking LCAC bake, just under the ~25k per-model
            // vertex ceiling) and toggling double-sided keeps the baked result under that budget.
            if (cfg.targetTris > 0)
            {
                if (!BlenderAvailable()) return Fail("'Reduce to tris' needs Blender installed (auto-detected, or set EditorPrefs 'ENC.blenderPath').");
                int effTarget = cfg.doubleSided ? Mathf.Max(1, cfg.targetTris / 2) : cfg.targetTris;
                if (cfg.doubleSided) Debug.Log($"[Factory] reduce target {cfg.targetTris} -> {effTarget} tris (double-sided halves it; it doubles the baked geometry)");
                string reduced = Path.Combine(Path.GetTempPath(), name + "_reduced.glb");
                if (!ReduceViaBlender(srcFile, reduced, effTarget)) return Fail("mesh reduction failed (see console)");
                srcFile = reduced;
            }
            string ext = Path.GetExtension(srcFile).ToLowerInvariant();
            if (ext == ".blend")
            {
                // .blend isn't a transfer format — convert it to GLB via headless Blender first, then fall through the
                // normal GLB path. Needs Blender installed (like GLB needs dotnet). Textures embed if the blend's material
                // is a normal Principled-BSDF setup; very old materials may export untextured (supply the albedo manually).
                string tmpGlb = Path.Combine(Path.GetTempPath(), name + "_fromblend.glb");
                if (!ConvertBlend(srcFile, tmpGlb)) return Fail("Blender .blend -> GLB conversion failed (see console)");
                string fsDir = Path.Combine(Application.dataPath, "Resources", name);
                Directory.CreateDirectory(fsDir);
                if (!ConvertGlb(tmpGlb, fsDir, name, cfg.convertGrid)) return Fail("GLB conversion failed (see console)");
            }
            else if (ext == ".glb" || ext == ".gltf")
            {
                string fsDir = Path.Combine(Application.dataPath, "Resources", name);
                Directory.CreateDirectory(fsDir);
                if (!ConvertGlb(srcFile, fsDir, name, cfg.convertGrid)) return Fail("GLB conversion failed (see console)");
            }
            else if (ext == ".obj" || ext == ".fbx")
            {
                objPath = resDir + "/" + name + ext;
                string destFull = Path.GetFullPath(Path.Combine(projRoot, objPath));
                // Skip the copy when the picked file IS the destination (e.g. resource "Hovercraft" + a source already at
                // Assets/Resources/Hovercraft/Hovercraft.obj) — copying a file onto itself throws "used by another
                // process". Its siblings are already in place too, so there's nothing to bring along.
                if (!string.Equals(Path.GetFullPath(srcFile), destFull, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(srcFile, destFull, true);
                    // Bring the model's sibling assets along: textures (so multi-material albedos resolve on import) and,
                    // for OBJ, its .mtl. Copied under their ORIGINAL names so the material->texture references still match.
                    string srcDir = Path.GetDirectoryName(srcFile);
                    if (!string.IsNullOrEmpty(srcDir) && Directory.Exists(srcDir))
                        foreach (var sib in Directory.GetFiles(srcDir))
                        {
                            string se = Path.GetExtension(sib).ToLowerInvariant();
                            if (se == ".png" || se == ".jpg" || se == ".jpeg" || se == ".tga" || se == ".bmp" || se == ".mtl")
                                File.Copy(sib, Path.Combine(projRoot, resDir, Path.GetFileName(sib)), true);
                        }
                }
            }
            else return Fail("unsupported model format: " + ext);
            AssetDatabase.Refresh();
        }
        // Force a SYNCHRONOUS import of the freshly-copied model before loading it. AssetDatabase.Refresh() can defer the
        // import to a later editor tick, so LoadAssetAtPath returns null on a first bake — the "no importable model"
        // failure seen on FBX. ForceSynchronousImport guarantees the imported GameObject exists right now.
        if (File.Exists(Path.Combine(projRoot, objPath)))
            AssetDatabase.ImportAsset(objPath, ImportAssetOptions.ForceSynchronousImport);
        if (AssetDatabase.LoadAssetAtPath<GameObject>(objPath) == null)
        {
            var found = Directory.GetFiles(Path.Combine(Directory.GetParent(Application.dataPath).FullName, resDir))
                .Select(p => p.Replace('\\', '/')).FirstOrDefault(p => p.EndsWith(name + ".obj") || p.EndsWith(name + ".fbx"));
            objPath = found != null ? "Assets" + found.Substring(found.IndexOf("/Resources/")) : objPath;
            if (found != null) AssetDatabase.ImportAsset(objPath, ImportAssetOptions.ForceSynchronousImport);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(objPath) == null) return Fail("no importable model at " + resDir);
        }

        // --- 1) import normals per mode (Recalculate => Calculate + smoothing; else Import the model's own) ---
        //     CRITICAL: weldVertices = false. Unity's model importer defaults to welding coincident verts, which merges
        //     the UV-seam vertices our converter deliberately split — re-scrambling the skin (the whole "wrong texture"
        //     bug). The converter keeps seams (grid 0); this stops Unity from undoing that on re-import.
        var wantImport = cfg.normals == NormalsMode.Recalculate ? ModelImporterNormals.Calculate : ModelImporterNormals.Import;
        if (AssetImporter.GetAtPath(objPath) is ModelImporter imp &&
            (imp.importNormals != wantImport || Mathf.Abs(imp.normalSmoothingAngle - smoothing) > 0.5f
             || imp.weldVertices))
        {
            imp.importNormals = wantImport; imp.normalSmoothingAngle = smoothing;
            imp.weldVertices = false;
            imp.SaveAndReimport();
        }

        var src = AssetDatabase.LoadAssetAtPath<GameObject>(objPath);
        string fsResDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, resDir);

        // --- MULTI-MATERIAL detection: gather the model's materials in submesh order. The game draws one atlas per
        // skeleton (a single _MainTex), so a model with >1 material must be packed into ONE atlas and its UVs remapped
        // into each material's sub-rect. A single-material model keeps the proven path below unchanged. ---
        var matList = new List<Material>();
        foreach (var mf in src.GetComponentsInChildren<MeshFilter>())
        {
            var mm = mf.sharedMesh; if (mm == null) continue;
            var mr0 = mf.GetComponent<MeshRenderer>(); var mats0 = mr0 != null ? mr0.sharedMaterials : null;
            int sc = Mathf.Max(1, mm.subMeshCount);
            for (int s = 0; s < sc; s++)
            {
                var smat = (mats0 != null && mats0.Length > 0) ? mats0[Mathf.Min(s, mats0.Length - 1)] : null;
                if (smat != null && !matList.Contains(smat)) matList.Add(smat);
            }
        }
        bool multiMat = matList.Count > 1;
        Texture2D packedAtlas = null; Rect[] atlasRects = null;
        if (multiMat)
        {
            var albs = matList.Select(mm => LoadReadableAlbedo(fsResDir, mm)).ToArray();
            packedAtlas = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name + "_Atlas" };
            atlasRects = packedAtlas.PackTextures(albs, 2, 4096);
            // Force opaque AND repaint near-black regions neutral grey. Two sources of near-black: (a) unused UV
            // "dead-zones" inside a source albedo (very common — e.g. the zeppelin hull texture's black corner that the
            // hull top samples), and (b) the gaps PackTextures leaves between packed islands. Faces whose UVs land on
            // either would render BLACK. The <32 threshold only catches near-pure-black, so real dark skins are safe.
            var apx = packedAtlas.GetPixels32();
            for (int i = 0; i < apx.Length; i++)
            {
                apx[i].a = 255;
                if (apx[i].r < 32 && apx[i].g < 32 && apx[i].b < 32) { apx[i].r = 160; apx[i].g = 160; apx[i].b = 168; }
            }
            packedAtlas.SetPixels32(apx); packedAtlas.Apply();
            Debug.Log($"[Factory] {name} MULTI-MATERIAL: {matList.Count} materials [{string.Join(", ", matList.Select(mm => mm.name))}] -> packed atlas {packedAtlas.width}x{packedAtlas.height}");
        }

        // Read meshes STRAIGHT FROM THE IMPORTED ASSET — not an Instantiate'd copy. Unity has a known bug where a
        // DUPLICATED imported model returns scrambled UVs (Unity issuetracker: "broken UV mapping when the imported
        // model is duplicated"). Proven here: MeshDumper reads the asset directly and is UV-clean, while the bake's
        // Instantiate produced a mesh with the deck markings mapped onto the superstructure. Combine manually
        // (concatenate verts/UVs/normals, remap triangles) so every vertex keeps its own UV.
        var rootInv = src.transform.worldToLocalMatrix;
        var cVerts = new List<Vector3>(); var cUV = new List<Vector2>(); var cNorm = new List<Vector3>(); var cTris = new List<int>();
        bool haveUV = false, haveNorm = false;
        foreach (var mf in src.GetComponentsInChildren<MeshFilter>())
        {
            var m = mf.sharedMesh; if (m == null) continue;
            var local = rootInv * mf.transform.localToWorldMatrix;
            var v = m.vertices; var uv = m.uv; var nr = m.normals;
            bool mUV = uv != null && uv.Length == v.Length, mNorm = nr != null && nr.Length == v.Length;
            if (!multiMat)
            {
                int baseIdx = cVerts.Count;
                for (int i = 0; i < v.Length; i++)
                {
                    cVerts.Add(local.MultiplyPoint3x4(v[i]));
                    cUV.Add(mUV ? uv[i] : Vector2.zero);
                    cNorm.Add(mNorm ? local.MultiplyVector(nr[i]).normalized : Vector3.up);
                }
                haveUV |= mUV; haveNorm |= mNorm;
                var tris = m.triangles;
                for (int i = 0; i < tris.Length; i++) cTris.Add(baseIdx + tris[i]);
            }
            else
            {
                // Per submesh, remap each vertex UV into its material's atlas rect: uv' = rect.xy + uv * rect.wh.
                // Verts are split per submesh (a vertex shared by two materials needs two different atlas UVs).
                var mr = mf.GetComponent<MeshRenderer>(); var mats = mr != null ? mr.sharedMaterials : null;
                int sc = Mathf.Max(1, m.subMeshCount);
                for (int s = 0; s < sc; s++)
                {
                    var smat = (mats != null && mats.Length > 0) ? mats[Mathf.Min(s, mats.Length - 1)] : null;
                    int mi = smat != null ? matList.IndexOf(smat) : -1;
                    Rect r = (mi >= 0 && atlasRects != null && mi < atlasRects.Length) ? atlasRects[mi] : new Rect(0, 0, 1, 1);
                    var tris = m.GetTriangles(s);
                    var remap = new Dictionary<int, int>();
                    foreach (int oldI in tris)
                    {
                        if (!remap.TryGetValue(oldI, out int ni))
                        {
                            ni = cVerts.Count; remap[oldI] = ni;
                            cVerts.Add(local.MultiplyPoint3x4(v[oldI]));
                            Vector2 u = mUV ? uv[oldI] : Vector2.zero;
                            cUV.Add(new Vector2(r.x + u.x * r.width, r.y + u.y * r.height));
                            cNorm.Add(mNorm ? local.MultiplyVector(nr[oldI]).normalized : Vector3.up);
                        }
                        cTris.Add(ni);
                    }
                    haveUV |= mUV; haveNorm = true;
                }
            }
        }
        var mesh = new Mesh { name = name + "_ModelMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(cVerts);
        if (haveUV) mesh.SetUVs(0, cUV);
        if (haveNorm) mesh.SetNormals(cNorm);
        mesh.SetTriangles(cTris, 0);

        // --- 2) normalize: recenter, scale longest axis -> size, align longest -> Y (+ rotationEuler) ---
        mesh.RecalculateBounds();
        var bb = mesh.bounds; var dims = bb.size;
        float longest = Mathf.Max(dims.x, Mathf.Max(dims.y, dims.z));
        float scl = longest > 0f ? size / longest : 1f;
        Quaternion align = (dims.x >= dims.y && dims.x >= dims.z) ? Quaternion.FromToRotation(Vector3.right, Vector3.up)
                         : (dims.z >= dims.x && dims.z >= dims.y) ? Quaternion.FromToRotation(Vector3.forward, Vector3.up)
                         : Quaternion.identity;
        Quaternion rot = Quaternion.Euler(cfg.rotationEuler) * align;
        var vv = mesh.vertices; var nrm = mesh.normals;
        for (int i = 0; i < vv.Length; i++) vv[i] = rot * ((vv[i] - bb.center) * scl);
        mesh.vertices = vv;
        if (nrm != null && nrm.Length == vv.Length) { for (int i = 0; i < nrm.Length; i++) nrm[i] = rot * nrm[i]; mesh.normals = nrm; }

        // --- 3) position: keel -> z=0, then apply the configured offset (z = waterline) ---
        mesh.RecalculateBounds();
        float raise = -mesh.bounds.min.z;
        var vr = mesh.vertices;
        for (int i = 0; i < vr.Length; i++)
            vr[i] = new Vector3(vr[i].x + cfg.positionOffset.x, vr[i].y + cfg.positionOffset.y, vr[i].z + raise + cfg.positionOffset.z);
        mesh.vertices = vr;
        Debug.Log($"[Factory] {name}: verts={mesh.vertexCount}, rawBox={dims}, size={size}, offset={cfg.positionOffset}, normals={cfg.normals}");

        // --- 3.4) winding fix (optional): CAD/"sketch" meshes have inconsistent face winding, so the engine culls half
        // their faces and parts render invisible (e.g. a hovercraft skirt). Rewind every triangle OUTWARD. Measured from
        // the ORIGIN, which after step 3 sits BELOW the raised model — so "outward" points horizontally out for low
        // side-walls like the skirt (not downward, as a model-CENTRE reference wrongly would). This is the exact test
        // from the retired HovercraftModel builder (Dot(geoNormal, a+b+c) < 0 => flip). Lighter than double-sided (no
        // extra geometry); assumes a roughly convex hull (true for vehicles). Preferred for CAD hulls; use double-sided
        // for genuinely non-convex thin shells.
        if (cfg.windingFix)
        {
            var wv = mesh.vertices; var wt = mesh.triangles; int flipped = 0;
            for (int i = 0; i < wt.Length; i += 3)
            {
                int a = wt[i], b = wt[i + 1], c = wt[i + 2];
                Vector3 geo = Vector3.Cross(wv[b] - wv[a], wv[c] - wv[a]);
                if (Vector3.Dot(geo, wv[a] + wv[b] + wv[c]) < 0f) { wt[i + 1] = c; wt[i + 2] = b; flipped++; }
            }
            mesh.SetTriangles(wt, 0);
            mesh.RecalculateNormals();   // normals follow the new winding so shading AND the engine's culling agree
            Debug.Log($"[Factory] {name} winding-fixed {flipped}/{wt.Length / 3} tris outward");
        }

        // --- 3.45) height-based UVs (optional): override the mesh UVs with U = position along the length (Y after align),
        // V = normalized height (Z). A vertical-gradient albedo then reads by HEIGHT regardless of the source/CAD UVs —
        // e.g. a black skirt low + grey hull high. The documented recipe (retired HovercraftModel builder).
        if (cfg.heightUV)
        {
            var vp = mesh.vertices;
            float mnz = float.MaxValue, mxz = float.MinValue, mny = float.MaxValue, mxy = float.MinValue;
            foreach (var p in vp) { if (p.z < mnz) mnz = p.z; if (p.z > mxz) mxz = p.z; if (p.y < mny) mny = p.y; if (p.y > mxy) mxy = p.y; }
            float hz = Mathf.Max(1e-4f, mxz - mnz), ly = Mathf.Max(1e-4f, mxy - mny);
            var uv = new Vector2[vp.Length];
            for (int i = 0; i < vp.Length; i++) uv[i] = new Vector2((vp[i].y - mny) / ly, (vp[i].z - mnz) / hz);
            mesh.uv = uv;
            Debug.Log($"[Factory] {name} height-based UVs (V=height): vertical-gradient albedo maps low->high");
        }

        // --- 3.5) double-sided (optional): the engine culls backfaces, so single-sided / CAD "sketch" meshes whose faces
        // are wound inward render INVISIBLE in-game (e.g. a hovercraft skirt). Append a REVERSED copy of every triangle,
        // on duplicated verts with flipped normals, so every surface has a face on BOTH sides. Non-destructive; doubles
        // the triangle count. The back shell is nudged slightly inward so it isn't coincident with the front (coincident
        // faces make the game's alpha-to-coverage shader read ~50% transparent).
        if (cfg.doubleSided)
        {
            var sv = mesh.vertices; var sn = mesh.normals; var su = mesh.uv; var st = mesh.triangles;
            bool hasN = sn != null && sn.Length == sv.Length;
            bool hasU = su != null && su.Length == sv.Length;
            int vn = sv.Length;
            float off = Mathf.Max(0.002f, size * 0.004f);
            var nv = new Vector3[vn * 2]; var nn = new Vector3[vn * 2]; var nu = new Vector2[vn * 2];
            for (int i = 0; i < vn; i++)
            {
                nv[i] = sv[i];
                nv[vn + i] = hasN ? sv[i] - sn[i].normalized * off : sv[i];
                if (hasN) { nn[i] = sn[i]; nn[vn + i] = -sn[i]; }
                if (hasU) { nu[i] = su[i]; nu[vn + i] = su[i]; }
            }
            var nt = new int[st.Length * 2];
            System.Array.Copy(st, nt, st.Length);
            for (int i = 0; i < st.Length; i += 3)
            { nt[st.Length + i] = st[i] + vn; nt[st.Length + i + 1] = st[i + 2] + vn; nt[st.Length + i + 2] = st[i + 1] + vn; }
            mesh.Clear(); mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(nv); if (hasN) mesh.SetNormals(nn); if (hasU) mesh.SetUVs(0, nu); mesh.SetTriangles(nt, 0);
            Debug.Log($"[Factory] {name} double-sided: {st.Length / 3} -> {nt.Length / 3} tris");
        }

        // --- 4) atlas (multi-material: the packed atlas built above; single: pick the one extracted albedo) ---
        var atlas = multiMat ? packedAtlas : BuildAtlas(resDir, name);
        AssetDatabase.CreateAsset(atlas, "Assets/Resources/" + name + "_Atlas.asset");

        // --- 5) Faceted shading: unweld so each triangle gets its own face normal ---
        if (cfg.normals == NormalsMode.Faceted)
        {
            var sv = mesh.vertices; var suv = mesh.uv; var st = mesh.triangles;
            bool hasUV = suv != null && suv.Length == sv.Length;
            var nv = new Vector3[st.Length]; var nuv = new Vector2[st.Length]; var nt = new int[st.Length];
            for (int i = 0; i < st.Length; i++) { nv[i] = sv[st[i]]; if (hasUV) nuv[i] = suv[st[i]]; nt[i] = i; }
            mesh.Clear(); mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = nv; if (hasUV) mesh.uv = nuv; mesh.triangles = nt;
        }

        // --- 6) rig + bake skeleton (the proven vehicle rig) ---
        var root  = new GameObject(name + "_Model");
        var dummy = new GameObject("Dummy_Root"); dummy.transform.SetParent(root.transform); dummy.transform.localPosition = Vector3.zero;
        var bone  = new GameObject("Base");       bone.transform.SetParent(dummy.transform);  bone.transform.localPosition = Vector3.zero;
        var bind = new Matrix4x4();
        bind.SetRow(0, new Vector4(1f, 0f, 0f, 0f));
        bind.SetRow(1, new Vector4(0f, 0f, 1f, 0.101f));
        bind.SetRow(2, new Vector4(0f, -1f, 0f, -0.069f));
        bind.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
        mesh.bindposes = new[] { bind };
        mesh.RecalculateBounds();
        if (cfg.normals == NormalsMode.Faceted || mesh.normals == null || mesh.normals.Length != mesh.vertexCount) mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        AssetDatabase.CreateAsset(mesh, "Assets/Resources/" + name + "_ModelMesh.asset");

        var mat = new Material(Shader.Find("Standard")) { name = name + "_Mat", mainTexture = atlas };
        AssetDatabase.CreateAsset(mat, "Assets/Resources/" + name + "_Mat.mat");

        var meshGO = new GameObject("Unit_" + name); meshGO.transform.SetParent(root.transform);
        meshGO.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
        var smr = meshGO.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh; smr.bones = new[] { bone.transform }; smr.rootBone = bone.transform; smr.sharedMaterial = mat; smr.updateWhenOffscreen = true;

        var anim = root.AddComponent<Animator>();
        anim.avatar = AvatarBuilder.BuildGenericAvatar(root, "");
        string prefabPath = "Assets/Resources/" + name + "_Model.prefab";
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

        var skelType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Mercury.Animation.Skeleton");
        if (skelType == null) return Fail("Amplitude Skeleton type not found");
        string skelPath = "Assets/Resources/" + name + "_Skeleton.asset";
        var skel = ScriptableObject.CreateInstance(skelType);
        AssetDatabase.CreateAsset(skel, skelPath);
        skelType.GetMethod("SetPrefab", new[] { typeof(GameObject) })?.Invoke(skel, new object[] { prefab });
        skelType.GetMethod("Reimport", Type.EmptyTypes)?.Invoke(skel, null);
        EditorUtility.SetDirty(skel);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        string skelGuid = AmplitudeGuid(skel), atlasGuid = AmplitudeGuid(atlas);
        Debug.Log($"[Factory] {name} DONE. skeleton={skelGuid} atlas={atlasGuid}");
        return new BakeResult { ok = true, skeletonGuid = skelGuid, atlasGuid = atlasGuid, bbox = dims };
    }

    // A readable albedo for one material, for multi-material atlas packing. Prefer the extracted png on disk whose name
    // matches the material's texture (so hand-edits still flow in); else copy the imported texture; else a grey tile.
    static Texture2D LoadReadableAlbedo(string fsDir, Material mat)
    {
        string texName = mat != null && mat.mainTexture != null ? mat.mainTexture.name : (mat != null ? mat.name : null);
        string png = null;
        if (Directory.Exists(fsDir) && !string.IsNullOrEmpty(texName))
        {
            var pngs = Directory.GetFiles(fsDir, "*.png")
                .Where(p => { var f = Path.GetFileNameWithoutExtension(p).ToLowerInvariant(); return !f.Contains("backup") && !f.Contains("orig"); }).ToArray();
            png = pngs.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(texName, StringComparison.OrdinalIgnoreCase))
               ?? pngs.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).IndexOf(texName, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? pngs.FirstOrDefault(p => texName.IndexOf(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase) >= 0);
        }
        if (png != null && File.Exists(png))
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = texName ?? Path.GetFileNameWithoutExtension(png) };
            t.LoadImage(File.ReadAllBytes(png));
            Debug.Log($"[Factory]   material '{(mat != null ? mat.name : "?")}' -> albedo {Path.GetFileName(png)}");
            return t;
        }
        if (mat != null && mat.mainTexture is Texture2D mt) { Debug.Log($"[Factory]   material '{mat.name}' -> imported texture '{mt.name}'"); return ReadableCopy(mt); }
        Debug.LogWarning($"[Factory]   material '{(mat != null ? mat.name : "?")}' has no albedo -> grey tile");
        var g = new Texture2D(64, 64, TextureFormat.RGBA32, false) { name = texName ?? "grey" };
        var gpx = new Color[64 * 64]; for (int i = 0; i < gpx.Length; i++) gpx[i] = new Color(0.62f, 0.64f, 0.67f, 1f);
        g.SetPixels(gpx); g.Apply();
        return g;
    }

    // Imported textures are usually not CPU-readable (needed by PackTextures); blit through a RenderTexture to copy.
    static Texture2D ReadableCopy(Texture2D srcTex)
    {
        var rt = RenderTexture.GetTemporary(srcTex.width, srcTex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(srcTex, rt);
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var t = new Texture2D(srcTex.width, srcTex.height, TextureFormat.RGBA32, false) { name = srcTex.name };
        t.ReadPixels(new Rect(0, 0, srcTex.width, srcTex.height), 0, 0); t.Apply();
        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
        return t;
    }

    static Texture2D BuildAtlas(string resDir, string name)
    {
        // Read the extracted albedo straight off disk. With "Reuse extracted files" on, this is the modder's hand-edited
        // copy (e.g. a texture fix done in paint.net), so their edits flow into the atlas untouched.
        string fsDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, resDir);
        // Pick the canonical albedo png. Exclude sidecar copies (a hand-made "-backup", or the converter's ".orig") and
        // prefer the shortest matching name so "<mat>_albedo.png" wins over "<mat>_albedo-backup.png" regardless of
        // directory order — a stray backup used to sort first ('-' < '.') and silently get baked instead.
        string albedo = Directory.Exists(fsDir)
            ? Directory.GetFiles(fsDir, "*.png")
                .Where(p => { var f = Path.GetFileNameWithoutExtension(p).ToLowerInvariant(); return f.Contains("albedo") && !f.Contains("backup") && !f.Contains("orig"); })
                .OrderBy(p => Path.GetFileName(p).Length)
                .FirstOrDefault()
            : null;
        Texture2D atlas;
        if (albedo != null && File.Exists(albedo))
        {
            atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name + "_Atlas" };
            atlas.LoadImage(File.ReadAllBytes(albedo));
            var px = atlas.GetPixels();
            for (int i = 0; i < px.Length; i++) px[i].a = 1f;
            atlas.SetPixels(px); atlas.Apply();
            Debug.Log($"[Factory] {name} albedo: {atlas.width}x{atlas.height} ({Path.GetFileName(albedo)})");
        }
        else
        {
            atlas = new Texture2D(64, 64, TextureFormat.RGBA32, false) { name = name + "_Atlas" };
            var px = new Color[64 * 64];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0.62f, 0.64f, 0.67f, 1f);
            atlas.SetPixels(px); atlas.Apply();
            Debug.Log($"[Factory] {name} no albedo -> flat grey");
        }
        return atlas;
    }

    static bool ConvertGlb(string glb, string outDir, string name, int grid)
    {
        string proj = Directory.GetParent(Application.dataPath).FullName;
        string tools = Path.Combine(proj, "Tools", "glbconv");
        string exe = Path.Combine(tools, "glbconv.exe");
        string dll = Path.Combine(tools, "glbconv.dll");
        string args = $"\"{glb}\" \"{outDir}\" {name} {Mathf.Max(0, grid)}";

        System.Diagnostics.ProcessStartInfo psi;
        if (File.Exists(exe))
            // Preferred: self-contained single-file exe — carries its own .NET runtime, so NO .NET install is needed.
            psi = new System.Diagnostics.ProcessStartInfo(exe, args);
        else if (File.Exists(dll))
        {
            // Dev fallback: framework-dependent dll run through an installed dotnet (needs the .NET runtime/SDK).
            string dotnet = EditorPrefs.GetString("ENC.dotnetPath", "dotnet");
            psi = new System.Diagnostics.ProcessStartInfo(dotnet, $"\"{dll}\" " + args);
        }
        else { Debug.LogError("[Factory] converter missing: no glbconv.exe or glbconv.dll in " + tools); return false; }

        psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
        try
        {
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(o)) Debug.Log("[glbconv] " + o.Trim());
                if (!string.IsNullOrWhiteSpace(e)) Debug.LogWarning("[glbconv] " + e.Trim());
                return p.ExitCode == 0;
            }
        }
        catch (Exception ex) { Debug.LogError("[Factory] could not run converter ('" + psi.FileName + "'): " + ex.Message + "\n(GLB path uses Tools/glbconv/glbconv.exe; the dev fallback needs a dotnet on PATH or EditorPrefs 'ENC.dotnetPath'.)"); return false; }
    }

    // Locate blender.exe with zero config: explicit EditorPrefs override, else the newest install under Program Files,
    // else fall back to "blender" on PATH. So .blend import "just works" whenever Blender is installed.
    public static string FindBlender()
    {
        string pref = EditorPrefs.GetString("ENC.blenderPath", "");
        if (!string.IsNullOrEmpty(pref) && File.Exists(pref)) return pref;
        foreach (var root in new[] { @"C:\Program Files\Blender Foundation", @"C:\Program Files (x86)\Blender Foundation" })
        {
            if (!Directory.Exists(root)) continue;
            var exe = Directory.GetDirectories(root, "Blender*")
                .Select(d => Path.Combine(d, "blender.exe")).Where(File.Exists)
                .OrderByDescending(p => p).FirstOrDefault();   // newest version folder wins ("Blender 5.1" > "Blender 4.2")
            if (exe != null) return exe;
        }
        return "blender";   // on PATH
    }

    public static bool BlenderAvailable()
    {
        string b = FindBlender();
        return b != "blender" || File.Exists(b);   // an absolute hit, or assume PATH has it
    }

    // Reduce any Blender-importable model (glb/gltf/obj/fbx) to ~targetTris via headless Blender + the bundled
    // mesh_reduce.py (per-object quadric decimation), exporting a reduced GLB the Factory then bakes.
    static bool ReduceViaBlender(string src, string outGlb, int targetTris)
    {
        string proj = Directory.GetParent(Application.dataPath).FullName;
        string script = Path.Combine(proj, "Tools", "mesh_reduce.py");
        if (!File.Exists(script)) { Debug.LogError("[Factory] bundled reducer missing: " + script); return false; }
        string blender = FindBlender();
        var psi = new System.Diagnostics.ProcessStartInfo(blender, $"--background --python \"{script}\" -- \"{src}\" \"{outGlb}\" {Mathf.Max(1, targetTris)}")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        try
        {
            if (File.Exists(outGlb)) File.Delete(outGlb);
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(o)) Debug.Log("[reduce] " + o.Trim());
                if (!string.IsNullOrWhiteSpace(e)) Debug.LogWarning("[reduce] " + e.Trim());
                if (p.ExitCode != 0 || !File.Exists(outGlb)) { Debug.LogError("[Factory] Blender reduce produced no GLB (exit " + p.ExitCode + ")."); return false; }
                return true;
            }
        }
        catch (Exception ex) { Debug.LogError("[Factory] could not run Blender reduce ('" + blender + "'): " + ex.Message); return false; }
    }

    // Convert a .blend to GLB via headless Blender + the bundled export script, so the normal GLB path can take over.
    static bool ConvertBlend(string blend, string outGlb)
    {
        string proj = Directory.GetParent(Application.dataPath).FullName;
        string script = Path.Combine(proj, "Tools", "blend_export.py");
        if (!File.Exists(script)) { Debug.LogError("[Factory] bundled blend exporter missing: " + script); return false; }
        string blender = FindBlender();
        var psi = new System.Diagnostics.ProcessStartInfo(blender, $"\"{blend}\" --background --python \"{script}\" -- \"{outGlb}\"")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        try
        {
            if (File.Exists(outGlb)) File.Delete(outGlb);
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(o)) Debug.Log("[blender] " + o.Trim());
                if (!string.IsNullOrWhiteSpace(e)) Debug.LogWarning("[blender] " + e.Trim());
                if (p.ExitCode != 0 || !File.Exists(outGlb)) { Debug.LogError("[Factory] Blender produced no GLB (exit " + p.ExitCode + ")."); return false; }
                return true;
            }
        }
        catch (Exception ex) { Debug.LogError("[Factory] could not run Blender ('" + blender + "'): " + ex.Message + "\nInstall Blender, or set EditorPrefs 'ENC.blenderPath' to blender.exe."); return false; }
    }

    static string AmplitudeGuid(UnityEngine.Object asset)
    {
        var adbType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Framework.Asset.AssetDatabase");
        var getGuid = adbType?.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) });
        var g = getGuid?.Invoke(null, new object[] { asset });
        if (g == null) return "";
        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object A = g.GetType().GetField("a", bf)?.GetValue(g), B = g.GetType().GetField("b", bf)?.GetValue(g),
               Cc = g.GetType().GetField("c", bf)?.GetValue(g), D = g.GetType().GetField("d", bf)?.GetValue(g);
        return $"{A},{B},{Cc},{D}";
    }

    static BakeResult Fail(string msg) { Debug.LogError("[Factory] " + msg); return new BakeResult { ok = false, error = msg }; }
    static Type[] SafeTypes(Assembly a) { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }
}
