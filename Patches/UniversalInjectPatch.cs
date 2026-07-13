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
    // Generic, registry-driven model injector — the runtime half of the Universal Baker. Reads enc_models.json from
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
        public float scale = 1f;              // ANIMATED models: runtime multiplier on the pawn's ObjectSpace.Scale (default 1 = unchanged). Lets us fix an animated model baked at the wrong scale WITHOUT a re-bake (e.g. the howitzer's 100x FBX unit-conversion oversize -> set 0.01). Config-only field; absent = 1.
        public int ca, cb, cc, cd;       // ANIMATED models: our baked ClipCollection Amplitude guid (its own clip, e.g. a drone's spinning-prop 'hover'). 0,0,0,0 = static model (no pose override).
        public object clipColl;          // loaded ClipCollection asset
        public int animId = -1;          // resolved animation id of our clip (after it's registered in AnimationManager.Apply)
        public float animDuration = 1f;  // clip duration (s); PawnEntryPose.Time is NORMALIZED (Mathf.Repeat(Time,1) = one loop), so Time = seconds/duration plays it at real speed with every frame
        public int skeletonId = -1;      // runtime AnimationManager skeleton index of our registered skeleton (to match PawnManager.PawnEntry.SkeletonId)
        public int descId = -1;          // runtime PawnDescriptorId of our unit (learned from the correctly-skinned pawn), to spot the wrong-skeleton twin the game spawns for the same unit
        public bool fragsLogged;         // one-shot: dump the donor's fragment mesh names once, so the modder can find hide targets
        public bool repointed;
        public bool respawnAfterLoad;    // FIX for the save-load first-instance rotor race: when true, the plugin re-runs the game's own PresentationUnit.UpdatePawns (ReleasePawns+InstantiatePawns) on this model's units ~3s after load, so the first instance's borrowed donor rotor is rebuilt correctly. Set ONLY for models that borrow a donor's animated sub-part (e.g. the helicopter's rotor); harmless-but-pointless flicker otherwise, so default off.
        public bool freezeDonorAnim;     // FREEZE the donor's idle/move animation: a STATIC borrowed mesh inherits the donor's pose bob (e.g. a drone donor's hover wiggle looks wrong on a large airship). When true, the pose hook pins every pawn pose's Time to 0 each frame so the donor animation can't advance — the mesh holds rigid while the pawn still glides tile-to-tile. Static models only (animated models drive their own clip).
        public bool fireOnAttack;        // ANIMATED: play the clip ONCE when the unit attacks (ArtilleryStrikeStarted), resting at frame 0 otherwise — instead of the default continuous loop (a drone's spinning prop). Set for a howitzer's barrel-elevation-on-fire. See docs/Firing-On-Attack.md.
        // PER-INSTANCE fire, so only the howitzer that actually bombarded animates (not every howitzer of the type):
        public readonly System.Collections.Concurrent.ConcurrentQueue<long> fireGuidQueue = new System.Collections.Concurrent.ConcurrentQueue<long>();  // SIM thread enqueues the firing unit's SimulationEntityGUID; Plugin.Update (main thread) drains it (no Unity access on the sim thread).
        public readonly List<FireInstance> activeFires = new List<FireInstance>();  // MAIN/render thread only (locked): each firing pawn's render position + start time; the pose hook plays the clip on the pawn nearest an active fire.
        // DEPLOY-ON-STOP (a HELD state, not a one-shot): the clip rests at the DEPLOYED pose by default and snaps to the
        // UNDEPLOYED pose while the unit is moving. Pure function of "is this pawn's unit moving right now" — no state machine,
        // AI/concurrency-safe. Plugin.Update polls PresentationUnit.IsAnyPawnMoving and records the moving pawns' positions.
        public bool deployOnStop;             // hold the deployed pose when idle, undeploy (frame 0) while moving
        public float deployPoseTime = 1f;     // normalized clip time of the DEPLOYED pose (1 = a real deploy clip's end; 0.5 = the barrel-fire clip's raised plateau, used to prove the plumbing without a deploy clip)
        public float deploySpeed = 1f;        // multiplier on the gradual-deploy ramp speed (1 = the clip's authored speed; 2 = twice as fast). Only affects the forward deploy-on-stop; folding on move is always instant.
        public float recoilSpeed = 1f;        // multiplier on the recoil-on-fire (kickback) playback speed (1 = the tail's authored speed; 3 = the kick plays 3x faster). Only affects deployOnStop+fireOnAttack models.
        // GRADUAL deploy: instead of snapping, ramp each unit's pose time toward its target (0 while moving, deployPoseTime
        // when stopped) at the clip's authored speed, so the legs visibly spread/fold. Progress is per-unit (stateful) so it
        // survives across polls and units entering/leaving view; the pose hook reads the ramped value matched by position.
        public readonly Dictionary<long, float> deployProgress = new Dictionary<long, float>();  // MAIN thread: unit GUID -> current normalized pose time (ramps toward target)
        public readonly Dictionary<long, UnityEngine.Vector3> deployLastPos = new Dictionary<long, UnityEngine.Vector3>();  // MAIN thread: unit GUID -> last render position; movement = the position actually changed (instant fold, settle-immune)
        public readonly List<DeploySample> deploySamples = new List<DeploySample>();             // MAIN thread only (locked): each pawn's render position + its unit's current (ramped) pose time; the pose hook holds that pose on the nearest pawn.
        public float deployLastPoll;          // Time.time of the last deploy poll, for a framerate-independent ramp step
    }

    // One in-flight one-shot: the world position of a pawn that just fired + when it started. The pose hook matches a
    // pawn to the nearest active fire by ObjectSpace position (both are Unity render coords), so only the firer animates.
    internal struct FireInstance { public UnityEngine.Vector3 pos; public float startTime; }
    // A pawn's render position + the (ramped) normalized pose time its unit should currently hold, for the gradual deploy.
    internal struct DeploySample { public UnityEngine.Vector3 pos; public float poseTime; }

    internal static class UniversalInject
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static List<ModelEntry> entries;
        static bool loaded, registered, repointActiveLogged, stLogged;
        static int loadAttempts;   // failed-load counter: latch `loaded` only after a success or a few tries, so a TRANSIENT read/parse error (AV scan, sharing violation at startup) retries instead of disabling injection for the whole session
        static UnityEngine.Texture2D _flatN, _white, _black, _grey;   // neutral overlay maps (kill the host's detail/camo)

        static void LoadRegistry()
        {
            if (loaded) return;
            entries = new List<ModelEntry>();
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "enc_models.json");
                if (!File.Exists(path)) { Plugin.Log.LogInfo("[Uni] no registry at " + path); loaded = true; return; }
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
                                scale = m["scale"] != null ? (float)m["scale"] : 1f,
                                respawnAfterLoad = (bool?)m["respawnAfterLoad"] ?? false,
                                freezeDonorAnim = (bool?)m["freezeDonorAnim"] ?? false,
                                fireOnAttack = (bool?)m["fireOnAttack"] ?? false,
                                deployOnStop = (bool?)m["deployOnStop"] ?? false,
                                deployPoseTime = m["deployPoseTime"] != null ? (float)m["deployPoseTime"] : 1f,
                                deploySpeed = m["deploySpeed"] != null ? (float)m["deploySpeed"] : 1f,
                                recoilSpeed = m["recoilSpeed"] != null ? (float)m["recoilSpeed"] : 1f,
                            });
                        }
                        Plugin.Log.LogInfo($"[Uni] parsed {entries.Count} entr(ies) via Newtonsoft [" + string.Join(", ", entries.Select(e => e.resourceName + "->" + e.pawnDescription)) + "]");
                        loaded = true;   // latch ONLY on a successful parse (not at the top) so a transient read/parse error retries
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
                var ra = Regex.Matches(text, "\"respawnAfterLoad\"\\s*:\\s*(true|false)");   // parity with the Newtonsoft path (line ~77) — else the first-instance rotor fix silently defaults off here
                var fz = Regex.Matches(text, "\"freezeDonorAnim\"\\s*:\\s*(true|false)");   // parity with the Newtonsoft path — else the donor-animation freeze silently defaults off here
                var foa = Regex.Matches(text, "\"fireOnAttack\"\\s*:\\s*(true|false)");     // parity: play the clip once on attack vs loop
                var dos = Regex.Matches(text, "\"deployOnStop\"\\s*:\\s*(true|false)");     // parity: hold deployed when idle, undeploy while moving
                var dpt = Regex.Matches(text, "\"deployPoseTime\"\\s*:\\s*(-?[\\d.eE+]+)"); // parity: normalized clip time of the deployed pose (default 1)
                var dsp = Regex.Matches(text, "\"deploySpeed\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: gradual-deploy ramp speed multiplier (default 1)
                var rsp = Regex.Matches(text, "\"recoilSpeed\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: recoil-on-fire playback speed multiplier (default 1)
                var sc = Regex.Matches(text, "\"scale\"\\s*:\\s*(-?[\\d.eE+]+)");           // runtime ObjectSpace scale multiplier (default 1)
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
                        respawnAfterLoad = i < ra.Count && ra[i].Groups[1].Value == "true",
                        freezeDonorAnim = i < fz.Count && fz[i].Groups[1].Value == "true",
                        fireOnAttack = i < foa.Count && foa[i].Groups[1].Value == "true",
                        deployOnStop = i < dos.Count && dos[i].Groups[1].Value == "true",
                        deployPoseTime = i < dpt.Count && float.TryParse(dpt[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _dpt) ? _dpt : 1f,
                        deploySpeed = i < dsp.Count && float.TryParse(dsp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _dsp) ? _dsp : 1f,
                        recoilSpeed = i < rsp.Count && float.TryParse(rsp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _rsp) ? _rsp : 1f,
                        scale = i < sc.Count && float.TryParse(sc[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sc) ? _sc : 1f,
                    });
                }
                loaded = true;   // regex fallback also succeeded — latch so we don't re-parse every pawn
                Plugin.Log.LogInfo($"[Uni] read {text.Length} chars; parsed {entries.Count} entr(ies) [" + string.Join(", ", entries.Select(e => e.resourceName + "->" + e.pawnDescription)) + "]");
            }
            catch (Exception e)
            {
                // Do NOT latch `loaded` on a failure — a transient hiccup (AV scan, sharing violation while the game is
                // still flushing the file) would otherwise disable ALL injection for the session. Retry on the next few
                // pawn loads; give up (latch) only after 3 tries so a genuinely broken file doesn't re-parse forever.
                entries = new List<ModelEntry>();
                if (++loadAttempts >= 3) { loaded = true; Plugin.Log.LogError("[Uni] registry load failed 3x, giving up for this session: " + e); }
                else Plugin.Log.LogWarning($"[Uni] registry load failed (attempt {loadAttempts}/3), will retry on next pawn: " + e.Message);
            }
        }

        // FIRING-ON-ATTACK: match a firing unit's UnitDefinition text (e.g. "LandUnit_Era6_Common_TowedGunHowitzers …")
        // to one of our injected models by the shared core of its pawnDescription
        // ("Era6_Common_TowedGunHowitzers_01" -> core "Era6_Common_TowedGunHowitzers"). Returns the entry, or null.
        internal static ModelEntry FindEntryForUnitDefinition(string unitDefText)
        {
            if (entries == null || string.IsNullOrEmpty(unitDefText)) return null;
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.pawnDescription)) continue;
                var core = Regex.Replace(e.pawnDescription, "_\\d+$", "");   // drop the trailing _NN instance suffix
                if (core.Length > 4 && unitDefText.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0) return e;
            }
            return null;
        }

        // register every skeleton before Apply() builds GPU buffers (AnimationLoad postfix)
        internal static void EnsureRegistered(object animMgr)
        {
            if (registered) return;
            if (animMgr == null) { Plugin.Log.LogWarning("[Uni] EnsureRegistered: animMgr null"); return; }
            Plugin.Log.LogInfo("[Uni] EnsureRegistered fired");
            LoadRegistry();
            // Latch on empty ONLY if the load actually succeeded (`loaded` — a genuinely empty/absent registry).
            // While a transient load failure is still retrying (`loaded` false), leave `registered` unlatched too, or
            // the retry would succeed later but registration would never run again — injection dead for the session.
            if (entries.Count == 0) { registered = loaded; return; }
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
        static bool? anyFreeze;          // cached early-out: skip the per-pawn hook if no model wants its donor animation frozen
        static bool rescueLogged, posLogged, poseErrLogged, scaleLogged;
        static HashSet<string> poseHookSeen;   // dump the pose-hook + runtime transform once PER MODEL (so the howitzer logs even if the drone spawns first)
        static HashSet<int> freezeLogSkels;   // distinct skeleton ids we've logged a freeze for (so a second-instance "twin via descriptor" shows up in the log without spamming)
        static float recoilLogStart = -1f;    // diagnostic: log once per fire when the deploy+recoil overlay actually sweeps

        // An entry the per-pawn hook acts on: an ANIMATED model (plays its own baked clip) OR a STATIC model that freezes
        // the donor's animation. Both share the same match (our skeleton id → learned descriptor) + skeleton force; only the
        // pose manipulation differs. Kept as one predicate so the two paths can't drift apart.
        static bool Hooked(ModelEntry x) => x.animId >= 0 || x.freezeDonorAnim;
        static ModelEntry HookedEntryFor(int skeletonId)
        {
            if (entries == null || skeletonId < 0) return null;
            foreach (var e in entries) if (Hooked(e) && e.skeletonId == skeletonId) return e;
            return null;
        }

        // Per-pawn state read once at the top of the hook and threaded through the behavior handlers. `entry` is the boxed
        // PawnEntry struct — every SetMember mutates that one box, and the handler writes it back via pawnEntries.SetValue.
        struct PawnCtx { public Array pawnEntries; public int idx; public object entry; public int skelId; public int descId; }

        // The game just wrote pawnEntries[pawnCount-1]. Match it to one of our models and hand off to the behavior that model
        // wants: FREEZE (pin the donor clip to frame 0) or an ANIMATED pose whose time is driven by loop / fire-once / deploy.
        // Each behavior is its own method below, so adding a new one is a new handler — not another branch on this hot path.
        internal static void OnPawnAdded(object pawnManager)
        {
            try
            {
                if (anyAnimated == null) anyAnimated = entries != null && entries.Any(x => x.ca != 0 || x.cb != 0 || x.cc != 0 || x.cd != 0);
                if (anyFreeze == null) anyFreeze = entries != null && entries.Any(x => x.freezeDonorAnim);
                if ((anyAnimated != true && anyFreeze != true) || !Plugin.UniversalInjectOn.Value) return;
                if (!TryReadLastPawn(pawnManager, out var ctx)) return;

                // Match this pawn to one of our entries (animated OR freeze-static) by OUR baked skeleton id (the correctly
                // skinned pawn), else by the descriptor learned from that first correct pawn. The game spawns a unit's LATER
                // instances on a different vanilla skeleton; without the descriptor fallback only the first instance is
                // handled and the rest slip through (animating / rocking on the donor's rig).
                var e = HookedEntryFor(ctx.skelId);
                if (e != null) e.descId = ctx.descId;                  // learn our unit's descriptor from the correct pawn
                else if (ctx.descId >= 0) e = entries.FirstOrDefault(x => Hooked(x) && x.descId >= 0 && x.descId == ctx.descId);
                if (e == null) return;

                ForceOurSkeleton(ctx, e);

                // FREEZE (static): no clip of our own — pin the donor pose to frame 0 and stop. ANIMATED: play our clip on Pose0.
                if (e.freezeDonorAnim && e.animId < 0) ApplyFreeze(ctx, e);
                else ApplyAnimatedPose(ctx, e);
            }
            // one-shot log: a bare catch here hid member renames after a game update (models just stopped animating, no clue why).
            catch (Exception ex) { if (!poseErrLogged) { poseErrLogged = true; Plugin.Log.LogError("[Uni] OnPawnAdded (pose hook disabled this pawn): " + ex); } }
        }

        // Read the just-written PawnEntry (pawnCount-1) + its skeleton/descriptor ids, or false if there's nothing to act on.
        static bool TryReadLastPawn(object pawnManager, out PawnCtx ctx)
        {
            ctx = default;
            var pawnEntries = GetMember(pawnManager, "pawnEntries") as Array;
            if (pawnEntries == null) return false;
            int pawnCount = Convert.ToInt32(GetMember(pawnManager, "pawnCount"));
            if (pawnCount <= 0 || pawnCount > pawnEntries.Length) return false;
            int idx = pawnCount - 1;
            var entry = pawnEntries.GetValue(idx);                     // boxed PawnEntry (struct)
            ctx = new PawnCtx
            {
                pawnEntries = pawnEntries, idx = idx, entry = entry,
                skelId = Convert.ToInt32(GetMember(entry, "SkeletonId")),
                descId = Convert.ToInt32(GetMember(entry, "PawnDescriptorId")),
            };
            return true;
        }

        // FORCE our skeleton so this pawn skins by OUR rig. A LATER instance the game spawned on a vanilla skeleton would
        // otherwise draw mis-skinned (animated) or WARP when we pin a foreign skeleton's frame 0 (freeze — the vertical,
        // "shape-shifting" airship). Shared by both paths, so every instance ends up on our skeleton.
        static void ForceOurSkeleton(PawnCtx ctx, ModelEntry e)
        {
            if (ctx.skelId == e.skeletonId) return;
            SetMember(ctx.entry, "SkeletonId", e.skeletonId);
            if (!rescueLogged) { rescueLogged = true; Plugin.Log.LogInfo($"[Uni] rescued wrong-skeleton pawn: skelId {ctx.skelId} -> {e.skeletonId} (descId {ctx.descId})"); }
        }

        // FREEZE (static): pin every pose's Time to frame 0 so the donor clip can't advance — the borrowed mesh holds rigid
        // instead of inheriting the donor's hover/drive bob. Weights untouched (keep the pose SHAPE, stop the MOTION); the
        // pawn still glides tile-to-tile (transform-driven, not in the pose). Re-applied every frame (per pawn-add), so it holds.
        static void ApplyFreeze(PawnCtx ctx, ModelEntry e)
        {
            for (int i = 0; i < 9; i++)
            {
                var pose = GetMember(ctx.entry, "Pose" + i);
                if (pose == null) continue;
                SetMember(pose, "Time", 0f);
                SetMember(ctx.entry, "Pose" + i, pose);
            }
            ctx.pawnEntries.SetValue(ctx.entry, ctx.idx);
            if (freezeLogSkels == null) freezeLogSkels = new HashSet<int>();
            if (freezeLogSkels.Add(ctx.skelId) && freezeLogSkels.Count <= 6)
                Plugin.Log.LogInfo($"[Uni] freeze: '{e.resourceName}' pinned (skelId {ctx.skelId} -> {e.skeletonId}, descId {ctx.descId})");
        }

        // ANIMATED: play OUR clip on Pose0 (weight 1, advancing time); zero the others (never all-zero => NaN => invisible),
        // clear the aim layer, and apply the runtime position/scale. The pose Time comes from the model's behavior below.
        static void ApplyAnimatedPose(PawnCtx ctx, ModelEntry e)
        {
            var entry = ctx.entry;
            var pose0 = GetMember(entry, "Pose0");                     // boxed PawnEntryPose (struct)
            SetMember(pose0, "AnimationId", (uint)e.animId);
            SetMember(pose0, "Weight", 1f);
            // PawnEntryPose.Time is NORMALIZED (sampler does Mathf.Repeat(Time,1) = one loop). ComputePoseTime divides by the
            // clip duration so it plays at REAL speed and hits every frame; raw Time.time = duration× too fast + frame-skipping.
            float dur = e.animDuration > 0.001f ? e.animDuration : 1f;
            SetMember(pose0, "Time", ComputePoseTime(e, entry, dur));
            SetMember(entry, "Pose0", pose0);
            for (int i = 1; i < 9; i++)
            {
                var pose = GetMember(entry, "Pose" + i);
                if (pose == null) continue;
                SetMember(pose, "Weight", 0f);
                SetMember(entry, "Pose" + i, pose);
            }
            ClearAimLayer(entry);
            ApplyPositionOffset(e, entry);
            ApplyScale(e, entry);
            ctx.pawnEntries.SetValue(entry, ctx.idx);
            LogPoseHookOnce(ctx, e, pose0);
        }

        // The normalized pose time for one animated pawn, per the model's behavior: continuous loop (a spinning prop),
        // fire-once (rest at 0, one pass when this instance bombards), or deploy-on-stop (+ recoil overlay).
        static float ComputePoseTime(ModelEntry e, object entry, float dur)
        {
            if (e.deployOnStop) return DeployPoseTime(e, entry, dur);
            if (e.fireOnAttack) return FireOncePoseTime(e, entry, dur);
            return UnityEngine.Time.time / dur;                        // default: continuous loop (a drone's spinning prop)
        }

        // DEPLOY-ON-STOP (gradual): hold the pose time Plugin.Update ramped for THIS pawn's unit — the deploy clip plays
        // forward (legs spread) after the unit stops and rewinds (fold) while it moves. Match by nearest recorded pawn
        // position (Unity render coords); default deployPoseTime if unmatched (idle -> deployed).
        static float DeployPoseTime(ModelEntry e, object entry, float dur)
        {
            var osD = GetMember(entry, "ObjectSpace");
            UnityEngine.Vector3 dpos = (UnityEngine.Vector3)GetMember(osD, "Translation");
            float poseTime = e.deployPoseTime;
            float bestSqD = 3f * 3f;
            lock (e.deploySamples)
                for (int i = 0; i < e.deploySamples.Count; i++)
                {
                    float d = (e.deploySamples[i].pos - dpos).sqrMagnitude;
                    if (d < bestSqD) { bestSqD = d; poseTime = e.deploySamples[i].poseTime; }
                }
            // RECOIL-ON-FIRE overlay: when this howitzer is DEPLOYED (held near deployPoseTime) AND it just fired, sweep the
            // pose time up through the recoil tail once. The clip's tail after deployPoseTime is the extracted kickback.
            if (e.fireOnAttack && poseTime >= e.deployPoseTime * 0.9f) poseTime = RecoilOverlay(e, dpos, dur, poseTime);
            return poseTime;
        }

        // Sweep the pose time up through the RECOIL TAIL [deployPoseTime, ~1) once from this pawn's nearest active fire, then
        // fall back to the deployed hold. Same per-instance fire match as fire-once (nearest active fire by render position).
        static float RecoilOverlay(ModelEntry e, UnityEngine.Vector3 dpos, float dur, float poseTime)
        {
            float bestSqF = 4f * 4f, bestStartF = -1f;
            lock (e.activeFires)
                for (int i = 0; i < e.activeFires.Count; i++)
                {
                    float d = (e.activeFires[i].pos - dpos).sqrMagnitude;
                    if (d < bestSqF) { bestSqF = d; bestStartF = e.activeFires[i].startTime; }
                }
            if (bestStartF < 0f) return poseTime;
            const float recoilMax = 0.999f;                            // stay below 1.0 (Mathf.Repeat wraps 1.0 -> frame 0 = folded)
            float rspd = e.recoilSpeed > 0f ? e.recoilSpeed : 1f;
            float recoilDur = dur * (recoilMax - e.deployPoseTime) / rspd;   // tail duration at authored speed, sped up by recoilSpeed
            float elapsedF = UnityEngine.Time.time - bestStartF;
            if (recoilDur > 0.0001f && elapsedF < recoilDur)
            {
                poseTime = e.deployPoseTime + (elapsedF / recoilDur) * (recoilMax - e.deployPoseTime);
                if (recoilLogStart != bestStartF)
                { recoilLogStart = bestStartF; Plugin.Log.LogInfo($"[Deploy-Fire] '{e.resourceName}' RECOIL sweep (dur={recoilDur:0.00}s, poseTime {e.deployPoseTime:0.00}->{recoilMax}, matchDist={UnityEngine.Mathf.Sqrt(bestSqF):0.0}u)"); }
            }
            return poseTime;
        }

        // FIRE-ONCE, PER-INSTANCE: rest at frame 0 unless THIS pawn is the one that bombarded. Match this pawn to the nearest
        // active fire by ObjectSpace position (both Unity render coords) and play one 0->1 pass from that fire's start time.
        // Only the firer animates — every other howitzer of the type stays at rest.
        static float FireOncePoseTime(ModelEntry e, object entry, float dur)
        {
            float poseTime = 0f;
            var osT = GetMember(entry, "ObjectSpace");
            UnityEngine.Vector3 tpos = (UnityEngine.Vector3)GetMember(osT, "Translation");
            float bestSq = float.MaxValue, bestStart = -1f;
            lock (e.activeFires)
            {
                for (int i = 0; i < e.activeFires.Count; i++)
                {
                    float d = (e.activeFires[i].pos - tpos).sqrMagnitude;
                    if (d < bestSq) { bestSq = d; bestStart = e.activeFires[i].startTime; }
                }
            }
            const float matchRadiusSq = 4f * 4f;                       // a pawn within 4u of a fire is the firer (tiles are spaced wider)
            if (bestStart >= 0f && bestSq <= matchRadiusSq)
            {
                float elapsed = UnityEngine.Time.time - bestStart;
                poseTime = elapsed >= dur ? 0f : elapsed / dur;        // one pass, then rest (Update prunes finished fires)
            }
            return poseTime;
        }

        // Clear the procedural AIM layer (PawnEntry.BoneRotation0-3 = SkeletonBoneIndex/AxisIndex/Angle): the game aims an
        // artillery barrel by layering a bone rotation ON TOP of the pose, which twists our barrel (most visible while moving,
        // as the aim swings). Zero the angles so ONLY our baked clip drives the skeleton. No-op for un-aimed models.
        static void ClearAimLayer(object entry)
        {
            for (int i = 0; i < 4; i++)
            {
                var br = GetMember(entry, "BoneRotation" + i);
                if (br == null) continue;
                SetMember(br, "Angle", 0f);
                SetMember(entry, "BoneRotation" + i, br);
            }
        }

        // Runtime position offset. Static models bake position into the mesh; the animated path can't, so apply it to the
        // pawn's world ObjectSpace here. z = height -> world up (Y); x/y -> world plane. Re-applied each frame on the game's
        // fresh world position, so it never accumulates. Logged once to confirm the axis.
        static void ApplyPositionOffset(ModelEntry e, object entry)
        {
            if (e.position == UnityEngine.Vector3.zero) return;
            var os = GetMember(entry, "ObjectSpace");                  // boxed TRS
            var tr = (UnityEngine.Vector3)GetMember(os, "Translation");
            if (!posLogged) { posLogged = true; Plugin.Log.LogInfo($"[Uni] {e.resourceName} pawn world pos {tr} + registry position {e.position} (z->up Y)"); }
            tr.x += e.position.x; tr.y += e.position.z; tr.z += e.position.y;   // registry z (height) -> world Y (up)
            SetMember(os, "Translation", tr);
            SetMember(entry, "ObjectSpace", os);
        }

        // Runtime scale multiplier: fix an animated model baked at the wrong scale WITHOUT a re-bake (the howitzer's 100x FBX
        // unit-conversion oversize -> scale 0.01). Multiplies the pawn's ObjectSpace.Scale each frame.
        static void ApplyScale(ModelEntry e, object entry)
        {
            if (e.scale == 1f || e.scale <= 0f) return;
            var oss = GetMember(entry, "ObjectSpace");
            var scObj = GetMember(oss, "Scale");
            if (scObj is float sf) SetMember(oss, "Scale", sf * e.scale);
            else if (scObj is UnityEngine.Vector3 sv) SetMember(oss, "Scale", sv * e.scale);
            else if (scObj != null) { try { SetMember(oss, "Scale", Convert.ToSingle(scObj) * e.scale); } catch { } }
            SetMember(entry, "ObjectSpace", oss);
            if (!scaleLogged) { scaleLogged = true; Plugin.Log.LogInfo($"[Uni] {e.resourceName} runtime scale x{e.scale} (ObjectSpace.Scale was {scObj})"); }
        }

        // Diagnostic: dump the pawn's runtime transform once per model — a zero/huge Scale or an off Translation explains a
        // model that's fine in the editor preview but invisible in-game (docs/Firing-On-Attack.md).
        static void LogPoseHookOnce(PawnCtx ctx, ModelEntry e, object pose0)
        {
            if (poseHookSeen == null) poseHookSeen = new HashSet<string>();
            if (!poseHookSeen.Add(e.resourceName)) return;
            var osd = GetMember(ctx.entry, "ObjectSpace");
            Plugin.Log.LogInfo($"[Uni] pose hook: '{e.resourceName}' -> Pose0 anim {e.animId} (skelId {ctx.skelId} -> {e.skeletonId}, desc {ctx.descId}); " +
                $"ObjectSpace T={GetMember(osd, "Translation")} S={GetMember(osd, "Scale")} R={GetMember(osd, "Rotation")} poseW={GetMember(pose0, "Weight")}");
        }

        // ---- RE-SPAWN NEWLY-CREATED INJECTED UNITS: fix the first-instance rotor race ----
        // A custom pawn that borrows a donor's animated sub-part (e.g. a helicopter rotor) can render that part ~1 unit low
        // when it is first CREATED — the first of a batch on a save-load, a lone unit built in a city, or one spawned via
        // dev tools. Re-creating its pawns fixes it. So we watch the presentation each frame and, ~5s after ANY unit of an
        // opted-in model first appears, re-run the game's own pawn rebuild (PresentationUnit.UpdatePawns = ReleasePawns +
        // InstantiatePawns) on it — a presentation-only refresh, no simulation touched, no unit lost. Deliberately applied
        // to EVERY such unit (one brief flicker each): better to re-spawn one too many than miss a buggy one. Called from
        // Plugin.Update (per-frame, a SAFE point OUTSIDE the AddPawnEntry loop — calling UpdatePawns inside that loop hangs).
        static int respawnFrame;
        const int RespawnAttempts = 1;         // re-spawn each unit ONCE; a single pass fixes the rotor and holds (tested)
        // Delay (in frames, after the unit first renders) before the re-spawn is CONFIGURABLE via the plugin cfg
        // (Factory/RespawnDelayFrames, default 1 = near-instant). A modder on slower hardware can raise it if a unit needs
        // longer to settle. base = first LOADED frame — before the unit is rendered there's nothing to fix.
        static readonly Dictionary<object, int> respawnBase = new Dictionary<object, int>();   // opted-in unit -> frame it was first seen loaded
        static readonly Dictionary<object, int> respawnCount = new Dictionary<object, int>();  // opted-in unit -> re-spawns done so far
        // Strip a pawn description's trailing variant suffix ("Era6_Common_StealthHelicopters_01" -> "Era6_Common_StealthHelicopters")
        // so it matches the unit-definition name ("LandUnit_Era6_Common_StealthHelicopters").
        static string CoreDesc(string pd) => System.Text.RegularExpressions.Regex.Replace(pd ?? "", "_[0-9]+$", "");
        internal static void MaybeRespawnPostLoad()
        {
            if (entries == null || !Plugin.UniversalInjectOn.Value) return;
            if (!entries.Any(x => x.respawnAfterLoad)) return;      // no model opted in — nothing to do
            if (++respawnFrame % 5 != 0) return;                    // throttle the scan to ~12x/s (frame counter still advances every frame)
            try
            {
                var presType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.Presentation");
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return;

                var present = new HashSet<object>();
                foreach (var army in armies)
                {
                    if (army == null) continue;
                    var unit = GetMember(army, "PresentationUnit");
                    if (unit == null) continue;
                    string uname = GetMember(GetMember(unit, "UnitDefinition"), "Name")?.ToString() ?? "";
                    if (uname.Length == 0) continue;
                    // Is this unit one of our opted-in models? (name match, minus the entry's trailing _NN suffix)
                    if (!entries.Any(x => x.respawnAfterLoad && x.pawnDescription.Length > 0
                            && uname.IndexOf(CoreDesc(x.pawnDescription), StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    present.Add(unit);
                    // Only start the clock once the unit is actually rendered (IsLoaded) — before that there's nothing to fix
                    // and the 1s marks would be wasted during the load.
                    bool loaded = true; try { loaded = Convert.ToBoolean(GetMember(unit, "IsLoaded")); } catch { }
                    if (!loaded) continue;
                    if (!respawnBase.ContainsKey(unit)) { respawnBase[unit] = respawnFrame; respawnCount[unit] = 0; continue; }
                    int done = respawnCount[unit];
                    if (done >= RespawnAttempts) continue;                                             // all passes done
                    if (respawnFrame - respawnBase[unit] < (done + 1) * Math.Max(1, Plugin.RespawnDelayFrames.Value)) continue; // not time for the next pass yet
                    respawnCount[unit] = done + 1;                                                     // bump first so a throwing unit isn't stuck
                    bool naval = false; try { naval = Convert.ToBoolean(GetMember(unit, "IsNaval")); } catch { }
                    AccessTools.Method(unit.GetType(), "UpdatePawns", new[] { typeof(bool) })?.Invoke(unit, new object[] { naval });
                    Plugin.Log.LogInfo($"[Uni][RESPAWN] re-spawned '{uname}' shortly after it rendered (clears the first-instance rotor race)");
                }
                // Drop bookkeeping for units that are gone (destroyed, or the previous game's units after a reload) so the
                // dicts don't grow and a genuinely new instance (a new object) is detected + fixed again.
                if (respawnBase.Count > 0) foreach (var k in respawnBase.Keys.Where(k => !present.Contains(k)).ToList()) { respawnBase.Remove(k); respawnCount.Remove(k); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni][RESPAWN] " + ex); }
        }

        // PER-INSTANCE fire targeting (main thread — Plugin.Update). The combat hook enqueues the firing unit/army GUID on the
        // sim thread; here we resolve the matching PresentationUnit and record each of its pawns' RENDER positions as active
        // fires, and prune fires whose clip has finished. The pose hook then plays the one-shot only on the pawn nearest an
        // active fire, so a single howitzer bombarding doesn't animate every howitzer of the type. Same presentation walk as
        // MaybeRespawnPostLoad (Presentation.PresentationEntityFactoryController.PresentationArmyEntities -> PresentationUnit).
        // Read a SimulationEntityGUID as a stable long. Convert.ToInt64/IConvertible.ToInt64 THROW InvalidCastException on
        // this struct, but ToString() returns its underlying ulong as a decimal string — parse that. Both the combat hook
        // (StrikerUnit/StrikerArmy.GUID) and ProcessFireQueues (PresentationUnit.GUID) use this so the values compare equal.
        internal static long GuidToLong(object guidBox)
            => guidBox != null && ulong.TryParse(guidBox.ToString(), out ulong g) ? unchecked((long)g) : 0L;

        internal static void ProcessFireQueues()
        {
            if (entries == null || !Plugin.UniversalInjectOn.Value) return;
            bool anyQueued = false;
            foreach (var e in entries)
            {
                if (!e.fireOnAttack) continue;
                float dur = e.animDuration > 0.001f ? e.animDuration : 1f;
                lock (e.activeFires) e.activeFires.RemoveAll(f => UnityEngine.Time.time - f.startTime >= dur);   // drop finished one-shots
                if (!e.fireGuidQueue.IsEmpty) anyQueued = true;
            }
            if (!anyQueued) return;
            try
            {
                var presType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.Presentation");
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return;
                foreach (var e in entries)
                {
                    if (!e.fireOnAttack || e.fireGuidQueue.IsEmpty) continue;
                    var fired = new HashSet<long>();
                    while (e.fireGuidQueue.TryDequeue(out long g)) fired.Add(g);
                    if (fired.Count == 0) continue;
                    bool matched = false;
                    foreach (var army in armies)
                    {
                        if (army == null) continue;
                        var unit = GetMember(army, "PresentationUnit");
                        if (unit == null) continue;
                        long uguid = GuidToLong(GetMember(unit, "GUID"));
                        if (uguid == 0 || !fired.Contains(uguid)) continue;
                        var pawns = GetMember(unit, "Pawns") as System.Collections.IEnumerable;
                        if (pawns == null) continue;
                        int n = 0; string posDump = "";
                        lock (e.activeFires)
                            foreach (var pawn in pawns)
                            {
                                var tr = GetMember(pawn, "Transform") as UnityEngine.Transform;
                                if (tr == null) continue;
                                e.activeFires.Add(new FireInstance { pos = tr.position, startTime = UnityEngine.Time.time });
                                posDump += $" {tr.position.ToString("0.0")}"; n++;
                            }
                        matched = true;
                        // Log the fire positions so they can be compared to the pose hook's 'ObjectSpace T=...' dump — if the
                        // two are in different spaces the nearest-match (radius 4u) won't fire; this shows it at a glance.
                        Plugin.Log.LogInfo($"[Fire] '{e.resourceName}' unit/army {uguid}: armed {n} pawn(s) at{posDump}");
                    }
                    if (!matched) Plugin.Log.LogWarning($"[Fire] '{e.resourceName}': fired GUID(s) [{string.Join(",", fired)}] matched no PresentationUnit — barrel won't animate (timing/GUID mismatch)");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Fire] ProcessFireQueues: " + ex); }
        }

        // DEPLOY-ON-STOP poll (main thread — Plugin.Update). For each deployOnStop model, record the render positions of the
        // pawns whose unit is currently MOVING (PresentationUnit.IsAnyPawnMoving). The pose hook then undeploys any pawn near
        // one of those and holds the deployed pose for the rest — an instant, per-pawn moving→pose mapping (no state machine).
        // Same presentation walk as MaybeRespawnPostLoad; scoped to VISIBLE our-model units, so AI/off-screen moves never reach it.
        static int deployFrame;
        static Dictionary<long, bool> deployMoveState;   // diagnostic: log each deploy unit's moving<->stopped transitions
        internal static void ProcessDeployState()
        {
            if (entries == null || !Plugin.UniversalInjectOn.Value) return;
            if (!entries.Any(x => x.deployOnStop)) return;
            if (++deployFrame % 3 != 0) return;   // ~20x/s; the ramp is dt-based so it stays smooth + framerate-independent
            try
            {
                // per-entry ramp step: dt (since last poll) / clip duration => normalized pose-time units this tick
                var now = UnityEngine.Time.time;
                var step = new Dictionary<ModelEntry, float>();
                var fresh = new Dictionary<ModelEntry, List<DeploySample>>();
                var seen = new Dictionary<ModelEntry, HashSet<long>>();
                foreach (var e in entries) if (e.deployOnStop)
                {
                    float dt = e.deployLastPoll > 0f ? Math.Min(now - e.deployLastPoll, 0.5f) : 0f;   // clamp a big first/stall gap
                    e.deployLastPoll = now;
                    float dur = e.animDuration > 0.001f ? e.animDuration : 1f;
                    step[e] = dt / dur * (e.deploySpeed > 0f ? e.deploySpeed : 1f);
                    fresh[e] = new List<DeploySample>();
                    seen[e] = new HashSet<long>();
                }
                var presType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.Presentation");
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies != null)
                    foreach (var army in armies)
                    {
                        if (army == null) continue;
                        var unit = GetMember(army, "PresentationUnit");
                        if (unit == null) continue;
                        string uname = GetMember(GetMember(unit, "UnitDefinition"), "Name")?.ToString() ?? "";
                        if (uname.Length == 0) continue;
                        var e = entries.FirstOrDefault(x => x.deployOnStop && x.pawnDescription.Length > 0
                                    && uname.IndexOf(CoreDesc(x.pawnDescription), StringComparison.OrdinalIgnoreCase) >= 0);
                        if (e == null) continue;
                        long guid = GuidToLong(GetMember(unit, "GUID"));
                        if (guid == 0) continue;
                        // Movement per PAWN via IsMoving(ignoreWaitToIdle: TRUE, isMovingAlongTilesOnly: TRUE). The unit-level
                        // IsAnyPawnMoving hardcodes ignoreWaitToIdle:false, so the wait-to-idle/turn settle after a unit stops
                        // reads as "moving" and the deploy snaps back to folded (barrel raises then drops to horizontal). We
                        // only want folded during ACTUAL tile-to-tile travel — ignoring the settle keeps the deployed pose held.
                        var pawnList = (GetMember(unit, "Pawns") as System.Collections.IEnumerable)?.Cast<object>().ToList();
                        // MOVEMENT = the unit's RENDER POSITION actually changed since the last poll (real tile traversal). This is
                        // INSTANT (no debounce lag) and settle-immune: the game's wait-to-idle / turn-in-place after stopping does
                        // NOT move the tile position, so a resting unit reads "not moving" and stays deployed, while a travelling one
                        // folds the moment it starts. (The deploy clip animates the SKELETON, not the pawn transform — no self-trigger.)
                        UnityEngine.Vector3 upos = UnityEngine.Vector3.zero; bool hasPos = false;
                        if (pawnList != null)
                            foreach (var pawn in pawnList)
                                if (GetMember(pawn, "Transform") is UnityEngine.Transform tr0) { upos = tr0.position; hasPos = true; break; }
                        bool moving = false;
                        if (hasPos)
                        {
                            if (e.deployLastPos.TryGetValue(guid, out var lastP)) moving = (upos - lastP).sqrMagnitude > 0.1f * 0.1f;
                            e.deployLastPos[guid] = upos;
                        }
                        // Clamp the deployed target just below 1.0: the pose sampler does Mathf.Repeat(Time,1), so a poseTime of
                        // EXACTLY 1.0 wraps to 0.0 = frame 0 = the FOLDED pose. Holding at 0.999 lands on the last real frame instead.
                        float target = UnityEngine.Mathf.Min(e.deployPoseTime, 0.999f);
                        float cur;
                        if (moving) cur = 0f;                                                          // travelling -> folded (instant)
                        else cur = e.deployProgress.TryGetValue(guid, out float p) ? UnityEngine.Mathf.MoveTowards(p, target, step[e]) : target;   // rest -> ramp to / HOLD fully deployed
                        e.deployProgress[guid] = cur;
                        seen[e].Add(guid);
                        if (deployMoveState == null) deployMoveState = new Dictionary<long, bool>();
                        if (!deployMoveState.TryGetValue(guid, out bool wasMoving) || wasMoving != moving)   // log on each moving<->stopped flip
                        { deployMoveState[guid] = moving; Plugin.Log.LogInfo($"[Deploy] '{e.resourceName}' unit {guid} moving={moving} poseTime={cur:0.00}"); }
                        if (pawnList != null)
                            foreach (var pawn in pawnList)
                                if (GetMember(pawn, "Transform") is UnityEngine.Transform tr) fresh[e].Add(new DeploySample { pos = tr.position, poseTime = cur });
                    }
                foreach (var e in fresh.Keys)
                {
                    lock (e.deploySamples) { e.deploySamples.Clear(); e.deploySamples.AddRange(fresh[e]); }
                    foreach (var g in e.deployProgress.Keys.Where(k => !seen[e].Contains(k)).ToList()) e.deployProgress.Remove(g);   // drop gone units
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Deploy] ProcessDeployState: " + ex); }
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

        // Memoize the Property/Field resolution per (type,name). OnPawnAdded resolves ~a dozen members per pawn-add on the
        // game's hot path; caching the lookup (null included) turns those from member scans into dict hits. Semantics are
        // identical to the old inline AccessTools calls (property-first, CanWrite for writes, field fallback). Main-thread only.
        static readonly Dictionary<(Type, string), PropertyInfo> propCache = new Dictionary<(Type, string), PropertyInfo>();
        static readonly Dictionary<(Type, string), FieldInfo> fieldCache = new Dictionary<(Type, string), FieldInfo>();
        static PropertyInfo CachedProp(Type t, string name) { var k = (t, name); if (!propCache.TryGetValue(k, out var p)) propCache[k] = p = AccessTools.Property(t, name); return p; }
        static FieldInfo CachedField(Type t, string name) { var k = (t, name); if (!fieldCache.TryGetValue(k, out var f)) fieldCache[k] = f = AccessTools.Field(t, name); return f; }

        static void SetMember(object o, string name, object val)
        { var t = o.GetType(); var p = CachedProp(t, name); if (p != null && p.CanWrite) { try { p.SetValue(o, val); return; } catch { } } var f = CachedField(t, name); if (f != null) { try { f.SetValue(o, val); } catch { } } }
        static object GetMember(object o, string name)
        { if (o == null) return null; var t = o.GetType(); var p = CachedProp(t, name); if (p != null) { try { return p.GetValue(o); } catch { } } var f = CachedField(t, name); if (f != null) { try { return f.GetValue(o); } catch { } } return null; }
        static object MakeGuid(int a, int b, int c, int d)
        { var gt = AccessTools.TypeByName("Amplitude.Framework.Guid"); if (gt == null) return null; var g = Activator.CreateInstance(gt);
          gt.GetField("a", BF)?.SetValue(g, a); gt.GetField("b", BF)?.SetValue(g, b); gt.GetField("c", BF)?.SetValue(g, c); gt.GetField("d", BF)?.SetValue(g, d); return g; }

        // Diagnostic: dump the LIVE GPU mesh-content buffer usage per content layer. Answers the real scaling question
        // ("how many more models fit"): the Amplitude manager packs every registered skeleton/mesh-collection into a
        // fixed buffer sized 100k verts / 250k indices / 256 meshes PER ContentLayer, tracked by running cursors. Reading
        // those cursors tells us exactly how full each layer is and whether the mod's models are all resident at once or
        // only the active unit types. Bound to a hotkey — press in-game with custom units on the map.
        // Build the live budget readout as lines (shared by the F8 window and the Shift+F8 log dump).
        internal static System.Collections.Generic.List<string> MeshBudgetLines()
        {
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                var amType = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
                var inst = amType != null ? AccessTools.Property(amType, "Instance")?.GetValue(null) : null;
                if (inst == null) { lines.Add("AnimationManager.Instance is null — load a game first."); return lines; }
                var fxMgr = GetMember(inst, "FxComponentMeshContentManager");
                if (fxMgr == null) { lines.Add("FxComponentMeshContentManager is null."); return lines; }
                int pawnLayer = GetMember(inst, "FXMeshLayerIndex") is int pl ? pl : -1;
                if (!(GetMember(fxMgr, "Layers") is Array layers)) { lines.Add("Layers array not found."); return lines; }
                lines.Add($"GPU mesh buffer — {layers.Length} layer(s), pawn layer = {pawnLayer}:");
                for (int i = 0; i < layers.Length; i++)
                {
                    var L = layers.GetValue(i);
                    if (L == null) { lines.Add($"  layer {i}: <null>"); continue; }
                    string nm = GetMember(L, "name") as string ?? "?";
                    int v = ToInt(GetMember(L, "currentVertexIndex")),   vMax = ToInt(GetMember(L, "baseVertexBufferSize"));
                    int x = ToInt(GetMember(L, "currentIndexIndex")),    xMax = ToInt(GetMember(L, "baseIndexBufferSize"));
                    int m = ToInt(GetMember(L, "currentMeshAddedCount")), mMax = ToInt(GetMember(L, "maxMeshCount"));
                    string tag = i == pawnLayer ? "  <-- your models" : "";
                    lines.Add($"  L{i} '{nm}': verts {v:n0}/{vMax:n0} ({Pct(v, vMax)}%) | idx {Pct(x, xMax)}% | meshes {m}/{mMax}{tag}");
                }
            }
            catch (Exception ex) { lines.Add("budget read failed: " + ex.Message); }
            return lines;
        }

        internal static void DumpMeshBudget()   // Shift+F8: same readout, to the log
        {
            foreach (var l in MeshBudgetLines()) Plugin.Log.LogInfo("[Budget] " + l);
        }
        static int ToInt(object o) { try { return o == null ? -1 : Convert.ToInt32(o); } catch { return -1; } }
        static int Pct(int a, int b) { return b > 0 ? (int)(100.0 * a / b) : 0; }

        // ---- ATLAS DUMP (retexture aid) ------------------------------------------------------------------------------
        // Dump every currently-loaded unit output-layer atlas (its material's _MainTex) to
        // BepInEx/config/enc_atlas_dump/<layer>.png, so a unit's skin can be found by its layer name and used as a
        // paint canvas (e.g. to make a desaturated "grey" variant of a Common copy). Reuses ApplyTexture's Content walk
        // (Content -> OutputLayerEntries -> OutputLayerInstance) and TickOne's material fields; the host atlas isn't
        // CPU-readable, so each is blitted through a RenderTexture first. One PNG per layer. Bound to the F8 window's
        // "Dump Atlases" button — load a game with the target units visible, then click.
        internal static void DumpOutputLayerAtlases()
        {
            try
            {
                var amType = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
                var mgr = amType != null ? AccessTools.Property(amType, "Instance")?.GetValue(null) : null;
                if (mgr == null) { Plugin.Log.LogWarning("[AtlasDump] AnimationManager.Instance is null — load a game first."); return; }
                var content = GetMember(mgr, "Content");
                var list = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (list == null) { Plugin.Log.LogWarning("[AtlasDump] no OutputLayerEntries found."); return; }
                string dir = Path.Combine(Paths.ConfigPath, "enc_atlas_dump");
                Directory.CreateDirectory(dir);
                var seen = new HashSet<string>();
                int n = 0;
                foreach (var entry in list)
                {
                    var ol = GetMember(entry, "OutputLayerInstance");
                    if (ol == null) continue;
                    string layer = (ol as UnityEngine.Object)?.name ?? "layer";
                    if (!seen.Add(layer)) continue;   // one dump per layer
                    UnityEngine.Texture tex = null;
                    if (GetMember(ol, "RenderOutputs") is Array ros)
                        foreach (var ro in ros)
                        {
                            foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial" })
                                if (GetMember(ro, fld) is UnityEngine.Material mat && mat.GetTexture("_MainTex") is UnityEngine.Texture mt) { tex = mt; break; }
                            if (tex != null) break;
                        }
                    if (tex == null) continue;
                    var tga = ToReadableTga(tex);
                    if (tga == null) continue;
                    File.WriteAllBytes(Path.Combine(dir, SanitizeFile(layer) + ".tga"), tga);
                    n++;
                    Plugin.Log.LogInfo($"[AtlasDump] {layer} -> {SanitizeFile(layer)}.tga ({tex.width}x{tex.height}, {tex.name})");
                }
                Plugin.Log.LogInfo($"[AtlasDump] wrote {n} atlas PNG(s) to {dir}");
            }
            catch (Exception e) { Plugin.Log.LogError("[AtlasDump] " + e); }
        }

        // Blit any (possibly non-readable / compressed) texture through a RenderTexture into a readable Texture2D, then
        // encode an uncompressed 32-bit TGA (BGRA). TGA avoids a UnityEngine.ImageConversionModule (EncodeToPNG) reference
        // the plugin doesn't carry, and any image editor opens it. Bottom-left origin matches Unity's bottom-up GetPixels32.
        static byte[] ToReadableTga(UnityEngine.Texture src)
        {
            try
            {
                int w = src.width, h = src.height;
                var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                var prev = UnityEngine.RenderTexture.active;
                UnityEngine.Graphics.Blit(src, rt);
                UnityEngine.RenderTexture.active = rt;
                var t = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false);
                t.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0); t.Apply();
                UnityEngine.RenderTexture.active = prev; UnityEngine.RenderTexture.ReleaseTemporary(rt);
                var px = t.GetPixels32();
                UnityEngine.Object.DestroyImmediate(t);
                var buf = new byte[18 + w * h * 4];
                buf[2] = 2;                                                     // uncompressed true-colour
                buf[12] = (byte)(w & 0xFF); buf[13] = (byte)((w >> 8) & 0xFF);
                buf[14] = (byte)(h & 0xFF); buf[15] = (byte)((h >> 8) & 0xFF);
                buf[16] = 32;                                                   // bits per pixel
                buf[17] = 0x08;                                                 // 8 alpha bits, bottom-left origin
                int o = 18;
                for (int i = 0; i < px.Length; i++) { buf[o++] = px[i].b; buf[o++] = px[i].g; buf[o++] = px[i].r; buf[o++] = px[i].a; }
                return buf;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[AtlasDump] readable copy failed for '" + (src != null ? src.name : "?") + "': " + e.Message); return null; }
        }

        static string SanitizeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "layer";
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.ToString();
        }
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
        static void Postfix(object __instance) { if (!hookLogged) { hookLogged = true; Plugin.Log.LogInfo("[Uni] UniRegisterHook POSTFIX fired"); } Prober.AnimMgr = __instance; UniversalInject.EnsureRegistered(__instance); }
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
