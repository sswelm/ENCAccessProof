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
    public float   albedoBrightness; // multiply the baked atlas RGB (1 = unchanged). >1 lifts a dark skin — the injection path ships FLAT albedo (donor PBR neutralized), so shiny/dark models read muddy in-game; this compensates at bake time
    public float   albedoSaturation; // scale colour vividness around per-pixel luminance (1 = unchanged, 0 = greyscale, >1 = punchier). Fixes desaturated albedos (game lighting can't add colour back)
    public bool    keepBlack;        // MULTI-MATERIAL only: skip the near-black->grey remap so an intentionally black material (glossy canopy, dark cockpit) stays black. Default false = neutralize (hides UV dead-zones / packing gaps)
    public MaterialMode materialMode; // Auto/Single/Multi atlas mode. The animated path uses this to decide single vs packed multi-material atlas (previously animated was always single).
    public int     atlasMaxDim;      // longest side of the baked atlas in px (256 / 512 / 1024 / 2048). Smaller = smaller shipped _Atlas.asset. A unit is ~80px at map zoom so 512-1024 is plenty; 0 = default (512)
    public int     targetTris;      // >0 = quadric-decimate the source to ~this many triangles (Blender) before baking, to fit the engine's shared vertex/index buffer
    public string  stripParts;      // bake-time (Blender): comma-separated object-name substrings to DELETE from the source before baking (e.g. a helicopter's own rotor so the donor's spinning rotor shows through). Empty = keep everything.
    public bool    animated;        // true = ANIMATED path: bake from the model's OWN armature + clip (Skeleton + ClipCollection), not the procedural vehicle rig
    public string  animClip;        // ANIMATED only: name of the clip to bake when the model has several (e.g. "hover"); empty = the assigned/first action
    public string  animateBones;    // ANIMATED only: comma-separated bone-name prefixes to keep animation on (e.g. "prop,rotor"); empty = keep the whole clip
    public bool    animUnitFix;     // ANIMATED only: if the model bakes ~100x too big & floats (a metre->cm FBX unit scale), tick this — the baker measures the FBX at its true scale (useFileScale off) then bakes with the unit scale on, so Size = in-game units. Per-model because different rig exports embed different unit scales (some need it, some break with it).
    public bool    convertRig;      // ANIMATED only: route the Blender step through the RAW-RIG CONVERSION (rest-normalize + rebake, root collapse, topological rename, rotation/scale fold, clean-unit export). THE pipeline switch — rotationEuler is just a rotation again (applied only on this path; the legacy path stays byte-identical).
    public bool    keepTexture;     // ANIMATED only: when the Blender step re-runs, DON'T regenerate the extracted albedo (protects hand-edited textures). This is the 'Reuse extracted files' checkbox's ONLY effect on the animated path — geometry caching is automatic (the windows re-slim exactly when a Blender-step setting changed).
}

public struct BakeResult
{
    public bool ok; public string error; public string skeletonGuid, atlasGuid, clipGuid; public Vector3 bbox;
}

public static class UniversalBaker
{
    public static BakeResult Build(BakeConfig cfg)
    {
        // E5: snapshot the existing baked outputs BEFORE the (delete-first) bake, and restore them if it fails, so a
        // partway-failed re-bake can't leave the registry pointing at now-deleted assets. Success path is unchanged.
        var backup = BackupOutputs(cfg.resourceName);
        BakeResult r;
        try { r = BuildInner(cfg); }
        catch (Exception e) { Debug.LogError("[Factory] " + e); r = new BakeResult { ok = false, error = e.Message }; }
        if (r.ok) DiscardBackup(backup); else RestoreOutputs(backup);
        return r;
    }

    // ANIMATED bake — a parallel pipeline that keeps the model's OWN armature + clip instead of the procedural
    // single-bone vehicle rig. Blender slims the rigged model (join+decimate, keep armature, clamp frame range, optional
    // bone strip, export albedo), Unity imports the FBX, then we bake an Amplitude Skeleton (from the FBX) and a
    // ClipCollection (from the folder) via the same reflected editor methods the SDK inspector uses. Returns skel + clip
    // + atlas guids for the registry ("clip" makes the runtime drive the pawn's pose onto our animation).
    public static BakeResult BuildAnimated(BakeConfig cfg)
    {
        var backup = BackupOutputs(cfg.resourceName);   // E5: same rollback protection on the animated path
        BakeResult r;
        try { r = BuildAnimatedInner(cfg); }
        catch (Exception e) { Debug.LogError("[Factory] " + e); r = new BakeResult { ok = false, error = e.Message }; }
        if (r.ok) DiscardBackup(backup); else RestoreOutputs(backup);
        return r;
    }

    // ---- E5: re-bake rollback protection --------------------------------------------------------------------------
    // Both bake paths DELETE the model's baked outputs before the fallible steps (Blender, glbconv, the Amplitude
    // skeleton/clip bake). Without this, a re-bake that throws or produces an empty GUID partway leaves those assets
    // gone while the registry still references them — "re-bake failed, old model destroyed". So we copy the existing
    // outputs (each .asset AND its .meta, so the GUIDs survive) to a temp dir OUTSIDE the project before the bake, and
    // restore them on failure. The delete-first stale-cache fix is untouched — this only wraps it.
    // OUTSIDE Assets/ is essential: a copy under Assets/ would import as a DUPLICATE-GUID asset and corrupt the original.
    static readonly string[] OutputSuffixes = { "_ModelMesh.asset", "_Atlas.asset", "_Mat.mat", "_Model.prefab", "_Skeleton.asset", "_Clips.asset" };

    class OutputBackup { public string name = ""; public string dir = ""; public readonly List<string> files = new List<string>(); }

    static string ResourcesFull() => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Assets", "Resources");

    // Copy whatever outputs currently exist for `name` (+ their .meta) to a fresh temp dir. Never throws.
    static OutputBackup BackupOutputs(string name)
    {
        var b = new OutputBackup { name = name ?? "" };
        if (string.IsNullOrEmpty(name)) return b;
        b.dir = Path.Combine(Path.GetTempPath(), "enc_rebake_backup", name);
        try
        {
            if (Directory.Exists(b.dir)) Directory.Delete(b.dir, true);
            string res = ResourcesFull();
            foreach (var s in OutputSuffixes)
                foreach (var ext in new[] { "", ".meta" })
                {
                    string src = Path.Combine(res, name + s + ext);
                    if (!File.Exists(src)) continue;
                    Directory.CreateDirectory(b.dir);
                    File.Copy(src, Path.Combine(b.dir, name + s + ext), true);
                    b.files.Add(name + s + ext);
                }
        }
        catch (Exception e) { Debug.LogWarning("[Factory] re-bake backup failed (proceeding WITHOUT rollback protection): " + e.Message); b.files.Clear(); }
        return b;
    }

    static void DiscardBackup(OutputBackup b)
    {
        try { if (b != null && !string.IsNullOrEmpty(b.dir) && Directory.Exists(b.dir)) Directory.Delete(b.dir, true); } catch { }
    }

    // Restore the backed-up outputs (called only on a FAILED bake): wipe any partial new outputs, copy the backups back
    // verbatim (asset + meta -> original GUIDs), and reimport. A no-op when nothing was backed up (a first bake).
    static void RestoreOutputs(OutputBackup b)
    {
        if (b == null || b.files.Count == 0) { DiscardBackup(b); return; }
        try
        {
            string res = ResourcesFull();
            foreach (var s in OutputSuffixes)
                foreach (var ext in new[] { "", ".meta" })
                { string p = Path.Combine(res, b.name + s + ext); try { if (File.Exists(p)) File.Delete(p); } catch { } }
            foreach (var f in b.files) File.Copy(Path.Combine(b.dir, f), Path.Combine(res, f), true);
            AssetDatabase.Refresh();
            int n = b.files.Count(f => !f.EndsWith(".meta"));
            Debug.LogWarning($"[Factory] {b.name}: re-bake FAILED — restored the previous {n} baked asset(s) from backup. Your working model is intact (the registry was not changed).");
        }
        catch (Exception e) { Debug.LogError("[Factory] re-bake RESTORE failed — recover the model from git or the project backup: " + e); }
        finally { DiscardBackup(b); }
    }

    static BakeResult BuildAnimatedInner(BakeConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.resourceName)) return Fail("resourceName is required");
        if (!BlenderAvailable()) return Fail("Animated import needs Blender installed (auto-detected, or set EditorPrefs 'ENC.blenderPath').");
        string name = cfg.resourceName;
        float size = cfg.size > 0f ? cfg.size : 5f;

        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        // Bake INPUTS (OBJ / albedos / preview prefab) go OUTSIDE Resources, into Assets/FactorySource. Unity force-
        // includes EVERYTHING under a Resources folder in the build — even unreferenced source — which bloated the mod
        // bundle (416MB) AND shipped the raw extractable source of licensed assets (a Fab-license breach). The baked
        // OUTPUTS (_Skeleton/_Atlas/_ModelMesh/_Mat/_Model.prefab) stay in Resources ROOT: they're self-contained
        // (reference only each other, never the source) and MUST ship + load by GUID. FactorySource is not shipped.
        string resDir = "Assets/FactorySource/" + name;
        if (!AssetDatabase.IsValidFolder("Assets/FactorySource")) AssetDatabase.CreateFolder("Assets", "FactorySource");
        if (!AssetDatabase.IsValidFolder(resDir)) AssetDatabase.CreateFolder("Assets/FactorySource", name);
        // Bake the FBX into a DEDICATED subfolder that holds ONLY our clip. The ClipCollection's SetFromDirectory scans a
        // whole folder for animation FBX, so if we dropped it in resDir it would also pick up any OTHER fbx there (e.g. a
        // hand-made drone_rigged_slim.fbx) and bake a 2-clip collection — the runtime then resolves the wrong clip and the
        // model tears apart. Isolating the fbx makes the bake correct regardless of what else is in the resource folder.
        string animDir = resDir + "/anim";
        if (!AssetDatabase.IsValidFolder(animDir)) AssetDatabase.CreateFolder(resDir, "anim");
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string fsDir = Path.Combine(Application.dataPath, "FactorySource", name);
        Directory.CreateDirectory(fsDir);

        string fbxRel = animDir + "/" + name + "_anim.fbx";
        string fbxFull = Path.Combine(projRoot, fbxRel);

        // --- 1) Blender: slim the rigged model (keep armature + clip, clamp frame range, optional bone strip, albedo) ---
        if (!string.IsNullOrEmpty(cfg.modelFile) && (!cfg.reuseExtracted || !File.Exists(fbxFull)))
        {
            int target = cfg.targetTris > 0 ? cfg.targetTris : 12000;   // animated skins want to stay well under the shared buffer
            string albedoOut = Path.Combine(fsDir, name + "_albedo.png");
            // 'Keep texture': skip regenerating an existing extracted albedo (rig_anim skips export on an empty path),
            // so a hand-edited texture survives geometry re-slims. A missing albedo is always (re)extracted.
            if (cfg.keepTexture && File.Exists(albedoOut))
            { Debug.Log($"[Factory] {name}: keeping the existing extracted albedo (hand-edit protection)."); albedoOut = ""; }
            bool keepMats = cfg.materialMode != MaterialMode.Single;   // Auto/Multi keep the material slots so the atlas step can pack them (Single collapses to 1, the old default)
            if (!RigAnimViaBlender(cfg.modelFile, fbxFull, target, cfg.animateBones ?? "", cfg.animClip ?? "", albedoOut, keepMats, cfg.rotationEuler, cfg.convertRig))
                return Fail("Blender animated slim failed (see console). Is the model rigged with the named animation clip?");
        }
        if (!File.Exists(fbxFull)) return Fail("no slim FBX at " + fbxRel + " — bake with a Model file first (Reuse extracted needs an existing one).");
        AssetDatabase.ImportAsset(fbxRel, ImportAssetOptions.ForceUpdate);

        // Decide multi-material EARLY (from the MTL on disk) — a multi-material bake rebuilds/merges the skinned mesh
        // (BuildMultiAtlasAndRemap below) and Amplitude's skeleton importer rejects that rebuilt mesh if tangents were
        // stripped, so the tangent optimization is limited to single-material models (Amplitude reads their mesh as-is).
        string fsResDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, resDir);
        var orderedAlb = LoadOrderedAlbedos(fsResDir, name);   // MTL-ordered (materialName -> albedo texture)
        // Need >1 REAL albedo to pack a multi-material atlas. Multi/Auto with 0-1 albedos (e.g. a fresh extraction that
        // didn't regenerate the per-material albedos, or a misconfig) falls back to a single atlas instead of building a
        // blank 2x2 and spamming "no atlas rect — left unmapped" for every submesh. Multi still forces multi whenever
        // the albedos are actually present (>1); it just can't conjure a multi-atlas from nothing.
        bool multiMat = orderedAlb.Count > 1
                        && (cfg.materialMode == MaterialMode.Multi || cfg.materialMode == MaterialMode.Auto);

        // --- 2) import the FBX: Generic rig, import animation, scale so the longest axis ~= size ---
        var imp = AssetImporter.GetAtPath(fbxRel) as ModelImporter;
        if (imp == null) return Fail("could not get ModelImporter for " + fbxRel);
        imp.animationType = ModelImporterAnimationType.Generic;
        imp.importAnimation = true;
        // Tangents: EXPLICITLY set both ways (a prior bake stamps this into the .meta, so a bare "skip" leaves it stuck).
        // Single-material -> None: runtime neutralizes normal maps, so tangents are 16 B/vert of dead weight in the mesh +
        // hex skeleton. Multi-material -> CalculateMikk (the default): its mesh is REBUILT (Instantiate + submesh-merge),
        // and Amplitude's skeleton importer throws IndexOutOfRange on that rebuilt mesh if it has no tangents.
        imp.importTangents = ModelImporterTangents.CalculateMikk;   // ALWAYS keep tangents on the ANIMATED path: its skeleton bake (Amplitude MeshCollection.ImportMeshes on a real skinned mesh) reads them, and stripping them throws IndexOutOfRange (broke the single-material ReconDrone — "worked last night", i.e. before the strip). The tangent-size optimization is safe ONLY on the static path below.
        imp.globalScale = 1f;
        // "Fix 100x oversize (FBX unit scale)" — PER-MODEL toggle, because different rig exports embed different unit scales.
        // OFF: measure at the file's TRUE scale AND bake with useFileScale OFF — everything folds into globalScale, so
        //   the baked skeleton's BindPose/Local scales are all 1 (like the proven ReconDrone). A live file-scale would
        //   materialize as a 0.01-per-bindpose + x100-root sandwich that Amplitude's uniform-scale TRS composition
        //   mangles on deep bone chains (the Combine soldier's head, depth 8-9, rode ~0.7u off the shoulders).
        // ON: measure at TRUE scale then bake with the unit scale ON (the howitzer needs this; its shallow rig
        //   tolerates the sandwich). No single rule fits both — hence the toggle stays per-model.
        imp.useFileScale = false;   // ALWAYS measure raw; the bake either keeps this (OFF) or re-enables it (ON, below)
        imp.SaveAndReimport();
        var fbxGo = AssetDatabase.LoadAssetAtPath<GameObject>(fbxRel);
        float longest = MeasureLongestAxis(fbxGo);
        if (cfg.animUnitFix) imp.useFileScale = true;   // bake with the embedded unit scale on (required for the correct render size)
        if (longest > 1e-4f)
        {
            imp.globalScale = size / longest;   // SDK skeleton uses the FBX native scale, so match `size` via Scale Factor
            imp.SaveAndReimport();
            fbxGo = AssetDatabase.LoadAssetAtPath<GameObject>(fbxRel);
            Debug.Log($"[Factory] {name} FBX scale factor {imp.globalScale:0.###} (native longest {longest:0.###} -> {size} units){(cfg.animUnitFix ? " [unit-fix]" : "")}");
        }
        else if (cfg.animUnitFix) { imp.SaveAndReimport(); fbxGo = AssetDatabase.LoadAssetAtPath<GameObject>(fbxRel); }   // apply useFileScale-on even if too small to rescale
        if (fbxGo == null) return Fail("imported FBX has no GameObject");

        // --- 3) atlas: SINGLE albedo, or (for a multi-material OPEN model like a towed gun) a PACKED atlas with the skinned
        //        mesh UVs remapped per-submesh. MaterialMode gates it: Single = one texture; Multi = force packing; Auto =
        //        pack when the model has >1 material. Without this, every part samples one texture and e.g. a wheel maps wrong.
        //        (fsResDir / orderedAlb / multiMat were computed up front so the importer could gate tangent-stripping.) ---
        Mesh previewMesh = null;
        Texture2D atlas = multiMat
            ? BuildMultiAtlasAndRemap(fbxGo, orderedAlb, name, cfg, out previewMesh)   // pack + remap the skinned mesh UVs in place (SetPrefab below reads them)
            : BuildAtlas(resDir, name, cfg.albedoBrightness, cfg.albedoSaturation);
        atlas = FinalizeAtlas(atlas, name, cfg.atlasMaxDim);   // cap resolution + DXT1-compress (keeps the shipped _Atlas.asset small)
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
        // Pre-check the skinned mesh's bone data: a vertex weighted to a bone index the mesh's bone list doesn't have
        // makes Amplitude's SetPrefab/Reimport (MeshCollection.ImportMeshes) throw an opaque IndexOutOfRange. Dump the
        // state and flag any mismatch clearly (this is what breaks the ReconDrone's FRESH bake).
        foreach (var smr in fbxGo.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var m = smr.sharedMesh; if (m == null) continue;
            int nBones = smr.bones != null ? smr.bones.Length : 0;
            int maxIdx = -1; var bws = m.boneWeights;
            if (bws != null) foreach (var w in bws) maxIdx = Mathf.Max(maxIdx, Mathf.Max(w.boneIndex0, w.boneIndex1), Mathf.Max(w.boneIndex2, w.boneIndex3));
            Debug.Log($"[Factory] {name} SKMESH '{smr.name}': verts={m.vertexCount} subMeshes={m.subMeshCount} mats={(smr.sharedMaterials != null ? smr.sharedMaterials.Length : 0)} bones={nBones} bindposes={(m.bindposes != null ? m.bindposes.Length : 0)} maxBoneIdxUsed={maxIdx} tangents={(m.tangents != null ? m.tangents.Length : 0)}");
            if (maxIdx >= nBones)
                Debug.LogError($"[Factory] {name}: BONE MISMATCH — a vertex is weighted to bone index {maxIdx} but the mesh lists only {nBones} bones. THIS is the IndexOutOfRange in Amplitude's ImportMeshes (a fresh-extraction rig/decimation issue).");
        }
        if (!InvokeReq(skelType, "SetPrefab", new[] { typeof(GameObject) }, skel, new object[] { fbxGo }, out var err)) return Fail(err);
        if (!InvokeReq(skelType, "Reimport", Type.EmptyTypes, skel, null, out err)) return Fail(err);
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
        if (!InvokeReq(clipType, "SetFromDirectory", new[] { typeof(string) }, clipColl, new object[] { animDir }, out err)) return Fail(err);   // isolated folder = only OUR fbx -> one clip
        if (!InvokeReq(clipType, "Reimport", Type.EmptyTypes, clipColl, null, out err)) return Fail(err);                                       // bakes the pose data
        EditorUtility.SetDirty(clipColl);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        // --- 6) preview aid: a STATIC textured prefab (the baked mesh + the atlas skin) you can select to inspect in
        //        Unity's preview window and to judge the (decimated) vertex count. NOT written to the registry, so the
        //        runtime ignores it entirely — it's purely a modeling aid.
        // Rotation is now baked INTO the rig by rig_anim.py (cfg.rotationEuler passed to the Blender step above), so
        // the preview must NOT apply it again — pass zero. What the preview shows is now what the game gets.
        GeneratePreviewPrefab(name, resDir, fbxRel, atlas, Vector3.zero, previewMesh);

        string skelGuid = AmplitudeGuid(skel), atlasGuid = AmplitudeGuid(atlas), clipGuid = AmplitudeGuid(clipColl);
        // an empty GUID means the SDK skeleton/clip bake produced nothing — fail loudly rather than write a dead registry entry.
        if (string.IsNullOrEmpty(skelGuid) || skelGuid == "0,0,0,0") return Fail($"{name}: skeleton bake produced an empty GUID (SetPrefab/Reimport did nothing).");
        if (string.IsNullOrEmpty(clipGuid) || clipGuid == "0,0,0,0") return Fail($"{name}: ClipCollection GUID is empty — the model has no bakeable clip (check the Clip name / that it's actually animated).");
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

    // Emit a static, textured, INSPECTABLE prefab (<name>_Preview) from the baked FBX mesh + the atlas skin. Purely a
    // modeling aid for Unity's preview window + judging the decimated vertex count — it is NOT in the registry, so the
    // runtime never touches it. Uses the same mesh that gets injected, so its vert count is the real one; logs it so you
    // can see the effect of 'Reduce to ~tris' and cut further.
    static void GeneratePreviewPrefab(string name, string resDir, string fbxRel, Texture2D atlas, Vector3 rotationEuler, Mesh overrideMesh = null)
    {
        try
        {
            // multi-material bake hands us the already-remapped, single-submesh mesh so the preview shows all parts with the
            // right UVs; otherwise load the largest mesh from the FBX (the body).
            var mesh = overrideMesh ?? AssetDatabase.LoadAllAssetsAtPath(fbxRel).OfType<Mesh>()
                .OrderByDescending(m => m.vertexCount).FirstOrDefault();
            if (mesh == null) { Debug.LogWarning("[Factory] preview: no mesh found in " + fbxRel); return; }
            if (overrideMesh != null && !AssetDatabase.Contains(mesh))   // a runtime clone can't be referenced by the saved prefab -> persist it as an asset first
            {
                string mp = resDir + "/" + name + "_PreviewMesh.asset";
                AssetDatabase.DeleteAsset(mp); AssetDatabase.CreateAsset(mesh, mp); AssetDatabase.SaveAssets();
            }
            var mat = new Material(Shader.Find("Standard")) { name = name + "_PreviewMat", mainTexture = atlas };
            mat.SetFloat("_Glossiness", 0f); mat.SetFloat("_Metallic", 0f);   // matte (Standard defaults to 0.5 smoothness -> glossy on dark textures, e.g. tyres)
            string matPath = resDir + "/" + name + "_PreviewMat.mat";
            AssetDatabase.DeleteAsset(matPath); AssetDatabase.CreateAsset(mat, matPath);
            // Mesh on a CHILD: the registry Rotation offset, then a fixed 180° world-X flip so the preview lands upright
            // (the offset alone flattens the model but upside-down). Preview-only — the game orients animated units via the pawn.
            var root = new GameObject(name + "_Preview");
            var child = new GameObject("Mesh");
            child.transform.SetParent(root.transform);
            child.transform.localRotation = Quaternion.Euler(0f, 90f, 0f) * Quaternion.Euler(180f, 0f, 0f) * Quaternion.Euler(rotationEuler);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            // ONE MATERIAL PER SUBMESH: Unity renders only as many submeshes as the renderer has materials, so a
            // multi-material mesh (the drone keeps 75 slots) with a single material previewed as just submesh 0 —
            // one propeller blade. Repeat the atlas material across every slot so the whole model shows.
            var mr = child.AddComponent<MeshRenderer>();
            var mats = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            mr.sharedMaterials = mats;
            string prefabPath = resDir + "/" + name + "_Preview.prefab";
            AssetDatabase.DeleteAsset(prefabPath);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log($"[Factory] {name} preview prefab -> {prefabPath}  (STATIC, textured, NOT injected). " +
                $"Mesh = {mesh.vertexCount} verts / {mesh.triangles.Length / 3} tris — select it to inspect; " +
                "lower 'Reduce to ~tris' + re-bake to cut further.");
        }
        catch (Exception e) { Debug.LogWarning("[Factory] preview prefab: " + e.Message); }
    }

    // Blender: slim a rigged/animated model into a decimated FBX that keeps its armature + one clip (Tools/rig_anim.py).
    // `rotation` (degrees; x = pitch/stand-up, y = heading, z = roll) is baked INTO THE RIG — the game orients animated
    // units by the rig, so a rig that round-trips lying down (the Combine soldier) can only be fixed here, at bake time.
    // `convertRig` is the EXPLICIT pipeline switch: true routes through the raw-rig conversion (rest-normalize, rename,
    // scale fold, clean-unit export) — it used to be inferred from rotation != 0, which made Rotation a landmine.
    static bool RigAnimViaBlender(string src, string outFbx, int targetTris, string bonePrefixes, string clipName, string albedoOut, bool keepMaterials, Vector3 rotation, bool convertRig)
    {
        string proj = Directory.GetParent(Application.dataPath).FullName;
        string script = Path.Combine(proj, "Tools", "rig_anim.py");
        if (!File.Exists(script)) { Debug.LogError("[Factory] bundled rig_anim.py missing: " + script); return false; }
        string blender = FindBlender();
        var inv = System.Globalization.CultureInfo.InvariantCulture;   // never the OS locale — a Dutch comma-decimal would corrupt the arg
        string rotArg = string.Format(inv, "{0:0.###},{1:0.###},{2:0.###}", rotation.x, rotation.y, rotation.z);
        string args = $"--background --python \"{script}\" -- \"{src}\" \"{outFbx}\" {Mathf.Max(1, targetTris)} \"{bonePrefixes ?? ""}\" \"{clipName ?? ""}\" \"{albedoOut ?? ""}\" {(keepMaterials ? "1" : "0")} \"{rotArg}\" {(convertRig ? "1" : "0")}";
        var psi = new System.Diagnostics.ProcessStartInfo(blender, args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        try
        {
            if (File.Exists(outFbx)) File.Delete(outFbx);
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (!RunBounded(p, ProcTimeoutMs, out string o, out string e))
                { Debug.LogError($"[Factory] a bake sub-process timed out (~{ProcTimeoutMs / 1000}s) and was killed (stuck process or over-heavy model)."); return false; }
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
        // The resource name is used as a folder name, a filename prefix, AND an unquoted-friendly converter argument.
        // A space (or other path-hostile char) breaks the glbconv arg split — "Attack Helicopter" made glbconv parse
        // "Helicopter" as the grid int (FormatException). Reject it up front with a clear message instead of failing
        // cryptically deep in a shell-out. Convention: single token, e.g. "StealthHelicopter" (matches every model).
        foreach (char c in cfg.resourceName)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                return Fail($"resource name '{cfg.resourceName}' contains an invalid character ('{c}'). " +
                            "Use letters, digits, '_' or '-' only — no spaces (e.g. 'AttackHelicopter').");
        string name = cfg.resourceName;
        float size = cfg.size > 0f ? cfg.size : 5f;
        float smoothing = cfg.smoothingAngle > 0f ? cfg.smoothingAngle : 20f;

        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        // Bake INPUTS (OBJ / albedos / preview prefab) go OUTSIDE Resources, into Assets/FactorySource. Unity force-
        // includes EVERYTHING under a Resources folder in the build — even unreferenced source — which bloated the mod
        // bundle (416MB) AND shipped the raw extractable source of licensed assets (a Fab-license breach). The baked
        // OUTPUTS (_Skeleton/_Atlas/_ModelMesh/_Mat/_Model.prefab) stay in Resources ROOT: they're self-contained
        // (reference only each other, never the source) and MUST ship + load by GUID. FactorySource is not shipped.
        string resDir = "Assets/FactorySource/" + name;
        if (!AssetDatabase.IsValidFolder("Assets/FactorySource")) AssetDatabase.CreateFolder("Assets", "FactorySource");
        if (!AssetDatabase.IsValidFolder(resDir)) AssetDatabase.CreateFolder("Assets/FactorySource", name);
        string objPath = resDir + "/" + name + ".obj";
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        bool haveObj = File.Exists(Path.Combine(projRoot, objPath)) || File.Exists(Path.Combine(projRoot, resDir + "/" + name + ".fbx"));

        // --- 0) (re)import the model. Skipped when "Reuse extracted files" is on AND files already exist, so manual
        //        OBJ/albedo edits survive a re-bake. Still imports on the first bake, or whenever reuse is off. ---
        if (!string.IsNullOrEmpty(cfg.modelFile) && (!cfg.reuseExtracted || !haveObj))
        {
            string srcFile = cfg.modelFile;
            // Optional PRE-BAKE PREP (strip + reduce) in ONE Blender session: import once, delete named objects (+ their
            // children) so the donor's rotor shows through, then per-object quadric-decimate to ~targetTris (thin parts
            // survive), and export one GLB the rest of the pipeline bakes. Running both in a single Blender process — vs
            // the old two — saves a Blender startup + an intermediate GLB export/import round-trip (~24% off the heavy
            // AH-1's Blender time). Either step is skipped when unset (empty Strip parts / targetTris 0); the survivor
            // runs alone. targetTris is a CEILING, not a quota (a model already under it passes through unchanged);
            // double-sided doubles the baked geometry, so we HALVE the target then (default 24000 -> 12000), keeping the
            // field a single "budget" the user sets once. NOTE: there is NO hard per-model cap in the engine — the real
            // budget is the shared pawn-layer pool (~1M verts; HAF docs/Vertex-Budget.md), so higher targets are fine.
            bool wantStrip = !string.IsNullOrWhiteSpace(cfg.stripParts);
            bool wantReduce = cfg.targetTris > 0;
            if (wantStrip || wantReduce)
            {
                if (!BlenderAvailable()) return Fail((wantStrip ? "'Strip parts'" : "'Reduce to tris'") +
                    " needs Blender installed (auto-detected, or set EditorPrefs 'ENC.blenderPath').");
                int effTarget = cfg.doubleSided ? Mathf.Max(1, cfg.targetTris / 2) : cfg.targetTris;
                if (wantReduce && cfg.doubleSided) Debug.Log($"[Factory] reduce target {cfg.targetTris} -> {effTarget} tris (double-sided halves it; it doubles the baked geometry)");
                string prepped = Path.Combine(Path.GetTempPath(), name + "_prepped.glb");
                if (!PrepViaBlender(srcFile, prepped, wantStrip ? cfg.stripParts : "", wantReduce ? effTarget : 0))
                    return Fail("model prep (strip / reduce) failed (see console)");
                srcFile = prepped;
            }
            string ext = Path.GetExtension(srcFile).ToLowerInvariant();
            if (ext == ".blend")
            {
                // .blend isn't a transfer format — convert it to GLB via headless Blender first, then fall through the
                // normal GLB path. Needs Blender installed (like GLB needs dotnet). Textures embed if the blend's material
                // is a normal Principled-BSDF setup; very old materials may export untextured (supply the albedo manually).
                string tmpGlb = Path.Combine(Path.GetTempPath(), name + "_fromblend.glb");
                if (!ConvertBlend(srcFile, tmpGlb)) return Fail("Blender .blend -> GLB conversion failed (see console)");
                string fsDir = Path.Combine(Application.dataPath, "FactorySource", name);
                Directory.CreateDirectory(fsDir);
                if (!ConvertGlb(tmpGlb, fsDir, name, cfg.convertGrid)) return Fail("GLB conversion failed (see console)");
            }
            else if (ext == ".glb" || ext == ".gltf")
            {
                string fsDir = Path.Combine(Application.dataPath, "FactorySource", name);
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
            // Fallback: objPath guessed .obj but the extracted source may be the other supported extension (e.g. a
            // reused FBX whose import block was skipped). Rebuild the asset path from resDir + the found filename —
            // the file is by construction inside resDir. (This used to substring on "/Resources/", a leftover from
            // before sources moved to FactorySource — it threw Substring(-1) instead of recovering.)
            var found = Directory.GetFiles(Path.Combine(Directory.GetParent(Application.dataPath).FullName, resDir))
                .Select(p => p.Replace('\\', '/')).FirstOrDefault(p => p.EndsWith(name + ".obj") || p.EndsWith(name + ".fbx"));
            objPath = found != null ? resDir + "/" + Path.GetFileName(found) : objPath;
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
             || imp.weldVertices || imp.importTangents != ModelImporterTangents.None))
        {
            imp.importNormals = wantImport; imp.normalSmoothingAngle = smoothing;
            imp.weldVertices = false;
            imp.importTangents = ModelImporterTangents.None;   // normal maps are neutralized at runtime -> tangents are dead weight (16 bytes/vert in mesh + skeleton, plus extra vert splits). Drop them.
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
            atlasRects = packedAtlas.PackTextures(albs, 2, cfg.atlasMaxDim > 0 ? cfg.atlasMaxDim : AtlasMaxDimDefault);   // cap packing at the final size (was 4096) — no need to pack huge then shrink
            foreach (var a in albs) if (a != null) UnityEngine.Object.DestroyImmediate(a);   // E8: free the packed source albedos — Unity objects don't GC, so they leak (tens of MB) per bake until a domain reload
            // Force opaque AND (unless keepBlack) repaint near-black regions neutral grey. Two sources of near-black:
            // (a) unused UV "dead-zones" inside a source albedo (e.g. the zeppelin hull texture's black corner that the
            // hull top samples), and (b) the gaps PackTextures leaves between packed islands — faces whose UVs land on
            // either render BLACK. BUT this can't tell those from an INTENTIONALLY black material (a glossy canopy, a
            // dark cockpit), which it would flatten to grey. keepBlack skips the remap for models that want true black.
            var apx = packedAtlas.GetPixels32();
            AdjustAlbedo(apx, cfg.albedoBrightness, cfg.albedoSaturation);   // optional brightness/saturation lift (baked in)
            for (int i = 0; i < apx.Length; i++)
            {
                apx[i].a = 255;
                if (!cfg.keepBlack && apx[i].r < 32 && apx[i].g < 32 && apx[i].b < 32) { apx[i].r = 160; apx[i].g = 160; apx[i].b = 168; }
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
                            // Atlas cells have no texture WRAP, but many source models park each material's UV
                            // island in a distant integer tile (this AH-1 runs U up to ~23) relying on wrap. Fold
                            // every UV into [0,1) so it lands inside its packed rect instead of flying into the black
                            // gaps between islands. An island that sits wholly within one tile has all its verts
                            // subtract the same integer -> no distortion; only a triangle straddling a tile edge
                            // smears slightly, which is unavoidable when emulating wrap on a packed atlas.
                            u.x -= Mathf.Floor(u.x); u.y -= Mathf.Floor(u.y);
                            cUV.Add(new Vector2(r.x + u.x * r.width, r.y + u.y * r.height));
                            cNorm.Add(mNorm ? local.MultiplyVector(nr[oldI]).normalized : Vector3.up);
                        }
                        cTris.Add(ni);
                    }
                    haveUV |= mUV; haveNorm = true;
                }
            }
        }
        // E3: a rigged/skinned-only FBX imports as SkinnedMeshRenderers, which this static combine (MeshFilter-only) never
        // sees -> cVerts is empty. Without this guard the bake would complete and ship a 0-vertex, INVISIBLE unit with a
        // valid-looking registry entry (only trace: "verts=0" in a log). Fail loudly; rigged models belong on the Animated path.
        if (cVerts.Count == 0)
            return Fail($"{name}: the static combine found no mesh geometry (0 vertices) — the model likely imports as " +
                        "skinned meshes (a rigged FBX) the static path can't read. Use the Animated bake, or check the model imported correctly.");
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
        var atlas = multiMat ? packedAtlas : BuildAtlas(resDir, name, cfg.albedoBrightness, cfg.albedoSaturation);
        atlas = FinalizeAtlas(atlas, name, cfg.atlasMaxDim);   // cap resolution + DXT1-compress so the shipped _Atlas.asset isn't 100s of MB
        // delete-first like the animated path: CreateAsset over an existing _Atlas.asset can keep the STALE atlas (old
        // skin) on a re-bake -- the same in-place-overwrite trap the mesh/prefab/skeleton delete-first block below fixes.
        AssetDatabase.DeleteAsset("Assets/Resources/" + name + "_Atlas.asset");
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
        // Re-bake = clean slate: delete prior outputs so nothing is overwritten IN PLACE. In-place overwrite leaves Unity
        // serving a stale cached mesh/prefab to the skeleton bake below -> the shipped skeleton lags a bake behind and the
        // ship renders 90 deg off in-game even though the (force-reimported) preview looks right. Fresh assets == first bake.
        foreach (var old in new[] { "_ModelMesh.asset", "_Mat.mat", "_Model.prefab", "_Skeleton.asset" })
            AssetDatabase.DeleteAsset("Assets/Resources/" + name + old);
        // E7: a prior ANIMATED bake left <name>_Preview.prefab (+ _PreviewMesh/_PreviewMat) in FactorySource, and the
        // window's LoadPreview PREFERS that over the static _Model.prefab whenever it exists — so without this a static
        // re-bake would keep showing the OLD animated model in the preview. The static path has no _Preview of its own
        // (its preview IS the _Model prefab), so delete the stale animated-preview assets to fall through to it.
        foreach (var pv in new[] { "_Preview.prefab", "_PreviewMesh.asset", "_PreviewMat.mat" })
            AssetDatabase.DeleteAsset(resDir + "/" + name + pv);
        AssetDatabase.CreateAsset(mesh, "Assets/Resources/" + name + "_ModelMesh.asset");

        var mat = new Material(Shader.Find("Standard")) { name = name + "_Mat", mainTexture = atlas };
        mat.SetFloat("_Glossiness", 0f); mat.SetFloat("_Metallic", 0f);   // matte (Standard defaults to 0.5 smoothness -> glossy on dark textures, e.g. tyres)
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
        // A re-bake overwrites the mesh/prefab IN PLACE, so LoadAssetAtPath below can return Unity's STALE cached copy --
        // which makes the skeleton bake from last bake's geometry and ship a skeleton lagging a bake behind (wrong
        // orientation in-game while the preview looks right). Force a synchronous reimport so the skeleton reads fresh.
        AssetDatabase.ImportAsset("Assets/Resources/" + name + "_ModelMesh.asset", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

        var skelType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Mercury.Animation.Skeleton");
        if (skelType == null) return Fail("Amplitude Skeleton type not found");
        string skelPath = "Assets/Resources/" + name + "_Skeleton.asset";
        var skel = ScriptableObject.CreateInstance(skelType);
        AssetDatabase.CreateAsset(skel, skelPath);
        if (!InvokeReq(skelType, "SetPrefab", new[] { typeof(GameObject) }, skel, new object[] { prefab }, out var err)) return Fail(err);
        if (!InvokeReq(skelType, "Reimport", Type.EmptyTypes, skel, null, out err)) return Fail(err);
        EditorUtility.SetDirty(skel);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        string skelGuid = AmplitudeGuid(skel), atlasGuid = AmplitudeGuid(atlas);
        // empty GUID = the SDK skeleton bake produced nothing -> fail loudly instead of writing a dead registry entry.
        if (string.IsNullOrEmpty(skelGuid) || skelGuid == "0,0,0,0") return Fail($"{name}: skeleton bake produced an empty GUID (SetPrefab/Reimport did nothing).");
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
            // Match png/jpg/jpeg (all decodable by Texture2D.LoadImage). glbconv emits jpgs for models whose GLB embeds
            // JPEG (e.g. the AH-1's per-part albedos); a solid-colour swatch is a .tga, which LoadImage can't decode, so
            // those are deliberately left to the mat.mainTexture fallback below.
            var pngs = Directory.GetFiles(fsDir)
                .Where(p => { var e = Path.GetExtension(p).ToLowerInvariant(); return e == ".png" || e == ".jpg" || e == ".jpeg"; })
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

    // Bake-time albedo tone control. The injection path ships a FLAT albedo (the donor's PBR normal/metallic/roughness
    // maps are neutralized so its camo can't bleed through), so a model that relied on shiny metal or lived in a dark
    // texture reads muddy in-game. brightness multiplies RGB; saturation scales colour around per-pixel luminance
    // (0 = greyscale, 1 = unchanged, >1 = punchier). Both default to 1 (no-op — existing bakes are byte-identical).
    static void AdjustAlbedo(Color32[] px, float brightness, float saturation)
    {
        float b = brightness <= 0f ? 1f : brightness;   // guard bad/zero config
        float s = saturation < 0f ? 1f : saturation;
        if (Mathf.Approximately(b, 1f) && Mathf.Approximately(s, 1f)) return;
        bool doS = !Mathf.Approximately(s, 1f), doB = !Mathf.Approximately(b, 1f);
        for (int i = 0; i < px.Length; i++)
        {
            float r = px[i].r, g = px[i].g, bl = px[i].b;
            if (doS) { float lum = r * 0.299f + g * 0.587f + bl * 0.114f; r = lum + (r - lum) * s; g = lum + (g - lum) * s; bl = lum + (bl - lum) * s; }
            if (doB) { r *= b; g *= b; bl *= b; }
            px[i].r = (byte)Mathf.Clamp(Mathf.RoundToInt(r), 0, 255);
            px[i].g = (byte)Mathf.Clamp(Mathf.RoundToInt(g), 0, 255);
            px[i].b = (byte)Mathf.Clamp(Mathf.RoundToInt(bl), 0, 255);
        }
    }

    // Shrink + compress the baked atlas before it's saved. The runtime path builds the atlas UNCOMPRESSED (RGBA32) and
    // packing can reach 4096x8192 -> a single _Atlas.asset hit 128MB, dominating the shipped bundle. A unit is ~80px at
    // map zoom, so cap the longest side at AtlasMaxDim and block-compress (DXT1: 6:1; our atlas is opaque so no alpha is
    // needed). UVs are fractional, so downscaling never disturbs them. Net: a 128MB atlas -> ~1MB, no visible loss.
    const int AtlasMaxDimDefault = 512;
    static Texture2D FinalizeAtlas(Texture2D atlas, string name, int maxDim)
    {
        if (atlas == null) return atlas;
        if (maxDim <= 0) maxDim = AtlasMaxDimDefault;   // guard unset/old-registry
        int w = atlas.width, h = atlas.height, mx = Mathf.Max(w, h);
        if (mx > maxDim)
        {
            float s = maxDim / (float)mx;
            int nw = Mathf.Max(4, (Mathf.RoundToInt(w * s) / 4) * 4);   // multiple of 4 for block compression
            int nh = Mathf.Max(4, (Mathf.RoundToInt(h * s) / 4) * 4);
            var rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active; Graphics.Blit(atlas, rt); RenderTexture.active = rt;
            var scaled = new Texture2D(nw, nh, TextureFormat.RGBA32, false) { name = atlas.name };
            scaled.ReadPixels(new Rect(0, 0, nw, nh), 0, 0); scaled.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(atlas);
            atlas = scaled; w = nw; h = nh;
        }
        if ((w % 4) == 0 && (h % 4) == 0)   // DXT1 needs multiple-of-4 dims; tiny odd atlases stay uncompressed
        {
            EditorUtility.CompressTexture(atlas, TextureFormat.DXT1, TextureCompressionQuality.Normal);
            atlas.Apply(false, false);
        }
        Debug.Log($"[Factory] {name} atlas finalized -> {atlas.width}x{atlas.height} {atlas.format}");
        return atlas;
    }

    static Texture2D BuildAtlas(string resDir, string name, float brightness, float saturation)
    {
        // Read the extracted albedo straight off disk. With "Reuse extracted files" on, this is the modder's hand-edited
        // copy (e.g. a texture fix done in paint.net), so their edits flow into the atlas untouched.
        string fsDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, resDir);
        // Pick the canonical albedo png. Exclude sidecar copies (a hand-made "-backup", or the converter's ".orig") and
        // prefer the shortest matching name so "<mat>_albedo.png" wins over "<mat>_albedo-backup.png" regardless of
        // directory order — a stray backup used to sort first ('-' < '.') and silently get baked instead.
        string albedo = Directory.Exists(fsDir)
            ? Directory.GetFiles(fsDir)
                .Where(p => { var e = Path.GetExtension(p).ToLowerInvariant(); return e == ".png" || e == ".jpg" || e == ".jpeg"; })
                .Where(p => { var f = Path.GetFileNameWithoutExtension(p).ToLowerInvariant(); return f.Contains("albedo") && !f.Contains("backup") && !f.Contains("orig"); })
                .OrderBy(p => Path.GetFileName(p).Length)
                .FirstOrDefault()
            : null;
        Texture2D atlas;
        if (albedo != null && File.Exists(albedo))
        {
            atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name + "_Atlas" };
            atlas.LoadImage(File.ReadAllBytes(albedo));
            var px = atlas.GetPixels32();
            AdjustAlbedo(px, brightness, saturation);   // optional brightness/saturation lift (baked in)
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            atlas.SetPixels32(px); atlas.Apply();
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

    // Read the glbconv MTL and return its materials IN ORDER, each with its base-colour albedo loaded. The MTL is the
    // authoritative material list/order for the atlas (submesh order matches it). Empty list => single-material fallback.
    static List<KeyValuePair<string, Texture2D>> LoadOrderedAlbedos(string fsResDir, string name)
    {
        var list = new List<KeyValuePair<string, Texture2D>>();
        string mtl = Path.Combine(fsResDir, name + ".mtl");
        if (!File.Exists(mtl)) return list;
        string cur = null;
        foreach (var raw in File.ReadAllLines(mtl))
        {
            var t = raw.Trim();
            if (t.StartsWith("newmtl ")) cur = t.Substring(7).Trim();
            else if (t.StartsWith("map_Kd ") && cur != null)
            {
                string p = Path.Combine(fsResDir, t.Substring(7).Trim());
                if (File.Exists(p)) { var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = cur }; tex.LoadImage(File.ReadAllBytes(p)); list.Add(new KeyValuePair<string, Texture2D>(cur, tex)); }
                cur = null;
            }
        }
        return list;
    }

    static string SimplifyMat(string s) => (s ?? "").ToLowerInvariant().Replace("material", "").Replace("mat", "").Replace("_", "").Replace(" ", "").Replace(":", "");

    // Pack the per-material albedos into ONE atlas and remap the FBX's SKINNED mesh UVs per-submesh into their packed rect,
    // so each part samples its own texture. The mesh is CLONED (we never mutate the imported asset) and re-assigned to the
    // renderer, so the skeleton bake (SetPrefab, run after this) reads the remapped UVs. Submesh->rect is matched by material
    // base-name, falling back to submesh index (submesh order == MTL order for a glbconv/rig_anim pair from the same GLB).
    static Texture2D BuildMultiAtlasAndRemap(GameObject fbxGo, List<KeyValuePair<string, Texture2D>> orderedAlb, string name, BakeConfig cfg, out Mesh remappedPreviewMesh)
    {
        remappedPreviewMesh = null;
        var albs = orderedAlb.Select(kv => kv.Value).ToArray();
        var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name + "_Atlas" };
        var rects = atlas.PackTextures(albs, 2, cfg.atlasMaxDim > 0 ? cfg.atlasMaxDim : AtlasMaxDimDefault);
        var apx = atlas.GetPixels32();
        AdjustAlbedo(apx, cfg.albedoBrightness, cfg.albedoSaturation);
        for (int i = 0; i < apx.Length; i++) { apx[i].a = 255; if (!cfg.keepBlack && apx[i].r < 32 && apx[i].g < 32 && apx[i].b < 32) { apx[i].r = 160; apx[i].g = 160; apx[i].b = 168; } }
        atlas.SetPixels32(apx); atlas.Apply();
        Debug.Log($"[Factory] {name} ANIMATED MULTI-MATERIAL: {albs.Length} materials [{string.Join(", ", orderedAlb.Select(kv => kv.Key))}] -> packed atlas {atlas.width}x{atlas.height}");
        foreach (var a in albs) if (a != null) UnityEngine.Object.DestroyImmediate(a);   // E8: free the packed source albedos (packing copied them into the atlas); only orderedAlb's KEYS are used below

        var baseNames = orderedAlb.Select(kv => SimplifyMat(kv.Key)).ToArray();
        foreach (var smr in fbxGo.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var srcMesh = smr.sharedMesh;
            if (srcMesh == null) continue;
            var mesh = UnityEngine.Object.Instantiate(srcMesh); mesh.name = srcMesh.name;
            var uv = mesh.uv;
            if (uv == null || uv.Length != mesh.vertexCount) { smr.sharedMesh = mesh; continue; }
            var mats = smr.sharedMaterials;
            var doneVert = new bool[uv.Length];   // remap each vertex once (parts don't share verts across submeshes after a join)
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int ri = -1;
                var sm = (mats != null && s < mats.Length) ? mats[s] : null;
                if (sm != null)
                {
                    // Match by simplified material name, EXACT first: the loose Contains-both-ways match alone lets a
                    // material "Body" grab "Body_Trim"'s atlas rect (simplified "body" is a substring of "bodytrim"),
                    // mapping the wrong texture onto that submesh. Try exact, then substring, else the index below.
                    string bn = SimplifyMat(sm.name);
                    ri = System.Array.FindIndex(baseNames, b => b.Length > 0 && b == bn);
                    if (ri < 0) ri = System.Array.FindIndex(baseNames, b => b.Length > 0 && (bn.Contains(b) || b.Contains(bn)));
                }
                if (ri < 0) ri = s;   // fall back to index (submesh order == MTL order)
                if (ri < 0 || ri >= rects.Length) { Debug.LogWarning($"[Factory] {name} submesh {s} ('{(sm != null ? sm.name : "null")}') no atlas rect — left unmapped"); continue; }
                var r = rects[ri];
                // Fold each UV into [0,1) first: atlas cells have no wrap, but source models often park a material's
                // island in a distant integer tile relying on texture wrap — unfolded it flies outside its rect into
                // the black gaps. Whole-in-one-tile islands subtract a uniform integer (no distortion); see the static path.
                foreach (int vi in mesh.GetTriangles(s)) if (!doneVert[vi]) { float fu = uv[vi].x - Mathf.Floor(uv[vi].x), fv = uv[vi].y - Mathf.Floor(uv[vi].y); uv[vi] = new Vector2(r.x + fu * r.width, r.y + fv * r.height); doneVert[vi] = true; }
                Debug.Log($"[Factory]   submesh {s} '{(sm != null ? sm.name : "null")}' -> rect[{ri}] '{orderedAlb[ri].Key}'");
            }
            mesh.uv = uv;
            // MERGE the submeshes into ONE so the whole mesh draws with the single atlas material. A renderer (and the game's
            // skeleton draw) only renders submesh i if a material slot i exists, and we supply ONE atlas material -> otherwise
            // every submesh past 0 goes invisible (only the barrel showed). UVs are already remapped, so one submesh is correct.
            var allTris = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) allTris.AddRange(mesh.GetTriangles(s));
            mesh.subMeshCount = 1;
            mesh.SetTriangles(allTris, 0);
            // Amplitude's skeleton importer reads tangents off this REBUILT mesh; regenerate them (aligned to the remapped
            // UVs) so it never gets an empty tangent array -> IndexOutOfRange. Needs normals+UVs, both present here.
            mesh.RecalculateTangents();
            smr.sharedMaterials = new[] { mats != null && mats.Length > 0 ? mats[0] : null };   // one slot to match the one submesh
            smr.sharedMesh = mesh;
            remappedPreviewMesh = mesh;   // hand the corrected mesh to the preview so it shows all parts with the right UVs
        }
        return atlas;
    }

    static bool ConvertGlb(string glb, string outDir, string name, int grid)
    {
        string proj = Directory.GetParent(Application.dataPath).FullName;
        string tools = Path.Combine(proj, "Tools", "glbconv");
        string exe = Path.Combine(tools, "glbconv.exe");
        string dll = Path.Combine(tools, "glbconv.dll");
        string args = $"\"{glb}\" \"{outDir}\" \"{name}\" {Mathf.Max(0, grid)}";

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
                if (!RunBounded(p, ProcTimeoutMs, out string o, out string e))
                { Debug.LogError($"[Factory] a bake sub-process timed out (~{ProcTimeoutMs / 1000}s) and was killed (stuck process or over-heavy model)."); return false; }
                if (!string.IsNullOrWhiteSpace(o)) Debug.Log("[glbconv] " + o.Trim());
                if (!string.IsNullOrWhiteSpace(e)) Debug.LogWarning("[glbconv] " + e.Trim());
                return p.ExitCode == 0;
            }
        }
        catch (Exception ex) { Debug.LogError("[Factory] could not run converter ('" + psi.FileName + "'): " + ex.Message + "\n(GLB path uses Tools/glbconv/glbconv.exe; the dev fallback needs a dotnet on PATH or EditorPrefs 'ENC.dotnetPath'.)"); return false; }
    }

    // Hard cap on any Blender/glbconv shell-out (ms). A stuck child (bad model, driver stall, a modal dialog it shows
    // despite --background) would otherwise block the editor's main thread on the pipe read FOREVER; this kills it and
    // fails the bake cleanly instead. 3 min is well above any legit bake (they finish in seconds); bump it if a genuinely
    // heavy decimation on a slow machine ever trips it.
    const int ProcTimeoutMs = 180000;

    // Run an already-configured redirected process with that hard timeout. BOTH streams are drained on pool threads
    // (reading stdout-then-stderr on this thread deadlocks: the child fills its ~4KB stderr pipe buffer, blocks writing,
    // never exits, stdout never EOFs, and we hang forever). The wait is bounded, so a hung child can't freeze the editor.
    // Returns false (after killing the child) on timeout; otherwise the process has exited and stdout/stderr are filled.
    static bool RunBounded(System.Diagnostics.Process p, int timeoutMs, out string stdout, out string stderr)
    {
        stdout = ""; stderr = "";
        var outTask = System.Threading.Tasks.Task.Run(() => p.StandardOutput.ReadToEnd());
        var errTask = System.Threading.Tasks.Task.Run(() => p.StandardError.ReadToEnd());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return false; }   // the process itself hung -> killed
        // E4: WaitForExit(timeout) returns as soon as the PROCESS exits, but stdout/stderr stay open until EVERY handle to
        // their write end is closed — including one a grandchild (a Blender helper) may have inherited. ReadToEnd only
        // returns at pipe EOF, so awaiting the drain tasks UNBOUNDED (the old GetResult()) could hang the editor main
        // thread forever — the exact freeze this timeout exists to prevent. Bound the drain to the remaining budget too;
        // on timeout, take whatever was captured and move on (the process already exited cleanly).
        int remaining = Math.Max(3000, timeoutMs - (int)sw.ElapsedMilliseconds);
        bool drained; try { drained = System.Threading.Tasks.Task.WaitAll(new[] { outTask, errTask }, remaining); } catch { drained = true; }
        stdout = outTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? outTask.Result : "";
        stderr = errTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? errTask.Result : "";
        if (!drained)
        {
            // reads are still blocked on an inherited-open pipe; don't hang. Observe any eventual fault (the caller
            // disposes the process, which closes the streams and makes the leaked ReadToEnd throw) so it isn't unobserved.
            outTask.ContinueWith(t => { var _ = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            errTask.ContinueWith(t => { var _ = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            Debug.LogWarning("[Factory] a bake sub-process exited but left its output pipe open (a grandchild likely inherited it); continued with partial output instead of hanging.");
        }
        return true;   // the process exited cleanly; report success even if a stray pipe kept us from fully draining it
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

    // prep_model.py — strip named objects (+ their children) AND/OR per-object quadric-decimate to ~targetTris, all in
    // ONE headless Blender session, exporting a single GLB the Factory then bakes. This replaces the old two-pass
    // strip-then-reduce (two Blender startups + an intermediate GLB round-trip) with one, cutting ~24% off a heavy
    // model's Blender time. substrings "" = skip strip; targetTris <= 0 = skip reduce (so either step can run alone).
    static bool PrepViaBlender(string src, string outGlb, string substrings, int targetTris)
    {
        if (!File.Exists(src)) { Debug.LogError("[Factory] prep: model file not found: " + src); return false; }
        string proj = Directory.GetParent(Application.dataPath).FullName;
        string script = Path.Combine(proj, "Tools", "prep_model.py");
        if (!File.Exists(script)) { Debug.LogError("[Factory] bundled prep_model.py missing: " + script); return false; }
        string blender = FindBlender();
        var psi = new System.Diagnostics.ProcessStartInfo(blender,
            $"--background --python \"{script}\" -- \"{src}\" \"{outGlb}\" \"{substrings ?? ""}\" {Mathf.Max(0, targetTris)}")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        try
        {
            if (File.Exists(outGlb)) File.Delete(outGlb);
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (!RunBounded(p, ProcTimeoutMs, out string o, out string e))
                { Debug.LogError($"[Factory] model prep timed out (~{ProcTimeoutMs / 1000}s) and was killed (stuck process or over-heavy model)."); return false; }
                if (!string.IsNullOrWhiteSpace(o)) Debug.Log("[prep] " + o.Trim());
                if (!string.IsNullOrWhiteSpace(e)) Debug.LogWarning("[prep] " + e.Trim());
                if (p.ExitCode != 0 || !File.Exists(outGlb)) { Debug.LogError("[Factory] Blender prep produced no GLB (exit " + p.ExitCode + ")."); return false; }
                return true;
            }
        }
        catch (Exception ex) { Debug.LogError("[Factory] could not run Blender prep ('" + blender + "'): " + ex.Message); return false; }
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
                if (!RunBounded(p, ProcTimeoutMs, out string o, out string e))
                { Debug.LogError($"[Factory] a bake sub-process timed out (~{ProcTimeoutMs / 1000}s) and was killed (stuck process or over-heavy model)."); return false; }
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

    // Invoke a required reflected SDK method, or report failure. A missing method (Amplitude API rename) must NOT silently
    // no-op via `?.Invoke` and let the bake ship an empty asset with ok=true — the caller returns Fail(err) instead.
    static bool InvokeReq(Type t, string name, Type[] sig, object target, object[] args, out string err)
    {
        var m = t?.GetMethod(name, sig);
        if (m == null) { err = $"reflected SDK method not found: {(t == null ? "?" : t.Name)}.{name} — Amplitude API changed? Bake aborted (nothing written)."; return false; }
        m.Invoke(target, args);
        err = null;
        return true;
    }
    static Type[] SafeTypes(Assembly a) { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }

    [Serializable] class GltfNameNode { public string name; }
    [Serializable] class GltfNameDoc { public GltfNameNode[] nodes; }
    // Object/node names in a GLB/glTF model, for the Strip-parts Pick. Editor-side, so UnityEngine.JsonUtility is fine
    // (its empty-result quirk is only the GAME's Mono runtime). Other formats -> empty (type names by hand). Drops the
    // glTF "Object_NN" import wrappers and dedupes, so the Pick list is the meaningful part names.
    public static string[] ListModelObjectNames(string modelFile)
    {
        if (string.IsNullOrWhiteSpace(modelFile) || !File.Exists(modelFile)) return new string[0];
        string ext = Path.GetExtension(modelFile).ToLowerInvariant();
        try
        {
            string json;
            if (ext == ".glb")
            {
                var b = File.ReadAllBytes(modelFile);
                if (b.Length < 20) return new string[0];
                int jsonLen = BitConverter.ToInt32(b, 12);
                json = System.Text.Encoding.UTF8.GetString(b, 20, Mathf.Min(jsonLen, b.Length - 20));
            }
            else if (ext == ".gltf") json = File.ReadAllText(modelFile);
            else return new string[0];
            var doc = JsonUtility.FromJson<GltfNameDoc>(json);
            if (doc == null || doc.nodes == null) return new string[0];
            return doc.nodes.Where(n => n != null && !string.IsNullOrEmpty(n.name)
                                        && !n.name.StartsWith("Object_", StringComparison.OrdinalIgnoreCase))
                            .Select(n => n.name).Distinct().OrderBy(s => s).ToArray();
        }
        catch (Exception e) { Debug.LogWarning("[Factory] list object names: " + e.Message); return new string[0]; }
    }
}
