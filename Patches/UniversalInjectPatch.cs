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
    // One pawn's published movement state (STATE-DRIVEN models, Phase 2): written by the main-thread poll
    // (ProcessAnimStates), read by the per-frame pose hook via nearest-position match (the deploy poll's approximation).
    internal struct StateSample { public UnityEngine.Vector3 pos; public bool moving; public float stoppedAt; public float moveStartedAt; public bool combat; }

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
        public float brightness = 1f;         // UNIVERSAL skin brightness GAMMA (1 = unchanged, >1 lighter, <1 darker). Applied FIRST (before desaturate/tint) to whatever skin the unit gets. Multiplicative in the dark range, so a near-black atlas actually lightens — the additive tint tops out (+30 lifts 18 to only 48; gamma 1.5 lifts it ~2.4x). Managed by the Unit Retexture window.
        public string textureFile = "";       // TEXTURE-ONLY RETEXTURE: a PNG filename in BepInEx/config/enc_skins/. When set, the plugin loads that PNG and paints it onto the unit's ISOLATED output layer (same isolation as desaturate — original untouched, vanilla mesh kept). Hot-loaded at runtime, no bake/rebuild. Takes precedence over desaturate. Painted on a dump of the unit's own atlas (round-trips via PNG). Managed by the Unit Retexture editor window.
        public float tintR = 0f;              // UNIVERSAL skin colour offset, red channel (-255..+255, 0 = none). Added AFTER desaturate to whatever skin this unit ends up with — the loaded textureFile PNG, OR a copy of its own atlas. Equal negative R/G/B = darken; equal positive = brighten; one channel tints.
        public float tintG = 0f;              // ... green channel (-255..+255).
        public float tintB = 0f;              // ... blue channel (-255..+255).
        public int ca, cb, cc, cd;       // ANIMATED models: our baked ClipCollection Amplitude guid (its own clip, e.g. a drone's spinning-prop 'hover'). 0,0,0,0 = static model (no pose override).
        public object clipColl;          // loaded ClipCollection asset
        public int animId = -1;          // resolved animation id of our clip (after it's registered in AnimationManager.Apply)
        // STATE-DRIVEN (Phase 2, 2026-07-19): idle = the primary clip above; MOVE plays while the unit travels;
        // optional AFTER plays once on stopping. Each role is its own baked ClipCollection sharing the one skeleton.
        public bool animStateDriven;
        public int mca, mcb, mcc, mcd;   // MOVEMENT ClipCollection Amplitude guid
        public int aca, acb, acc, acd;   // AFTER-MOVEMENT ClipCollection Amplitude guid (0,0,0,0 = none)
        public int ata, atb, atc, atd;   // ATTACK ClipCollection Amplitude guid (0,0,0,0 = none) — played once when the pawn ranged-attacks
        public int cba, cbb, cbc, cbd;   // COMBAT-IDLE ClipCollection Amplitude guid (0,0,0,0 = none) — replaces IDLE while the army is locked in a battle
        public int pva, pvb, pvc, pvd;   // PRE-MOVEMENT ClipCollection Amplitude guid (0,0,0,0 = none) — played ONCE when the unit STARTS moving (e.g. a howitzer folding), then the Movement loop
        public int iea, ieb, iec, ied;   // IDLE-OVERRIDE ClipCollection Amplitude guid (0,0,0,0 = none) — a STANCE baked as a ROLE (real deltas vs the full primary clip's reference pose); the stance-as-PRIMARY trap encodes ~identity and renders as REST (the howitzer's "forgot to deploy")
        public int ala, alb, alc, ald;   // IDLE-ALT ClipCollection Amplitude guid (0,0,0,0 = none) — an OCCASIONAL flavor one-shot while plain-idle (the tiger's howl), played on the jittered idleAltInterval cadence; one pawn per unit type per firing
        public int a2a, a2b, a2c, a2d;   // IDLE-ALT 2 ClipCollection Amplitude guid (0,0,0,0 = none) — optional second flavor clip (eat/groom); each firing picks randomly between the two
        public object moveClipColl, afterClipColl, attackClipColl, combatClipColl, preMoveClipColl, idleClipColl, idleAltClipColl, idleAlt2ClipColl;
        public int moveAnimId = -1, afterAnimId = -1, attackAnimId = -1, combatAnimId = -1, preMoveAnimId = -1, idleAnimId = -1, idleAltAnimId = -1, idleAlt2AnimId = -1;
        public float moveDur = 1f, afterDur = 1f, attackDur = 1f, combatDur = 1f, preMoveDur = 1f, idleDur = 1f, idleAltDur = 1f, idleAlt2Dur = 1f;
        public float idleAltInterval = 0f;   // avg SECONDS between idle-alt one-shots (jittered 0.6-1.4x, like the idle growl); <=0 disables even when clips are baked
        public float idleAltNextAt, idleAltStart = -1f, idleAltChosenDur = 1f;   // session cadence state (per entry = one voice per unit type)
        public int idleAltChosenId = -1;
        public UnityEngine.Vector3 idleAltPos;   // which pawn is performing this firing (nearest-match, same 4u radius class)
        public int attackRepeats = 1;    // how many times the ATTACK clip replays per trigger (the fire window = repeats x clip duration; the GPU sampler's Repeat(Time,1) wraps each pass). For short recoil-pop source clips (shootAR2s = 0.17s) that should read as sustained fire. Runtime-only knob — no re-bake.
        // HAND PROP (weapon axis, 2026-07-19): a rigid Prop-Lab mesh glued to a bone of OUR skeleton — the soldier's
        // gun. The donor (an APC) has no weapon slots, so the plugin CONSTRUCTS the FragmentEntry itself at repoint
        // time instead of riding the vanilla slot path. All four are runtime-only registry strings.
        public string handPropName = "";  // the Prop Lab resource name (assets <name>_Collection / mesh <name>_DistrictMesh)
        public string handPropGuid = "";  // the <name>_Collection Amplitude guid "a,b,c,d" (Prop Lab prints + clipboards it)
        public string handPropMat = "";   // borrowed material guid "a,b,c,d"; "" = the shared EQ_DLC04_Weapons material
        public string handPropBone = "";  // bone-name SUBSTRING on OUR skeleton (bones are renamed b###_<orig>); "" = "R_Hand"
        public string handPropAngles = "";// draw-time rotation "x,y,z" (deg) stamped onto the FxMesh asset BEFORE encoding; "" stamps ZERO (neutralizes the engine's -90X class default — baked angle values don't survive the bundle). Hand-edited escape hatch: change + relaunch, no bake/rebuild.
        public bool disabled;             // DEBUG toggle: skip this override entirely so the ORIGINAL vanilla unit renders (compare against the custom model, observe the donor's own animation). Runtime-only — Save (no bake) + relaunch. The entry is dropped at load, so it doesn't even claim its pawn.
        public bool clearAimLayer;        // clear the game's procedural BoneRotation layer for THIS model (artillery: the donor streams aim/wheel junk that twists the rig). Replaces the old blanket fire/deploy rule for STATE-DRIVEN artillery — characters need the layer (facing), a migrated howitzer needs it cleared. Runtime-only.
        public string turretBone = "";    // TURRETIZE (2026-07-24): bone-name SUBSTRING (renamed b###_<orig>) of a turret to aim at the target. The game already streams its aim/heading angle into a BoneRotation slot on an INVALID bone index — we retarget that slot's SkeletonBoneIndex to THIS bone so the engine's own aim yaws our turret. "" = no turret aim. Runtime-only (no re-bake).
        public int turretBoneIdx = -2;    // cached bone index for turretBone (-2 = not resolved yet, -1 = not found). Resolved once from e.skeleton.BoneInfos.
        public int turretAxis = -1;       // aim-axis override for the turret bone: -1 = keep the game's streamed axis (1 = "up" in ITS frame, which on a bone pointing along its own length reads as PITCH); 0/1/2 = force the bone's local X/Y/Z. A vehicle TURRET needs its YAW axis; a mechanized HOWITZER/ARTILLERY barrel needs its PITCH axis — hence per-model.
        public object handPropLayer;      // session-scoped: our PRIVATE clone of the borrowed weapon output layer, painted with the prop's own atlas (<prop>_Atlas)
        public UnityEngine.Texture2D propAtlasTex;   // session-scoped: the prop atlas — repainted EVERY TICK like the unit retexture (the game resets the material; a one-shot paint flip-flopped between sessions)
        public readonly Dictionary<long, UnityEngine.Vector3> stateLastPos = new Dictionary<long, UnityEngine.Vector3>();  // MAIN thread poll: unit GUID -> last render pos
        public readonly Dictionary<long, bool> stateMoving = new Dictionary<long, bool>();                                 // unit GUID -> was moving last poll (detects the moving->stopped flip)
        public readonly Dictionary<long, float> stateStoppedAt = new Dictionary<long, float>();                            // unit GUID -> Time.time the unit stopped moving
        public readonly Dictionary<long, float> stateMoveStartedAt = new Dictionary<long, float>();                       // unit GUID -> Time.time the unit STARTED moving (the PRE-MOVEMENT one-shot window, e.g. the howitzer folding)
        public readonly List<StateSample> stateSamples = new List<StateSample>();   // published for the pose hook (lock on it); pos = pawn render position
        public int lastStateAnim = -2;   // diagnostic: last anim the state pose picked (log only on change)
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
        public bool silenceDonorAudio;         // SUPPRESS all of the borrowed donor's Wwise sound on this unit's pawns (idle growl + combat maul/scratch that ride in on the reused animator/description). Reusable: any unit that inherits an unwanted donor sound can set it. Silences ONLY Wwise (AudioEmitter.PostEvent) — our own custom WAVs (Unity AudioSource) still play, so it composes with soundIdleFile/soundFile.
        public string engineStartEvent = "";  // Wwise event NAME posted on move-START (e.g. Play_UNIT_Vehicles_StealthCorvette_Start). Set => posted BY NAME (works for the FIRST unit, no live capture); empty => fall back to the auto-captured handle.
        public string engineStopEvent = "";   // ... move-STOP (..._Stop). Extract names via the F8 "Dump Sound Catalog"; assign per unit in the registry.
        public string soundFile = "";          // CUSTOM audio, LOOP while moving: a WAV filename in BepInEx/config/enc_sounds/. Unity AudioSource, 3D. For units the game has NO sound for (drones, zeppelins) or a bespoke engine.
        public string soundStartFile = "";     // CUSTOM one-shot on move-START (spool-up): a WAV in enc_sounds/.
        public string soundStopFile = "";      // CUSTOM one-shot on move-STOP (spool-down): a WAV in enc_sounds/.
        public float soundVolume = 1f;         // travel-loop volume
        public float soundStartVolume = 1f;    // move-start one-shot volume
        public float soundStopVolume = 1f;     // move-stop one-shot volume
        public string soundIdleFile = "";      // CUSTOM one-shot growl played OCCASIONALLY WHILE IDLE (not moving): a WAV in enc_sounds/. Replaces a donor's periodic idle vocalization (pair with silenceDonorAudio). Fired on a randomized timer per pawn.
        public float soundIdleVolume = 1f;     // idle-growl one-shot volume
        public float soundIdleInterval = 11f;  // AVERAGE seconds between idle growls (jittered 0.6..1.4x per pawn so a pack doesn't chorus). <=0 disables.
        public float soundIdleGroupRadius = 10f; // GROUP de-dup: growls suppressed within this radius of another recent growl, so a clustered unit (many pawns) snarls with ONE voice per interval instead of all at once. <=0 = per-pawn (no de-dup).
        public string soundAttackFile = "";    // CUSTOM one-shot played ON ATTACK (each swing/shot) — a WAV in enc_sounds/. A DISTINCT, more violent sound than the idle growl; fired from OnPawnAttack with a per-pawn min-gap so rapid multi-swing fights don't machine-gun it.
        public float soundAttackVolume = 1f;   // attack one-shot volume
        public float soundAttackOffset = 0f;   // seconds INTO the attack WAV where playback starts (skip a silent/windup lead-in so the impact lands on the swing); 0 = from the top
        public string soundDeathFile = "";     // CUSTOM one-shot on a pawn's DEATH (PresentationPawn.TriggerDeath) — the rattle/scream that closes the unit's audio arc. Per-entry min-gap so a wiped stack doesn't chorus five at once.
        public float soundDeathVolume = 1f;    // death one-shot volume
        public float soundDeathOffset = 0f;    // seconds into the death WAV (same semantics as the attack offset)
        public string soundBattleFile = "";    // CUSTOM one-shot WAR CRY when a battle STARTS with this unit in it (SimulationEvent_BattleStarted; sim thread -> queued, played camera-anchored on the main thread). One cry per entry per battle.
        public float soundBattleVolume = 1f;   // war-cry volume
        public float soundBattleOffset = 0f;   // seconds into the war-cry WAV
        public UnityEngine.AudioClip customClip, customStartClip, customStopClip, customIdleClip, customAttackClip, customDeathClip, customBattleClip;    // loaded once from the files
        public float deathSoundNextAt, battleCryNextAt;   // per-entry min-gap clocks (a wiped stack / double battle shouldn't chorus)
        public readonly Dictionary<long, float> attackSoundNextAt = new Dictionary<long, float>();   // attacking-pawn id -> earliest Time.time it may play the attack sound again (min-gap)
        public bool customClipTried;                                                 // don't retry a failed load every poll
        public readonly Dictionary<int, float> idleNextAt = new Dictionary<int, float>();   // sub-pawn instance id -> Time.time of its next idle growl (jittered)
        public readonly List<KeyValuePair<UnityEngine.Vector3, float>> idleRecent = new List<KeyValuePair<UnityEngine.Vector3, float>>();  // recent growls (pos, Time.time) for group de-dup — pruned each poll
        public string assetDir = "";     // owning pack's asset root (set at registry load, never parsed from JSON): WAVs/PNGs resolve from <assetDir>/sounds|skins first, then the legacy shared enc_sounds/enc_skins
        public readonly Dictionary<int, UnityEngine.AudioSource> customSources = new Dictionary<int, UnityEngine.AudioSource>();  // sub-pawn instance id -> our looping AudioSource (played while moving)
        public readonly Dictionary<int, float> loopHoldUntil = new Dictionary<int, float>();   // instance id -> Time.time to hold the travel loop off until (so the spool-up one-shot isn't masked)
        public readonly Dictionary<int, UnityEngine.Vector3> engineLastPos = new Dictionary<int, UnityEngine.Vector3>();  // sub-pawn instance id -> last render pos
        public readonly Dictionary<int, bool> engineMoving = new Dictionary<int, bool>();                                  // sub-pawn instance id -> was moving last poll
    }

    // One in-flight one-shot: the world position of a pawn that just fired + when it started. The pose hook matches a
    // pawn to the nearest active fire by ObjectSpace position (both are Unity render coords), so only the firer animates.
    internal struct FireInstance { public UnityEngine.Vector3 pos; public float startTime; public long pawnId; }
    // A pawn's render position + the (ramped) normalized pose time its unit should currently hold, for the gradual deploy.
    internal struct DeploySample { public UnityEngine.Vector3 pos; public float poseTime; }

    internal static class UniversalInject
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static List<ModelEntry> entries;
        static bool loaded, registered, repointActiveLogged, stLogged, greyWaitLogged;
        static int loadAttempts;   // failed-load counter: latch `loaded` only after a success or a few tries, so a TRANSIENT read/parse error (AV scan, sharing violation at startup) retries instead of disabling injection for the whole session
        static UnityEngine.Texture2D _flatN, _white, _black, _grey;   // neutral overlay maps (kill the host's detail/camo)

        // A discovered mod PACK: one registry file's wrapper metadata + its models. HAF (Humankind Asset Framework)
        // multi-mod support merges many packs into `entries`, so ENC is just one mod among many — any modder ships their
        // own pack (config + assets) to join. The wrapper keys (modId/schemaVersion/dependsOn/loadAfter/overrides) sit
        // BESIDE the existing "models" array, so a legacy bare { "models": [...] } file still parses — with default metadata.
        class Pack
        {
            public string modId = "", file = "";
            public string assetDir = "";              // per-pack ASSET ROOT (2026-07-19): file-based assets (WAVs in sounds/, PNGs in skins/) resolve here FIRST, then fall back to the legacy shared enc_sounds/enc_skins — so a third-party pack ships self-contained instead of feeling like an ENC extension. "" (the base pack) = legacy folders only.
            public int schemaVersion;
            public List<string> dependsOn = new List<string>();
            public List<string> loadAfter = new List<string>();
            public List<PackOverride> overrides = new List<PackOverride>();   // ENFORCED since 2026-07-19: an explicit, declared replacement of another pack's entry
            public List<ModelEntry> models = new List<ModelEntry>();
        }
        // A declared cross-pack replacement: "this pack intentionally replaces <modId>'s entry on <pawnDescription>".
        // Declared = consensual under the HAF conflict philosophy (an UNdeclared clash is still first-loaded-wins, loud).
        class PackOverride { public string modId = "", pawn = ""; }

        static void LoadRegistry()
        {
            if (loaded) return;
            // Build into a LOCAL list and publish `entries` once, fully populated (reference assignment is atomic).
            // The old code published the empty list first and Add()ed into it — the sim-thread combat hook
            // (FindEntryForUnitDefinition) could then foreach over it mid-Add and throw (review 2026-07-19).
            entries = new List<ModelEntry>();   // readers see empty while we build
            var built = new List<ModelEntry>();
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
                {
                    var found = new List<string>(Directory.GetFiles(packDir, "*.json"));
                    // Subdirectory packs (2026-07-19): haf_packs/<mymod>/pack.json — the pack's own folder is its
                    // asset root (sounds/, skins/ inside it), so a pack ships as ONE self-contained directory.
                    foreach (var dir in Directory.GetDirectories(packDir))
                    {
                        var pj = Path.Combine(dir, "pack.json");
                        if (File.Exists(pj)) found.Add(pj);
                    }
                    files.AddRange(found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }
                if (files.Count == 0) { Plugin.Log.LogInfo("[Uni] no registry at " + basePath + " and no haf_packs/*.json"); loaded = true; return; }

                var packs = new List<Pack>();
                foreach (var file in files)
                {
                    // One unreadable pack must not sink the others — skip it loudly and keep going.
                    try { packs.Add(ParsePack(file, file == basePath)); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] pack '{Path.GetFileName(file)}' failed to parse ({ex.Message}); skipped"); }
                }
                if (packs.Count == 0) throw new Exception("no packs could be read");   // transient (lock/AV) — retry, don't latch

                // PACK RESOLUTION (enforced 2026-07-19): duplicate modIds rejected, dependsOn validated, load order
                // topologically sorted over dependsOn + loadAfter (stable — no declared constraints = the old
                // base-first + filename order, byte-identical), cycles fall back loudly. See ResolvePacks.
                var resolution = new List<string>();
                packs = ResolvePacks(packs, resolution);
                if (packs.Count == 0) throw new Exception("no packs survived resolution (see haf_load_report.txt)");

                // MERGE with explicit conflict detection. A model's identity is its pawnDescription (the physical pawn slot
                // — two skins can't ride one pawn). Policy: a DECLARED override (the pack's `overrides` array names the
                // owning modId + pawn) REPLACES the earlier entry — declared = consensual, logged as an override, not a
                // conflict. An UNdeclared clash stays FIRST-loaded wins, logged LOUD (no silent overrides — the HAF
                // conflict philosophy).
                var ownerMod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var ownerIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var conflicts = new List<string>();
                var applied = new List<string>();
                foreach (var pk in packs)
                    foreach (var e in pk.models)
                    {
                        e.assetDir = pk.assetDir;   // file-based assets (WAV/PNG) resolve pack-relative first
                        if (!string.IsNullOrEmpty(e.pawnDescription) && ownerMod.TryGetValue(e.pawnDescription, out var held))
                        {
                            bool declared = false;
                            foreach (var ov in pk.overrides)
                                if (string.Equals(ov.modId, held, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(ov.pawn, e.pawnDescription, StringComparison.OrdinalIgnoreCase))
                                { declared = true; break; }
                            if (declared)
                            {
                                built[ownerIdx[e.pawnDescription]] = e;   // replace in place — ownerIdx stays valid
                                ownerMod[e.pawnDescription] = pk.modId;
                                applied.Add($"pawn={e.pawnDescription} '{pk.modId}' replaces '{held}' (declared override)");
                                Plugin.Log.LogInfo($"[Uni] OVERRIDE: pack '{pk.modId}' replaces '{held}' on pawn '{e.pawnDescription}' (declared).");
                                continue;
                            }
                            conflicts.Add($"pawn={e.pawnDescription} kept={held} dropped={pk.modId}({e.resourceName})");
                            Plugin.Log.LogWarning($"[Uni] CONFLICT: pack '{pk.modId}' targets pawn '{e.pawnDescription}' already claimed by '{held}' — keeping '{held}' (first-loaded wins; declare it in `overrides` to replace).");
                            continue;
                        }
                        if (e.disabled) { Plugin.Log.LogInfo($"[Uni] '{e.resourceName}' -> '{e.pawnDescription}': DISABLED in registry — skipping override (original unit rendered)."); continue; }
                        if (!string.IsNullOrEmpty(e.pawnDescription)) { ownerMod[e.pawnDescription] = pk.modId; ownerIdx[e.pawnDescription] = built.Count; }
                        built.Add(e);
                    }

                WriteLoadReport(packs, built.Count, conflicts, applied, resolution);
                Plugin.Log.LogInfo($"[Uni] loaded {packs.Count} pack(s), {built.Count} model(s), {conflicts.Count} conflict(s), {applied.Count} override(s) [" + string.Join(", ", packs.Select(p => p.modId + "×" + p.models.Count)) + "]");
                entries = built;   // publish fully built — never mutated after this point
                // Invalidate the pawn-hook early-out caches: if pawns spawned during a transient-failure retry window
                // they latched anyAnimated/anyFreeze against the EMPTY list — without this, a recovered retry would
                // repoint meshes but leave every animated/freeze behavior dead for the session (review round 2).
                anyAnimated = null; anyFreeze = null;
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
            bool isDirPack = !isBase && string.Equals(Path.GetFileName(file), "pack.json", StringComparison.OrdinalIgnoreCase);
            var pk = new Pack
            {
                file = file,
                // default modId: the DIRECTORY name for a subdirectory pack (every one is named pack.json — the
                // filename would collide as "pack"), else the filename; an explicit "modId" key overrides below.
                modId = isBase ? "enc" : isDirPack ? Path.GetFileName(Path.GetDirectoryName(file)) : Path.GetFileNameWithoutExtension(file),
                // asset root: the pack's own directory for a subdirectory pack; for a flat haf_packs/<name>.json, a
                // sibling folder of the same name (may not exist — resolution then falls through to the legacy
                // shared folders); the base pack keeps the legacy enc_sounds/enc_skins.
                assetDir = isBase ? "" : isDirPack ? Path.GetDirectoryName(file)
                                                   : Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file)),
            };
            try
            {
                var root = JObject.Parse(text);
                if (root["modId"] != null) pk.modId = (string)root["modId"];
                if (root["schemaVersion"] != null) pk.schemaVersion = (int)root["schemaVersion"];
                pk.dependsOn = StrList(root["dependsOn"]);
                pk.loadAfter = StrList(root["loadAfter"]);
                if (root["overrides"] is JArray ovs)
                    foreach (var ov in ovs)
                    {
                        string om = (string)ov["modId"] ?? "", op = (string)ov["pawnDescription"] ?? "";
                        if (om.Length > 0 && op.Length > 0) pk.overrides.Add(new PackOverride { modId = om, pawn = op });
                    }
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
        // PACK RESOLUTION (2026-07-19, was reserved-and-logged): enforce what the wrapper declares, in this order —
        // duplicate modIds rejected (first file keeps the id), dependsOn validated (a missing dependency SKIPS the
        // pack; iterated to a fixpoint since a skip can strand a dependent), then a STABLE topological sort over
        // dependsOn + loadAfter edges. Stability matters: ready packs are picked in SEED order (base first, then
        // filename order), so with no declared constraints the result is byte-identical to the pre-resolution order —
        // today's single-pack setup is provably unaffected. loadAfter naming an absent modId is soft (ignored);
        // a dependency cycle appends its members in seed order with a loud warning. `notes` feeds the load report.
        static List<Pack> ResolvePacks(List<Pack> packs, List<string> notes)
        {
            // -- duplicate modIds --
            var byId = new Dictionary<string, Pack>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<Pack>();
            foreach (var pk in packs)
            {
                if (byId.TryGetValue(pk.modId, out var first))
                {
                    notes.Add($"SKIPPED '{Path.GetFileName(pk.file)}': duplicate modId '{pk.modId}' (kept '{Path.GetFileName(first.file)}')");
                    Plugin.Log.LogWarning($"[Uni] pack '{Path.GetFileName(pk.file)}' skipped: duplicate modId '{pk.modId}' — already claimed by '{Path.GetFileName(first.file)}'.");
                    continue;
                }
                byId[pk.modId] = pk; kept.Add(pk);
            }
            // -- dependsOn (hard requirement, fixpoint) --
            bool removedAny = true;
            while (removedAny)
            {
                removedAny = false;
                for (int i = kept.Count - 1; i >= 0; i--)
                    foreach (var dep in kept[i].dependsOn)
                        if (!kept.Any(x => string.Equals(x.modId, dep, StringComparison.OrdinalIgnoreCase)))
                        {
                            notes.Add($"SKIPPED '{kept[i].modId}': dependsOn '{dep}' is not loaded");
                            Plugin.Log.LogWarning($"[Uni] pack '{kept[i].modId}' skipped: dependsOn '{dep}' is not loaded.");
                            kept.RemoveAt(i); removedAny = true; break;
                        }
            }
            // -- stable topological order (Kahn, seed-order picks) --
            var ordered = new List<Pack>();
            var pending = new List<Pack>(kept);
            while (pending.Count > 0)
            {
                Pack pick = null;
                foreach (var pk in pending)
                {
                    bool ready = true;
                    foreach (var pre in pk.dependsOn.Concat(pk.loadAfter))
                        if (pending.Any(x => !ReferenceEquals(x, pk) && string.Equals(x.modId, pre, StringComparison.OrdinalIgnoreCase)))
                        { ready = false; break; }
                    if (ready) { pick = pk; break; }
                }
                if (pick == null)   // every pending pack waits on another pending pack = a cycle
                {
                    notes.Add("CYCLE in dependsOn/loadAfter among: " + string.Join(", ", pending.Select(p => p.modId)) + " — file order used for these");
                    Plugin.Log.LogWarning("[Uni] pack ordering CYCLE among: " + string.Join(", ", pending.Select(p => p.modId)) + " — falling back to file order.");
                    ordered.AddRange(pending);
                    break;
                }
                ordered.Add(pick); pending.Remove(pick);
            }
            for (int i = 0; i < ordered.Count; i++)
                if (!ReferenceEquals(ordered[i], kept[i])) { notes.Add("load order (after sort): " + string.Join(" → ", ordered.Select(p => p.modId))); break; }
            return ordered;
        }

        static void WriteLoadReport(List<Pack> packs, int total, List<string> conflicts, List<string> applied, List<string> resolution)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("HAF load report  (regenerated every load)");
                sb.AppendLine($"packs={packs.Count}  models={total}  conflicts={conflicts.Count}  overrides applied={applied.Count}");
                sb.AppendLine();
                foreach (var p in packs)
                {
                    sb.AppendLine($"[{p.modId}]  schemaVersion={p.schemaVersion}  models={p.models.Count}  file={Path.GetFileName(p.file)}");
                    if (p.dependsOn.Count > 0) sb.AppendLine("    dependsOn: " + string.Join(", ", p.dependsOn));
                    if (p.loadAfter.Count > 0) sb.AppendLine("    loadAfter: " + string.Join(", ", p.loadAfter));
                    if (p.overrides.Count > 0) sb.AppendLine("    overrides declared: " + string.Join(", ", p.overrides.Select(o => o.modId + ":" + o.pawn)));
                }
                if (resolution.Count > 0) { sb.AppendLine(); sb.AppendLine("RESOLUTION:"); foreach (var r in resolution) sb.AppendLine("  " + r); }
                if (applied.Count > 0) { sb.AppendLine(); sb.AppendLine("OVERRIDES APPLIED (declared replacements):"); foreach (var a in applied) sb.AppendLine("  " + a); }
                if (conflicts.Count > 0) { sb.AppendLine(); sb.AppendLine("CONFLICTS (undeclared — first-loaded kept; declare in `overrides` to replace):"); foreach (var c in conflicts) sb.AppendLine("  " + c); }
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
                            var cmv = m["clipMove"]; var cfa = m["clipAfter"]; var cat = m["clipAttack"]; var ccb = m["clipCombat"]; var cpv = m["clipPreMove"]; var cid = m["clipIdle"]; var cAlt = m["clipIdleAlt"]; var ca2 = m["clipIdleAlt2"];
                            entries.Add(new ModelEntry
                            {
                                resourceName = (string)m["resourceName"] ?? "", pawnDescription = (string)m["pawnDescription"] ?? "", hideMeshes = (string)m["hideMeshes"] ?? "",
                                sa = A(s, 0), sb = A(s, 1), sc = A(s, 2), sd = A(s, 3),
                                ta = A(t, 0), tb = A(t, 1), tc = A(t, 2), td = A(t, 3),
                                ca = A(c, 0), cb = A(c, 1), cc = A(c, 2), cd = A(c, 3),
                                animStateDriven = (bool?)m["animStateDriven"] ?? false,
                                mca = A(cmv, 0), mcb = A(cmv, 1), mcc = A(cmv, 2), mcd = A(cmv, 3),
                                aca = A(cfa, 0), acb = A(cfa, 1), acc = A(cfa, 2), acd = A(cfa, 3),
                                ata = A(cat, 0), atb = A(cat, 1), atc = A(cat, 2), atd = A(cat, 3),
                                cba = A(ccb, 0), cbb = A(ccb, 1), cbc = A(ccb, 2), cbd = A(ccb, 3),
                                pva = A(cpv, 0), pvb = A(cpv, 1), pvc = A(cpv, 2), pvd = A(cpv, 3),
                                iea = A(cid, 0), ieb = A(cid, 1), iec = A(cid, 2), ied = A(cid, 3),
                                ala = A(cAlt, 0), alb = A(cAlt, 1), alc = A(cAlt, 2), ald = A(cAlt, 3),
                                a2a = A(ca2, 0), a2b = A(ca2, 1), a2c = A(ca2, 2), a2d = A(ca2, 3),
                                idleAltInterval = m["idleAltInterval"] != null ? (float)m["idleAltInterval"] : 0f,
                                position = new UnityEngine.Vector3(Fp(p, "x"), Fp(p, "y"), Fp(p, "z")),
                                scale = m["scale"] != null ? (float)m["scale"] : 1f,
                                brightness = m["brightness"] != null ? (float)m["brightness"] : 1f,
                                desaturate = m["desaturate"] != null ? (float)m["desaturate"] : 0f,
                                tintR = m["tintR"] != null ? (float)m["tintR"] : 0f,
                                tintG = m["tintG"] != null ? (float)m["tintG"] : 0f,
                                tintB = m["tintB"] != null ? (float)m["tintB"] : 0f,
                                textureFile = (string)m["textureFile"] ?? "",
                                handPropName = (string)m["handPropName"] ?? "",
                                handPropGuid = (string)m["handPropGuid"] ?? "",
                                handPropMat = (string)m["handPropMat"] ?? "",
                                handPropBone = (string)m["handPropBone"] ?? "",
                                handPropAngles = (string)m["handPropAngles"] ?? "",
                                respawnAfterLoad = (bool?)m["respawnAfterLoad"] ?? false,
                                freezeDonorAnim = (bool?)m["freezeDonorAnim"] ?? false,
                                disabled = (bool?)m["disabled"] ?? false,
                                clearAimLayer = (bool?)m["clearAimLayer"] ?? false,
                                turretBone = (string)m["turretBone"] ?? "",
                                turretAxis = (int?)m["turretAxis"] ?? -1,
                                fireOnAttack = (bool?)m["fireOnAttack"] ?? false,
                                deployOnStop = (bool?)m["deployOnStop"] ?? false,
                                engineSound = (bool?)m["engineSound"] ?? false,
                                silenceDonorAudio = (bool?)m["silenceDonorAudio"] ?? false,
                                engineStartEvent = (string)m["engineStartEvent"] ?? "",
                                engineStopEvent = (string)m["engineStopEvent"] ?? "",
                                soundFile = (string)m["soundFile"] ?? "",
                                soundStartFile = (string)m["soundStartFile"] ?? "",
                                soundStopFile = (string)m["soundStopFile"] ?? "",
                                soundVolume = m["soundVolume"] != null ? (float)m["soundVolume"] : 1f,
                                soundStartVolume = m["soundStartVolume"] != null ? (float)m["soundStartVolume"] : 1f,
                                soundStopVolume = m["soundStopVolume"] != null ? (float)m["soundStopVolume"] : 1f,
                                soundIdleFile = (string)m["soundIdleFile"] ?? "",
                                soundIdleVolume = m["soundIdleVolume"] != null ? (float)m["soundIdleVolume"] : 1f,
                                soundIdleInterval = m["soundIdleInterval"] != null ? (float)m["soundIdleInterval"] : 11f,
                                soundIdleGroupRadius = m["soundIdleGroupRadius"] != null ? (float)m["soundIdleGroupRadius"] : 10f,
                                soundAttackFile = (string)m["soundAttackFile"] ?? "",
                                soundAttackVolume = m["soundAttackVolume"] != null ? (float)m["soundAttackVolume"] : 1f,
                                soundAttackOffset = m["soundAttackOffset"] != null ? (float)m["soundAttackOffset"] : 0f,
                                soundDeathFile = (string)m["soundDeathFile"] ?? "",
                                soundDeathVolume = m["soundDeathVolume"] != null ? (float)m["soundDeathVolume"] : 1f,
                                soundDeathOffset = m["soundDeathOffset"] != null ? (float)m["soundDeathOffset"] : 0f,
                                soundBattleFile = (string)m["soundBattleFile"] ?? "",
                                soundBattleVolume = m["soundBattleVolume"] != null ? (float)m["soundBattleVolume"] : 1f,
                                soundBattleOffset = m["soundBattleOffset"] != null ? (float)m["soundBattleOffset"] : 0f,
                                deployPoseTime = m["deployPoseTime"] != null ? (float)m["deployPoseTime"] : 1f,
                                attackRepeats = m["attackRepeats"] != null ? (int)m["attackRepeats"] : 1,
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
                var cmvR = Regex.Matches(text, "\"clipMove\"\\s*:\\s*" + i4);    // parity: STATE-DRIVEN movement ClipCollection guid
                var cfaR = Regex.Matches(text, "\"clipAfter\"\\s*:\\s*" + i4);   // parity: STATE-DRIVEN after-movement ClipCollection guid
                var catR = Regex.Matches(text, "\"clipAttack\"\\s*:\\s*" + i4);  // parity: STATE-DRIVEN attack ClipCollection guid
                var ccbR = Regex.Matches(text, "\"clipCombat\"\\s*:\\s*" + i4);  // parity: STATE-DRIVEN combat-idle ClipCollection guid
                var cpvR = Regex.Matches(text, "\"clipPreMove\"\\s*:\\s*" + i4); // parity: STATE-DRIVEN pre-movement ClipCollection guid
                var cidR = Regex.Matches(text, "\"clipIdle\"\\s*:\\s*" + i4);   // parity: STATE-DRIVEN idle-override ClipCollection guid
                var calR = Regex.Matches(text, "\"clipIdleAlt\"\\s*:\\s*" + i4);   // parity: idle-alt flavor one-shot ClipCollection guid
                var ca2R = Regex.Matches(text, "\"clipIdleAlt2\"\\s*:\\s*" + i4);  // parity: second idle-alt flavor ClipCollection guid
                var iai = Regex.Matches(text, "\"idleAltInterval\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: avg seconds between idle-alt one-shots
                var asd = Regex.Matches(text, "\"animStateDriven\"\\s*:\\s*(true|false)");   // parity: state-driven mode flag
                // position object {x,y,z} — JsonUtility writes Vector3 in x,y,z order. Applied as a runtime world offset for animated models.
                var po = Regex.Matches(text, "\"position\"\\s*:\\s*\\{\\s*\"x\"\\s*:\\s*(-?[\\d.eE+]+)\\s*,\\s*\"y\"\\s*:\\s*(-?[\\d.eE+]+)\\s*,\\s*\"z\"\\s*:\\s*(-?[\\d.eE+]+)");
                var ra = Regex.Matches(text, "\"respawnAfterLoad\"\\s*:\\s*(true|false)");   // parity with the Newtonsoft path (line ~77) — else the first-instance rotor fix silently defaults off here
                var fz = Regex.Matches(text, "\"freezeDonorAnim\"\\s*:\\s*(true|false)");   // parity with the Newtonsoft path — else the donor-animation freeze silently defaults off here
                var cal = Regex.Matches(text, "\"clearAimLayer\"\\s*:\\s*(true|false)");    // parity: per-model aim-layer clear (state-driven artillery)
                var dsb = Regex.Matches(text, "\"disabled\"\\s*:\\s*(true|false)");         // parity: per-model DEBUG disable (show original unit)
                var foa = Regex.Matches(text, "\"fireOnAttack\"\\s*:\\s*(true|false)");     // parity: play the clip once on attack vs loop
                var dos = Regex.Matches(text, "\"deployOnStop\"\\s*:\\s*(true|false)");     // parity: hold deployed when idle, undeploy while moving
                var eng = Regex.Matches(text, "\"engineSound\"\\s*:\\s*(true|false)");      // parity: fire the per-ship engine move sound on our units
                var sda = Regex.Matches(text, "\"silenceDonorAudio\"\\s*:\\s*(true|false)"); // parity: suppress the borrowed donor's Wwise sound (idle + combat)
                var esa = Regex.Matches(text, "\"engineStartEvent\"\\s*:\\s*\"([^\"]*)\"");  // parity: Wwise event name posted on move-start
                var eso = Regex.Matches(text, "\"engineStopEvent\"\\s*:\\s*\"([^\"]*)\"");    // parity: Wwise event name posted on move-stop
                var sf = Regex.Matches(text, "\"soundFile\"\\s*:\\s*\"([^\"]*)\"");           // parity: custom WAV loop in enc_sounds/
                var sfa = Regex.Matches(text, "\"soundStartFile\"\\s*:\\s*\"([^\"]*)\"");     // parity: custom WAV one-shot on move-start
                var sfo = Regex.Matches(text, "\"soundStopFile\"\\s*:\\s*\"([^\"]*)\"");      // parity: custom WAV one-shot on move-stop
                var svl = Regex.Matches(text, "\"soundVolume\"\\s*:\\s*(-?[\\d.eE+]+)");       // parity: travel-loop volume
                var svs = Regex.Matches(text, "\"soundStartVolume\"\\s*:\\s*(-?[\\d.eE+]+)");  // parity: start one-shot volume
                var svp = Regex.Matches(text, "\"soundStopVolume\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: stop one-shot volume
                var sif = Regex.Matches(text, "\"soundIdleFile\"\\s*:\\s*\"([^\"]*)\"");      // parity: custom WAV growl played occasionally while idle
                var siv = Regex.Matches(text, "\"soundIdleVolume\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: idle-growl one-shot volume
                var sii = Regex.Matches(text, "\"soundIdleInterval\"\\s*:\\s*(-?[\\d.eE+]+)"); // parity: avg seconds between idle growls
                var sig = Regex.Matches(text, "\"soundIdleGroupRadius\"\\s*:\\s*(-?[\\d.eE+]+)"); // parity: group de-dup radius for idle growls
                var saf = Regex.Matches(text, "\"soundAttackFile\"\\s*:\\s*\"([^\"]*)\"");        // parity: custom WAV one-shot on attack
                var sav = Regex.Matches(text, "\"soundAttackVolume\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: attack one-shot volume
                var sao = Regex.Matches(text, "\"soundAttackOffset\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: attack one-shot start offset (s into the WAV)
                var sdf = Regex.Matches(text, "\"soundDeathFile\"\\s*:\\s*\"([^\"]*)\"");         // parity: custom WAV one-shot on death
                var sdv = Regex.Matches(text, "\"soundDeathVolume\"\\s*:\\s*(-?[\\d.eE+]+)");     // parity: death one-shot volume
                var sdo = Regex.Matches(text, "\"soundDeathOffset\"\\s*:\\s*(-?[\\d.eE+]+)");     // parity: death one-shot start offset
                var sbf = Regex.Matches(text, "\"soundBattleFile\"\\s*:\\s*\"([^\"]*)\"");        // parity: custom WAV war cry on battle start
                var sbv = Regex.Matches(text, "\"soundBattleVolume\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: war-cry volume
                var sbo = Regex.Matches(text, "\"soundBattleOffset\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: war-cry start offset
                var dpt = Regex.Matches(text, "\"deployPoseTime\"\\s*:\\s*(-?[\\d.eE+]+)"); // parity: normalized clip time of the deployed pose (default 1)
                var arp = Regex.Matches(text, "\"attackRepeats\"\\s*:\\s*(-?[\\d.eE+]+)");  // parity: STATE-DRIVEN attack clip replay count per trigger (default 1)
                var dsp = Regex.Matches(text, "\"deploySpeed\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: gradual-deploy ramp speed multiplier (default 1)
                var rsp = Regex.Matches(text, "\"recoilSpeed\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: recoil-on-fire playback speed multiplier (default 1)
                var sc = Regex.Matches(text, "\"scale\"\\s*:\\s*(-?[\\d.eE+]+)");           // runtime ObjectSpace scale multiplier (default 1)
                var bri = Regex.Matches(text, "\"brightness\"\\s*:\\s*(-?[\\d.eE+]+)");     // universal skin brightness gamma (default 1 = unchanged)
                var des = Regex.Matches(text, "\"desaturate\"\\s*:\\s*(-?[\\d.eE+]+)");     // texture-only grey strength (default 0 = off)
                var tR = Regex.Matches(text, "\"tintR\"\\s*:\\s*(-?[\\d.eE+]+)");           // universal skin colour offset R (-255..255)
                var tG = Regex.Matches(text, "\"tintG\"\\s*:\\s*(-?[\\d.eE+]+)");           // ... G
                var tB = Regex.Matches(text, "\"tintB\"\\s*:\\s*(-?[\\d.eE+]+)");           // ... B
                var txf = Regex.Matches(text, "\"textureFile\"\\s*:\\s*\"([^\"]*)\"");      // texture-only retexture: PNG filename in enc_skins/
                var hpn = Regex.Matches(text, "\"handPropName\"\\s*:\\s*\"([^\"]*)\"");     // parity: hand-prop resource name
                var hpg = Regex.Matches(text, "\"handPropGuid\"\\s*:\\s*\"([^\"]*)\"");     // parity: hand-prop collection guid csv
                var hpm = Regex.Matches(text, "\"handPropMat\"\\s*:\\s*\"([^\"]*)\"");      // parity: hand-prop borrowed material guid csv
                var hpb = Regex.Matches(text, "\"handPropBone\"\\s*:\\s*\"([^\"]*)\"");     // parity: hand-prop bone-name substring
                var tbn = Regex.Matches(text, "\"turretBone\"\\s*:\\s*\"([^\"]*)\"");       // parity: turret aim bone-name substring
                var tax = Regex.Matches(text, "\"turretAxis\"\\s*:\\s*(-?\\d+)");           // parity: turret aim-axis override
                var hpa = Regex.Matches(text, "\"handPropAngles\"\\s*:\\s*\"([^\"]*)\"");   // parity: hand-prop draw-time import angles csv
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
                        animStateDriven = i < asd.Count && asd[i].Groups[1].Value == "true",
                        mca = i < cmvR.Count ? G(cmvR[i], 1) : 0, mcb = i < cmvR.Count ? G(cmvR[i], 2) : 0, mcc = i < cmvR.Count ? G(cmvR[i], 3) : 0, mcd = i < cmvR.Count ? G(cmvR[i], 4) : 0,
                        aca = i < cfaR.Count ? G(cfaR[i], 1) : 0, acb = i < cfaR.Count ? G(cfaR[i], 2) : 0, acc = i < cfaR.Count ? G(cfaR[i], 3) : 0, acd = i < cfaR.Count ? G(cfaR[i], 4) : 0,
                        ata = i < catR.Count ? G(catR[i], 1) : 0, atb = i < catR.Count ? G(catR[i], 2) : 0, atc = i < catR.Count ? G(catR[i], 3) : 0, atd = i < catR.Count ? G(catR[i], 4) : 0,
                        cba = i < ccbR.Count ? G(ccbR[i], 1) : 0, cbb = i < ccbR.Count ? G(ccbR[i], 2) : 0, cbc = i < ccbR.Count ? G(ccbR[i], 3) : 0, cbd = i < ccbR.Count ? G(ccbR[i], 4) : 0,
                        pva = i < cpvR.Count ? G(cpvR[i], 1) : 0, pvb = i < cpvR.Count ? G(cpvR[i], 2) : 0, pvc = i < cpvR.Count ? G(cpvR[i], 3) : 0, pvd = i < cpvR.Count ? G(cpvR[i], 4) : 0,
                        iea = i < cidR.Count ? G(cidR[i], 1) : 0, ieb = i < cidR.Count ? G(cidR[i], 2) : 0, iec = i < cidR.Count ? G(cidR[i], 3) : 0, ied = i < cidR.Count ? G(cidR[i], 4) : 0,
                        ala = i < calR.Count ? G(calR[i], 1) : 0, alb = i < calR.Count ? G(calR[i], 2) : 0, alc = i < calR.Count ? G(calR[i], 3) : 0, ald = i < calR.Count ? G(calR[i], 4) : 0,
                        a2a = i < ca2R.Count ? G(ca2R[i], 1) : 0, a2b = i < ca2R.Count ? G(ca2R[i], 2) : 0, a2c = i < ca2R.Count ? G(ca2R[i], 3) : 0, a2d = i < ca2R.Count ? G(ca2R[i], 4) : 0,
                        idleAltInterval = i < iai.Count && float.TryParse(iai[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _iai) ? _iai : 0f,
                        position = i < po.Count ? new UnityEngine.Vector3(F(po[i], 1), F(po[i], 2), F(po[i], 3)) : UnityEngine.Vector3.zero,
                        respawnAfterLoad = i < ra.Count && ra[i].Groups[1].Value == "true",
                        freezeDonorAnim = i < fz.Count && fz[i].Groups[1].Value == "true",
                        disabled = i < dsb.Count && dsb[i].Groups[1].Value == "true",
                        clearAimLayer = i < cal.Count && cal[i].Groups[1].Value == "true",
                        fireOnAttack = i < foa.Count && foa[i].Groups[1].Value == "true",
                        deployOnStop = i < dos.Count && dos[i].Groups[1].Value == "true",
                        engineSound = i < eng.Count && eng[i].Groups[1].Value == "true",
                        silenceDonorAudio = i < sda.Count && sda[i].Groups[1].Value == "true",
                        engineStartEvent = i < esa.Count ? esa[i].Groups[1].Value : "",
                        engineStopEvent = i < eso.Count ? eso[i].Groups[1].Value : "",
                        soundFile = i < sf.Count ? sf[i].Groups[1].Value : "",
                        soundStartFile = i < sfa.Count ? sfa[i].Groups[1].Value : "",
                        soundStopFile = i < sfo.Count ? sfo[i].Groups[1].Value : "",
                        soundVolume = i < svl.Count && float.TryParse(svl[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svl) ? _svl : 1f,
                        soundStartVolume = i < svs.Count && float.TryParse(svs[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svs) ? _svs : 1f,
                        soundStopVolume = i < svp.Count && float.TryParse(svp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svp) ? _svp : 1f,
                        soundIdleFile = i < sif.Count ? sif[i].Groups[1].Value : "",
                        soundIdleVolume = i < siv.Count && float.TryParse(siv[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _siv) ? _siv : 1f,
                        soundIdleInterval = i < sii.Count && float.TryParse(sii[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sii) ? _sii : 11f,
                        soundIdleGroupRadius = i < sig.Count && float.TryParse(sig[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sig) ? _sig : 10f,
                        soundAttackFile = i < saf.Count ? saf[i].Groups[1].Value : "",
                        soundAttackVolume = i < sav.Count && float.TryParse(sav[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sav) ? _sav : 1f,
                        soundAttackOffset = i < sao.Count && float.TryParse(sao[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sao) ? _sao : 0f,
                        soundDeathFile = i < sdf.Count ? sdf[i].Groups[1].Value : "",
                        soundDeathVolume = i < sdv.Count && float.TryParse(sdv[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sdv) ? _sdv : 1f,
                        soundDeathOffset = i < sdo.Count && float.TryParse(sdo[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sdo) ? _sdo : 0f,
                        soundBattleFile = i < sbf.Count ? sbf[i].Groups[1].Value : "",
                        soundBattleVolume = i < sbv.Count && float.TryParse(sbv[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sbv) ? _sbv : 1f,
                        soundBattleOffset = i < sbo.Count && float.TryParse(sbo[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sbo) ? _sbo : 0f,
                        deployPoseTime = i < dpt.Count && float.TryParse(dpt[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _dpt) ? _dpt : 1f,
                        attackRepeats = i < arp.Count && int.TryParse(arp[i].Groups[1].Value, out var _arp) ? _arp : 1,
                        deploySpeed = i < dsp.Count && float.TryParse(dsp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _dsp) ? _dsp : 1f,
                        recoilSpeed = i < rsp.Count && float.TryParse(rsp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _rsp) ? _rsp : 1f,
                        scale = i < sc.Count && float.TryParse(sc[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sc) ? _sc : 1f,
                        brightness = i < bri.Count && float.TryParse(bri[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _bri) ? _bri : 1f,
                        desaturate = i < des.Count && float.TryParse(des[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _des) ? _des : 0f,
                        tintR = i < tR.Count && float.TryParse(tR[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tr) ? _tr : 0f,
                        tintG = i < tG.Count && float.TryParse(tG[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tg) ? _tg : 0f,
                        tintB = i < tB.Count && float.TryParse(tB[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tb) ? _tb : 0f,
                        textureFile = i < txf.Count ? txf[i].Groups[1].Value : "",
                        handPropName = i < hpn.Count ? hpn[i].Groups[1].Value : "",
                        handPropGuid = i < hpg.Count ? hpg[i].Groups[1].Value : "",
                        handPropMat = i < hpm.Count ? hpm[i].Groups[1].Value : "",
                        handPropBone = i < hpb.Count ? hpb[i].Groups[1].Value : "",
                        turretBone = i < tbn.Count ? tbn[i].Groups[1].Value : "",
                        turretAxis = i < tax.Count && int.TryParse(tax[i].Groups[1].Value, out var _tax) ? _tax : -1,
                        handPropAngles = i < hpa.Count ? hpa[i].Groups[1].Value : "",
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
            // Snapshot the reference: this runs on the SIM thread (combat hook) while the main thread may republish
            // `entries` (LoadRegistry retry). The published list is never mutated after publish, so iterating a
            // snapshot is safe; iterating the field directly was not (review 2026-07-19).
            var list = entries;
            if (list == null || string.IsNullOrEmpty(unitDefText)) return null;
            foreach (var e in list)
            {
                if (string.IsNullOrEmpty(e.pawnDescription)) continue;
                var core = Regex.Replace(e.pawnDescription, "_\\d+$", "");   // drop the trailing _NN instance suffix
                if (core.Length > 4 && unitDefText.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0) return e;
            }
            return null;
        }

        // SESSION RE-ARM (review 2026-07-19): AnimationLoad fires per game-session and the manager's collection list is
        // rebuilt — the props axis re-arms on every fire (proven in-game), but the core model axis latched `registered`
        // once per PROCESS: load a second game in the same app run and EnsureRegistered no-ops, leaving our skeletons
        // unregistered and every learned id (skeletonId/animId/descId) stale against the new session's assignments.
        // Called from the AnimationLoad postfix, right before EnsureRegistered re-registers into the fresh manager.
        internal static void RearmModelRegistration()
        {
            registered = false;
            anyAnimated = null; anyFreeze = null;                    // recomputed on the next pawn-add
            _listenerChecked = false;                                // the AudioListener rode a session-scoped camera
            var list = entries;
            if (list != null)
                foreach (var e in list)
                {
                    e.skeletonId = -1; e.animId = -1; e.descId = -1; e.repointed = false;   // session-scoped ids re-learn
                    e.moveAnimId = -1; e.afterAnimId = -1; e.attackAnimId = -1; e.combatAnimId = -1; e.preMoveAnimId = -1; e.idleAnimId = -1; e.idleAltAnimId = -1; e.idleAlt2AnimId = -1;   // state-role ids re-resolve
                    e.idleAltNextAt = 0f; e.idleAltStart = -1f; e.idleAltChosenId = -1;   // idle-alt cadence is session-scoped (Time.time resets)
                    e.stateLastPos.Clear(); e.stateMoving.Clear(); e.stateStoppedAt.Clear(); e.stateMoveStartedAt.Clear();
                    lock (e.stateSamples) e.stateSamples.Clear();
                    lock (e.activeFires) e.activeFires.Clear();                              // session-1 fire windows (positions + Time.time) are meaningless in the new session
                    // Retexture/isolation state is session-scoped too: the isolated layer is a clone of a SESSION-1
                    // output layer and the adjusted atlas was dumped from it — handing either to the new session's
                    // content manager re-injects dead objects. Cheap to re-derive; destroy the texture we created.
                    e.isolatedLayer = null; e.hostOutputLayer = null; e.handPropLayer = null; e.propAtlasTex = null;
                    if (e.tex != null) { try { UnityEngine.Object.Destroy(e.tex); } catch { } e.tex = null; }
                    // Per-instance state keyed by session-scoped ids (unit GUIDs / sub-pawn instance ids): a new game
                    // can REUSE those ids, so stale entries would feed the first poll wrong moving/deploy decisions,
                    // and the maps otherwise only ever grow. The AudioSources rode session-1 pawn objects (destroyed
                    // with them) — dropping the references is enough.
                    e.deployProgress.Clear(); e.deployLastPos.Clear();
                    e.customSources.Clear(); e.loopHoldUntil.Clear(); e.engineLastPos.Clear(); e.engineMoving.Clear();
                }
            deployMoveState = null;                                  // diagnostic map, unit GUIDs are session-scoped
            respawnBase.Clear(); respawnCount.Clear();               // keyed by session-1 unit objects
            // DISTRICT axis session state (same bug class): the FxManager and each entry's leaves/private clone were
            // captured from session-1 presentation objects — reusing them in a second game points at torn-down GPU
            // state. Null everything; DistrictApplyEntries re-derives per district instance as the new session loads.
            distFxManager = null;
            foreach (var d in distModels)
            { d.plbc = null; d.privateLeaf = null; d.leaves.Clear(); d.collected = false; d.wait = 0; d.matchLogged = false; d.pointedLogged = false; }
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
                    if (e.moveClipColl != null) e.moveAnimId = ResolveCollAnimId(animMgr, e.moveClipColl, e.resourceName + ":move", out e.moveDur);
                    if (e.afterClipColl != null) e.afterAnimId = ResolveCollAnimId(animMgr, e.afterClipColl, e.resourceName + ":after", out e.afterDur);
                    if (e.attackClipColl != null) e.attackAnimId = ResolveCollAnimId(animMgr, e.attackClipColl, e.resourceName + ":attack", out e.attackDur);
                    if (e.combatClipColl != null) e.combatAnimId = ResolveCollAnimId(animMgr, e.combatClipColl, e.resourceName + ":combat", out e.combatDur);
                    if (e.preMoveClipColl != null) e.preMoveAnimId = ResolveCollAnimId(animMgr, e.preMoveClipColl, e.resourceName + ":premove", out e.preMoveDur);
                    if (e.idleClipColl != null) e.idleAnimId = ResolveCollAnimId(animMgr, e.idleClipColl, e.resourceName + ":idle", out e.idleDur);
                    if (e.idleAltClipColl != null) e.idleAltAnimId = ResolveCollAnimId(animMgr, e.idleAltClipColl, e.resourceName + ":idlealt", out e.idleAltDur);
                    if (e.idleAlt2ClipColl != null) e.idleAlt2AnimId = ResolveCollAnimId(animMgr, e.idleAlt2ClipColl, e.resourceName + ":idlealt2", out e.idleAlt2Dur);
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
                // A CUSTOM MODEL entry (non-zero skeleton guid) must NOT take this path: it carries its OWN baked atlas,
                // and a textureFile/adjust on it is a recolour of THAT atlas (applied in ApplyTexture), not a donor-layer
                // repaint — diverting here would paint the vanilla donor layer and return before the mesh is even repointed
                // (symptom: the custom mech stayed its baked colour). Only true texture-only entries (no skeleton) divert.
                bool isModelEntry = !(e.sa == 0 && e.sb == 0 && e.sc == 0 && e.sd == 0);
                if (!isModelEntry && (NeedsAdjust(e) || !string.IsNullOrEmpty(e.textureFile))) { ApplyTextureOnly(addon, animMgr, e, name); return; }

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
                InjectHandProp(addon, animMgr, e.skeleton, e);
                ApplyTexture(e, animMgr);
                if (!e.repointed) { e.repointed = true; Plugin.Log.LogInfo($"[Uni] repointed '{name}' -> {e.resourceName} (mesh '{bodyName}', layer '{e.layerHint}')"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] repoint: " + ex); }
        }

        internal static void TickTexture()
        {
            if (entries == null) return;
            foreach (var e in entries)
            {
                TickOne(e);
                // prop-skin recovery: same reason as TickOne's re-set path — the game can recreate/reset the weapon
                // material, reverting _MainTex to the EQ proxy. Steady state is a few ReferenceEquals checks.
                if (e.handPropLayer != null && e.propAtlasTex != null)
                    PaintLayer(e.handPropLayer, e.propAtlasTex, e.resourceName);
            }
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

        // HAND PROP (weapon axis, 2026-07-19): append ONE rigid fragment — a Prop-Lab weapon glued to a bone of OUR
        // skeleton — to the addon's FragmentEntries. The soldier's donor (an APC) has no weapon slots, and the vanilla
        // path needs a matching slot in the pawn DESCRIPTION (GetSlotIndex -1 silently drops the attachment), so the
        // plugin constructs the FragmentEntry directly: our MeshCollection + mesh, a borrowed weapon output layer, and
        // boneName resolved by SUBSTRING against our renamed (b###_<orig>) bones. FragmentEntry.Load() then computes
        // BoneIndex = skeleton.GetBoneIndex(boneName) against OUR skeleton and GPU-encodes the mesh — the same call
        // the vanilla loader makes. Runs right after ReloadFragments in the AddOn.Load window; re-entrant (skips if
        // our mesh name is already in the array). Parse failures / missing assets log a warning and change nothing.
        static void InjectHandProp(object addon, object animMgr, object skel, ModelEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.handPropGuid)) return;
            try
            {
                int[] Csv(string s)
                {
                    var parts = (s ?? "").Split(','); if (parts.Length != 4) return null;
                    var r = new int[4];
                    for (int i = 0; i < 4; i++) if (!int.TryParse(parts[i].Trim(), out r[i])) return null;
                    return r;
                }
                var cg = Csv(e.handPropGuid);
                if (cg == null) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: bad collection guid '{e.handPropGuid}' (want \"a,b,c,d\")"); return; }
                string propName = string.IsNullOrEmpty(e.handPropName) ? e.resourceName + "Prop" : e.handPropName;
                string meshName = propName + "_DistrictMesh";   // the Prop Lab's fixed mesh name inside the collection
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) return;
                var fragType = frags.GetType().GetElementType();
                var mnField = AccessTools.Field(fragType, "meshName");
                for (int i = 0; i < frags.Length; i++)
                    if (mnField?.GetValue(frags.GetValue(i)) as string == meshName) return;   // already injected on this addon
                // 1) the MeshCollection: already-registered lookup -> Amplitude catalog -> mounted-bundle name fallback
                //    (the catalog misses mod-bundle MeshCollections by GUID — Pawn-Props trap 3); register after a raw load
                //    (RegisterMeshCollection dedupes internally and LoadIFNs the meshes into the GPU content manager).
                var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
                var guid = MakeGuid(cg[0], cg[1], cg[2], cg[3]);
                if (mcType == null || guid == null) return;
                object mc = null;
                var getMc = animMgr.GetType().GetMethods(BF).FirstOrDefault(m2 => m2.Name == "GetMeshCollection" && m2.GetParameters().Length == 1 && m2.GetParameters()[0].ParameterType == guid.GetType());
                try { mc = getMc?.Invoke(animMgr, new[] { guid }); } catch { }
                bool Dead(object o) => o == null || (o is UnityEngine.Object uo && !uo);
                bool preRegistered = !Dead(mc);   // already encoded (e.g. via [Props] PropCollectionGuids) — angles stamped now would be TOO LATE (guid-cached)
                if (Dead(mc))
                {
                    var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                    var loadA = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m2 => (m2.Name == "TryLoadAsset" || m2.Name == "LoadAsset") && m2.IsGenericMethodDefinition && m2.GetParameters().Length == 1)?.MakeGenericMethod(mcType);
                    try { mc = loadA?.Invoke(null, new[] { guid }); } catch { }
                    if (Dead(mc))
                        foreach (var b in UnityEngine.AssetBundle.GetAllLoadedAssetBundles())
                        { var a = b.LoadAsset(propName + "_Collection"); if (a != null && mcType.IsInstanceOfType(a)) { mc = a; break; } }
                    if (Dead(mc)) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: collection not loadable (guid {e.handPropGuid}, name '{propName}_Collection') — no weapon this session"); return; }
                }
                // 1b) draw-time IMPORT ANGLES — ALWAYS stamped BEFORE RegisterMeshCollection: the encoder DISCARDS
                //     the authored FxMeshContent, rebuilds it from the FxMesh ASSET (rotating vertices by the asset's
                //     ImportAngles), and caches per guid (decompiled: FxMeshLayer.GetFxMeshStructIndex). The baked
                //     angle value does NOT survive the mod bundle — in-game the asset reports the CLASS DEFAULT
                //     (-90,0,0), which silently tipped every prop over vs the preview. So: stamp the registry
                //     override when set, else stamp ZERO — in-game then always matches the baked vertices
                //     (orientation authored via the Prop Lab's Rotation offset).
                {
                    var av = (string.IsNullOrEmpty(e.handPropAngles) ? "0,0,0" : e.handPropAngles).Split(',');
                    if (av.Length == 3
                        && float.TryParse(av[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ax)
                        && float.TryParse(av[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ay)
                        && float.TryParse(av[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float az))
                    {
                        // Stamp BOTH angle carriers before the encoding in Load below: the collection's
                        // FxMeshContent.ImportAngles (harmless; proven ignored by the fragment path in the field)
                        // AND the FxMesh ASSET's own importAngles — the field the district axis proved effective,
                        // read when the mesh geometry is uploaded.
                        bool stamped = false, fxStamped = false;
                        if (GetMember(mc, "skinnedMeshInfos") is Array sis)
                            foreach (var si in sis)
                            {
                                if ((GetMember(si, "MeshName")?.ToString() ?? "") != meshName) continue;
                                var fmc = GetMember(si, "FxMeshContent");
                                if (fmc != null)
                                {
                                    var iaF = AccessTools.Field(fmc.GetType(), "ImportAngles");
                                    if (iaF != null && iaF.FieldType == typeof(UnityEngine.Vector3))
                                    { iaF.SetValue(fmc, new UnityEngine.Vector3(ax, ay, az)); stamped = true; }
                                    // resolve + stamp the FxMesh asset the content entry points at
                                    var fxGuid = GetMember(fmc, "Guid");
                                    var fxType = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxMesh");
                                    if (fxGuid != null && fxType != null)
                                    {
                                        var adb2 = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
                                        var loadFx = adb2?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                            .FirstOrDefault(m2 => (m2.Name == "TryLoadAsset" || m2.Name == "LoadAsset") && m2.IsGenericMethodDefinition && m2.GetParameters().Length == 1)?.MakeGenericMethod(fxType);
                                        object fx = null;
                                        try { fx = loadFx?.Invoke(null, new[] { fxGuid }); } catch { }
                                        if (fx != null && !(fx is UnityEngine.Object ufo && !ufo))
                                        {
                                            var iaF2 = AccessTools.Field(fxType, "importAngles");
                                            if (iaF2 != null && iaF2.FieldType == typeof(UnityEngine.Vector3))
                                            { iaF2.SetValue(fx, new UnityEngine.Vector3(ax, ay, az)); fxStamped = true; }
                                        }
                                    }
                                }
                                break;
                            }
                        Plugin.Log.LogInfo($"[Props] '{e.resourceName}' hand prop import angles ({ax},{ay},{az}) content={(stamped ? "stamped" : "MISS")} fxMeshAsset={(fxStamped ? "stamped" : "MISS")}{(preRegistered ? " — WARNING: collection was already registered/encoded (PropCollectionGuids?), angles may not take this session" : "")}");
                    }
                    else Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: bad angles '{e.handPropAngles}' (want \"x,y,z\")");
                }
                // register AFTER the stamp (dedupes internally; LoadIFNs the meshes = the encode that reads the angles)
                if (!preRegistered)
                    try { animMgr.GetType().GetMethod("RegisterMeshCollection", BindingFlags.Public | BindingFlags.Instance)?.Invoke(animMgr, new[] { mc }); } catch { }
                // 2) the output layer from the borrowed material ("" = the shared EQ_DLC04_Weapons, sling-verified)
                var mg = Csv(string.IsNullOrEmpty(e.handPropMat) ? "1356489961,1316891353,-864888678,1241300466" : e.handPropMat);
                if (mg == null) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: bad material guid '{e.handPropMat}'"); return; }
                var content = GetMember(animMgr, "Content");
                object fol = null;
                var folM = content?.GetType().GetMethods(BF).FirstOrDefault(m2 => m2.Name == "OutputLayerFromMaterialGuid" && m2.GetParameters().Length == 1);
                try { fol = folM?.Invoke(content, new[] { MakeGuid(mg[0], mg[1], mg[2], mg[3]) }); } catch { }
                if (Dead(fol)) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: no output layer for the borrowed material — no weapon"); return; }
                // 2b) PROP SKIN: give the fragment a PRIVATE CLONE of the borrowed layer, painted with the prop's OWN
                //     baked atlas (<prop>_Atlas, by name from the mounted mod bundles — the mesh's UVs were remapped to
                //     it at bake). Without this the mesh samples the EQ weapon atlas (the M60 rendered leather-tan).
                //     Clone-first mirrors the unit-retexture isolation: the shared EQ layer (real DLC weapons) is never
                //     touched; FragmentEntry.Load below registers the clone via GetLayerIndexAddItIFN = a fresh GPU slot.
                UnityEngine.Texture2D propAtlas = null;
                foreach (var b in UnityEngine.AssetBundle.GetAllLoadedAssetBundles())
                { var a2 = b.LoadAsset(propName + "_Atlas"); if (a2 is UnityEngine.Texture2D t2) { propAtlas = t2; break; } }
                if (propAtlas != null && fol is UnityEngine.Object folObj)
                {
                    var clone = UnityEngine.Object.Instantiate(folObj);
                    clone.name = propName + "_PropLayer";
                    e.handPropLayer = clone;
                    fol = clone;
                }
                else if (propAtlas == null)
                    Plugin.Log.LogInfo($"[Props] '{e.resourceName}' hand prop: no '{propName}_Atlas' in the mounted bundles — keeping the borrowed layer's skin");
                // 3) OUR bone, by substring (case-insensitive; first match wins)
                string boneSub = string.IsNullOrEmpty(e.handPropBone) ? "R_Hand" : e.handPropBone;
                string boneName = null;
                if (GetMember(skel, "BoneInfos") is Array bones)
                    foreach (var b in bones)
                    {
                        var n = GetMember(b, "Name")?.ToString() ?? "";
                        if (n.IndexOf(boneSub, StringComparison.OrdinalIgnoreCase) >= 0) { boneName = n; break; }
                    }
                if (boneName == null) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: no bone matches '{boneSub}' on our skeleton — no weapon"); return; }
                // 4) construct, Load (encodes the mesh + resolves BoneIndex), append
                var ctor5 = fragType.GetConstructors(BF).FirstOrDefault(c => c.GetParameters().Length == 5);
                if (ctor5 == null) { Plugin.Log.LogWarning("[Props] FragmentEntry ctor not found (game update?)"); return; }
                var item = ctor5.Invoke(new object[] { 0, mc, meshName, fol, boneName });
                var renderer = GetMember(animMgr, "FxComponentRenderer");
                var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
                var layerObj = GetMember(animMgr, "FXMeshLayerIndex");
                int layer = layerObj is int li2 ? li2 : Convert.ToInt32(layerObj ?? 0);
                try { AccessTools.Method(fragType, "Load")?.Invoke(item, new object[] { skel, renderer, mcm, layer }); }
                catch (Exception ex) { Plugin.Log.LogWarning("[Props] hand prop Load: " + (ex.InnerException ?? ex).Message); return; }
                uint enc = 0, bidx = 0;
                try { enc = (uint)AccessTools.Field(fragType, "EncodedMeshAndVisualParticleCount").GetValue(item); bidx = (uint)AccessTools.Field(fragType, "BoneIndex").GetValue(item); } catch { }
                if (enc == 0) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: mesh '{meshName}' encoded to 0 (name not in the collection?) — not appended"); return; }
                // paint OUR atlas onto the private layer clone (post-Load: the clone now owns a registered GPU slot);
                // cached on the entry — TickTexture REPAINTS every frame (cheap ReferenceEquals skip when stable),
                // because the game can reset/recreate the material and a one-shot paint flip-flopped across sessions.
                if (propAtlas != null && e.handPropLayer != null)
                {
                    e.propAtlasTex = propAtlas;
                    PaintLayer(e.handPropLayer, propAtlas, propName);
                }
                var narr = Array.CreateInstance(fragType, frags.Length + 1);
                Array.Copy(frags, narr, frags.Length);
                narr.SetValue(item, frags.Length);
                SetMember(addon, "FragmentEntries", narr);
                // CRITICAL: the GPU pawn DESCRIPTOR (per definition: StartFragment + FragmentCount into the fragment
                // buffer) is snapshotted from FragmentEntries at RegisterPawnDefinition time — an append after that
                // snapshot exists in the array but the renderer still draws the OLD fragment count (the M60 was
                // "glued" yet invisible). Do NOT call UpdateDescriptorBufferContent: that full re-pack SKIPS any
                // definition whose LoadingStatus isn't Loaded yet (common mid-session-load) WITHOUT advancing the
                // descriptor slot, shifting every later pawn type onto the wrong fragments — in the field this drew
                // the recon drones as scattered parts. Instead patch ONLY OUR definition, alignment-safe: copy its
                // existing fragment slots to the tail of the persistent fragment array, append ours there, and
                // repoint just descriptor[defId] at the new contiguous block (its old slots go dead, harmless).
                try
                {
                    var pmType = AccessTools.TypeByName("Amplitude.Mercury.Animation.PawnManager");
                    var pm = pmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                             ?? AccessTools.Field(pmType, "Instance")?.GetValue(null);
                    int defId = -1;
                    try { defId = Convert.ToInt32(GetMember(addon, "PawnDefinitionId")); } catch { }
                    if (pm == null || defId < 0)
                    {
                        // Not registered yet: RegisterPawnDefinition's own Add() snapshots the appended array — nothing to patch.
                        Plugin.Log.LogInfo($"[Props] '{e.resourceName}' hand prop appended pre-registration — the registration snapshot carries it");
                    }
                    else
                    {
                        var descF = AccessTools.Field(pmType, "gpuPawnDescriptorEntries");
                        var fragF = AccessTools.Field(pmType, "gpuPawnDescriptorFragmentEntries");
                        var cntF = AccessTools.Field(pmType, "persistentFragmentEntryCount");
                        var dirtyF = AccessTools.Field(pmType, "descriptorBufferDirty");
                        var descs = descF?.GetValue(pm) as Array;
                        var gfrags = fragF?.GetValue(pm) as Array;
                        if (descs == null || gfrags == null || cntF == null || defId >= descs.Length)
                        { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: descriptor arrays unreadable (defId {defId}) — prop stays invisible"); }
                        else
                        {
                            var dEntry = descs.GetValue(defId);
                            var dT = dEntry.GetType();
                            uint start = (uint)dT.GetField("StartFragment").GetValue(dEntry);
                            uint count = (uint)dT.GetField("FragmentCount").GetValue(dEntry);
                            int tail = Convert.ToInt32(cntF.GetValue(pm));
                            int need = tail + (int)count + 1;
                            if (gfrags.Length < need)
                            {
                                var grown = Array.CreateInstance(gfrags.GetType().GetElementType(), need + 100);
                                Array.Copy(gfrags, grown, gfrags.Length);
                                fragF.SetValue(pm, grown); gfrags = grown;
                            }
                            for (int k = 0; k < count; k++) gfrags.SetValue(gfrags.GetValue((int)start + k), tail + k);
                            var feType = gfrags.GetType().GetElementType();
                            var ge = Activator.CreateInstance(feType);
                            feType.GetField("SkinnedMeshIndex").SetValue(ge, 0u);
                            feType.GetField("EncodedMeshAndVisualParticleCountFxMeshIndex").SetValue(ge, enc);
                            feType.GetField("BoneIndex").SetValue(ge, bidx);
                            uint folIdx = 0;
                            try { folIdx = (uint)Convert.ToInt32(GetMember(fol, "LayerIndex")); } catch { }
                            feType.GetField("FxOutputLayerIndex").SetValue(ge, folIdx);
                            gfrags.SetValue(ge, tail + (int)count);
                            dT.GetField("StartFragment").SetValue(dEntry, (uint)tail);
                            dT.GetField("FragmentCount").SetValue(dEntry, count + 1);
                            descs.SetValue(dEntry, defId);
                            cntF.SetValue(pm, tail + (int)count + 1);
                            dirtyF?.SetValue(pm, true);
                            Plugin.Log.LogInfo($"[Props] descriptor[{defId}] repointed: fragments {start}+{count} -> {tail}+{count + 1} (surgical, layer {folIdx})");
                        }
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[Props] descriptor patch: " + ex.Message); }
                Plugin.Log.LogInfo($"[Props] '{e.resourceName}' hand prop '{meshName}' glued to bone '{boneName}' (boneIndex {bidx}, encoded {enc})");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Props] InjectHandProp: " + ex); }
        }

        static void ApplyTexture(ModelEntry e, object mgr)
        {
            try
            {
                if (e.tex == null)
                {
                    // A custom model's skin is its baked atlas — UNLESS a textureFile recolour is set on the model entry,
                    // then hot-load THAT PNG instead (recolour a custom model without a re-bake; same enc_skins PNG the
                    // Unit Retexture window writes). Desaturate/RGB then adjust the loaded PNG. Falls back to the baked
                    // atlas if the PNG is missing. (Adjust-only with no PNG stays on the baked atlas — that path isn't
                    // CPU-readable for AdjustSkin, so a model recolour goes through a PNG.)
                    if (!string.IsNullOrEmpty(e.textureFile))
                    {
                        e.tex = LoadSkinPng(e.textureFile, e.resourceName, e.assetDir);
                        if (e.tex != null && NeedsAdjust(e))
                        {
                            AdjustSkin(e.tex, e.brightness, e.desaturate, e.tintR, e.tintG, e.tintB);
                            Plugin.Log.LogInfo($"[Skin] {e.resourceName}: adjustments applied to retexture (gamma {e.brightness:0.00}, desat {UnityEngine.Mathf.Clamp01(e.desaturate):0.00}, rgb {e.tintR:+0;-0;0}/{e.tintG:+0;-0;0}/{e.tintB:+0;-0;0})");
                        }
                    }
                    if (e.tex == null) e.tex = LoadAtlas(e.ta, e.tb, e.tc, e.td, e.resourceName);
                }
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
                    e.tex = LoadSkinPng(e.textureFile, e.resourceName, e.assetDir);
                    if (e.tex != null && NeedsAdjust(e)) AdjustSkin(e.tex, e.brightness, e.desaturate, e.tintR, e.tintG, e.tintB);   // brighten/desaturate/tint the loaded PNG too
                }
                GreyIsolate(addon, animMgr, e);    // clone the body fragment's output layer (+ build the desaturated atlas if desaturate>0)
                ApplyTexture(e, animMgr);          // paint e.tex on the isolated clone + neutralise the civ-colour/overlay maps (TickOne)
                if (!e.repointed) { e.repointed = true; Plugin.Log.LogInfo($"[Skin] '{name}' -> {e.resourceName}: {(string.IsNullOrEmpty(e.textureFile) ? $"greyed (desaturate {e.desaturate:0.00})" : $"retextured ('{e.textureFile}')")}, layer '{e.layerHint}', atlas={(e.tex != null ? e.tex.width + "x" + e.tex.height : "NONE — will retry")}"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Skin] " + ex); }
        }

        // Load a retexture PNG: the owning pack's <assetDir>/skins/<file> first (per-pack assets, 2026-07-19), then the
        // legacy shared BepInEx/config/enc_skins/<file>. (Needs ImageConversionModule.LoadImage.)
        static UnityEngine.Texture2D LoadSkinPng(string file, string tag, string assetDir = "")
        {
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "enc_skins", file);
                if (!string.IsNullOrEmpty(assetDir))
                {
                    var pp = Path.Combine(assetDir, "skins", file);
                    if (File.Exists(pp)) path = pp;
                }
                if (!File.Exists(path)) { Plugin.Log.LogWarning($"[Skin] {tag}: retexture file not found: {path}" + (string.IsNullOrEmpty(assetDir) ? "" : $" (also tried {Path.Combine(assetDir, "skins")})")); return null; }
                // mipChain TRUE: the baked atlas ships with mips, and a hot-loaded PNG without them undersamples at map
                // zoom (each screen pixel picks 1 raw texel instead of an average) — a dark high-contrast skin renders
                // as noisy near-black. LoadImage fills the chain; AdjustSkin's Apply() rebuilds it after the pixel pass.
                var t = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, true) { name = tag + "_Skin" };
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
                    if (e.tex == null && host != null && NeedsAdjust(e)) e.tex = BuildAdjustedAtlas(host, e.brightness, e.desaturate, e.tintR, e.tintG, e.tintB, e.resourceName);   // adjust the ORIGINAL skin, once (skipped when a custom PNG already set e.tex)
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
        static UnityEngine.Texture2D BuildAdjustedAtlas(object hostLayer, float brightness, float desat, float tR, float tG, float tB, string tag)
        {
            try
            {
                UnityEngine.Texture src = null;
                if (GetMember(hostLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                    {
                        foreach (var fld in RenderMatFields)
                            if (GetMember(ro, fld) is UnityEngine.Material mat && mat.GetTexture("_MainTex") is UnityEngine.Texture mt) { src = mt; break; }
                        if (src != null) break;
                    }
                // log the wait ONCE — TickOne retries this every frame until the layer's atlas loads, and a slow load
                // used to flood the BepInEx log with one warning per frame (perf pass 2026-07-19)
                if (src == null) { if (!greyWaitLogged) { greyWaitLogged = true; Plugin.Log.LogWarning($"[Grey] {tag}: no _MainTex on the output layer yet (will keep retrying silently)"); } return null; }
                int w = src.width, h = src.height;
                var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                var prev = UnityEngine.RenderTexture.active;
                UnityEngine.Graphics.Blit(src, rt);
                UnityEngine.RenderTexture.active = rt;
                var t = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false) { name = tag + "_Grey" };
                t.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0); t.Apply();
                UnityEngine.RenderTexture.active = prev; UnityEngine.RenderTexture.ReleaseTemporary(rt);
                AdjustSkin(t, brightness, desat, tR, tG, tB);
                Plugin.Log.LogInfo($"[Grey] {tag}: adjusted atlas {w}x{h} (gamma {brightness:0.00}, desat {UnityEngine.Mathf.Clamp01(desat):0.00}, rgb {tR:+0;-0;0}/{tG:+0;-0;0}/{tB:+0;-0;0})");
                return t;
            }
            catch (Exception e) { Plugin.Log.LogError("[Grey] build atlas: " + e); return null; }
        }

        // Apply the universal skin adjustments in place, in this order: (1) BRIGHTNESS — a gamma lift (1 = unchanged;
        // LUT, endpoint-preserving, multiplicative in the dark range — the only adjust that meaningfully lightens a
        // near-black atlas), (2) pull each pixel toward its luminance by `desat` (1 = full grey), (3) add the
        // per-channel colour offset tR/tG/tB (-255..+255). Shared by the own-atlas path and the PNG path. The editor's
        // Retexture-window preview mirrors this math exactly — keep them in lockstep.
        static void AdjustSkin(UnityEngine.Texture2D t, float brightness, float desat, float tR, float tG, float tB)
        {
            var px = t.GetPixels32();
            float s = UnityEngine.Mathf.Clamp01(desat);
            byte[] lut = null;
            if (UnityEngine.Mathf.Abs(brightness - 1f) > 0.001f)
            {
                float inv = 1f / UnityEngine.Mathf.Clamp(brightness, 0.2f, 4f);
                lut = new byte[256];
                for (int v = 0; v < 256; v++) lut[v] = (byte)UnityEngine.Mathf.Clamp(UnityEngine.Mathf.RoundToInt(255f * UnityEngine.Mathf.Pow(v / 255f, inv)), 0, 255);
            }
            for (int i = 0; i < px.Length; i++)
            {
                if (lut != null) { px[i].r = lut[px[i].r]; px[i].g = lut[px[i].g]; px[i].b = lut[px[i].b]; }
                float lum = px[i].r * 0.299f + px[i].g * 0.587f + px[i].b * 0.114f;
                px[i].r = (byte)UnityEngine.Mathf.Clamp((px[i].r + (lum - px[i].r) * s) + tR, 0, 255);
                px[i].g = (byte)UnityEngine.Mathf.Clamp((px[i].g + (lum - px[i].g) * s) + tG, 0, 255);
                px[i].b = (byte)UnityEngine.Mathf.Clamp((px[i].b + (lum - px[i].b) * s) + tB, 0, 255);
            }
            t.SetPixels32(px); t.Apply();
        }

        // True if this entry carries any texture adjustment (brightness gamma, desaturate or a non-zero colour offset).
        static bool NeedsAdjust(ModelEntry e) => e.desaturate > 0f || e.tintR != 0f || e.tintG != 0f || e.tintB != 0f || UnityEngine.Mathf.Abs(e.brightness - 1f) > 0.001f;

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
                object InjectOne(object coll, int a, int b, int c2, int d2, string tag)
                {
                    if (a == 0 && b == 0 && c2 == 0 && d2 == 0) return coll;
                    if (coll == null) coll = LoadClipCollection(a, b, c2, d2, tag);
                    if (coll == null) return null;
                    try { AccessTools.Method(coll.GetType(), "Load", Type.EmptyTypes)?.Invoke(coll, null); } catch { }
                    var arr = field.GetValue(animMgr) as Array;
                    bool present = false;
                    if (arr != null) foreach (var c in arr) if (ReferenceEquals(c, coll)) { present = true; break; }
                    if (present) return coll;
                    int len = arr?.Length ?? 0;
                    var narr = Array.CreateInstance(ccType, len + 1);
                    if (arr != null) Array.Copy(arr, narr, len);
                    narr.SetValue(coll, len);
                    field.SetValue(animMgr, narr);
                    Plugin.Log.LogInfo($"[Uni] injected clipCollection '{tag}' at [{len}]");
                    return coll;
                }
                foreach (var e in entries)
                {
                    e.clipColl = InjectOne(e.clipColl, e.ca, e.cb, e.cc, e.cd, e.resourceName);
                    if (e.animStateDriven)
                    {
                        e.moveClipColl = InjectOne(e.moveClipColl, e.mca, e.mcb, e.mcc, e.mcd, e.resourceName + ":move");
                        e.afterClipColl = InjectOne(e.afterClipColl, e.aca, e.acb, e.acc, e.acd, e.resourceName + ":after");
                        e.attackClipColl = InjectOne(e.attackClipColl, e.ata, e.atb, e.atc, e.atd, e.resourceName + ":attack");
                        e.combatClipColl = InjectOne(e.combatClipColl, e.cba, e.cbb, e.cbc, e.cbd, e.resourceName + ":combat");
                        e.preMoveClipColl = InjectOne(e.preMoveClipColl, e.pva, e.pvb, e.pvc, e.pvd, e.resourceName + ":premove");
                        e.idleClipColl = InjectOne(e.idleClipColl, e.iea, e.ieb, e.iec, e.ied, e.resourceName + ":idle");
                        e.idleAltClipColl = InjectOne(e.idleAltClipColl, e.ala, e.alb, e.alc, e.ald, e.resourceName + ":idlealt");
                        e.idleAlt2ClipColl = InjectOne(e.idleAlt2ClipColl, e.a2a, e.a2b, e.a2c, e.a2d, e.resourceName + ":idlealt2");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] InjectClipCollections: " + ex); }
        }

        // After Apply built the animation buffer, resolve our clip's animation id via GetAnimationId(clip guid).
        static int ResolveAnimId(object animMgr, ModelEntry e)
        {
            int id = ResolveCollAnimId(animMgr, e.clipColl, e.resourceName, out float d);
            if (id >= 0 && d > 0.001f) e.animDuration = d;
            return id;
        }

        // Same resolution for ANY of an entry's ClipCollections (idle / move / after — Phase 2): index-0 clip guid ->
        // animation id + real duration (the pose hook normalizes Time by it so the clip plays at real speed).
        static int ResolveCollAnimId(object animMgr, object coll, string tag, out float dur)
        {
            dur = 1f;
            try
            {
                var clips = AccessTools.Field(coll.GetType(), "animationClipEntries")?.GetValue(coll) as Array;
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
                        if (d > 0.001f) { dur = d; Plugin.Log.LogInfo($"[Uni] clip '{tag}' animId {id} duration {d:0.###}s"); }
                    }
                }
                return id;
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] ResolveAnimId '{tag}': " + ex.Message); return -1; }
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

        // Precomputed member names (perf pass 2026-07-19): "Pose" + i / "BoneRotation" + i built fresh STRINGS per
        // pawn per FRAME on the game's hottest loop — thousands of small allocations a second at scale, pure GC churn.
        static readonly string[] PoseNames = { "Pose0", "Pose1", "Pose2", "Pose3", "Pose4", "Pose5", "Pose6", "Pose7", "Pose8" };
        static readonly string[] BoneRotationNames = { "BoneRotation0", "BoneRotation1", "BoneRotation2", "BoneRotation3" };

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
                else if (ctx.descId >= 0)
                {
                    // plain loop, NOT FirstOrDefault: the lambda captured ctx into a fresh closure allocation for
                    // EVERY pawn the game adds — including every vanilla pawn (perf pass 2026-07-19)
                    var list = entries;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        { var x = list[i]; if (Hooked(x) && x.descId >= 0 && x.descId == ctx.descId) { e = x; break; } }
                }
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
                var pose = GetMember(ctx.entry, PoseNames[i]);
                if (pose == null) continue;
                SetMember(pose, "Time", 0f);
                SetMember(ctx.entry, PoseNames[i], pose);
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
            // PawnEntryPose.Time is NORMALIZED (sampler does Mathf.Repeat(Time,1) = one loop). ComputePoseTime divides by the
            // clip duration so it plays at REAL speed and hits every frame; raw Time.time = duration× too fast + frame-skipping.
            // STATE-DRIVEN models switch Pose0's ANIMATIONID by movement state. This is safe: PawnManager's
            // DoComputation() does a FULL pawnEntriesBuffer.SetData(pawnEntries) EVERY FRAME (decompiled), so every
            // field we write — ids included — reaches the GPU each frame; there is no id latching. (The earlier
            // "id switching is ignored" reading was an illusion: the move clip's data was CONSTANT at a near-idle
            // stance at the time. A Pose1 weight-switching attempt rendered the pawn INVISIBLE while moving — the
            // secondary slots misbehave on the GPU pass in some unmapped way — so the state machine deliberately
            // uses only the battle-tested Pose0.)
            if (e.animStateDriven && e.moveAnimId >= 0)
            {
                StatePose(e, entry, out bool moving, out bool inAfter, out float afterT, out bool inAttack, out float attackT, out bool inCombat, out bool inPreMove, out float preMoveT, out bool inIdleAlt, out float idleAltT, out int idleAltId);
                if (inAttack)
                {
                    // ATTACK wins over every other state: the pawn just fired (ranged-fight hook armed a window at
                    // its position) — one clamped 0->1 pass of the attack clip, holding the last frame until it elapses.
                    SetMember(pose0, "AnimationId", (uint)e.attackAnimId);
                    SetMember(pose0, "Time", attackT);
                }
                else if (inPreMove)
                {
                    // PRE-MOVEMENT: the unit just STARTED moving — one clamped pass (e.g. the howitzer folding),
                    // then the Move loop takes over when the window elapses.
                    SetMember(pose0, "AnimationId", (uint)e.preMoveAnimId);
                    SetMember(pose0, "Time", preMoveT);
                }
                else if (moving)
                {
                    float md = e.moveDur > 0.001f ? e.moveDur : 1f;
                    SetMember(pose0, "AnimationId", (uint)e.moveAnimId);
                    SetMember(pose0, "Time", UnityEngine.Time.time / md);
                }
                else if (inAfter)
                {
                    SetMember(pose0, "AnimationId", (uint)e.afterAnimId);
                    SetMember(pose0, "Time", afterT);
                }
                else if (inCombat && e.combatAnimId >= 0)
                {
                    // COMBAT-IDLE: the army is locked in a battle — hold the combat stance instead of the relaxed idle.
                    // (A single-frame stance clip renders fine: FrameCount 1 pins the sampler to frame 0 at any Time.)
                    float cd = e.combatDur > 0.001f ? e.combatDur : 1f;
                    SetMember(pose0, "AnimationId", (uint)e.combatAnimId);
                    SetMember(pose0, "Time", UnityEngine.Time.time / cd);
                }
                else if (inIdleAlt && idleAltId >= 0)
                {
                    // IDLE-ALT: the occasional flavor one-shot (howl/eat) — one clamped 0->1 pass at the performer
                    // pawn, then back to the normal idle. Never fires outside plain idle (StatePose gates it).
                    SetMember(pose0, "AnimationId", (uint)idleAltId);
                    SetMember(pose0, "Time", idleAltT);
                }
                else if (e.idleAnimId >= 0)
                {
                    // IDLE OVERRIDE role (bake-only stance fix, 2026-07-19): a stance baked as the PRIMARY clip
                    // encodes ~identity against the skeleton's reference pose (the primary defines that reference)
                    // and renders as REST in-game — the "forgot to deploy" trap. The stance therefore bakes as a
                    // ROLE (real deltas against the full primary clip's reference) and idle plays it here; the
                    // primary (e.animId) stays the full reference clip and is what plays when no override is set.
                    float idleDur = e.idleDur > 0.001f ? e.idleDur : 1f;
                    SetMember(pose0, "AnimationId", (uint)e.idleAnimId);
                    SetMember(pose0, "Time", UnityEngine.Time.time / idleDur);
                }
                else
                {
                    float idleDur = e.animDuration > 0.001f ? e.animDuration : 1f;
                    SetMember(pose0, "AnimationId", (uint)e.animId);
                    SetMember(pose0, "Time", UnityEngine.Time.time / idleDur);
                }
                SetMember(pose0, "Weight", 1f);
                SetMember(entry, "Pose0", pose0);
            }
            else
            {
                float dur = e.animDuration > 0.001f ? e.animDuration : 1f;
                SetMember(pose0, "AnimationId", (uint)e.animId);
                SetMember(pose0, "Weight", 1f);
                SetMember(pose0, "Time", ComputePoseTime(e, entry, dur));
                SetMember(entry, "Pose0", pose0);
            }
            for (int i = 1; i < 9; i++)
            {
                var pose = GetMember(entry, PoseNames[i]);
                if (pose == null) continue;
                SetMember(pose, "Weight", 0f);
                SetMember(entry, PoseNames[i], pose);
            }
            // The AIM layer is cleared only for the ARTILLERY behaviors (it twists the howitzer's barrel as the game
            // aims). For other animated models the game's bone-rotation layer stays — it carries the pawn's FACING
            // (clearing it froze the soldier to one compass direction) — but on some donors it arrives with an
            // INVALID bone index (0xFFFFFFFF) and RUNAWAY angles (1558°…): those magnitudes deform the rig (the
            // soldier's ripped-off head). SanitizeAimLayer wraps such angles into 0..360 — same orientation, sane
            // magnitude — instead of zeroing (which would kill facing).
            if (((e.fireOnAttack || e.deployOnStop) && !e.animStateDriven) || e.clearAimLayer) ClearAimLayer(entry);   // legacy artillery rule, OR the explicit per-model knob (a STATE-DRIVEN howitzer still needs the donor's aim/wheel junk cleared; characters keep the layer for facing)
            else if (!string.IsNullOrEmpty(e.turretBone)) TurretizeAimLayer(entry, e);   // retarget the game's aim/heading angle onto OUR turret bone (a vehicle turret tracks the target)
            else SanitizeAimLayer(entry);
            ApplyPositionOffset(e, entry);
            ApplyScale(e, entry);
            // PROVEN 2026-07-19 (temp probe, removed): PawnDescriptorId is a per-pawn WRITABLE mesh selector the
            // GPU honors — AssaultInfantry pawns rendered another entry's mesh from a one-int write. This is the
            // foundation for the vanilla-in-combat feature: keep the donor's descriptor alive alongside ours and
            // flip each pawn by combat state.
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

        // STATE-DRIVEN (Phase 2): resolve this pawn's movement state from the samples ProcessAnimStates published,
        // matched by nearest sample position (the deploy poll's proven approximation). The caller drives the pose
        // WEIGHTS from it — moving -> the MOVE slot; recently stopped + an AFTER clip -> one 0->1 AFTER pass
        // (clamped below 1.0: Repeat(1.0)=frame 0 would snap back); otherwise IDLE. An unmatched pawn idles.
        // ATTACK (highest priority, independent of the movement samples): the ranged-fight hook (Hk_PawnRangedFight)
        // arms a FireInstance at the shooter's render position; the pawn nearest an unexpired one plays the attack
        // clip once — same position-match approximation the fire/deploy behaviors use.
        static void StatePose(ModelEntry e, object entry, out bool moving, out bool inAfter, out float afterT, out bool inAttack, out float attackT, out bool inCombat, out bool inPreMove, out float preMoveT, out bool inIdleAlt, out float idleAltT, out int idleAltId)
        {
            moving = false; inAfter = false; afterT = 0f; inAttack = false; attackT = 0f; inCombat = false; inPreMove = false; preMoveT = 0f; inIdleAlt = false; idleAltT = 0f; idleAltId = -1;
            var os = GetMember(entry, "ObjectSpace");
            if (os == null) return;
            UnityEngine.Vector3 pos;
            try { pos = (UnityEngine.Vector3)GetMember(os, "Translation"); } catch { return; }
            if (e.attackAnimId >= 0)
            {
                float atd = e.attackDur > 0.001f ? e.attackDur : 1f;
                // attackRepeats: the window spans N passes of the clip; Time is fed UNCLAMPED (dt/clipDur) and the
                // sampler's Repeat(Time,1) wraps it, so the clip replays each pass — sustained fire from a
                // single-pop source clip. repeats=1 degenerates to the original clamped one-shot.
                int rep = e.attackRepeats > 0 ? e.attackRepeats : 1;
                float win = atd * rep;
                float nowT = UnityEngine.Time.time;
                lock (e.activeFires)
                    for (int i = 0; i < e.activeFires.Count; i++)
                    {
                        float dtF = nowT - e.activeFires[i].startTime;
                        if (dtF < 0f || dtF >= win) continue;
                        if ((e.activeFires[i].pos - pos).sqrMagnitude < 4f * 4f)
                        { inAttack = true; attackT = UnityEngine.Mathf.Min(dtF / atd, rep - 0.001f); break; }
                    }
            }
            float stoppedAt = -1f, moveStartedAt = -1f; bool matched = false;
            float nearest = float.MaxValue; int sampleCount;   // diagnostic: nearest sample distance even when unmatched
            lock (e.stateSamples)
            {
                sampleCount = e.stateSamples.Count;
                float best = 4f * 4f;   // same 4u match radius class as the fire/deploy hooks
                for (int i = 0; i < e.stateSamples.Count; i++)
                {
                    var s = e.stateSamples[i];
                    float d = (s.pos - pos).sqrMagnitude;
                    if (d < nearest) nearest = d;
                    if (d < best) { best = d; moving = s.moving; stoppedAt = s.stoppedAt; moveStartedAt = s.moveStartedAt; inCombat = s.combat; matched = true; }
                }
            }
            if (!matched)
            {
                moving = false;
                if (e.lastStateAnim != -3)   // log the miss once per streak (this is the link that decides run vs idle)
                { e.lastStateAnim = -3; Plugin.Log.LogInfo($"[State] pose '{e.resourceName}': NO sample match (samples={sampleCount}, nearest={(nearest < float.MaxValue ? UnityEngine.Mathf.Sqrt(nearest).ToString("0.0") : "-")}u, pawn T={pos.ToString("0.0")}) — idling"); }
                return;
            }
            // BAKE-ONLY pacing (user decision 2026-07-19): the runtime plays every clip at its authored length —
            // pacing belongs in the DATA. A fold that outlasts a map move is authored shorter via a slice STEP
            // (deploy[179..0/3] = every 3rd frame = 3x faster), not scaled here.
            if (!moving && e.afterAnimId >= 0 && stoppedAt > 0f)
            {
                float ad = e.afterDur > 0.001f ? e.afterDur : 1f;
                float dt = UnityEngine.Time.time - stoppedAt;
                if (dt >= 0f && dt < ad) { inAfter = true; afterT = UnityEngine.Mathf.Min(dt / ad, 0.999f); }   // one pass, hold the last frame until it elapses
            }
            // PRE-MOVEMENT one-shot: just STARTED moving (e.g. a howitzer folding its legs) — plays once, then the Move loop
            if (moving && e.preMoveAnimId >= 0 && moveStartedAt > 0f)
            {
                float pd = e.preMoveDur > 0.001f ? e.preMoveDur : 1f;
                float dtp = UnityEngine.Time.time - moveStartedAt;
                if (dtp >= 0f && dtp < pd) { inPreMove = true; preMoveT = UnityEngine.Mathf.Min(dtp / pd, 0.999f); }
            }
            bool combatIdle = inCombat && e.combatAnimId >= 0 && !moving && !inAfter && !inAttack;
            // IDLE-ALT (2026-07-23, the tiger's howl): an OCCASIONAL flavor one-shot while PLAIN idle — never during
            // move/attack/after/combat. One cadence per ENTRY (unit type): the pawn evaluated at due time becomes the
            // performer (its position is pinned; packmates keep idling — the animation twin of the growl's one-voice
            // radius). With BOTH alt clips baked, each firing picks randomly (howl now, eat later). Cadence = the
            // registry idleAltInterval, jittered 0.6-1.4x like the growl so it never reads as a metronome.
            if (!moving && !inAfter && !inAttack && !combatIdle && e.idleAltInterval > 0.01f && (e.idleAltAnimId >= 0 || e.idleAlt2AnimId >= 0))
            {
                float nowA = UnityEngine.Time.time;
                if (e.idleAltStart >= 0f)   // a firing is running — is THIS pawn the performer, and is it still inside the window?
                {
                    float dtA = nowA - e.idleAltStart;
                    if (dtA >= e.idleAltChosenDur) e.idleAltStart = -1f;
                    else if ((e.idleAltPos - pos).sqrMagnitude < 4f * 4f)
                    { inIdleAlt = true; idleAltT = UnityEngine.Mathf.Min(dtA / e.idleAltChosenDur, 0.999f); idleAltId = e.idleAltChosenId; }
                }
                else if (e.idleAltNextAt <= 0f)
                    e.idleAltNextAt = nowA + e.idleAltInterval * UnityEngine.Random.Range(0.6f, 1.4f);
                else if (nowA >= e.idleAltNextAt)
                {
                    bool both = e.idleAltAnimId >= 0 && e.idleAlt2AnimId >= 0;
                    bool second = e.idleAltAnimId < 0 || (both && UnityEngine.Random.value < 0.5f);
                    e.idleAltChosenId = second ? e.idleAlt2AnimId : e.idleAltAnimId;
                    float d = second ? e.idleAlt2Dur : e.idleAltDur;
                    e.idleAltChosenDur = d > 0.001f ? d : 1f;
                    e.idleAltStart = nowA; e.idleAltPos = pos;
                    e.idleAltNextAt = nowA + e.idleAltInterval * UnityEngine.Random.Range(0.6f, 1.4f);
                    inIdleAlt = true; idleAltT = 0f; idleAltId = e.idleAltChosenId;
                }
            }
            int chosen = inAttack ? e.attackAnimId : inPreMove ? e.preMoveAnimId : moving ? e.moveAnimId : inAfter ? e.afterAnimId : combatIdle ? e.combatAnimId : inIdleAlt ? idleAltId : e.animId;
            if (chosen != e.lastStateAnim)   // diagnostic: which state the pose weights select (transitions only)
            { e.lastStateAnim = chosen; Plugin.Log.LogInfo($"[State] pose '{e.resourceName}': matched, moving={moving} -> {(inAttack ? "ATTACK" : inPreMove ? "PRE-MOVE" : moving ? "MOVE" : inAfter ? "AFTER" : combatIdle ? "COMBAT-IDLE" : inIdleAlt ? "IDLE-ALT" : "IDLE")} (weights)"); }
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

        // TURRETIZE: aim a vehicle turret at the target by hijacking the game's OWN aim layer. The engine streams a
        // HEADING angle (AxisIndex 1 = up) into a BoneRotation slot whose SkeletonBoneIndex is the INVALID sentinel on
        // our injected models (so it hits nothing). We repoint that slot at OUR turret bone — the engine's aim yaw then
        // rotates the turret, no per-frame trig. Wheel-spin junk (axis 0) is still zeroed (our wheels are baked). The
        // turret bone index is resolved once from the skeleton's BoneInfos (same substring match as the hand prop).
        static bool turretLogged;
        static void TurretizeAimLayer(object entry, ModelEntry e)
        {
            if (e.turretBoneIdx == -2)   // resolve once
            {
                e.turretBoneIdx = -1;
                if (e.skeleton != null && GetMember(e.skeleton, "BoneInfos") is Array bones)
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var n = GetMember(bones.GetValue(i), "Name")?.ToString() ?? "";
                        if (n.IndexOf(e.turretBone, StringComparison.OrdinalIgnoreCase) >= 0) { e.turretBoneIdx = i; break; }
                    }
                Plugin.Log.LogInfo($"[Turret] '{e.resourceName}' turret bone '{e.turretBone}' -> skeleton index {e.turretBoneIdx}");
            }
            if (e.turretBoneIdx < 0) { SanitizeAimLayer(entry); return; }   // no such bone — fall back to the safe path
            for (int i = 0; i < 4; i++)
            {
                var br = GetMember(entry, BoneRotationNames[i]);
                if (br == null) continue;
                long axis, boneIdx;
                try { axis = Convert.ToInt64(GetMember(br, "AxisIndex")); boneIdx = Convert.ToInt64(GetMember(br, "SkeletonBoneIndex")); } catch { continue; }
                bool invalid = boneIdx < 0 || boneIdx >= 100000;           // the 0xFFFFFFFF sentinel (aim meant for us)
                if (!invalid) continue;                                    // a real game bone — leave it
                if (axis == 1)                                             // HEADING channel -> aim our turret
                {
                    SetMember(br, "SkeletonBoneIndex", (uint)e.turretBoneIdx);
                    if (e.turretAxis >= 0)                                  // override the rotation axis (yaw for a turret, pitch for an artillery barrel)
                    {
                        object curAx = GetMember(br, "AxisIndex"); Type axT = curAx?.GetType();
                        object newAx = (axT != null && axT.IsEnum) ? Enum.ToObject(axT, e.turretAxis)
                                     : (axT != null) ? Convert.ChangeType((long)e.turretAxis, axT)
                                     : (object)(uint)e.turretAxis;
                        SetMember(br, "AxisIndex", newAx);
                    }
                    SetMember(entry, BoneRotationNames[i], br);
                    if (!turretLogged) { turretLogged = true; float ang = 0f; try { ang = Convert.ToSingle(GetMember(br, "Angle")); } catch { }
                        Plugin.Log.LogInfo($"[Turret] '{e.resourceName}' slot {i} heading angle {ang:0.#}° -> bone {e.turretBoneIdx}, axis {(e.turretAxis >= 0 ? e.turretAxis.ToString() : "keep")}"); }
                }
                else                                                       // wheel-spin (axis 0) etc. -> zero
                {
                    float a; try { a = Convert.ToSingle(GetMember(br, "Angle")); } catch { continue; }
                    if (a != 0f) { SetMember(br, "Angle", 0f); SetMember(entry, BoneRotationNames[i], br); }
                }
            }
        }

        // Clear the procedural AIM layer (PawnEntry.BoneRotation0-3 = SkeletonBoneIndex/AxisIndex/Angle): the game aims an
        // artillery barrel by layering a bone rotation ON TOP of the pose, which twists our barrel (most visible while moving,
        // as the aim swings). Zero the angles so ONLY our baked clip drives the skeleton. No-op for un-aimed models.
        // SANITIZE the bone-rotation layer without killing it: on slots whose SkeletonBoneIndex is INVALID (the
        // 0xFFFFFFFF sentinel some donors emit), wrap the runaway accumulated Angle (1558°…) into 0..360 — the same
        // orientation, but a sane magnitude, so whatever downstream math consumes the raw value stops tearing the rig
        // apart (the Combine soldier's head). Slots with REAL bone indices are left untouched (genuine game layers).
        static bool sanitizeLogged;
        static void SanitizeAimLayer(object entry)
        {
            for (int i = 0; i < 4; i++)
            {
                var br = GetMember(entry, BoneRotationNames[i]);
                if (br == null) continue;
                long boneIdx;
                try { boneIdx = Convert.ToInt64(GetMember(br, "SkeletonBoneIndex")); } catch { continue; }
                if (boneIdx >= 0 && boneIdx < 100000) continue;        // a real bone — the game's own layer, keep it
                // Invalid-bone slots, by axis (observed on the Humvee donor): axis=1 (up) ≈ the HEADING channel — keep
                // it, it turns the pawn. axis=0 (roll) = the donor's WHEEL SPIN, accumulating as the unit travels —
                // on a humanoid rig those land on head/neck bones and wind them up (the ripped-off head). Zero them:
                // our model has no wheels to roll. (Magnitude-wrapping alone was tried first and didn't help — even a
                // sane 250° on the wrong bone tears the rig.)
                long axis;
                try { axis = Convert.ToInt64(GetMember(br, "AxisIndex")); } catch { continue; }
                if (axis == 1) continue;                                // heading — leave alone
                float a;
                try { a = Convert.ToSingle(GetMember(br, "Angle")); } catch { continue; }
                if (a == 0f) continue;
                SetMember(br, "Angle", 0f);
                SetMember(entry, BoneRotationNames[i], br);
                if (!sanitizeLogged) { sanitizeLogged = true; Plugin.Log.LogInfo($"[Uni] aim-layer sanitize: slot {i} bone={boneIdx} axis={axis} angle {a:0.#} -> 0 (donor wheel-spin on an invalid bone)"); }
            }
        }

        static float lastAimLog;
        static void ClearAimLayer(object entry)
        {
            for (int i = 0; i < 4; i++)
            {
                var br = GetMember(entry, BoneRotationNames[i]);
                if (br == null) continue;
                // DIAGNOSTIC (bombard face-plant, 2026-07-19): log what the game streamed into the aim layer BEFORE
                // we flatten it — angle + which of OUR bones the donor-meant index lands on. Throttled to 1/s.
                try
                {
                    float ang = Convert.ToSingle(GetMember(br, "Angle"));
                    if (ang != 0f && UnityEngine.Time.time - lastAimLog > 1f)
                    {
                        lastAimLog = UnityEngine.Time.time;
                        // dump EVERY field of the BoneRotation struct — the first probe guessed member names
                        // (BoneIndex/Axis) and read nothing, and the index decides whether this runaway angle
                        // lands on a real bone (the diver) or the invalid 0xFFFFFFFF sentinel (a red herring).
                        var t = br.GetType();
                        var dump = string.Join(" ", t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            .Select(fi => fi.Name + "=" + fi.GetValue(br)));
                        Plugin.Log.LogInfo($"[Aim] streamed slot{i} ({t.Name}): {dump} (cleared)");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[Aim] dump: " + ex.Message); }
                SetMember(br, "Angle", 0f);
                SetMember(entry, BoneRotationNames[i], br);
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
                // Two consumers share activeFires: fireOnAttack (artillery one-shot, window = the main clip's duration)
                // and the STATE-DRIVEN attack clip (window = attackDur; armed directly by the ranged-fight hook).
                bool stateAttack = e.animStateDriven && e.attackAnimId >= 0;
                if (!e.fireOnAttack && !stateAttack) continue;
                float dur = stateAttack ? (e.attackDur > 0.001f ? e.attackDur : 1f) * (e.attackRepeats > 0 ? e.attackRepeats : 1)
                                        : (e.animDuration > 0.001f ? e.animDuration : 1f);
                // reverse for-loop, NOT RemoveAll: the dur-capturing lambda allocated a closure per entry per FRAME,
                // even with zero active fires (perf pass 2026-07-19)
                lock (e.activeFires)
                    for (int i = e.activeFires.Count - 1; i >= 0; i--)
                        if (UnityEngine.Time.time - e.activeFires[i].startTime >= dur) e.activeFires.RemoveAt(i);   // drop finished one-shots
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
                    // Drain for state-attack entries too: a bombard by a state-driven unit arrives via the artillery
                    // hook's GUID queue (and an undrained queue would otherwise grow for the whole session).
                    if ((!e.fireOnAttack && !(e.animStateDriven && e.attackAnimId >= 0)) || e.fireGuidQueue.IsEmpty) continue;
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

        // STATE-DRIVEN ATTACK trigger (Hk_PawnRangedFight postfix). Every pawn ranged shot — battle volley, unit
        // target, district bombard — funnels through PawnRangedFightSequence.InitializeCommon(shooter, ...), and the
        // sequence is built PRESENTATION-side (main thread), so unlike the artillery sim-thread hook we can read the
        // shooter's Transform directly and arm the fire window right here: no GUID queue, no Update-drain roundtrip.
        // Arm the state-driven ATTACK clip for a pawn that just attacked — RANGED or MELEE. Not ranged-specific: it reads
        // the attacker pawn's unit def + world position and, if it's one of our state-attack models, records a FireInstance
        // the pose hook plays the attack clip from. `how` is just for the log ("ranged shot" / "melee attack").
        // Play a one-shot attack cue that stays AUDIBLE regardless of camera distance. AudioSource.PlayClipAtPoint uses
        // minDistance=1 + logarithmic rolloff, so during a battle (camera pulled far back) the roar attenuated to silence —
        // the log showed it firing, but you couldn't hear it. Here: a temp AudioSource, mostly-2D blend + a large minDistance
        // + linear rolloff, so an attack cue reads loud and clear at any zoom (it's a dramatic focal moment, not ambience).
        static void PlayAttackOneShot(UnityEngine.AudioClip clip, UnityEngine.Vector3 pos, float vol, float offsetSec = 0f)
        {
            var go = new UnityEngine.GameObject("HAF_attackSfx");
            go.transform.position = pos;
            var src = go.AddComponent<UnityEngine.AudioSource>();
            src.clip = clip; src.volume = vol; src.spatialBlend = 0.35f;   // mostly 2D → always audible, slight directional flavour
            src.minDistance = 60f; src.maxDistance = 1200f; src.rolloffMode = UnityEngine.AudioRolloffMode.Linear;
            // start offset: skip a silent/windup lead-in so the impact lands on the swing (clamped inside the clip)
            float off = UnityEngine.Mathf.Clamp(offsetSec, 0f, UnityEngine.Mathf.Max(0f, clip.length - 0.05f));
            if (off > 0f) src.time = off;
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length - off + 0.15f);
        }

        // playSound/armAnim let a hook drive ONLY one channel: the melee attack SOUND fires from the EARLY fight-start hook
        // (StartPawnAction — the moment the attack begins), while the attack ANIMATION arms from the PER-SWING hook
        // (StartPairMeleeAttack). Firing the sound per-swing made the roar land near the END of the anim (too late);
        // fight-start is "the moment you attack". Ranged still does both from its one shot hook (timing is fine there).
        internal static void OnPawnAttack(object attacker, string how, bool playSound = true, bool armAnim = true)
        {
            try
            {
                if (attacker == null || !Plugin.UniversalInjectOn.Value) return;
                var list = entries;
                if (list == null) return;
                bool any = false;
                foreach (var x in list) if ((x.animStateDriven && x.attackAnimId >= 0) || !string.IsNullOrEmpty(x.soundAttackFile)) { any = true; break; }
                if (!any) return;   // no state-attack model AND no attack-sound model registered — skip the reflection walk entirely
                var unit = GetMember(attacker, "PresentationUnit");
                string unitDef = GetMember(unit, "UnitDefinition")?.ToString() ?? "";
                var e = FindEntryForUnitDefinition(unitDef);
                if (e == null) return;
                bool wantAnim = armAnim && e.animStateDriven && e.attackAnimId >= 0;
                bool wantSound = playSound && e.customAttackClip != null;
                if (!wantAnim && !wantSound) return;
                if (!(GetMember(attacker, "Transform") is UnityEngine.Transform tr)) return;
                long pid = attacker.GetHashCode();
                float now = UnityEngine.Time.time;

                // ATTACK SOUND: a DISTINCT, more violent one-shot at the attacker (vs the idle growl). Per-pawn min-gap so a
                // rapid multi-swing fight doesn't machine-gun it — each distinct swing past the gap still triggers. Plays
                // even when the model isn't state-driven (a unit can want an attack sound without our attack animation).
                if (wantSound && (!e.attackSoundNextAt.TryGetValue(pid, out var nextS) || now >= nextS))
                {
                    PlayAttackOneShot(e.customAttackClip, tr.position, e.soundAttackVolume, e.soundAttackOffset);
                    float audible = Math.Max(0.05f, e.customAttackClip.length - Math.Max(0f, e.soundAttackOffset));   // gap keys off what actually PLAYS (post-offset)
                    e.attackSoundNextAt[pid] = now + Math.Max(0.25f, Math.Min(audible, 1.2f));
                }

                // ATTACK ANIMATION: key the fire by the ATTACKING PAWN's identity, and if that pawn already has an active
                // fire, RESTART it (update start time + position) instead of stacking a second. Melee swings come faster
                // than one attack window, and the pose hook plays the FIRST/oldest matching fire for the whole window — so
                // without this a rapid second swing was swallowed by the first's window (looked like ONE long attack, or
                // nothing when the window was tiny). Per-pawn restart = each swing replays the bite from frame 0.
                if (wantAnim)
                {
                    var fi = new FireInstance { pos = tr.position, startTime = now, pawnId = pid };
                    lock (e.activeFires)
                    {
                        int at = -1;
                        for (int i = 0; i < e.activeFires.Count; i++) if (e.activeFires[i].pawnId == pid) { at = i; break; }
                        if (at >= 0) e.activeFires[at] = fi; else e.activeFires.Add(fi);   // restart this pawn's bite, or arm a new one
                    }
                }
                Plugin.Log.LogInfo($"[State] '{e.resourceName}' {how} at {tr.position.ToString("0.0")} — {(wantAnim ? "attack clip armed" : "")}{(wantAnim && wantSound ? " + " : "")}{(wantSound ? "attack sound" : "")} (pawn {pid})");
            }
            catch (Exception ex) { Plugin.Log.LogError("[State] OnPawnAttack: " + ex); }
        }

        // ---- ADJACENT-ATTACK ROTATION DIAGNOSTIC (2026-07-21) ----
        // Symptom: a custom unit turns toward a RANGED target but not an ADJACENT one. Range facing and adjacent
        // facing run different choreography actions (UnitActionFaceEnemy vs UnitActionLookAt, asym vs symmetrical
        // prepare), all funnelling into RotationFSM.StartDirectionToLook. These logs show, per fight, WHICH actions
        // start for OUR units and whether the rotation FSM was asked to turn at all — separating "action never
        // created" (a choreography gate) from "FSM started but nothing visibly turned" (animation-driven yaw or
        // aim-layer masking). Fires only during fights; filtered to our units, so it stays quiet.
        internal static void OnUnitActionDiag(object action, string kind)
        {
            try
            {
                if (entries == null) return;
                string au = GetMember(GetMember(GetMember(action, "AttackerBattleUnit"), "PresentationUnit"), "UnitDefinition")?.ToString() ?? "";
                string du = GetMember(GetMember(GetMember(action, "DefenderBattleUnit"), "PresentationUnit"), "UnitDefinition")?.ToString() ?? "";
                var ea = FindEntryForUnitDefinition(au); var ed = FindEntryForUnitDefinition(du);
                if (ea == null && ed == null) return;
                string scope = GetMember(action, "actionScope")?.ToString() ?? "?";
                Plugin.Log.LogInfo($"[Rot] {kind} scope={scope} attacker={(ea != null ? "OURS:" + ea.resourceName : Tail(au))} defender={(ed != null ? "OURS:" + ed.resourceName : Tail(du))}");

                // EARLY ATTACK SOUND: FaceEnemy.Start fires the moment OUR unit commits to the strike — it turns to face the
                // enemy BEFORE the melee choreography/swing (verified: this line precedes 'melee start' in the log). That's as
                // close to "the moment you order the attack" as the presentation gives us, so we fire the roar HERE instead of
                // the later fight-start hook. Gate: our unit is the ATTACKER and has an attack clip. Per-attacker min-gap
                // (keyed by our entry + the battle-unit) stops a double if FaceEnemy re-fires. Played mostly-2D at the camera
                // so it's audible at any zoom (PlayAttackOneShot).
                if (ea != null && ea.customAttackClip != null && kind == "FaceEnemy.Start"
                    && scope != null && scope.IndexOf("Attacker", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    long key = ((long)ea.resourceName.GetHashCode() << 20) ^ (GetMember(action, "AttackerBattleUnit")?.GetHashCode() ?? 0);
                    float now = UnityEngine.Time.time;
                    if (!ea.attackSoundNextAt.TryGetValue(key, out var next) || now >= next)
                    {
                        var camPos = UnityEngine.Camera.main != null ? UnityEngine.Camera.main.transform.position : UnityEngine.Vector3.zero;
                        PlayAttackOneShot(ea.customAttackClip, camPos, ea.soundAttackVolume, ea.soundAttackOffset);
                        ea.attackSoundNextAt[key] = now + Math.Max(0.25f, Math.Min(ea.customAttackClip.length, 1.2f));
                        Plugin.Log.LogInfo($"[State] '{ea.resourceName}' attack sound (FaceEnemy — early)");
                    }
                }
            }
            catch { }
        }

        internal static void OnRotationStartDiag(object fsm, int result)
        {
            try
            {
                if (entries == null) return;
                var pawn = GetMember(fsm, "ownerPawn");
                string ud = GetMember(GetMember(pawn, "PresentationUnit"), "UnitDefinition")?.ToString() ?? "";
                var e = FindEntryForUnitDefinition(ud);
                if (e == null) return;
                var tr = GetMember(pawn, "Transform") as UnityEngine.Transform;
                string dir = GetMember(fsm, "rotationLookDirection") is UnityEngine.Vector3 d ? d.ToString("0.00") : "?";
                Plugin.Log.LogInfo($"[Rot] '{e.resourceName}' StartDirectionToLook -> steps={result} lookDir={dir} yawNow={(tr != null ? tr.eulerAngles.y.ToString("0") : "?")}");
            }
            catch { }
        }

        static string Tail(string s) => string.IsNullOrEmpty(s) ? "(none)" : (s.Length > 60 ? s.Substring(0, 60) : s);

        // ---- DEATH CUE (2026-07-23): a one-shot when a pawn of ours starts dying. Seam: PresentationPawn.TriggerDeath —
        // presentation-side, fired exactly once per dying pawn as its death FSM starts (IsDead is set inside it). A wiped
        // stack dies pawn-by-pawn in a burst, so a short per-entry min-gap turns five simultaneous rattles into one. ----
        internal static void OnPawnDeath(object pawn)
        {
            try
            {
                if (pawn == null || !Plugin.UniversalInjectOn.Value) return;
                var list = entries;
                if (list == null) return;
                bool any = false;
                foreach (var x in list) if (!string.IsNullOrEmpty(x.soundDeathFile)) { any = true; break; }
                if (!any) return;
                string ud = GetMember(GetMember(pawn, "PresentationUnit"), "UnitDefinition")?.ToString() ?? "";
                var e = FindEntryForUnitDefinition(ud);
                if (e == null || e.customDeathClip == null) return;
                float now = UnityEngine.Time.time;
                if (now < e.deathSoundNextAt) return;
                e.deathSoundNextAt = now + 0.6f;
                var tr = GetMember(pawn, "Transform") as UnityEngine.Transform;
                PlayAttackOneShot(e.customDeathClip, tr != null ? tr.position : UnityEngine.Vector3.zero, e.soundDeathVolume, e.soundDeathOffset);
                Plugin.Log.LogInfo($"[Sound] '{e.resourceName}' death cue");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Sound] OnPawnDeath: " + ex); }
        }

        // ---- BATTLE-START WAR CRY (2026-07-23). SimulationEvent_BattleStarted.Raise carries the Battle; walking
        // AttackerGroup/DefenderGroup -> Contenders -> Units -> Unit.UnitDefinition finds OUR units in it (pure managed
        // reads — safe on the sim thread). Matches are QUEUED and played on the main thread (Unity audio APIs), one cry
        // per entry per battle, camera-anchored like the attack roar so it opens the battle audibly at any zoom. ----
        static readonly System.Collections.Concurrent.ConcurrentQueue<ModelEntry> battleCryQueue = new System.Collections.Concurrent.ConcurrentQueue<ModelEntry>();
        internal static void OnBattleStarted(object battle)
        {
            try
            {
                if (battle == null || !Plugin.UniversalInjectOn.Value) return;
                var list = entries;
                if (list == null) return;
                bool any = false;
                foreach (var x in list) if (!string.IsNullOrEmpty(x.soundBattleFile)) { any = true; break; }
                if (!any) return;
                var found = new HashSet<ModelEntry>();
                foreach (var g in new[] { GetMember(battle, "AttackerGroup"), GetMember(battle, "DefenderGroup") })
                {
                    if (!(GetMember(g, "Contenders") is System.Collections.IEnumerable conts)) continue;
                    foreach (var c in conts)
                    {
                        if (!(GetMember(c, "Units") is System.Collections.IEnumerable units)) continue;
                        foreach (var bu in units)
                        {
                            string ud = GetMember(GetMember(bu, "Unit"), "UnitDefinition")?.ToString() ?? "";
                            var e = FindEntryForUnitDefinition(ud);
                            if (e != null && !string.IsNullOrEmpty(e.soundBattleFile)) found.Add(e);
                        }
                    }
                }
                foreach (var e in found)
                {
                    battleCryQueue.Enqueue(e);
                    Plugin.Log.LogInfo($"[Sound] battle started with '{e.resourceName}' — war cry queued");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Sound] OnBattleStarted: " + ex); }
        }

        // Main-thread drain (Plugin.Update). If an entry's clips haven't lazy-loaded yet (battle at load, before the
        // first audio poll), the cry is left in the queue for a later frame rather than dropped.
        internal static void ProcessBattleCries()
        {
            while (battleCryQueue.TryDequeue(out var e))
            {
                if (e.customBattleClip == null)
                {
                    if (!e.customClipTried) { battleCryQueue.Enqueue(e); return; }   // clips not loaded yet — retry next frame
                    continue;                                                        // load ran and failed — drop, already logged
                }
                float now = UnityEngine.Time.time;
                if (now < e.battleCryNextAt) continue;
                e.battleCryNextAt = now + 2f;
                var camPos = UnityEngine.Camera.main != null ? UnityEngine.Camera.main.transform.position : UnityEngine.Vector3.zero;
                PlayAttackOneShot(e.customBattleClip, camPos, e.soundBattleVolume, e.soundBattleOffset);
                Plugin.Log.LogInfo($"[Sound] '{e.resourceName}' war cry");
            }
        }

        // STATE-DRIVEN poll (main thread — Plugin.Update; Phase 2, 2026-07-19). For each animStateDriven model: per
        // unit, MOVEMENT = the render position actually changed since the last poll — the deploy poll's proven
        // settle-immune signal (wait-to-idle / turn-in-place after stopping does NOT move the tile position, so a
        // settling unit reads stopped and the after/idle clips play instead of the run). On a moving->stopped flip
        // the stop time is recorded for the AFTER one-shot window. Publishes one sample per PAWN under lock, so
        // every soldier of a squad animates (the pose hook matches by nearest sample).
        static int stateFrame;
        internal static void ProcessAnimStates()
        {
            var list = entries;
            if (list == null || !Plugin.UniversalInjectOn.Value) return;
            bool any = false;
            foreach (var e in list) if (e.animStateDriven && e.moveAnimId >= 0) { any = true; break; }
            if (!any) return;
            if (++stateFrame % 3 != 0) return;   // ~20x/s, like the deploy poll
            try
            {
                float now = UnityEngine.Time.time;
                var presType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.Presentation");
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return;
                var fresh = new Dictionary<ModelEntry, List<StateSample>>();
                var seen = new Dictionary<ModelEntry, HashSet<long>>();
                foreach (var e in list) if (e.animStateDriven && e.moveAnimId >= 0) { fresh[e] = new List<StateSample>(); seen[e] = new HashSet<long>(); }
                // One sampler for BOTH walks below. keySalt separates the map-army and battle bookkeeping for the
                // same sim unit: during a battle the army's PresentationUnit still exists at the STACK position while
                // the battle deploys a SECOND PresentationUnit on its combat tile — same GUID, different objects. A
                // shared key would ping-pong stateLastPos between the two positions and read as permanently "moving".
                void SampleUnit(object unit, bool combat, long keySalt)
                {
                    if (unit == null) return;
                    string uname = GetMember(GetMember(unit, "UnitDefinition"), "Name")?.ToString() ?? "";
                    if (uname.Length == 0) return;
                    ModelEntry e = null;
                    foreach (var x in list)
                        if (x.animStateDriven && x.moveAnimId >= 0 && x.pawnDescription.Length > 0
                            && uname.IndexOf(CoreDesc(x.pawnDescription), StringComparison.OrdinalIgnoreCase) >= 0) { e = x; break; }
                    if (e == null) return;
                    long guid = GuidToLong(GetMember(unit, "GUID"));
                    if (guid == 0) return;
                    guid = unchecked(guid ^ keySalt);
                    var pawnList = (GetMember(unit, "Pawns") as System.Collections.IEnumerable)?.Cast<object>().ToList();
                    UnityEngine.Vector3 upos = UnityEngine.Vector3.zero; bool hasPos = false;
                    if (pawnList != null)
                        foreach (var pawn in pawnList)
                            if (GetMember(pawn, "Transform") is UnityEngine.Transform tr0) { upos = tr0.position; hasPos = true; break; }
                    bool moving = false;
                    if (hasPos)
                    {
                        if (e.stateLastPos.TryGetValue(guid, out var lastP)) moving = (upos - lastP).sqrMagnitude > 0.1f * 0.1f;
                        e.stateLastPos[guid] = upos;
                    }
                    if (!e.stateMoving.TryGetValue(guid, out bool wasMoving)) wasMoving = false;
                    if (wasMoving != moving)
                    {
                        if (wasMoving && !moving) e.stateStoppedAt[guid] = now;        // the AFTER one-shot window starts here
                        if (!wasMoving && moving) e.stateMoveStartedAt[guid] = now;    // the PRE-MOVEMENT one-shot window starts here
                        Plugin.Log.LogInfo($"[State] '{e.resourceName}' unit {guid} moving={moving}{(combat ? " (battle)" : "")}");
                    }
                    e.stateMoving[guid] = moving;
                    float stoppedAt = e.stateStoppedAt.TryGetValue(guid, out var sAt) ? sAt : -1f;
                    float moveStartedAt = e.stateMoveStartedAt.TryGetValue(guid, out var mAt) ? mAt : -1f;
                    seen[e].Add(guid);
                    if (pawnList != null)
                        foreach (var pawn in pawnList)
                            if (GetMember(pawn, "Transform") is UnityEngine.Transform tr)
                                fresh[e].Add(new StateSample { pos = tr.position, moving = moving, stoppedAt = stoppedAt, moveStartedAt = moveStartedAt, combat = combat });
                }
                foreach (var army in armies)
                {
                    if (army == null) continue;
                    // COMBAT stance: PresentationArmy.IsLockedByBattle (public bool on exactly the objects this walk
                    // iterates) is true from battle deployment until resolution — the pose hook swaps IDLE for the
                    // combat-idle clip while it holds. Reflection-safe: a missing member just reads false.
                    bool combat = false;
                    try { combat = GetMember(army, "IsLockedByBattle") is bool b && b; } catch { }
                    SampleUnit(GetMember(army, "PresentationUnit"), combat, 0L);
                }
                // BATTLE-DEPLOYED units: a battle spawns its own PresentationBattleUnit list whose PresentationUnits
                // live on the combat tiles — the army walk's sample sits at the STACK position (27u+ away in the
                // field report), so without this walk a deployed pawn never matches (no stance, no movement state).
                // Presentation.PresentationBattleReportController (a PresentationBattleController) -> Battles ->
                // AllUnits -> PresentationUnit (PresentationUnitHolder, same shape as the army's). Always combat=true.
                var bctl = CachedField(presType, "PresentationBattleReportController")?.GetValue(null);
                if (GetMember(bctl, "Battles") is System.Collections.IEnumerable battles)
                    foreach (var b in battles)
                        if (GetMember(b, "AllUnits") is System.Collections.IEnumerable allUnits)
                            foreach (var bu in allUnits)
                                SampleUnit(GetMember(bu, "PresentationUnit"), true, unchecked((long)0x5AAB5AAB5AAB5AABUL));
                foreach (var e in fresh.Keys)
                {
                    lock (e.stateSamples) { e.stateSamples.Clear(); e.stateSamples.AddRange(fresh[e]); }
                    foreach (var g in e.stateLastPos.Keys.Where(k => !seen[e].Contains(k)).ToList()) e.stateLastPos.Remove(g);      // drop gone units
                    foreach (var g in e.stateMoving.Keys.Where(k => !seen[e].Contains(k)).ToList()) e.stateMoving.Remove(g);
                    foreach (var g in e.stateStoppedAt.Keys.Where(k => !seen[e].Contains(k)).ToList()) e.stateStoppedAt.Remove(g);
                    foreach (var g in e.stateMoveStartedAt.Keys.Where(k => !seen[e].Contains(k)).ToList()) e.stateMoveStartedAt.Remove(g);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[State] ProcessAnimStates: " + ex); }
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
                    foreach (var g in e.deployLastPos.Keys.Where(k => !seen[e].Contains(k)).ToList()) e.deployLastPos.Remove(g);    // ...and their last-pos entries (this map used to grow forever)
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Deploy] ProcessDeployState: " + ex); }
        }

        static readonly string[] RenderMatFields = { "currentRenderMaterial", "runTimeRenderMaterial" };   // hoisted — was a new[] per RenderOutput per FRAME
        static void TickOne(ModelEntry e)
        {
            // GREY retry: if the skin wasn't ready when ApplyGrey ran (build returned null), build it now from the
            // isolated layer's still-original _MainTex. Runs at most until the first successful build (then e.tex latches).
            if (NeedsAdjust(e) && e.tex == null && e.isolatedLayer != null)
                e.tex = BuildAdjustedAtlas(e.isolatedLayer, e.brightness, e.desaturate, e.tintR, e.tintG, e.tintB, e.resourceName);
            if (e.hostOutputLayer == null || e.tex == null) return;
            try
            {
                if (GetMember(e.hostOutputLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                        foreach (var fld in RenderMatFields)
                            if (GetMember(ro, fld) is UnityEngine.Material mat)
                            {
                                // Already ours -> skip the 7 texture sets. The re-set stays as the RECOVERY path (the
                                // game can recreate/reset the material, which this check detects by reference), but it
                                // no longer runs redundantly every frame on a stable material (perf pass 2026-07-19).
                                if (ReferenceEquals(mat.GetTexture("_MainTex"), e.tex)) continue;
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

        // Paint an atlas onto a (cloned) output layer's render materials — the TickOne recipe generalized for props:
        // _MainTex swapped, the atlas UV transform reset to 1:1 (the host cropped a slice of a SHARED atlas), and the
        // host's overlay maps neutralized so only our albedo shows.
        static void PaintLayer(object layer, UnityEngine.Texture2D tex, string tag)
        {
            try
            {
                if (!(GetMember(layer, "RenderOutputs") is Array ros)) return;
                int painted = 0;
                foreach (var ro in ros)
                    foreach (var fld in RenderMatFields)
                        if (GetMember(ro, fld) is UnityEngine.Material mat)
                        {
                            if (ReferenceEquals(mat.GetTexture("_MainTex"), tex)) continue;
                            if (_flatN == null) { _flatN = Solid(0.5f, 0.5f, 1f); _white = Solid(1f, 1f, 1f); _black = Solid(0f, 0f, 0f); _grey = Solid(0.5f, 0.5f, 0.5f); }
                            mat.SetTexture("_MainTex", tex);
                            mat.SetTextureScale("_MainTex", UnityEngine.Vector2.one);
                            mat.SetTextureOffset("_MainTex", UnityEngine.Vector2.zero);
                            mat.SetTexture("_NormalMap", _flatN);
                            mat.SetTexture("_AmbiantOcclusionMap", _white);
                            mat.SetTexture("_ColorMask", _black);
                            mat.SetTexture("_RoughnessMap", _grey);
                            mat.SetTexture("_MetallicMap", _black);
                            painted++;
                        }
                if (painted > 0)   // silent when stable (per-tick recovery path) — logs only actual (re)paints
                    Plugin.Log.LogInfo($"[Props] '{tag}' prop layer painted ({painted} material(s), atlas {tex.width}x{tex.height})");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Props] PaintLayer: " + ex.Message); }
        }

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
        static List<ModelEntry> _audioOn;      // cached audio-enabled subset — the fields it filters on are set once at registry load
        static List<ModelEntry> _audioOnSrc;   // the entries list the cache was built from (rebuilt when the registry republishes)

        // ---- SILENCE DONOR AUDIO ----
        // AudioEmitter InstanceIDs whose Wwise posts we drop. Hk_SilenceAudio (prefix on AudioEmitter.PostEvent) reads this
        // every post and returns false — no lock: both writer (this poll) and reader (the post) run on the presentation
        // thread. Stale ids from destroyed emitters are harmless (they just never match a live emitter again).
        internal static readonly HashSet<int> _silencedEmitterIds = new HashSet<int>();
        static System.Reflection.MethodInfo _akStopAll; static bool _akStopAllTried;
        // Cut everything currently playing on ONE emitter's Wwise game-object — the idle loop that already started at spawn,
        // before this poll first registered the emitter. Future posts are handled by the suppress-prefix, so this is a
        // one-shot per emitter. AkSoundEngine.StopAll(ulong gameObjectId); the id is the emitter's AudioEntityGUID (implicitly
        // a ulong — we read the struct's backing 'guid' field). Best-effort: if the type/method can't be resolved the
        // prefix still stops all FUTURE growls, only the current loop instance would linger.
        static void StopAllOnEmitter(object emitter)
        {
            try
            {
                if (!_akStopAllTried)
                {
                    _akStopAllTried = true;
                    var ak = AccessTools.TypeByName("Amplitude.Wwise.Interop.AkSoundEngine") ?? AccessTools.TypeByName("AkSoundEngine");
                    if (ak != null)
                        foreach (var m in ak.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                            if (m.Name == "StopAll" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(ulong)) { _akStopAll = m; break; }
                    if (_akStopAll == null) Plugin.Log.LogWarning("[Audio] AkSoundEngine.StopAll(ulong) not found — an already-playing donor idle loop may linger until re-init");
                }
                if (_akStopAll == null) return;
                var g = GetMember(emitter, "AudioEntityGUID"); if (g == null) return;   // AudioEntityGUID struct (boxed)
                var raw = GetMember(g, "guid"); if (raw == null) return;                 // private readonly ulong backing field
                _akStopAll.Invoke(null, new object[] { Convert.ToUInt64(raw) });
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Audio] StopAllOnEmitter: " + ex.Message); }
        }

        public static void ProcessEngineAudio()
        {
            var list = entries;
            if (list == null || !Plugin.UniversalInjectOn.Value) return;
            // Throttle FIRST, and cache the filtered subset: the Where().ToList() used to run — and allocate — on
            // EVERY Plugin.Update call, 60x/s for the whole session, before the frame throttle (perf pass 2026-07-19).
            if (++engineFrame % 6 != 0) return;   // ~10x/s (render-position delta = movement)
            if (!ReferenceEquals(_audioOnSrc, list))
            {
                var built = new List<ModelEntry>();
                foreach (var x in list)
                    if ((x.engineSound || x.silenceDonorAudio || !string.IsNullOrEmpty(x.soundFile) || !string.IsNullOrEmpty(x.soundStartFile) || !string.IsNullOrEmpty(x.soundStopFile) || !string.IsNullOrEmpty(x.soundIdleFile) || !string.IsNullOrEmpty(x.soundAttackFile)) && !string.IsNullOrEmpty(x.pawnDescription))
                        built.Add(x);
                _audioOn = built; _audioOnSrc = list;
            }
            var on = _audioOn;
            if (on.Count == 0) return;
            try
            {
                // Lazy-load custom WAV clips + make sure a Unity AudioListener exists (a Wwise game may have none, which
                // would make our AudioSources silent).
                bool anyCustom = false;
                foreach (var e in on)
                {
                    if (string.IsNullOrEmpty(e.soundFile) && string.IsNullOrEmpty(e.soundStartFile) && string.IsNullOrEmpty(e.soundStopFile) && string.IsNullOrEmpty(e.soundIdleFile) && string.IsNullOrEmpty(e.soundAttackFile) && string.IsNullOrEmpty(e.soundDeathFile) && string.IsNullOrEmpty(e.soundBattleFile)) continue;
                    anyCustom = true;
                    if (!e.customClipTried)
                    {
                        e.customClipTried = true;
                        e.customClip = LoadCustom(e.soundFile, e.resourceName, e.assetDir);
                        e.customStartClip = LoadCustom(e.soundStartFile, e.resourceName + "_start", e.assetDir);
                        e.customStopClip = LoadCustom(e.soundStopFile, e.resourceName + "_stop", e.assetDir);
                        e.customIdleClip = LoadCustom(e.soundIdleFile, e.resourceName + "_idle", e.assetDir);
                        e.customAttackClip = LoadCustom(e.soundAttackFile, e.resourceName + "_attack", e.assetDir);
                        e.customDeathClip = LoadCustom(e.soundDeathFile, e.resourceName + "_death", e.assetDir);
                        e.customBattleClip = LoadCustom(e.soundBattleFile, e.resourceName + "_battle", e.assetDir);
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

                    // SILENCE DONOR AUDIO: register this pawn's emitter so Hk_SilenceAudio drops its future Wwise posts
                    // (idle growl re-posts + combat maul/scratch), and StopAll ONCE to cut the idle loop already running
                    // since spawn. Our custom WAVs (Unity AudioSource) don't post through the emitter, so they still play.
                    if (e.silenceDonorAudio && GetMember(sp, "AudioEmitter") is UnityEngine.Object emo && emo != null && _silencedEmitterIds.Add(emo.GetInstanceID()))
                    {
                        StopAllOnEmitter(emo);
                        Plugin.Log.LogInfo($"[Audio] '{e.resourceName}' donor audio silenced (emitter {emo.GetInstanceID()})");
                    }
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

                    // (A0) idle growl: an occasional one-shot WHILE IDLE (not moving), on a per-pawn jittered timer —
                    // replaces a silenced donor's periodic idle vocalization. Moving cancels the cadence (reschedules on
                    // stop) so it never fires mid-walk; jitter (0.6..1.4x) keeps a pack from growling in unison. The first
                    // growl is one interval out, so a fresh spawn/stop doesn't bark instantly.
                    if (e.customIdleClip != null && e.soundIdleInterval > 0f)
                    {
                        if (moving) e.idleNextAt.Remove(id);
                        else if (!e.idleNextAt.TryGetValue(id, out var idleNext))
                            e.idleNextAt[id] = now + e.soundIdleInterval * UnityEngine.Random.Range(0.6f, 1.4f);
                        else if (now >= idleNext)
                        {
                            // GROUP de-dup: a unit is many pawns, so without this all 5 of a formation snarl at once (a
                            // "cut-up" wall). Suppress this growl if another for THIS entry played within groupRadius in the
                            // last interval — a nearby packmate is already the voice; this pawn just waits its next turn. The
                            // voice rotates naturally as timers come due. radius<=0 disables (per-pawn, the old behaviour).
                            bool nearbyRecent = false;
                            if (e.soundIdleGroupRadius > 0f)
                            {
                                float cutoff = now - e.soundIdleInterval;
                                e.idleRecent.RemoveAll(r => r.Value < cutoff);
                                float r2 = e.soundIdleGroupRadius * e.soundIdleGroupRadius;
                                for (int k = 0; k < e.idleRecent.Count; k++)
                                    if ((e.idleRecent[k].Key - pos).sqrMagnitude <= r2) { nearbyRecent = true; break; }
                            }
                            if (!nearbyRecent)
                            {
                                UnityEngine.AudioSource.PlayClipAtPoint(e.customIdleClip, pos, e.soundIdleVolume);
                                if (e.soundIdleGroupRadius > 0f) e.idleRecent.Add(new KeyValuePair<UnityEngine.Vector3, float>(pos, now));
                            }
                            e.idleNextAt[id] = now + e.soundIdleInterval * UnityEngine.Random.Range(0.6f, 1.4f);
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

        // Load a custom WAV: the owning pack's <assetDir>/sounds/<file> first (per-pack assets, 2026-07-19), then the
        // legacy shared enc_sounds/<file>. Null if unset/missing.
        static UnityEngine.AudioClip LoadCustom(string file, string tag, string assetDir = "")
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (!string.IsNullOrEmpty(assetDir))
            {
                var pp = Path.Combine(assetDir, "sounds", file);
                if (File.Exists(pp)) return LoadWav(pp, tag);
            }
            var p = Path.Combine(Paths.ConfigPath, "enc_sounds", file);
            if (!File.Exists(p)) { Plugin.Log.LogWarning($"[Sound] file not found: {p}" + (string.IsNullOrEmpty(assetDir) ? "" : $" (also tried {Path.Combine(assetDir, "sounds")})")); return null; }
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
                    // A corrupt/truncated file can hold a negative chunk size -> p stops advancing -> the game hangs
                    // hard in this loop on the main thread. Bail on any size that can't advance the cursor.
                    if (csz < 0) { Plugin.Log.LogWarning("[Sound] malformed WAV chunk (negative size) in " + path + " — stopping parse."); break; }
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
            // NO '-' in the separators: the components are SIGNED ints, and splitting on '-' silently strips the
            // sign (a negative 'a' produced a corrupted GUID and a catalog miss — found via the Sling_Collection).
            var p = gs.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
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
        static bool propParsed; static int propWait; static bool propTickArmed; static int propTick;
        internal static void RearmPropRegistration() { propParsed = false; propPending.Clear(); propTickArmed = true; }   // AnimationLoad cleared the manager's list — register ours again
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
                // Until the first AnimationLoad the mod bundle isn't mounted, so every catalog request is a
                // guaranteed miss that LogErrors into the Amplitude diagnostics (64+ red lines per boot). The
                // loud AnimationLoad postfix is the registration moment that works; this tick only repairs
                // late failures after it — armed there, and paced to ~1 attempt/second.
                if (!propTickArmed || (++propTick % 60) != 0) return;
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
        static void Postfix(object __instance) { if (!hookLogged) { hookLogged = true; Plugin.Log.LogInfo("[Uni] UniRegisterHook POSTFIX fired"); } Prober.AnimMgr = __instance; UniversalInject.RearmModelRegistration(); UniversalInject.EnsureRegistered(__instance); }
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
            try
            {
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
            // This was the ONLY unguarded patch body in the plugin, sitting inside the game's own audio call path — a
            // destroyed handle whose .name throws must never break AudioManager.PostEvent (review 2026-07-19).
            catch { }
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
