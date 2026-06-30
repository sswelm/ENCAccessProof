using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // COMBO (step 3): the zeppelin's skeleton was already REGISTERED + GPU-UPLOADED by shakee's merge
    // (SkeletonId=70, MeshIndex=115) — so here we do ONLY the thin runtime repoint: point the zeppelin pawn-def at
    // that merged skeleton and re-resolve its body fragment. Deliberately NO EnsureRegistered / Apply() / LoadIFN —
    // that omission is the robustness claim being tested. Gated by config Shakee/MergeModContent.
    [HarmonyPatch]
    internal static class ShakeeZeppelinCombo
    {
        // the name the zeppelin's vanilla Description body fragment looks up (from discovery)
        const string BodyMeshName = "Unit_Era6_CruiseMissile_01";
        // our zeppelin skeleton ASSET GUID (the one the merge registered)
        const int ZepA = 781638270, ZepB = 1224895347, ZepC = -2137756227, ZepD = -1885149832;
        // our zeppelin atlas texture GUID (skin) — to replace the cruise-missile material the mesh samples by default
        const int TexA = 1762477492, TexB = 1213769387, TexC = -475189569, TexD = 500770371;
        private static bool dumped, repointLogged;
        private static UnityEngine.Texture2D atlas;
        private static object outputLayer;

        private static MethodBase TargetMethod()
        {
            var addon = AccessTools.TypeByName("Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn");
            var animMgr = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return (addon != null && animMgr != null) ? AccessTools.Method(addon, "Load", new[] { animMgr }) : null;
        }

        private static void Postfix(object __instance, object __0)
        {
            if (!Plugin.MergeModContent.Value || __instance == null || __0 == null) return;
            try
            {
                var name = (GetMember(__instance, "Definition") as UnityEngine.Object)?.name ?? "";
                if (name.IndexOf("Zeppelin", StringComparison.OrdinalIgnoreCase) < 0) return;

                if (!dumped) { DumpState(__instance, "before"); dumped = true; }

                // our merged skeleton (LoadAsset returns the cached, already-registered instance)
                var ourSkel = LoadOurSkeleton();
                if (ourSkel == null) { Plugin.Log.LogWarning("[ZepCombo] merged skeleton not loadable (merge off / mod not built?)"); return; }
                var sid = GetMember(ourSkel, "SkeletonId");
                int meshIdx = MeshIndexOf(ourSkel);

                // rename our skeleton's body mesh entry so the Description fragment resolves to it (no LoadIFN here)
                RenameBody(ourSkel, BodyMeshName);

                // THIN repoint: just point the pawn-def at the merged skeleton + re-resolve fragments. No Apply/LoadIFN.
                SetMember(__instance, "Skeleton", ourSkel);
                SetMember(__instance, "MeshCollection", ourSkel);
                ReloadFragments(__instance, __0, ourSkel);
                ApplyTexture(__0);   // replace the cruise-missile material (the red stain) with our zeppelin atlas

                if (!repointLogged)
                {
                    Plugin.Log.LogInfo($"[ZepCombo] repointed '{name}' -> MERGED skeleton (SkeletonId={sid}, MeshIndex={meshIdx}) — NO Apply/LoadIFN/register");
                    DumpState(__instance, "after");
                    repointLogged = true;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[ZepCombo] error: " + e); }
        }

        private static void DumpState(object addon, string when)
        {
            var skel = GetMember(addon, "Skeleton");
            Plugin.Log.LogInfo($"[ZepCombo] {when}: Skeleton='{(skel as UnityEngine.Object)?.name}' SkeletonId={GetMember(skel, "SkeletonId")}");
            var frags = GetMember(addon, "FragmentEntries") as Array;
            if (frags != null) foreach (var f in frags)
            {
                if (f == null) continue;
                var s = f.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(fi => fi.FieldType == typeof(string) || fi.FieldType == typeof(uint) || fi.FieldType == typeof(int))
                    .Select(fi => fi.Name + "=" + fi.GetValue(f));
                Plugin.Log.LogInfo($"[ZepCombo] {when}: fragment " + string.Join(" ", s));
            }
        }

        // Skin: put our zeppelin atlas on the (shared) cruise-missile output layer's material. Per-frame re-apply
        // beats the async proxy rebind. (Texture is not yet scoped — same shared-material caveat as before.)
        private static void ApplyTexture(object animMgr)
        {
            try
            {
                if (atlas == null) atlas = LoadAtlas();
                if (atlas == null) { Plugin.Log.LogWarning("[ZepCombo] atlas not loaded (rebuild mod?)"); return; }
                var content = GetMember(animMgr, "Content");
                var entries = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (entries == null) return;
                foreach (var e in entries)
                {
                    var ol = GetMember(e, "OutputLayerInstance");
                    if (ol == null || ((ol as UnityEngine.Object)?.name ?? "").IndexOf("CruiseMissile", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    outputLayer = ol; TickTexture(); break;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[ZepCombo] texture error: " + e); }
        }

        internal static void TickTexture()
        {
            if (outputLayer == null || atlas == null) return;
            try
            {
                if (GetMember(outputLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                        foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial" })
                            if (GetMember(ro, fld) is UnityEngine.Material mat) mat.SetTexture("_MainTex", atlas);
            }
            catch { }
        }

        private static UnityEngine.Texture2D LoadAtlas()
        {
            var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
            var m = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => (x.Name == "LoadAsset" || x.Name == "TryLoadAsset") && x.IsGenericMethodDefinition && x.GetParameters().Length >= 1);
            var g = m.MakeGenericMethod(typeof(UnityEngine.Texture2D));
            var guid = MakeGuid(TexA, TexB, TexC, TexD);
            var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
            return g.Invoke(null, args) as UnityEngine.Texture2D;
        }

        private static object LoadOurSkeleton()
        {
            var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
            var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
            var m = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => (x.Name == "LoadAsset" || x.Name == "TryLoadAsset") && x.IsGenericMethodDefinition && x.GetParameters().Length >= 1);
            var g = m.MakeGenericMethod(mcType);
            var guid = MakeGuid(ZepA, ZepB, ZepC, ZepD);
            var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
            return g.Invoke(null, args);
        }

        private static int MeshIndexOf(object skel)
        {
            var arr = AccessTools.Field(skel.GetType(), "skinnedMeshInfos")?.GetValue(skel) as Array;
            return (arr != null && arr.Length > 0) ? Convert.ToInt32(GetMember(arr.GetValue(0), "MeshIndex")) : -1;
        }

        private static void RenameBody(object skel, string newName)
        {
            var arr = AccessTools.Field(skel.GetType(), "skinnedMeshInfos")?.GetValue(skel) as Array;
            if (arr == null || arr.Length == 0) return;
            var item = arr.GetValue(0);
            AccessTools.Field(item.GetType(), "MeshName")?.SetValue(item, newName);
            arr.SetValue(item, 0);
        }

        private static void ReloadFragments(object addon, object animMgr, object ourSkel)
        {
            var frags = GetMember(addon, "FragmentEntries") as Array;
            if (frags == null) return;
            var renderer = GetMember(animMgr, "FxComponentRenderer");
            var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
            var layerObj = GetMember(animMgr, "FXMeshLayerIndex");
            int layer = layerObj is int l ? l : Convert.ToInt32(layerObj ?? 0);
            var fragType = frags.GetType().GetElementType();
            var mcField = AccessTools.Field(fragType, "meshCollection");
            var load = AccessTools.Method(fragType, "Load");
            for (int i = 0; i < frags.Length; i++)
            {
                var item = frags.GetValue(i);
                if (item == null) continue;
                mcField?.SetValue(item, ourSkel);
                try { load?.Invoke(item, new object[] { ourSkel, renderer, mcm, layer }); }
                catch (Exception e) { Plugin.Log.LogWarning("[ZepCombo] frag reload: " + (e.InnerException ?? e).Message); }
                frags.SetValue(item, i);
            }
        }

        private static object GetMember(object o, string n)
        { if (o == null) return null; var t = o.GetType(); var p = AccessTools.Property(t, n); if (p != null) try { return p.GetValue(o); } catch { } var f = AccessTools.Field(t, n); return f?.GetValue(o); }
        private static void SetMember(object o, string n, object v)
        { var t = o.GetType(); var p = AccessTools.Property(t, n); if (p != null && p.CanWrite) { try { p.SetValue(o, v); return; } catch { } } AccessTools.Field(t, n)?.SetValue(o, v); }
        private static object MakeGuid(int a, int b, int c, int d)
        { var gt = AccessTools.TypeByName("Amplitude.Framework.Guid"); var g = Activator.CreateInstance(gt); const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance; gt.GetField("a", BF)?.SetValue(g, a); gt.GetField("b", BF)?.SetValue(g, b); gt.GetField("c", BF)?.SetValue(g, c); gt.GetField("d", BF)?.SetValue(g, d); return g; }
    }
}
