using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // NATIVE SCOPED approach for the Era6 StealthCruisers naval unit — same proven recipe as HovercraftInject:
    //   1) Register our baked Zumwalt skeleton with AnimationManager BEFORE Apply() runs (AnimationLoad postfix), so
    //      GetMeshCollection finds it and Apply() builds its GPU bone buffers.
    //   2) Repoint ONLY the StealthCruisers pawn-def's AddOn at our skeleton (scoped by Definition name).
    //   3) Skin via the host naval output layer's _MainTex — but here we load the REAL baked atlas (the Zumwalt skin),
    //      not a procedural texture.
    //
    // FIRST RUN IS A DISCOVERY PASS: BodyMeshName below is a guess. The PRE fragment dump reveals the host's real body
    // mesh name + the ApplyTexture dump lists the output-layer names -> set BodyMeshName + the layer filter, re-bake.
    internal static class StealthCruiserInject
    {
        // Amplitude.Framework.Guid for the baked StealthCruiser_Skeleton.asset (our Zumwalt).
        const int SkelA = -958028325, SkelB = 1316059366, SkelC = -2071331166, SkelD = -779568690;
        // Amplitude.Framework.Guid for StealthCruiser_Atlas.asset (the Zumwalt skin).
        const int TexA = 456614180, TexB = 1267693964, TexC = -921504093, TexD = 639359095;
        // host body fragment mesh name — GUESS; corrected from the PRE fragment dump on the first run.
        const string BodyMeshName = "Unit_Era6_Common_StealthCruisers_01";
        // substring of the host output layer to skin — GUESS; corrected from the ApplyTexture layer dump.
        const string OutputLayerHint = "StealthCruiser";

        internal static object ourSkeleton;
        private static bool tried, registered, repointLogged;
        private static object hostOutputLayer;
        private static UnityEngine.Texture2D ourTex;
        private static string layerHint = OutputLayerHint;   // auto-derived from the discovered body mesh family

        // (1) load + register our skeleton; AnimationLoad postfix (before pawns present, FX manager ready).
        internal static void EnsureRegistered(object animMgr)
        {
            if (registered || animMgr == null) return;
            try
            {
                if (ourSkeleton == null && !tried) { tried = true; ourSkeleton = LoadOurSkeleton(); }
                if (ourSkeleton == null) return;

                var sf = AccessTools.Field(ourSkeleton.GetType(), "loadingStatus");
                if (sf != null) sf.SetValue(ourSkeleton, Enum.ToObject(sf.FieldType, 0)); // NotLoaded
                SetMember(ourSkeleton, "SkeletonId", -1);
                // NOTE: rename happens at repoint time now, using the host's OWN auto-discovered body mesh name.

                var reg = AccessTools.Method(animMgr.GetType(), "RegisterMeshCollection");
                if (reg == null) { Plugin.Log.LogError("[Cruiser] RegisterMeshCollection not found"); return; }
                reg.Invoke(animMgr, new[] { ourSkeleton });

                var apply = AccessTools.Method(animMgr.GetType(), "Apply", Type.EmptyTypes)
                    ?? animMgr.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "Apply" && m.GetParameters().Length == 0);
                if (apply != null) { apply.Invoke(animMgr, null); Plugin.Log.LogInfo("[Cruiser] re-Apply'd GPU buffers"); }
                else Plugin.Log.LogWarning("[Cruiser] Apply() not found");

                registered = true;
                Plugin.Log.LogInfo("[Cruiser] registered Zumwalt skeleton; SkeletonId=" + GetMember(ourSkeleton, "SkeletonId"));
                var smi = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                if (smi != null) for (int i = 0; i < smi.Length; i++)
                { var it = smi.GetValue(i); Plugin.Log.LogInfo($"[Cruiser]   our skinnedMeshInfos[{i}] MeshName='{GetMember(it, "MeshName")}' MeshIndex={GetMember(it, "MeshIndex")}"); }
            }
            catch (Exception e) { Plugin.Log.LogError("[Cruiser] register error: " + e); }
        }

        // (2) repoint only the StealthCruisers AddOn at our registered skeleton; AddOn.Load postfix.
        internal static void RepointCruiser(object addon, object animMgr)
        {
            if (!Plugin.CruiserInject.Value || addon == null || animMgr == null) return;
            try
            {
                var def = GetMember(addon, "Definition");
                var name = (def as UnityEngine.Object)?.name ?? "";
                if (name.IndexOf("StealthCruiser", StringComparison.OrdinalIgnoreCase) < 0) return;

                EnsureRegistered(animMgr);
                if (ourSkeleton == null) return;

                bool first = !repointLogged;
                if (first) DumpFragmentPaths(addon, "PRE ");

                // AUTO-DISCOVER the host's own body mesh name and rename OUR mesh to match, so its body fragment
                // resolves to our mesh. No hardcoding, no separate discovery run — works for any borrowed naval unit.
                var bodyName = DiscoverBodyMeshName(addon) ?? BodyMeshName;
                RenameBodyMesh(ourSkeleton, bodyName);
                layerHint = bodyName;   // host output layer is named "<bodyMeshName>_OutputLayer" -> match by the full mesh name
                if (first) Plugin.Log.LogInfo("[Cruiser] matched our mesh -> '" + bodyName + "', skin layer hint='" + layerHint + "'");

                EnsureUploaded(animMgr);
                if (first)
                {
                    var smi = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                    if (smi != null && smi.Length > 0) Plugin.Log.LogInfo("[Cruiser] our mesh @ repoint: MeshName='" + GetMember(smi.GetValue(0), "MeshName") + "' MeshIndex=" + GetMember(smi.GetValue(0), "MeshIndex"));
                }

                SetMember(addon, "Skeleton", ourSkeleton);
                SetMember(addon, "MeshCollection", ourSkeleton);
                ReloadFragments(addon, animMgr);
                if (first) DumpFragmentPaths(addon, "POST");
                ApplyTexture(animMgr);

                if (first) { Plugin.Log.LogInfo("[Cruiser] repointed StealthCruiser '" + name + "' -> registered Zumwalt skeleton"); repointLogged = true; }
            }
            catch (Exception e) { Plugin.Log.LogError("[Cruiser] repoint error: " + e); }
        }

        private static void EnsureUploaded(object animMgr)
        {
            try
            {
                var smi = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                if (smi != null && smi.Length > 0 && Convert.ToInt32(GetMember(smi.GetValue(0), "MeshIndex")) != 0)
                { Plugin.Log.LogInfo("[Cruiser] mesh already uploaded; MeshIndex=" + GetMember(smi.GetValue(0), "MeshIndex")); return; }

                var fxMgr = GetMember(animMgr, "FxComponentMeshContentManager");
                if (fxMgr == null) { Plugin.Log.LogWarning("[Cruiser] upload: FxComponentMeshContentManager null"); return; }
                var sf = AccessTools.Field(ourSkeleton.GetType(), "loadingStatus");
                if (sf != null) sf.SetValue(ourSkeleton, Enum.ToObject(sf.FieldType, 0)); // NotLoaded
                var layerIdx = GetMember(animMgr, "FXMeshLayerIndex");
                int slot = GetMember(ourSkeleton, "SkeletonId") is int s ? s : 0;
                var loadIfn = AccessTools.Method(ourSkeleton.GetType(), "LoadIFN");
                if (loadIfn == null) { Plugin.Log.LogWarning("[Cruiser] LoadIFN not found"); return; }
                loadIfn.Invoke(ourSkeleton, new object[] { fxMgr, layerIdx, slot });

                var smi2 = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                Plugin.Log.LogInfo("[Cruiser] uploaded our mesh; MeshIndex=" + (smi2 != null && smi2.Length > 0 ? GetMember(smi2.GetValue(0), "MeshIndex") : "?"));
            }
            catch (Exception e) { Plugin.Log.LogError("[Cruiser] upload error: " + e); }
        }

        private static object LoadOurSkeleton()
        {
            var guid = MakeGuid(SkelA, SkelB, SkelC, SkelD);
            var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
            var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
            if (guid == null || mcType == null || adb == null) { Plugin.Log.LogError("[Cruiser] missing types"); return null; }
            var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
            var g = load?.MakeGenericMethod(mcType);
            var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
            var skel = g.Invoke(null, args);
            Plugin.Log.LogInfo("[Cruiser] loaded Zumwalt skeleton: " + ((skel as UnityEngine.Object)?.name ?? "NULL (rebuild mod?)"));
            return skel;
        }

        private static void RenameBodyMesh(object skel, string newName)
        {
            try
            {
                var arr = AccessTools.Field(skel.GetType(), "skinnedMeshInfos")?.GetValue(skel) as Array;
                if (arr != null && arr.Length > 0)
                {
                    var item = arr.GetValue(0);
                    AccessTools.Field(item.GetType(), "MeshName")?.SetValue(item, newName);
                    arr.SetValue(item, 0);
                }
                var amnField = AccessTools.Field(skel.GetType(), "allMeshNames");
                var amn = amnField?.GetValue(skel) as string[];
                if (amn != null && amn.Length > 0) { Plugin.Log.LogInfo("[Cruiser] allMeshNames before: [" + string.Join(", ", amn) + "]"); amn[0] = newName; }
                else if (arr != null && arr.Length > 0)
                {
                    var names = new string[arr.Length];
                    for (int i = 0; i < arr.Length; i++) names[i] = GetMember(arr.GetValue(i), "MeshName") as string;
                    amnField?.SetValue(skel, names);
                    Plugin.Log.LogInfo("[Cruiser] built allMeshNames: [" + string.Join(", ", names) + "]");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[Cruiser] rename error: " + e); }
        }

        // Read the host AddOn's own fragments and pick the hull mesh name (a Unit_* mesh that isn't a water/wake/foam
        // effect). This is what the on-map body fragment looks up, so renaming our mesh to it makes it resolve to ours.
        private static string DiscoverBodyMeshName(object addon)
        {
            try
            {
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) return null;
                var fragType = frags.GetType().GetElementType();
                var mnField = AccessTools.Field(fragType, "meshName");
                if (mnField == null) return null;
                string hull = null, any = null;
                foreach (var f in frags)
                {
                    if (f == null) continue;
                    var mn = mnField.GetValue(f) as string;
                    if (string.IsNullOrEmpty(mn)) continue;
                    Plugin.Log.LogInfo("[Cruiser] host fragment meshName='" + mn + "'");
                    if (any == null) any = mn;
                    if (hull == null
                        && mn.IndexOf("Unit_", StringComparison.OrdinalIgnoreCase) >= 0
                        && mn.IndexOf("Water", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Wake",  StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Foam",  StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Proof", StringComparison.OrdinalIgnoreCase) < 0)
                        hull = mn;
                }
                return hull ?? any;
            }
            catch (Exception e) { Plugin.Log.LogError("[Cruiser] discover error: " + e); return null; }
        }

        private static void ReloadFragments(object addon, object animMgr)
        {
            try
            {
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) return;
                var renderer = GetMember(animMgr, "FxComponentRenderer");
                var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
                var layerObj = GetMember(animMgr, "FXMeshLayerIndex");
                int layer = layerObj is int li ? li : Convert.ToInt32(layerObj ?? 0);
                var fragType = frags.GetType().GetElementType();
                var mcField = AccessTools.Field(fragType, "meshCollection");
                var load = AccessTools.Method(fragType, "Load");
                for (int i = 0; i < frags.Length; i++)
                {
                    var item = frags.GetValue(i);
                    if (item == null) continue;
                    mcField?.SetValue(item, ourSkeleton);
                    try { load?.Invoke(item, new object[] { ourSkeleton, renderer, mcm, layer }); }
                    catch (Exception e) { Plugin.Log.LogWarning("[Cruiser] frag reload: " + (e.InnerException ?? e).Message); }
                    frags.SetValue(item, i);
                }
                Plugin.Log.LogInfo("[Cruiser] re-resolved " + frags.Length + " fragment(s) with meshCollection=our skeleton");
            }
            catch (Exception e) { Plugin.Log.LogError("[Cruiser] ReloadFragments error: " + e); }
        }

        private static void DumpFragmentPaths(object addon, string label)
        {
            try
            {
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) { Plugin.Log.LogInfo("[Cruiser] " + label + " no FragmentEntries"); return; }
                foreach (var f in frags)
                {
                    if (f == null) continue;
                    var flds = f.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(fi => fi.FieldType == typeof(string) || fi.FieldType == typeof(int) || fi.FieldType == typeof(uint) || fi.FieldType.IsEnum)
                        .Select(fi => fi.Name + "=" + fi.GetValue(f));
                    Plugin.Log.LogInfo("[Cruiser] " + label + " fragment(" + f.GetType().Name + ") " + string.Join(" ", flds));
                }
            }
            catch { }
        }

        private static void ApplyTexture(object mgr)
        {
            try
            {
                var content = GetMember(mgr, "Content");
                var entries = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (entries == null) return;
                if (ourTex == null) ourTex = LoadAtlas();
                bool firstScan = hostOutputLayer == null;
                foreach (var e in entries)
                {
                    var ol = GetMember(e, "OutputLayerInstance");
                    var oln = (ol as UnityEngine.Object)?.name ?? "";
                    if (firstScan && oln.Length > 0) Plugin.Log.LogInfo("[Cruiser]   output layer: '" + oln + "'");   // discovery: which layer to skin
                    if (ol == null || oln.IndexOf(layerHint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hostOutputLayer = ol; TickTexture();
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Cruiser] texture error: " + ex); }
        }

        internal static void TickTexture()
        {
            if (hostOutputLayer == null || ourTex == null) return;
            try
            {
                if (GetMember(hostOutputLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                        foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial" })
                            if (GetMember(ro, fld) is UnityEngine.Material mat) mat.SetTexture("_MainTex", ourTex);
            }
            catch { }
        }

        private static UnityEngine.Texture2D LoadAtlas()
        {
            try
            {
                var guid = MakeGuid(TexA, TexB, TexC, TexD);
                var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                if (guid == null || adb == null) return null;
                var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
                var g = load?.MakeGenericMethod(typeof(UnityEngine.Texture2D));
                var args = g.GetParameters().Length == 1 ? new object[] { guid } : new object[] { guid, null };
                var tex = g.Invoke(null, args) as UnityEngine.Texture2D;
                Plugin.Log.LogInfo("[Cruiser] loaded atlas: " + (tex != null ? tex.name + " " + tex.width + "x" + tex.height : "NULL (rebuild mod?)"));
                return tex;
            }
            catch (Exception e) { Plugin.Log.LogError("[Cruiser] atlas load error: " + e); return null; }
        }

        private static void SetMember(object o, string name, object val)
        { var t = o.GetType(); var p = AccessTools.Property(t, name); if (p != null && p.CanWrite) { try { p.SetValue(o, val); return; } catch { } } var f = AccessTools.Field(t, name); if (f != null) { try { f.SetValue(o, val); } catch { } } }
        private static object GetMember(object o, string name)
        { if (o == null) return null; var t = o.GetType(); var p = AccessTools.Property(t, name); if (p != null) { try { return p.GetValue(o); } catch { } } var f = AccessTools.Field(t, name); if (f != null) { try { return f.GetValue(o); } catch { } } return null; }
        private static object MakeGuid(int a, int b, int c, int d)
        { var gt = AccessTools.TypeByName("Amplitude.Framework.Guid"); if (gt == null) return null; var g = Activator.CreateInstance(gt);
          const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
          gt.GetField("a", BF)?.SetValue(g, a); gt.GetField("b", BF)?.SetValue(g, b); gt.GetField("c", BF)?.SetValue(g, c); gt.GetField("d", BF)?.SetValue(g, d); return g; }
    }

    // Hook A: register our skeleton before Apply() builds GPU bone buffers.
    [HarmonyPatch]
    internal static class CruiserRegisterHook
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return t != null ? AccessTools.Method(t, "AnimationLoad") : null;
        }
        // Stand down when the registry-driven UniversalInject owns injection — otherwise BOTH inject the Stealth
        // Cruiser (old Apply()+host-layer path vs new clone path), fighting over the GPU mesh buffers and scrambling it.
        static void Postfix(object __instance) { if (!Plugin.UniversalInjectOn.Value) StealthCruiserInject.EnsureRegistered(__instance); }
    }

    // Hook B: repoint only the StealthCruisers pawn-def at our registered skeleton.
    [HarmonyPatch]
    internal static class CruiserRepointHook
    {
        static MethodBase TargetMethod()
        {
            var addon = AccessTools.TypeByName("Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn");
            var animMgr = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return (addon != null && animMgr != null) ? AccessTools.Method(addon, "Load", new[] { animMgr }) : null;
        }
        static void Postfix(object __instance, object __0) { if (!Plugin.UniversalInjectOn.Value) StealthCruiserInject.RepointCruiser(__instance, __0); }
    }
}
