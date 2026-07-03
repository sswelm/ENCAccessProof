// AmplitudeGuidLogger — tiny helper for the animated-model work. Logs the Amplitude {a,b,c,d} GUID of the selected
// asset(s) so they can be pasted into the plugin's model registry (enc_models.json). Amplitude GUIDs only resolve
// inside the editor via Amplitude.Framework.Asset.AssetDatabase.GetAssetGUID, hence a menu command.
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class AmplitudeGuidLogger
{
    [MenuItem("Tools/Log Amplitude GUID (selection)")]
    static void LogSelection()
    {
        var objs = Selection.objects;
        if (objs == null || objs.Length == 0) { Debug.LogWarning("[GUID] nothing selected"); return; }
        foreach (var o in objs)
            Debug.Log($"[GUID] {o.GetType().Name} '{o.name}'  ->  {AmplitudeGuid(o)}   (asset: {AssetDatabase.GetAssetPath(o)})");
    }

    static string AmplitudeGuid(UnityEngine.Object asset)
    {
        var adbType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Framework.Asset.AssetDatabase");
        var getGuid = adbType?.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) });
        var g = getGuid?.Invoke(null, new object[] { asset });
        if (g == null) return "<no Amplitude GUID>";
        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object A = g.GetType().GetField("a", bf)?.GetValue(g), B = g.GetType().GetField("b", bf)?.GetValue(g),
               Cc = g.GetType().GetField("c", bf)?.GetValue(g), D = g.GetType().GetField("d", bf)?.GetValue(g);
        return $"{A},{B},{Cc},{D}";
    }

    static Type[] SafeTypes(Assembly a)
    { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }
}
