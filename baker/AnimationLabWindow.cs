// AnimationLabWindow.cs (ENC editor) — Tools > ENC > Animation Lab.
// The dedicated dialog for a model's ANIMATION: which clip plays and how (fire-on-attack, deploy-on-stop, recoil —
// and, next, the state-driven idle/movement/after-movement set). Mutually exclusive with the Universal Model Factory
// and working together with it: the Factory owns the MODEL (identity, file, transform, size, geometry/shading) and
// shows a read-only animation summary with a jump button here; this Lab owns the animation settings and shows the
// model identity read-only. Both bake through the same pipeline (ConfigFor -> UniversalBaker.BuildAnimated ->
// ModelRegistry.Upsert), so it does not matter where Bake is pressed — the settings are just EDITED in one place each.
// Docks as a tab next to the Factory so the pair presents as one tabbed dialog.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class AnimationLabWindow : EditorWindow
{
    // [SerializeField] so the form survives a DOMAIN RELOAD (recompile / play-mode toggle), matching the other Labs.
    [SerializeField] ModelDef cur = new ModelDef { animated = true };
    [SerializeField] int selected;                 // 0 = <New>, else index into `existing`
    [SerializeField] Vector2 scroll;
    string[] existing = { "<New>" };
    string status = "";
    List<string> animClips = new List<string>();   // clip names read from the model (Clip picker)
    List<KeyValuePair<string, int>> animBonePrefixes = new List<KeyValuePair<string, int>>();  // bone-name prefix -> count (Bones picker)
    string clipProbeFile = "\0";                    // sentinel != any real path so the first real path always inspects

    [MenuItem("Tools/ENC/Animation Lab")]
    static void Open()
    {
        // Dock as a TAB next to the Universal Model Factory (desiredDockNextTo) so the two authoring tools present as
        // one tabbed dialog. Falls back to a floating window when the Factory isn't open.
        var w = GetWindow<AnimationLabWindow>("Animation Lab", false, typeof(ModelFactoryWindow));
        w.minSize = new Vector2(480, 420);
        w.RefreshList();
    }

    void OnEnable() { RefreshList(); }

    // Context handoff from the Universal Model Factory ("Edit in Animation Lab"): land on the RIGHT entry. Match an
    // existing animated entry by resource name, then by model file; otherwise pre-fill a NEW animated entry from the
    // Factory's current form (a fresh rigged model getting its animation configured for the first time).
    internal static void OpenFor(string resourceName, string modelFile, string pawnDescription)
    {
        Open();
        var w = GetWindow<AnimationLabWindow>();
        var all = ModelRegistry.Load();
        var e = all.FirstOrDefault(x => x.animated && !string.IsNullOrEmpty(resourceName) && x.resourceName == resourceName)
             ?? all.FirstOrDefault(x => x.animated && !string.IsNullOrEmpty(modelFile) && x.modelFile == modelFile)
             ?? all.FirstOrDefault(x => !string.IsNullOrEmpty(resourceName) && x.resourceName == resourceName);   // not-yet-animated entry being upgraded
        if (e != null)
        {
            w.cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone, as OnSelectResource does
            w.cur.animated = true;
            w.status = "Loaded '" + e.resourceName + "' (handed over from the Model Factory).";
        }
        else
        {
            w.cur = new ModelDef { animated = true, resourceName = resourceName ?? "", modelFile = modelFile ?? "", pawnDescription = pawnDescription ?? "" };
            w.status = "New animated entry pre-filled from the Model Factory — pick a clip and Bake.";
        }
        w.RefreshList();   // re-derives the dropdown index from the loaded name
        w.Repaint();
    }

    // Only ANIMATED entries are listed (static models have no animation to configure).
    void RefreshList()
    {
        var names = ModelRegistry.Load().Where(m => m.animated).Select(m => m.resourceName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        names.Insert(0, "<New>");
        existing = names.ToArray();
        // Index-by-NAME, never a persisted numeric index: the list is filtered + rebuilt per reload, so a stale index
        // would present a different entry than the form holds.
        selected = Array.IndexOf(existing, cur.resourceName);
        if (selected < 0) selected = 0;
    }

    // Re-read the model's clip/bone names only when the path changes (OnGUI runs every frame; file I/O must not).
    void EnsureClips()
    {
        string f = cur.modelFile ?? "";
        if (f == clipProbeFile) return;
        clipProbeFile = f;
        (animClips, animBonePrefixes) = ModelFactoryWindow.InspectModel(f);
    }

    void OnSelectResource()
    {
        if (selected <= 0) { cur = new ModelDef { animated = true }; status = ""; return; }
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == existing[selected]);
        if (e == null) return;
        cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone so edits don't mutate the stored copy
        cur.animated = true;
        status = "Loaded '" + e.resourceName + "'.";
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("Animation Lab", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Configures the ANIMATION of a model: which clip plays and how (continuous loop, fire-on-attack, " +
            "deploy-on-stop + recoil). The model itself (file, transform, size, shading) is set up in the Universal " +
            "Model Factory — each setting lives in exactly one of the two windows.", MessageType.None);

        // --- Pick the animated entry to edit ---
        using (new EditorGUILayout.HorizontalScope())
        {
            int newSel = EditorGUILayout.Popup("Edit existing", selected, existing);
            if (newSel != selected) { selected = newSel; OnSelectResource(); GUI.FocusControl(null); }
            using (new EditorGUI.DisabledScope(selected <= 0))
                if (GUILayout.Button("Remove", GUILayout.Width(72)))
                    if (EditorUtility.DisplayDialog("Remove entry",
                        $"Remove '{existing[selected]}' from the registry? (Baked assets stay on disk.)", "Remove", "Cancel"))
                    { ModelRegistry.Remove(existing[selected]); cur = new ModelDef { animated = true }; selected = 0; RefreshList(); status = "Removed."; }
        }
        EditorGUILayout.Space();

        // --- Model identity: READ-ONLY here (the Factory owns it) ---
        bool hasModel = !string.IsNullOrWhiteSpace(cur.resourceName);
        if (!hasModel)
        {
            EditorGUILayout.HelpBox("No model loaded. Pick one under 'Edit existing', or set the model up in the " +
                "Universal Model Factory first (file, transform, size) and press its 'Edit in Animation Lab' button.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.LabelField("Resource", cur.resourceName);
            EditorGUILayout.LabelField("Target pawn", string.IsNullOrWhiteSpace(cur.pawnDescription) ? "(set in the Model Factory)" : cur.pawnDescription);
            EditorGUILayout.LabelField("Model file", string.IsNullOrWhiteSpace(cur.modelFile) ? "(re-bake uses the extracted files)" : cur.modelFile, EditorStyles.wordWrappedMiniLabel);
        }
        EnsureClips();

        // --- Clip ---
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Clip", EditorStyles.miniBoldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.animClip = EditorGUILayout.TextField(new GUIContent("Clip name",
                "Which animation to bake when the model has several clips. Use Pick to choose from the clips found in the " +
                "model. Leave EMPTY to use the model's assigned/first clip. Changing the clip needs a re-Bake."), cur.animClip ?? "");
            using (new EditorGUI.DisabledScope(animClips.Count == 0))
                if (GUILayout.Button(new GUIContent("Pick", animClips.Count == 0 ? "No clips readable (glb/gltf only) — type the name" : null), GUILayout.Width(70)))
                {
                    var r = GUILayoutUtility.GetLastRect();
                    var arr = animClips.ToArray();
                    new StringDropdown(new AdvancedDropdownState(), arr, arr, "Clips", n => { cur.animClip = n; Repaint(); }).Show(r);
                }
        }
        // Animate only bones — free text + a Pick that appends a bone-name prefix (grouped, with counts) from the model.
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.animateBones = EditorGUILayout.TextField(new GUIContent("Animate only bones",
                "Optional. Comma-separated bone-name PREFIXES to keep animation on — e.g. 'prop' keeps the spinning parts " +
                "and strips camera / body-bob curves that make the model wobble. Use Pick to add a prefix found in the model. " +
                "Leave EMPTY to keep the whole clip. The frame range is always auto-clamped (kills the ~1s per-loop stall)."), cur.animateBones ?? "");
            using (new EditorGUI.DisabledScope(animBonePrefixes.Count == 0))
                if (GUILayout.Button(new GUIContent("Pick", animBonePrefixes.Count == 0 ? "No bones readable from this model (glb/gltf only) — type prefixes" : null), GUILayout.Width(70)))
                {
                    var r = GUILayoutUtility.GetLastRect();
                    var labels = animBonePrefixes.Select(kv => $"{kv.Key}  ({kv.Value} part{(kv.Value == 1 ? "" : "s")})").ToArray();
                    var values = animBonePrefixes.Select(kv => kv.Key).ToArray();
                    new StringDropdown(new AdvancedDropdownState(), labels, values, "Bone prefixes", p =>
                    {
                        var set = (cur.animateBones ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        if (!set.Contains(p)) set.Add(p);
                        cur.animateBones = string.Join(",", set);
                        Repaint();
                    }).Show(r);
                }
        }
        cur.animUnitFix = EditorGUILayout.Toggle(new GUIContent("Fix 100× oversize (FBX unit scale)",
            "Bake-time scale fix for rigged exports that embed a metre→centimetre unit scale (bake ~100× too big, float " +
            "in the sky). PER-MODEL: if the model bakes huge/floating, tick it; if ticking makes it vanish, untick (the " +
            "drone bakes right OFF; the howitzer needs it ON). Re-bake after changing."),
            cur.animUnitFix);
        if (!UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("The animated bake needs Blender (to slim the rig + bake the clip) — it wasn't found. " +
                "Install Blender or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);

        // --- Behavior (runtime flags — Save (no bake) + relaunch applies them) ---
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Behavior", EditorStyles.miniBoldLabel);
        cur.fireOnAttack = EditorGUILayout.Toggle(new GUIContent("Fire on attack (play once)",
            "Play the baked clip ONCE when this unit attacks, instead of looping — the model rests, then plays a single pass " +
            "on the shot and returns to rest (e.g. a howitzer barrel that elevates only when it bombards). AUTHOR THE CLIP TO " +
            "START AND END AT REST. Leave OFF for a continuous loop (a drone's spinning prop)."),
            cur.fireOnAttack);
        cur.deployOnStop = EditorGUILayout.Toggle(new GUIContent("Deploy when stopped",
            "Hold the baked clip's DEPLOYED pose while idle, and snap to the UNDEPLOYED pose (frame 0) the instant it moves — " +
            "e.g. a howitzer that deploys its barrel/trails when it stops and folds them for travel. AUTHOR THE CLIP so frame 0 " +
            "= travelling and the deployed pose sits at 'Deployed pose time'."),
            cur.deployOnStop);
        using (new EditorGUI.DisabledScope(!cur.deployOnStop))
            cur.deployPoseTime = EditorGUILayout.Slider(new GUIContent("Deployed pose time",
                "Normalized clip time (0..1) of the DEPLOYED pose held when idle. 1 = a purpose-made deploy clip's end frame."),
                cur.deployPoseTime <= 0f ? 1f : cur.deployPoseTime, 0f, 1f);
        using (new EditorGUI.DisabledScope(!cur.deployOnStop))
            cur.deploySpeed = EditorGUILayout.Slider(new GUIContent("Deploy speed",
                "Speed multiplier on the gradual deploy-on-stop ramp: 1 = the clip's authored speed. Folding on move is always instant."),
                cur.deploySpeed <= 0f ? 1f : cur.deploySpeed, 0.25f, 5f);
        using (new EditorGUI.DisabledScope(!cur.deployOnStop || !cur.fireOnAttack))
            cur.recoilSpeed = EditorGUILayout.Slider(new GUIContent("Recoil speed",
                "Speed multiplier on the recoil-on-fire kickback (needs Deploy-when-stopped + Fire-on-attack): 1 = the clip " +
                "tail's authored speed."),
                cur.recoilSpeed <= 0f ? 1f : cur.recoilSpeed, 0.25f, 8f);

        // --- Bake / Save ---
        EditorGUILayout.Space();
        bool hasBaked = cur.skel != null && cur.skel.Length == 4 && !(cur.skel[0] == 0 && cur.skel[1] == 0 && cur.skel[2] == 0 && cur.skel[3] == 0);
        bool canBake = hasModel && !string.IsNullOrWhiteSpace(cur.pawnDescription)
                    && (hasBaked || !string.IsNullOrWhiteSpace(cur.modelFile));
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!canBake))
                if (GUILayout.Button(new GUIContent("Bake", "Re-run the animated pipeline (Blender slim + skeleton + clip + atlas) with the settings above, then save the registry entry."), GUILayout.Height(34))) DoBake();
            using (new EditorGUI.DisabledScope(!hasBaked || !hasModel))
                if (GUILayout.Button(new GUIContent("Save (no bake)",
                    "Write the registry entry with the current settings WITHOUT re-baking assets — enough for the runtime " +
                    "Behavior flags/sliders (the clip fields need a real Bake). Relaunch the game to see it."),
                    GUILayout.Height(34), GUILayout.Width(110)))
                    SaveOnly();
            if (GUILayout.Button("Reset", GUILayout.Height(34), GUILayout.Width(72)))
            { cur = new ModelDef { animated = true }; selected = 0; status = ""; GUI.FocusControl(null); }
        }
        if (!canBake && hasModel)
            EditorGUILayout.HelpBox("This entry has no baked assets and no model file — open it in the Model Factory to set the file, then bake.", MessageType.Warning);
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        EditorGUILayout.HelpBox("Registry: " + ModelRegistry.RegistryPath, MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    // Persist runtime-only tweaks without touching the baked assets: the entry keeps its existing skeleton/clip/atlas
    // GUIDs (loaded with the entry), so the plugin re-reads the new settings on the next game launch.
    void SaveOnly()
    {
        cur.animated = true;
        cur.resourceName = (cur.resourceName ?? "").Trim();
        cur.pawnDescription = (cur.pawnDescription ?? "").Trim();
        bool saved = ModelRegistry.Upsert(cur);
        RefreshList();
        status = saved
            ? $"Saved '{cur.resourceName}' (registry only, assets untouched). Relaunch the game — no re-bake, no mod rebuild."
            : "REGISTRY SAVE FAILED (see Console). Close whatever's locking enc_models.json and retry.";
        Debug.Log("[AnimLab] " + status);
    }

    // Same flow as ModelFactoryWindow.DoBake, scoped to the animated path: trim -> ConfigFor -> BuildAnimated ->
    // capture the baked GUIDs onto the entry -> Upsert to the registry.
    void DoBake()
    {
        cur.animated = true;
        cur.resourceName = (cur.resourceName ?? "").Trim();
        cur.pawnDescription = (cur.pawnDescription ?? "").Trim();
        cur.modelFile = (cur.modelFile ?? "").Trim();
        cur.animClip = (cur.animClip ?? "").Trim();
        cur.animateBones = (cur.animateBones ?? "").Trim();
        var cfg = ModelFactoryWindow.ConfigFor(cur);
        var r = UniversalBaker.BuildAnimated(cfg);
        if (!r.ok) { status = "Bake FAILED: " + r.error; Debug.LogError("[AnimLab] " + r.error); return; }
        cur.skel = ModelRegistry.ParseGuid(r.skeletonGuid);
        cur.atlas = ModelRegistry.ParseGuid(r.atlasGuid);
        cur.clip = ModelRegistry.ParseGuid(r.clipGuid);
        bool saved = ModelRegistry.Upsert(cur);
        RefreshList();
        status = saved
            ? $"Baked ANIMATED '{cur.resourceName}' -> '{cur.pawnDescription}'\nskeleton {r.skeletonGuid}\nclip {r.clipGuid}\nRebuild the mod + relaunch."
            : $"Baked '{cur.resourceName}', but the REGISTRY SAVE FAILED (see Console). Close whatever's locking enc_models.json and re-bake.";
        Debug.Log("[AnimLab] " + status);
    }
}
