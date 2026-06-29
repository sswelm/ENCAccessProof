// HovercraftModel.cs  (ENC editor) — bake the LCAC hovercraft OBJ into an Amplitude Skeleton/MeshCollection that
// ships in the mod, rigged 100% to ONE "Base" bone. The BepInEx plugin loads it and points the Hovercraft unit at
// it (mesh via MeshIndex swap). The model is an untextured CAD sketch, so there's no atlas — the plugin applies a
// flat procedural military-grey skin (the detail is the geometry).
//
// MODEL: "LCAC Hovercraft croqui" by LM3D — Sketchfab, CC-BY. GLB decimated (vertex-clustering) to ~27k tris and
//        converted to OBJ offline; the OBJ lives at Assets/Resources/Hovercraft/Hovercraft.obj.
//
// RUN:  Tools > Hovercraft > Build Hovercraft Skeleton   (then BUILD the mod)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class HovercraftModel
{
    const string ObjPath      = "Assets/Resources/Hovercraft/Hovercraft.obj";
    const string PrefabPath   = "Assets/Resources/Hovercraft_Model.prefab";
    const string SkeletonPath = "Assets/Resources/Hovercraft_Skeleton.asset";
    const float  TargetLength = 3.6f;                              // long-axis size in mesh units (tune to fit the tile)
    static readonly Vector3 OrientEuler = new Vector3(0f, 90f, 0f); // 90° roll about the long axis -> deck up (was on its side)
    const float HoverGap = 0.12f;                                  // raise the hull this far above the water (anti-"sunk")

    [MenuItem("Tools/Hovercraft/Build Hovercraft Skeleton")]
    static void Build()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");

        // --- 1) load the imported OBJ, combine every submesh into ONE mesh, normalize, fix winding ---
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(ObjPath);
        if (src == null) { Debug.LogError("[Hovercraft] OBJ not found at " + ObjPath + " — import it first."); return; }
        var inst = (GameObject)UnityEngine.Object.Instantiate(src);

        var parts = new List<CombineInstance>();
        var rootInv = inst.transform.worldToLocalMatrix;
        foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
        {
            var m = mf.sharedMesh; if (m == null) continue;
            var local = rootInv * mf.transform.localToWorldMatrix;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var sub = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = m.vertices };
                if (m.normals != null && m.normals.Length == m.vertexCount) sub.normals = m.normals;
                sub.triangles = m.GetTriangles(s);
                parts.Add(new CombineInstance { mesh = sub, transform = local });
            }
        }
        UnityEngine.Object.DestroyImmediate(inst);

        var mesh = new Mesh { name = "Hovercraft_ModelMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
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
        var vv = mesh.vertices;
        for (int i = 0; i < vv.Length; i++) vv[i] = rot * ((vv[i] - bb.center) * scl);
        mesh.vertices = vv;

        // raise so the hull bottom hovers just above the water (centred on the surface looked half-sunk). World-up
        // maps to mesh +Z here (via the bindpose), so shift the lowest point to +HoverGap.
        mesh.RecalculateBounds();
        float raise = -mesh.bounds.min.z + HoverGap;
        var vr = mesh.vertices;
        for (int i = 0; i < vr.Length; i++) vr[i].z += raise;
        mesh.vertices = vr;
        Debug.Log($"[Hovercraft] raised by {raise:0.###} (HoverGap={HoverGap})");

        // Override the CAD model's (unreliable) UVs with height-based mapping so the plugin's skin reads as a real
        // hovercraft: U = position along the hull (length, Y), V = normalized height (Z) -> dark skirt low, light body high.
        {
            var vp = mesh.vertices;
            float mnz = float.MaxValue, mxz = float.MinValue, mny = float.MaxValue, mxy = float.MinValue;
            foreach (var p in vp) { if (p.z < mnz) mnz = p.z; if (p.z > mxz) mxz = p.z; if (p.y < mny) mny = p.y; if (p.y > mxy) mxy = p.y; }
            float hz = Mathf.Max(1e-4f, mxz - mnz), ly = Mathf.Max(1e-4f, mxy - mny);
            var uv = new Vector2[vp.Length];
            for (int i = 0; i < vp.Length; i++) uv[i] = new Vector2((vp[i].y - mny) / ly, (vp[i].z - mnz) / hz);
            mesh.uv = uv;
        }

        // the CAD model has inconsistent winding -> wind every tri outward from the centred origin (convex-ish hull)
        {
            var v = mesh.vertices; var t = mesh.triangles; int flipped = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Vector3 geo = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                if (Vector3.Dot(geo, v[a] + v[b] + v[c]) < 0f) { t[i + 1] = c; t[i + 2] = b; flipped++; }
            }
            mesh.triangles = t;
            Debug.Log($"[Hovercraft] winding-fixed {flipped}/{t.Length / 3} tris");
        }
        Debug.Log($"[Hovercraft] baked: verts={mesh.vertexCount}, rawBounds={size}");

        // --- 2) rig: root -> Dummy_Root -> Base (single bone), skinned 100% to Base ---
        var root  = new GameObject("Hovercraft_Model");
        var dummy = new GameObject("Dummy_Root"); dummy.transform.SetParent(root.transform); dummy.transform.localPosition = Vector3.zero;
        var bone  = new GameObject("Base");       bone.transform.SetParent(dummy.transform);  bone.transform.localPosition = Vector3.zero;

        var bind = new Matrix4x4();
        bind.SetRow(0, new Vector4(1f,  0f, 0f,  0f));
        bind.SetRow(1, new Vector4(0f,  0f, 1f,  0.101f));
        bind.SetRow(2, new Vector4(0f, -1f, 0f, -0.069f));
        bind.SetRow(3, new Vector4(0f,  0f, 0f,  1f));
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
        mesh.bindposes = new[] { bind };
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();   // VertexEncodingFormat 6 needs tangents
        AssetDatabase.CreateAsset(mesh, "Assets/Resources/Hovercraft_ModelMesh.asset");

        var mat = new Material(Shader.Find("Standard")) { name = "Hovercraft_ModelMat", color = new Color(0.5f, 0.52f, 0.55f) };
        AssetDatabase.CreateAsset(mat, "Assets/Resources/Hovercraft_ModelMat.mat");

        var meshGO = new GameObject("Unit_Hovercraft"); meshGO.transform.SetParent(root.transform);
        meshGO.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
        var smr = meshGO.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh; smr.bones = new[] { bone.transform }; smr.rootBone = bone.transform; smr.sharedMaterial = mat; smr.updateWhenOffscreen = true;

        var anim = root.AddComponent<Animator>();
        anim.avatar = AvatarBuilder.BuildGenericAvatar(root, "");

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

        // --- 3) bake the Amplitude Skeleton/MeshCollection from the prefab ---
        var skelType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Mercury.Animation.Skeleton");
        if (skelType == null) { Debug.LogError("[Hovercraft] Skeleton type not found."); return; }
        var skel = ScriptableObject.CreateInstance(skelType);
        AssetDatabase.CreateAsset(skel, SkeletonPath);
        try { skelType.GetMethod("SetPrefab", new[] { typeof(GameObject) })?.Invoke(skel, new object[] { prefab });
              skelType.GetMethod("Reimport", Type.EmptyTypes)?.Invoke(skel, null); }
        catch (Exception e) { Debug.LogError("[Hovercraft] bake failed: " + e); }
        EditorUtility.SetDirty(skel);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        // log the Amplitude GUID for the plugin
        string ampInfo = "(Amplitude AssetDatabase not found)";
        var adbType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Framework.Asset.AssetDatabase");
        if (adbType != null)
        {
            var getGuid = adbType.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) });
            object sg = null;
            try { sg = getGuid?.Invoke(null, new object[] { skel }); } catch (Exception e) { ampInfo = "GetAssetGUID failed: " + e.Message; }
            if (sg != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var f in sg.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    sb.Append($"{f.Name}({f.FieldType.Name})={f.GetValue(sg)} ");
                ampInfo = $"Amplitude skeleton GUID = {sg}\nskeleton Guid fields: {sb}";
            }
        }
        var report = $"[Hovercraft] DONE.\n  skeleton GUID = {AssetDatabase.AssetPathToGUID(SkeletonPath)}\n{ampInfo}\nNow: BUILD the mod, then give Cloud the GUID.";
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "HovercraftModelGuids.txt"), report);
        Debug.Log(report);
    }

    static Type[] SafeTypes(Assembly a)
    { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }
}
