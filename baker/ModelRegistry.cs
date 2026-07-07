// ModelRegistry.cs (ENC editor) — the ENC Model Factory's config store. Writes a JSON file the in-game plugin reads
// to bind each baked model onto its pawn definition at runtime. This EDITOR side uses UnityEngine.JsonUtility to WRITE
// the file (fine in the editor's Mono). IMPORTANT: the game-runtime plugin does NOT read it back with JsonUtility —
// JsonUtility silently returns an EMPTY object in the game's Mono runtime, so the plugin parses with Newtonsoft (its own
// JSON dependency; see UniversalInjectPatch.LoadRegistry). Do not "simplify" the plugin back to JsonUtility — it will
// no-op and inject nothing. Written into the game's BepInEx/config.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// One baked model. Field names MUST match the plugin's reader (JsonUtility binds by field name; extra fields ignored).
[Serializable]
public class ModelDef
{
    public string resourceName = "";
    public string pawnDescription = "";
    public string modelFile = "";
    public Vector3 rotation;            // rotation offset (deg)
    public Vector3 position;            // position offset (z = waterline)
    public float size = 5f;             // world length of the model's longest axis
    public int normalsMode = 1;         // 0 KeepModel, 1 Recalculate, 2 Faceted
    public float smoothingAngle = 20f;
    public int convertGrid = 0;         // GLB->OBJ: 0 = faithful (preserve UV seams), >0 = decimate
    public bool reuseExtracted = false; // reuse existing OBJ/albedo on re-bake (skip re-import), preserving hand-edited textures
    public bool doubleSided = false;    // add a reversed back face to every triangle — fixes single-sided/CAD meshes (e.g. a hovercraft skirt) that render invisible in-game because the engine culls backfaces
    public bool windingFix = false;     // rewind every face OUTWARD (documented CAD winding fix) so single-sided/CAD meshes render single-sided without doubling geometry — preferred over doubleSided for convex hulls (hovercraft, ships)
    public bool heightUV = false;       // override UVs with U=length, V=normalized height, so a vertical-gradient albedo maps by height (e.g. black skirt low, grey hull high) regardless of the source/CAD UVs
    public int targetTris = 24000;      // >0 = quadric-decimate the source to ~this many triangles (via Blender) before baking, to fit the engine's shared buffer. Default 24000 (halves to 12000 under double-sided — the confirmed best-looking LCAC bake, just under the ~25k per-model vertex ceiling); it's a CEILING (models already under it pass through untouched, never upscaled). 0 = off. Double-sided halves the effective target at bake time (it doubles the baked geometry).
    public string stripParts = "";      // BAKE-TIME (Blender): comma-separated object-name substrings to DELETE from YOUR model before baking — e.g. its own rotor so the donor's spinning rotor shows through, or a crew figure / weapon pod. Mirror of hideMeshes but on the source model, not the donor. Ignored by the runtime plugin. Empty = keep everything.
    public string hideMeshes = "";      // RUNTIME (not baked): comma-separated donor-FRAGMENT name substrings the plugin hides on this unit — e.g. hide a fragment-based extra. NOTE: a donor's animated skinned sub-parts (a helicopter rotor, spinning wheels) are encoded at pawn-spawn and CANNOT be hidden this late — choose a donor without such parts. Find fragment names in the BepInEx log: "[Uni] <name> donor fragment[i] mesh='...'".
    public int[] skel = new int[4];     // baked skeleton Amplitude guid {a,b,c,d}
    public int[] atlas = new int[4];    // baked atlas Amplitude guid {a,b,c,d}
    public int[] clip = new int[4];     // ANIMATED only: baked ClipCollection Amplitude guid {a,b,c,d}; static models leave it {0,0,0,0}
    public bool animated = false;       // true = baked from the model's OWN armature + clip (animated path), not the procedural vehicle rig
    public string animClip = "";        // ANIMATED only: name of the clip to bake when the model has several (e.g. "hover"); empty = the assigned/first action
    public string animateBones = "";    // ANIMATED only: comma-separated bone-name prefixes to keep animation on (e.g. "prop,rotor"); empty = keep the whole clip
    public bool respawnAfterLoad = false; // RUNTIME (not baked): fix the save-load first-instance rotor race. The engine draws the FIRST custom pawn that borrows a donor's animated sub-part (a helicopter rotor) built during a save-load ~1 low; anything rebuilt AFTER load is correct. Tick this and the plugin re-runs the game's own PresentationUnit.UpdatePawns (release+re-instantiate) on this model's units ~3s post-load, clearing it. Set ONLY for borrowed-rotor models; a brief one-time flicker as those units rebuild.
}

[Serializable]
class ModelDefList { public List<ModelDef> models = new List<ModelDef>(); }

public static class ModelRegistry
{
    // Last-resort fallback if Steam auto-detection finds nothing (e.g. non-default install Steam can't report).
    const string FallbackConfigDir = @"C:\Program Files (x86)\Steam\steamapps\common\Humankind\BepInEx\config";

    // Where the plugin reads the registry from: an explicit user override wins; otherwise auto-detect the Humankind
    // install via Steam's library config; otherwise the fallback. The manual override is the escape hatch — the
    // Factory window exposes it so an adopter with a weird layout can point it by hand.
    public static string ConfigDir
    {
        get
        {
            var over = EditorPrefs.GetString("ENC.bepinexConfig", "");
            if (!string.IsNullOrWhiteSpace(over)) return over;
            return AutoDetectConfigDir() ?? FallbackConfigDir;
        }
    }
    public static string ConfigDirOverride
    {
        get => EditorPrefs.GetString("ENC.bepinexConfig", "");
        set => EditorPrefs.SetString("ENC.bepinexConfig", value ?? "");
    }
    public static string RegistryPath => Path.Combine(ConfigDir, "enc_models.json");

    // ---- zero-config game-path discovery (mirrors the Blender/glbconv self-location) ----

    // First Humankind install found across all Steam libraries -> its BepInEx/config. Returns null if not found.
    public static string AutoDetectConfigDir()
    {
        try
        {
            foreach (var hk in HumankindInstallDirs())
                return Path.Combine(hk, "BepInEx", "config");   // config may not exist until BepInEx runs once
        }
        catch { }
        return null;
    }

    static IEnumerable<string> HumankindInstallDirs()
    {
        foreach (var lib in SteamLibraries())
        {
            string hk = Path.Combine(lib, "steamapps", "common", "Humankind");
            if (Directory.Exists(hk)) yield return hk;
        }
    }

    static IEnumerable<string> SteamLibraries()
    {
        string steam = SteamPath();
        if (string.IsNullOrEmpty(steam)) yield break;
        yield return steam;   // the base Steam dir is itself a library
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    static string SteamPath()
    {
        // Probe the standard Steam install locations. Games on OTHER drives are still found — libraryfolders.vdf
        // (inside this base install) lists every library. Steam installed to a truly custom folder is the one case
        // this misses; that's what the Factory window's manual Override is for. (Deliberately no registry lookup:
        // Microsoft.Win32.Registry isn't referenced under Unity's default .NET Standard API level.)
        foreach (var c in new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        })
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
        return null;
    }

    // Set when the last Load() found a registry file it couldn't parse. Save() refuses to run while it's
    // set, so a corrupt / half-edited registry is never silently overwritten with a fresh (empty) list —
    // which would wipe every baked model's settings.
    static bool lastLoadCorrupt;

    // A VERSIONED shadow copy of the registry, written into the mod repo on every Save (Assets/Databases is
    // git-tracked). It survives a game reinstall / Steam "verify files" wiping BepInEx/config, gives version
    // history in git, and Load() auto-restores from it if the game registry ever goes missing.
    public static string ProjectBackupPath => Path.Combine(Application.dataPath, "Databases", "enc_models.backup.json");

    public static List<ModelDef> Load()
    {
        try
        {
            if (!File.Exists(RegistryPath))
            {
                lastLoadCorrupt = false;
                // Game registry gone (fresh/reinstalled game, verified files). Recover from the versioned project
                // backup and write it back to the game target, so nothing is lost.
                if (File.Exists(ProjectBackupPath))
                {
                    var backupJson = File.ReadAllText(ProjectBackupPath);
                    var b = JsonUtility.FromJson<ModelDefList>(backupJson);
                    if (b?.models != null && b.models.Count > 0)
                    {
                        try { Directory.CreateDirectory(ConfigDir); File.WriteAllText(RegistryPath, backupJson); } catch { }
                        Debug.Log($"[Factory] game registry was missing — restored {b.models.Count} model(s) from the project backup ({ProjectBackupPath}).");
                        return b.models;
                    }
                }
                return new List<ModelDef>();
            }
            var data = JsonUtility.FromJson<ModelDefList>(File.ReadAllText(RegistryPath));
            lastLoadCorrupt = false;
            return data?.models ?? new List<ModelDef>();
        }
        catch (Exception e)
        {
            // The file exists but won't parse (a hand-edit typo, a half-written file). Preserve it and flag
            // it so Save() won't clobber it and lose everything.
            lastLoadCorrupt = true;
            try { File.Copy(RegistryPath, RegistryPath + ".corrupt.json", true); } catch { }
            Debug.LogError($"[Factory] registry '{RegistryPath}' is unreadable ({e.Message}) — backed up to " +
                           $"'{Path.GetFileName(RegistryPath)}.corrupt.json'. Fix or delete it (then press Refresh); " +
                           $"a versioned copy is also at '{ProjectBackupPath}'. Baking won't save until then, to avoid wiping your models.");
            return new List<ModelDef>();
        }
    }

    // Returns true if the registry was written. False = nothing was saved (corrupt-guard tripped, or the atomic write
    // hit a transient lock) — the caller should surface that instead of assuming success.
    public static bool Save(List<ModelDef> models)
    {
        if (lastLoadCorrupt)
        {
            Debug.LogError("[Factory] not saving: the existing registry was unreadable (see the .corrupt.json backup). " +
                           "Fix or delete it and press Refresh first — refusing to overwrite it and lose your models.");
            return false;
        }
        var json = JsonUtility.ToJson(new ModelDefList { models = models }, true);
        // 1) Atomic write to the live game target (what the plugin reads): fill a temp file, then swap it in, so an
        //    interrupted or locked write can never leave a truncated registry. GUARDED — File.Replace/Move throws on a
        //    transient lock (AV, search indexer, the running game holding the file). Without this, a bake that succeeds
        //    then fails to save would leak a raw editor exception AND leave the user thinking it saved.
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var tmp = RegistryPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(RegistryPath)) File.Replace(tmp, RegistryPath, null);
            else File.Move(tmp, RegistryPath);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Factory] registry write FAILED — the model baked but its entry was NOT saved to " +
                           $"'{RegistryPath}' ({e.Message}). Close whatever's locking it (AV, search indexer, the running " +
                           $"game) and re-bake; the previous registry and the versioned copy at '{ProjectBackupPath}' are intact.");
            return false;
        }
        // 2) Versioned shadow copy inside the mod repo (git-tracked; survives a game reinstall). Best-effort.
        try { File.WriteAllText(ProjectBackupPath, json); } catch (Exception e) { Debug.LogWarning("[Factory] project backup write failed: " + e.Message); }
        AssetDatabase.Refresh();
        return true;
    }

    public static bool Upsert(ModelDef def)
    {
        var list = Load();
        list.RemoveAll(m => m.resourceName == def.resourceName);
        list.Add(def);
        return Save(list);
    }

    // Remove a model from the registry by resource name. Returns true if something was removed. The baked skeleton/atlas
    // assets are left in the project (harmless); this just stops the plugin injecting that model.
    public static bool Remove(string resourceName)
    {
        var list = Load();
        int before = list.Count;
        list.RemoveAll(m => m.resourceName == resourceName);
        if (list.Count == before) return false;
        return Save(list);
    }

    public static int[] ParseGuid(string csv)
    {
        var p = (csv ?? "").Split(',');
        int g(int i) => p.Length > i && int.TryParse(p[i], out var r) ? r : 0;
        return new[] { g(0), g(1), g(2), g(3) };
    }
}
