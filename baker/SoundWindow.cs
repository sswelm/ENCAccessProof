// SoundWindow.cs — "Sound Studio" (Tools ▸ HAF ▸ Sound Studio): one place to configure a unit's whole audio profile,
// apart from the skin (Unit Retexture) window. Foldout sections: Silence an inherited donor sound; add an Idle growl
// (occasional, one-voice-per-unit), an Attack roar (on strike), Movement WAVs (start/travel/stop), or a Wwise engine
// event. The runtime plugin (ENCAccessProof) drives all of it — no bake. Writes onto the SAME registry entry the
// Factory/Retexture use (matched by pawn), so a unit keeps ONE entry.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public class SoundWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Sound Studio")]
    static void Open() => GetWindow<SoundWindow>("Sound Studio");

    string pawn = "";
    string resourceName = "";
    string startFile = "", startPath = ""; float startVol = 1f;   // Start spool-up
    string loopFile = "", loopPath = "";  float loopVol = 1f;     // Travel loop
    string stopFile = "", stopPath = "";  float stopVol = 1f;     // Stop spool-down
    bool engineSound = false; string engineStart = "", engineStop = "";   // game (Wwise) engine event alternative
    bool silenceDonor = false;   // suppress the borrowed donor's inherited Wwise sound (idle growl + combat maul)
    string idleFile = "", idlePath = ""; float idleVol = 1f, idleInterval = 11f, idleGroupRadius = 10f;   // occasional idle growl (one-shot on a timer) + one-voice-per-unit radius
    string attackFile = "", attackPath = ""; float attackVol = 1f, attackOffset = 0f;   // violent one-shot on each attack (+ seconds skipped into the WAV)
    string deathFile = "", deathPath = ""; float deathVol = 1f, deathOffset = 0f;       // one-shot rattle/scream when a pawn dies
    string battleFile = "", battlePath = ""; float battleVol = 1f, battleOffset = 0f;   // war-cry one-shot when a battle starts with this unit in it
    // foldout section state (creature-voice trio open by default; movement/wwise collapsed)
    bool foldSilence = true, foldIdle = true, foldAttack = true, foldDeath = true, foldBattle = true, foldMove = false, foldWwise = false;
    Vector2 scroll; string status = "";

    static string SoundsDir => Path.Combine(ModelRegistry.ConfigDir, "enc_sounds");

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Sound Studio — a unit's whole audio profile in one place. Silence an inherited donor sound, and add your own " +
            "idle growl, attack roar, and movement sounds (or a Wwise engine event). Custom WAVs: 16-bit PCM; mono = 3D. " +
            "Relaunch / reload a save to hear changes.", MessageType.Info);

        var all = ModelRegistry.Load();

        // --- pawn ---
        EditorGUILayout.LabelField("Pawn", EditorStyles.boldLabel);
        pawn = EditorGUILayout.TextField(new GUIContent("Pawn description", "A unique substring of the unit's pawn descriptor, e.g. Era6_Common_ReconDrones_01."), pawn);
        var pawns = all.Select(m => m.pawnDescription).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToArray();
        if (pawns.Length > 0)
        {
            var opts = new string[pawns.Length + 1];
            opts[0] = $"— pick from registry ({pawns.Length}) —";
            Array.Copy(pawns, 0, opts, 1, pawns.Length);
            int sel = EditorGUILayout.Popup("  pick from registry", 0, opts);
            if (sel > 0) { LoadForPawn(pawns[sel - 1], all); GUIUtility.ExitGUI(); }
        }

        // ── Silence inherited donor sound ──
        if (Section(ref foldSilence, "Silence inherited donor sound"))
        {
            silenceDonor = EditorGUILayout.ToggleLeft(new GUIContent("Silence the borrowed donor's inherited sounds",
                "For a custom unit that reuses a donor: the donor's own sounds (idle vocalisations, combat SFX, engine loops, etc.) " +
                "ride in on the reused animator/description and can't be nulled in data. This drops them at runtime. " +
                "Only silences the game's (Wwise) sound — your custom WAVs below still play, so use both to REPLACE the sound."), silenceDonor);        }

        // ── Idle growl ──
        if (Section(ref foldIdle, "Idle growl (occasional, while standing)"))
        {
            WavVolRow("Idle growl", idleFile, ref idlePath, ref idleVol);
            idleInterval = EditorGUILayout.Slider(new GUIContent("  avg interval (s)",
                "Average seconds between idle growls; jittered 0.6–1.4× per unit so a pack doesn't growl in unison. 0 = off."), idleInterval, 0f, 40f);
            idleGroupRadius = EditorGUILayout.Slider(new GUIContent("  one-voice radius",
                "Within this world-distance, only ONE pawn of a clustered unit growls per interval (so a 5-stack doesn't snarl in unison). 0 = every pawn growls."), idleGroupRadius, 0f, 30f);        }

        // ── Attack sound ──
        if (Section(ref foldAttack, "Attack sound (violent, on strike)"))
        {
            WavVolRow("Attack", attackFile, ref attackPath, ref attackVol, attackOffset);
            attackOffset = EditorGUILayout.Slider(new GUIContent("  start offset (s)",
                "Seconds INTO the WAV where playback starts — skip a silent or slow windup lead-in so the impact lands on the swing. " +
                "0 = play from the top. The ▶ preview honors it, so you can dial it in by ear."), attackOffset, 0f, 5f);        }

        // ── Death sound ──
        if (Section(ref foldDeath, "Death sound (rattle/scream, when a pawn dies)"))
        {
            WavVolRow("Death", deathFile, ref deathPath, ref deathVol, deathOffset);
            deathOffset = EditorGUILayout.Slider(new GUIContent("  start offset (s)",
                "Seconds INTO the WAV where playback starts (skip a lead-in). The ▶ preview honors it. A wiped stack dies " +
                "pawn-by-pawn — the runtime spaces the rattles out instead of playing five at once."), deathOffset, 0f, 5f);        }

        // ── Battle-start war cry ──
        if (Section(ref foldBattle, "Battle start (war cry, once when a battle begins)"))
        {
            WavVolRow("War cry", battleFile, ref battlePath, ref battleVol, battleOffset);
            battleOffset = EditorGUILayout.Slider(new GUIContent("  start offset (s)",
                "Seconds INTO the WAV where playback starts (skip a lead-in). The ▶ preview honors it. Plays ONCE per battle " +
                "when this unit is on either side — camera-anchored so it opens the battle audibly at any zoom."), battleOffset, 0f, 5f);        }

        // ── Movement (start / travel / stop) ──
        if (Section(ref foldMove, "Movement (start / travel / stop)"))
        {
            WavVolRow("Start (spool-up)", startFile, ref startPath, ref startVol);
            WavVolRow("Travel (loop)", loopFile, ref loopPath, ref loopVol);
            WavVolRow("Stop (spool-down)", stopFile, ref stopPath, ref stopVol);        }

        // ── Wwise engine event (game's own per-ship sound) ──
        if (Section(ref foldWwise, "Wwise engine event (game's own sound)"))
        {
            engineSound = EditorGUILayout.ToggleLeft(new GUIContent("Use a Wwise engine event (posted on move start/stop)",
                "The game's own per-ship sound. Get names from F8 ▸ Dump Sound Catalog (enc_sound_catalog.txt)."), engineSound);
            if (engineSound)
            {
                engineStart = EditorGUILayout.TextField(new GUIContent("  Start event", "e.g. Play_UNIT_Vehicles_StealthCorvette_Start"), engineStart);
                engineStop = EditorGUILayout.TextField(new GUIContent("  Stop event", "e.g. Play_UNIT_Vehicles_StealthCorvette_Stop"), engineStop);
            }        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(pawn)))
                if (GUILayout.Button("Apply", GUILayout.Height(30))) Apply(all);
            if (GUILayout.Button(new GUIContent("■ Stop", "Stop the audio preview"), GUILayout.Height(30), GUILayout.Width(90))) PreviewStop();
        }
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

        // --- units with audio ---
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Units with audio", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(150));
        var withAudio = all.Where(HasAudio).ToList();
        if (withAudio.Count == 0) EditorGUILayout.LabelField("  (none yet)", EditorStyles.miniLabel);
        foreach (var m in withAudio)
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{m.pawnDescription}  [{DescribeAudio(m)}]");
                if (GUILayout.Button("Edit", GUILayout.Width(46))) { LoadForPawn(m.pawnDescription, all); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("Clear", GUILayout.Width(52)))
                {
                    m.soundStartFile = m.soundFile = m.soundStopFile = m.soundIdleFile = m.soundAttackFile = m.soundDeathFile = m.soundBattleFile = ""; m.engineSound = false; m.engineStartEvent = m.engineStopEvent = ""; m.silenceDonorAudio = false;
                    ModelRegistry.Upsert(m); status = "Cleared audio on '" + m.pawnDescription + "'."; GUIUtility.ExitGUI();
                }
            }
        EditorGUILayout.EndScrollView();
    }

    // A collapsible section header. Uses Foldout (not BeginFoldoutHeaderGroup) so there's no strict End pairing to balance.
    static bool Section(ref bool state, string title)
    {
        EditorGUILayout.Space(4);
        state = EditorGUILayout.Foldout(state, title, true, EditorStyles.foldoutHeader);
        return state;
    }

    void LoadForPawn(string p, List<ModelDef> all)
    {
        pawn = p;
        var m = all.FirstOrDefault(x => x.pawnDescription == p);
        if (m != null)
        {
            resourceName = m.resourceName;
            startFile = m.soundStartFile; loopFile = m.soundFile; stopFile = m.soundStopFile;
            startVol = m.soundStartVolume; loopVol = m.soundVolume; stopVol = m.soundStopVolume;
            engineSound = m.engineSound; engineStart = m.engineStartEvent; engineStop = m.engineStopEvent;
            silenceDonor = m.silenceDonorAudio;
            idleFile = m.soundIdleFile; idleVol = m.soundIdleVolume; idleInterval = m.soundIdleInterval; idleGroupRadius = m.soundIdleGroupRadius;
            attackFile = m.soundAttackFile; attackVol = m.soundAttackVolume; attackOffset = m.soundAttackOffset;
            deathFile = m.soundDeathFile; deathVol = m.soundDeathVolume; deathOffset = m.soundDeathOffset;
            battleFile = m.soundBattleFile; battleVol = m.soundBattleVolume; battleOffset = m.soundBattleOffset;
        }
        else
        {
            resourceName = "Sound_" + Sanitize(p);
            startFile = loopFile = stopFile = ""; startVol = loopVol = stopVol = 1f;
            engineSound = false; engineStart = engineStop = ""; silenceDonor = false;
            idleFile = ""; idleVol = 1f; idleInterval = 11f; idleGroupRadius = 10f;
            attackFile = ""; attackVol = 1f; attackOffset = 0f;
            deathFile = ""; deathVol = 1f; deathOffset = 0f;
            battleFile = ""; battleVol = 1f; battleOffset = 0f;
        }
        idlePath = attackPath = deathPath = battlePath = "";
        startPath = loopPath = stopPath = ""; status = "";
    }

    void WavVolRow(string label, string current, ref string path, ref float vol, float previewOffset = 0f)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(label, GUILayout.Width(130));
            // Show the file this clip is actually using: the just-browsed file if any, else the current registry filename.
            string shown = !string.IsNullOrEmpty(path) ? "→ " + Path.GetFileName(path)
                         : (string.IsNullOrEmpty(current) ? "(none)" : current);
            EditorGUILayout.SelectableLabel(shown, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            string playPath = !string.IsNullOrEmpty(path) ? path : (string.IsNullOrEmpty(current) ? "" : Path.Combine(SoundsDir, current));
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(playPath) || !File.Exists(playPath)))
                if (GUILayout.Button(new GUIContent("▶", "Preview this clip at the configured volume (and start offset, if set)"), GUILayout.Width(26)))
                    PreviewPlay(playPath, vol, previewOffset);
            if (GUILayout.Button(string.IsNullOrEmpty(current) && string.IsNullOrEmpty(path) ? "Browse…" : "Replace…", GUILayout.Width(80)))
            {
                var p = EditorUtility.OpenFilePanel("Pick a WAV", "", "wav");
                if (!string.IsNullOrEmpty(p)) path = p;
            }
        }
        // Perceptual slider: hearing is logarithmic, so a LINEAR amplitude slider feels bunched near the top. Show the
        // slider as sqrt(amplitude) (≈perceived loudness) and store amplitude = slider^2 — the value the plugin feeds to
        // AudioSource.volume. The ×N label shows the real amplitude.
        float perc = Mathf.Sqrt(Mathf.Clamp01(vol));
        using (new EditorGUILayout.HorizontalScope())
        {
            perc = EditorGUILayout.Slider("    volume", perc, 0f, 1f);
            EditorGUILayout.LabelField($"×{perc * perc:0.00}", GUILayout.Width(48));
        }
        vol = perc * perc;
    }

    void Apply(List<ModelDef> all)
    {
        try
        {
            // reuse the unit's existing registry entry (a model / retexture) if it has one, so a pawn has ONE entry.
            // The resourceName fallback is ONLY safe when that entry isn't already bound to a DIFFERENT pawn: with
            // the pick-then-edit workflow (pick unit A from the dropdown, retype the pawn to unit B, Apply), the old
            // unconditional fallback matched A's FULL entry and silently retargeted its pawnDescription to B —
            // hijacking A's custom model/skin. And a fresh entry must NOT inherit A's resourceName either: Upsert
            // replaces by resourceName, so that was the same hijack via replacement (review round 2).
            string p = pawn.Trim();
            bool nameTakenByOtherPawn = !string.IsNullOrEmpty(resourceName) &&
                all.Any(m => m.resourceName == resourceName && !string.IsNullOrEmpty(m.pawnDescription) && m.pawnDescription != p);
            var def = all.FirstOrDefault(m => m.pawnDescription == p)
                   ?? (nameTakenByOtherPawn ? null : all.FirstOrDefault(m => m.resourceName == resourceName && !string.IsNullOrEmpty(resourceName)))
                   ?? new ModelDef();
            def.pawnDescription = p;
            if (string.IsNullOrEmpty(def.resourceName))
                def.resourceName = (string.IsNullOrEmpty(resourceName) || nameTakenByOtherPawn) ? "Sound_" + Sanitize(pawn) : resourceName;

            if (!CopyWav(startPath, def.resourceName + "_start", ref def.soundStartFile)) return;
            if (!CopyWav(loopPath, def.resourceName + "_loop", ref def.soundFile)) return;
            if (!CopyWav(stopPath, def.resourceName + "_stop", ref def.soundStopFile)) return;
            if (!CopyWav(idlePath, def.resourceName + "_idle", ref def.soundIdleFile)) return;
            if (!CopyWav(attackPath, def.resourceName + "_attack", ref def.soundAttackFile)) return;
            if (!CopyWav(deathPath, def.resourceName + "_death", ref def.soundDeathFile)) return;
            if (!CopyWav(battlePath, def.resourceName + "_battle", ref def.soundBattleFile)) return;
            def.soundStartVolume = startVol; def.soundVolume = loopVol; def.soundStopVolume = stopVol;
            def.soundIdleVolume = idleVol; def.soundIdleInterval = idleInterval; def.soundIdleGroupRadius = idleGroupRadius;
            def.soundAttackVolume = attackVol;
            def.soundAttackOffset = Mathf.Max(0f, attackOffset);
            def.soundDeathVolume = deathVol; def.soundDeathOffset = Mathf.Max(0f, deathOffset);
            def.soundBattleVolume = battleVol; def.soundBattleOffset = Mathf.Max(0f, battleOffset);
            def.engineSound = engineSound;
            def.engineStartEvent = engineSound ? (engineStart ?? "").Trim() : "";
            def.engineStopEvent = engineSound ? (engineStop ?? "").Trim() : "";
            def.silenceDonorAudio = silenceDonor;

            bool ok = ModelRegistry.Upsert(def);
            status = ok ? $"Saved audio for '{def.pawnDescription}' ({DescribeAudio(def)}).\nRelaunch (or reload a save) to hear it."
                        : "Registry save FAILED — see the Console.";
        }
        catch (Exception e) { status = "Failed: " + e.Message; }
    }

    // Copy a browsed WAV into enc_sounds/<baseName>.wav and set `field`; keep the existing value if none browsed.
    bool CopyWav(string src, string baseName, ref string field)
    {
        if (string.IsNullOrEmpty(src)) return true;
        if (!File.Exists(src)) { status = "WAV not found: " + src; return false; }
        Directory.CreateDirectory(SoundsDir);
        string f = Sanitize(baseName) + ".wav";
        File.Copy(src, Path.Combine(SoundsDir, f), true);
        field = f;
        return true;
    }

    static bool HasAudio(ModelDef m) => m.engineSound || m.silenceDonorAudio || !string.IsNullOrEmpty(m.soundFile) || !string.IsNullOrEmpty(m.soundStartFile) || !string.IsNullOrEmpty(m.soundStopFile) || !string.IsNullOrEmpty(m.soundIdleFile) || !string.IsNullOrEmpty(m.soundAttackFile) || !string.IsNullOrEmpty(m.soundDeathFile) || !string.IsNullOrEmpty(m.soundBattleFile);
    static string DescribeAudio(ModelDef m)
    {
        var p = new List<string>();
        if (!string.IsNullOrEmpty(m.soundStartFile)) p.Add("start");
        if (!string.IsNullOrEmpty(m.soundFile)) p.Add("loop");
        if (!string.IsNullOrEmpty(m.soundStopFile)) p.Add("stop");
        if (!string.IsNullOrEmpty(m.soundIdleFile)) p.Add("idle");
        if (!string.IsNullOrEmpty(m.soundAttackFile)) p.Add("attack");
        if (!string.IsNullOrEmpty(m.soundDeathFile)) p.Add("death");
        if (!string.IsNullOrEmpty(m.soundBattleFile)) p.Add("warcry");
        if (m.engineSound) p.Add("wwise");
        if (m.silenceDonorAudio) p.Add("silenced");
        return p.Count > 0 ? string.Join("+", p) : "none";
    }
    // ---- editor audio preview (Unity's internal UnityEditor.AudioUtil, resolved by reflection across versions) ----
    static Type _audioUtil;
    static Type AudioUtilType()
    {
        if (_audioUtil != null) return _audioUtil;
        _audioUtil = Type.GetType("UnityEditor.AudioUtil,UnityEditor");
        if (_audioUtil == null)
            _audioUtil = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetType("UnityEditor.AudioUtil"); } catch { return null; } })
                .FirstOrDefault(t => t != null);
        return _audioUtil;
    }
    const string PreviewAsset = "Assets/_ENCSoundPreview.wav";   // transient; imported so AudioUtil can preview it, deleted on close
    void OnDisable() { PreviewStop(); try { if (File.Exists(PreviewAsset)) AssetDatabase.DeleteAsset(PreviewAsset); } catch { } }

    // AudioUtil.PlayPreviewClip only plays IMPORTED AudioClips (not runtime AudioClip.Create ones), so build a temporary,
    // volume-scaled 16-bit WAV under Assets/, import it, and preview that.
    static void PreviewPlay(string srcPath, float volume, float offsetSec = 0f)
    {
        PreviewStop();
        if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return;
        if (!ParseWav(srcPath, out var f, out int ch, out int rate)) { Debug.LogWarning("[ENC Sound] preview: WAV parse failed (need PCM WAV): " + srcPath); return; }
        if (offsetSec > 0f)
        {
            // drop the skipped lead-in samples so the preview starts exactly where the runtime will
            int skip = Mathf.Min(f.Length, Mathf.RoundToInt(offsetSec * rate) * ch);
            if (skip >= f.Length) { Debug.LogWarning("[ENC Sound] preview: start offset is past the end of the clip"); return; }
            if (skip > 0) { var g = new float[f.Length - skip]; Array.Copy(f, skip, g, 0, g.Length); f = g; }
        }
        for (int i = 0; i < f.Length; i++) f[i] = Mathf.Clamp(f[i] * volume, -1f, 1f);
        try { WriteWav16(PreviewAsset, f, ch, rate); } catch (Exception e) { Debug.LogWarning("[ENC Sound] preview: write failed: " + e.Message); return; }
        AssetDatabase.ImportAsset(PreviewAsset, ImportAssetOptions.ForceSynchronousImport);
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(PreviewAsset);
        if (clip == null) { Debug.LogWarning("[ENC Sound] preview: import failed"); return; }
        var t = AudioUtilType();
        var m = t?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                  .Where(x => (x.Name == "PlayPreviewClip" || x.Name == "PlayClip") && x.GetParameters().Length >= 1 && x.GetParameters()[0].ParameterType == typeof(AudioClip))
                  .OrderByDescending(x => x.Name == "PlayPreviewClip").FirstOrDefault();
        if (m == null) { Debug.LogWarning("[ENC Sound] preview: AudioUtil play method not found"); return; }
        var ps = m.GetParameters(); var args = new object[ps.Length]; args[0] = clip;
        for (int i = 1; i < ps.Length; i++) args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
        try { m.Invoke(null, args); } catch (Exception e) { Debug.LogWarning("[ENC Sound] preview invoke failed: " + (e.InnerException ?? e).Message); }
    }
    static void PreviewStop()
    {
        var t = AudioUtilType();
        var m = t?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                  .FirstOrDefault(x => x.Name == "StopAllPreviewClips" || x.Name == "StopAllClips");
        try { m?.Invoke(null, null); } catch { }
    }

    // Parse a PCM/float WAV to interleaved float samples + format.
    static bool ParseWav(string path, out float[] f, out int ch, out int rate)
    {
        f = null; ch = 1; rate = 44100;
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return false;
            int bits = 16, fmt = 1, dataOff = -1, dataLen = 0, p = 12;
            while (p + 8 <= b.Length)
            {
                string cid = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int csz = BitConverter.ToInt32(b, p + 4);
                if (csz < 0) return false;   // corrupt chunk size -> p would stop advancing = editor hard-hang (twin of the plugin's LoadWav guard)
                if (cid == "fmt ") { fmt = BitConverter.ToInt16(b, p + 8); ch = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToInt16(b, p + 22); }
                else if (cid == "data") { dataOff = p + 8; dataLen = Math.Min(csz, b.Length - (p + 8)); break; }
                p += 8 + csz + (csz & 1);
            }
            if (dataOff < 0 || ch < 1) return false;
            int bps = bits / 8, n = dataLen / bps;
            f = new float[n];
            for (int i = 0; i < n; i++)
            {
                int o = dataOff + i * bps;
                if (fmt == 3 && bits == 32) f[i] = BitConverter.ToSingle(b, o);
                else if (bits == 16) f[i] = BitConverter.ToInt16(b, o) / 32768f;
                else if (bits == 32) f[i] = BitConverter.ToInt32(b, o) / 2147483648f;
                else if (bits == 24) { int v = b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16); f[i] = v / 8388608f; }
                else if (bits == 8) f[i] = (b[o] - 128) / 128f;
            }
            return true;
        }
        catch { return false; }
    }

    // Write interleaved float samples as a 16-bit PCM WAV.
    static void WriteWav16(string path, float[] f, int ch, int rate)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int dataBytes = f.Length * 2;
            void Tag(string s) { foreach (var c in s) w.Write((byte)c); }
            Tag("RIFF"); w.Write(36 + dataBytes); Tag("WAVE");
            Tag("fmt "); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
            Tag("data"); w.Write(dataBytes);
            foreach (var s in f) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
        }
    }

    static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s ?? "") sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        return sb.ToString();
    }
}
