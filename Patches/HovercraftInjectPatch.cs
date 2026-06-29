using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // NATIVE SCOPED approach (replaces the global mesh swap):
    //   1) Register our baked LCAC skeleton with AnimationManager BEFORE Apply() runs (hook AnimationLoad), so
    //      GetMeshCollection can find it AND Apply() builds its GPU bone buffers (the thing the runtime clone missed
    //      -> no slab, because this is a real registered asset, not a shallow Instantiate clone).
    //   2) Repoint ONLY the Hovercraft pawn-def's AddOn at our skeleton (addOn.Skeleton/MeshCollection). The barge
    //      transport is a different AddOn on the vanilla skeleton -> untouched. Scoped.
    //   3) Skin via the shared LandingCrafts output layer's _MainTex (mod can't add its own OutputLayerEntry).
    //
    // The Hovercraft's Description body fragment looks up the body mesh by NAME, so our skeleton's mesh entry must
    // carry the LandingCrafts body mesh name (we rename it; logged fragment paths confirm the exact name).
    internal static class HovercraftInject
    {
        // Amplitude.Framework.Guid fields for the baked Hovercraft_Skeleton.asset (our LCAC).
        const int SkelA = -1153397905, SkelB = 1134277020, SkelC = 577920438, SkelD = -573259371;
        const string BodyMeshName = "Unit_Era6_Common_LandingCrafts_01";   // best guess; corrected from logged fragment paths

        internal static object ourSkeleton;
        private static bool tried, registered, repointLogged;
        private static object hostOutputLayer;
        private static UnityEngine.Texture2D ourTex;

        // (1) load + register our skeleton; called from the AnimationLoad prefix (before Apply()).
        internal static void EnsureRegistered(object animMgr)
        {
            if (registered || animMgr == null) return;
            try
            {
                if (ourSkeleton == null && !tried) { tried = true; ourSkeleton = LoadOurSkeleton(); }
                if (ourSkeleton == null) return;

                // reset status so LoadIFN actually uploads the mesh; rename body entry to the host body mesh name
                var sf = AccessTools.Field(ourSkeleton.GetType(), "loadingStatus");
                if (sf != null) sf.SetValue(ourSkeleton, Enum.ToObject(sf.FieldType, 0)); // NotLoaded
                SetMember(ourSkeleton, "SkeletonId", -1);
                RenameBodyMesh(ourSkeleton, BodyMeshName);

                var reg = AccessTools.Method(animMgr.GetType(), "RegisterMeshCollection");
                if (reg == null) { Plugin.Log.LogError("[HoverNative] RegisterMeshCollection not found"); return; }
                reg.Invoke(animMgr, new[] { ourSkeleton });

                // Apply() (builds the GPU skeleton/bone buffers from skeletons[]) only runs inside AnimationLoad; our
                // skeleton was just appended, so re-run it now to build OUR bones into the buffer (else: collapsed mesh).
                var apply = AccessTools.Method(animMgr.GetType(), "Apply", Type.EmptyTypes)
                    ?? animMgr.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "Apply" && m.GetParameters().Length == 0);
                if (apply != null) { apply.Invoke(animMgr, null); Plugin.Log.LogInfo("[HoverNative] re-Apply'd GPU buffers"); }
                else Plugin.Log.LogWarning("[HoverNative] Apply() not found");

                registered = true;
                Plugin.Log.LogInfo("[HoverNative] registered LCAC skeleton; SkeletonId=" + GetMember(ourSkeleton, "SkeletonId"));
                var smi = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                if (smi != null) for (int i = 0; i < smi.Length; i++)
                { var it = smi.GetValue(i); Plugin.Log.LogInfo($"[HoverNative]   our skinnedMeshInfos[{i}] MeshName='{GetMember(it, "MeshName")}' MeshIndex={GetMember(it, "MeshIndex")}"); }
            }
            catch (Exception e) { Plugin.Log.LogError("[HoverNative] register error: " + e); }
        }

        // (2) repoint only the Hovercraft AddOn at our registered skeleton; called from AddOn.Load postfix.
        internal static void RepointHovercraft(object addon, object animMgr)
        {
            if (!Plugin.RepointOnLoad.Value || addon == null || animMgr == null) return;
            try
            {
                var def = GetMember(addon, "Definition");
                var name = (def as UnityEngine.Object)?.name ?? "";
                if (name.IndexOf("Hovercraft", StringComparison.OrdinalIgnoreCase) < 0) return;

                EnsureRegistered(animMgr);
                if (ourSkeleton == null) return;

                bool first = !repointLogged;
                if (first) DumpFragmentPaths(addon, "PRE ");

                EnsureUploaded(animMgr);            // upload our mesh now that FX is loaded -> valid MeshIndex
                if (first)
                {
                    var smi = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                    if (smi != null && smi.Length > 0) Plugin.Log.LogInfo("[HoverNative] our mesh @ repoint: MeshName='" + GetMember(smi.GetValue(0), "MeshName") + "' MeshIndex=" + GetMember(smi.GetValue(0), "MeshIndex"));
                    var amn = AccessTools.Field(ourSkeleton.GetType(), "allMeshNames")?.GetValue(ourSkeleton) as string[];
                    Plugin.Log.LogInfo("[HoverNative] our allMeshNames @ repoint: [" + (amn != null ? string.Join(", ", amn) : "null") + "]");
                }

                SetMember(addon, "Skeleton", ourSkeleton);
                SetMember(addon, "MeshCollection", ourSkeleton);
                ReloadFragments(addon, animMgr);   // re-resolve the body fragment against OUR skeleton -> our mesh
                if (first) DumpFragmentPaths(addon, "POST");   // did the body fragment's encoded mesh change?
                ApplyTexture(animMgr);

                if (first) { Plugin.Log.LogInfo("[HoverNative] repointed Hovercraft '" + name + "' -> registered LCAC skeleton"); repointLogged = true; }
            }
            catch (Exception e) { Plugin.Log.LogError("[HoverNative] repoint error: " + e); }
        }

        // Explicitly upload our mesh into the GPU mesh-content manager (RegisterMeshCollection doesn't, because FX
        // isn't loaded yet at AnimationLoad time). Called from the repoint, when the unit presents and FX is up.
        private static void EnsureUploaded(object animMgr)
        {
            try
            {
                var smi = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                if (smi != null && smi.Length > 0 && Convert.ToInt32(GetMember(smi.GetValue(0), "MeshIndex")) != 0)
                { Plugin.Log.LogInfo("[HoverNative] mesh already uploaded; [0] MeshName='" + GetMember(smi.GetValue(0), "MeshName") + "' MeshIndex=" + GetMember(smi.GetValue(0), "MeshIndex")); return; }

                var fxMgr = GetMember(animMgr, "FxComponentMeshContentManager");
                if (fxMgr == null) { Plugin.Log.LogWarning("[HoverNative] upload: FxComponentMeshContentManager null"); return; }
                var sf = AccessTools.Field(ourSkeleton.GetType(), "loadingStatus");
                if (sf != null) sf.SetValue(ourSkeleton, Enum.ToObject(sf.FieldType, 0)); // NotLoaded so LoadIFN uploads
                var layerIdx = GetMember(animMgr, "FXMeshLayerIndex");
                int slot = GetMember(ourSkeleton, "SkeletonId") is int s ? s : 0;
                var loadIfn = AccessTools.Method(ourSkeleton.GetType(), "LoadIFN");
                if (loadIfn == null) { Plugin.Log.LogWarning("[HoverNative] LoadIFN not found"); return; }
                loadIfn.Invoke(ourSkeleton, new object[] { fxMgr, layerIdx, slot });

                var smi2 = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                Plugin.Log.LogInfo("[HoverNative] uploaded our mesh; MeshIndex=" + (smi2 != null && smi2.Length > 0 ? GetMember(smi2.GetValue(0), "MeshIndex") : "?"));
            }
            catch (Exception e) { Plugin.Log.LogError("[HoverNative] upload error: " + e); }
        }

        private static object LoadOurSkeleton()
        {
            var guid = MakeGuid(SkelA, SkelB, SkelC, SkelD);
            var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
            var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
            if (guid == null || mcType == null || adb == null) { Plugin.Log.LogError("[HoverNative] missing types"); return null; }
            var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
            var g = load?.MakeGenericMethod(mcType);
            var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
            var skel = g.Invoke(null, args);
            Plugin.Log.LogInfo("[HoverNative] loaded LCAC skeleton: " + ((skel as UnityEngine.Object)?.name ?? "NULL (rebuild mod?)"));
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
                // GetFxMeshIndex resolves by the allMeshNames cache, which is null on our baked skeleton -> BUILD it
                // (parallel to skinnedMeshInfos) so the body fragment's name resolves to our mesh index.
                var amnField = AccessTools.Field(skel.GetType(), "allMeshNames");
                var amn = amnField?.GetValue(skel) as string[];
                if (amn != null && amn.Length > 0) { Plugin.Log.LogInfo("[HoverNative] allMeshNames before: [" + string.Join(", ", amn) + "]"); amn[0] = newName; }
                else if (arr != null && arr.Length > 0)
                {
                    var names = new string[arr.Length];
                    for (int i = 0; i < arr.Length; i++) names[i] = GetMember(arr.GetValue(i), "MeshName") as string;
                    amnField?.SetValue(skel, names);
                    Plugin.Log.LogInfo("[HoverNative] built allMeshNames: [" + string.Join(", ", names) + "]");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[HoverNative] rename error: " + e); }
        }

        // Re-run FragmentEntry.Load against our skeleton so the body fragment ('Unit_Era6_Common_LandingCrafts_01')
        // resolves its GPU mesh index to OUR renamed mesh entry. Fragments whose mesh name we don't have (e.g. the
        // barge floor) simply won't resolve -> not drawn, which is what we want.
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
                var mcField = AccessTools.Field(fragType, "meshCollection");   // the fragment's OWN lookup target
                var load = AccessTools.Method(fragType, "Load");
                for (int i = 0; i < frags.Length; i++)
                {
                    var item = frags.GetValue(i);                  // box the struct
                    if (item == null) continue;
                    mcField?.SetValue(item, ourSkeleton);          // repoint its meshCollection at OUR skeleton
                    try { load?.Invoke(item, new object[] { ourSkeleton, renderer, mcm, layer }); }
                    catch (Exception e) { Plugin.Log.LogWarning("[HoverNative] frag reload: " + (e.InnerException ?? e).Message); }
                    frags.SetValue(item, i);                       // write the modified struct BACK to the array
                }
                Plugin.Log.LogInfo("[HoverNative] re-resolved " + frags.Length + " fragment(s) with meshCollection=our skeleton");
            }
            catch (Exception e) { Plugin.Log.LogError("[HoverNative] ReloadFragments error: " + e); }
        }

        private static void DumpFragmentPaths(object addon, string label)
        {
            try
            {
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) { Plugin.Log.LogInfo("[HoverNative] " + label + " no FragmentEntries"); return; }
                foreach (var f in frags)
                {
                    if (f == null) continue;
                    var flds = f.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(fi => fi.FieldType == typeof(string) || fi.FieldType == typeof(int) || fi.FieldType == typeof(uint) || fi.FieldType.IsEnum)
                        .Select(fi => fi.Name + "=" + fi.GetValue(f));
                    Plugin.Log.LogInfo("[HoverNative] " + label + " fragment(" + f.GetType().Name + ") " + string.Join(" ", flds));
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
                if (ourTex == null) ourTex = BuildHovercraftTexture();
                foreach (var e in entries)
                {
                    var ol = GetMember(e, "OutputLayerInstance");
                    if (ol == null || ((ol as UnityEngine.Object)?.name ?? "").IndexOf("LandingCraft", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hostOutputLayer = ol; TickTexture(); break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[HoverNative] texture error: " + ex); }
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

        private static UnityEngine.Texture2D BuildHovercraftTexture()
        {
            const int S = 256;
            var tex = new UnityEngine.Texture2D(S, S, UnityEngine.TextureFormat.RGBA32, true) { name = "Hovercraft_Skin", wrapMode = UnityEngine.TextureWrapMode.Repeat };
            var px = new UnityEngine.Color[S * S];
            var skirt = new UnityEngine.Color(0.11f, 0.12f, 0.14f); var body = new UnityEngine.Color(0.60f, 0.62f, 0.65f); var bump = new UnityEngine.Color(0.78f, 0.80f, 0.82f);
            const float SkirtTop = 0.20f;
            for (int y = 0; y < S; y++) { float v = (y + 0.5f) / S; for (int x = 0; x < S; x++) { float u = (x + 0.5f) / S; UnityEngine.Color c;
                if (v < SkirtTop) { c = skirt; float du = UnityEngine.Mathf.Abs(Frac(u * 22f) - 0.5f); float dv = UnityEngine.Mathf.Abs(v - 0.09f);
                    if (du < 0.17f && dv < 0.05f) c = UnityEngine.Color.Lerp(skirt, bump, UnityEngine.Mathf.Clamp01((1f - UnityEngine.Mathf.Max(du / 0.17f, dv / 0.05f)) * 1.5f));
                    if (v > SkirtTop - 0.02f) c = UnityEngine.Color.Lerp(c, body, (v - (SkirtTop - 0.02f)) / 0.02f); }
                else { float panel = ((y % 40) < 1 || (x % 52) < 1) ? 0.90f : 1f; float wear = 0.95f + 0.05f * UnityEngine.Mathf.PerlinNoise(u * 9f, v * 9f);
                    c = new UnityEngine.Color(body.r * panel * wear, body.g * panel * wear, body.b * panel * wear, 1f);
                    if (v > 0.93f) c = UnityEngine.Color.Lerp(c, new UnityEngine.Color(0.50f, 0.52f, 0.55f), (v - 0.93f) / 0.07f); }
                px[y * S + x] = new UnityEngine.Color(c.r, c.g, c.b, 1f); } }
            tex.SetPixels(px); tex.Apply(); return tex;
        }
        private static float Frac(float f) => f - UnityEngine.Mathf.Floor(f);

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
    internal static class HoverRegisterHook
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return t != null ? AccessTools.Method(t, "AnimationLoad") : null;
        }
        static void Postfix(object __instance) { HovercraftInject.EnsureRegistered(__instance); }   // postfix: FX manager is ready
    }

    // Hook B: repoint only the Hovercraft pawn-def at our registered skeleton.
    [HarmonyPatch]
    internal static class HoverRepointHook
    {
        static MethodBase TargetMethod()
        {
            var addon = AccessTools.TypeByName("Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn");
            var animMgr = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return (addon != null && animMgr != null) ? AccessTools.Method(addon, "Load", new[] { animMgr }) : null;
        }
        static void Postfix(object __instance, object __0) { HovercraftInject.RepointHovercraft(__instance, __0); }
    }
}
