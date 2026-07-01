using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;

namespace ENCAccessProof
{
    // Generic, registry-driven model injector — the runtime half of the Universal Baker. Reads enc_models.txt from
    // BepInEx/config (written by the editor), registers every baked skeleton, and on each unit's AddOn.Load repoints
    // the matching pawn definition onto its skeleton using the proven self-discovery (read the host's body mesh name,
    // rename ours to match, resolve, skin via <bodyMesh>_OutputLayer). One patch handles any number of models.
    internal class ModelEntry
    {
        public string resourceName = "", pawnDescription = "";
        public int sa, sb, sc, sd, ta, tb, tc, td;   // skeleton + atlas Amplitude guid components
        public object skeleton;
        public object hostOutputLayer;
        public UnityEngine.Texture2D tex;
        public string layerHint = "";
        public object isolatedLayer;     // our private clone of the host output layer (texture isolation)
        public bool repointed;
    }

    // JSON shapes matching the editor's ModelDef (JsonUtility binds by field name; extra fields are ignored).
    // Full mirror of the editor's ModelDef so JsonUtility maps every field cleanly (it can return models=null if the
    // JSON has nested objects like rotation/position that the target class doesn't declare).
    [Serializable] internal class JsonModel
    {
        public string resourceName, pawnDescription, modelFile;
        public UnityEngine.Vector3 rotation, position;
        public float size, smoothingAngle;
        public int normalsMode, convertGrid;
        public int[] skel, atlas;
    }
    [Serializable] internal class JsonModelList { public List<JsonModel> models; }

    internal static class UniversalInject
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static List<ModelEntry> entries;
        static bool loaded, registered, repointActiveLogged, stLogged;
        static UnityEngine.Texture2D _flatN, _white, _black, _grey;   // neutral overlay maps (kill the host's detail/camo)

        static void LoadRegistry()
        {
            if (loaded) return; loaded = true;
            entries = new List<ModelEntry>();
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "enc_models.json");
                if (!File.Exists(path)) { Plugin.Log.LogInfo("[Uni] no registry at " + path); return; }
                var text = File.ReadAllText(path);

                // direct parse of the known fields (JsonUtility returns models=null on this nested structure). Each model
                // has exactly one of each field in document order, so the i-th match of each belongs to model i.
                const string i4 = @"\[\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\]";
                var rn = Regex.Matches(text, "\"resourceName\"\\s*:\\s*\"([^\"]*)\"");
                var pd = Regex.Matches(text, "\"pawnDescription\"\\s*:\\s*\"([^\"]*)\"");
                var sk = Regex.Matches(text, "\"skel\"\\s*:\\s*" + i4);
                var at = Regex.Matches(text, "\"atlas\"\\s*:\\s*" + i4);
                int G(Match m, int g) => int.TryParse(m.Groups[g].Value, out var r) ? r : 0;
                int n = Math.Min(pd.Count, Math.Min(sk.Count, at.Count));
                for (int i = 0; i < n; i++)
                {
                    entries.Add(new ModelEntry
                    {
                        resourceName = i < rn.Count ? rn[i].Groups[1].Value : ("model" + i),
                        pawnDescription = pd[i].Groups[1].Value,
                        sa = G(sk[i], 1), sb = G(sk[i], 2), sc = G(sk[i], 3), sd = G(sk[i], 4),
                        ta = G(at[i], 1), tb = G(at[i], 2), tc = G(at[i], 3), td = G(at[i], 4),
                    });
                }
                Plugin.Log.LogInfo($"[Uni] read {text.Length} chars; parsed {entries.Count} entr(ies) [" + string.Join(", ", entries.Select(e => e.resourceName + "->" + e.pawnDescription)) + "]");
            }
            catch (Exception e) { Plugin.Log.LogError("[Uni] registry load: " + e); }
        }

        // register every skeleton before Apply() builds GPU buffers (AnimationLoad postfix)
        internal static void EnsureRegistered(object animMgr)
        {
            if (registered) return;
            if (animMgr == null) { Plugin.Log.LogWarning("[Uni] EnsureRegistered: animMgr null"); return; }
            Plugin.Log.LogInfo("[Uni] EnsureRegistered fired");
            LoadRegistry();
            if (entries.Count == 0) { registered = true; return; }
            try
            {
                var reg = AccessTools.Method(animMgr.GetType(), "RegisterMeshCollection");
                if (reg == null) { Plugin.Log.LogError("[Uni] RegisterMeshCollection not found"); return; }
                int n = 0;
                foreach (var e in entries)
                {
                    if (e.skeleton == null) e.skeleton = LoadSkeleton(e.sa, e.sb, e.sc, e.sd, e.resourceName);
                    if (e.skeleton == null) continue;
                    var sf = AccessTools.Field(e.skeleton.GetType(), "loadingStatus");
                    if (sf != null) sf.SetValue(e.skeleton, Enum.ToObject(sf.FieldType, 0)); // NotLoaded
                    SetMember(e.skeleton, "SkeletonId", -1);
                    reg.Invoke(animMgr, new[] { e.skeleton });
                    n++;
                }
                if (n > 0)
                {
                    var apply = AccessTools.Method(animMgr.GetType(), "Apply", Type.EmptyTypes)
                        ?? animMgr.GetType().GetMethods(BF).FirstOrDefault(m => m.Name == "Apply" && m.GetParameters().Length == 0);
                    apply?.Invoke(animMgr, null);
                }
                registered = true;
                Plugin.Log.LogInfo($"[Uni] registered {n} skeleton(s) + re-Apply'd");
            }
            catch (Exception e) { Plugin.Log.LogError("[Uni] register: " + e); }
        }

        // repoint a matching unit (AddOn.Load postfix)
        internal static void RepointMatch(object addon, object animMgr)
        {
            if (addon == null || animMgr == null) return;
            if (!repointActiveLogged) { repointActiveLogged = true; Plugin.Log.LogInfo($"[Uni] repoint-hook ACTIVE (UniversalInject={Plugin.UniversalInjectOn.Value}, entries={(entries == null ? -1 : entries.Count)})"); }
            if (!Plugin.UniversalInjectOn.Value) return;
            LoadRegistry();
            if (entries.Count == 0) return;
            try
            {
                var def = GetMember(addon, "Definition");
                var name = (def as UnityEngine.Object)?.name ?? "";
                if (name.Length == 0) return;
                var e = entries.FirstOrDefault(x => x.pawnDescription.Length > 0 && name.IndexOf(x.pawnDescription, StringComparison.OrdinalIgnoreCase) >= 0);
                if (e == null) return;
                Plugin.Log.LogInfo($"[Uni] MATCH addon='{name}' -> {e.resourceName} (skel {e.sa},{e.sb},{e.sc},{e.sd})");

                EnsureRegistered(animMgr);
                if (e.skeleton == null) return;

                var bodyName = DiscoverBodyMeshName(addon);
                if (!string.IsNullOrEmpty(bodyName)) { RenameBodyMesh(e.skeleton, bodyName); e.layerHint = bodyName; }
                EnsureUploaded(e, animMgr);
                SetMember(addon, "Skeleton", e.skeleton);
                SetMember(addon, "MeshCollection", e.skeleton);
                ReloadFragments(addon, animMgr, e.skeleton, e);
                ApplyTexture(e, animMgr);
                if (!e.repointed) { e.repointed = true; Plugin.Log.LogInfo($"[Uni] repointed '{name}' -> {e.resourceName} (mesh '{bodyName}', layer '{e.layerHint}')"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] repoint: " + ex); }
        }

        internal static void TickTexture()
        {
            if (entries == null) return;
            foreach (var e in entries) TickOne(e);
        }

        // ---- helpers (per-entry generalizations of StealthCruiserInject) ----

        static object LoadSkeleton(int a, int b, int c, int d, string tag)
        {
            var guid = MakeGuid(a, b, c, d);
            var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
            var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
            if (guid == null || mcType == null || adb == null) return null;
            var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
            var g = load?.MakeGenericMethod(mcType);
            var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
            var skel = g.Invoke(null, args);
            Plugin.Log.LogInfo($"[Uni] loaded skeleton '{tag}': " + ((skel as UnityEngine.Object)?.name ?? "NULL (rebuild mod?)"));
            return skel;
        }

        static void EnsureUploaded(ModelEntry e, object animMgr)
        {
            try
            {
                var smi = AccessTools.Field(e.skeleton.GetType(), "skinnedMeshInfos")?.GetValue(e.skeleton) as Array;
                if (smi != null && smi.Length > 0 && Convert.ToInt32(GetMember(smi.GetValue(0), "MeshIndex")) != 0) return;
                var fxMgr = GetMember(animMgr, "FxComponentMeshContentManager");
                if (fxMgr == null) return;
                var sf = AccessTools.Field(e.skeleton.GetType(), "loadingStatus");
                if (sf != null) sf.SetValue(e.skeleton, Enum.ToObject(sf.FieldType, 0));
                var layerIdx = GetMember(animMgr, "FXMeshLayerIndex");
                int slot = GetMember(e.skeleton, "SkeletonId") is int s ? s : 0;
                AccessTools.Method(e.skeleton.GetType(), "LoadIFN")?.Invoke(e.skeleton, new object[] { fxMgr, layerIdx, slot });
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] upload: " + ex); }
        }

        static string DiscoverBodyMeshName(object addon)
        {
            try
            {
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) return null;
                var mnField = AccessTools.Field(frags.GetType().GetElementType(), "meshName");
                if (mnField == null) return null;
                string hull = null, any = null;
                foreach (var f in frags)
                {
                    if (f == null) continue;
                    var mn = mnField.GetValue(f) as string;
                    if (string.IsNullOrEmpty(mn)) continue;
                    if (any == null) any = mn;
                    if (hull == null && mn.IndexOf("Unit_", StringComparison.OrdinalIgnoreCase) >= 0
                        && mn.IndexOf("Water", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Wake", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Foam", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Proof", StringComparison.OrdinalIgnoreCase) < 0)
                        hull = mn;
                }
                return hull ?? any;
            }
            catch { return null; }
        }

        static void RenameBodyMesh(object skel, string newName)
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
                if (amn != null && amn.Length > 0) amn[0] = newName;
                else if (arr != null && arr.Length > 0)
                {
                    var names = new string[arr.Length];
                    for (int i = 0; i < arr.Length; i++) names[i] = GetMember(arr.GetValue(i), "MeshName") as string;
                    amnField?.SetValue(skel, names);
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[Uni] rename: " + e); }
        }

        static void ReloadFragments(object addon, object animMgr, object skel, ModelEntry e)
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
                var mnField = AccessTools.Field(fragType, "meshName");
                var folField = AccessTools.Field(fragType, "fxOutputLayer");
                var load = AccessTools.Method(fragType, "Load");
                for (int i = 0; i < frags.Length; i++)
                {
                    var item = frags.GetValue(i);
                    if (item == null) continue;
                    mcField?.SetValue(item, skel);
                    // TEXTURE ISOLATION: give OUR body fragment a private CLONE of the output layer. Load() then calls
                    // GetLayerIndexAddItIFN(clone), allocating it a fresh GPU slot -> our skin (painted on the clone)
                    // no longer bleeds onto other units that share the host layer (e.g. the real Visby Corvette).
                    var mn = mnField?.GetValue(item) as string;
                    if (e != null && folField != null && !string.IsNullOrEmpty(e.layerHint) && mn == e.layerHint)
                    {
                        if (e.isolatedLayer == null && folField.GetValue(item) is UnityEngine.Object host && host != null)
                        {
                            var clone = UnityEngine.Object.Instantiate(host);
                            clone.name = e.resourceName + "_OutputLayer";
                            e.isolatedLayer = clone;
                            Plugin.Log.LogInfo($"[Uni] cloned output layer for {e.resourceName} -> '{clone.name}'");
                        }
                        if (e.isolatedLayer != null) folField.SetValue(item, e.isolatedLayer);
                    }
                    try { load?.Invoke(item, new object[] { skel, renderer, mcm, layer }); }
                    catch (Exception ex) { Plugin.Log.LogWarning("[Uni] frag reload: " + (ex.InnerException ?? ex).Message); }
                    frags.SetValue(item, i);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] ReloadFragments: " + ex); }
        }

        static void ApplyTexture(ModelEntry e, object mgr)
        {
            try
            {
                if (e.tex == null) e.tex = LoadAtlas(e.ta, e.tb, e.tc, e.td, e.resourceName);
                // isolated path: paint ONLY our private clone (the host layer + real units keep their own skin)
                if (e.isolatedLayer != null) { e.hostOutputLayer = e.isolatedLayer; TickOne(e); return; }
                // fallback (no clone): the shared host layer
                var content = GetMember(mgr, "Content");
                var list = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (list == null || string.IsNullOrEmpty(e.layerHint)) return;
                foreach (var entry in list)
                {
                    var ol = GetMember(entry, "OutputLayerInstance");
                    var oln = (ol as UnityEngine.Object)?.name ?? "";
                    if (ol == null || oln.IndexOf(e.layerHint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    e.hostOutputLayer = ol; TickOne(e);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] texture: " + ex); }
        }

        static void TickOne(ModelEntry e)
        {
            if (e.hostOutputLayer == null || e.tex == null) return;
            try
            {
                if (GetMember(e.hostOutputLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                        foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial" })
                            if (GetMember(ro, fld) is UnityEngine.Material mat)
                            {
                                if (_flatN == null) { _flatN = Solid(0.5f, 0.5f, 1f); _white = Solid(1f, 1f, 1f); _black = Solid(0f, 0f, 0f); _grey = Solid(0.5f, 0.5f, 0.5f); }
                                if (!stLogged) { stLogged = true; Plugin.Log.LogInfo($"[Uni] {e.resourceName} host _MainTex_ST scale={mat.GetTextureScale("_MainTex")} offset={mat.GetTextureOffset("_MainTex")}"); }
                                mat.SetTexture("_MainTex", e.tex);
                                // Reset the atlas UV transform. The host's material crops _MainTex to its slice of a SHARED
                                // atlas (scale/offset != 1,0); left in place, our full atlas is sampled through that crop and
                                // the skin looks scrambled. Map our atlas 1:1 to the mesh UVs.
                                mat.SetTextureScale("_MainTex", UnityEngine.Vector2.one);
                                mat.SetTextureOffset("_MainTex", UnityEngine.Vector2.zero);
                                // neutralise the host's overlay maps so only OUR albedo shows (they're sampled with our
                                // UVs -> they'd smear the host's detail/camo across the model, worst at the stern).
                                mat.SetTexture("_NormalMap", _flatN);
                                mat.SetTexture("_AmbiantOcclusionMap", _white);
                                mat.SetTexture("_ColorMask", _black);
                                mat.SetTexture("_RoughnessMap", _grey);
                                mat.SetTexture("_MetallicMap", _black);
                            }
            }
            catch { }
        }

        static UnityEngine.Texture2D Solid(float r, float g, float b)
        { var t = new UnityEngine.Texture2D(1, 1); t.SetPixel(0, 0, new UnityEngine.Color(r, g, b, 1f)); t.Apply(); return t; }

        static UnityEngine.Texture2D LoadAtlas(int a, int b, int c, int d, string tag)
        {
            try
            {
                var guid = MakeGuid(a, b, c, d);
                var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                if (guid == null || adb == null) return null;
                var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
                var g = load?.MakeGenericMethod(typeof(UnityEngine.Texture2D));
                var args = g.GetParameters().Length == 1 ? new object[] { guid } : new object[] { guid, null };
                var tex = g.Invoke(null, args) as UnityEngine.Texture2D;
                Plugin.Log.LogInfo($"[Uni] loaded atlas '{tag}': " + (tex != null ? tex.name + " " + tex.width + "x" + tex.height : "NULL"));
                return tex;
            }
            catch (Exception e) { Plugin.Log.LogError("[Uni] atlas: " + e); return null; }
        }

        static void SetMember(object o, string name, object val)
        { var t = o.GetType(); var p = AccessTools.Property(t, name); if (p != null && p.CanWrite) { try { p.SetValue(o, val); return; } catch { } } var f = AccessTools.Field(t, name); if (f != null) { try { f.SetValue(o, val); } catch { } } }
        static object GetMember(object o, string name)
        { if (o == null) return null; var t = o.GetType(); var p = AccessTools.Property(t, name); if (p != null) { try { return p.GetValue(o); } catch { } } var f = AccessTools.Field(t, name); if (f != null) { try { return f.GetValue(o); } catch { } } return null; }
        static object MakeGuid(int a, int b, int c, int d)
        { var gt = AccessTools.TypeByName("Amplitude.Framework.Guid"); if (gt == null) return null; var g = Activator.CreateInstance(gt);
          gt.GetField("a", BF)?.SetValue(g, a); gt.GetField("b", BF)?.SetValue(g, b); gt.GetField("c", BF)?.SetValue(g, c); gt.GetField("d", BF)?.SetValue(g, d); return g; }
    }

    [HarmonyPatch]
    internal static class UniRegisterHook
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return t != null ? AccessTools.Method(t, "AnimationLoad") : null;
        }
        static bool hookLogged;
        static void Postfix(object __instance) { if (!hookLogged) { hookLogged = true; Plugin.Log.LogInfo("[Uni] UniRegisterHook POSTFIX fired"); } UniversalInject.EnsureRegistered(__instance); }
    }

    [HarmonyPatch]
    internal static class UniRepointHook
    {
        static MethodBase TargetMethod()
        {
            var addon = AccessTools.TypeByName("Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn");
            var animMgr = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return (addon != null && animMgr != null) ? AccessTools.Method(addon, "Load", new[] { animMgr }) : null;
        }
        static void Postfix(object __instance, object __0) { UniversalInject.RepointMatch(__instance, __0); }
    }
}
