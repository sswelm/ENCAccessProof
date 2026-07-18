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
        public float desaturate = 0f;         // TEXTURE-ONLY GREY variant: 0 = off. >0 = DON'T repoint the mesh; isolate this unit's output layer and paint a DESATURATED copy of its OWN atlas (1 = full grey) while the civ-colour tint is neutralised. Makes a Common copy read as a bland grey version of an emblematic unit; the original is untouched (they share the layer, so the isolation clone is essential). No bake / no custom model needed.
        public string textureFile = "";       // TEXTURE-ONLY RETEXTURE: a PNG filename in BepInEx/config/enc_skins/. When set, the plugin loads that PNG and paints it onto the unit's ISOLATED output layer (same isolation as desaturate — original untouched, vanilla mesh kept). Hot-loaded at runtime, no bake/rebuild. Takes precedence over desaturate. Painted on a dump of the unit's own atlas (round-trips via PNG). Managed by the Unit Retexture editor window.
        public float tintR = 0f;              // UNIVERSAL skin colour offset, red channel (-255..+255, 0 = none). Added AFTER desaturate to whatever skin this unit ends up with — the loaded textureFile PNG, OR a copy of its own atlas. Equal negative R/G/B = darken; equal positive = brighten; one channel tints.
        public float tintG = 0f;              // ... green channel (-255..+255).
        public float tintB = 0f;              // ... blue channel (-255..+255).
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
        // ENGINE AUDIO: our injected units never FIRE the per-ship move sound (Play_UNIT_Vehicles_<Type>_Start/_Stop) — it
        // rides the service path tied to the vanilla unit's move state, which our re-loaded units don't trigger. When set,
        // the plugin detects each instance starting/stopping (render-position delta, like deployOnStop) and posts the
        // captured Start/Stop AudioEventHandle onto that pawn's AudioEmitter, restoring the missing engine sound.
        public bool engineSound;
        public string engineStartEvent = "";  // Wwise event NAME posted on move-START (e.g. Play_UNIT_Vehicles_StealthCorvette_Start). Set => posted BY NAME (works for the FIRST unit, no live capture); empty => fall back to the auto-captured handle.
        public string engineStopEvent = "";   // ... move-STOP (..._Stop). Extract names via the F8 "Dump Sound Catalog"; assign per unit in the registry.
        public string soundFile = "";          // CUSTOM audio, LOOP while moving: a WAV filename in BepInEx/config/enc_sounds/. Unity AudioSource, 3D. For units the game has NO sound for (drones, zeppelins) or a bespoke engine.
        public string soundStartFile = "";     // CUSTOM one-shot on move-START (spool-up): a WAV in enc_sounds/.
        public string soundStopFile = "";      // CUSTOM one-shot on move-STOP (spool-down): a WAV in enc_sounds/.
        public float soundVolume = 1f;         // travel-loop volume
        public float soundStartVolume = 1f;    // move-start one-shot volume
        public float soundStopVolume = 1f;     // move-stop one-shot volume
        public UnityEngine.AudioClip customClip, customStartClip, customStopClip;    // loaded once from the three files
        public bool customClipTried;                                                 // don't retry a failed load every poll
        public readonly Dictionary<int, UnityEngine.AudioSource> customSources = new Dictionary<int, UnityEngine.AudioSource>();  // sub-pawn instance id -> our looping AudioSource (played while moving)
        public readonly Dictionary<int, float> loopHoldUntil = new Dictionary<int, float>();   // instance id -> Time.time to hold the travel loop off until (so the spool-up one-shot isn't masked)
        public readonly Dictionary<int, UnityEngine.Vector3> engineLastPos = new Dictionary<int, UnityEngine.Vector3>();  // sub-pawn instance id -> last render pos
        public readonly Dictionary<int, bool> engineMoving = new Dictionary<int, bool>();                                  // sub-pawn instance id -> was moving last poll
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

        // A discovered mod PACK: one registry file's wrapper metadata + its models. HAF (Humankind Asset Framework)
        // multi-mod support merges many packs into `entries`, so ENC is just one mod among many — any modder ships their
        // own pack (config + assets) to join. The wrapper keys (modId/schemaVersion/dependsOn/loadAfter/overrides) sit
        // BESIDE the existing "models" array, so a legacy bare { "models": [...] } file still parses — with default metadata.
        class Pack
        {
            public string modId = "", file = "";
            public int schemaVersion;
            public List<string> dependsOn = new List<string>();
            public List<string> loadAfter = new List<string>();
            public bool hasOverrides;                 // RESERVED: parsed + echoed in the load report; ordering/override RESOLUTION is a later increment (a second real author will shape it)
            public List<ModelEntry> models = new List<ModelEntry>();
        }

        static void LoadRegistry()
        {
            if (loaded) return;
            entries = new List<ModelEntry>();
            try
            {
                // DISCOVERY: the ENC base registry (enc_models.json) + every *.json a third-party mod drops in haf_packs/.
                // Each file is a PACK; a joining modder ships their own pack instead of editing ours. The base loads FIRST,
                // so ENC's own models are protected from an accidental clash (first-loaded wins — see the merge below).
                var basePath = Path.Combine(Paths.ConfigPath, "enc_models.json");
                var files = new List<string>();
                if (File.Exists(basePath)) files.Add(basePath);
                var packDir = Path.Combine(Paths.ConfigPath, "haf_packs");
                if (Directory.Exists(packDir))
                    files.AddRange(Directory.GetFiles(packDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                if (files.Count == 0) { Plugin.Log.LogInfo("[Uni] no registry at " + basePath + " and no haf_packs/*.json"); loaded = true; return; }

                var packs = new List<Pack>();
                foreach (var file in files)
                {
                    // One unreadable pack must not sink the others — skip it loudly and keep going.
                    try { packs.Add(ParsePack(file, file == basePath)); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] pack '{Path.GetFileName(file)}' failed to parse ({ex.Message}); skipped"); }
                }
                if (packs.Count == 0) throw new Exception("no packs could be read");   // transient (lock/AV) — retry, don't latch

                // MERGE with explicit conflict detection. A model's identity is its pawnDescription (the physical pawn slot
                // — two skins can't ride one pawn). v1 policy: FIRST-loaded wins on an undeclared clash, and it's logged LOUD
                // (no silent overrides — the HAF conflict philosophy). dependsOn/loadAfter/overrides are parsed and echoed in
                // the report but NOT yet enforced; that resolution lands when a second real author needs it.
                var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var conflicts = new List<string>();
                foreach (var pk in packs)
                    foreach (var e in pk.models)
                    {
                        if (!string.IsNullOrEmpty(e.pawnDescription) && owner.TryGetValue(e.pawnDescription, out var held))
                        {
                            conflicts.Add($"pawn={e.pawnDescription} kept={held} dropped={pk.modId}({e.resourceName})");
                            Plugin.Log.LogWarning($"[Uni] CONFLICT: pack '{pk.modId}' targets pawn '{e.pawnDescription}' already claimed by '{held}' — keeping '{held}' (first-loaded wins).");
                            continue;
                        }
                        if (!string.IsNullOrEmpty(e.pawnDescription)) owner[e.pawnDescription] = pk.modId;
                        entries.Add(e);
                    }

                WriteLoadReport(packs, entries.Count, conflicts);
                Plugin.Log.LogInfo($"[Uni] loaded {packs.Count} pack(s), {entries.Count} model(s), {conflicts.Count} conflict(s) [" + string.Join(", ", packs.Select(p => p.modId + "×" + p.models.Count)) + "]");
                loaded = true;
            }
            catch (Exception e)
            {
                // Do NOT latch `loaded` on a failure — a transient hiccup (AV scan, sharing violation while the game is
                // still flushing a file) would otherwise disable ALL injection for the session. Retry on the next few
                // pawn loads; give up (latch) only after 3 tries so a genuinely broken file doesn't re-parse forever.
                entries = new List<ModelEntry>();
                if (++loadAttempts >= 3) { loaded = true; Plugin.Log.LogError("[Uni] registry load failed 3x, giving up for this session: " + e); }
                else Plugin.Log.LogWarning($"[Uni] registry load failed (attempt {loadAttempts}/3), will retry on next pawn: " + e.Message);
            }
        }

        // Parse one pack file into its wrapper metadata + models. The wrapper is OPTIONAL: a legacy bare { "models": [...] }
        // yields default metadata (modId = "enc" for the base file, else the filename). Throws only if the file can't be read.
        static Pack ParsePack(string file, bool isBase)
        {
            var text = File.ReadAllText(file);
            var pk = new Pack { file = file, modId = isBase ? "enc" : Path.GetFileNameWithoutExtension(file) };
            try
            {
                var root = JObject.Parse(text);
                if (root["modId"] != null) pk.modId = (string)root["modId"];
                if (root["schemaVersion"] != null) pk.schemaVersion = (int)root["schemaVersion"];
                pk.dependsOn = StrList(root["dependsOn"]);
                pk.loadAfter = StrList(root["loadAfter"]);
                pk.hasOverrides = (root["overrides"] as JArray)?.Count > 0;
            }
            catch { }   // wrapper is best-effort; the models parse below has its own primary+fallback and is what matters
            pk.models = ParseModels(text);
            return pk;
        }

        static List<string> StrList(JToken t) =>
            (t as JArray)?.Select(x => (string)x).Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new List<string>();

        // Write a human-readable load report next to the registry — packs discovered, model counts, reserved metadata, and
        // any conflicts. This is what makes a multi-pack setup DEBUGGABLE (the early slice of HAF runtime diagnostics) and is
        // the first thing a joining modder checks to confirm their pack was seen.
        static void WriteLoadReport(List<Pack> packs, int total, List<string> conflicts)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("HAF load report  (regenerated every load)");
                sb.AppendLine($"packs={packs.Count}  models={total}  conflicts={conflicts.Count}");
                sb.AppendLine();
                foreach (var p in packs)
                {
                    sb.AppendLine($"[{p.modId}]  schemaVersion={p.schemaVersion}  models={p.models.Count}  file={Path.GetFileName(p.file)}");
                    if (p.dependsOn.Count > 0) sb.AppendLine("    dependsOn: " + string.Join(", ", p.dependsOn) + "   (reserved — not yet enforced)");
                    if (p.loadAfter.Count > 0) sb.AppendLine("    loadAfter: " + string.Join(", ", p.loadAfter) + "   (reserved — not yet enforced)");
                    if (p.hasOverrides) sb.AppendLine("    overrides: declared   (reserved — not yet enforced)");
                }
                if (conflicts.Count > 0) { sb.AppendLine(); sb.AppendLine("CONFLICTS (first-loaded kept):"); foreach (var c in conflicts) sb.AppendLine("  " + c); }
                File.WriteAllText(Path.Combine(Paths.ConfigPath, "haf_load_report.txt"), sb.ToString());
            }
            catch { }
        }

        // Parse the "models" array from one registry file's text. PRIMARY: Newtonsoft (object-per-model, robust to a
        // missing/reordered field). FALLBACK: field-by-field regex (index-aligned). Semantics identical to the original
        // single-file loader; lifted into a helper so every pack shares it. The local `entries` intentionally shadows the
        // field to keep the large per-field Add blocks below verbatim.
        static List<ModelEntry> ParseModels(string text)
        {
            var entries = new List<ModelEntry>();
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
                                desaturate = m["desaturate"] != null ? (float)m["desaturate"] : 0f,
                                tintR = m["tintR"] != null ? (float)m["tintR"] : 0f,
                                tintG = m["tintG"] != null ? (float)m["tintG"] : 0f,
                                tintB = m["tintB"] != null ? (float)m["tintB"] : 0f,
                                textureFile = (string)m["textureFile"] ?? "",
                                respawnAfterLoad = (bool?)m["respawnAfterLoad"] ?? false,
                                freezeDonorAnim = (bool?)m["freezeDonorAnim"] ?? false,
                                fireOnAttack = (bool?)m["fireOnAttack"] ?? false,
                                deployOnStop = (bool?)m["deployOnStop"] ?? false,
                                engineSound = (bool?)m["engineSound"] ?? false,
                                engineStartEvent = (string)m["engineStartEvent"] ?? "",
                                engineStopEvent = (string)m["engineStopEvent"] ?? "",
                                soundFile = (string)m["soundFile"] ?? "",
                                soundStartFile = (string)m["soundStartFile"] ?? "",
                                soundStopFile = (string)m["soundStopFile"] ?? "",
                                soundVolume = m["soundVolume"] != null ? (float)m["soundVolume"] : 1f,
                                soundStartVolume = m["soundStartVolume"] != null ? (float)m["soundStartVolume"] : 1f,
                                soundStopVolume = m["soundStopVolume"] != null ? (float)m["soundStopVolume"] : 1f,
                                deployPoseTime = m["deployPoseTime"] != null ? (float)m["deployPoseTime"] : 1f,
                                deploySpeed = m["deploySpeed"] != null ? (float)m["deploySpeed"] : 1f,
                                recoilSpeed = m["recoilSpeed"] != null ? (float)m["recoilSpeed"] : 1f,
                            });
                        }
                        Plugin.Log.LogInfo($"[Uni] parsed {entries.Count} model(s) via Newtonsoft [" + string.Join(", ", entries.Select(e => e.resourceName + "->" + e.pawnDescription)) + "]");
                        return entries;
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
                var eng = Regex.Matches(text, "\"engineSound\"\\s*:\\s*(true|false)");      // parity: fire the per-ship engine move sound on our units
                var esa = Regex.Matches(text, "\"engineStartEvent\"\\s*:\\s*\"([^\"]*)\"");  // parity: Wwise event name posted on move-start
                var eso = Regex.Matches(text, "\"engineStopEvent\"\\s*:\\s*\"([^\"]*)\"");    // parity: Wwise event name posted on move-stop
                var sf = Regex.Matches(text, "\"soundFile\"\\s*:\\s*\"([^\"]*)\"");           // parity: custom WAV loop in enc_sounds/
                var sfa = Regex.Matches(text, "\"soundStartFile\"\\s*:\\s*\"([^\"]*)\"");     // parity: custom WAV one-shot on move-start
                var sfo = Regex.Matches(text, "\"soundStopFile\"\\s*:\\s*\"([^\"]*)\"");      // parity: custom WAV one-shot on move-stop
                var svl = Regex.Matches(text, "\"soundVolume\"\\s*:\\s*(-?[\\d.eE+]+)");       // parity: travel-loop volume
                var svs = Regex.Matches(text, "\"soundStartVolume\"\\s*:\\s*(-?[\\d.eE+]+)");  // parity: start one-shot volume
                var svp = Regex.Matches(text, "\"soundStopVolume\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: stop one-shot volume
                var dpt = Regex.Matches(text, "\"deployPoseTime\"\\s*:\\s*(-?[\\d.eE+]+)"); // parity: normalized clip time of the deployed pose (default 1)
                var dsp = Regex.Matches(text, "\"deploySpeed\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: gradual-deploy ramp speed multiplier (default 1)
                var rsp = Regex.Matches(text, "\"recoilSpeed\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: recoil-on-fire playback speed multiplier (default 1)
                var sc = Regex.Matches(text, "\"scale\"\\s*:\\s*(-?[\\d.eE+]+)");           // runtime ObjectSpace scale multiplier (default 1)
                var des = Regex.Matches(text, "\"desaturate\"\\s*:\\s*(-?[\\d.eE+]+)");     // texture-only grey strength (default 0 = off)
                var tR = Regex.Matches(text, "\"tintR\"\\s*:\\s*(-?[\\d.eE+]+)");           // universal skin colour offset R (-255..255)
                var tG = Regex.Matches(text, "\"tintG\"\\s*:\\s*(-?[\\d.eE+]+)");           // ... G
                var tB = Regex.Matches(text, "\"tintB\"\\s*:\\s*(-?[\\d.eE+]+)");           // ... B
                var txf = Regex.Matches(text, "\"textureFile\"\\s*:\\s*\"([^\"]*)\"");      // texture-only retexture: PNG filename in enc_skins/
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
                        engineSound = i < eng.Count && eng[i].Groups[1].Value == "true",
                        engineStartEvent = i < esa.Count ? esa[i].Groups[1].Value : "",
                        engineStopEvent = i < eso.Count ? eso[i].Groups[1].Value : "",
                        soundFile = i < sf.Count ? sf[i].Groups[1].Value : "",
                        soundStartFile = i < sfa.Count ? sfa[i].Groups[1].Value : "",
                        soundStopFile = i < sfo.Count ? sfo[i].Groups[1].Value : "",
                        soundVolume = i < svl.Count && float.TryParse(svl[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svl) ? _svl : 1f,
                        soundStartVolume = i < svs.Count && float.TryParse(svs[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svs) ? _svs : 1f,
                        soundStopVolume = i < svp.Count && float.TryParse(svp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svp) ? _svp : 1f,
                        deployPoseTime = i < dpt.Count && float.TryParse(dpt[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _dpt) ? _dpt : 1f,
                        deploySpeed = i < dsp.Count && float.TryParse(dsp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _dsp) ? _dsp : 1f,
                        recoilSpeed = i < rsp.Count && float.TryParse(rsp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _rsp) ? _rsp : 1f,
                        scale = i < sc.Count && float.TryParse(sc[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sc) ? _sc : 1f,
                        desaturate = i < des.Count && float.TryParse(des[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _des) ? _des : 0f,
                        tintR = i < tR.Count && float.TryParse(tR[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tr) ? _tr : 0f,
                        tintG = i < tG.Count && float.TryParse(tG[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tg) ? _tg : 0f,
                        tintB = i < tB.Count && float.TryParse(tB[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tb) ? _tb : 0f,
                        textureFile = i < txf.Count ? txf[i].Groups[1].Value : "",
                    });
                }
                Plugin.Log.LogInfo($"[Uni] read {text.Length} chars; parsed {entries.Count} model(s) via regex [" + string.Join(", ", entries.Select(e => e.resourceName + "->" + e.pawnDescription)) + "]");
                return entries;
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

                // TEXTURE-ONLY override: keep the vanilla mesh, just isolate this unit's output layer and repaint its skin
                // — either a hot-loaded PNG (textureFile) or a desaturated copy of its own atlas (desaturate). Returns
                // before any skeleton repoint. The isolation leaves the emblematic original untouched (shared layer).
                if (NeedsAdjust(e) || !string.IsNullOrEmpty(e.textureFile)) { ApplyTextureOnly(addon, animMgr, e, name); return; }

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

        // ---- TEXTURE-ONLY override: keep the vanilla mesh, repaint the isolated skin (custom PNG or desaturate) ----

        static void ApplyTextureOnly(object addon, object animMgr, ModelEntry e, string name)
        {
            try
            {
                var bodyName = DiscoverBodyMeshName(addon);
                if (!string.IsNullOrEmpty(bodyName)) e.layerHint = bodyName;
                // Custom skin PNG takes precedence; otherwise the desaturated original is built in GreyIsolate/TickOne.
                if (!string.IsNullOrEmpty(e.textureFile) && e.tex == null)
                {
                    e.tex = LoadSkinPng(e.textureFile, e.resourceName);
                    if (e.tex != null && NeedsAdjust(e)) AdjustSkin(e.tex, e.desaturate, e.tintR, e.tintG, e.tintB);   // desaturate/tint the loaded PNG too
                }
                GreyIsolate(addon, animMgr, e);    // clone the body fragment's output layer (+ build the desaturated atlas if desaturate>0)
                ApplyTexture(e, animMgr);          // paint e.tex on the isolated clone + neutralise the civ-colour/overlay maps (TickOne)
                if (!e.repointed) { e.repointed = true; Plugin.Log.LogInfo($"[Skin] '{name}' -> {e.resourceName}: {(string.IsNullOrEmpty(e.textureFile) ? $"greyed (desaturate {e.desaturate:0.00})" : $"retextured ('{e.textureFile}')")}, layer '{e.layerHint}', atlas={(e.tex != null ? e.tex.width + "x" + e.tex.height : "NONE — will retry")}"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Skin] " + ex); }
        }

        // Load a retexture PNG from BepInEx/config/enc_skins/<file> into a Texture2D (needs ImageConversionModule.LoadImage).
        static UnityEngine.Texture2D LoadSkinPng(string file, string tag)
        {
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "enc_skins", file);
                if (!File.Exists(path)) { Plugin.Log.LogWarning($"[Skin] {tag}: retexture file not found: {path}"); return null; }
                var t = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false) { name = tag + "_Skin" };
                if (!UnityEngine.ImageConversion.LoadImage(t, File.ReadAllBytes(path))) { Plugin.Log.LogWarning($"[Skin] {tag}: LoadImage failed for {file} (not a PNG/JPG?)"); UnityEngine.Object.DestroyImmediate(t); return null; }
                Plugin.Log.LogInfo($"[Skin] {tag}: loaded retexture '{file}' ({t.width}x{t.height})");
                return t;
            }
            catch (Exception e) { Plugin.Log.LogError($"[Skin] {tag}: retexture load failed: " + e); return null; }
        }

        // Isolate the copy's body-fragment output layer (a private clone, so the shared emblematic original is untouched)
        // and build a desaturated atlas from that layer's CURRENT skin into e.tex. Keeps the vanilla meshCollection
        // (texture-only). Mirrors ReloadFragments' isolation, minus the mesh repoint.
        static void GreyIsolate(object addon, object animMgr, ModelEntry e)
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
                if (folField == null) return;
                for (int i = 0; i < frags.Length; i++)
                {
                    var item = frags.GetValue(i);
                    if (item == null) continue;
                    var mn = mnField?.GetValue(item) as string;
                    if (string.IsNullOrEmpty(e.layerHint) || mn != e.layerHint) continue;   // only the body layer
                    var host = folField.GetValue(item);
                    if (e.tex == null && host != null && NeedsAdjust(e)) e.tex = BuildAdjustedAtlas(host, e.desaturate, e.tintR, e.tintG, e.tintB, e.resourceName);   // adjust the ORIGINAL skin, once (skipped when a custom PNG already set e.tex)
                    if (e.isolatedLayer == null && host is UnityEngine.Object ho && ho != null)
                    {
                        var clone = UnityEngine.Object.Instantiate(ho); clone.name = e.resourceName + "_GreyLayer"; e.isolatedLayer = clone;
                        Plugin.Log.LogInfo($"[Grey] cloned output layer for {e.resourceName} -> '{clone.name}'");
                    }
                    if (e.isolatedLayer != null) folField.SetValue(item, e.isolatedLayer);
                    var mc = mcField?.GetValue(item);   // KEEP the vanilla meshCollection; re-Load so the clone gets its own GPU slot
                    try { load?.Invoke(item, new object[] { mc, renderer, mcm, layer }); }
                    catch (Exception ex) { Plugin.Log.LogWarning("[Grey] frag reload: " + (ex.InnerException ?? ex).Message); }
                    frags.SetValue(item, i);
                    break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Grey] isolate: " + ex); }
        }

        // Read the output layer's current _MainTex and return an ADJUSTED copy (AdjustSkin: desaturate toward luminance by
        // `desat`, then add the R/G/B colour offset -255..+255). Blits through a RenderTexture first (the host atlas isn't
        // CPU-readable). The civ-colour tint is killed separately by TickOne (_ColorMask -> black).
        static UnityEngine.Texture2D BuildAdjustedAtlas(object hostLayer, float desat, float tR, float tG, float tB, string tag)
        {
            try
            {
                UnityEngine.Texture src = null;
                if (GetMember(hostLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                    {
                        foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial" })
                            if (GetMember(ro, fld) is UnityEngine.Material mat && mat.GetTexture("_MainTex") is UnityEngine.Texture mt) { src = mt; break; }
                        if (src != null) break;
                    }
                if (src == null) { Plugin.Log.LogWarning($"[Grey] {tag}: no _MainTex on the output layer yet"); return null; }
                int w = src.width, h = src.height;
                var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                var prev = UnityEngine.RenderTexture.active;
                UnityEngine.Graphics.Blit(src, rt);
                UnityEngine.RenderTexture.active = rt;
                var t = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false) { name = tag + "_Grey" };
                t.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0); t.Apply();
                UnityEngine.RenderTexture.active = prev; UnityEngine.RenderTexture.ReleaseTemporary(rt);
                AdjustSkin(t, desat, tR, tG, tB);
                Plugin.Log.LogInfo($"[Grey] {tag}: adjusted atlas {w}x{h} (desat {UnityEngine.Mathf.Clamp01(desat):0.00}, rgb {tR:+0;-0;0}/{tG:+0;-0;0}/{tB:+0;-0;0})");
                return t;
            }
            catch (Exception e) { Plugin.Log.LogError("[Grey] build atlas: " + e); return null; }
        }

        // Apply the universal skin adjustments in place: pull each pixel toward its luminance by `desat` (1 = full grey),
        // then add the per-channel colour offset tR/tG/tB (-255..+255). Shared by the own-atlas path and the PNG path.
        static void AdjustSkin(UnityEngine.Texture2D t, float desat, float tR, float tG, float tB)
        {
            var px = t.GetPixels32();
            float s = UnityEngine.Mathf.Clamp01(desat);
            for (int i = 0; i < px.Length; i++)
            {
                float lum = px[i].r * 0.299f + px[i].g * 0.587f + px[i].b * 0.114f;
                px[i].r = (byte)UnityEngine.Mathf.Clamp((px[i].r + (lum - px[i].r) * s) + tR, 0, 255);
                px[i].g = (byte)UnityEngine.Mathf.Clamp((px[i].g + (lum - px[i].g) * s) + tG, 0, 255);
                px[i].b = (byte)UnityEngine.Mathf.Clamp((px[i].b + (lum - px[i].b) * s) + tB, 0, 255);
            }
            t.SetPixels32(px); t.Apply();
        }

        // True if this entry carries any texture adjustment (desaturate or a non-zero colour offset).
        static bool NeedsAdjust(ModelEntry e) => e.desaturate > 0f || e.tintR != 0f || e.tintG != 0f || e.tintB != 0f;

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
            // The AIM layer is cleared only for the ARTILLERY behaviors (it twists the howitzer's barrel as the game
            // aims). For other animated models the game's procedural bone-rotation layers are left alone — clearing
            // them is suspected of pinning the Combine soldier to one fixed compass facing (the game may turn pawns
            // through that layer). Diagnostics below record what the layer + pawn rotation actually do while moving.
            if (e.fireOnAttack || e.deployOnStop) ClearAimLayer(entry);
            ApplyPositionOffset(e, entry);
            ApplyScale(e, entry);
            ctx.pawnEntries.SetValue(entry, ctx.idx);
            LogPoseHookOnce(ctx, e, pose0);
            DebugFacingPeriodic(e, entry);
        }

        // TEMP FACING DIAGNOSTIC (fixed-compass soldier): every ~3s, log the pawn's ObjectSpace rotation and the
        // BoneRotation layer angles for one animated pawn. Moving the unit in different directions then tells us:
        // R changing but facing fixed => the renderer ignores pawn rotation for this rig; R constant => the game
        // never turns this pawn; BoneRotation angles nonzero => the game steers through the procedural layer.
        static float dbgFacingNext;
        static void DebugFacingPeriodic(ModelEntry e, object entry)
        {
            if (UnityEngine.Time.time < dbgFacingNext) return;
            dbgFacingNext = UnityEngine.Time.time + 3f;
            var os = GetMember(entry, "ObjectSpace");
            string br = "";
            for (int i = 0; i < 4; i++)
            {
                var b = GetMember(entry, "BoneRotation" + i);
                if (b == null) continue;
                br += $" [{i}] bone={GetMember(b, "SkeletonBoneIndex")} axis={GetMember(b, "AxisIndex")} angle={GetMember(b, "Angle")}";
            }
            Plugin.Log.LogInfo($"[Uni][facing] '{e.resourceName}' R={GetMember(os, "Rotation")} T={GetMember(os, "Translation")}{br}");
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

        // Runtime position offset. Static models bake position into the mesh — i.e. in the MODEL'S OWN FRAME, so it
        // turns with the unit. The animated path can't bake it, so it must match that semantic here: rotate the
        // planar (x = sideways, y = fore/aft) part by the pawn's FACING before adding. The old code added constant
        // WORLD axes, which pointed a fixed compass direction — the offset visibly drifted around the model as the
        // unit turned ("works for static, inconsistent in all directions when moving" bug). z (height) stays world-up
        // (Y). Re-applied each frame on the game's fresh world position, so it never accumulates. Logged once.
        static void ApplyPositionOffset(ModelEntry e, object entry)
        {
            if (e.position == UnityEngine.Vector3.zero) return;
            var os = GetMember(entry, "ObjectSpace");                  // boxed TRS
            var tr = (UnityEngine.Vector3)GetMember(os, "Translation");
            var planar = new UnityEngine.Vector3(e.position.x, 0f, e.position.y);   // registry y (fore/aft) -> local Z
            bool rotated = TryQuaternion(GetMember(os, "Rotation"), out var rot);
            if (rotated) planar = rot * planar;                        // pawn frame; else fall back to world axes
            tr += planar; tr.y += e.position.z;                        // registry z (height) -> world Y (up)
            if (!posLogged) { posLogged = true; Plugin.Log.LogInfo($"[Uni] {e.resourceName} position offset {e.position} applied in {(rotated ? "PAWN frame (turns with the unit)" : "world axes (Rotation unreadable)")}, z->up Y"); }
            SetMember(os, "Translation", tr);
            SetMember(entry, "ObjectSpace", os);
        }

        // ObjectSpace.Rotation as a UnityEngine.Quaternion: it may BE one, or an Amplitude quaternion type with the
        // same x/y/z/w layout — read the components reflectively in that case. False (identity) if unreadable.
        static bool TryQuaternion(object o, out UnityEngine.Quaternion q)
        {
            if (o is UnityEngine.Quaternion uq) { q = uq; return true; }
            try
            {
                q = new UnityEngine.Quaternion(
                    Convert.ToSingle(GetMember(o, "x")), Convert.ToSingle(GetMember(o, "y")),
                    Convert.ToSingle(GetMember(o, "z")), Convert.ToSingle(GetMember(o, "w")));
                return true;
            }
            catch { q = UnityEngine.Quaternion.identity; return false; }
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
            // GREY retry: if the skin wasn't ready when ApplyGrey ran (build returned null), build it now from the
            // isolated layer's still-original _MainTex. Runs at most until the first successful build (then e.tex latches).
            if (NeedsAdjust(e) && e.tex == null && e.isolatedLayer != null)
                e.tex = BuildAdjustedAtlas(e.isolatedLayer, e.desaturate, e.tintR, e.tintG, e.tintB, e.resourceName);
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
        // ---- AUDIO PROBE (step 1 diagnostic) ----
        // Walk every live PresentationSubPawn and log its audio wiring, so we can see WHY custom/retextured units are
        // silent. The decompile says movement/engine sound is posted to an AudioEmitter component on the sub-pawn
        // (NOT the material/mesh) from PresentationPawnDescription.IdleAudioEvent at spawn. This dumps, per sub-pawn:
        // does the AudioEmitter exist? is it registered (EntityName/Name)? is an IdleAudioEvent actually set? — so we can
        // compare a working unit (emblematic corvette: idleEvent SET) against a silent one (our copy: expected empty).
        // Bound to the F8 window; reuses the atlas-dump name filter (e.g. "Corvette" shows both corvettes side by side).
        public static void DumpAudioState(string filter = null)
        {
            try
            {
                var spType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationSubPawn");
                if (spType == null) { Plugin.Log.LogError("[Audio] PresentationSubPawn type not found (game update?)"); return; }
                var holderType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationUnitHolder");
                var all = UnityEngine.Object.FindObjectsOfType(spType);
                Plugin.Log.LogInfo($"[Audio] --- audio probe: {all.Length} sub-pawns in scene (filter='{filter}') ---");
                int shown = 0;
                foreach (var sp in all)
                {
                    var go = (sp as UnityEngine.Component) != null ? ((UnityEngine.Component)sp).gameObject : null;
                    string goName = go != null ? go.name : "?";
                    var desc = GetMember(sp, "PresentationPawnDescription");
                    string descName = desc is UnityEngine.Object od && od != null ? od.name : "(null desc)";
                    var emitter = GetMember(sp, "AudioEmitter");
                    string entityName = emitter != null ? GetMember(emitter, "EntityName") as string : null;
                    string regName = emitter != null ? GetMember(emitter, "Name") as string : null;
                    string descAudioName = desc != null ? GetMember(desc, "AudioEntityName") as string : null;

                    string hay = goName + " | " + descName + " | " + entityName;
                    if (!string.IsNullOrEmpty(filter) && hay.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // Is an IdleAudioEvent set? IdleAudioEvent is an AudioEventHandleReference struct; .Value resolves the
                    // handle (null when the guid is zero / bank not loaded). This is the engine/idle loop source.
                    string idle = "n/a";
                    if (desc != null)
                    {
                        var idleRef = GetMember(desc, "IdleAudioEvent");
                        if (idleRef == null) idle = "no-field";
                        else { object v = null; try { v = GetMember(idleRef, "Value"); } catch { } idle = v != null ? "SET" : "empty"; }
                    }

                    // Layer C: movement free-event SFX readiness (walk/jog/run hashes populated from the animation).
                    int freeCount = -1;
                    var fh = GetMember(sp, "FreeEventHashes");
                    if (fh != null && GetMember(fh, "Count") is int fc) freeCount = fc;

                    // Is this sub-pawn's emitter actually REGISTERED with Wwise? A present component != registered; an
                    // unregistered emitter silently no-ops every PostEvent. AudioEntityGUID.IsValid == registered.
                    string reg = "n/a";
                    if (emitter != null) { var g = GetMember(emitter, "AudioEntityGUID"); if (g != null && GetMember(g, "IsValid") is bool bv) reg = bv ? "REG" : "unreg"; }

                    // Emitter 3D position vs the unit's actual position — a big gap = the emitter tracks a stale transform
                    // (our re-load), so its sound plays away from the ship and is inaudible even when the right event posts.
                    string pos = "?";
                    if (emitter != null)
                    {
                        var ep = GetMember(emitter, "Position");
                        var tr = GetMember(sp, "Transform") as UnityEngine.Transform;
                        var tp = tr != null ? tr.position : UnityEngine.Vector3.zero;
                        if (ep is UnityEngine.Vector3 epv) pos = $"emit({epv.x:0.0},{epv.z:0.0}) unit({tp.x:0.0},{tp.z:0.0}) d={(epv - tp).magnitude:0.0}";
                    }

                    int eid = emitter is UnityEngine.Object eo2 && eo2 != null ? eo2.GetInstanceID() : 0;
                    // The game posts the engine to our emitters at the right position, yet silent — so check whether the
                    // emitter component actually RUNS (its Update pushes position/speed to Wwise; disabled/inactive => the
                    // sound stays parked at the origin, far from the listener = inaudible).
                    string act = "?";
                    if (emitter is UnityEngine.Behaviour beh)
                        act = $"en={beh.enabled} actHier={(beh.gameObject != null && beh.gameObject.activeInHierarchy)} actSelf={(beh.gameObject != null && beh.gameObject.activeSelf)}";
                    Plugin.Log.LogInfo($"[Audio] '{goName}' emitter={(emitter != null ? "YES" : "NULL")} id={eid} reg={reg} {act} " +
                                       $"idleEvent={idle} freeEvents={freeCount} pos=[{pos}]");
                    shown++;
                }
                // Layer B: the unit-holder move RUMBLE (generic, posted on move to the HOLDER's own emitter). Holders
                // aren't transform-parents of sub-pawns, so sample them directly. Rumble config is generic, so a few
                // samples tell us if Layer B is set up at all and whether holder emitters register.
                if (holderType != null)
                {
                    var holders = UnityEngine.Object.FindObjectsOfType(holderType);
                    int hi = 0;
                    foreach (var h in holders)
                    {
                        var hemit = GetMember(h, "audioEmitter");
                        string hreg = "n/a";
                        if (hemit != null) { var g = GetMember(hemit, "AudioEntityGUID"); if (g != null && GetMember(g, "IsValid") is bool hb) hreg = hb ? "REG" : "unreg"; }
                        object playV = null; var play = GetMember(h, "playRumbleAudioEvent");
                        if (play != null) { try { playV = GetMember(play, "Value"); } catch { } }
                        Plugin.Log.LogInfo($"[Audio] holder[{hi}] {h.GetType().Name} emitter={(hemit != null ? "YES" : "NULL")} reg={hreg} rumble={(playV != null ? "SET" : "empty")}");
                        if (++hi >= 8) break;
                    }
                    Plugin.Log.LogInfo($"[Audio] total holders in scene: {holders.Length}");
                }
                Plugin.Log.LogInfo($"[Audio] --- probe done: {shown} shown of {all.Length} (emitter reg=REG/unreg, idle/free events, holder rumble) ---");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] DumpAudioState: " + ex); }
        }

        // ---- AUDIO: post a harvested engine/rumble event onto our units' emitters, so we can HEAR something ----
        // Foundation for the from-scratch audio feature. Static config is byte-identical between the audible vanilla
        // unit and our silent copies, so instead of un-breaking the vanilla move-trigger we DRIVE the sound ourselves:
        // harvest one live, registered move-rumble AudioEventHandle (every holder carries one), and PostEvent it straight
        // onto each matched sub-pawn's AudioEmitter (which is present + registered). If audible, we own unit audio and
        // can wire play-on-move / stop-on-idle next. NOTE: rumble is a LOOP — each click stacks another until we add Stop.
        // Live audio trace (Hk_AudioTrace patches Wwise PostEvent; gated here so it's free until toggled on in F8).
        public static bool AudioTraceOn;
        public static string AudioTraceFilter = "";
        public static object StashedEngineHandle;   // live 'Play_UNIT_Vehicles_ModernBoat_Idle' AudioEventHandle, auto-captured by Hk_AudioTrace
        public static object StashedLoudHandle;      // the per-ship engine MOVE-START handle (Play_UNIT_Vehicles_<Type>_Start), auto-captured
        public static object StashedStopHandle;      // the matching MOVE-STOP handle (..._Stop), auto-captured
        public static string StashedLoudName = "";
        public static readonly System.Collections.Generic.HashSet<string> SeenEvents = new System.Collections.Generic.HashSet<string>();
        public static string EmitterName(object emitter) =>
            (GetMember(emitter, "EntityName") as string) ?? (GetMember(emitter, "Name") as string) ?? emitter?.GetType().Name ?? "?";

        static System.Reflection.MethodInfo _postEvent;
        public static void PlayAudioTest(string filter = null)
        {
            try
            {
                // Post the REAL vehicle engine-idle event (captured live by Hk_AudioTrace) onto each matched sub-pawn's
                // emitter. This is what the trace proved the game itself posts to the audible boats.
                var handle = StashedLoudHandle ?? StashedEngineHandle;
                string which = StashedLoudHandle != null ? StashedLoudName : ((StashedEngineHandle as UnityEngine.Object)?.name ?? "engine");
                if (handle == null) { Plugin.Log.LogError("[Audio] nothing captured — turn Audio Trace ON and give a unit a MOVE ORDER (captures a recognizable sound), then retry."); return; }

                var spType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationSubPawn");
                int posted = 0;
                foreach (var sp in UnityEngine.Object.FindObjectsOfType(spType))
                {
                    var desc = GetMember(sp, "PresentationPawnDescription");
                    string nm = ((sp as UnityEngine.Component) != null ? ((UnityEngine.Component)sp).gameObject.name : "") +
                                " " + (desc is UnityEngine.Object od && od != null ? od.name : "");
                    if (!string.IsNullOrEmpty(filter) && nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var emitter = GetMember(sp, "AudioEmitter");
                    if (emitter == null) continue;
                    if (_postEvent == null)
                        _postEvent = emitter.GetType().GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "AudioEventHandle");
                    if (_postEvent == null) { Plugin.Log.LogError("[Audio] PostEvent(AudioEventHandle) not found on emitter"); return; }
                    try { _postEvent.Invoke(emitter, new[] { handle }); posted++; }
                    catch (Exception ie) { Plugin.Log.LogWarning("[Audio] engine post: " + (ie.InnerException ?? ie).Message); }
                }
                Plugin.Log.LogInfo($"[Audio] posted '{which}' to {posted} matched sub-pawn emitter(s) (filter='{filter}'). LISTEN.");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] PlayAudioTest: " + ex); }
        }

        // ---- ENGINE AUDIO driver: fire the per-ship move Start/Stop sound on our units, which never trigger it themselves.
        // Polls each of our engineSound units' sub-pawns; on a movement TRANSITION (render-position delta, like deployOnStop)
        // it PostEvents the captured Start (begin) / Stop (end) AudioEventHandle onto that pawn's emitter. The Start/Stop
        // handles are auto-captured from any vehicle move by Hk_AudioTrace, so they're ready once any boat has moved once.
        static int engineFrame;
        static bool _listenerChecked;
        static List<KeyValuePair<UnityEngine.Object, ModelEntry>> _ourSubpawns;   // cached OUR-units' sub-pawns (refreshed ~every 2s)
        static float _spCacheAt;
        public static void ProcessEngineAudio()
        {
            if (entries == null || !Plugin.UniversalInjectOn.Value) return;
            var on = entries.Where(x => (x.engineSound || !string.IsNullOrEmpty(x.soundFile) || !string.IsNullOrEmpty(x.soundStartFile) || !string.IsNullOrEmpty(x.soundStopFile)) && !string.IsNullOrEmpty(x.pawnDescription)).ToList();
            if (on.Count == 0) return;
            if (++engineFrame % 6 != 0) return;   // ~10x/s (render-position delta = movement)
            try
            {
                // Lazy-load custom WAV clips + make sure a Unity AudioListener exists (a Wwise game may have none, which
                // would make our AudioSources silent).
                bool anyCustom = false;
                foreach (var e in on)
                {
                    if (string.IsNullOrEmpty(e.soundFile) && string.IsNullOrEmpty(e.soundStartFile) && string.IsNullOrEmpty(e.soundStopFile)) continue;
                    anyCustom = true;
                    if (!e.customClipTried)
                    {
                        e.customClipTried = true;
                        e.customClip = LoadCustom(e.soundFile, e.resourceName);
                        e.customStartClip = LoadCustom(e.soundStartFile, e.resourceName + "_start");
                        e.customStopClip = LoadCustom(e.soundStopFile, e.resourceName + "_stop");
                    }
                }
                if (anyCustom) EnsureAudioListener();

                var spType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationSubPawn");
                if (spType == null) return;
                // FindObjectsOfType is a FULL scene scan — running it every poll causes periodic frame spikes (bad 1% lows).
                // Do it (and the name-match) only every ~2s, caching just OUR units' sub-pawns; each poll then touches only
                // those few. New units appear within ~2s; destroyed ones read as Unity fake-null and are skipped.
                float now = UnityEngine.Time.time;
                if (_ourSubpawns == null || now - _spCacheAt > 2f)
                {
                    _ourSubpawns = new List<KeyValuePair<UnityEngine.Object, ModelEntry>>();
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(spType))
                    {
                        if (!(o is UnityEngine.Component c) || c == null) continue;
                        var m = on.FirstOrDefault(x => c.gameObject.name.IndexOf(x.pawnDescription, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (m != null) _ourSubpawns.Add(new KeyValuePair<UnityEngine.Object, ModelEntry>(o, m));
                    }
                    _spCacheAt = now;
                }
                foreach (var pair in _ourSubpawns)
                {
                    var sp = pair.Key; var e = pair.Value;
                    if (sp == null) continue;   // destroyed since the last refresh
                    var comp = sp as UnityEngine.Component;
                    int id = sp.GetInstanceID();
                    var pos = (GetMember(sp, "Transform") as UnityEngine.Transform)?.position ?? comp.transform.position;
                    bool moving = e.engineLastPos.TryGetValue(id, out var last) && (pos - last).sqrMagnitude > 0.06f * 0.06f;
                    e.engineLastPos[id] = pos;
                    bool wasMoving = e.engineMoving.TryGetValue(id, out var wm) && wm;

                    // custom one-shots on a move-start / move-stop TRANSITION (spool-up / spool-down), 3D at the unit.
                    if (moving != wasMoving)
                    {
                        var oneShot = moving ? e.customStartClip : e.customStopClip;
                        if (oneShot != null)
                        {
                            UnityEngine.AudioSource.PlayClipAtPoint(oneShot, pos, moving ? e.soundStartVolume : e.soundStopVolume);
                            if (moving) e.loopHoldUntil[id] = now + oneShot.length;   // hold the loop off so it doesn't mask the spool-up
                        }
                    }

                    // (A) custom WAV: a Unity AudioSource looped WHILE MOVING, paused when stopped (and held off during any spool-up)
                    if (e.customClip != null)
                    {
                        if (!e.customSources.TryGetValue(id, out var src) || src == null)
                        {
                            src = comp.gameObject.AddComponent<UnityEngine.AudioSource>();
                            src.clip = e.customClip; src.loop = true; src.playOnAwake = false; src.spatialBlend = 1f;
                            src.minDistance = 6f; src.maxDistance = 220f; src.rolloffMode = UnityEngine.AudioRolloffMode.Linear;
                            e.customSources[id] = src;
                        }
                        src.volume = e.soundVolume;   // live-configurable travel-loop volume
                        bool held = e.loopHoldUntil.TryGetValue(id, out var until) && now < until;
                        if (moving && !held && !src.isPlaying) src.Play();
                        else if (!moving && src.isPlaying) src.Pause();
                    }

                    // (B) Wwise engine event: post Start/Stop on a movement TRANSITION
                    var emitter = GetMember(sp, "AudioEmitter");
                    if (e.engineSound && emitter != null && moving != wasMoving)
                    {
                        string evName = moving ? e.engineStartEvent : e.engineStopEvent;
                        if (!string.IsNullOrEmpty(evName)) PostEventByName(emitter, evName);   // BY NAME — first-unit-safe
                        else
                        {
                            if (_postEvent == null)
                                _postEvent = emitter.GetType().GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "AudioEventHandle");
                            var handle = moving ? StashedLoudHandle : StashedStopHandle;
                            if (_postEvent != null && handle != null) try { _postEvent.Invoke(emitter, new[] { handle }); } catch { }
                        }
                    }
                    e.engineMoving[id] = moving;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] ProcessEngineAudio: " + ex); }
        }

        // On-demand test: play a loaded custom WAV as a 2D (non-positional) full-volume sound, and report the Unity
        // AudioListener state. Isolates "can Unity play ANY sound in this Wwise game?" from the 3D/movement path. If this
        // is silent while AudioListener.volume reads 0, the game has muted Unity's audio bus (Wwise-only) and we can't use
        // AudioSource for custom files without forcing that back.
        static UnityEngine.AudioSource _testSrc;
        public static void PlaySoundTest()
        {
            try
            {
                var clip = entries?.FirstOrDefault(e => e.customClip != null)?.customClip;
                if (clip == null)
                {
                    var p = Path.Combine(Paths.ConfigPath, "enc_sounds", "drone.wav");
                    if (File.Exists(p)) clip = LoadWav(p, "test");
                }
                if (clip == null) { Plugin.Log.LogError("[Sound] test: no WAV loaded and no enc_sounds/drone.wav to fall back on"); return; }
                EnsureAudioListener();
                float ov = UnityEngine.AudioListener.volume; bool op = UnityEngine.AudioListener.pause;
                UnityEngine.AudioListener.volume = 1f; UnityEngine.AudioListener.pause = false;   // force, in case the game muted Unity's bus
                bool haveListener = UnityEngine.Object.FindObjectOfType<UnityEngine.AudioListener>() != null;
                if (_testSrc == null)
                {
                    var go = new UnityEngine.GameObject("ENC_SoundTest");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _testSrc = go.AddComponent<UnityEngine.AudioSource>();
                    _testSrc.spatialBlend = 0f; _testSrc.volume = 1f; _testSrc.loop = false;
                }
                _testSrc.clip = clip; _testSrc.Play();
                Plugin.Log.LogInfo($"[Sound] test: playing '{clip.name}' 2D @full volume. AudioListener: present={haveListener}, volume was {ov} (forced 1), pause was {op} (forced false).");
            }
            catch (Exception e) { Plugin.Log.LogError("[Sound] PlaySoundTest: " + e); }
        }

        // A Unity AudioListener is required for our AudioSources to be audible; a Wwise-driven game may ship without one.
        static void EnsureAudioListener()
        {
            if (_listenerChecked) return; _listenerChecked = true;
            try
            {
                if (UnityEngine.Object.FindObjectOfType<UnityEngine.AudioListener>() != null) return;
                var cam = UnityEngine.Camera.main;
                var go = cam != null ? cam.gameObject : new UnityEngine.GameObject("ENC_AudioListener");
                go.AddComponent<UnityEngine.AudioListener>();
                Plugin.Log.LogInfo("[Sound] no Unity AudioListener found — added one to '" + go.name + "'");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Sound] EnsureAudioListener: " + e.Message); }
        }

        // Load a custom WAV from enc_sounds/<file> (null if unset/missing).
        static UnityEngine.AudioClip LoadCustom(string file, string tag)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var p = Path.Combine(Paths.ConfigPath, "enc_sounds", file);
            if (!File.Exists(p)) { Plugin.Log.LogWarning($"[Sound] file not found: {p}"); return null; }
            return LoadWav(p, tag);
        }

        // Load a PCM/float WAV file into an AudioClip (no Wwise, no coroutine). Handles 8/16/24/32-bit PCM + 32-bit float.
        static UnityEngine.AudioClip LoadWav(string path, string tag)
        {
            try
            {
                var b = File.ReadAllBytes(path);
                if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') { Plugin.Log.LogWarning("[Sound] not a WAV (need PCM WAV, not mp3/ogg): " + path); return null; }
                int channels = 1, rate = 44100, bits = 16, fmt = 1, dataOff = -1, dataLen = 0, p = 12;
                while (p + 8 <= b.Length)
                {
                    string cid = System.Text.Encoding.ASCII.GetString(b, p, 4);
                    int csz = BitConverter.ToInt32(b, p + 4);
                    if (cid == "fmt ") { fmt = BitConverter.ToInt16(b, p + 8); channels = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToInt16(b, p + 22); }
                    else if (cid == "data") { dataOff = p + 8; dataLen = Math.Min(csz, b.Length - (p + 8)); break; }
                    p += 8 + csz + (csz & 1);
                }
                if (dataOff < 0 || channels < 1) { Plugin.Log.LogWarning("[Sound] no data/fmt chunk: " + path); return null; }
                int bps = bits / 8, n = dataLen / bps;
                var f = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int o = dataOff + i * bps;
                    if (fmt == 3 && bits == 32) f[i] = BitConverter.ToSingle(b, o);
                    else if (bits == 16) f[i] = BitConverter.ToInt16(b, o) / 32768f;
                    else if (bits == 32) f[i] = BitConverter.ToInt32(b, o) / 2147483648f;
                    else if (bits == 24) { int v = b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16); f[i] = v / 8388608f; }
                    else if (bits == 8) f[i] = (b[o] - 128) / 128f;
                }
                var clip = UnityEngine.AudioClip.Create(tag, n / channels, channels, rate, false);
                clip.SetData(f, 0);
                Plugin.Log.LogInfo($"[Sound] loaded WAV '{tag}' ({channels}ch {rate}Hz {bits}bit, {(n / (float)channels / rate):0.0}s)");
                return clip;
            }
            catch (Exception e) { Plugin.Log.LogError("[Sound] LoadWav '" + path + "': " + e); return null; }
        }

        // Post a Wwise event BY NAME onto a unit emitter (AkSoundEngine.PostEvent(name, gameObjectID)). Needs no captured
        // handle, so a named engine sound plays for the FIRST unit at load. The emitter's Wwise game-object id is its
        // AudioEntityGUID (a ulong).
        static System.Reflection.MethodInfo _postByName;
        static void PostEventByName(object emitter, string eventName)
        {
            try
            {
                var g = GetMember(emitter, "AudioEntityGUID");
                if (!(GetMember(g, "guid") is ulong gid)) return;
                if (_postByName == null)
                {
                    var ak = AccessTools.TypeByName("Amplitude.Wwise.Interop.AkSoundEngine");
                    _postByName = ak?.GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType == typeof(ulong));
                }
                _postByName?.Invoke(null, new object[] { eventName, gid });
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Audio] postByName '" + eventName + "': " + ex.Message); }
        }

        // Runtime SOUND EXTRACTOR: write the full Wwise event-name catalog (every loaded AudioEventHandle) to a config
        // file, so the modder can browse the names and assign the right engineStartEvent/engineStopEvent per unit.
        public static void DumpSoundCatalog()
        {
            try
            {
                var t = AccessTools.TypeByName("Amplitude.Wwise.AudioEventHandle") ?? AccessTools.TypeByName("AudioEventHandle");
                if (t == null) { Plugin.Log.LogError("[Audio] AudioEventHandle type not found"); return; }
                var names = UnityEngine.Resources.FindObjectsOfTypeAll(t).OfType<UnityEngine.Object>()
                    .Select(o => o.name).Where(n => !string.IsNullOrEmpty(n))
                    .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var path = Path.Combine(Paths.ConfigPath, "enc_sound_catalog.txt");
                File.WriteAllLines(path, names);
                Plugin.Log.LogInfo($"[Audio] sound catalog: {names.Count} AudioEventHandle names -> {path}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] DumpSoundCatalog: " + ex); }
        }

        static object GetMember(object o, string name)
        { if (o == null) return null; var t = o.GetType(); var p = CachedProp(t, name); if (p != null) { try { return p.GetValue(o); } catch { } } var f = CachedField(t, name); if (f != null) { try { return f.GetValue(o); } catch { } } return null; }
        static object MakeGuid(int a, int b, int c, int d)
        { var gt = AccessTools.TypeByName("Amplitude.Framework.Guid"); if (gt == null) return null; var g = Activator.CreateInstance(gt);
          gt.GetField("a", BF)?.SetValue(g, a); gt.GetField("b", BF)?.SetValue(g, b); gt.GetField("c", BF)?.SetValue(g, c); gt.GetField("d", BF)?.SetValue(g, d); return g; }

        // ---- EXPERIMENTAL district-visual repoint (docs/District-Visuals.md) ----
        // Parsed once from config: the target district name + the two override modes. dGuid is a boxed Amplitude Guid or null.
        // One custom district model, from the enc_districts.json registry (written by the District Factory window).
        // Runtime state lives here too, so any number of districts can carry custom models simultaneously.
        internal class DistrictModel
        {
            public string district = "";     // ConstructibleDefinitionName to match
            public object fxMeshGuid;        // parsed Amplitude Guid of the baked FxMesh
            public bool isolate = true;      // true = private per-instance leaf (this tile only); false = global shared-leaf swap
            // runtime
            public object plbc; public int layer;
            public object privateLeaf;                                   // isolate mode: the Instantiated leaf
            public readonly List<object> leaves = new List<object>();    // global mode: collected shared leaves
            public bool collected, matchLogged, pointedLogged; public int wait;
        }
        internal static readonly List<DistrictModel> distModels = new List<DistrictModel>();

        static bool distParsed, distOn; static string distName, distAffinity; static object distGuid, distFxMeshGuid;
        static bool distSwapLogged, distGuidLogged;
        static object ParseGuidCsv(string gs)
        {
            if (string.IsNullOrEmpty(gs)) return null;
            var p = gs.Split(new[] { ',', ' ', '\t', ';', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 4 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b) && int.TryParse(p[2], out var c) && int.TryParse(p[3], out var d))
                return MakeGuid(a, b, c, d);
            Plugin.Log.LogError($"[District] GUID must be four ints \"a,b,c,d\" (got '{gs}').");
            return null;
        }
        static void EnsureDistrictConfig()
        {
            if (distParsed) return; distParsed = true;
            try
            {
                distOn = Plugin.DistrictRepointOn != null && Plugin.DistrictRepointOn.Value;
                distName = Plugin.DistrictName?.Value?.Trim() ?? "";
                distAffinity = Plugin.DistrictAffinity?.Value?.Trim() ?? "";
                distGuid = ParseGuidCsv(Plugin.DistrictEvolverGuid?.Value?.Trim() ?? "");
                distFxMeshGuid = ParseGuidCsv(Plugin.DistrictFxMeshGuid?.Value?.Trim() ?? "");
                if (distOn) Plugin.Log.LogInfo($"[District] repoint ACTIVE: name='{distName}' affinity='{distAffinity}' evolverGuid={(distGuid != null ? "set" : "none")} fxMeshGuid={(distFxMeshGuid != null ? "set" : "none")}");

                // The district REGISTRY (written by the District Factory window): any number of district models at once.
                distModels.Clear();
                var regPath = Path.Combine(Paths.ConfigPath, "enc_districts.json");
                if (File.Exists(regPath))
                {
                    try
                    {
                        var root = JObject.Parse(File.ReadAllText(regPath));
                        foreach (var d in (root["districts"] as JArray) ?? new JArray())
                        {
                            var e = new DistrictModel
                            {
                                district = (string)d["district"] ?? "",
                                fxMeshGuid = ParseGuidCsv((string)d["fxMeshGuid"] ?? ""),
                                isolate = (bool?)d["isolate"] ?? true,
                            };
                            if (e.district.Length > 0 && e.fxMeshGuid != null) distModels.Add(e);
                            else Plugin.Log.LogWarning($"[District] registry entry skipped (district='{e.district}', bad fxMeshGuid?)");
                        }
                        Plugin.Log.LogInfo($"[District] registry: {distModels.Count} district model(s) from enc_districts.json");
                    }
                    catch (Exception rex) { Plugin.Log.LogError("[District] enc_districts.json parse: " + rex); }
                }
                // legacy single-model config keeps working: synthesize an entry when the registry has none
                if (distModels.Count == 0 && !string.IsNullOrEmpty(distName) && distFxMeshGuid != null)
                    distModels.Add(new DistrictModel { district = distName, fxMeshGuid = distFxMeshGuid, isolate = Plugin.DistrictIsolate == null || Plugin.DistrictIsolate.Value });
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] config parse: " + ex); }
        }

        static readonly HashSet<string> distSeen = new HashSet<string>();
        // Diagnostic: log every distinct district name UpdateLevelBuild fires for, so we can see the ACTUAL
        // ConstructibleDefinitionName to target (an Extension_Base_* reactor may present under its host district's name).
        internal static void DistrictDiag(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;   // investigation aid — off unless DistrictDebug
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString() ?? "<null>";
                if (distSeen.Add(name))
                    Plugin.Log.LogInfo($"[District] saw district '{name}'{(name == distName ? "  <-- MATCHES DistrictName" : "")}");
            }
            catch { }
        }

        static readonly HashSet<string> distMatDumped = new HashSet<string>();
        // Diagnostic: after a district builds, read the FxEvolverMaterial GUID its main channel resolved to and log it as
        // "a,b,c,d" — ready to paste into another district's DistrictEvolverGuid (the clean SetChannel path), and the way to
        // grab a donor material for the bake. plbc.channels[layer].EvolverMaterialGuid (PerChannelData is a private struct).
        internal static void DistrictDumpMaterial(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;   // investigation aid — off unless DistrictDebug
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString() ?? "<null>";
                if (!distMatDumped.Add(name)) return;
                var plbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent")?.GetValue(district);
                if (plbc == null) return;
                int layer = 0;
                var lf = district.GetType().GetField("mainLevelBuildComponantLayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                if (lf?.GetValue(null) is int li) layer = li;
                if (!(AccessTools.Field(plbc.GetType(), "channels")?.GetValue(plbc) is Array channels) || layer >= channels.Length) return;
                var box = channels.GetValue(layer);
                var guid = box.GetType().GetProperty("EvolverMaterialGuid")?.GetValue(box);
                if (guid == null) return;
                var gt = guid.GetType();
                object a = gt.GetField("a", BF)?.GetValue(guid), b = gt.GetField("b", BF)?.GetValue(guid),
                       c = gt.GetField("c", BF)?.GetValue(guid), d = gt.GetField("d", BF)?.GetValue(guid);
                Plugin.Log.LogInfo($"[DistrictMat] {name} -> material {a},{b},{c},{d}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[DistrictMat] dump: " + ex); }
        }

        static readonly HashSet<string> distSubDumped = new HashSet<string>();
        // Diagnostic: the district's channel-0 material is an FxEvolverMaterialLevelBuildSelector that picks among a table
        // of PLAIN building-variant drawers (its `pairs` NameToGuidPair[] + `defaultMaterial`). Dump those sub-material
        // GUIDs — those are the plain drawers that actually render a mesh, and the clean thing to map an affinity at.
        internal static void DistrictDumpSubMaterials(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;   // investigation aid — off unless DistrictDebug
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString() ?? "<null>";
                if (name != distName) return;                       // only the targeted district, to avoid spam
                if (!distSubDumped.Add(name)) return;
                var plbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent")?.GetValue(district);
                if (plbc == null) return;
                if (!(AccessTools.Field(plbc.GetType(), "channels")?.GetValue(plbc) is Array channels) || channels.Length == 0) return;
                var box = channels.GetValue(0);
                var sel = AccessTools.Field(box.GetType(), "evolverMaterial")?.GetValue(box);
                if (sel == null) { Plugin.Log.LogInfo($"[DistrictSub] {name}: channel 0 material not loaded yet."); return; }
                Plugin.Log.LogInfo($"[DistrictSub] {name}: channel-0 material type = {sel.GetType().Name}");
                // defaultMaterial / deferredName / deferredTable give context
                foreach (var fn in new[] { "defaultMaterial", "invalidNameMaterial", "deferredTable" })
                {
                    var g = AccessTools.Field(sel.GetType(), fn)?.GetValue(sel);
                    if (g != null) { var gt = g.GetType(); Plugin.Log.LogInfo($"[DistrictSub]   {fn} = {gt.GetField("a", BF)?.GetValue(g)},{gt.GetField("b", BF)?.GetValue(g)},{gt.GetField("c", BF)?.GetValue(g)},{gt.GetField("d", BF)?.GetValue(g)}"); }
                }
                var dn = AccessTools.Field(sel.GetType(), "deferredName")?.GetValue(sel) as string;
                if (!string.IsNullOrEmpty(dn)) Plugin.Log.LogInfo($"[DistrictSub]   deferredName = '{dn}'");
                if (AccessTools.Field(sel.GetType(), "pairs")?.GetValue(sel) is Array pairs)
                {
                    Plugin.Log.LogInfo($"[DistrictSub]   pairs ({pairs.Length} building variants):");
                    foreach (var pr in pairs)
                    {
                        if (pr == null) continue;
                        var pt = pr.GetType();
                        var pn = pt.GetField("Name", BF)?.GetValue(pr) ?? pt.GetProperty("Name", BF)?.GetValue(pr);
                        var pg = pt.GetField("Guid", BF)?.GetValue(pr) ?? pt.GetField("Value", BF)?.GetValue(pr) ?? pt.GetField("guid", BF)?.GetValue(pr);
                        if (pg != null) { var gt = pg.GetType(); Plugin.Log.LogInfo($"[DistrictSub]     '{pn}' -> {gt.GetField("a", BF)?.GetValue(pg)},{gt.GetField("b", BF)?.GetValue(pg)},{gt.GetField("c", BF)?.GetValue(pg)},{gt.GetField("d", BF)?.GetValue(pg)}"); }
                        else Plugin.Log.LogInfo($"[DistrictSub]     '{pn}' -> (guid field not found on {pt.Name})");
                    }
                }
                else Plugin.Log.LogInfo($"[DistrictSub]   no 'pairs' field (uses a deferred table?).");
            }
            catch (Exception ex) { Plugin.Log.LogError("[DistrictSub] dump: " + ex); }
        }

        static bool DistrictMatches(object district, out object plbc)
        {
            plbc = null;
            EnsureDistrictConfig();
            if (!distOn || string.IsNullOrEmpty(distName)) return false;
            var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
            if (name != distName) return false;
            plbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent")?.GetValue(district);
            return true;
        }

        // MODE 1 (zero-bake): before the request is built, swap the district's visualAffinity to another vanilla one so it
        // resolves to an existing building. Pure config, no custom asset — proves the hook + per-district scoping in-game.
        internal static void DistrictAffinitySwap(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (string.IsNullOrEmpty(distAffinity) || distGuid != null) return;  // cheap bail BEFORE any reflection — this legacy mode is usually off
                if (!DistrictMatches(district, out _)) return;
                // Derive the StaticString type from the field itself (it's Amplitude.StaticString, not Amplitude.Framework.*).
                var vf = AccessTools.Field(district.GetType(), "visualAffinityName");
                if (vf == null) { Plugin.Log.LogError("[District] visualAffinityName field not found."); return; }
                var ss = Activator.CreateInstance(vf.FieldType, new object[] { distAffinity });
                SetMember(district, "visualAffinityName", ss);
                SetMember(district, "initialVisualAffinityName", ss);
                if (!distSwapLogged) { distSwapLogged = true; Plugin.Log.LogInfo($"[District] '{distName}' affinity -> '{distAffinity}' (zero-bake swap)"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] affinity swap: " + ex); }
        }

        // MODE 3 (best render odds): keep the district's OWN loaded material (which already renders correctly in this
        // context) and swap only its mesh GUID to our baked FxMesh. Avoids the "foreign material doesn't render here"
        // problem — our model draws through the exact material/shader/output-layer the vanilla building already uses.
        // Recursively rewrite the `mesh` GUID of every LEAF drawer reachable from a material: a plain drawer has a `mesh`
        // Guid field (swap it); a SELECTOR/EMITTER holds its loaded sub-materials in `fxMaterialCacheEntries.Entries[].FxMaterial`
        // (+ InvalidNameEntry) — recurse into those. Reuses the game's OWN loaded drawers, so they keep the selector's context.
        static MethodInfo fxTryLoad;
        static object TryLoadMaterial(object guid)
        {
            try
            {
                if (fxTryLoad == null)
                {
                    var fmType = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxEvolverMaterial");
                    // prefer the SYNCHRONOUS overload TryLoad(Guid, bool synchrone) so we get the material now, not async-null.
                    fxTryLoad = fmType?.GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "TryLoad" && m.GetParameters().Length == 2
                            && m.GetParameters()[0].ParameterType.Name == "Guid" && m.GetParameters()[1].ParameterType == typeof(bool));
                }
                return fxTryLoad?.Invoke(null, new object[] { guid, true });
            }
            catch { return null; }
        }
        static bool GuidIsNull(object g) { if (g == null) return true; var t = g.GetType(); return (int)(t.GetField("a", BF)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("b", BF)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("c", BF)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("d", BF)?.GetValue(g) ?? 0) == 0; }
        static object PairGuid(object pair) { var pt = pair.GetType(); return pt.GetField("Guid", BF)?.GetValue(pair) ?? pt.GetField("Value", BF)?.GetValue(pair) ?? pt.GetField("guid", BF)?.GetValue(pair); }

        // The leaf that holds geometry is FxEvolverMaterialLevelBuildElement with an `fxMesh` Guid field. Reached via:
        //   Selector.pairs[culture] -> Emitter.levelBuildItems[].loadedEvolverMaterial -> Element(.fxMesh)  (Emitters nest).
        static readonly List<object> distLeaves = new List<object>();   // legacy shared list (single-model path)
        static FieldInfo GF(Type t, string n) => t.GetField(n, BF);      // no AccessTools warning-on-miss (probing spams the log)
        static void CollectLeaves(object mat, List<object> outLeaves, int depth, HashSet<object> visited)
        {
            if (mat == null || depth > 8 || !visited.Add(mat)) return;
            var t = mat.GetType();
            // a leaf: has an fxMesh (or mesh) Guid field
            var lf = GF(t, "fxMesh") ?? GF(t, "mesh");
            if (lf != null && lf.FieldType.Name == "Guid") { outLeaves.Add(mat); return; }
            // emitter: levelBuildItems[].loadedEvolverMaterial
            if (AccessTools.Field(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items) if (it != null) CollectLeaves(AccessTools.Field(it.GetType(), "loadedEvolverMaterial")?.GetValue(it), outLeaves, depth + 1, visited);
            // selector: loaded cache entries + the pairs variant table (load each distinct GUID)
            var cache = AccessTools.Field(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && AccessTools.Field(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var e in entries) if (e != null) CollectLeaves(AccessTools.Field(e.GetType(), "FxMaterial")?.GetValue(e), outLeaves, depth + 1, visited);
            var seen = new HashSet<string>();
            void tryGuid(object g)
            {
                if (GuidIsNull(g)) return; var gt = g.GetType();
                if (!seen.Add($"{gt.GetField("a", BF)?.GetValue(g)},{gt.GetField("b", BF)?.GetValue(g)},{gt.GetField("c", BF)?.GetValue(g)},{gt.GetField("d", BF)?.GetValue(g)}")) return;
                CollectLeaves(TryLoadMaterial(g), outLeaves, depth + 1, visited);
            }
            if (AccessTools.Field(t, "pairs")?.GetValue(mat) is Array pairs)
                foreach (var pr in pairs) if (pr != null) tryGuid(PairGuid(pr));
            foreach (var fn in new[] { "defaultMaterial", "invalidNameMaterial" })
            { var g = AccessTools.Field(t, fn)?.GetValue(mat); if (g != null) tryGuid(g); }
        }
        static object distFxManager; static MethodInfo fxNextDoublon;
        // Re-point every collected leaf's fxMesh at our FxMesh. On the first pass, also call the leaf's own Load() so it
        // RE-RESOLVES meshIndex from our GUID (the render reads meshIndex, not fxMesh; uint.MaxValue is 'unresolved').
        static int ApplyLeaves(List<object> leaves, object fxGuid, bool resolve)
        {
            int n = 0, resolved = 0;
            foreach (var leaf in leaves)
            {
                var t = leaf.GetType();
                var lf = GF(t, "fxMesh") ?? GF(t, "mesh");
                if (lf == null) continue;
                lf.SetValue(leaf, fxGuid);   // persists our GUID so any game re-Load also uses ours
                n++;
                if (resolve && distFxManager != null)
                {
                    try
                    {
                        var load = t.GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (load != null && load.GetParameters().Length == 2)
                        {
                            if (fxNextDoublon == null)
                                fxNextDoublon = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxEvolverMaterial")?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Static | BindingFlags.Public);
                            uint doublon = fxNextDoublon != null ? (uint)fxNextDoublon.Invoke(null, null) : 0u;
                            load.Invoke(leaf, new object[] { distFxManager, doublon });
                            resolved++;
                            if (resolved <= 4)   // peek the resolved meshIndex: uint.MaxValue = GetMeshIndex FAILED to find our FxMesh
                            {
                                var mi = AccessTools.Field(t, "meshIndex")?.GetValue(leaf);
                                var oli = AccessTools.Field(t, "outputLayerIndex")?.GetValue(leaf);
                                Plugin.Log.LogInfo($"[District]   leaf '{GetMember(leaf, "Name")}' after Load: meshIndex={mi} outputLayerIndex={oli}");
                            }
                        }
                    }
                    catch { }
                }
            }
            if (resolve) Plugin.Log.LogInfo($"[District] '{distName}': re-pointed {n} leaves, re-resolved {resolved} via Load().");
            return n;
        }

        // (The old single-district MODE 3 cache was replaced by the registry-driven DistrictApplyEntries/Tick below.
        // Two dead investigation dumps — DumpOurFxMesh and DumpMaterialTree ([DistrictTree]) — were removed in the
        // post-breakthrough cleanup; recover from git history if a new district material chain ever needs mapping.)

        // Full district diagnostic for the F8 window: our FxMesh, the collected leaves, their resolved meshIndex, and the
        // DISTRICT mesh manager's per-layer buffer FILL (verts used / buffer size) — so we can SEE if our mesh doesn't fit.
        internal static List<string> DumpDistrictState()
        {
            var lines = new List<string>();
            try
            {
                EnsureDistrictConfig();
                lines.Add($"district registry: {distModels.Count} model(s)");
                var fxMeshType = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxMesh");
                var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                var load = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
                var loadFx = fxMeshType != null && load != null ? load.MakeGenericMethod(fxMeshType) : null;
                object anyLeaf = null;
                foreach (var e in distModels)
                {
                    string meshInfo = "?";
                    if (loadFx != null && e.fxMeshGuid != null)
                    {
                        var fx = loadFx.Invoke(null, loadFx.GetParameters().Length == 1 ? new[] { e.fxMeshGuid } : new[] { e.fxMeshGuid, null });
                        var um = fx != null ? (fxMeshType.GetProperty("Mesh", BF)?.GetValue(fx) ?? GF(fxMeshType, "mesh")?.GetValue(fx)) as UnityEngine.Mesh : null;
                        meshInfo = fx == null ? "FxMesh NULL by GUID" : um == null ? "Mesh NULL" : um.vertexCount + " verts, bounds " + um.bounds.size;
                    }
                    var leaf = e.privateLeaf ?? (e.leaves.Count > 0 ? e.leaves[0] : null);
                    var mi = leaf != null ? GF(leaf.GetType(), "meshIndex")?.GetValue(leaf) : null;
                    lines.Add($"  '{e.district}' isolate={e.isolate} matched={(e.plbc != null)} leaf={(leaf != null ? "meshIndex=" + mi : "not built")} | {meshInfo}");
                    anyLeaf = anyLeaf ?? leaf;
                }
                // walk to the district mesh content manager and dump each layer's fill
                if (anyLeaf != null)
                {
                    var desc = GetMember(anyLeaf, "FxEvolverDescriptor");
                    var mgr = desc != null ? GetMember(desc, "AssetContentManagerMesh") : null;
                    if (mgr != null && GetMember(mgr, "Layers") is Array layers)
                    {
                        lines.Add($"mesh manager: {layers.Length} layer(s)");
                        for (int i = 0; i < layers.Length; i++)
                        {
                            var L = layers.GetValue(i); if (L == null) continue;
                            var nm = GetMember(L, "Name") ?? GetMember(L, "name");
                            var cv = GetMember(L, "currentVertexIndex");
                            var vb = GetMember(L, "vertexBuffer"); var vbSize = vb != null ? GetMember(vb, "Size") : null;
                            var cm = GetMember(L, "currentMeshAddedCount");
                            lines.Add($"  layer {i} '{nm}': verts {cv}/{vbSize}, meshes {cm}");
                        }
                    }
                }
            }
            catch (Exception ex) { lines.Add("dump error: " + ex.Message); }
            foreach (var l in lines) Plugin.Log.LogInfo("[DistrictState] " + l);
            return lines;
        }

        // ISOLATION: instead of mutating the shared building leaves globally, give ONLY the target district a PRIVATE leaf.
        // Each PresentationLevelBuildComponent has its own channel + Shuriken particle, so pointing just this district's
        // channel at a private (Instantiated) leaf — and re-spawning its particle — scopes our mesh to this tile alone.
        static object BuildPrivateLeaf(object channelSelector, object fxGuid)
        {
            try
            {
                var found = new List<object>();
                CollectLeaves(channelSelector, found, 0, new HashSet<object>());
                if (found.Count == 0) return null;
                if (!(found[0] is UnityEngine.Object src) || src == null) return null;
                var clone = UnityEngine.Object.Instantiate(src);   // a private copy — mutating it won't touch the shared leaf
                var t = clone.GetType();
                (GF(t, "fxMesh") ?? GF(t, "mesh"))?.SetValue(clone, fxGuid);
                // reset load state so LoadIFN actually re-runs and resolves our mesh + assigns a MaterialIndex
                foreach (var ls in new[] { "loadingStatus" }) { var f = GF(t, ls); if (f != null) f.SetValue(clone, System.Enum.ToObject(f.FieldType, 0)); }
                var loadIFN = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "LoadIFN" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.Name.Contains("FxManager"));
                if (loadIFN != null && distFxManager != null)
                {
                    if (fxNextDoublon == null) fxNextDoublon = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxEvolverMaterial")?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Static | BindingFlags.Public);
                    var pars = loadIFN.GetParameters();
                    var args = pars.Length == 1 ? new[] { distFxManager } : new object[] { distFxManager, fxNextDoublon != null ? fxNextDoublon.Invoke(null, null) : (uint)0 };
                    loadIFN.Invoke(clone, args);
                }
                // also force the mesh re-resolve directly (Load), in case LoadIFN short-circuited
                var load = t.GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (load != null && load.GetParameters().Length == 2 && distFxManager != null)
                    load.Invoke(clone, new object[] { distFxManager, fxNextDoublon != null ? fxNextDoublon.Invoke(null, null) : (uint)0 });
                Plugin.Log.LogInfo($"[District] built PRIVATE leaf '{t.Name}': MaterialIndex={GF(t, "materialIndex")?.GetValue(clone)} meshIndex={GF(t, "meshIndex")?.GetValue(clone)}");
                return clone;
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] build private leaf: " + ex); return null; }
        }
        // ISOLATE mode, per entry: keep this district's channel pointed at ITS private leaf + re-spawned particle.
        // Re-applied each frame (the game reloads the shared selector into the channel on UpdateLevelBuild).
        static void PointEntryAtPrivateLeaf(DistrictModel e)
        {
            try
            {
                if (e.plbc == null) return;
                if (!(AccessTools.Field(e.plbc.GetType(), "channels")?.GetValue(e.plbc) is Array channels) || e.layer >= channels.Length) return;
                var box = channels.GetValue(e.layer);
                var evf = GF(box.GetType(), "evolverMaterial");
                if (evf == null) return;
                // build the private leaf lazily — the selector's sub-materials load async, so retry until they're ready.
                if (e.privateLeaf == null)
                {
                    var sel = evf.GetValue(box);
                    if (sel == null) return;
                    e.privateLeaf = BuildPrivateLeaf(sel, e.fxMeshGuid);
                    if (e.privateLeaf == null) { if (e.wait++ % 300 == 0) Plugin.Log.LogInfo($"[District] '{e.district}': waiting for leaves to load..."); return; }
                }
                if (ReferenceEquals(evf.GetValue(box), e.privateLeaf)) return;   // already ours this frame
                evf.SetValue(box, e.privateLeaf);
                channels.SetValue(box, e.layer);   // write the mutated struct back into the array
                // re-spawn the particle so PatchParticle picks up the private leaf's MaterialIndex
                var refresh = e.plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                if (refresh != null) refresh.Invoke(e.plbc, new object[] { e.layer, System.Enum.ToObject(refresh.GetParameters()[1].ParameterType, 0) });
                if (!e.pointedLogged) { e.pointedLogged = true; Plugin.Log.LogInfo($"[District] '{e.district}' ISOLATED: channel {e.layer} -> its private leaf (this tile only)."); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] point channel: " + ex); }
        }

        // GLOBAL mode, per entry: collect the shared leaves once and re-point them all (affects every district sharing them).
        static void GlobalSwapEntry(DistrictModel e)
        {
            try
            {
                if (e.plbc == null) return;
                if (!e.collected)
                {
                    if (!(AccessTools.Field(e.plbc.GetType(), "channels")?.GetValue(e.plbc) is Array channels) || e.layer >= channels.Length) return;
                    var mat = GF(channels.GetValue(e.layer).GetType(), "evolverMaterial")?.GetValue(channels.GetValue(e.layer));
                    if (mat == null) return;
                    e.leaves.Clear();
                    CollectLeaves(mat, e.leaves, 0, new HashSet<object>());
                    if (e.leaves.Count == 0) return;   // async load — retry next frame
                    e.collected = true;
                    Plugin.Log.LogInfo($"[District] '{e.district}': collected {e.leaves.Count} shared leaf Element(s) (GLOBAL swap).");
                    ApplyLeaves(e.leaves, e.fxMeshGuid, resolve: true);
                }
                else ApplyLeaves(e.leaves, e.fxMeshGuid, resolve: false);   // keep our GUID set in case the game re-Loads
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] global swap: " + ex); }
        }

        // Postfix (per district UpdateLevelBuild): match against the registry and cache each entry's component + layer.
        internal static void DistrictApplyEntries(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn || distModels.Count == 0) return;
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name)) return;
                foreach (var e in distModels)
                {
                    if (e.district != name || e.fxMeshGuid == null) continue;
                    var plbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent")?.GetValue(district);
                    if (plbc == null) continue;
                    distFxManager = distFxManager ?? GetMember(plbc, "FxManager");
                    int layer = 0;
                    var lf = district.GetType().GetField("mainLevelBuildComponantLayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                    if (lf?.GetValue(null) is int li) layer = li;
                    e.plbc = plbc; e.layer = layer;
                    if (!e.matchLogged) { e.matchLogged = true; Plugin.Log.LogInfo($"[District] registry matched '{e.district}' (isolate={e.isolate})."); }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] apply entries: " + ex); }
        }

        // Per-frame (Plugin.Update): drive every registry entry.
        internal static void TickDistrictMeshSwap()
        {
            if (distModels.Count == 0) return;
            foreach (var e in distModels)
            {
                if (e.isolate) PointEntryAtPrivateLeaf(e);
                else GlobalSwapEntry(e);
            }
        }

        // ---- EXPERIMENTAL pawn PROP/attachment axis (custom weapons & gear on attachment slots; the sling experiment) ----
        // A pawn's Attachements[] slot references a PresentationPawnFragmentMesh (the EQ_* assets) = {ModelPrefab, ModelName,
        // MaterialRef}: a RIGID mesh glued to the slot's bone, GPU-encoded at spawn. The loader hard-gates on
        // AnimationManager.GetMeshCollection(ModelPrefab.Guid) finding a REGISTERED collection ("was not registered ...
        // please add it to AnimationManagerContent" -> draws nothing). AnimationManager.RegisterMeshCollection is PUBLIC and
        // also uploads the collection's meshes to the GPU content manager (LoadIFN) — so one call crosses the gate. Skeleton
        // DERIVES from MeshCollection, so our baked Skeleton assets qualify directly. Retry per-frame: the manager instance
        // and its internal list only exist once the presentation loads, and our bundle assets load async.
        // TIMING is the crux: pawn definitions resolve their fragments INSIDE the game's loading chunk, so an Update-tick
        // registration loses the race by construction — the pawn definition then fails its Load, never registers a pawn
        // id, and its units draw as pawn definition 0 (the MAMMOTH — observed). The real seam is Hk_PropRegister below:
        // a postfix on AnimationManager.AnimationLoad, which rebuilds the manager's collection list and registers the
        // game's own collections; we append ours right there, before any pawn resolves. The Update tick stays as a
        // late-repair safety net only.
        // ---- EXPERIMENTAL projectile axis (docs/Projectiles.md) ----
        // A unit's PresentationPawnDefinition.Projectile (a ProjectileAssetReference) is read at attack time to spawn the
        // flying FX. We load the pawn def AND our baked ProjectileAsset by GUID and re-point the reference's inner guid —
        // the same AssetReference-guid swap the prop axis uses for a fragment's ModelPrefab. Applied at AnimationLoad (data
        // is up, before combat); idempotent, so re-running each session just re-asserts it.
        static bool projParsed;
        static readonly List<(object pawnGuid, object projGuid, string raw)> projOverrides = new List<(object, object, string)>();
        internal static void RearmProjectileOverrides() { projParsed = false; projOverrides.Clear(); }

        // Comma-ONLY 4-int parser. (ParseGuidCsv splits on '-' too, which would corrupt the negative ints a projectile
        // GUID routinely has, e.g. -839228096.)
        static object ParseGuid4(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var p = s.Split(',');
            if (p.Length == 4 && int.TryParse(p[0].Trim(), out var a) && int.TryParse(p[1].Trim(), out var b)
                && int.TryParse(p[2].Trim(), out var c) && int.TryParse(p[3].Trim(), out var d))
                return MakeGuid(a, b, c, d);
            return null;
        }

        static MethodInfo adbLoadAsset;
        static object LoadAmpliAsset(Type assetType, object guid)
        {
            if (adbLoadAsset == null)
            {
                var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                adbLoadAsset = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "TryLoadAsset" || m.Name == "LoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            }
            try { return adbLoadAsset?.MakeGenericMethod(assetType).Invoke(null, new[] { guid }); } catch { return null; }
        }

        // AssetReference<T> hides its guid on a private base — walk the chain (mirrors PropBaker.FindFieldDeep).
        static FieldInfo FindGuidField(Type t)
        {
            for (; t != null; t = t.BaseType)
                foreach (var n in new[] { "guid", "Guid" })
                {
                    var f = t.GetField(n, BF | BindingFlags.DeclaredOnly);
                    if (f != null && f.FieldType.Name == "Guid") return f;
                }
            return null;
        }

        internal static void ApplyProjectileOverrides(string cfg)
        {
            if (!projParsed)
            {
                projParsed = true;
                foreach (var entry in (cfg ?? "").Split(';'))
                {
                    var e = entry.Trim(); if (e.Length == 0) continue;
                    int eq = e.IndexOf('=');
                    if (eq < 0) { Plugin.Log.LogError($"[Projectile] override needs '<pawnDefGuid>=<projectileGuid>' (got '{e}')"); continue; }
                    var pawnGuid = ParseGuid4(e.Substring(0, eq));
                    var projGuid = ParseGuid4(e.Substring(eq + 1));
                    if (pawnGuid == null || projGuid == null) { Plugin.Log.LogError($"[Projectile] both sides must be four ints \"a,b,c,d\" (got '{e}')"); continue; }
                    projOverrides.Add((pawnGuid, projGuid, e));
                }
                if (projOverrides.Count > 0) Plugin.Log.LogInfo($"[Projectile] {projOverrides.Count} override(s) to apply");
            }
            if (projOverrides.Count == 0) return;

            var pawnType = AccessTools.TypeByName("Amplitude.Mercury.Data.World.PresentationPawnDefinition");
            var projType = AccessTools.TypeByName("Amplitude.Mercury.Data.World.ProjectileAsset");
            if (pawnType == null || projType == null) { Plugin.Log.LogError("[Projectile] ProjectileAsset/PresentationPawnDefinition type not found (game update?)"); return; }
            var projField = AccessTools.Field(pawnType, "Projectile");   // ProjectileAssetReference (declared on the base; AccessTools walks it)
            if (projField == null) { Plugin.Log.LogError("[Projectile] pawn def has no 'Projectile' field (game update?)"); return; }

            foreach (var (pawnGuid, projGuid, raw) in projOverrides)
            {
                var pawnDef = LoadAmpliAsset(pawnType, pawnGuid) as UnityEngine.Object;
                if (pawnDef == null) { Plugin.Log.LogWarning($"[Projectile] pawn def GUID didn't resolve ('{raw}') — check the GUID / that its bundle is loaded."); continue; }
                var proj = LoadAmpliAsset(projType, projGuid) as UnityEngine.Object;
                if (proj == null) { Plugin.Log.LogWarning($"[Projectile] projectile GUID didn't resolve for '{pawnDef.name}' — is Projectile_KamikazeDrone in a BUILT, loaded bundle?"); continue; }
                var pref = projField.GetValue(pawnDef);
                if (pref == null) { pref = Activator.CreateInstance(projField.FieldType); projField.SetValue(pawnDef, pref); }
                var gf = FindGuidField(pref.GetType());
                if (gf == null) { Plugin.Log.LogError("[Projectile] ProjectileAssetReference has no guid field (layout changed?)"); continue; }
                gf.SetValue(pref, projGuid);
                Plugin.Log.LogInfo($"[Projectile] '{pawnDef.name}'.Projectile -> '{proj.name}'  ({raw})");
            }
        }

        static readonly List<object> propPending = new List<object>();   // parsed GUIDs not yet registered (per-session)
        static bool propParsed; static int propWait;
        internal static void RearmPropRegistration() { propParsed = false; propPending.Clear(); }   // AnimationLoad cleared the manager's list — register ours again
        static void ParsePropGuidsIFN()
        {
            if (propParsed) return;
            propParsed = true;
            foreach (var part in (Plugin.PropCollectionGuids?.Value ?? "").Split(';'))
            {
                var g = ParseGuidCsv(part.Trim());
                if (g != null) propPending.Add(g);
            }
            if (propPending.Count > 0) Plugin.Log.LogInfo($"[Props] {propPending.Count} mesh collection(s) to register");
        }

        // Called from Hk_PropRegister's postfix (loud: this is THE moment it must work) and from the Update tick (quiet).
        internal static void RegisterPropCollections(object animationManager, bool loud)
        {
            ParsePropGuidsIFN();
            if (propPending.Count == 0 || animationManager == null) return;
            var amType = animationManager.GetType();
            var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
            var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
            var load = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => (m.Name == "TryLoadAsset" || m.Name == "LoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)?.MakeGenericMethod(mcType);
            var reg = amType.GetMethod("RegisterMeshCollection", BindingFlags.Public | BindingFlags.Instance);
            if (load == null || reg == null) { Plugin.Log.LogError("[Props] reflection targets missing (LoadAsset / RegisterMeshCollection) — axis disabled this session."); propPending.Clear(); return; }
            for (int i = propPending.Count - 1; i >= 0; i--)
            {
                var mc = load.Invoke(null, new[] { propPending[i] });
                if (mc == null || (mc is UnityEngine.Object uo && !uo))
                    mc = LoadCollectionFromLoadedBundles(mcType, i);   // Amplitude's catalog misses our MeshCollection (type-specific) — pull it from the mounted Unity bundle by name instead
                if (mc == null || (mc is UnityEngine.Object uo2 && !uo2))
                {
                    if (loud) Plugin.Log.LogError("[Props] mesh collection NOT loadable at AnimationLoad time (GUID catalog miss AND no loaded bundle carries it by name).");
                    else if (++propWait % 600 == 0) Plugin.Log.LogWarning("[Props] a mesh collection isn't loadable yet — retrying.");
                    continue;
                }
                reg.Invoke(animationManager, new[] { mc });   // dedupes internally; also LoadIFNs the meshes into the GPU content manager
                Plugin.Log.LogInfo($"[Props] registered mesh collection '{(mc as UnityEngine.Object)?.name}'" + (loud ? " (at AnimationLoad — before pawn resolution)" : " (late tick)"));
                propPending.RemoveAt(i);
            }
        }

        // FALLBACK loader: Amplitude's AssetDatabase resolves our FxMesh/Skeleton GUIDs from the community bundle fine,
        // but NOT a MeshCollection (type-specific catalog gap). The bundle itself is a plain Unity AssetBundle the game
        // has already mounted, so load the asset object BY NAME from the loaded bundles — RegisterMeshCollection takes
        // the object, and the collection's internal FxMesh GUID still resolves through the game's own (working) path.
        static object LoadCollectionFromLoadedBundles(Type mcType, int pendingIndex)
        {
            try
            {
                var names = (Plugin.PropCollectionNames?.Value ?? "").Split(';');
                string name = pendingIndex < names.Length ? names[pendingIndex].Trim() : "";
                if (name.Length == 0) return null;
                foreach (var b in UnityEngine.AssetBundle.GetAllLoadedAssetBundles())
                {
                    var a = b.LoadAsset(name);                 // short-name lookup; unique within our bundle
                    if (a != null && mcType.IsInstanceOfType(a)) return a;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Props] bundle-name fallback: " + ex.Message); }
            return null;
        }

        internal static void TickPropRegister()   // safety net: late registration/repair only
        {
            try
            {
                ParsePropGuidsIFN();
                if (propPending.Count == 0) return;
                var amType = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
                var inst = amType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                           ?? GF(amType, "Instance")?.GetValue(null);
                if (inst != null) RegisterPropCollections(inst, loud: false);
            }
            // The manager exists but its internals aren't built yet (Register throws before its Load) — retry next frame.
            catch (Exception ex) { if (++propWait % 600 == 0) Plugin.Log.LogWarning("[Props] tick (retrying): " + ex.Message); }
        }

        // MODE 2 (custom model): after UpdateLevelBuild loaded the vanilla material, override the main mesh channel with our
        // own baked FxEvolverMaterial via the game's public SetChannel(int layer, Guid, RenderMode, forceRefresh).
        internal static void DistrictGuidOverride(object district)
        {
            try
            {
                if (distGuid == null) return;                          // cheap bail when GUID mode is off
                if (!DistrictMatches(district, out var plbc) || plbc == null) return;
                // main level-build layer is a private static int on PresentationDistrict (= 0); read it, fall back to 0.
                int layer = 0;
                var lf = district.GetType().GetField("mainLevelBuildComponantLayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                if (lf?.GetValue(null) is int li) layer = li;
                var renderMode = GetMember(district, "RenderMode");    // boxed HgFxAnchorComponent.RenderModeEnum
                var setChannel = plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "SetChannel" && m.GetParameters().Length == 4
                        && m.GetParameters()[1].ParameterType.Name == "Guid");
                if (setChannel == null) { Plugin.Log.LogError("[District] SetChannel(int,Guid,...) overload not found (game update?)."); distGuid = null; return; }
                setChannel.Invoke(plbc, new object[] { layer, distGuid, renderMode, true });
                if (!distGuidLogged) { distGuidLogged = true; Plugin.Log.LogInfo($"[District] '{distName}' mesh channel -> our FxEvolverMaterial (layer {layer})"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] guid override: " + ex); }
        }

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
                    // the PER-MESH ceiling: quads beyond this are SILENTLY dropped at encode (holes in the model). 0 = unlimited.
                    int mt = ToInt(GetMember(L, "maxMeshTriangleCount"));
                    string tag = i == pawnLayer ? "  <-- your models" : "";
                    lines.Add($"  L{i} '{nm}': verts {v:n0}/{vMax:n0} ({Pct(v, vMax)}%) | idx {Pct(x, xMax)}% | meshes {m}/{mMax} | maxTris/mesh {(mt == 0 ? "unlimited" : mt.ToString("n0"))}{tag}");
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
        internal static void DumpOutputLayerAtlases(string filter = null)
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
                    if (!string.IsNullOrEmpty(filter) && layer.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;   // only this unit (e.g. "Corvette")
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
                    var png = ToReadablePng(tex);
                    if (png == null) continue;
                    File.WriteAllBytes(Path.Combine(dir, SanitizeFile(layer) + ".png"), png);
                    n++;
                    Plugin.Log.LogInfo($"[AtlasDump] {layer} -> {SanitizeFile(layer)}.png ({tex.width}x{tex.height}, {tex.name})");
                }
                Plugin.Log.LogInfo($"[AtlasDump] wrote {n} atlas PNG(s){(string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'")} to {dir}");
            }
            catch (Exception e) { Plugin.Log.LogError("[AtlasDump] " + e); }
        }

        // Blit any (possibly non-readable / compressed) texture through a RenderTexture into a readable Texture2D and
        // PNG-encode it. PNG (vs TGA) round-trips cleanly with LoadImage — paint on the dumped canvas and the retexture
        // maps back exactly. Uses UnityEngine.ImageConversionModule (also referenced for the retexture skin-load).
        static byte[] ToReadablePng(UnityEngine.Texture src)
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
                var png = UnityEngine.ImageConversion.EncodeToPNG(t);   // static form: no `using UnityEngine;` in this file
                UnityEngine.Object.DestroyImmediate(t);
                return png;
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

    // Live trace of every Wwise PostEvent — see exactly what the game posts on the AUDIBLE vanilla unit's emitter vs
    // ours during a move. Gated behind UniversalInject.AudioTraceOn (+ name filter), toggled from the F8 window, so it's
    // free until enabled. The recipe extracted here is what we reproduce to give our units real movement sound.
    [HarmonyPatch]
    internal static class Hk_AudioTrace
    {
        // Patch the SERVICE-level sink AudioManager.PostEvent(AudioEventHandle, AudioEntityGUID): both emitter sounds AND
        // service-direct sounds (the unit voice, etc.) funnel through here, so it enumerates the FULL sound palette a unit
        // uses — including whatever path the audible engine actually takes (the emitter-level trace only saw the idle).
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Wwise.Audio.AudioManager");
            return t?.GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType.Name == "AudioEventHandle"
                && m.GetParameters()[1].ParameterType.Name == "AudioEntityGUID");
        }
        static void Postfix(object __0)
        {
            // Hot path: this runs on EVERY sound the game posts. Once we're not tracing and the fallback handles are all
            // captured, there is nothing to do — bail before any string work.
            if (!UniversalInject.AudioTraceOn && UniversalInject.StashedEngineHandle != null
                && UniversalInject.StashedLoudHandle != null && UniversalInject.StashedStopHandle != null) return;
            if (!(__0 is UnityEngine.Object eo) || eo == null) return;
            string en = eo.name;
            // Auto-capture (free) the two engine sounds we replay: the idle loop, and the per-ship move-START one-shot
            // (e.g. 'Play_UNIT_Vehicles_StealthCorvette_Start' — the distinct engine sound the audible boats fire on move,
            // which our units never get because it takes the service path, not the emitter's PostEvent).
            if (UniversalInject.StashedEngineHandle == null && en.IndexOf("ModernBoat_Idle", StringComparison.OrdinalIgnoreCase) >= 0)
                UniversalInject.StashedEngineHandle = __0;
            if (en.IndexOf("Vehicles", StringComparison.OrdinalIgnoreCase) >= 0 && en.IndexOf("_Start", StringComparison.OrdinalIgnoreCase) >= 0)
            { UniversalInject.StashedLoudHandle = __0; UniversalInject.StashedLoudName = en; }
            if (en.IndexOf("Vehicles", StringComparison.OrdinalIgnoreCase) >= 0 && en.IndexOf("_Stop", StringComparison.OrdinalIgnoreCase) >= 0)
                UniversalInject.StashedStopHandle = __0;
            if (!UniversalInject.AudioTraceOn) return;
            if (UniversalInject.SeenEvents.Add(en)) Plugin.Log.LogInfo($"[AudioTrace] NEW event: '{en}'");
        }
    }

    // EXPERIMENTAL — the DISTRICT injection axis (docs/District-Visuals.md). A district's on-map building is chosen by a
    // named visual-affinity slot (the district-side analogue of a unit's pawnDescription) resolved to a static FxMesh via
    // FxEvolverMaterial. We patch PresentationDistrict.UpdateLevelBuild (the moment the district builds its asset request
    // and calls SetChannel) and, ONLY for the one district named in config, either swap its affinity to another vanilla
    // one (zero-bake proof) or override the resolved mesh channel with our own baked FxEvolverMaterial GUID (custom model).
    // Scoped by ConstructibleDefinitionName, so the shared affinity every other district borrows is never touched.
    [HarmonyPatch]
    internal static class Hk_DistrictRepoint
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationDistrict");
            // UpdateLevelBuild(HgFxAnchorComponent.EventNameEnum) — the district's own override (fires only for districts).
            return t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "UpdateLevelBuild" && m.GetParameters().Length == 1);
        }
        // Prefix runs before the request is built, so the affinity swap feeds FillRequest.
        static void Prefix(object __instance) { UniversalInject.DistrictDiag(__instance); UniversalInject.DistrictAffinitySwap(__instance); }
        // Postfix: after UpdateLevelBuild built the request/material — dumps + the registry-driven apply act here.
        static void Postfix(object __instance) { UniversalInject.DistrictDumpMaterial(__instance); UniversalInject.DistrictDumpSubMaterials(__instance); UniversalInject.DistrictApplyEntries(__instance); UniversalInject.DistrictGuidOverride(__instance); }
    }

    // EXPERIMENTAL (opt-in) — enlarge the shared 'Visual' GPU mesh buffer so custom district meshes fit even in a full
    // late-game city. ContentLayer.LoadEncodingVertexAndBuffer() creates the vertex buffer from baseVertexBufferSize; we
    // bump that field for the BIG layer (only, to avoid touching the tiny Emitter/default layers) by DistrictBufferHeadroom.
    [HarmonyPatch]
    internal static class Hk_DistrictBufferHeadroom
    {
        static MethodBase TargetMethod()
        {
            var layer = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxComponentMeshContentManager+ContentLayer");
            return layer?.GetMethod("LoadEncodingVertexAndBuffer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }
        static void Prefix(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                var nm = (t.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__instance)
                          ?? t.GetField("name", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(__instance)) as string ?? "";

                // legacy single-purpose knob: enlarge only the building 'Visual' layer (where district meshes go)
                int extra = Plugin.DistrictBufferHeadroom != null ? Plugin.DistrictBufferHeadroom.Value : 0;
                if (extra > 0 && nm.IndexOf("Visual", StringComparison.OrdinalIgnoreCase) >= 0)
                    BumpIntField(__instance, "baseVertexBufferSize", extra, nm, "[District]");

                // generic per-layer overrides: "<nameSubstr>:verts=+N,idx=+N,meshes=+N,maxtris=N;..."
                // verts/idx/meshes ADD to buffer sizes; maxtris SETS the per-mesh triangle cap (0 = unlimited —
                // otherwise quads beyond it are SILENTLY dropped at encode, leaving holes in detailed models).
                var spec = Plugin.BufferOverrides?.Value?.Trim() ?? "";
                if (spec.Length == 0) return;
                foreach (var part in spec.Split(';'))
                {
                    int colon = part.IndexOf(':');
                    if (colon <= 0) continue;
                    string match = part.Substring(0, colon).Trim();
                    if (match.Length == 0 || nm.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var kv in part.Substring(colon + 1).Split(','))
                    {
                        var p = kv.Split('=');
                        if (p.Length != 2 || !int.TryParse(p[1].Trim().TrimStart('+'), out int val)) continue;
                        switch (p[0].Trim().ToLowerInvariant())
                        {
                            case "verts":   BumpIntField(__instance, "baseVertexBufferSize", val, nm, "[Buffers]"); break;
                            case "idx":     BumpIntField(__instance, "baseIndexBufferSize", val, nm, "[Buffers]"); break;
                            case "meshes":  BumpIntField(__instance, "maxMeshCount", val, nm, "[Buffers]"); break;
                            case "maxtris": SetIntField(__instance, "maxMeshTriangleCount", val, nm, "[Buffers]"); break;
                        }
                    }
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[Buffers] layer override: " + ex); }
        }

        static void BumpIntField(object o, string field, int extra, string layer, string tag)
        {
            var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null || f.FieldType != typeof(int)) { Plugin.Log.LogWarning($"{tag} field '{field}' not found on ContentLayer."); return; }
            int cur = (int)f.GetValue(o);
            f.SetValue(o, cur + extra);
            Plugin.Log.LogInfo($"{tag} '{layer}' {field}: {cur} -> {cur + extra} (+{extra}).");
        }

        static void SetIntField(object o, string field, int val, string layer, string tag)
        {
            var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null || f.FieldType != typeof(int)) { Plugin.Log.LogWarning($"{tag} field '{field}' not found on ContentLayer."); return; }
            int cur = (int)f.GetValue(o);
            f.SetValue(o, val);
            Plugin.Log.LogInfo($"{tag} '{layer}' {field}: {cur} -> {val}{(field == "maxMeshTriangleCount" && val == 0 ? " (unlimited)" : "")}.");
        }
    }

    // EXPERIMENTAL (opt-in, [Props]) — pawn PROP/attachment axis. Postfix on AnimationManager.AnimationLoad: the exact
    // moment the game rebuilds its collection list and registers its OWN MeshCollections — we append ours right after,
    // BEFORE any pawn definition resolves its fragments. (An Update-tick registration loses that race: the pawn def then
    // fails its Load, never gets a pawn id, and its units draw as pawn definition 0 — a herd of mammoths, memorably.)
    // AnimationLoad runs per game-session and clears the list, so we re-arm the pending set each time it fires.
    [HarmonyPatch]
    internal static class Hk_PropRegister
    {
        static MethodBase TargetMethod()
        {
            var am = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return am?.GetMethod("AnimationLoad", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }
        static void Postfix(object __instance)
        {
            try
            {
                if (Plugin.PropRegisterOn == null || !Plugin.PropRegisterOn.Value) return;
                UniversalInject.RearmPropRegistration();                     // fresh session list -> register everything again
                UniversalInject.RegisterPropCollections(__instance, loud: true);
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[Props] AnimationLoad postfix: " + ex); }
        }
    }

    // EXPERIMENTAL (opt-in, [Projectiles]) — projectile axis. Postfix on AnimationManager.AnimationLoad (same per-session
    // seam as props, after data is up and before combat spawns a projectile): re-point the configured units' Projectile at
    // our baked ProjectileAsset. Re-armed each session so the swap re-asserts if the game reloads its data.
    [HarmonyPatch]
    internal static class Hk_ProjectileOverride
    {
        static MethodBase TargetMethod()
        {
            var am = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager");
            return am?.GetMethod("AnimationLoad", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }
        static void Postfix()
        {
            try
            {
                var cfg = Plugin.ProjectileOverrides?.Value;
                if (string.IsNullOrWhiteSpace(cfg)) return;
                UniversalInject.RearmProjectileOverrides();
                UniversalInject.ApplyProjectileOverrides(cfg);
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[Projectile] AnimationLoad postfix: " + ex); }
        }
    }
}
