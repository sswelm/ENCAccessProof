using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // Replace the Era-6 naval transport mesh with our baked LCAC, via the shared LandingCrafts skeleton. The
    // Hovercraft AND the barge transport both ride Unit_Era6_Common_LandingCrafts_01_Skeleton, so this swap turns
    // BOTH into the LCAC. Per-pawn scoping (rendering the LCAC for only the Hovercraft) was attempted via a
    // registered skeleton clone but the clone breaks GPU skinning (mesh collapses/slabs for some facings), so the
    // robust shared swap is the working approach. True scoping would need a dedicated skeleton asset (data-side).
    //
    // Keep the real skeleton (bones/animation/material); only repoint its body mesh entry's GPU MeshIndex to our
    // uploaded LCAC, and set a naval skin on its material.
    [HarmonyPatch]
    internal static class HovercraftInject
    {
        // Amplitude.Framework.Guid fields for the baked Hovercraft_Skeleton.asset (our LCAC).
        const int SkelA = -1153397905, SkelB = 1134277020, SkelC = 577920438, SkelD = -573259371;

        private static object ourSkeleton;
        private static object boundFxMgr;
        private static object hostOutputLayer;            // the LandingCrafts FxOutputLayer (for the skin)
        private static UnityEngine.Texture2D ourTex;
        private static int ourMeshIndex;
        private static bool tried, loaded, uploaded, redirectLogged;

        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return t != null ? AccessTools.Method(t, "GetMeshCollection") : null;
        }

        private static void Postfix(object __instance, ref object __result)
        {
            if (!Plugin.RepointOnLoad.Value) return;
            EnsureInjected(__instance);
            if (!loaded || __result == null) return;
            var name = (__result as UnityEngine.Object)?.name;
            if (string.IsNullOrEmpty(name)) return;
            if (name.IndexOf("LandingCraft", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Skeleton", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                EnsureUploaded(__instance, __result);
                if (!uploaded || ourMeshIndex == 0) return;
                SwapMeshIndexInto(__result, ourMeshIndex);
                ApplyCustomTexture(__instance);
                if (!redirectLogged) { Plugin.Log.LogInfo("[Hover] redirecting '" + name + "' -> LCAC hovercraft"); redirectLogged = true; }
            }
        }

        private static void EnsureInjected(object mgr)
        {
            if (tried) return; tried = true;
            try
            {
                var guid = MakeGuid(SkelA, SkelB, SkelC, SkelD);
                var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
                var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                if (guid == null || mcType == null || adb == null) { Plugin.Log.LogError("[Hover] inject: missing types"); return; }
                var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition);
                if (load == null) { Plugin.Log.LogError("[Hover] inject: LoadAsset not found"); return; }
                var g = load.MakeGenericMethod(mcType);
                var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
                ourSkeleton = g.Invoke(null, args);
                if (ourSkeleton == null) { Plugin.Log.LogWarning("[Hover] inject: skeleton not loaded (rebuild the mod with the Hovercraft skeleton?)"); return; }

                var statusField = AccessTools.Field(ourSkeleton.GetType(), "loadingStatus");
                if (statusField != null) statusField.SetValue(ourSkeleton, Enum.ToObject(statusField.FieldType, 0)); // NotLoaded
                SetMember(ourSkeleton, "SkeletonId", -1);
                loaded = true;
                Plugin.Log.LogInfo("[Hover] hovercraft skeleton loaded + status reset: " + (ourSkeleton as UnityEngine.Object)?.name);
            }
            catch (Exception e) { Plugin.Log.LogError("[Hover] inject error: " + e); }
        }

        // (Re)upload our mesh into the GPU mesh-content manager; re-runs when the manager instance changes (save load).
        private static void EnsureUploaded(object mgr, object hostSkel)
        {
            try
            {
                var fxMgr = GetMember(mgr, "FxComponentMeshContentManager");
                if (fxMgr == null) return;
                if (uploaded && ReferenceEquals(fxMgr, boundFxMgr) && ourMeshIndex != 0) return;

                int slot = GetMember(hostSkel, "SkeletonId") is int i ? i : 0;
                var layerIdx = GetMember(mgr, "FXMeshLayerIndex");
                var loadIfn = AccessTools.Method(ourSkeleton.GetType(), "LoadIFN");
                if (layerIdx == null || loadIfn == null) { Plugin.Log.LogWarning("[Hover] upload: missing layer/LoadIFN"); return; }

                var statusField = AccessTools.Field(ourSkeleton.GetType(), "loadingStatus");
                if (statusField != null) statusField.SetValue(ourSkeleton, Enum.ToObject(statusField.FieldType, 0)); // NotLoaded
                loadIfn.Invoke(ourSkeleton, new object[] { fxMgr, layerIdx, slot });

                object meshIdx = null;
                var smiArr = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                if (smiArr != null && smiArr.Length > 0)
                    meshIdx = AccessTools.Field(smiArr.GetValue(0).GetType(), "MeshIndex")?.GetValue(smiArr.GetValue(0));
                ourMeshIndex = meshIdx != null ? Convert.ToInt32(meshIdx) : 0;
                boundFxMgr = fxMgr;
                uploaded = ourMeshIndex != 0;
                Plugin.Log.LogInfo($"[Hover] (re)uploaded LCAC mesh: ourMeshIndex={ourMeshIndex}");
            }
            catch (Exception e) { Plugin.Log.LogError("[Hover] upload error: " + e); }
        }

        // Keep the real LandingCrafts skeleton, repoint its first mesh entry's GPU MeshIndex to our LCAC. Idempotent.
        private static void SwapMeshIndexInto(object hostSkel, int idx)
        {
            try
            {
                var arr = AccessTools.Field(hostSkel.GetType(), "skinnedMeshInfos")?.GetValue(hostSkel) as Array;
                if (arr == null || arr.Length == 0) return;
                var item = arr.GetValue(0);
                var miField = AccessTools.Field(item.GetType(), "MeshIndex");
                if (miField == null) return;
                var old = miField.GetValue(item);
                if (Convert.ToInt32(old) == idx) return;
                miField.SetValue(item, Convert.ChangeType(idx, miField.FieldType));
                arr.SetValue(item, 0);
                Plugin.Log.LogInfo($"[Hover] repointed LandingCrafts mesh index {old} -> {idx} (reapplied after a reset)");
            }
            catch (Exception e) { Plugin.Log.LogError("[Hover] swap error: " + e); }
        }

        // Naval skin on the LandingCrafts output layer's material (_MainTex).
        private static void ApplyCustomTexture(object mgr)
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
                    hostOutputLayer = ol;
                    TickTexture();
                    break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Hover] texture inject error: " + ex); }
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

        // LCAC naval skin. The baker bakes height-based UVs (V = height, U = length), so this is a vertical gradient:
        // dark charcoal skirt with a row of light bumps along the bottom, light naval-gray hull/superstructure above.
        private static UnityEngine.Texture2D BuildHovercraftTexture()
        {
            const int S = 256;
            var tex = new UnityEngine.Texture2D(S, S, UnityEngine.TextureFormat.RGBA32, true)
            { name = "Hovercraft_Skin", wrapMode = UnityEngine.TextureWrapMode.Repeat };
            var px = new UnityEngine.Color[S * S];
            var skirt = new UnityEngine.Color(0.11f, 0.12f, 0.14f);
            var body = new UnityEngine.Color(0.60f, 0.62f, 0.65f);
            var bump = new UnityEngine.Color(0.78f, 0.80f, 0.82f);
            const float SkirtTop = 0.20f;
            for (int y = 0; y < S; y++)
            {
                float v = (y + 0.5f) / S;
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S;
                    UnityEngine.Color c;
                    if (v < SkirtTop)
                    {
                        c = skirt;
                        float du = UnityEngine.Mathf.Abs(Frac(u * 22f) - 0.5f);
                        float dv = UnityEngine.Mathf.Abs(v - 0.09f);
                        if (du < 0.17f && dv < 0.05f)
                            c = UnityEngine.Color.Lerp(skirt, bump, UnityEngine.Mathf.Clamp01((1f - UnityEngine.Mathf.Max(du / 0.17f, dv / 0.05f)) * 1.5f));
                        if (v > SkirtTop - 0.02f) c = UnityEngine.Color.Lerp(c, body, (v - (SkirtTop - 0.02f)) / 0.02f);
                    }
                    else
                    {
                        float panel = ((y % 40) < 1 || (x % 52) < 1) ? 0.90f : 1f;
                        float wear = 0.95f + 0.05f * UnityEngine.Mathf.PerlinNoise(u * 9f, v * 9f);
                        c = new UnityEngine.Color(body.r * panel * wear, body.g * panel * wear, body.b * panel * wear, 1f);
                        if (v > 0.93f) c = UnityEngine.Color.Lerp(c, new UnityEngine.Color(0.50f, 0.52f, 0.55f), (v - 0.93f) / 0.07f);
                    }
                    px[y * S + x] = new UnityEngine.Color(c.r, c.g, c.b, 1f);
                }
            }
            tex.SetPixels(px); tex.Apply();
            return tex;
        }

        private static float Frac(float f) => f - UnityEngine.Mathf.Floor(f);

        private static void SetMember(object o, string name, object val)
        {
            var t = o.GetType();
            var p = AccessTools.Property(t, name); if (p != null && p.CanWrite) { try { p.SetValue(o, val); return; } catch { } }
            var f = AccessTools.Field(t, name); if (f != null) { try { f.SetValue(o, val); } catch { } }
        }

        private static object GetMember(object o, string name)
        {
            var t = o.GetType();
            var p = AccessTools.Property(t, name); if (p != null) { try { return p.GetValue(o); } catch { } }
            var f = AccessTools.Field(t, name); if (f != null) { try { return f.GetValue(o); } catch { } }
            return null;
        }

        private static object MakeGuid(int a, int b, int c, int d)
        {
            var gt = AccessTools.TypeByName("Amplitude.Framework.Guid");
            if (gt == null) return null;
            var g = Activator.CreateInstance(gt);
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            gt.GetField("a", BF)?.SetValue(g, a); gt.GetField("b", BF)?.SetValue(g, b);
            gt.GetField("c", BF)?.SetValue(g, c); gt.GetField("d", BF)?.SetValue(g, d);
            return g;
        }
    }
}
