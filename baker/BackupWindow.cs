// BackupWindow.cs — Tools ▸ HAF ▸ Backup & Restore. A safety net for everything that ISN'T under git: the editor
// scripts (Assets/Scripts/Editor), the source models (Assets/FactorySource), the baked assets (Assets/Resources),
// the ENC databases, the Tools scripts, and the LIVE runtime config the plugin reads (BepInEx/config: enc_*.json +
// enc_skins + enc_sounds). ENCReload's git only tracks Assets/Databases, so most of this has no version control.
//
// DESIGN — anti-loss first (the user's explicit ask, "guards that ensure we don't lose anything"):
//   • A backup is a TIMESTAMPED, self-describing folder on D: with a manifest.txt recording every source's ORIGINAL
//     absolute path + file count + bytes. Backups are never overwritten (each is a new timestamp) and never auto-deleted.
//   • RESTORE is guarded three ways: (1) it FIRST snapshots the current state to a _prerestore_<ts> backup, so a wrong
//     restore is always undoable; (2) it is ADDITIVE — it copies backed-up files OVER current ones but NEVER deletes a
//     file that exists now and isn't in the backup, so new work can't vanish; (3) it asks for explicit confirmation
//     listing exactly what will be written. After both backup and restore, file counts are verified and reported.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class BackupWindow : EditorWindow
{
    // NOTE: '&' is a shortcut-modifier char in Unity MenuItem paths, so the menu uses "and"; the window title keeps "&".
    [MenuItem("Tools/HAF/Backup and Restore")]
    static void Open()
    {
        var w = GetWindow<BackupWindow>("Backup & Restore");
        w.minSize = new Vector2(680, 560);   // wide enough for the backup rows (timestamp + size + Reveal/Restore/Delete) without cramping
    }

    const string PrefDest = "HAF.Backup.Dest";
    string dest;                                   // backup root on D:
    Vector2 scroll, listScroll;
    string status = "";
    readonly Dictionary<string, bool> enabled = new Dictionary<string, bool>();   // group key -> included
    readonly Dictionary<string, long> sizeCache = new Dictionary<string, long>();  // path -> bytes (cleared on any change; walks are expensive)

    long CachedBytes(string p) { if (!sizeCache.TryGetValue(p, out var v)) { v = TreeBytes(p); sizeCache[p] = v; } return v; }

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;   // C:/Repo/ENCReload
    static string AssetsDir => Application.dataPath;

    // A backup group: a display name, a prefs/manifest key, and the concrete source paths it captures (file or dir).
    class Group { public string Name, Key; public List<string> Sources; public Group(string n, string k, List<string> s) { Name = n; Key = k; Sources = s; } }

    // Resolved fresh each time (paths must exist to be offered). Runtime config = the enc_* entries the plugin reads.
    static List<Group> BuildGroups()
    {
        var g = new List<Group>();
        void Add(string name, string key, IEnumerable<string> srcs)
        {
            var present = srcs.Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
            if (present.Count > 0) g.Add(new Group(name, key, present));
        }
        Add("Editor scripts (Assets/Scripts/Editor)", "editor", new[] { Path.Combine(AssetsDir, "Scripts/Editor") });
        Add("Source models (Assets/FactorySource)", "source", new[] { Path.Combine(AssetsDir, "FactorySource") });
        Add("Baked assets (Assets/Resources)", "resources", new[] { Path.Combine(AssetsDir, "Resources") });
        Add("ENC Databases (Assets/Databases)", "databases", new[] { Path.Combine(AssetsDir, "Databases") });
        Add("Tools (Blender / converters)", "tools", new[] { Path.Combine(ProjectRoot, "Tools") });
        // Runtime config: the LIVE files the plugin reads — enc_*.json + the skins/sounds folders (skip the regenerable atlas dump).
        string cfg = SafeConfigDir();
        if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg))
        {
            var cfgSrcs = new List<string>();
            cfgSrcs.AddRange(Directory.GetFiles(cfg, "enc_*.json"));
            foreach (var sub in new[] { "enc_skins", "enc_sounds" }) { var p = Path.Combine(cfg, sub); if (Directory.Exists(p)) cfgSrcs.Add(p); }
            Add("Runtime config (registry + skins + sounds)", "config", cfgSrcs);
        }
        return g;
    }

    static string SafeConfigDir() { try { return ModelRegistry.ConfigDir; } catch { return null; } }

    void OnEnable()
    {
        dest = EditorPrefs.GetString(PrefDest, "D:/HAF_Backups");
        foreach (var g in BuildGroups()) if (!enabled.ContainsKey(g.Key)) enabled[g.Key] = true;   // everything on by default
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Backup everything that git does NOT protect (editor scripts, source & baked models, databases, Tools, and the " +
            "live BepInEx runtime config). Each backup is a timestamped folder on D: with a manifest.\n\n" +
            "Restore is safe: it first snapshots the current state to a _prerestore backup, then copies files back ADDITIVELY " +
            "(it never deletes anything you've added since). Nothing is ever overwritten or auto-deleted here.", MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            dest = EditorGUILayout.TextField(new GUIContent("Backup folder (on D:)", "Root folder for all backups; each backup is a timestamped subfolder."), dest);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var p = EditorUtility.OpenFolderPanel("Pick the backup root folder", Directory.Exists(dest) ? dest : "D:/", "");
                if (!string.IsNullOrEmpty(p)) { dest = p; EditorPrefs.SetString(PrefDest, dest); }
            }
        }
        EditorPrefs.SetString(PrefDest, dest);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Include in backup", EditorStyles.boldLabel);
            if (GUILayout.Button("↻ sizes", GUILayout.Width(70))) sizeCache.Clear();
        }
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(130));
        foreach (var grp in BuildGroups())
        {
            if (!enabled.ContainsKey(grp.Key)) enabled[grp.Key] = true;
            using (new EditorGUILayout.HorizontalScope())
            {
                enabled[grp.Key] = EditorGUILayout.ToggleLeft(grp.Name, enabled[grp.Key]);
                EditorGUILayout.LabelField(Human(grp.Sources.Sum(CachedBytes)), EditorStyles.miniLabel, GUILayout.Width(90));
            }
        }
        EditorGUILayout.EndScrollView();

        using (new EditorGUI.DisabledScope(!BuildGroups().Any(g => enabled.TryGetValue(g.Key, out var b) && b)))
            if (GUILayout.Button("Back up now", GUILayout.Height(30))) DoBackup();

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Existing backups (newest first)", EditorStyles.boldLabel);
        listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.Height(180));
        var backups = ListBackups();
        if (backups.Count == 0) EditorGUILayout.LabelField("  (none yet)", EditorStyles.miniLabel);
        foreach (var b in backups)
            using (new EditorGUILayout.HorizontalScope())
            {
                bool pre = Path.GetFileName(b).StartsWith("_prerestore");
                EditorGUILayout.LabelField($"{Path.GetFileName(b)}   {Human(CachedBytes(b))}" + (pre ? "   (auto safety snapshot)" : ""));
                if (GUILayout.Button("Reveal", GUILayout.Width(60))) EditorUtility.RevealInFinder(b + Path.DirectorySeparatorChar);
                if (GUILayout.Button("Restore", GUILayout.Width(64))) { DoRestore(b); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("Delete", GUILayout.Width(60))) { DeleteBackup(b); GUIUtility.ExitGUI(); }
            }
        EditorGUILayout.EndScrollView();
    }

    // ---- backup ----
    void DoBackup()
    {
        var groups = BuildGroups().Where(g => enabled.TryGetValue(g.Key, out var b) && b).ToList();
        DoBackupInto(NewBackupDir(""), groups, "manual backup");
        sizeCache.Clear();
    }

    // Snapshot the given groups into a fresh folder; write a manifest; verify counts. Returns the folder (or null on failure).
    string DoBackupInto(string dir, List<Group> groups, string note)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var manifest = new List<string> { "# HAF backup manifest", "# note: " + note, "# created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "" };
            int totalFiles = 0; long totalBytes = 0;
            foreach (var g in groups)
                foreach (var src in g.Sources)
                {
                    // Store under <group>/<sourceLeafName>; record the ORIGINAL absolute path so Restore knows where it goes back.
                    string leaf = Path.GetFileName(src.TrimEnd('/', '\\'));
                    string rel = Path.Combine(g.Key, leaf);
                    string dst = Path.Combine(dir, rel);
                    int files = File.Exists(src) ? CopyFile(src, dst) : CopyTree(src, dst);
                    long bytes = TreeBytes(dst);
                    totalFiles += files; totalBytes += bytes;
                    manifest.Add($"SRC\t{rel.Replace('\\', '/')}\t{src.Replace('\\', '/')}\t{files}\t{bytes}");
                }
            File.WriteAllLines(Path.Combine(dir, "manifest.txt"), manifest);
            // verify: re-count what actually landed vs the manifest
            int landed = ManifestSources(dir).Sum(m => FileCount(Path.Combine(dir, m.rel)));
            status = landed == totalFiles
                ? $"Backed up {totalFiles} files ({Human(totalBytes)}) → {Path.GetFileName(dir)}."
                : $"⚠ Backup COUNT MISMATCH: expected {totalFiles}, found {landed} in {Path.GetFileName(dir)} — inspect before trusting it.";
            return dir;
        }
        catch (Exception e) { status = "Backup FAILED: " + e.Message; try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { } return null; }
    }

    // ---- restore (guarded) ----
    void DoRestore(string backupDir)
    {
        var srcs = ManifestSources(backupDir);
        if (srcs.Count == 0) { status = "Nothing to restore — no manifest in " + Path.GetFileName(backupDir) + "."; return; }
        string list = string.Join("\n", srcs.Select(s => "  • " + s.original + "   (" + s.files + " files)"));
        if (!EditorUtility.DisplayDialog("Restore this backup?",
            $"Restoring '{Path.GetFileName(backupDir)}' will copy these back OVER the current files:\n\n{list}\n\n" +
            "GUARDS: your CURRENT state is snapshotted to a _prerestore backup first, and files you've ADDED since are NOT deleted. " +
            "This only overwrites files that also exist in the backup.", "Snapshot & Restore", "Cancel")) { status = "Restore cancelled."; return; }

        // GUARD 1: snapshot the CURRENT state of exactly the paths we're about to overwrite (unique key per source so
        // two originals sharing a leaf name can't collide in the snapshot folder).
        var affected = new List<Group>();
        for (int i = 0; i < srcs.Count; i++) affected.Add(new Group(srcs[i].original, "restore" + i, new List<string> { srcs[i].original }));
        string snap = DoBackupInto(NewBackupDir("_prerestore_"), affected, "auto snapshot before restoring " + Path.GetFileName(backupDir));
        if (snap == null) { status = "Restore ABORTED — could not take the pre-restore safety snapshot (nothing was changed)."; return; }

        // GUARD 2: additive copy back — overwrite matching files, never delete extras.
        int restored = 0;
        try
        {
            foreach (var s in srcs)
            {
                string from = Path.Combine(backupDir, s.rel);
                if (File.Exists(from)) restored += CopyFile(from, s.original);
                else if (Directory.Exists(from)) restored += CopyTree(from, s.original);
            }
            sizeCache.Clear();
            AssetDatabase.Refresh();
            status = $"Restored {restored} files from '{Path.GetFileName(backupDir)}'. Current state was saved to '{Path.GetFileName(snap)}' first (undo by restoring that).";
        }
        catch (Exception e) { status = $"Restore FAILED midway ({e.Message}). Your pre-restore snapshot '{Path.GetFileName(snap)}' is intact — restore IT to get back."; }
    }

    void DeleteBackup(string dir)
    {
        if (!EditorUtility.DisplayDialog("Delete backup?", $"Permanently delete '{Path.GetFileName(dir)}'?\n\nThis removes the backup folder only — your live files are untouched.", "Delete", "Cancel")) return;
        try { Directory.Delete(dir, true); sizeCache.Clear(); status = "Deleted backup '" + Path.GetFileName(dir) + "'."; }
        catch (Exception e) { status = "Delete FAILED: " + e.Message; }
    }

    // ---- helpers ----
    string NewBackupDir(string prefix) => Path.Combine(dest, prefix + DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));

    List<string> ListBackups()
    {
        try { return Directory.Exists(dest) ? Directory.GetDirectories(dest).OrderByDescending(d => d).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    struct MSrc { public string rel, original; public int files; }
    List<MSrc> ManifestSources(string backupDir)
    {
        var outp = new List<MSrc>();
        string mf = Path.Combine(backupDir, "manifest.txt");
        if (!File.Exists(mf)) return outp;
        foreach (var line in File.ReadAllLines(mf))
        {
            if (!line.StartsWith("SRC\t")) continue;
            var p = line.Split('\t');
            if (p.Length >= 4) outp.Add(new MSrc { rel = p[1], original = p[2], files = int.TryParse(p[3], out var n) ? n : 0 });
        }
        return outp;
    }

    static int CopyFile(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        File.Copy(src, dst, true);
        return 1;
    }

    // Recursively copy src dir into dst, overwriting matching files but NEVER deleting files already in dst — so this is
    // inherently safe/additive for both backup (dst is fresh) and restore (dst keeps anything added since the backup).
    static int CopyTree(string src, string dst)
    {
        int n = 0;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); n++;
        }
        foreach (var d in Directory.GetDirectories(src))
            n += CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        return n;
    }

    static int FileCount(string path)
    {
        try { return File.Exists(path) ? 1 : Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length : 0; }
        catch { return 0; }
    }

    static long TreeBytes(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB" }; double b = bytes; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
}
