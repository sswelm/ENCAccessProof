using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // Discovery helper: logs every distinct MeshCollection/skeleton name GetMeshCollection hands out, so we can find
    // which (vanilla) barge skeleton the Hovercraft unit borrows. Read BepInEx/LogOutput.log for "[Discover]" lines
    // (grep for transport/barge/landing/hover). Remove once the host skeleton name is known.
    [HarmonyPatch]
    internal static class HovercraftDiscovery
    {
        private static readonly HashSet<string> seen = new HashSet<string>();

        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return t != null ? AccessTools.Method(t, "GetMeshCollection") : null;
        }

        private static void Postfix(object __result)
        {
            var name = (__result as UnityEngine.Object)?.name;
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) return;
            Plugin.Log.LogInfo("[Discover] skeleton: " + name);
        }
    }
}
