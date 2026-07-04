using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;             // provided by the game (mod.io); robust registry parse where JsonUtility no-ops in the game runtime

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
        public string hideMeshes = "";   // comma-separated donor-FRAGMENT name substrings to hide (works for fragment-based extras; a donor's animated skinned sub-parts, e.g. a helicopter rotor, are encoded at pawn-spawn and cannot be hidden this late — pick a rotor-free donor instead)
        public UnityEngine.Vector3 position;  // ANIMATED models: applied as a runtime world offset in the pose hook (z = height/up). Static models bake position into the mesh at Bake time instead, so this is only read for animated entries.
        public int ca, cb, cc, cd;       // ANIMATED models: our baked ClipCollection Amplitude guid (its own clip, e.g. a drone's spinning-prop 'hover'). 0,0,0,0 = static model (no pose override).
        public object clipColl;          // loaded ClipCollection asset
        public int animId = -1;          // resolved animation id of our clip (after it's registered in AnimationManager.Apply)
        public float animDuration = 1f;  // clip duration (s); PawnEntryPose.Time is NORMALIZED (Mathf.Repeat(Time,1) = one loop), so Time = seconds/duration plays it at real speed with every frame
        public int skeletonId = -1;      // runtime AnimationManager skeleton index of our registered skeleton (to match PawnManager.PawnEntry.SkeletonId)
        public int descId = -1;          // runtime PawnDescriptorId of our unit (learned from the correctly-skinned pawn), to spot the wrong-skeleton twin the game spawns for the same unit
        public bool fragsLogged;         // one-shot: dump the donor's fragment mesh names once, so the modder can find hide targets
        public bool repointed;
    }

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

                // PRIMARY: Newtonsoft (the game's own copy) parses each model as an OBJECT, so fields stay with their
                // model — robust to a missing/reordered field on any single model, unlike the index-aligned regex below.
                // UnityEngine.JsonUtility silently returns empty in the game's Mono runtime (works only in the editor),
                // so it's not usable here; Newtonsoft works in-process. Regex is kept as a last-resort fallback.
                try
                {
                    var models = JObject.Parse(text)["models"] as JArray;
                    if (models != null && models.Count > 0)
                    {
                        int A(JToken arr, int k) => (arr is JArray a && k < a.Count) ? (int)a[k] : 0;
                        float Fp(JToken o, string k) => o?[k] != null ? (float)o[k] : 0f;
                        foreach (var m in models)
                        {
                            var s = m["skel"]; var t = m["atlas"]; var c = m["clip"]; var p = m["position"];
                            entries.Add(new ModelEntry
                            {
                                resourceName = (string)m["resourceName"] ?? "", pawnDescription = (string)m["pawnDescription"] ?? "", hideMeshes = (string)m["hideMeshes"] ?? "",
                                sa = A(s, 0), sb = A(s, 1), sc = A(s, 2), sd = A(s, 3),
                                ta = A(t, 0), tb = A(t, 1), tc = A(t, 2), td = A(t, 3),
                                ca = A(c, 0), cb = A(c, 1), cc = A(c, 2), cd = A(c, 3),
                                position = new UnityEngine.Vector3(Fp(p, "x"), Fp(p, "y"), Fp(p, "z")),
                            });
                        }
                        Plugin.Log.LogInfo($"[Uni] parsed {entries.Count} entr(ies) via Newtonsoft [" + string.Join(", ", entries.Select(e => e.resourceName + "->" + e.pawnDescription)) + "]");
                        return;
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[Uni] Newtonsoft parse failed (" + ex.Message + "); using regex fallback"); entries.Clear(); }

                // FALLBACK: field-by-field regex. Each model has exactly one of each field in document order, so the i-th
                // match of each belongs to model i (fragile only if a MIDDLE model omits a field — the object parse above avoids that).
                const string i4 = @"\[\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\]";
                var rn = Regex.Matches(text, "\"resourceName\"\\s*:\\s*\"([^\"]*)\"");
                var pd = Regex.Matches(text, "\"pawnDescription\"\\s*:\\s*\"([^\"]*)\"");
                var hm = Regex.Matches(text, "\"hideMeshes\"\\s*:\\s*\"([^\"]*)\"");
                var sk = Regex.Matches(text, "\"skel\"\\s*:\\s*" + i4);
                var at = Regex.Matches(text, "\"atlas\"\\s*:\\s*" + i4);
                var cl = Regex.Matches(text, "\"clip\"\\s*:\\s*" + i4);   // ClipCollection guid (animated models); absent on static models
                // position object {x,y,z} — JsonUtility writes Vector3 in x,y,z order. Applied as a runtime world offset for animated models.
                var po = Regex.Matches(text, "\"position\"\\s*:\\s*\\{\\s*\"x\"\\s*:\\s*(-?[\\d.eE+]+)\\s*,\\s*\"y\"\\s*:\\s*(-?[\\d.eE+]+)\\s*,\\s*\"z\"\\s*:\\s*(-?[\\d.eE+]+)");
                int G(Match m, int g) => int.TryParse(m.Groups[g].Value, out var r) ? r : 0;
                float F(Match m, int g) => float.TryParse(m.Groups[g].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0f;
                int n = Math.Min(pd.Count, Math.Min(sk.Count, at.Count));
                for (int i = 0; i < n; i++)
                {
                    entries.Add(new ModelEntry
                    {
                        resourceName = i < rn.Count ? rn[i].Groups[1].Value : ("model" + i),
                        pawnDescription = pd[i].Groups[1].Value,
                        hideMeshes = i < hm.Count ? hm[i].Groups[1].Value : "",   // hideMeshes appears once per model in doc order, same as the others
                        sa = G(sk[i], 1), sb = G(sk[i], 2), sc = G(sk[i], 3), sd = G(sk[i], 4),
                        ta = G(at[i], 1), tb = G(at[i], 2), tc = G(at[i], 3), td = G(at[i], 4),
                        ca = i < cl.Count ? G(cl[i], 1) : 0, cb = i < cl.Count ? G(cl[i], 2) : 0, cc = i < cl.Count ? G(cl[i], 3) : 0, cd = i < cl.Count ? G(cl[i], 4) : 0,
                        position = i < po.Count ? new UnityEngine.Vector3(F(po[i], 1), F(po[i], 2), F(po[i], 3)) : UnityEngine.Vector3.zero,
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
                    // isolate each entry: a single bad model (missing asset, reflection miss) must not abort the whole
                    // loop -- that would skip Apply and take down EVERY custom model, not just the broken one.
                    try
                    {
                        if (e.skeleton == null) e.skeleton = LoadSkeleton(e.sa, e.sb, e.sc, e.sd, e.resourceName);
                        if (e.skeleton == null) continue;
                        var sf = AccessTools.Field(e.skeleton.GetType(), "loadingStatus");
                        if (sf != null) sf.SetValue(e.skeleton, Enum.ToObject(sf.FieldType, 0)); // NotLoaded
                        SetMember(e.skeleton, "SkeletonId", -1);
                        reg.Invoke(animMgr, new[] { e.skeleton });
                        n++;
                    }
                    catch (Exception ex) { Plugin.Log.LogError($"[Uni] register '{e.resourceName}' failed (skipped, others continue): " + ex); }
                }
                // inject our ClipCollections (animated models) into loadedAnimationClipCollections BEFORE Apply, so
                // Apply's builder bakes their pose data + assigns each clip an animation id.
                InjectClipCollections(animMgr);
                if (n > 0 || entries.Any(x => x.clipColl != null))
                {
                    var apply = AccessTools.Method(animMgr.GetType(), "Apply", Type.EmptyTypes)
                        ?? animMgr.GetType().GetMethods(BF).FirstOrDefault(m => m.Name == "Apply" && m.GetParameters().Length == 0);
                    apply?.Invoke(animMgr, null);
                }
                // capture each skeleton's runtime SkeletonId (assigned during Apply's GPU build) so the pawn-pose hook
                // can match PawnManager.PawnEntry.SkeletonId; and resolve our clip's animation id for the pose override.
                foreach (var e in entries)
                {
                    if (e.skeleton != null) { try { e.skeletonId = Convert.ToInt32(GetMember(e.skeleton, "SkeletonId")); } catch { } }
                    if (e.clipColl != null) e.animId = ResolveAnimId(animMgr, e);
                }
                registered = true;
                Plugin.Log.LogInfo($"[Uni] registered {n} skeleton(s) + re-Apply'd; " + string.Join(", ", entries.Select(x => $"{x.resourceName}(skel {x.skeletonId}, anim {x.animId})")));
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

                // One-shot diagnostic (BEFORE we swap): dump the DONOR skeleton / mesh-collection sub-meshes, so we can
                // find parts that aren't separate fragments (e.g. a helicopter rotor baked as its own skinned sub-mesh).
                if (!e.fragsLogged)
                {
                    var sk0 = GetMember(addon, "Skeleton"); var mc0 = GetMember(addon, "MeshCollection");
                    Plugin.Log.LogInfo($"[Uni] {e.resourceName} donor Skeleton='{(sk0 as UnityEngine.Object)?.name}' MeshCollection='{(mc0 as UnityEngine.Object)?.name}'");
                    DumpSkinned(sk0, e.resourceName + " donor.Skeleton");
                    if (mc0 != null && !ReferenceEquals(mc0, sk0)) DumpSkinned(mc0, e.resourceName + " donor.MeshCollection");
                    // WIDE NET: the rotor is neither a sub-mesh nor a fragment, so dump every field/array on the addon
                    // and the skeleton to find where that mesh is referenced (or confirm it's engine-procedural).
                    DumpFields(addon, e.resourceName + " addon");
                    DumpFields(sk0, e.resourceName + " skeleton");
                }

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
            if (g == null) { Plugin.Log.LogError($"[Uni] LoadSkeleton '{tag}': Amplitude LoadAsset/TryLoadAsset not resolved (game update?) — skipping this model"); return null; }
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

        // Diagnostic: list a MeshCollection/Skeleton's skinned sub-meshes (names + fx mesh index), to spot baked-in
        // parts like a rotor that aren't separate fragments.
        static void DumpSkinned(object mc, string label)
        {
            try
            {
                if (mc == null) { Plugin.Log.LogInfo($"[Uni] {label}: <null>"); return; }
                var arr = AccessTools.Field(mc.GetType(), "skinnedMeshInfos")?.GetValue(mc) as Array;
                if (arr == null) { Plugin.Log.LogInfo($"[Uni] {label}: no skinnedMeshInfos field"); return; }
                Plugin.Log.LogInfo($"[Uni] {label}: {arr.Length} skinned sub-mesh(es)");
                for (int i = 0; i < arr.Length; i++)
                {
                    var it = arr.GetValue(i);
                    var nm = GetMember(it, "MeshName");
                    var mi = GetMember(it, "MeshIndex");
                    Plugin.Log.LogInfo($"[Uni]    {label}[{i}] mesh='{nm}' meshIndex={mi}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] DumpSkinned {label}: " + ex.Message); }
        }

        // Wide diagnostic: shallow-dump every field of an object — arrays (length + any MeshName/name elements),
        // Unity object refs (name), strings, primitives — to find a mesh/rotor reference hiding outside the usual slots.
        static void DumpFields(object o, string label)
        {
            try
            {
                if (o == null) { Plugin.Log.LogInfo($"[Uni] {label}: <null>"); return; }
                var t = o.GetType();
                Plugin.Log.LogInfo($"[Uni] === {label} ({t.Name}) fields ===");
                for (var bt = t; bt != null && bt != typeof(object); bt = bt.BaseType)
                    foreach (var f in bt.GetFields(BF | BindingFlags.DeclaredOnly))
                    {
                        object v = null; try { v = f.GetValue(o); } catch { }
                        string disp;
                        if (v == null) disp = "null";
                        else if (v is Array a)
                        {
                            var parts = new List<string>();
                            for (int i = 0; i < a.Length && i < 8; i++)
                            {
                                var el = a.GetValue(i);
                                var nm = GetMember(el, "MeshName") ?? GetMember(el, "meshName") ?? GetMember(el, "Name") ?? (el as UnityEngine.Object)?.name;
                                parts.Add(nm?.ToString() ?? el?.GetType().Name ?? "null");
                            }
                            disp = $"[{a.Length}] {{{string.Join(", ", parts)}}}";
                        }
                        else if (v is UnityEngine.Object uo) disp = "obj:" + uo.name;
                        else if (v is string s) disp = "\"" + s + "\"";
                        else if (f.FieldType.IsPrimitive || f.FieldType.IsEnum) disp = v.ToString();
                        else disp = v.GetType().Name;
                        // only log the interesting ones to keep the log readable
                        string ln = f.Name.ToLowerInvariant();
                        if (v is Array || v is UnityEngine.Object || v is string || ln.Contains("mesh") || ln.Contains("rotor") || ln.Contains("fx") || ln.Contains("bone") || ln.Contains("attach") || ln.Contains("sub") || ln.Contains("frag"))
                            Plugin.Log.LogInfo($"[Uni]    {label}.{f.Name} ({f.FieldType.Name}) = {disp}");
                    }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] DumpFields {label}: " + ex.Message); }
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
                var encField = AccessTools.Field(fragType, "EncodedMeshAndVisualParticleCount"); // 0 => fragment renders nothing
                var load = AccessTools.Method(fragType, "Load");
                var hides = (e?.hideMeshes ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                for (int i = 0; i < frags.Length; i++)
                {
                    var item = frags.GetValue(i);
                    if (item == null) continue;

                    // Dump the donor's fragment mesh names once, so the modder can see what to hide (e.g. a rotor).
                    var fragMesh = mnField?.GetValue(item) as string;
                    if (e != null && !e.fragsLogged) Plugin.Log.LogInfo($"[Uni] {e.resourceName} donor fragment[{i}] mesh='{fragMesh}'");

                    // HIDE donor fragments whose mesh name matches hideMeshes (kept separate per model: a drone hides the
                    // helicopter rotor; a custom helicopter leaves hideMeshes empty and borrows that same spinning rotor).
                    if (!string.IsNullOrEmpty(fragMesh) && hides.Any(h => fragMesh.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        encField?.SetValue(item, (uint)0);   // force EncodedMeshAndVisualParticleCount = 0 => invisible
                        frags.SetValue(item, i);
                        if (e != null && !e.fragsLogged) Plugin.Log.LogInfo($"[Uni] {e.resourceName} HID donor fragment[{i}] mesh='{fragMesh}'");
                        continue;
                    }

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
                if (e != null) e.fragsLogged = true;   // dumped the donor fragment names once; don't spam on every load
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

        // ---- animated models: register our ClipCollection + override the pawn's pose to play it ----

        static object LoadClipCollection(int a, int b, int c, int d, string tag)
        {
            try
            {
                var guid = MakeGuid(a, b, c, d);
                var ccType = AccessTools.TypeByName("Amplitude.Mercury.Animation.ClipCollection");
                var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                if (guid == null || ccType == null || adb == null) return null;
                var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
                var g = load?.MakeGenericMethod(ccType);
                if (g == null) { Plugin.Log.LogError($"[Uni] loadClip '{tag}': Amplitude LoadAsset/TryLoadAsset not resolved (game update?)"); return null; }
                var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
                var cc = g.Invoke(null, args);
                Plugin.Log.LogInfo($"[Uni] loaded clipCollection '{tag}': " + ((cc as UnityEngine.Object)?.name ?? "NULL (rebuild mod?)"));
                return cc;
            }
            catch (Exception e) { Plugin.Log.LogError("[Uni] loadClip: " + e); return null; }
        }

        // Append each animated model's ClipCollection to AnimationManager.loadedAnimationClipCollections so Apply's
        // builder bakes its pose data + assigns it an animation id. Idempotent.
        static void InjectClipCollections(object animMgr)
        {
            try
            {
                var field = AccessTools.Field(animMgr.GetType(), "loadedAnimationClipCollections");
                var ccType = AccessTools.TypeByName("Amplitude.Mercury.Animation.ClipCollection");
                if (field == null || ccType == null) { Plugin.Log.LogWarning("[Uni] clipCollection field/type not found"); return; }
                foreach (var e in entries)
                {
                    if (e.ca == 0 && e.cb == 0 && e.cc == 0 && e.cd == 0) continue;
                    if (e.clipColl == null) e.clipColl = LoadClipCollection(e.ca, e.cb, e.cc, e.cd, e.resourceName);
                    if (e.clipColl == null) continue;
                    try { AccessTools.Method(e.clipColl.GetType(), "Load", Type.EmptyTypes)?.Invoke(e.clipColl, null); } catch { }
                    var arr = field.GetValue(animMgr) as Array;
                    bool present = false;
                    if (arr != null) foreach (var c in arr) if (ReferenceEquals(c, e.clipColl)) { present = true; break; }
                    if (present) continue;
                    int len = arr?.Length ?? 0;
                    var narr = Array.CreateInstance(ccType, len + 1);
                    if (arr != null) Array.Copy(arr, narr, len);
                    narr.SetValue(e.clipColl, len);
                    field.SetValue(animMgr, narr);
                    Plugin.Log.LogInfo($"[Uni] injected clipCollection '{e.resourceName}' at [{len}]");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] InjectClipCollections: " + ex); }
        }

        // After Apply built the animation buffer, resolve our clip's animation id via GetAnimationId(clip guid).
        static int ResolveAnimId(object animMgr, ModelEntry e)
        {
            try
            {
                var clips = AccessTools.Field(e.clipColl.GetType(), "animationClipEntries")?.GetValue(e.clipColl) as Array;
                if (clips == null || clips.Length == 0) return -1;
                var clipGuid = GetMember(clips.GetValue(0), "UnityAnimationClip");
                if (clipGuid == null) return -1;
                var getId = AccessTools.Method(animMgr.GetType(), "GetAnimationId", new[] { clipGuid.GetType() });
                int id = getId != null ? Convert.ToInt32(getId.Invoke(animMgr, new[] { clipGuid })) : -1;
                if (id >= 0)   // capture the clip's real duration so the pose hook can normalize Time (play at real speed)
                {
                    var getDur = AccessTools.Method(animMgr.GetType(), "GetAnimationDuration", new[] { typeof(int) });
                    if (getDur != null)
                    {
                        float d = Convert.ToSingle(getDur.Invoke(animMgr, new object[] { id }));
                        if (d > 0.001f) { e.animDuration = d; Plugin.Log.LogInfo($"[Uni] clip animId {id} duration {d:0.###}s"); }
                    }
                }
                return id;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] ResolveAnimId: " + ex.Message); return -1; }
        }

        static bool? anyAnimated;        // cached early-out: skip the per-pawn hook if no model is animated
        static bool poseHookLogged, rescueLogged, posLogged;

        static ModelEntry AnimEntryFor(int skeletonId)
        {
            if (entries == null || skeletonId < 0) return null;
            foreach (var e in entries) if (e.animId >= 0 && e.skeletonId == skeletonId) return e;
            return null;
        }

        // The game just wrote pawnEntries[pawnCount-1]. If it's one of our animated models, force Pose0 to OUR clip
        // (weight 1, advancing time) and zero the other poses — so it plays our animation instead of the donor's.
        // sumWeight stays 1 (never zero all — that divides by zero => NaN => invisible).
        internal static void OnPawnAdded(object pawnManager)
        {
            try
            {
                if (anyAnimated == null) anyAnimated = entries != null && entries.Any(x => x.ca != 0 || x.cb != 0 || x.cc != 0 || x.cd != 0);
                if (anyAnimated != true || !Plugin.UniversalInjectOn.Value) return;
                var pawnEntries = GetMember(pawnManager, "pawnEntries") as Array;
                if (pawnEntries == null) return;
                int pawnCount = Convert.ToInt32(GetMember(pawnManager, "pawnCount"));
                if (pawnCount <= 0 || pawnCount > pawnEntries.Length) return;
                int idx = pawnCount - 1;
                var entry = pawnEntries.GetValue(idx);                 // boxed PawnEntry (struct)
                int skelId = Convert.ToInt32(GetMember(entry, "SkeletonId"));
                int descId = Convert.ToInt32(GetMember(entry, "PawnDescriptorId"));

                // Match our animated unit by our skeleton id (the correct pawn) OR by our unit's DESCRIPTOR — the game
                // can spawn a unit's pawn on a different vanilla skeleton, which then draws our mesh mis-skinned. Learn
                // the descriptor from the first correct pawn, then rescue any wrong-skeleton pawn.
                var e = AnimEntryFor(skelId);
                if (e != null) e.descId = descId;                      // learn our unit's descriptor
                else if (descId >= 0) e = entries.FirstOrDefault(x => x.animId >= 0 && x.descId >= 0 && x.descId == descId);
                if (e == null) return;

                // FORCE our skeleton so this pawn skins by OUR rig (fixes a unit that spawned on a vanilla skeleton).
                if (skelId != e.skeletonId)
                {
                    SetMember(entry, "SkeletonId", e.skeletonId);
                    if (!rescueLogged) { rescueLogged = true; Plugin.Log.LogInfo($"[Uni] rescued wrong-skeleton pawn: skelId {skelId} -> {e.skeletonId} (descId {descId})"); }
                }

                // Play OUR clip on Pose0 (weight 1, advancing time); zero the others. Never all-zero (=> NaN => invisible).
                var pose0 = GetMember(entry, "Pose0");                 // boxed PawnEntryPose (struct)
                SetMember(pose0, "AnimationId", (uint)e.animId);
                SetMember(pose0, "Weight", 1f);
                // PawnEntryPose.Time is NORMALIZED (sampler does Mathf.Repeat(Time,1) = one loop). Divide by the clip
                // duration so it plays at REAL speed and hits every frame; raw Time.time = duration× too fast + frame-skipping.
                SetMember(pose0, "Time", UnityEngine.Time.time / (e.animDuration > 0.001f ? e.animDuration : 1f));
                SetMember(entry, "Pose0", pose0);
                for (int i = 1; i < 9; i++)
                {
                    var pose = GetMember(entry, "Pose" + i);
                    if (pose == null) continue;
                    SetMember(pose, "Weight", 0f);
                    SetMember(entry, "Pose" + i, pose);
                }
                // Runtime position offset. Static models bake position into the mesh; the animated path can't, so we
                // apply it to the pawn's world ObjectSpace here. z = height -> world up (Y); x/y -> world plane. Re-applied
                // each frame on the game's fresh world position, so it never accumulates. Logged once to confirm the axis.
                if (e.position != UnityEngine.Vector3.zero)
                {
                    var os = GetMember(entry, "ObjectSpace");                        // boxed TRS
                    var tr = (UnityEngine.Vector3)GetMember(os, "Translation");
                    if (!posLogged) { posLogged = true; Plugin.Log.LogInfo($"[Uni] {e.resourceName} pawn world pos {tr} + registry position {e.position} (z->up Y)"); }
                    tr.x += e.position.x; tr.y += e.position.z; tr.z += e.position.y;   // registry z (height) -> world Y (up)
                    SetMember(os, "Translation", tr);
                    SetMember(entry, "ObjectSpace", os);
                }
                pawnEntries.SetValue(entry, idx);
                if (!poseHookLogged) { poseHookLogged = true; Plugin.Log.LogInfo($"[Uni] pose hook: '{e.resourceName}' -> Pose0 anim {e.animId} (skelId {skelId} -> {e.skeletonId}, desc {descId})"); }
            }
            catch { }
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
                if (g == null) { Plugin.Log.LogError($"[Uni] loadAtlas '{tag}': Amplitude LoadAsset/TryLoadAsset not resolved (game update?)"); return null; }
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

    // Per-pawn-per-frame: after the game writes a PawnEntry, let us override its pose for our animated models.
    [HarmonyPatch]
    internal static class UniPawnPoseHook
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.PawnManager");
            return t != null ? AccessTools.Method(t, "AddPawnEntry") : null;
        }
        static void Postfix(object __instance) => UniversalInject.OnPawnAdded(__instance);
    }
}
