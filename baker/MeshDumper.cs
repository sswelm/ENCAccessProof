using UnityEngine;
using UnityEditor;
using System.IO;
using System.Text;
using System.Globalization;

// One-off diagnostic: dumps the Unity-imported source mesh AND the baked ModelMesh to plain OBJs (v + vt + f),
// so we can render them externally and see exactly where the UVs get scrambled. Menu: Tools/Dump StealthCruiser Meshes.
public static class MeshDumper
{
    [MenuItem("Tools/Dump StealthCruiser Meshes")]
    static void Dump()
    {
        // 1) the mesh Unity produced when it imported our clean OBJ
        var go = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/StealthCruiser/StealthCruiser.obj");
        if (go != null)
        {
            var mf = go.GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) DumpMesh(mf.sharedMesh, "imported");
            else Debug.LogWarning("[Dumper] no MeshFilter under imported OBJ");
        }
        else Debug.LogWarning("[Dumper] StealthCruiser.obj not found");

        // 2) the final baked mesh the game actually renders
        var baked = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/StealthCruiser_ModelMesh.asset");
        if (baked != null) DumpMesh(baked, "baked");
        else Debug.LogWarning("[Dumper] StealthCruiser_ModelMesh.asset not found");

        AssetDatabase.Refresh();
        Debug.Log("[Dumper] done");
    }

    static void DumpMesh(Mesh m, string tag)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var v = m.vertices; var uv = m.uv; var t = m.triangles;
        foreach (var p in v) sb.AppendLine($"v {p.x.ToString(ci)} {p.y.ToString(ci)} {p.z.ToString(ci)}");
        foreach (var c in uv) sb.AppendLine($"vt {c.x.ToString(ci)} {c.y.ToString(ci)}");
        for (int i = 0; i < t.Length; i += 3)
            sb.AppendLine($"f {t[i] + 1}/{t[i] + 1} {t[i + 1] + 1}/{t[i + 1] + 1} {t[i + 2] + 1}/{t[i + 2] + 1}");
        string outp = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "_DUMP_" + tag + ".obj");
        File.WriteAllText(outp, sb.ToString());
        Debug.Log($"[Dumper] {tag}: verts={v.Length} uvs={uv.Length} tris={t.Length / 3} -> {outp}");
    }
}
