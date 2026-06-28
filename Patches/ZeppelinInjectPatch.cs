using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // Inject our CUSTOM zeppelin skeleton (mesh + bone authored together, so it binds cleanly) and redirect
    // the zeppelin's cruise-missile skeleton lookup to it. We hook AnimationManager.GetMeshCollection: once
    // the registry is live we load our baked Skeleton (shipped in ENC), RegisterMeshCollection + Register it,
    // then whenever GetMeshCollection would return the cruise-missile collection, return ours instead. No def
    // mutation -> no re-presentation; same single-bone rig as the missile -> clean binding.
    [HarmonyPatch]
    internal static class ZeppelinInject
    {
        // Amplitude.Framework.Guid fields for our baked Zeppelin_Skeleton.asset (from the editor dump).
        const int SkelA = 781638270, SkelB = 1224895347, SkelC = -2137756227, SkelD = -1885149832;
        // ...and for the baked Zeppelin_Atlas.asset (the model's real hull textures, atlased).
        const int TexA = 1762477492, TexB = 1213769387, TexC = -475189569, TexD = 500770371;

        // The pawn's fragment looks up its mesh BY NAME (FragmentEntry.Load -> GetFxMeshIndex(SkinnedMeshPath)),
        // and SkinnedMeshPath is the missile's mesh name. Our entry must carry that name to be found.
        const string MissileMeshName = "Unit_Era6_CruiseMissile_01";

        private static object ourSkeleton;
        private static object boundFxMgr;   // the FX mesh-content manager our mesh is currently uploaded against
        private static object missileOutputLayer;   // the FxOutputLayer whose render material we re-texture each frame
        private static UnityEngine.Texture2D ourTex;
        private static int ourMeshIndex;
        private static bool tried, loaded, uploaded, dumped, texturedLogged;

        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return t != null ? AccessTools.Method(t, "GetMeshCollection") : null;
        }

        private static void Postfix(object __instance, ref object __result)
        {
            if (!Plugin.RepointOnLoad.Value) return;
            EnsureInjected(__instance);          // load our skeleton + reset its load status (once)
            if (!loaded || __result == null) return;
            var name = (__result as UnityEngine.Object)?.name;
            if (string.IsNullOrEmpty(name)) return;
            // ONLY the missile SKELETON (not its projectile/effect collections, which would break the bomb FX)
            if (name.IndexOf("CruiseMissile", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Skeleton", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DumpRenderInfo(__instance, __result);   // shakee's request (once): animation/skeleton/matref/shader-layer

                // (Re)upload our mesh into the GPU mesh-content manager if needed (gives it a valid MeshIndex).
                // Re-uploads after a save load / FX reload, when the manager instance changes.
                EnsureUploaded(__instance, __result);
                if (!uploaded || ourMeshIndex == 0) return;   // not ready yet -> show the missile, retry next call

                // POC (CalmBreakfast's suggestion): do NOT inject our skeleton at all. Keep the REAL missile skeleton
                // (its bones, animation, GPU skinning, slot are all proven-good) and only point its mesh entry at OUR
                // uploaded mesh. Re-apply EVERY call: the engine re-LoadIFNs the missile skeleton on re-present/reload,
                // which resets MeshIndex back to the missile's mesh -> a one-time swap reverts. Idempotent.
                SwapMeshIndexInto(__result, ourMeshIndex);
                ApplyCustomTexture(__instance);   // put OUR texture on the zeppelin's albedo slot (_MainTex)
                // __result stays the missile skeleton (now drawing our mesh)
            }
        }

        // Keep the real missile skeleton, just repoint its first mesh entry's GPU MeshIndex to our uploaded mesh.
        // SkinnedMeshInfo is a struct in an array -> mutate the boxed copy and write it back.
        private static void SwapMeshIndexInto(object missileSkel, int idx)
        {
            try
            {
                var arr = AccessTools.Field(missileSkel.GetType(), "skinnedMeshInfos")?.GetValue(missileSkel) as Array;
                if (arr == null || arr.Length == 0) { Plugin.Log.LogWarning("[ENCProof] swap: missile skeleton has no skinnedMeshInfos"); return; }
                var item = arr.GetValue(0);
                var miField = AccessTools.Field(item.GetType(), "MeshIndex");
                if (miField == null) { Plugin.Log.LogWarning("[ENCProof] swap: no MeshIndex field"); return; }
                var old = miField.GetValue(item);
                if (Convert.ToInt32(old) == idx) return;     // already ours -> nothing to do
                miField.SetValue(item, Convert.ChangeType(idx, miField.FieldType));  // MeshIndex is uint
                arr.SetValue(item, 0);
                Plugin.Log.LogInfo($"[ENCProof] repointed missile mesh index {old} -> {idx} (reapplied after an engine/load reset)");
            }
            catch (Exception e) { Plugin.Log.LogError("[ENCProof] swap error: " + e); }
        }

        // Put OUR texture on the zeppelin's albedo slot (_MainTex) of the missile output layer's render material.
        // Re-applied each call (the engine may rebuild the proxy material). Affects the shared missile material.
        private static void ApplyCustomTexture(object mgr)
        {
            try
            {
                var content = GetMember(mgr, "Content");
                var entries = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (entries == null) return;
                if (ourTex == null) ourTex = LoadAtlasTexture() ?? BuildZeppelinTexture();   // real model atlas, else procedural

                foreach (var e in entries)
                {
                    var ol = GetMember(e, "OutputLayerInstance");
                    if (ol == null || ((ol as UnityEngine.Object)?.name ?? "").IndexOf("CruiseMissile", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    missileOutputLayer = ol;   // remember it so Update() can re-texture every frame (beats the async proxy rebind)
                    TickTexture();
                    break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[ENCProof] texture inject error: " + ex); }
        }

        // Re-apply our texture to the live render material(s) — called every frame from Plugin.Update so it wins
        // after the proxy textures finish loading async (which would otherwise rebind _MainTex over ours).
        internal static void TickTexture()
        {
            if (missileOutputLayer == null || ourTex == null) return;
            try
            {
                if (GetMember(missileOutputLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                        foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial" })
                            if (GetMember(ro, fld) is UnityEngine.Material mat)
                            {
                                mat.SetTexture("_MainTex", ourTex);
                                if (!texturedLogged) { Plugin.Log.LogInfo("[ENCProof] applied custom texture to " + mat.name + "._MainTex"); texturedLogged = true; }
                            }
            }
            catch { }
        }

        // Load the baked airship atlas (the model's real hull textures) from the shipped bundle, by GUID — same
        // pattern as loading the skeleton. Returns null if it's not in the bundle yet (then we fall back to procedural).
        private static UnityEngine.Texture2D LoadAtlasTexture()
        {
            try
            {
                var guid = MakeGuid(TexA, TexB, TexC, TexD);
                var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                if (guid == null || adb == null) return null;
                var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition);
                if (load == null) return null;
                var g = load.MakeGenericMethod(typeof(UnityEngine.Texture2D));
                var ps = g.GetParameters();
                var tex = g.Invoke(null, ps.Length == 1 ? new[] { guid } : new[] { guid, null }) as UnityEngine.Texture2D;
                Plugin.Log.LogInfo("[ENCProof] airship atlas texture: " + (tex != null ? tex.name : "NOT FOUND (using procedural)"));
                return tex;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[ENCProof] atlas load failed: " + e.Message); return null; }
        }

        // A procedural real-zeppelin skin (ref: the "L 2"): warm tan canvas with faint structural panels and a gentle
        // around-the-hull shade. UVs: y ~ along the length (rings), x ~ around the circumference (longitudinal seams).
        private static UnityEngine.Texture2D BuildZeppelinTexture()
        {
            const int S = 256;
            var tex = new UnityEngine.Texture2D(S, S, UnityEngine.TextureFormat.RGBA32, false) { name = "Zeppelin_CustomTex" };
            var baseCol = new UnityEngine.Color(0.84f, 0.77f, 0.62f);   // warm tan/cream canvas
            var px = new UnityEngine.Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    // gentle light/dark around the hull (top-lit feel): 0.82 .. 1.0
                    float shade = 0.91f + 0.09f * UnityEngine.Mathf.Cos((x / (float)S) * 6.2831853f);
                    // faint structural lines: circumferential rings (along length) + longitudinal seams (around)
                    float panel = ((y % 26) < 2) ? 0.90f : 1f;   // ring frames
                    if ((x % 32) < 1) panel *= 0.93f;            // longitudinal seam
                    float m = shade * panel;
                    px[y * S + x] = new UnityEngine.Color(baseCol.r * m, baseCol.g * m, baseCol.b * m, 1f);
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        // shakee's request: dump the unit's animation + skeleton + the full material-ref -> shader-layer catalog to a
        // shareable file, to see "how a custom material needs to look". Runs once, when the FX content is loaded.
        private static void DumpRenderInfo(object mgr, object missileSkel)
        {
            if (dumped) return;
            try
            {
                var content = GetMember(mgr, "Content");
                var entries = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (entries == null || entries.Length == 0) return;   // FX content not ready yet -> retry next call

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== ENC RENDER DUMP  (animation / skeleton / material ref / shader layer) ===");

                // --- SKELETON ---
                sb.AppendLine("\n[SKELETON]  " + (missileSkel as UnityEngine.Object)?.name);
                sb.AppendLine("  SkeletonId=" + GetMember(missileSkel, "SkeletonId") + "  BonesCount=" + GetMember(missileSkel, "BonesCount"));
                if (GetMember(missileSkel, "BoneInfos") is Array bones)
                {
                    string names = "";
                    foreach (var b in bones) names += (names.Length > 0 ? ", " : "") + GetMember(b, "Name");
                    sb.AppendLine("  bones=[" + names + "]");
                }
                if (AccessTools.Field(missileSkel.GetType(), "skinnedMeshInfos")?.GetValue(missileSkel) is Array smi)
                    foreach (var it in smi)
                    {
                        var fx = AccessTools.Field(it.GetType(), "FxMeshContent")?.GetValue(it);
                        sb.AppendLine("  mesh: name=" + GetMember(it, "MeshName") + " MeshIndex=" + GetMember(it, "MeshIndex") +
                                      " format=" + (fx != null ? GetMember(fx, "encodingFormat") : null) +
                                      " fxGuid=" + (fx != null ? GetMember(fx, "Guid") : null));
                    }

                // --- ANIMATION ---
                sb.AppendLine("\n[ANIMATION]");
                sb.AppendLine("  AnimatorController=" + GetMember(missileSkel, "AnimatorController"));
                sb.AppendLine("  AnimatorOverrideController=" + GetMember(missileSkel, "AnimatorOverrideController"));
                var animClips = GetMember(content, "AnimationClipCollections") as Array;
                sb.AppendLine("  AnimationClipCollections count=" + (animClips != null ? animClips.Length : 0));

                // --- MATERIAL REF -> SHADER (OUTPUT) LAYER CATALOG ---
                // Each FxOutputLayer holds RenderOutputs[]; each RenderOutput carries the actual material-asset GUIDs
                // (high/mid res) + keyword, and (once loaded) a runtime render material whose shader we can name.
                sb.AppendLine("\n[OUTPUT-LAYER CATALOG]  matRef GUID -> FxOutputLayer -> renderOutputs{material GUIDs, shader}   (count=" + entries.Length + ")");
                foreach (var e in entries)
                {
                    var ol = GetMember(e, "OutputLayerInstance");
                    sb.Append("  matRef=" + GetMember(e, "Material") + "  layer=" + ((ol as UnityEngine.Object)?.name ?? "null") +
                              "  preview=" + (ol != null ? GetMember(ol, "previewMaterialRef") : null));
                    if (ol != null && GetMember(ol, "RenderOutputs") is Array outs)
                        foreach (var ro in outs)
                        {
                            var rm = GetMember(ro, "renderMaterial") as UnityEngine.Object;
                            string shader = rm != null ? (AccessTools.Property(rm.GetType(), "shader")?.GetValue(rm) as UnityEngine.Object)?.name ?? "null" : "null";
                            sb.Append("  {highResMat=" + GetMember(ro, "highResMaterialGuid") + " midResMat=" + GetMember(ro, "midResMaterialGuid") +
                                      " kw='" + GetMember(ro, "materialKeyword") + "' runtimeMat=" + (rm?.name ?? "null") + " shader=" + shader + "}");
                        }
                    sb.AppendLine();
                }

                // --- FULL FxOutputLayer STRUCTURE (real values) for the missile's layer: the authoring template ---
                foreach (var e in entries)
                {
                    var ol = GetMember(e, "OutputLayerInstance");
                    var nm = (ol as UnityEngine.Object)?.name ?? "";
                    if (ol == null || nm.IndexOf("CruiseMissile", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.AppendLine("\n[FXOUTPUTLAYER DETAIL]  " + nm + "  (every field, real values -> authoring template)");
                    DumpFields(sb, "  ", ol);
                    if (GetMember(ol, "RenderOutputs") is Array ros)
                        for (int i = 0; i < ros.Length; i++)
                        {
                            var ro = ros.GetValue(i);
                            sb.AppendLine("  RenderOutput[" + i + "]:");
                            DumpFields(sb, "    ", ro);
                            var liveMat = GetMember(ro, "currentRenderMaterial") ?? GetMember(ro, "runTimeRenderMaterial") ?? GetMember(ro, "renderMaterial");
                            DumpMaterialTextures(sb, "    tex ", liveMat);
                        }
                    break;
                }

                var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "ENC_RenderDump.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo("[ENCProof] render dump (" + entries.Length + " output layers) written to " + path);
                dumped = true;
            }
            catch (Exception ex) { Plugin.Log.LogError("[ENCProof] dump error: " + ex); }
        }

        // List a material's texture slots + the texture bound to each (so we know where to inject a custom texture).
        private static void DumpMaterialTextures(System.Text.StringBuilder sb, string prefix, object matObj)
        {
            var mat = matObj as UnityEngine.Object;
            if (mat == null) { sb.AppendLine(prefix + "(no render material loaded)"); return; }
            try
            {
                var getNames = AccessTools.Method(mat.GetType(), "GetTexturePropertyNames", Type.EmptyTypes);
                var getTex = AccessTools.Method(mat.GetType(), "GetTexture", new[] { typeof(string) });
                if (getNames?.Invoke(mat, null) is string[] names)
                    foreach (var n in names)
                    {
                        var tex = getTex?.Invoke(mat, new object[] { n }) as UnityEngine.Object;
                        sb.AppendLine(prefix + n + " = " + (tex?.name ?? "null"));
                    }
            }
            catch (Exception e) { sb.AppendLine(prefix + "err: " + e.Message); }
        }

        // Dump every instance field (public + private) of an object with its real value — for the FxOutputLayer
        // authoring template.
        private static void DumpFields(System.Text.StringBuilder sb, string indent, object o)
        {
            if (o == null) { sb.AppendLine(indent + "null"); return; }
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var f in o.GetType().GetFields(BF))
            {
                object v; try { v = f.GetValue(o); } catch { v = "<err>"; }
                string disp = v is Array a ? "[" + a.Length + "]" : (v as UnityEngine.Object)?.name ?? v?.ToString() ?? "null";
                sb.AppendLine(indent + f.Name + " (" + f.FieldType.Name + ") = " + disp);
            }
        }

        // (Re)upload our mesh into the GPU mesh-content manager. Re-runs when the manager instance changes (a save
        // load / FX reload rebuilds it and drops our mesh), so the swap always has a valid MeshIndex to point at.
        private static void EnsureUploaded(object mgr, object missileSkel)
        {
            try
            {
                var fxMgr = GetMember(mgr, "FxComponentMeshContentManager");
                if (fxMgr == null) return;
                if (uploaded && ReferenceEquals(fxMgr, boundFxMgr) && ourMeshIndex != 0) return;   // still valid

                var mObj = GetMember(missileSkel, "SkeletonId");
                int slot = (mObj is int i) ? i : 0;          // any valid index; the mesh upload is index-independent
                var layerIdx = GetMember(mgr, "FXMeshLayerIndex");
                var loadIfn = AccessTools.Method(ourSkeleton.GetType(), "LoadIFN");
                if (layerIdx == null || loadIfn == null) { Plugin.Log.LogWarning("[ENCProof] upload: missing layer/LoadIFN"); return; }

                // force a fresh Load (loadingStatus is left Loaded after a prior upload -> LoadIFN would no-op)
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
                Plugin.Log.LogInfo($"[ENCProof] (re)uploaded our mesh: ourMeshIndex={ourMeshIndex}");
            }
            catch (Exception e) { Plugin.Log.LogError("[ENCProof] upload error: " + e); }
        }

        // compare our baked collection against the missile's to find which render field ours lacks
        private static void DumpMc(string tag, object mc)
        {
            try
            {
                if (mc == null) { Plugin.Log.LogInfo($"[ENCProof] {tag}: NULL"); return; }
                var t = mc.GetType();
                Plugin.Log.LogInfo($"[ENCProof] {tag}: type={t.Name} name={(mc as UnityEngine.Object)?.name}");
                var smi = AccessTools.Field(t, "skinnedMeshInfos")?.GetValue(mc) as Array;
                Plugin.Log.LogInfo($"[ENCProof]   skinnedMeshInfos: {(smi != null ? smi.Length.ToString() : "null")}");
                if (smi != null)
                    foreach (var it in smi)
                    {
                        var mn = AccessTools.Field(it.GetType(), "MeshName")?.GetValue(it);
                        var fx = AccessTools.Field(it.GetType(), "FxMeshContent")?.GetValue(it);
                        Plugin.Log.LogInfo($"[ENCProof]      MeshName={mn} FxMeshContent={(fx != null ? "set" : "NULL")}");
                    }
                var si = AccessTools.Property(t, "SkeletonInstance")?.GetValue(mc);
                Plugin.Log.LogInfo($"[ENCProof]   SkeletonInstance={(si as UnityEngine.Object)?.name ?? "null"}, LoadingStatus={AccessTools.Field(t, "loadingStatus")?.GetValue(mc)}");
                var bc = AccessTools.Property(t, "BonesCount")?.GetValue(mc) ?? AccessTools.Field(t, "BonesCount")?.GetValue(mc);
                Plugin.Log.LogInfo($"[ENCProof]   BonesCount={bc}");

                // dump the FxMeshContent internals of the first mesh (vertex/weight/format data)
                const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                if (smi != null && smi.Length > 0)
                {
                    var fx = AccessTools.Field(smi.GetValue(0).GetType(), "FxMeshContent")?.GetValue(smi.GetValue(0));
                    if (fx != null)
                        foreach (var f in fx.GetType().GetFields(BF))
                        {
                            var v = f.GetValue(fx);
                            Plugin.Log.LogInfo($"[ENCProof]      fx.{f.Name}({f.FieldType.Name})={(v is Array a ? "[" + a.Length + "]" : v?.ToString() ?? "null")}");
                        }
                }
            }
            catch (Exception e) { Plugin.Log.LogInfo($"[ENCProof] {tag} dump err: {e.Message}"); }
        }

        private static void EnsureInjected(object mgr)
        {
            if (tried) return; tried = true;
            try
            {
                // 1) build the Amplitude GUID for our skeleton and load it from the shipped bundle
                var guid = MakeGuid(SkelA, SkelB, SkelC, SkelD);
                var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
                var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                if (guid == null || mcType == null || adb == null) { Plugin.Log.LogError("[ENCProof] inject: missing types"); return; }

                var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition);
                if (load == null) { Plugin.Log.LogError("[ENCProof] inject: LoadAsset not found"); return; }
                var g = load.MakeGenericMethod(mcType);
                var ps = g.GetParameters();
                var args = ps.Length == 1 ? new[] { guid } : new[] { guid, null };
                ourSkeleton = g.Invoke(null, args);
                if (ourSkeleton == null) { Plugin.Log.LogWarning("[ENCProof] inject: skeleton not loaded (built the mod with the new Skeleton?)"); return; }

                // Our skeleton ships loadingStatus=Loaded, which makes MeshCollection.LoadIFN a NO-OP (Load never
                // runs -> MeshIndex stays 0). Reset it so the later slot-reuse LoadIFN actually uploads the mesh.
                var statusField = AccessTools.Field(ourSkeleton.GetType(), "loadingStatus");
                if (statusField != null) statusField.SetValue(ourSkeleton, Enum.ToObject(statusField.FieldType, 0)); // NotLoaded
                SetMember(ourSkeleton, "SkeletonId", -1);

                // Rename our mesh entry so the fragment's by-name lookup (GetFxMeshIndex(SkinnedMeshPath)) finds it.
                // SkinnedMeshInfo is a struct in an array -> mutate the boxed copy and write it back.
                try
                {
                    var arr = AccessTools.Field(ourSkeleton.GetType(), "skinnedMeshInfos")?.GetValue(ourSkeleton) as Array;
                    if (arr != null && arr.Length > 0)
                    {
                        var item = arr.GetValue(0);
                        var mnField = AccessTools.Field(item.GetType(), "MeshName");
                        var old = mnField?.GetValue(item);
                        mnField?.SetValue(item, MissileMeshName);
                        arr.SetValue(item, 0);
                        Plugin.Log.LogInfo($"[ENCProof] renamed mesh entry '{old}' -> '{MissileMeshName}' (so the fragment lookup matches)");
                    }
                }
                catch (Exception re) { Plugin.Log.LogError("[ENCProof] rename error: " + re); }

                loaded = true;
                Plugin.Log.LogInfo("[ENCProof] zeppelin skeleton loaded + status reset: " + (ourSkeleton as UnityEngine.Object)?.name);
            }
            catch (Exception e) { Plugin.Log.LogError("[ENCProof] inject error: " + e); }
        }

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
