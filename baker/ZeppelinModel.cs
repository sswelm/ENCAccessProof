// ZeppelinModel.cs  (ENC editor) — bake a real airship FBX into an Amplitude Skeleton/MeshCollection (+ a texture
// atlas) that ships in the mod, rigged 100% to ONE "Base" bone. The BepInEx plugin loads the skeleton + atlas at
// runtime and points the zeppelin unit at them (mesh via MeshIndex swap, texture via _MainTex).
//
// MODEL: "Дирижабль HD" by MMD_SonicNewYear — Sketchfab, CC-BY (Attribution). Download the FBX + hull albedo PNGs
//        into Assets/Resources/Airship/  (R00_Baloon.fbx, R00_Baloon_hull_0_albedo.png, R00_Baloon_hull_1_albedo.png).
//
// What this does to make a third-party model engine-ready: combine all parts into ONE mesh, atlas the hull albedos
// and remap UVs, force the atlas opaque + paint over its near-black UV dead-zone, normalize scale/orientation, and
// fix the model's inconsistent winding (radial-outward) so it renders correctly single-sided.
//
// RUN:  Tools > Zeppelin > Build Zeppelin Skeleton    (then BUILD the mod)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class ZeppelinModel
{
    const string PrefabPath   = "Assets/Resources/Zeppelin_Model.prefab";
    const string SkeletonPath = "Assets/Resources/Zeppelin_Skeleton.asset";
    const string FbxPath      = "Assets/Resources/Airship/R00_Baloon.fbx";
    const string TexDir       = "Assets/Resources/Airship/";
    const string AtlasPath    = "Assets/Resources/Airship/Zeppelin_Atlas.asset";
    const float  TargetLength = 4.0f;                              // long-axis size (the procedural hull was ~4)
    static readonly Vector3 OrientEuler = new Vector3(0f, 180f, 0f); // 180° roll about the long axis -> right-side up

    [MenuItem("Tools/Zeppelin/Build Zeppelin Skeleton")]
    static void Build()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");

        // --- 1) load the real airship FBX (CC-BY, "Дирижабль HD" by MMD_SonicNewYear), atlas its hull albedos, and
        //        COMBINE every part into ONE mesh with remapped UVs. Then normalize: recenter, scale the long axis to
        //        TargetLength, align the long axis to Y (the missile's long axis; forward = -Y downstream via bindpose). ---
        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (fbx == null) { Debug.LogError("[Zeppelin] FBX not found at " + FbxPath + " — import it first."); return; }
        var inst = (GameObject)UnityEngine.Object.Instantiate(fbx);

        // atlas the two hull albedos (loaded readable straight from PNG bytes)
        var tex0 = LoadPng(TexDir + "R00_Baloon_hull_0_albedo.png");
        var tex1 = LoadPng(TexDir + "R00_Baloon_hull_1_albedo.png");
        var atlas = new Texture2D(2048, 2048, TextureFormat.RGBA32, false) { name = "Zeppelin_Atlas" };
        var rects = atlas.PackTextures(new[] { tex0, tex1 }, 2, 2048, false);
        // force opaque (the "AlbedoTransparency" alpha can read as cutout) AND kill the big near-black UV dead-zone in
        // the hull albedo -> the top of the hull maps onto it and renders black; replace it with hull grey.
        var ap = atlas.GetPixels32();
        for (int i = 0; i < ap.Length; i++)
        {
            ap[i].a = 255;
            if (ap[i].r < 32 && ap[i].g < 32 && ap[i].b < 32) { ap[i].r = 160; ap[i].g = 160; ap[i].b = 168; }
        }
        atlas.SetPixels32(ap); atlas.Apply();

        // gather every submesh, remap its UVs into the atlas rect for its material, assemble in model space
        var parts = new List<CombineInstance>();
        var rootInv = inst.transform.worldToLocalMatrix;
        int subCount = 0;
        foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
        {
            var m = mf.sharedMesh; if (m == null) continue;
            var rend = mf.GetComponent<MeshRenderer>();
            var mats = rend != null ? rend.sharedMaterials : null;
            var local = rootInv * mf.transform.localToWorldMatrix;
            var verts = m.vertices; var norms = m.normals; var uv = m.uv;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                string mn = (mats != null && s < mats.Length && mats[s] != null) ? mats[s].name.ToLower() : "";
                var r = mn.Contains("hull_0") ? rects[0] : rects[1];   // hull_0 -> tex0 region; everything else -> hull_1
                var sub = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = verts };
                if (norms != null && norms.Length == verts.Length) sub.normals = norms;
                if (uv != null && uv.Length == verts.Length)
                {
                    var newUv = new Vector2[uv.Length];
                    for (int i = 0; i < uv.Length; i++) newUv[i] = new Vector2(r.x + uv[i].x * r.width, r.y + uv[i].y * r.height);
                    sub.uv = newUv;
                }
                sub.triangles = m.GetTriangles(s);
                parts.Add(new CombineInstance { mesh = sub, transform = local });
                subCount++;
            }
        }
        UnityEngine.Object.DestroyImmediate(inst);

        var mesh = new Mesh { name = "Zeppelin_ModelMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.CombineMeshes(parts.ToArray(), true, true);

        // normalize: recenter, uniform-scale longest axis to TargetLength, align longest axis -> Y (+ OrientEuler tweak)
        mesh.RecalculateBounds();
        var bb = mesh.bounds; var size = bb.size;
        float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        float scl = longest > 0f ? TargetLength / longest : 1f;
        Quaternion align = (size.x >= size.y && size.x >= size.z) ? Quaternion.FromToRotation(Vector3.right, Vector3.up)
                         : (size.z >= size.x && size.z >= size.y) ? Quaternion.FromToRotation(Vector3.forward, Vector3.up)
                         : Quaternion.identity;
        Quaternion rot = Quaternion.Euler(OrientEuler) * align;
        var vv = mesh.vertices; var nn = mesh.normals;
        for (int i = 0; i < vv.Length; i++) vv[i] = rot * ((vv[i] - bb.center) * scl);
        if (nn != null && nn.Length == vv.Length) for (int i = 0; i < nn.Length; i++) nn[i] = rot * nn[i];
        mesh.vertices = vv; if (nn != null && nn.Length == vv.Length) mesh.normals = nn;

        // the model's winding AND its authored normals are inconsistent (faces wound inside-out -> culled/dark top).
        // The hull is convex, so "outward" = the direction from the centred origin to each triangle's centroid. Wind
        // every face that way (geometry-based, no reliance on the model's bad normals); RecalculateNormals (below)
        // then yields clean outward normals from the now-consistent winding.
        {
            var v = mesh.vertices; var t = mesh.triangles; int flipped = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Vector3 geo = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                Vector3 outward = v[a] + v[b] + v[c];   // centroid from the centred origin ~ outward for a convex hull
                if (Vector3.Dot(geo, outward) < 0f) { t[i + 1] = c; t[i + 2] = b; flipped++; }
            }
            mesh.triangles = t;
            Debug.Log($"[Zeppelin] winding-fixed {flipped}/{t.Length / 3} tris (radial-outward)");
        }
        Debug.Log($"[Zeppelin] FBX baked: submeshes={subCount}, verts={mesh.vertexCount}, atlas={atlas.width}x{atlas.height}, rawBounds={size}");

        // --- 2) rig: root -> Dummy_Root -> Base  (same shape as the cruise-missile rig) ---
        var root  = new GameObject("Zeppelin_Model");
        var dummy = new GameObject("Dummy_Root"); dummy.transform.SetParent(root.transform); dummy.transform.localPosition = Vector3.zero;
        var bone  = new GameObject("Base");       bone.transform.SetParent(dummy.transform);  bone.transform.localPosition = Vector3.zero;

        // skin 100% to Base, using the missile's bindpose (orients the hull nose-forward like the missile)
        var bind = new Matrix4x4();
        bind.SetRow(0, new Vector4(1f,  0f, 0f,  0f));
        bind.SetRow(1, new Vector4(0f,  0f, 1f,  0.101f));
        bind.SetRow(2, new Vector4(0f, -1f, 0f, -0.069f));
        bind.SetRow(3, new Vector4(0f,  0f, 0f,  1f));
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
        mesh.bindposes = new[] { bind };
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();   // the game's VertexEncodingFormat 6 needs tangents — missing => GPU hang
        AssetDatabase.CreateAsset(mesh, "Assets/Resources/Zeppelin_ModelMesh.asset");

        AssetDatabase.CreateAsset(atlas, AtlasPath);   // ship the atlas; the plugin loads it as the unit's _MainTex
        var mat = new Material(Shader.Find("Standard")) { name = "Zeppelin_ModelMat", mainTexture = atlas };
        AssetDatabase.CreateAsset(mat, "Assets/Resources/Zeppelin_ModelMat.mat");

        var meshGO = new GameObject("Unit_Era6_CruiseMissile_01"); meshGO.transform.SetParent(root.transform);
        meshGO.transform.localPosition = new Vector3(0f, 0.10f, -0.07f); meshGO.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
        var smr = meshGO.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh; smr.bones = new[] { bone.transform }; smr.rootBone = bone.transform; smr.sharedMaterial = mat; smr.updateWhenOffscreen = true;

        // generic Avatar so the rig is a valid animation target
        var anim = root.AddComponent<Animator>();
        anim.avatar = AvatarBuilder.BuildGenericAvatar(root, "");

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var prefabGuid = AssetDatabase.AssetPathToGUID(PrefabPath);

        // --- 3) bake the Amplitude Skeleton/MeshCollection from the prefab ---
        var skelType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Mercury.Animation.Skeleton");
        if (skelType == null) { Debug.LogError("[Zeppelin] Skeleton type not found."); return; }

        var skel = ScriptableObject.CreateInstance(skelType);
        AssetDatabase.CreateAsset(skel, SkeletonPath);
        // SetPrefab(GameObject) -> stores the prefab GUID, then Reimport() bakes bones + skinned mesh
        var setPrefab = skelType.GetMethod("SetPrefab", new[] { typeof(GameObject) });
        var reimport  = skelType.GetMethod("Reimport", Type.EmptyTypes);
        try { setPrefab?.Invoke(skel, new object[] { prefab }); reimport?.Invoke(skel, null); }
        catch (Exception e) { Debug.LogError("[Zeppelin] bake failed: " + e); }

        EditorUtility.SetDirty(skel);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        var skelGuid = AssetDatabase.AssetPathToGUID(SkeletonPath);

        // log the AMPLITUDE runtime GUID (the exact value RegisterMeshCollection/LoadAsset use at runtime),
        // so the plugin doesn't have to guess the Unity->Amplitude encoding.
        string ampInfo = "(Amplitude AssetDatabase not found)";
        var adbType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Framework.Asset.AssetDatabase");
        if (adbType != null)
        {
            var getGuid = adbType.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) });
            object sg = null, pg = null, ag = null;
            var atlasAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);
            try { sg = getGuid?.Invoke(null, new object[] { skel }); pg = getGuid?.Invoke(null, new object[] { prefab }); ag = getGuid?.Invoke(null, new object[] { atlasAsset }); } catch (Exception e) { ampInfo = "GetAssetGUID failed: " + e.Message; }
            if (sg != null)
            {
                string Fields(object g)
                {
                    if (g == null) return "(null)";
                    var sb = new System.Text.StringBuilder();
                    foreach (var f in g.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        sb.Append($"{f.Name}({f.FieldType.Name})={f.GetValue(g)} ");
                    return sb.ToString();
                }
                ampInfo = $"Amplitude skeleton GUID = {sg}\nAmplitude prefab GUID   = {pg}\nAmplitude atlas GUID    = {ag}\nAmplitude Guid type     = {sg.GetType().FullName}\n" +
                          $"skeleton Guid fields: {Fields(sg)}\nprefab Guid fields:   {Fields(pg)}\natlas Guid fields:    {Fields(ag)}";
            }
        }

        var report = $"[Zeppelin] DONE.\n  prefab GUID   = {prefabGuid}\n  skeleton GUID = {skelGuid}\n{ampInfo}\n" +
                     "Now: BUILD the mod, then give Cloud both GUIDs.";
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ZeppelinModelGuids.txt"), report);
        Debug.Log(report);
    }

    // load a PNG straight from disk into a readable Texture2D (bypasses import settings, so PackTextures works)
    static Texture2D LoadPng(string assetPath)
    {
        string full = Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = Path.GetFileNameWithoutExtension(assetPath) };
        t.LoadImage(File.ReadAllBytes(full));
        return t;
    }

    static Type[] SafeTypes(Assembly a)
    { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }
}
