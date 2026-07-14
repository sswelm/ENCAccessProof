using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ENCAccessProof
{
    // First BepInEx mod: prove we can (1) load, (2) hook the game, (3) read the live registry
    // AND the configured target mod's own assets — with a config file + an in-game feedback window.
    [BepInPlugin(GUID, "Humankind Asset Framework", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "community.humankind.encaccessproof";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> TargetMod;       // which mod's assets to access
        internal static ConfigEntry<string> AssetNameFilter; // substring that identifies that mod's assets
        internal static ConfigEntry<KeyCode> ToggleKey;      // open/close the feedback window (Shift+ToggleKey = dump GPU mesh-buffer usage)
        internal static ConfigEntry<bool>   UniversalInjectOn; // registry-driven universal injector (Model Factory)
        internal static ConfigEntry<int>    RespawnDelayFrames; // frames to wait after a borrowed-rotor unit renders before re-spawning it (first-instance rotor fix)

        private bool show;
        private Rect winRect = new Rect(60, 60, 480, 420);
        private Vector2 scroll;
        private string atlasFilter = "";   // Dump Atlases: only layers whose name contains this (blank = all)

        private void Awake()
        {
            Log = Logger;

            // --- the config file (auto-written to BepInEx/config/community.humankind.encaccessproof.cfg) ---
            TargetMod       = Config.Bind("General", "TargetMod", "ENCReload",
                                  "Name of the mod whose assets this plugin should access.");
            AssetNameFilter = Config.Bind("General", "AssetNameFilter", "Zeppelin",
                                  "Substring used to find that mod's assets in the loaded databases (proof of access).");
            ToggleKey       = Config.Bind("General", "ToggleWindowKey", KeyCode.F8,
                                  "Key to toggle the in-game feedback window. Hold SHIFT + this key to instead dump the live " +
                                  "GPU mesh-content buffer usage (verts/indices/meshes per layer vs the 100k/250k/256 ceiling) to the log.");
            UniversalInjectOn = Config.Bind("Factory", "UniversalInject", true,
                                  "Registry-driven universal model injector (the Model Factory). Reads the model registry JSON " +
                                  "from this config folder and repoints each listed pawn definition onto its baked skeleton.");
            RespawnDelayFrames = Config.Bind("Factory", "RespawnDelayFrames", 1,
                                  "Frames to wait after a borrowed-rotor unit (a model with respawnAfterLoad set) renders before " +
                                  "the plugin re-spawns it to clear the first-instance low-rotor bug. 1 = near-instant (default). " +
                                  "Increase (e.g. 30 = ~0.5s at 60fps) only if a slower machine briefly shows the low rotor before it corrects.");

            // Patch each hook independently so a single missing Amplitude target (a game update renaming one type) only
            // disables THAT hook -- instead of a null TargetMethod throwing out of PatchAll and failing the whole plugin.
            var harmony = new Harmony(GUID);
            int patched = 0;
            var hooks = new[] {
                typeof(UniRegisterHook), typeof(UniRepointHook), typeof(UniPawnPoseHook),
                typeof(Hk_ArtilleryStrike),   // firing-on-attack: bombard -> play the model's clip once (docs/Firing-On-Attack.md)
                typeof(Hk_AudioTrace),        // diagnostic: live-trace Wwise PostEvent (gated behind the F8 Audio Trace toggle)
            };
            foreach (var t in hooks)
            {
                try { harmony.CreateClassProcessor(t).Patch(); patched++; }
                catch (System.Exception ex) { Log.LogError($"[Uni] hook '{t.Name}' failed to apply (Amplitude API changed?): {ex.Message}"); }
            }
            Log.LogInfo($"Model Factory plugin loaded ({patched}/{hooks.Length} hooks patched). Press {ToggleKey.Value} in-game for the " +
                        $"diagnostic window. UniversalInject={UniversalInjectOn.Value}");
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey.Value))
            {
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    UniversalInject.DumpMeshBudget();   // Shift+F8 = dump GPU mesh-buffer usage (F9 collided with Humankind quick-load)
                else
                    show = !show;                       // F8 = toggle the feedback window
            }
            if (UniversalInjectOn.Value)
            {
                UniversalInject.TickTexture();          // keep registry-driven model atlases applied
                UniversalInject.MaybeRespawnPostLoad(); // one-shot post-load re-spawn to clear the first-instance rotor race
                UniversalInject.ProcessFireQueues();    // per-instance fire-on-attack: arm only the pawn that actually bombarded
                UniversalInject.ProcessDeployState();   // deploy-on-stop: record which of our pawns' units are currently moving
                UniversalInject.ProcessEngineAudio();   // engine sound: fire the per-ship Start/Stop move sound on our units
            }
        }

        private void OnGUI()
        {
            if (!show) return;
            winRect = GUILayout.Window(GUID.GetHashCode(), winRect, DrawWindow, "Humankind Asset Framework");
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label($"Target mod: {TargetMod.Value}     Filter: \"{AssetNameFilter.Value}\"");
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Re-scan now")) Prober.RunScan();
                if (GUILayout.Button("Scan Models")) Prober.ScanModels();
                if (GUILayout.Button("Test Write")) Prober.TestWrite();
                if (GUILayout.Button("Clear")) Prober.Report.Clear();
            }
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Dump UnitDef")) Prober.DumpUnitDef();
                if (GUILayout.Button("Dump Formation")) Prober.DumpFormation();
                if (GUILayout.Button("Dump Atlases")) UniversalInject.DumpOutputLayerAtlases(atlasFilter);
                if (GUILayout.Button("Dump Audio")) UniversalInject.DumpAudioState(atlasFilter);
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Dump name filter — atlas/audio (blank = all):", GUILayout.Width(220));
                atlasFilter = GUILayout.TextField(atlasFilter);   // e.g. "Corvette" -> dumps only that unit's layer, not all 600+
            }
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Play Audio (test) on filtered units")) UniversalInject.PlayAudioTest(atlasFilter);
                if (GUILayout.Button(UniversalInject.AudioTraceOn ? "Audio Trace: ON" : "Audio Trace: OFF"))
                { UniversalInject.AudioTraceOn = !UniversalInject.AudioTraceOn; UniversalInject.AudioTraceFilter = atlasFilter; }
                if (GUILayout.Button("Dump Sound Catalog")) UniversalInject.DumpSoundCatalog();
                if (GUILayout.Button("Play Sound Test (WAV)")) UniversalInject.PlaySoundTest();
            }
            GUILayout.Space(4);
            GUILayout.Label("GPU mesh buffer (live) — Shift+F8 also logs it:");
            foreach (var l in UniversalInject.MeshBudgetLines()) GUILayout.Label(l);
            GUILayout.Space(4);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(320));
            if (Prober.Report.Count == 0)
                GUILayout.Label("No scan yet — load a game (auto-scans on load), or press Re-scan.");
            foreach (var line in Prober.Report)
                GUILayout.Label(line);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }
    }
}
