using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ENCAccessProof
{
    // First BepInEx mod: prove we can (1) load, (2) hook the game, (3) read the live registry
    // AND the configured target mod's own assets — with a config file + an in-game feedback window.
    [BepInPlugin(GUID, "ENC Access Proof", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "community.humankind.encaccessproof";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> TargetMod;       // which mod's assets to access
        internal static ConfigEntry<string> AssetNameFilter; // substring that identifies that mod's assets
        internal static ConfigEntry<KeyCode> ToggleKey;      // open/close the feedback window
        internal static ConfigEntry<string> SourcePawn;      // pawn def to repoint (the zeppelin)
        internal static ConfigEntry<string> TargetFilter;    // pawn def to copy visuals FROM (substring)
        internal static ConfigEntry<string> CopyFields;      // which pawn-def fields to copy
        internal static ConfigEntry<string> ClearFields;     // pawn-def fields to null out (kill duplicates)
        internal static ConfigEntry<bool>   UniversalInjectOn; // registry-driven universal injector (Model Factory)
        internal static ConfigEntry<int>    RespawnDelayFrames; // frames to wait after a borrowed-rotor unit renders before re-spawning it (first-instance rotor fix)

        private bool show;
        private Rect winRect = new Rect(60, 60, 480, 420);
        private Vector2 scroll;

        private void Awake()
        {
            Log = Logger;

            // --- the config file (auto-written to BepInEx/config/community.humankind.encaccessproof.cfg) ---
            TargetMod       = Config.Bind("General", "TargetMod", "ENCReload",
                                  "Name of the mod whose assets this plugin should access.");
            AssetNameFilter = Config.Bind("General", "AssetNameFilter", "Zeppelin",
                                  "Substring used to find that mod's assets in the loaded databases (proof of access).");
            ToggleKey       = Config.Bind("General", "ToggleWindowKey", KeyCode.F8,
                                  "Key to toggle the in-game feedback window.");
            SourcePawn      = Config.Bind("Repoint", "SourcePawn", "Era5_Common_Zeppelins_01",
                                  "The pawn definition to repoint (the zeppelin).");
            TargetFilter    = Config.Bind("Repoint", "TargetFilter", "StrategicBomber",
                                  "Substring of the pawn definition to copy the visual from (e.g. StrategicBomber, Biplanes).");
            CopyFields      = Config.Bind("Repoint", "CopyFields", "Description,AnimatorOverrideController",
                                  "Comma-separated pawn-def fields to copy from target to source. Try dropping/adding " +
                                  "Attachements if you get a doubled model. Options: Description, Attachements, " +
                                  "AnimatorOverrideController, AnimationCapabilityProfile, MaxHeight, CharacterPalette.");
            ClearFields     = Config.Bind("Repoint", "ClearFields", "",
                                  "Comma-separated pawn-def fields to null out after copying (e.g. SubPawnDefinitions) " +
                                  "to remove duplicate/overlapping geometry.");
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
            foreach (var t in new[] { typeof(UniRegisterHook), typeof(UniRepointHook), typeof(UniPawnPoseHook) })
            {
                try { harmony.CreateClassProcessor(t).Patch(); patched++; }
                catch (System.Exception ex) { Log.LogError($"[Uni] hook '{t.Name}' failed to apply (Amplitude API changed?): {ex.Message}"); }
            }
            Log.LogInfo($"Model Factory plugin loaded ({patched}/3 hooks patched). Press {ToggleKey.Value} in-game for the " +
                        $"diagnostic window. UniversalInject={UniversalInjectOn.Value}");
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey.Value)) show = !show;
            if (UniversalInjectOn.Value)
            {
                UniversalInject.TickTexture();          // keep registry-driven model atlases applied
                UniversalInject.MaybeRespawnPostLoad(); // one-shot post-load re-spawn to clear the first-instance rotor race
            }
        }

        private void OnGUI()
        {
            if (!show) return;
            winRect = GUILayout.Window(GUID.GetHashCode(), winRect, DrawWindow, "ENC Access Proof");
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
                if (GUILayout.Button($"Repoint zeppelin -> {TargetFilter.Value}")) Prober.Repoint();
                if (GUILayout.Button("Dump Struct")) Prober.DumpStructure();
                if (GUILayout.Button("Dump UnitDef")) Prober.DumpUnitDef();
                if (GUILayout.Button("Dump Formation")) Prober.DumpFormation();
            }
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
