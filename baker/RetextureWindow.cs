// RetextureWindow.cs — a SEPARATE editor window (Tools ▸ ENC ▸ Unit Retexture) for reskinning an EXISTING unit at
// runtime WITHOUT baking a custom model. Pick the unit's pawn, DOWNLOAD its skin (from the in-game atlas dump) to paint,
// then REPLACE it with your PNG (or just grey it). The runtime plugin hot-loads the result onto an ISOLATED clone of the
// unit's output layer, so the emblematic original is untouched and the vanilla mesh is kept. It writes texture-only
// entries (textureFile / desaturate) into the SAME registry the Factory uses — see ModelDef in ModelRegistry.cs.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class RetextureWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Unit Retexture")]
    static void Open() => GetWindow<RetextureWindow>("Unit Retexture");

    string pawn = "Era6_Common_StealthCorvettes_01";   // the pawn descriptor to retexture (a unique substring the plugin matches)
    string resourceName = "";                          // registry id for this override entry
    string pngPath = "";                               // the painted PNG to inject
    float brightness = 1f;                             // gamma lift, 1 = unchanged (>1 lighter, <1 darker) — applied FIRST
    float desaturate = 0f;                             // 0 = keep colours, 1 = full grey
    float tintR = 0f, tintG = 0f, tintB = 0f;          // per-channel colour offset, -255..+255
    // Sound config (engine move sound + custom WAVs) lives EXCLUSIVELY in the dedicated Unit Sound window (SoundWindow.cs),
    // which owns the same registry fields plus per-clip volumes and a ▶ preview — this window is skins only. Existing sound
    // on an entry is preserved through Apply (it loads the entry first and never touches the sound fields).
    Vector2 scroll;
    string status = "";
    string editedEntry = "";                           // which entry the form was last LOADED from (Edit button) — Apply onto a different existing entry asks first

    // --- live skin preview (mirrors the plugin's AdjustSkin pixel math so what you see == what gets injected) ---
    Texture2D previewTex;                              // the composited preview drawn in the window
    Color32[] srcPixels;                              // downscaled source-atlas pixels, cached per source file
    int srcW, srcH;                                  // preview dimensions
    string srcSig = null;                            // signature of the loaded source (path + timestamp)
    string previewSig = null;                        // signature of the built preview (source + adjustments)

    // The game's BepInEx/config (auto-detected by ModelRegistry, same folder the plugin reads).
    static string SkinsDir => Path.Combine(ModelRegistry.ConfigDir, "enc_skins");
    static string DumpDir  => Path.Combine(ModelRegistry.ConfigDir, "enc_atlas_dump");

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Reskin an EXISTING unit at runtime — no bake.\n" +
            "1) Pick the pawn (use the _Common_..._01 copy, NOT the emblematic original).\n" +
            "2) Download its skin: dump atlases in-game first (F8 window ▸ Dump Atlases), then paint the unit's .png.\n" +
            "3) Replace with your PNG and/or adjust it (Desaturate + R/G/B ±255). Relaunch/reload the game to see it — the " +
            "plugin injects onto an isolated copy of the layer, so the original stays as-is.", MessageType.Info);

        var existing = ModelRegistry.Load();

        // --- 1) pawn ---
        EditorGUILayout.LabelField("1 · Pawn", EditorStyles.boldLabel);
        pawn = EditorGUILayout.TextField(new GUIContent("Pawn description",
            "The unit's pawn descriptor — a unique substring the plugin matches, e.g. Era6_Common_StealthCorvettes_01."), pawn);
        var pawns = existing.Select(m => m.pawnDescription).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToArray();
        if (pawns.Length > 0)
        {
            // A visible placeholder as item 0 so the dropdown reads as a populated picker (a Popup at index -1 shows blank).
            var opts = new string[pawns.Length + 1];
            opts[0] = $"— pick from registry ({pawns.Length}) —";
            System.Array.Copy(pawns, 0, opts, 1, pawns.Length);
            int sel = EditorGUILayout.Popup("  pick from registry", 0, opts);
            if (sel > 0) { pawn = pawns[sel - 1]; resourceName = ""; }
        }
        else
            EditorGUILayout.LabelField("  pick from registry", "(nothing in the registry yet — type the descriptor)", EditorStyles.miniLabel);
        if (string.IsNullOrEmpty(resourceName) && !string.IsNullOrEmpty(pawn)) resourceName = "Retex_" + Sanitize(pawn);
        resourceName = EditorGUILayout.TextField(new GUIContent("Entry name", "Registry id for this override (any unique name)."), resourceName);

        EditorGUILayout.Space();
        // --- 2) download ---
        EditorGUILayout.LabelField("2 · Download UV map (skin)", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open atlas-dump folder", GUILayout.Height(22)))
            {
                Directory.CreateDirectory(DumpDir);
                EditorUtility.RevealInFinder(DumpDir + Path.DirectorySeparatorChar);
            }
            EditorGUILayout.LabelField("← dump in-game first (F8 ▸ Dump Atlases), then paint the unit's .png", EditorStyles.miniLabel);
        }

        EditorGUILayout.Space();
        // --- 3) replace ---
        EditorGUILayout.LabelField("3 · Replace / adjust skin", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            pngPath = EditorGUILayout.TextField(new GUIContent("Replacement PNG (optional)",
                "A painted skin PNG. Leave empty to adjust the unit's OWN atlas — or, when editing an entry, to keep its current skin and only change the adjustments below."), pngPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var p = EditorUtility.OpenFilePanel("Pick the painted skin PNG", "", "png");
                if (!string.IsNullOrEmpty(p)) pngPath = p;
            }
        }
        // BROKEN-LINK REPORT: the loaded entry references a skin PNG that isn't in enc_skins/ (deleted/renamed) and no new
        // PNG is queued — warn so it's obvious the skin won't load (the unit would fall back to its own atlas).
        if (string.IsNullOrEmpty(pngPath))
        {
            var curEntry = existing.FirstOrDefault(m => m.pawnDescription == pawn && !string.IsNullOrEmpty(m.textureFile));
            if (curEntry != null && !File.Exists(Path.Combine(SkinsDir, curEntry.textureFile)))
                EditorGUILayout.HelpBox("Skin PNG missing from enc_skins/: " + curEntry.textureFile +
                    "\nBrowse a replacement, or the unit falls back to its own atlas.", MessageType.Warning);
        }
        EditorGUILayout.LabelField("Adjustments — applied on top of the skin above (or the own atlas):", EditorStyles.miniLabel);
        brightness = EditorGUILayout.Slider(new GUIContent("Brightness (gamma)",
            "Gamma lift, applied FIRST: 1 = unchanged, >1 lighter, <1 darker. Multiplies along a curve that lifts dark/mid tones most " +
            "while keeping black and white pinned — the right knob for 'this skin reads too dark in-game' (the RGB sliders are ADDITIVE: " +
            "they shift every pixel equally and wash out before they meaningfully lighten a dark skin)."), brightness, 0.4f, 2.5f);
        desaturate = EditorGUILayout.Slider(new GUIContent("Desaturate",
            "0 = keep colours, 1 = full grey (pull each pixel toward its brightness)."), desaturate, 0f, 1f);
        tintR = EditorGUILayout.Slider(new GUIContent("Red  ±255",
            "Add (or subtract) red. Equal negatives on all three = darken; equal positives = brighten."), tintR, -255f, 255f);
        tintG = EditorGUILayout.Slider(new GUIContent("Green ±255", "Add (or subtract) green."), tintG, -255f, 255f);
        tintB = EditorGUILayout.Slider(new GUIContent("Blue ±255", "Add (or subtract) blue."), tintB, -255f, 255f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Reset adjustments", GUILayout.Width(150))) { brightness = 1f; desaturate = 0f; tintR = tintG = tintB = 0f; }
            GUILayout.FlexibleSpace();
        }

        DrawSkinPreview(existing);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Engine/custom move sounds live in the dedicated Unit Sound window (with per-clip volume + ▶ preview).", EditorStyles.miniLabel);

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(pawn) || string.IsNullOrEmpty(resourceName)))
            if (GUILayout.Button("Apply", GUILayout.Height(30)))
                Apply();

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

        EditorGUILayout.Space();
        // --- existing overrides ---
        EditorGUILayout.LabelField("Texture-only overrides", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(150));
        var overrides = existing.Where(x => x.desaturate > 0f || x.tintR != 0f || x.tintG != 0f || x.tintB != 0f || (x.brightness > 0f && Mathf.Abs(x.brightness - 1f) > 0.001f) || !string.IsNullOrEmpty(x.textureFile) || x.engineSound || !string.IsNullOrEmpty(x.soundFile) || !string.IsNullOrEmpty(x.soundStartFile) || !string.IsNullOrEmpty(x.soundStopFile)).ToList();
        if (overrides.Count == 0) EditorGUILayout.LabelField("  (none yet)", EditorStyles.miniLabel);
        foreach (var m in overrides)
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{m.resourceName}  → {m.pawnDescription}  [{Describe(m)}]");
                if (GUILayout.Button("Edit", GUILayout.Width(46)))
                {
                    pawn = m.pawnDescription; resourceName = m.resourceName; editedEntry = m.resourceName;
                    desaturate = m.desaturate; tintR = m.tintR; tintG = m.tintG; tintB = m.tintB;
                    brightness = m.brightness <= 0f ? 1f : m.brightness;   // entries saved before the knob existed carry 0 through JsonUtility — treat as unchanged
                    pngPath = "";   // sound stays put — it's owned by the Unit Sound window and untouched by Apply here
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Remove", GUILayout.Width(64)))
                {
                    // This list matches ANY entry carrying texture/tint/sound settings — including FULL MODEL entries
                    // (Unit Sound deliberately writes audio onto a pawn's model entry, e.g. the howitzer). Deleting
                    // such an entry here would wipe the whole baked-model registration, not just the override
                    // (review 2026-07-19). Model entries get their overrides CLEARED instead; only pure
                    // texture/sound-only entries are actually removed — and both ask first.
                    bool isModelEntry = m.animated || !string.IsNullOrEmpty(m.modelFile) ||
                        (m.skel != null && m.skel.Length == 4 && !(m.skel[0] == 0 && m.skel[1] == 0 && m.skel[2] == 0 && m.skel[3] == 0));
                    if (isModelEntry)
                    {
                        if (EditorUtility.DisplayDialog("Clear overrides",
                            $"'{m.resourceName}' is a FULL MODEL entry — removing it would deregister the baked model itself.\n\n" +
                            "Clear only its texture/tint/sound overrides and keep the model?", "Clear overrides", "Cancel"))
                        {
                            var def = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == m.resourceName);
                            if (def != null)
                            {
                                def.brightness = 1f; def.desaturate = 0f; def.tintR = 0f; def.tintG = 0f; def.tintB = 0f; def.textureFile = "";
                                def.engineSound = false; def.engineStartEvent = ""; def.engineStopEvent = "";
                                def.soundFile = ""; def.soundStartFile = ""; def.soundStopFile = "";
                                def.soundVolume = 1f; def.soundStartVolume = 1f; def.soundStopVolume = 1f;
                                status = ModelRegistry.Upsert(def)
                                    ? "Cleared the overrides on '" + m.resourceName + "' (model entry kept)."
                                    : "Clear FAILED — see the Console.";
                            }
                        }
                    }
                    else if (EditorUtility.DisplayDialog("Remove override",
                        $"Remove '{m.resourceName}' (→ {m.pawnDescription}) from the registry?", "Remove", "Cancel"))
                    {
                        status = ModelRegistry.Remove(m.resourceName)
                            ? "Removed '" + m.resourceName + "'."
                            : "Remove FAILED — see the Console.";
                    }
                    GUIUtility.ExitGUI();
                }
            }
        EditorGUILayout.EndScrollView();
    }

    // Live preview of the skin that WILL be injected: the browsed Replacement PNG (or, when none is browsed, this
    // entry's already-saved skin) with the Desaturate + R/G/B adjustments applied by the SAME math the plugin runs
    // (AdjustSkin). Units are GPU crowd-rendered with no editor-side GameObject, so this previews the atlas itself —
    // not a posed 3D mech. Rebuilds only when the source file or an adjustment changes (cached), so slider drags are smooth.
    void DrawSkinPreview(System.Collections.Generic.IEnumerable<ModelDef> existing)
    {
        string src = null;
        if (!string.IsNullOrEmpty(pngPath) && File.Exists(pngPath)) src = pngPath;
        else
        {
            var def = existing.FirstOrDefault(m => m.resourceName == resourceName);
            if (def != null && !string.IsNullOrEmpty(def.textureFile))
            {
                var p = Path.Combine(SkinsDir, def.textureFile);
                if (File.Exists(p)) src = p;
            }
        }

        EditorGUILayout.LabelField("Preview — skin atlas (live, = what gets injected)", EditorStyles.boldLabel);
        if (src == null)
        {
            EditorGUILayout.HelpBox("Browse a Replacement PNG (or Edit a saved entry) to preview here. The unit's OWN atlas " +
                "can only be previewed after an in-game atlas dump — units are GPU-rendered, so there's no 3D mech view in the editor.", MessageType.None);
            return;
        }

        EnsureSource(src);
        if (srcPixels == null) { EditorGUILayout.HelpBox("Could not read '" + Path.GetFileName(src) + "' as an image.", MessageType.Warning); return; }

        string sig = srcSig + "|" + brightness + "|" + desaturate + "|" + tintR + "|" + tintG + "|" + tintB;
        if (sig != previewSig || previewTex == null) { BuildPreview(); previewSig = sig; }

        float ar = (float)srcH / Mathf.Max(1, srcW);
        float boxW = Mathf.Min(EditorGUIUtility.currentViewWidth - 30f, 320f);
        var r = GUILayoutUtility.GetRect(boxW, boxW * ar, GUILayout.ExpandWidth(false));
        EditorGUI.DrawTextureTransparent(r, previewTex, ScaleMode.ScaleToFit);   // checkerboard shows the atlas's transparent gaps
        EditorGUILayout.LabelField($"  {Path.GetFileName(src)} — adjustments applied live (Desaturate/RGB)", EditorStyles.miniLabel);
    }

    // Load a source image and cache a downscaled Color32 copy (<=256px) so re-applying adjustments per slider-drag is cheap.
    void EnsureSource(string path)
    {
        string sig = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        if (sig == srcSig && srcPixels != null) return;
        try
        {
            var full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!full.LoadImage(File.ReadAllBytes(path))) { DestroyImmediate(full); srcPixels = null; srcSig = sig; return; }
            const int MAX = 256;
            int fw = full.width, fh = full.height;
            int sw = Mathf.Min(MAX, fw), sh = Mathf.Max(1, Mathf.RoundToInt(sw * (float)fh / fw));
            var fpx = full.GetPixels32();
            var dst = new Color32[sw * sh];
            for (int y = 0; y < sh; y++)
            {
                int sy = y * fh / sh;
                for (int x = 0; x < sw; x++) dst[y * sw + x] = fpx[sy * fw + (x * fw / sw)];
            }
            DestroyImmediate(full);
            srcPixels = dst; srcW = sw; srcH = sh; srcSig = sig; previewSig = null;
        }
        catch { srcPixels = null; srcSig = sig; }
    }

    // Rebuild previewTex from the cached source pixels — EXACTLY the plugin's AdjustSkin (luminance pull + RGB offset).
    void BuildPreview()
    {
        if (previewTex == null || previewTex.width != srcW || previewTex.height != srcH)
        {
            if (previewTex != null) DestroyImmediate(previewTex);
            previewTex = new Texture2D(srcW, srcH, TextureFormat.RGBA32, false);
        }
        var px = (Color32[])srcPixels.Clone();
        float s = Mathf.Clamp01(desaturate);
        // gamma FIRST, then desaturate, then tint — the plugin's AdjustSkin order, mirrored exactly
        byte[] lut = null;
        if (Mathf.Abs(brightness - 1f) > 0.001f)
        {
            float inv = 1f / Mathf.Clamp(brightness, 0.2f, 4f);
            lut = new byte[256];
            for (int v = 0; v < 256; v++) lut[v] = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * Mathf.Pow(v / 255f, inv)), 0, 255);
        }
        for (int i = 0; i < px.Length; i++)
        {
            if (lut != null) { px[i].r = lut[px[i].r]; px[i].g = lut[px[i].g]; px[i].b = lut[px[i].b]; }
            float lum = px[i].r * 0.299f + px[i].g * 0.587f + px[i].b * 0.114f;
            px[i].r = (byte)Mathf.Clamp((px[i].r + (lum - px[i].r) * s) + tintR, 0, 255);
            px[i].g = (byte)Mathf.Clamp((px[i].g + (lum - px[i].g) * s) + tintG, 0, 255);
            px[i].b = (byte)Mathf.Clamp((px[i].b + (lum - px[i].b) * s) + tintB, 0, 255);
        }
        previewTex.SetPixels32(px); previewTex.Apply();
    }

    void OnDisable()
    {
        if (previewTex != null) DestroyImmediate(previewTex);
        previewTex = null; srcPixels = null; srcSig = previewSig = null;
    }

    void Apply()
    {
        try
        {
            var def = ModelRegistry.Load().FirstOrDefault(m => m.resourceName == resourceName) ?? new ModelDef();
            // Applying onto an EXISTING entry the form was never loaded from would overwrite its adjust/engine-sound
            // settings with the form's (possibly default) values — e.g. typing a name and hitting Apply silently
            // flipped an entry's engineSound off (review 2026-07-19). Ask first; Edit pre-loads and skips the dialog.
            if (!string.IsNullOrEmpty(def.resourceName) && editedEntry != resourceName &&
                !EditorUtility.DisplayDialog("Overwrite existing entry?",
                    $"'{resourceName}' already exists ({Describe(def)}).\n\nApply will overwrite its adjustment/engine-sound " +
                    "settings with this form's values (un-browsed files are kept). Press Edit on the entry below to load " +
                    "its current settings first.", "Overwrite", "Cancel"))
            { status = "Cancelled — press Edit on '" + resourceName + "' to load its settings first."; return; }
            def.resourceName = resourceName.Trim();
            def.pawnDescription = pawn.Trim();
            def.brightness = Mathf.Clamp(brightness, 0.2f, 4f);
            def.desaturate = Mathf.Clamp01(desaturate);
            def.tintR = Mathf.Clamp(tintR, -255f, 255f);
            def.tintG = Mathf.Clamp(tintG, -255f, 255f);
            def.tintB = Mathf.Clamp(tintB, -255f, 255f);
            // Sound fields (engineSound / WAVs / volumes) are NOT touched here — they're owned by the Unit Sound window.
            // def was loaded from the existing entry above, so whatever sound it already carries is preserved on save.
            if (!string.IsNullOrEmpty(pngPath))   // a new PNG was picked -> copy it in and use it as the skin
            {
                if (!File.Exists(pngPath)) { status = "PNG not found: " + pngPath; return; }
                Directory.CreateDirectory(SkinsDir);
                string file = Sanitize(resourceName) + ".png";
                File.Copy(pngPath, Path.Combine(SkinsDir, file), true);   // into the game's config/enc_skins the plugin reads
                def.textureFile = file;
            }
            // else: keep def.textureFile as-is (an existing entry's PNG, or "" to adjust the unit's own atlas).

            bool hasSkin = !string.IsNullOrEmpty(def.textureFile);
            bool hasAdjust = def.desaturate > 0f || def.tintR != 0f || def.tintG != 0f || def.tintB != 0f || Mathf.Abs(def.brightness - 1f) > 0.001f;
            bool hasSound = def.engineSound || !string.IsNullOrEmpty(def.soundFile) || !string.IsNullOrEmpty(def.soundStartFile) || !string.IsNullOrEmpty(def.soundStopFile);
            if (!hasSkin && !hasAdjust && !hasSound)   // truly-empty entry (no skin here, no sound from the Sound window)
            {
                status = "Nothing to apply — browse a Replacement PNG or set a Desaturate/RGB adjustment.";
                return;
            }
            bool ok = ModelRegistry.Upsert(def);
            if (ok) editedEntry = def.resourceName;   // the form now matches the entry — no dialog on a re-Apply
            status = ok
                ? $"Saved '{def.resourceName}' → {def.pawnDescription}  ({Describe(def)}).\nRelaunch the game (or reload a save) to see it."
                : "Registry save FAILED — see the Console.";
        }
        catch (Exception e) { status = "Failed: " + e.Message; }
    }

    static string Describe(ModelDef m)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(m.textureFile)) parts.Add("skin " + m.textureFile);
        if (m.brightness > 0f && Mathf.Abs(m.brightness - 1f) > 0.001f) parts.Add($"gamma {m.brightness:0.00}");
        if (m.desaturate > 0f) parts.Add($"desat {m.desaturate:0.00}");
        if (m.tintR != 0f || m.tintG != 0f || m.tintB != 0f) parts.Add($"rgb {m.tintR:+0;-0;0}/{m.tintG:+0;-0;0}/{m.tintB:+0;-0;0}");
        if (m.engineSound) parts.Add("engine");
        if (!string.IsNullOrEmpty(m.soundFile)) parts.Add("wav " + m.soundFile);
        if (!string.IsNullOrEmpty(m.soundStartFile)) parts.Add("wav-start");
        if (!string.IsNullOrEmpty(m.soundStopFile)) parts.Add("wav-stop");
        return parts.Count > 0 ? string.Join(", ", parts) : "no change";
    }

    static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s ?? "") sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        return sb.ToString();
    }
}
