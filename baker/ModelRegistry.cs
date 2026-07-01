// ModelRegistry.cs (ENC editor) — the ENC Model Factory's config store. Writes a JSON file the in-game plugin reads
// to bind each baked model onto its pawn definition at runtime. Uses UnityEngine.JsonUtility, which works in BOTH the
// editor and the game runtime, so no JSON dependency on either side. Written into the game's BepInEx/config.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// One baked model. Field names MUST match the plugin's reader (JsonUtility binds by field name; extra fields ignored).
[Serializable]
public class ModelDef
{
    public string resourceName = "";
    public string pawnDescription = "";
    public string modelFile = "";
    public Vector3 rotation;            // rotation offset (deg)
    public Vector3 position;            // position offset (z = waterline)
    public float size = 5f;             // world length of the model's longest axis
    public int normalsMode = 1;         // 0 KeepModel, 1 Recalculate, 2 Faceted
    public float smoothingAngle = 20f;
    public int convertGrid = 0;         // GLB->OBJ: 0 = faithful (preserve UV seams), >0 = decimate
    public bool reuseExtracted = false; // reuse existing OBJ/albedo on re-bake (skip re-import), preserving hand-edited textures
    public int[] skel = new int[4];     // baked skeleton Amplitude guid {a,b,c,d}
    public int[] atlas = new int[4];    // baked atlas Amplitude guid {a,b,c,d}
}

[Serializable]
class ModelDefList { public List<ModelDef> models = new List<ModelDef>(); }

public static class ModelRegistry
{
    const string DefaultConfigDir = @"C:\Program Files (x86)\Steam\steamapps\common\Humankind\BepInEx\config";
    public static string ConfigDir => EditorPrefs.GetString("ENC.bepinexConfig", DefaultConfigDir);
    public static string RegistryPath => Path.Combine(ConfigDir, "enc_models.json");

    public static List<ModelDef> Load()
    {
        try
        {
            if (!File.Exists(RegistryPath)) return new List<ModelDef>();
            var data = JsonUtility.FromJson<ModelDefList>(File.ReadAllText(RegistryPath));
            return data?.models ?? new List<ModelDef>();
        }
        catch (Exception e) { Debug.LogError("[Factory] registry load: " + e); return new List<ModelDef>(); }
    }

    public static void Save(List<ModelDef> models)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(RegistryPath, JsonUtility.ToJson(new ModelDefList { models = models }, true));
        AssetDatabase.Refresh();
    }

    public static void Upsert(ModelDef def)
    {
        var list = Load();
        list.RemoveAll(m => m.resourceName == def.resourceName);
        list.Add(def);
        Save(list);
    }

    public static int[] ParseGuid(string csv)
    {
        var p = (csv ?? "").Split(',');
        int g(int i) => p.Length > i && int.TryParse(p[i], out var r) ? r : 0;
        return new[] { g(0), g(1), g(2), g(3) };
    }
}
