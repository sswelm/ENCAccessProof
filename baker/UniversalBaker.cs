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
}

public struct BakeResult
{
    public bool ok; public string error; public string skeletonGuid, atlasGuid; public Vector3 bbox;
}

public static class UniversalBaker
{
    public static BakeResult Build(BakeConfig cfg)
    {
        try { return BuildInner(cfg); }
        catch (Exception e) { Debug.LogError("[Factory] " + e); return new BakeResult { ok = false, error = e.Message }; }
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
            string ext = Path.GetExtension(cfg.modelFile).ToLowerInvariant();
            if (ext == ".blend")
            {
                // .blend isn't a transfer format — convert it to GLB via headless Blender first, then fall through the
                // normal GLB path. Needs Blender installed (like GLB needs dotnet). Textures embed if the blend's material
                // is a normal Principled-BSDF setup; very old materials may export untextured (supply the albedo manually).
                string tmpGlb = Path.Combine(Path.GetTempPath(), name + "_fromblend.glb");
                if (!ConvertBlend(cfg.modelFile, tmpGlb)) return Fail("Blender .blend -> GLB conversion failed (see console)");
                string fsDir = Path.Combine(Application.dataPath, "Resources", name);
                Directory.CreateDirectory(fsDir);
                if (!ConvertGlb(tmpGlb, fsDir, name, cfg.convertGrid)) return Fail("GLB conversion failed (see console)");
            }
            else if (ext == ".glb" || ext == ".gltf")
            {
                string fsDir = Path.Combine(Application.dataPath, "Resources", name);
                Directory.CreateDirectory(fsDir);
                if (!ConvertGlb(cfg.modelFile, fsDir, name, cfg.convertGrid)) return Fail("GLB conversion failed (see console)");
            }
            else if (ext == ".obj" || ext == ".fbx")
            {
                objPath = resDir + "/" + name + ext;
                File.Copy(cfg.modelFile, Path.Combine(projRoot, objPath), true);
                // Bring the model's sibling assets along: textures (so multi-material albedos resolve on import) and,
                // for OBJ, its .mtl. Copied under their ORIGINAL names so the material->texture references still match.
                string srcDir = Path.GetDirectoryName(cfg.modelFile);
                if (!string.IsNullOrEmpty(srcDir) && Directory.Exists(srcDir))
                    foreach (var sib in Directory.GetFiles(srcDir))
                    {
                        string se = Path.GetExtension(sib).ToLowerInvariant();
                        if (se == ".png" || se == ".jpg" || se == ".jpeg" || se == ".tga" || se == ".bmp" || se == ".mtl")
                            File.Copy(sib, Path.Combine(projRoot, resDir, Path.GetFileName(sib)), true);
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
            var apx = packedAtlas.GetPixels(); for (int i = 0; i < apx.Length; i++) apx[i].a = 1f;
            packedAtlas.SetPixels(apx); packedAtlas.Apply();
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
