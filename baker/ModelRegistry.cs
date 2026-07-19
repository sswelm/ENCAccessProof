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

// BAKE-TIME atlas mode. Auto = pack multi-material when the model has >1 material, else single texture. Single/Multi force it.
public enum MaterialMode { Auto = 0, Single = 1, Multi = 2 }

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
    public float albedoBrightness = 1f; // BAKE-TIME: multiply the baked atlas RGB (1 = unchanged). >1 lifts a dark skin — the injection path ships FLAT albedo (donor PBR neutralized), so shiny/dark models read muddy in-game. Runtime plugin ignores it (baked into the atlas).
    public float albedoSaturation = 1f; // BAKE-TIME: scale colour vividness around per-pixel luminance (1 = unchanged, 0 = greyscale, >1 = punchier). Fixes a washed-out/desaturated albedo. Baked into the atlas; plugin ignores it.
    public bool keepBlack = false;      // BAKE-TIME (multi-material): default false neutralizes near-black atlas regions to grey (hides UV dead-zones / packing gaps). Tick for a model with an INTENTIONALLY black material (glossy canopy, dark cockpit) so it stays black. Default false = existing behaviour, so old registries are unaffected.
    public MaterialMode materialMode = MaterialMode.Auto;   // BAKE-TIME: Auto = pack an atlas when the model has >1 material (needed for OPEN kit: a towed gun's wheels/legs/barrel), else one texture. Single = force one texture (closed models: tanks/planes). Multi = force the multi-material atlas even if auto-detect misfires. The ANIMATED path previously only did Single — Multi/Auto now bring per-material atlas packing to animated models too.
    public int atlasMaxDim = 512;      // BAKE-TIME: longest side of the baked atlas in px (256/512/1024/2048). Smaller = smaller shipped _Atlas.asset (DXT1). Units are ~80px at map zoom so 512-1024 is ample. 0/absent -> 512 (baker guards it), so old registries are safe.
    public int targetTris = 24000;      // >0 = quadric-decimate the source to ~this many triangles (via Blender) before baking. NO hard per-model cap exists in the engine (verified live: maxMeshTriangleCount ships unlimited) — the budget is the SHARED pawn-layer pool (~1M verts across all loaded model types; see HAF docs/Vertex-Budget.md + the F8 Mesh Budget readout). Default 24000 = a good roster citizen (halves to 12000 under double-sided); go higher for hero units. It's a CEILING (models already under it pass through untouched, never upscaled). 0 = off.
    public string stripParts = "";      // BAKE-TIME (Blender): comma-separated object-name substrings to DELETE from YOUR model before baking — e.g. its own rotor so the donor's spinning rotor shows through, or a crew figure / weapon pod. Mirror of hideMeshes but on the source model, not the donor. Ignored by the runtime plugin. Empty = keep everything.
    public string hideMeshes = "";      // RUNTIME (not baked): comma-separated donor-FRAGMENT name substrings the plugin hides on this unit — e.g. hide a fragment-based extra. NOTE: a donor's animated skinned sub-parts (a helicopter rotor, spinning wheels) are encoded at pawn-spawn and CANNOT be hidden this late — choose a donor without such parts. Find fragment names in the BepInEx log: "[Uni] <name> donor fragment[i] mesh='...'".
    public int[] skel = new int[4];     // baked skeleton Amplitude guid {a,b,c,d}
    public int[] atlas = new int[4];    // baked atlas Amplitude guid {a,b,c,d}
    public int[] clip = new int[4];     // ANIMATED only: baked ClipCollection Amplitude guid {a,b,c,d}; static models leave it {0,0,0,0}
    public bool animated = false;       // true = baked from the model's OWN armature + clip (animated path), not the procedural vehicle rig
    public string animClip = "";        // ANIMATED only: name of the clip to bake when the model has several (e.g. "hover"); empty = the assigned/first action
    public string animateBones = "";    // ANIMATED only: comma-separated bone-name prefixes to keep animation on (e.g. "prop,rotor"); empty = keep the whole clip
    public bool animUnitFix = false;    // ANIMATED only (BAKE-TIME, not runtime): tick if the model bakes ~100x too big & floats (a metre->cm FBX unit scale). The baker then measures the FBX at true scale (useFileScale off) + bakes with the unit scale on, so Size = in-game units. Per-model: some rig exports need it, others break with it (the drone bakes correct OFF, the howitzer needs it ON).
    public bool convertRig = false;     // ANIMATED only (BAKE-TIME, not runtime): route the bake through the RAW-RIG CONVERSION (rest-normalize + visual rebake, no-op root collapse, topological bone rename, rotation/scale fold, clean-unit export). Needed for auto-rigged/location-keyed rigs (the Combine soldier); leave OFF for purpose-made rigs (drone, howitzer) — off = the byte-identical legacy pipeline. This flag — not the Rotation field — is the pipeline switch (it used to be 'rotation != 0', which made Rotation a landmine on legacy models).
    public bool respawnAfterLoad = false; // RUNTIME (not baked): fix the save-load first-instance rotor race. The engine draws the FIRST custom pawn that borrows a donor's animated sub-part (a helicopter rotor) built during a save-load ~1 low; anything rebuilt AFTER load is correct. Tick this and the plugin re-runs the game's own PresentationUnit.UpdatePawns (release+re-instantiate) on this model's units ~3s post-load, clearing it. Set ONLY for borrowed-rotor models; a brief one-time flicker as those units rebuild.
    public bool freezeDonorAnim = false;  // RUNTIME (not baked): freeze the DONOR's idle/move animation so a STATIC borrowed mesh doesn't inherit its pose bob. Use when your model rides a donor whose hover/idle wiggle looks wrong on it (e.g. the Zeppelin on the Recon-Drone donor). The plugin pins every pawn pose's Time to 0 each frame, holding the mesh rigid while it still glides tile-to-tile. Static models only (animated models play their own baked clip); no re-bake.
    public bool fireOnAttack = false;   // RUNTIME (not baked): ANIMATED only. Play the baked clip ONCE when this unit attacks (bombards) instead of looping — the barrel rests, then elevates on the shot and returns. The plugin listens for SimulationEvent_ArtilleryStrikeStarted, matches the firing unit to this entry, and plays a single 0->1 pass of the clip (author it to start AND end at rest). Leave off for a continuously-looping clip (a drone's spinning prop).
    public bool deployOnStop = false;   // RUNTIME (not baked): ANIMATED only. Hold the baked clip's DEPLOYED pose when the unit is idle, and snap to the UNDEPLOYED pose (frame 0) while it moves — e.g. a howitzer whose barrel/trails deploy when it stops and fold for travel. Author the clip so frame 0 = travelling (undeployed) and deployPoseTime = deployed. Per-unit, instant snap, no state machine. Combine with fireOnAttack for deploy+recoil on one clip: author deploy in [0, deployPoseTime] and the recoil kickback in [deployPoseTime, 1] (deploy_convert.py recoilSrcStart/End); the fire event then sweeps the tail once.
    public float deployPoseTime = 1f;   // RUNTIME (not baked): deployOnStop only. Normalized clip time (0..1) of the DEPLOYED pose held when idle. 1 = a purpose-made deploy clip's end. (0.5 was used to prove the mechanism with the barrel-fire clip's raised plateau.)
    public float deploySpeed = 1f;      // RUNTIME (not baked): deployOnStop only. Speed multiplier on the gradual deploy-on-stop ramp (1 = the clip's authored speed, 2 = twice as fast). Folding on move is always instant, so this only affects the forward deploy.
    public float recoilSpeed = 1f;      // RUNTIME (not baked): deployOnStop + fireOnAttack. Speed multiplier on the recoil-on-fire kickback (1 = the tail's authored speed, 3 = the kick plays 3x faster). Tune this if the kick reads too slow/fast.
    public float desaturate = 0f;       // RUNTIME (not baked): TEXTURE-ONLY GREY variant. 0 = off. >0 = don't repoint the mesh; isolate this unit's output layer and paint a DESATURATED copy of its OWN atlas (1 = full grey) + neutralise the civ-colour tint. Makes a Common copy read as a bland grey version of an emblematic unit (they share the layer, so the original is untouched). No bake / no custom model needed — a hand-added or window-added registry entry keyed on the copy's pawnDescription.
    public float tintR = 0f;            // RUNTIME (not baked): UNIVERSAL skin colour offset, red (-255..+255, 0 = none). Added AFTER desaturate to whatever skin the unit gets (its own atlas OR the textureFile PNG). Equal negatives on R/G/B = darken, positives = brighten, one channel tints. Managed by the Unit Retexture window.
    public float tintG = 0f;            // RUNTIME (not baked): universal skin colour offset, green (-255..+255).
    public float tintB = 0f;            // RUNTIME (not baked): universal skin colour offset, blue (-255..+255).
    public string textureFile = "";     // RUNTIME (not baked): TEXTURE-ONLY RETEXTURE. A PNG filename in the game's BepInEx/config/enc_skins/. When set, the plugin hot-loads that PNG onto the unit's ISOLATED output layer (vanilla mesh kept, original untouched) — no bake/rebuild. Takes precedence over desaturate. Managed by the Unit Retexture window (Tools ▸ ENC ▸ Unit Retexture): paint on a dump of the unit's own atlas, drop it in enc_skins/.
    public bool engineSound = false;    // RUNTIME (not baked): fire the per-ship engine MOVE sound (Play_UNIT_Vehicles_<Type>_Start/_Stop) on this unit's instances. Our injected/retextured units never trigger it themselves (it rides the audio-service path tied to the vanilla unit's move state), so they're silent on move. The plugin detects each instance's move-start/stop (render-position delta) and posts the engine event onto the pawn's AudioEmitter. Naval units proven; land/air TBD.
    public string engineStartEvent = ""; // RUNTIME (not baked): Wwise event NAME posted on move-START (e.g. Play_UNIT_Vehicles_StealthCorvette_Start). Set => posted BY NAME so it works for the FIRST unit at load (no live capture). Empty => the plugin falls back to a handle auto-captured from any same-family vehicle that moved this session. Extract names via the F8 "Dump Sound Catalog" (writes enc_sound_catalog.txt).
    public string engineStopEvent = "";  // RUNTIME (not baked): Wwise event name posted on move-STOP (..._Stop).
    public string soundFile = "";        // RUNTIME (not baked): CUSTOM audio, LOOP — a WAV filename in the game's BepInEx/config/enc_sounds/. The plugin loads it (Unity AudioSource, not Wwise) and plays it as a LOOPING 3D sound WHILE the unit moves — for units the game has no sound for (drones, zeppelins) or a bespoke engine. Convert mp3/ogg to WAV first (16-bit PCM). Managed by the Unit Retexture window.
    public string soundStartFile = "";   // RUNTIME (not baked): CUSTOM audio one-shot played on move-START (spool-up) — a WAV in enc_sounds/.
    public string soundStopFile = "";    // RUNTIME (not baked): CUSTOM audio one-shot played on move-STOP (spool-down) — a WAV in enc_sounds/.
    public float soundVolume = 1f;       // RUNTIME (not baked): travel-loop volume (0..2).
    public float soundStartVolume = 1f;  // RUNTIME (not baked): move-start one-shot volume (0..2).
    public float soundStopVolume = 1f;   // RUNTIME (not baked): move-stop one-shot volume (0..2).
}

// An explicit override of another pack's asset: "this pack intentionally replaces <modId>'s skin on <pawnDescription>."
// RESERVED for HAF multi-mod: the runtime parses + reports it today; ordering/override RESOLUTION is a later increment.
[Serializable]
public class OverrideRef { public string modId = ""; public string pawnDescription = ""; }

// The registry FILE (one HAF pack). The wrapper keys sit BESIDE the existing `models` array — additive, so an older
// bare { "models": [...] } file still loads (JsonUtility fills wrapper defaults). ENC writes itself as the base pack
// (modId "enc", no deps), and the same shape is what a joining modder copies as a template for their own pack.
[Serializable]
class RegistryFile
{
    public int schemaVersion = 1;                                   // HAF schema version this file targets (bump additively)
    public string modId = "enc";                                   // unique pack id; ENC is the base pack
    public List<string> dependsOn = new List<string>();            // RESERVED: modIds this pack requires (parsed + reported, not yet enforced)
    public List<string> loadAfter = new List<string>();            // RESERVED: modIds this pack must load after (deterministic ordering, not yet enforced)
    public List<OverrideRef> overrides = new List<OverrideRef>();  // RESERVED: explicit cross-pack replacements (no implicit overrides)
    public List<ModelDef> models = new List<ModelDef>();           // the Factory-generated model entries (unchanged)
}

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

    // Keep the registry in a STABLE alphabetical order (by resourceName, case-insensitive) everywhere it's read or
    // written, so the Factory dropdown AND both config files (the live enc_models.json + the git-tracked backup) list
    // models the same way every time. Without this the order was insertion/re-serialization order, so a re-bake could
    // shuffle entries — an annoying dropdown that keeps changing, and a giant meaningless backup.json diff each commit.
    // Ordering is display/serialization only; the runtime plugin matches by pawnDescription, so order never affects it.
    static List<ModelDef> SortByName(List<ModelDef> list)
    {
        list?.Sort((a, b) => string.Compare(a?.resourceName, b?.resourceName, StringComparison.OrdinalIgnoreCase));
        return list ?? new List<ModelDef>();
    }

    // LEGACY-CONTRACT MIGRATION (2026-07-18): the conversion pipeline used to be triggered by 'rotation != 0' on an
    // animated entry (the soldier shipped with the 360,0,0 identity trick). That made Rotation a hidden pipeline
    // switch — editing it on a legacy model silently rerouted the bake. The explicit convertRig flag replaced it.
    // ONE-SHOT: it only runs when the FILE predates the flag (no "convertRig" key anywhere in the JSON — every
    // post-refactor Save writes the key on every entry, so its absence is a reliable pre-refactor marker). Gating on
    // the file, not the entry, is what makes a user's deliberate choice stick: an always-on migration would force
    // convertRig back ON every Load for any animated entry with a rotation set, making the unticked state
    // impossible to keep (review 2026-07-19).
    static List<ModelDef> Migrate(List<ModelDef> list, string rawJson)
    {
        if (rawJson != null && rawJson.Contains("\"convertRig\"")) return list;   // post-refactor file — user intent is explicit, keep it
        foreach (var m in list)
            if (m != null && m.animated && !m.convertRig && m.rotation != Vector3.zero)
            {
                m.convertRig = true;
                Debug.Log($"[Factory] {m.resourceName}: migrated to the explicit 'Convert raw rig' flag (was implied by rotation {m.rotation}). The rotation value is unchanged.");
            }
        return list;
    }

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
                    // E6: parse the backup in ITS OWN try/catch. If the backup is corrupt WHILE the live registry is
                    // missing, a throw here would fall into the outer catch — which sets lastLoadCorrupt and names
                    // RegistryPath (a file that doesn't exist in this case), locking Save forever with instructions that
                    // can't be followed. Treat an unreadable backup as "no backup": there's no live registry to protect,
                    // and the next successful Save rewrites the backup anyway.
                    try
                    {
                        var backupJson = File.ReadAllText(ProjectBackupPath);
                        var b = JsonUtility.FromJson<RegistryFile>(backupJson);
                        if (b?.models != null && b.models.Count > 0)
                        {
                            try { Directory.CreateDirectory(ConfigDir); File.WriteAllText(RegistryPath, backupJson); } catch { }
                            Debug.Log($"[Factory] game registry was missing — restored {b.models.Count} model(s) from the project backup ({ProjectBackupPath}).");
                            return Migrate(SortByName(b.models), backupJson);
                        }
                    }
                    catch (Exception be)
                    {
                        Debug.LogWarning($"[Factory] the project backup '{ProjectBackupPath}' is unreadable ({be.Message}) — treating as no backup. " +
                                         "Fix or delete it if you expected models to restore; a fresh Save will overwrite it.");
                    }
                }
                return new List<ModelDef>();
            }
            var liveJson = File.ReadAllText(RegistryPath);
            var data = JsonUtility.FromJson<RegistryFile>(liveJson);
            lastLoadCorrupt = false;
            return Migrate(SortByName(data?.models ?? new List<ModelDef>()), liveJson);
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
        SortByName(models);   // write BOTH the live registry and the backup alphabetically, so the order is stable across bakes
        var json = JsonUtility.ToJson(new RegistryFile { models = models }, true);
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
