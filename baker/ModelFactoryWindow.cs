// ModelFactoryWindow.cs (ENC editor) — Tools > Model Factory.
// Create a NEW 3D resource or pick an existing one, choose a target pawn description + a model file (.glb/.obj/.fbx),
// and configure EVERYTHING we learned makes a model work: rotation, position (z = waterline), size, normals mode,
// smoothing angle, conversion grid. Press Bake -> skeleton + atlas + a JSON registry entry the in-game plugin reads.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;                 // SDK-provided (mod.io) — robust glTF parse for the Clip/Bone pickers
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class ModelFactoryWindow : EditorWindow
{
    // [SerializeField] so Unity preserves the form across a DOMAIN RELOAD (any script recompile, entering/exiting Play
    // mode, etc.). Without it these are wiped back to defaults mid-edit — the "fields went empty on their own" bug.
    // (ModelDef is [Serializable], so the whole edited entry round-trips.)
    [SerializeField] ModelDef cur = new ModelDef();
    [SerializeField] int selected;      // 0 = <New>, else index into `existing`
    string[] existing = { "<New>" };
    string status = "";
    Vector2 scroll;
    Vector2 stripScroll;                // scroll position of the multi-line Strip-parts text area
    GUIStyle wrapArea;                  // cached word-wrapping text-area style (lazy-init in OnGUI; EditorStyles isn't ready earlier)
    bool showSettings;
    UnityEditor.Editor previewEditor;   // embedded interactive 3D preview of the baked model (its _Preview/_Model prefab)
    string previewFor = "";

    // Cheap animation probe (no Blender), cached per model-file path. State: 0 = unknown (allow), 1 = animation
    // detected (allow + hint), 2 = definitely none (disable the Animated toggle). Keeps the checkbox from being ticked
    // on a static model. Runs once when the path changes, not every OnGUI frame.
    string animProbeFile = "";   // sentinel != any real path so the first real path always probes
    int animProbeState;
    List<string> animClips = new List<string>();                                   // clip names read from the model (Clip picker)
    List<KeyValuePair<string, int>> animBonePrefixes = new List<KeyValuePair<string, int>>();  // bone-name prefix -> count (Bones picker)

    [MenuItem("Tools/HAF/Model Factory")]
    static void Open()
    {
        var w = GetWindow<ModelFactoryWindow>(false, "Model Factory");
        w.minSize = new Vector2(500, 470);
        w.RefreshList();
    }

    void OnEnable()
    {
        titleContent = new GUIContent("Model Factory");   // rename any pre-existing docked instance: Unity caches the tab title in the window's serialized state, so the GetWindow title alone never reaches already-open windows
        RefreshList(); LoadPreview(cur.resourceName);
        // Tear the preview editor down BEFORE the domain unloads (and before editor quit). Destroying it from
        // OnDisable DURING a reload is too late: Unity's GameObjectInspector.OnDisable then runs against an
        // already-Disposed SerializedObject and logs "NullReferenceException: SerializedObject of SerializedProperty
        // has been Disposed" — logged INSIDE DestroyImmediate by Unity itself, so our try/catch never sees it.
        // Destroying at beforeAssemblyReload, while everything is still alive, is clean; OnDisable then no-ops.
        AssemblyReloadEvents.beforeAssemblyReload += DestroyPreview;
        EditorApplication.quitting += DestroyPreview;
    }
    void OnDisable()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= DestroyPreview;
        EditorApplication.quitting -= DestroyPreview;
        DestroyPreview();
    }

    // ANY window that re-bakes (the Animation Lab included) must release the Factory's live preview first: the bake
    // rewrites the preview prefab, and a GameObjectInspector still watching it throws
    // "InstantiateForAnimatorPreview(null)" from Unity internals mid-bake (seen on the first Lab state-bake).
    internal static void ReleasePreviews()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<ModelFactoryWindow>()) w.LoadPreview(null);
    }
    internal static void ReloadPreviews()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<ModelFactoryWindow>())
        { w.LoadPreview(w.cur != null ? w.cur.resourceName : null, forceReimport: true); w.Repaint(); }
    }

    // Destroy the interactive preview editor safely. The try/catch covers a plain window close where Unity's
    // GameObjectInspector teardown can still throw for reasons of its own; the domain-reload variant of that
    // failure is prevented at the source by the beforeAssemblyReload hook above.
    void DestroyPreview()
    {
        if (previewEditor == null) return;
        try { UnityEngine.Object.DestroyImmediate(previewEditor); } catch { }
        previewEditor = null;
    }

    // Load the baked prefab (animated <name>_Preview, else static <name>_Model) and build an interactive preview editor.
    // forceReimport: after a bake, the static path overwrites the mesh/prefab IN PLACE, so Unity can serve the preview a
    // stale cached copy until a manual reimport. Force a synchronous reimport of the mesh + prefab so the preview is current.
    void LoadPreview(string name, bool forceReimport = false)
    {
        DestroyPreview();
        previewFor = name ?? "";
        if (string.IsNullOrEmpty(name)) return;
        // The animated preview companion lives in FactorySource (bake INPUT, not shipped), NOT Resources — see
        // GeneratePreviewPrefab. This path lagged behind when the working folder moved out of Resources, so the animated
        // preview silently stopped loading; keep it pointed at FactorySource. The static _Model.prefab is a shipped OUTPUT and stays in Resources root.
        string animPath = "Assets/FactorySource/" + name + "/" + name + "_Preview.prefab";
        string staticPath = "Assets/Resources/" + name + "_Model.prefab";
        string path = AssetDatabase.LoadMainAssetAtPath(animPath) != null ? animPath
                    : AssetDatabase.LoadMainAssetAtPath(staticPath) != null ? staticPath : null;
        if (path == null) return;
        if (forceReimport)
            foreach (var dep in new[] { "Assets/Resources/" + name + "_ModelMesh.asset", path })
                if (AssetDatabase.LoadMainAssetAtPath(dep) != null)
                    AssetDatabase.ImportAsset(dep, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (go != null) previewEditor = UnityEditor.Editor.CreateEditor(go);
    }

    // Interactive 3D preview embedded in the window, so a bake's result shows immediately (no hunting in the Project view).
    void DrawPreview()
    {
        if (previewEditor == null || !previewEditor.HasPreviewGUI()) return;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Preview — " + previewFor + "   (drag to orbit, scroll to zoom)", EditorStyles.miniBoldLabel);
        var r = GUILayoutUtility.GetRect(200, 260, GUILayout.ExpandWidth(true));
        previewEditor.OnInteractivePreviewGUI(r, EditorStyles.helpBox);
    }

    void RefreshList()
    {
        // STATIC entries only — ANIMATED entries are authored exclusively in Tools ▸ ENC ▸ Animation Lab (which in
        // turn lists only animated ones). One entry = one owning window, so the same model can never be edited (and
        // silently overwritten, last-save-wins) from two places at once.
        var names = ModelRegistry.Load().Select(e => e.resourceName).ToList();
        names.Insert(0, "<New>");
        existing = names.ToArray();
        // The dropdown INDEX follows the loaded entry by NAME — the list is rebuilt on every reload, so a persisted
        // numeric index can silently point at a different entry than the form holds.
        selected = Array.IndexOf(existing, cur.resourceName);
        if (selected < 0) selected = 0;
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        // Widen the label column so the longer labels ("Position offset (Z = waterline)", "Double-sided (single-sided/CAD)",
        // "Animated (own rig + clip)") aren't clipped. Scales with window width so fields still get room when widened.
        EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.42f, 210f, 320f);
        GUILayout.Space(10f);
        EditorGUILayout.LabelField("Model Factory", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        DrawSettings();
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            int sel = EditorGUILayout.Popup("3D resource", selected, existing);
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) RefreshList();
            // Remove the selected registry entry (disabled on <New>). Prompts, then drops it from enc_models.json.
            using (new EditorGUI.DisabledScope(selected <= 0))
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    // E2: key on the SELECTED entry, NOT the (possibly edited) resource-name text field. Keying on the
                    // text field meant editing the name then Remove would delete a DIFFERENT model — or nothing — while
                    // still reporting "Removed". Also branch the status on Remove's actual result.
                    var name = selected > 0 && selected < existing.Length ? existing[selected] : null;
                    if (!string.IsNullOrEmpty(name) &&
                        EditorUtility.DisplayDialog("Remove model",
                            $"Remove '{name}' from the registry? The plugin will stop injecting it on next launch. " +
                            "(The baked skeleton/atlas assets stay in the project.)", "Remove", "Cancel"))
                    {
                        bool removed = ModelRegistry.Remove(name);
                        selected = 0; cur = new ModelDef(); RefreshList(); GUI.FocusControl(null);
                        status = removed ? $"Removed '{name}' from the registry."
                                         : $"'{name}' was not in the registry — nothing removed.";
                    }
                }
            if (sel != selected) { selected = sel; OnSelectResource(); GUI.FocusControl(null); }
        }
        EditorGUILayout.Space();

        cur.resourceName = EditorGUILayout.TextField("Resource name", cur.resourceName);
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.pawnDescription = EditorGUILayout.TextField("Pawn description", cur.pawnDescription);
            if (GUILayout.Button("Pick", GUILayout.Width(70)))
            {
                var r = GUILayoutUtility.GetLastRect();
                new PawnDropdown(new AdvancedDropdownState(), GatherPawnNames(), n =>
                {
                    cur.pawnDescription = n;
                    if (string.IsNullOrWhiteSpace(cur.resourceName)) cur.resourceName = DeriveResourceName(n); // suggest a name for a NEW resource
                    Repaint();
                }).Show(r);
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.modelFile = EditorGUILayout.TextField("Model file", cur.modelFile);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                // Start at the current model file's folder (walking up to the nearest existing ancestor if it was
                // renamed/moved), so Browse opens near the source instead of the project root.
                string start = string.IsNullOrWhiteSpace(cur.modelFile) ? "" : System.IO.Path.GetDirectoryName(cur.modelFile);
                while (!string.IsNullOrEmpty(start) && !System.IO.Directory.Exists(start))
                    start = System.IO.Path.GetDirectoryName(start);
                var p = EditorUtility.OpenFilePanel("Select 3D model", start ?? "", "glb,gltf,obj,fbx,blend");
                if (!string.IsNullOrEmpty(p))
                {
                    cur.modelFile = p;
                    // Prefill "Fix 100x oversize" from the model's TRUE size (only on an explicit new pick, so a loaded
                    // entry keeps its saved value). Best-effort guess the user can override — see SuggestUnitFix.
                    float sz; bool guess = SuggestUnitFix(p, out sz);
                    if (sz > 0f) { cur.animUnitFix = guess; status = $"Auto-set 'Fix 100× oversize' = {(guess ? "ON" : "off")} (model true size ≈ {sz:0.###}u). Override if the bake comes out wrong."; }
                }
            }
        }
        // BROKEN-LINK REPORT: a referenced model file that isn't on disk (source moved/renamed) — warn as soon as the
        // entry is shown, not only when Bake fails.
        if (!string.IsNullOrWhiteSpace(cur.modelFile) && !System.IO.File.Exists(cur.modelFile))
            EditorGUILayout.HelpBox("Model file not found on disk:\n" + cur.modelFile +
                "\nThe source was moved or renamed — fix it with Browse (or reload the entry if the registry was already corrected).", MessageType.Warning);
        if ((cur.modelFile ?? "").ToLowerInvariant().EndsWith(".blend") && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox(".blend import needs Blender installed (auto-detected). Install it, or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);

        // --- Animation: SUMMARY ONLY. The settings themselves (clip, bones, behaviors) are edited exclusively in the
        //     Animation Lab — mutually exclusive settings, working together: this window shows what's configured and
        //     jumps there; Bake here still uses the saved animation config, so baking works from either window.
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Animation", EditorStyles.miniBoldLabel);
        EnsureAnimProbe(cur.modelFile);
        if (!cur.animated && LooksAnimated(cur)) cur.animated = true;   // self-heal a lost flag (entry carries animation config)
        if (cur.animated)
        {
            var beh = new List<string>();
            if (cur.animStateDriven) beh.Add($"STATE-DRIVEN (idle '{cur.animClip}', move '{cur.animClipMove}'{(string.IsNullOrWhiteSpace(cur.animClipAfter) ? "" : $", after '{cur.animClipAfter}'")}{(string.IsNullOrWhiteSpace(cur.animClipAttack) ? "" : $", attack '{cur.animClipAttack}'")}{(string.IsNullOrWhiteSpace(cur.animClipCombat) ? "" : $", combat '{cur.animClipCombat}'")})");
            else if (!string.IsNullOrWhiteSpace(cur.animClip)) beh.Add("clip '" + cur.animClip + "'");
            if (!string.IsNullOrWhiteSpace(cur.animateBones)) beh.Add("bones '" + cur.animateBones + "'");
            if (cur.convertRig) beh.Add("raw-rig conversion");
            if (cur.fireOnAttack) beh.Add("fire-on-attack");
            if (cur.deployOnStop) beh.Add($"deploy-on-stop (pose {cur.deployPoseTime:0.##}, speed {cur.deploySpeed:0.##})");
            if (cur.fireOnAttack && cur.deployOnStop) beh.Add($"recoil (speed {cur.recoilSpeed:0.##})");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("ANIMATED — " + (beh.Count > 0 ? string.Join(", ", beh) : "no clip/behaviors configured yet") +
                    "\nAnimation settings are edited in the Animation Lab; Bake here uses them as saved.", MessageType.None);
                if (GUILayout.Button("Edit in\nAnimation Lab", GUILayout.Width(110), GUILayout.Height(38)))
                    AnimationLabWindow.OpenFor(cur.resourceName, cur.modelFile, cur.pawnDescription);
            }
            if (!UniversalBaker.BlenderAvailable())
                EditorGUILayout.HelpBox("The animated path needs Blender (to slim the rig + bake the clip) — it wasn't found. " +
                    "Install Blender or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);
            EditorGUILayout.HelpBox("Animated mode uses Size + Reduce-to-tris; the static Mesh/shading options below " +
                "(normals, winding, double-sided, height UVs, convert grid) don't apply.", MessageType.None);
        }
        else if (animProbeState == 1)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("Animation detected in this model — configure its clip + behaviors in the " +
                    "Animation Lab (it will bake as ANIMATED from then on).", MessageType.Info);
                if (GUILayout.Button("Open\nAnimation Lab", GUILayout.Width(110), GUILayout.Height(38)))
                    AnimationLabWindow.OpenFor(cur.resourceName, cur.modelFile, cur.pawnDescription);
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Transform", EditorStyles.miniBoldLabel);
        cur.rotation = EditorGUILayout.Vector3Field("Rotation offset (XYZ)", cur.rotation);
        cur.position = EditorGUILayout.Vector3Field("Position offset (Z = waterline)", cur.position);
        using (new EditorGUILayout.HorizontalScope())
        {
            // Keep the Size input compact (not full-width) but sized RELATIVE to the label column — a fixed 220 left the
            // input a ~10px sliver once the label column widened to ~210. label + ~90px input keeps the box usable.
            cur.size = EditorGUILayout.FloatField(new GUIContent("Size (units)", "Length of the model's longest axis, in world units"), cur.size, GUILayout.Width(EditorGUIUtility.labelWidth + 90f));
            GUILayout.FlexibleSpace();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Source geometry — pre-bake (Blender)", EditorStyles.miniBoldLabel);
        // Sequential transforms on YOUR model BEFORE the bake, in the order the baker runs them: strip → reduce → convert.
        // Strip parts — BAKE-TIME (Blender). Deletes objects from YOUR model before baking (mirror of Hide donor meshes,
        // which acts on the DONOR at runtime). Use it to drop your model's own rotor so the donor's spinning rotor shows
        // through, or to remove a crew figure / weapon pod. Comma-separated object-name substrings (case-insensitive).
        if (wrapArea == null) wrapArea = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
        using (new EditorGUILayout.HorizontalScope())
        {
            // Label + a fixed-height (~3-line) scroll view holding a word-wrapping text area, so a long list (a rotor's
            // hub + blades + tail + their doubled -FACES copies) stays readable and scrolls instead of running off-screen.
            GUILayout.Label(new GUIContent("Strip parts (names)",
                "BAKE-TIME: comma-separated object-name substrings to DELETE from your model before baking (each match takes " +
                "its children too). Use it to remove parts you don't want baked in — e.g. a helicopter's OWN rotor (so the " +
                "donor's animated rotor spins through), a crew figure, or a weapon pod. Case-insensitive substring match on the " +
                "source object names. This is the mirror of 'Hide donor meshes' (which hides the DONOR's parts at runtime) — " +
                "this edits YOUR model at bake time. Needs Blender. Empty = keep everything."),
                GUILayout.Width(EditorGUIUtility.labelWidth));
            using (var sv = new EditorGUILayout.ScrollViewScope(stripScroll, GUILayout.Height(74f)))
            {
                stripScroll = sv.scrollPosition;
                // ExpandHeight so the editable box FILLS the 74px (3-line) viewport even when it's near-empty — without it
                // the text area only grows to the content, so you'd see 1-2 lines. It still grows past 3 lines (scrollbar).
                cur.stripParts = EditorGUILayout.TextArea(cur.stripParts ?? "", wrapArea, GUILayout.ExpandHeight(true));
            }
            if (GUILayout.Button(new GUIContent("Pick", "List the object names in the Model file so you can choose which to strip (reads GLB/glTF directly)."), GUILayout.Width(70), GUILayout.Height(74f)))
            {
                var r = GUILayoutUtility.GetLastRect();
                var names = UniversalBaker.ListModelObjectNames((cur.modelFile ?? "").Trim());
                if (names.Length == 0)
                    EditorUtility.DisplayDialog("Strip parts",
                        "Couldn't read object names from the Model file.\n\n" +
                        "Pick reads names directly from GLB / glTF — make sure the Model file above points at a .glb/.gltf. " +
                        "For FBX / OBJ / .blend, open the model in Blender to see the object names and type the substrings by " +
                        "hand (each match strips that object + its children).", "OK");
                else
                    new StringDropdown(new AdvancedDropdownState(), names, names, "Model objects", m =>
                    {
                        var set = (cur.stripParts ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        if (!set.Contains(m)) set.Add(m);
                        cur.stripParts = string.Join(",", set);
                        Repaint();
                    }).Show(r);
            }
        }
        if (!string.IsNullOrWhiteSpace(cur.stripParts) && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("Strip parts uses Blender — it wasn't found, so Bake will fail. Clear the field or set " +
                "Blender's path in Settings above.", MessageType.Warning);
        // Reduce-to-tris — runs AFTER strip (so the tri budget is spent only on the geometry you keep, not on a rotor
        // you're about to delete). There is NO hard per-model cap in the engine (verified: maxMeshTriangleCount ships
        // 0/unlimited) — the real budget is the SHARED pawn-layer pool (~1M verts, ~700k used by the full roster at load;
        // see HAF docs/Vertex-Budget.md). Slider range 0..100000: default 24000 is a sensible share of the pool; go higher
        // for a hero unit (mind the F8 'Mesh Budget' readout), or grow the pool itself via [Buffers] BufferOverrides.
        cur.targetTris = EditorGUILayout.IntSlider(new GUIContent("Reduce to ~tris (0 = off)",
            "Quadric-decimate a heavy model to about this many triangles (via Blender) before baking. There's NO hard " +
            "per-model limit — the budget is the SHARED pawn buffer (~1,000,000 verts across ALL loaded model types; " +
            "~300k free with vanilla + the current set — check F8 ▸ Mesh Budget in-game, or raise the pool with the " +
            "plugin's [Buffers] BufferOverrides). Runs AFTER 'Strip parts', so the budget covers only the geometry you " +
            "keep. Default 24000 is a good roster citizen; 50k+ is fine for a hero unit. It's a CEILING, not a quota: a " +
            "model already under it passes through untouched (never upscaled). Toggling Double-sided automatically HALVES " +
            "the effective target (it doubles the baked geometry). Preserves thin parts (per-object). 0 = no reduction. " +
            "Needs Blender (auto-detected)."), cur.targetTris, 0, 100000);
        if (cur.targetTris > 0 && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("Reduce-to-tris uses Blender (quadric decimation) — Blender wasn't found, so Bake will " +
                "fail. Either set this to 0, use 'Convert grid' below (Blender-free GLB decimation), or install Blender / " +
                "set its path in Settings above.", MessageType.Warning);
        // Convert grid — the GLB/glTF/.blend -> OBJ conversion (runs after strip + reduce). Also a geometry step, so it
        // lives with its siblings here rather than up among the shading knobs.
        cur.convertGrid = EditorGUILayout.IntField(new GUIContent("Convert grid",
            "GLB / glTF / .blend only — controls how the source mesh is converted to OBJ.\n\n" +
            "0 = faithful: keep every vertex and UV exactly (preserves texture seams). Use this for " +
            "textured models — any decimation averages UVs across seams and scrambles the skin.\n\n" +
            ">0 = decimate to a vertex-cluster grid of this resolution along the longest axis " +
            "(higher = more detail/vertices). Use only for heavy UNtextured meshes that need simplifying.\n\n" +
            "Ignored for OBJ/FBX (already meshes)."), cur.convertGrid);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Shading / normals — bake-time", EditorStyles.miniBoldLabel);
        // Parameters read when the final mesh is built (import/bake) — NOT sequential steps, so order among them is
        // irrelevant to the result. Grouped apart from the geometry transforms above for that reason.
        cur.normalsMode = (int)(NormalsMode)EditorGUILayout.EnumPopup("Normals", (NormalsMode)cur.normalsMode);
        using (new EditorGUI.DisabledScope(cur.normalsMode != (int)NormalsMode.Recalculate))
            cur.smoothingAngle = EditorGUILayout.Slider("Smoothing angle", cur.smoothingAngle, 0f, 180f);
        cur.heightUV = EditorGUILayout.Toggle(new GUIContent("Height-based UVs",
            "Override UVs with V = normalized height, so a vertical-gradient albedo maps by height regardless of the " +
            "model's own UVs — e.g. a black skirt low + grey hull high (put a bottom-black / top-grey PNG named " +
            "'*albedo*.png' in the resource folder). For untextured CAD models that need a simple gradient skin."), cur.heightUV);
        cur.windingFix = EditorGUILayout.Toggle(new GUIContent("Winding fix (CAD/convex)",
            "Rewind faces outward so single-sided / CAD 'sketch' meshes render single-sided instead of culling to invisible " +
            "(e.g. a hovercraft skirt). Lighter than double-sided (no extra geometry). Assumes a roughly convex hull — " +
            "true for vehicles/ships. Preferred for CAD hulls; use Double-sided for genuinely non-convex thin shells."), cur.windingFix);
        cur.doubleSided = EditorGUILayout.Toggle(new GUIContent("Double-sided (single-sided/CAD)",
            "Add a back face to every surface so single-sided or CAD 'sketch' meshes don't render invisible in-game (the " +
            "engine culls backfaces). Enable for models with missing / see-through parts — e.g. a hovercraft skirt. " +
            "Doubles the triangle count."), cur.doubleSided);
        // Albedo tone (baked into the atlas). The injection path ships a FLAT albedo — the donor's PBR normal/metallic/
        // roughness maps are neutralized so its camo can't bleed onto our model — so a skin that relied on shiny metal,
        // or a dark/washed-out texture, reads muddy in-game. These lift it at bake time (1.0 = unchanged). Slider ranges
        // are generous headroom; the number box takes exact values. No re-import needed — tweak + re-bake to preview.
        cur.albedoBrightness = EditorGUILayout.Slider(new GUIContent("Albedo brightness",
            "Multiply the baked atlas RGB. 1 = unchanged; >1 lifts a dark skin (the in-game look is flat albedo with the " +
            "donor's PBR neutralized, so shiny/dark models come out muddy). Baked into the atlas — re-bake to apply."), cur.albedoBrightness <= 0f ? 1f : cur.albedoBrightness, 0.5f, 2f);
        cur.albedoSaturation = EditorGUILayout.Slider(new GUIContent("Albedo saturation",
            "Colour vividness of the baked skin. 1 = unchanged, 0 = greyscale, >1 = punchier. Fixes a washed-out/" +
            "desaturated albedo (the game's lighting can't add colour back). Baked into the atlas — re-bake to apply."), cur.albedoSaturation < 0f ? 1f : cur.albedoSaturation, 0f, 2f);
        cur.keepBlack = EditorGUILayout.Toggle(new GUIContent("Keep black (glass/cockpit)",
            "MULTI-MATERIAL models only. By default the bake repaints near-black atlas regions neutral grey to hide UV " +
            "dead-zones and packing gaps (which would render as black patches). That also flattens an INTENTIONALLY black " +
            "material — a glossy canopy, a dark cockpit — to grey. Tick this to keep true black on such a model. Re-bake to apply."), cur.keepBlack);
        if (cur.atlasMaxDim <= 0) cur.atlasMaxDim = 512;   // default / migrate old registries
        cur.atlasMaxDim = EditorGUILayout.IntPopup(new GUIContent("Atlas size",
            "Longest side of the baked atlas, in pixels. The atlas is DXT1-compressed and saved to the shipped _Atlas.asset, " +
            "so SMALLER = smaller mod bundle. A unit is ~80px at map zoom (and its info card uses your 2D portrait, not the " +
            "model), so 512-1024 is plenty; pick 2048 only for a unit you zoom in on closely. Re-bake to apply."),
            cur.atlasMaxDim, new[] { new GUIContent("256"), new GUIContent("512"), new GUIContent("1024"), new GUIContent("2048") }, new[] { 256, 512, 1024, 2048 });
        cur.materialMode = (MaterialMode)EditorGUILayout.EnumPopup(new GUIContent("Material mode",
            "How the bake handles a model with MORE THAN ONE material. Auto = pack a multi-material atlas when the model has >1 " +
            "material, else a single texture (right for most). Single = force one texture — correct for CLOSED models (tanks, " +
            "planes) sharing a skin. Multi = force the multi-material atlas — needed for OPEN kit (a towed gun's wheels/legs/" +
            "barrel each on their own material) where the wheel would otherwise sample the wrong texture. Costs atlas space. Re-bake to apply."),
            cur.materialMode);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Runtime — applied on load, no re-bake", EditorStyles.miniBoldLabel);
        // Hide donor meshes — runtime field. The donor's fragment names only exist at runtime, so Pick reads them back
        // from the BepInEx log (the plugin dumps "[Uni] <name> donor fragment[i] mesh='...'" once per launch).
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.hideMeshes = EditorGUILayout.TextField(new GUIContent("Hide donor meshes",
                "RUNTIME, not baked. Comma-separated name substrings of the DONOR unit's extra parts to hide on this unit — " +
                "e.g. 'Rotor' to remove the attack-helicopter rotor from a drone. Leave EMPTY to keep them (a custom " +
                "helicopter can borrow the donor's spinning rotor by leaving this blank). Use Pick to choose from the donor " +
                "fragments the plugin logged (launch the game once first). Takes effect on reload — no re-bake. " +
                "NOTE: only hides FRAGMENT-based extras; a donor's animated skinned sub-parts (helicopter rotor, spinning " +
                "wheels) are encoded at pawn-spawn and can't be hidden — pick a donor without them."), cur.hideMeshes ?? "");
            if (GUILayout.Button(new GUIContent("Pick", "List the donor's fragment meshes the plugin logged for this resource. Launch the game once with the model injected first."), GUILayout.Width(70)))
            {
                var r = GUILayoutUtility.GetLastRect();
                var frags = ReadDonorFragments(cur.resourceName);
                if (frags.Count == 0)
                    EditorUtility.DisplayDialog("Hide donor meshes",
                        $"No donor fragment meshes have been logged for '{cur.resourceName}' yet.\n\n" +
                        "Launch the game once with this model injected — the plugin writes the donor's fragment mesh names " +
                        "to BepInEx\\LogOutput.log — then click Pick again. (If the donor has no extra fragments, there's " +
                        "nothing to hide and you can leave this empty.)", "OK");
                else
                {
                    var arr = frags.ToArray();
                    new StringDropdown(new AdvancedDropdownState(), arr, arr, "Donor fragments", m =>
                    {
                        var set = (cur.hideMeshes ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        if (!set.Contains(m)) set.Add(m);
                        cur.hideMeshes = string.Join(",", set);
                        Repaint();
                    }).Show(r);
                }
            }
        }
        cur.respawnAfterLoad = EditorGUILayout.Toggle(new GUIContent("Re-spawn after load (borrowed rotor fix)",
            "FIX for the save-load first-instance rotor bug: on a save-load the engine draws the FIRST custom-helicopter " +
            "pawn with its borrowed donor rotor ~1 unit low; anything (re)built after load is correct. Tick this and the " +
            "plugin re-runs the game's own pawn rebuild on this model's units ~3s after load, clearing it (a brief one-time " +
            "flicker as they rebuild). Tick ONLY for models that borrow a donor's animated sub-part (a spinning rotor); " +
            "pointless flicker otherwise."), cur.respawnAfterLoad);

        cur.freezeDonorAnim = EditorGUILayout.Toggle(new GUIContent("Freeze donor animation",
            "Stop the DONOR's idle/move animation from bobbing your STATIC mesh. A borrowed mesh inherits the donor's pose " +
            "wiggle (e.g. a Recon-Drone donor's hover bob looks wrong on a big airship). Tick this and the plugin pins every " +
            "pose's time to frame 0 each frame, holding the mesh rigid — it still glides tile-to-tile (that's transform-driven, " +
            "not animation). Static models only; animated models play their own baked clip. No re-bake, just rebuild the mod."),
            cur.freezeDonorAnim);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Texture / import", EditorStyles.miniBoldLabel);
        cur.reuseExtracted = EditorGUILayout.Toggle(new GUIContent("Keep extracted texture (hand-edits)",
            "Protect the extracted albedo from being regenerated, so a hand-edited texture (e.g. in paint.net) survives " +
            "re-bakes. ANIMATED models: this is the checkbox's ONLY effect — geometry is re-processed automatically " +
            "whenever a relevant setting changes (rotation, tris, clip, bones, material, model file), so rotation etc. " +
            "always respond. STATIC models: also reuses the extracted OBJ (skip re-import, fast iteration)."), cur.reuseExtracted);

        EditorGUILayout.Space();
        // A brand-new resource (<New>) has nothing to re-bake, so it also needs a model file; an existing one may leave
        // Model file empty to re-bake with new settings. Missing either target field greys Bake out with a reason.
        bool isNew = selected <= 0;
        // The resource name becomes a folder name, a filename prefix, and a converter argument — a space or other
        // path-hostile char breaks the bake (a space made glbconv parse the next word as the grid int). Require a
        // single token so the modder is told BEFORE baking, not by a cryptic shell-out error after.
        char badChar = '\0';
        foreach (char c in cur.resourceName ?? "")
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) { badChar = c; break; }
        bool nameValid = badChar == '\0';
        bool canBake = !string.IsNullOrWhiteSpace(cur.resourceName)
                    && nameValid
                    && !string.IsNullOrWhiteSpace(cur.pawnDescription)
                    && (!isNew || !string.IsNullOrWhiteSpace(cur.modelFile));
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!canBake))
                if (GUILayout.Button("Bake", GUILayout.Height(34))) DoBake();
            if (GUILayout.Button("Reset", GUILayout.Height(34), GUILayout.Width(72))) { cur = new ModelDef(); selected = 0; status = ""; GUI.FocusControl(null); }
        }
        if (!canBake)
            EditorGUILayout.HelpBox(
                !nameValid && !string.IsNullOrWhiteSpace(cur.resourceName)
                    ? $"Resource name can't contain '{(badChar == ' ' ? "space" : badChar.ToString())}'. Use letters, digits, '_' or '-' only — e.g. 'AttackHelicopter'."
                : isNew ? "New resource: set Resource name, Pawn description and a Model file to bake."
                        : "Set Resource name and Pawn description to bake.", MessageType.Warning);

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        DrawPreview();
        EditorGUILayout.HelpBox(
            "Bake imports the model, bakes a skeleton + atlas, and writes the JSON registry the in-game plugin reads.\n" +
            "• Formats: GLB / glTF / OBJ / FBX, and .blend (auto-converted via installed Blender).\n" +
            "• Model file empty = re-bake the existing resource with new settings (fast iteration).\n" +
            "• Normals: KeepModel = the artist's; Recalculate = hard edges via smoothing angle; Faceted = fully flat.\n" +
            "• Convert grid: 0 keeps UV seams (textured models); >0 decimates (heavy untextured meshes).\n" +
            "Registry: " + ModelRegistry.RegistryPath, MessageType.None);
        using (new EditorGUILayout.HorizontalScope())
        {
            // One-click way to reach the config folder (enc_models.json + the plugin's .cfg live here).
            if (GUILayout.Button("Open config folder", GUILayout.Width(150)))
                EditorUtility.RevealInFinder(System.IO.File.Exists(ModelRegistry.RegistryPath)
                    ? ModelRegistry.RegistryPath : ModelRegistry.ConfigDir);
            GUILayout.Label("↑ enc_models.json + the plugin .cfg", EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    // Game-path settings: auto-detected Humankind BepInEx/config, with a manual override for odd install layouts.
    void DrawSettings()
    {
        string resolved = ModelRegistry.ConfigDir;
        bool exists = System.IO.Directory.Exists(resolved);
        bool blenderOk = UniversalBaker.BlenderAvailable();
        // Header shows a marker even when collapsed, so a missing game path OR missing Blender is always visible.
        string mark = (!exists ? "  ⚠ game path" : "") + (!blenderOk ? (exists ? "  ⚠ Blender not detected" : " & Blender") : "");
        showSettings = EditorGUILayout.Foldout(showSettings, "Settings — game & Blender path" + mark, true);
        if (!showSettings) return;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            string auto = ModelRegistry.AutoDetectConfigDir();
            EditorGUILayout.LabelField("Auto-detected", string.IsNullOrEmpty(auto) ? "(Humankind not found via Steam)" : auto, EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                string ov = EditorGUILayout.TextField(new GUIContent("Override", "Leave empty to use auto-detect. Point at <Humankind>/BepInEx/config if detection misses your install."), ModelRegistry.ConfigDirOverride);
                if (ov != ModelRegistry.ConfigDirOverride) ModelRegistry.ConfigDirOverride = ov;
                if (GUILayout.Button("Browse", GUILayout.Width(70)))
                {
                    string p = EditorUtility.OpenFolderPanel("Select Humankind BepInEx/config", resolved, "");
                    if (!string.IsNullOrEmpty(p)) { ModelRegistry.ConfigDirOverride = p; GUI.FocusControl(null); }
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(ModelRegistry.ConfigDirOverride)))
                    if (GUILayout.Button("Clear", GUILayout.Width(56))) { ModelRegistry.ConfigDirOverride = ""; GUI.FocusControl(null); }
            }
            EditorGUILayout.HelpBox("Registry target:\n" + ModelRegistry.RegistryPath +
                (exists ? "" : "\n(folder doesn't exist yet — created on Bake; check the path if this looks wrong)"),
                exists ? MessageType.None : MessageType.Warning);
        }

        // --- Blender: needed for animated import, .blend import, and Reduce-to-tris. Show status + an in-UI override. ---
        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            string found = UniversalBaker.FindBlender();
            EditorGUILayout.LabelField("Blender", blenderOk ? found : "⚠ not detected", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                string bov = EditorPrefs.GetString("ENC.blenderPath", "");
                string nb = EditorGUILayout.TextField(new GUIContent("Override (blender.exe)", "Leave empty to auto-detect the newest install under Program Files. Set this if Blender is elsewhere or only on PATH."), bov);
                if (nb != bov) EditorPrefs.SetString("ENC.blenderPath", nb ?? "");
                if (GUILayout.Button("Browse", GUILayout.Width(70)))
                {
                    string p = EditorUtility.OpenFilePanel("Select blender.exe", "", "exe");
                    if (!string.IsNullOrEmpty(p)) { EditorPrefs.SetString("ENC.blenderPath", p); GUI.FocusControl(null); }
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(EditorPrefs.GetString("ENC.blenderPath", ""))))
                    if (GUILayout.Button("Clear", GUILayout.Width(56))) { EditorPrefs.SetString("ENC.blenderPath", ""); GUI.FocusControl(null); }
            }
            EditorGUILayout.HelpBox(blenderOk
                ? "Used for animated import, .blend import, and Reduce-to-tris. GLB/OBJ/FBX static bakes work without it."
                : "Not found — animated import, .blend import, and Reduce-to-tris will fail. Install Blender or point the override at blender.exe. Static GLB/OBJ/FBX bakes still work (glbconv), and 'Convert grid' decimates without Blender.",
                blenderOk ? MessageType.None : MessageType.Warning);
        }
    }

    // An entry that CARRIES animation config is an animated entry, whatever its (window-state-fragile) 'animated'
    // bool says: a named clip, animation behaviors, bone filter, or an actually-baked clip GUID. Used to self-heal
    // the flag so a stale unticked checkbox can't silently downgrade a working animated unit to a static bake
    // (the "howitzers on their side" incident).
    internal static bool LooksAnimated(ModelDef d) =>
        !string.IsNullOrWhiteSpace(d.animClip) || d.fireOnAttack || d.deployOnStop || d.convertRig || d.animStateDriven ||
        !string.IsNullOrWhiteSpace(d.animateBones) ||
        (d.clip != null && d.clip.Length == 4 && !(d.clip[0] == 0 && d.clip[1] == 0 && d.clip[2] == 0 && d.clip[3] == 0));

    void OnSelectResource()
    {
        if (selected <= 0) { cur = new ModelDef(); status = ""; LoadPreview(null); return; }
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == existing[selected]);
        if (e == null) return;
        cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone so edits don't mutate the stored copy
        status = "Loaded '" + e.resourceName + "'. Edit + Bake; leave Model file empty to re-bake with new settings.";
        if (!cur.animated && LooksAnimated(cur))
        {
            cur.animated = true;   // self-heal: the entry carries animation config, so it IS animated
            status += "\nRe-marked ANIMATED (the entry carries a clip/animation behaviors — the flag had been lost).";
        }
        LoadPreview(cur.resourceName);
    }

    // Re-probe only when the model-file path changes (OnGUI runs every frame; file I/O must not).
    void EnsureAnimProbe(string file)
    {
        file = file ?? "";
        if (file == animProbeFile) return;
        animProbeFile = file;
        animProbeState = ProbeAnimation(file);
        (animClips, animBonePrefixes) = InspectModel(file);   // populate the Clip / Bones pickers
    }

    // Read clip names + bone-name prefixes from the model, for the Pick dropdowns. glTF/GLB only (no Blender); returns
    // empties for FBX/.blend (fields stay manual). Primary parse is Newtonsoft (SDK-provided; robust on any valid glTF,
    // where JsonUtility silently fails) — it maps skin joints -> node names so bone COUNTS are accurate. A scoped
    // bracket-matching fallback (NamesInArray) handles truncated/odd JSON with no dependency. Clips = animations[].name;
    // bones grouped into prefixes (text before the first _ . - or space) with counts.
    // Guess the "Fix 100× oversize (FBX unit scale)" default from the model's TRUE final size (POSITION accessor extent ×
    // node world-scale, exactly what glbconv would report). Rationale (proven on 2 models, mechanism-backed): a metre-scale
    // model (~2u gun) hits the metre→cm FBX-unit issue and needs the fix ON; a tiny-authored model (a GLB with a 0.01 root
    // node scale → ~0.0025u, e.g. the drone) is re-inflated by Blender's FBX export and bakes correct with the fix OFF.
    // Best-effort: glTF/GLB only (FBX/.blend/OBJ can't be read cheaply → sz=0, caller keeps the existing value). Node
    // `matrix` transforms are not decomposed (→ sz=0, no guess) — most rigged glTF use TRS.
    internal static bool SuggestUnitFix(string file, out float trueSize)
    {
        trueSize = 0f;
        try
        {
            string ext = System.IO.Path.GetExtension(file ?? "").ToLowerInvariant();
            string json = ext == ".glb" ? ReadGlbJson(file) : (ext == ".gltf" ? System.IO.File.ReadAllText(file) : null);
            if (json == null) return false;
            var root = JObject.Parse(json);
            var nodes = root["nodes"] as JArray; var meshes = root["meshes"] as JArray; var accessors = root["accessors"] as JArray;
            if (nodes == null || meshes == null || accessors == null) return false;
            // largest POSITION extent of a mesh (in its own space)
            float MeshExtent(int mi)
            {
                float e = 0f;
                foreach (var prim in (meshes[mi]?["primitives"] as JArray ?? new JArray()))
                {
                    var ai = (int?)prim["attributes"]?["POSITION"]; if (ai == null || ai < 0 || ai >= accessors.Count) continue;
                    var a = accessors[ai.Value]; var mn = a["min"] as JArray; var mx = a["max"] as JArray;
                    if (mn == null || mx == null || mn.Count < 3 || mx.Count < 3) continue;
                    for (int k = 0; k < 3; k++) e = Mathf.Max(e, Mathf.Abs((float)mx[k] - (float)mn[k]));
                }
                return e;
            }
            // DFS from the scene roots, accumulating uniform-ish world scale; track max(meshExtent × worldScale)
            float best = 0f;
            var child = new HashSet<int>();
            foreach (var n in nodes) if (n["children"] is JArray ch) foreach (var c in ch) child.Add((int)c);
            var stack = new Stack<KeyValuePair<int, float>>();
            for (int i = 0; i < nodes.Count; i++) if (!child.Contains(i)) stack.Push(new KeyValuePair<int, float>(i, 1f));
            int guard = 0;
            while (stack.Count > 0 && guard++ < 100000)
            {
                var kv = stack.Pop(); var n = nodes[kv.Key];
                float ns = 1f; var s = n["scale"] as JArray;
                if (s != null && s.Count == 3) ns = ((float)s[0] + (float)s[1] + (float)s[2]) / 3f;   // uniform-ish average
                float ws = kv.Value * ns;
                var mesh = (int?)n["mesh"]; if (mesh != null && mesh >= 0 && mesh < meshes.Count) best = Mathf.Max(best, MeshExtent(mesh.Value) * ws);
                if (n["children"] is JArray chn) foreach (var c in chn) stack.Push(new KeyValuePair<int, float>((int)c, ws));
            }
            if (best <= 1e-6f) return false;   // no positional data (or matrix-only nodes) → don't guess
            trueSize = best;
            return best >= 0.1f;   // metre-scale → fix ON; tiny-authored → OFF
        }
        catch { trueSize = 0f; return false; }
    }

    // clip name -> human length ("frames 0..250 (10.4s @24fps)"), PER FILE — the Pick dropdowns append it so slicing
    // ranges (clip[start..end]) are discoverable without opening Blender. Keyed "file|clip" because SEVERAL windows
    // (Factory + Lab, both visible) inspect DIFFERENT files: a single last-writer dict got wiped by whichever window
    // inspected last, so the other window's dropdown showed plain names.
    internal static readonly Dictionary<string, string> ClipLengths = new Dictionary<string, string>();
    internal static string ClipLengthOf(string file, string clip)
        => ClipLengths.TryGetValue((file ?? "") + "|" + clip, out var len) ? len : null;

    internal static (List<string>, List<KeyValuePair<string, int>>) InspectModel(string file)
    {
        var clips = new List<string>();
        var prefixes = new List<KeyValuePair<string, int>>();
        try
        {
            if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file)) return (clips, prefixes);
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            string json = ext == ".glb" ? ReadGlbJson(file) : (ext == ".gltf" ? System.IO.File.ReadAllText(file) : null);
            if (json == null) return (clips, prefixes);
            List<string> boneNames;
            try
            {
                // Robust parse: Newtonsoft handles any valid glTF, and maps skin joints -> node names by index so bone
                // COUNTS are accurate (the real bones, not every node). (JsonUtility silently fails on real glTF.)
                var root = JObject.Parse(json);
                clips = (root["animations"] as JArray)?.Select(a => (string)a["name"])
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList() ?? new List<string>();
                // Clip LENGTHS for the Pick dropdowns — the answer to "how do I know the frame range for a
                // clip[start..end] slice?" glTF stores times in SECONDS; frames assume the Blender-standard 24 fps
                // export (verified exact on every model so far: deploy 10.417s=250f, Idle1 14.208s=341f).
                var accsJ = root["accessors"] as JArray;
                if (root["animations"] is JArray animsJ && accsJ != null)
                    foreach (var a in animsJ)
                    {
                        string nm = (string)a["name"]; if (string.IsNullOrEmpty(nm)) continue;
                        float maxSec = 0f;
                        foreach (var s in (a["samplers"] as JArray ?? new JArray()))
                        {
                            var ii = (int?)s["input"];
                            if (ii == null || ii < 0 || ii >= accsJ.Count) continue;
                            if (accsJ[ii.Value]?["max"] is JArray mxa && mxa.Count > 0)
                                maxSec = Mathf.Max(maxSec, (float)mxa[0]);
                        }
                        if (maxSec > 0f) ClipLengths[file + "|" + nm] = $"frames 0..{Mathf.RoundToInt(maxSec * 24f)}  ({maxSec:0.0}s @24fps)";
                    }
                var nodes = root["nodes"] as JArray;
                var joints = new HashSet<int>();
                if (root["skins"] is JArray skins)
                    foreach (var s in skins)
                        if (s["joints"] is JArray js)
                            foreach (var j in js) joints.Add((int)j);
                IEnumerable<string> bn = (joints.Count > 0 && nodes != null)
                    ? joints.Where(i => i >= 0 && i < nodes.Count).Select(i => (string)nodes[i]?["name"])
                    : (nodes?.Select(n => (string)n?["name"]) ?? Enumerable.Empty<string>());
                boneNames = bn.Where(n => !string.IsNullOrEmpty(n)).ToList();
            }
            catch (Exception ix)   // truncated / odd JSON -> zero-dependency bracket-matching fallback (glTF-specific)
            {
                Debug.LogWarning("[Factory] InspectModel: structured parse failed (" + ix.Message + ") — name-only fallback, no clip lengths.");
                clips = NamesInArray(json, "\"animations\"\\s*:\\s*\\[").Distinct().ToList();
                boneNames = NamesInArray(json, "\"nodes\"\\s*:\\s*\\[(?=\\s*\\{)");
            }
            prefixes = boneNames.Where(n => !string.IsNullOrEmpty(n)).GroupBy(PrefixOf)
                .Where(gr => !string.IsNullOrEmpty(gr.Key))
                .Select(gr => new KeyValuePair<string, int>(gr.Key, gr.Count()))
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
        }
        catch { }
        return (clips, prefixes);
    }

    // Collect every "name":"…" inside the JSON array opened by `openRegex` (matched through its '['), by tracking bracket
    // depth (strings respected) to find the array's matching ']'. Within that array the only "name" fields are the
    // entities' names (glTF animation channels/samplers and node transforms carry no "name").
    static List<string> NamesInArray(string json, string openRegex)
    {
        var res = new List<string>();
        var m = System.Text.RegularExpressions.Regex.Match(json, openRegex);
        if (!m.Success) return res;
        int i = m.Index + m.Length, start = i, depth = 1; bool inStr = false, esc = false;
        for (; i < json.Length && depth > 0; i++)
        {
            char c = json[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; }
            else if (c == '"') inStr = true;
            else if (c == '[' || c == '{') depth++;
            else if (c == ']' || c == '}') depth--;
        }
        string arr = json.Substring(start, System.Math.Max(0, i - 1 - start));
        foreach (System.Text.RegularExpressions.Match nm in System.Text.RegularExpressions.Regex.Matches(arr, "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
            res.Add(nm.Groups[1].Value);
        return res;
    }

    // Read the donor fragment mesh names the plugin logged for this resource, from BepInEx/LogOutput.log. The plugin
    // emits "[Uni] <name> donor fragment[i] mesh='...'" (and "HID donor fragment") once per launch. We stream the WHOLE
    // log with a shared read (the running game holds it open) — the fragments are logged early (first unit load), so on a
    // big verbose log they're nowhere near the tail; a cheap substring pre-filter keeps the full scan fast. Deduped.
    static List<string> ReadDonorFragments(string resourceName)
    {
        var res = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(resourceName)) return res;
            string log = System.IO.Path.GetFullPath(System.IO.Path.Combine(ModelRegistry.ConfigDir, "..", "LogOutput.log"));
            if (!System.IO.File.Exists(log)) return res;
            var rx = new System.Text.RegularExpressions.Regex(
                @"\[Uni\] " + System.Text.RegularExpressions.Regex.Escape(resourceName) + @" (?:HID )?donor fragment\[\d+\] mesh='([^']*)'");
            var seen = new HashSet<string>();
            // Scan the WHOLE log (shared-read, so it works while the game holds it open). The plugin logs each donor
            // fragment ONCE per session, early (first unit load) — on a big verbose log (300 MB+) that's nowhere near the
            // tail, so tailing missed it. A cheap substring pre-filter keeps the regex off the millions of non-fragment
            // lines, so a full streaming scan stays quick (disk-bound, a couple of seconds even on a huge log).
            using (var fs = new System.IO.FileStream(log, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var sr = new System.IO.StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.IndexOf("donor fragment[", System.StringComparison.Ordinal) < 0) continue;   // cheap pre-filter
                    var m = rx.Match(line);
                    if (m.Success) { var nm = m.Groups[1].Value; if (nm.Length > 0 && seen.Add(nm)) res.Add(nm); }
                }
            }
        }
        catch { }
        return res;
    }

    // Prefix = the name up to the first separator (_ . - space); "prop_1_jnt" -> "prop", "Center" -> "Center".
    static string PrefixOf(string bone)
    {
        if (string.IsNullOrEmpty(bone)) return "";
        int i = bone.IndexOfAny(new[] { '_', '.', '-', ' ' });
        return i > 0 ? bone.Substring(0, i) : bone;
    }

    // Does the model file contain a skeletal animation? 0 = unknown (can't tell cheaply → allow), 1 = yes, 2 = no.
    // Deliberately conservative: only returns 2 ("none") when we're confident (OBJ, or a glTF with no animations), so we
    // never wrongly BLOCK a rigged model; ambiguous formats (.blend) and a token-less FBX stay "unknown" (allowed).
    static int ProbeAnimation(string file)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file)) return 0;
            switch (System.IO.Path.GetExtension(file).ToLowerInvariant())
            {
                case ".obj": return 2;                                   // OBJ can't carry animation
                case ".glb": return ProbeGlb(file);
                case ".gltf": return HasGltfAnim(System.IO.File.ReadAllText(file)) ? 1 : 2;
                case ".fbx": return ProbeFbx(file) ? 1 : 0;              // token found = yes; absent = unknown (don't block)
                default: return 0;                                       // .blend etc — needs Blender to know
            }
        }
        catch { return 0; }
    }

    // Check a GLB's JSON chunk for a non-empty "animations" array.
    static int ProbeGlb(string file)
    {
        string json = ReadGlbJson(file);
        return json == null ? 0 : (HasGltfAnim(json) ? 1 : 2);
    }

    // Extract the JSON (first) chunk of a binary glTF as a string, or null if it isn't a GLB.
    static string ReadGlbJson(string file)
    {
        using (var fs = System.IO.File.OpenRead(file))
        using (var br = new System.IO.BinaryReader(fs))
        {
            if (fs.Length < 20 || br.ReadUInt32() != 0x46546C67u) return null;   // "glTF" magic
            br.ReadUInt32(); br.ReadUInt32();                                    // version, total length
            uint clen = br.ReadUInt32(); uint ctype = br.ReadUInt32();           // first chunk = JSON
            if (ctype != 0x4E4F534Au) return null;                              // "JSON"
            var bytes = br.ReadBytes((int)System.Math.Min(clen, 16u * 1024 * 1024));
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }

    // "animations":[ … ] present AND non-empty (next non-space char after '[' isn't ']').
    static bool HasGltfAnim(string json)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"animations\"\\s*:\\s*\\[");
        if (!m.Success) return false;
        int i = m.Index + m.Length;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        return i < json.Length && json[i] != ']';
    }

    // FBX (binary or ASCII) names its animation via AnimStack / AnimCurveNode object records. Scan up to a cap.
    static bool ProbeFbx(string file)
    {
        try
        {
            var data = System.IO.File.ReadAllBytes(file);
            int n = System.Math.Min(data.Length, 48 * 1024 * 1024);
            string hay = System.Text.Encoding.ASCII.GetString(data, 0, n);
            return hay.IndexOf("AnimStack", System.StringComparison.Ordinal) >= 0
                || hay.IndexOf("AnimCurveNode", System.StringComparison.Ordinal) >= 0;
        }
        catch { return false; }
    }

    static string[] pawnCache;
    internal static string[] GatherPawnNames()
    {
        if (pawnCache != null) return pawnCache;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("PresentationPawnDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.GetType().Name == "PresentationPawnDefinition" && !string.IsNullOrEmpty(o.name))
                    names.Add(o.name);
        }
        pawnCache = names.ToArray();
        return pawnCache;
    }

    // Era6_Common_StealthCruisers_01 -> "StealthCruisers" (drop a trailing numeric token). Suggested resource name.
    internal static string DeriveResourceName(string pawnName)
    {
        if (string.IsNullOrEmpty(pawnName)) return "";
        var parts = pawnName.Split('_');
        int end = parts.Length - 1;
        if (end > 0 && int.TryParse(parts[end], out _)) end--;
        return end >= 0 ? parts[end] : pawnName;
    }

    // 'Reuse extracted' is a CACHE, not a switch: these are the settings the ANIMATED Blender step consumes — if any
    // of them changed vs the saved entry, reusing the old FBX would silently ignore the change (the "rotation doesn't
    // respond" trap). The bake then re-runs the Blender step for that one bake; the ticked checkbox itself is kept as
    // the user's fast-iteration preference. A never-baked entry always slims.
    internal static bool AnimatedSlimInputsChanged(ModelDef cur)
    {
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == cur.resourceName);
        if (e == null) return true;
        return cur.rotation != e.rotation
            || cur.targetTris != e.targetTris
            || (cur.animClip ?? "") != (e.animClip ?? "")
            || (cur.animateBones ?? "") != (e.animateBones ?? "")
            || cur.materialMode != e.materialMode
            || cur.convertRig != e.convertRig
            || cur.animStateDriven != e.animStateDriven
            || (cur.animClipMove ?? "") != (e.animClipMove ?? "")
            || (cur.animClipAfter ?? "") != (e.animClipAfter ?? "")
            || (cur.animClipAttack ?? "") != (e.animClipAttack ?? "")
            || (cur.animClipCombat ?? "") != (e.animClipCombat ?? "")
            || (cur.animClipPreMove ?? "") != (e.animClipPreMove ?? "")
            || (cur.animClipIdle ?? "") != (e.animClipIdle ?? "")
            || (cur.animClipIdleAlt ?? "") != (e.animClipIdleAlt ?? "")
            || (cur.animClipIdleAlt2 ?? "") != (e.animClipIdleAlt2 ?? "")
            || (cur.modelFile ?? "") != (e.modelFile ?? "");
    }

    // Map a registry ModelDef to a BakeConfig. SHARED so the bake smoke test (BakeSmokeTest.cs) bakes through the exact
    // same config path as the Bake button — a parallel copy would silently drift from what ships.
    internal static BakeConfig ConfigFor(ModelDef cur) => new BakeConfig
    {
        resourceName = cur.resourceName, modelFile = cur.modelFile, pawnDescription = cur.pawnDescription,
        rotationEuler = cur.rotation, positionOffset = cur.position, size = cur.size,
        normals = (NormalsMode)cur.normalsMode, smoothingAngle = cur.smoothingAngle, convertGrid = cur.convertGrid,
        reuseExtracted = cur.reuseExtracted, doubleSided = cur.doubleSided, windingFix = cur.windingFix, heightUV = cur.heightUV, targetTris = cur.targetTris,
        albedoBrightness = cur.albedoBrightness, albedoSaturation = cur.albedoSaturation, keepBlack = cur.keepBlack, materialMode = cur.materialMode,
        atlasMaxDim = cur.atlasMaxDim <= 0 ? 512 : cur.atlasMaxDim,
        stripParts = cur.stripParts,
        animated = cur.animated, animClip = (cur.animClip ?? "").Trim(), animateBones = (cur.animateBones ?? "").Trim(), animUnitFix = cur.animUnitFix, convertRig = cur.convertRig,
        deployConvert = cur.deployConvert, deployStart = cur.deployStart, deployEnd = cur.deployEnd,
        deployStrip = (cur.deployStrip ?? "").Trim(), deployReadyFrame = (cur.deployReadyFrame ?? "").Trim(), deployLegScale = (cur.deployLegScale ?? "").Trim(), deployBarrelScale = (cur.deployBarrelScale ?? "").Trim(),
        deployRecoil = (cur.deployRecoil ?? "").Trim(), deployRecoilStep = (cur.deployRecoilStep ?? "").Trim(), deployRecoilMag = (cur.deployRecoilMag ?? "").Trim(), deployArcR = (cur.deployArcR ?? "").Trim(), deployRecoilReturn = (cur.deployRecoilReturn ?? "").Trim(), deploySlamDeg = (cur.deploySlamDeg ?? "").Trim(), deploySlamSettle = (cur.deploySlamSettle ?? "").Trim(),
        animStateDriven = cur.animStateDriven, animClipMove = (cur.animClipMove ?? "").Trim(), animClipAfter = (cur.animClipAfter ?? "").Trim(), animClipAttack = (cur.animClipAttack ?? "").Trim(), animClipCombat = (cur.animClipCombat ?? "").Trim(), animClipPreMove = (cur.animClipPreMove ?? "").Trim(), animClipIdle = (cur.animClipIdle ?? "").Trim(), animClipIdleAlt = (cur.animClipIdleAlt ?? "").Trim(), animClipIdleAlt2 = (cur.animClipIdleAlt2 ?? "").Trim(),
        keepTexture = cur.reuseExtracted   // on the ANIMATED path the checkbox's ONLY meaning is 'protect the hand-edited extracted texture'
    };

    void DoBake()
    {
        // Tear down the preview editor BEFORE baking. The baked prefab has an Animator, so the preview is a GameObjectInspector
        // with a live animator preview; the bake's delete-first (DeleteAsset _Model.prefab) would then null its target mid-bake,
        // and SaveAsPrefabAsset's OnPostprocessAllAssets fires InstantiateForAnimatorPreview(null) -> ArgumentException. Destroying
        // it up front means nothing watches the prefab while it's deleted; we rebuild the preview after the bake.
        LoadPreview(null);
        AnimationLabWindow.InvalidateFitPreviews();   // the Lab's combined fit view would go stale (magenta) on this bake — retire it; its Refresh rebuilds
        // Trim the text fields ON cur ITSELF, not just into the bake config: Upsert(cur) below persists cur, and a
        // pasted trailing space in pawnDescription used to bake fine but write the untrimmed string to the registry —
        // the plugin's substring match then never fired: "Baked ✓", model silently never injected (review finding E1).
        // Trimming cur keeps what's baked and what's registered identical.
        cur.resourceName = (cur.resourceName ?? "").Trim();
        cur.pawnDescription = (cur.pawnDescription ?? "").Trim();
        cur.modelFile = (cur.modelFile ?? "").Trim();
        cur.stripParts = (cur.stripParts ?? "").Trim();
        cur.hideMeshes = (cur.hideMeshes ?? "").Trim();
        cur.animClip = (cur.animClip ?? "").Trim();
        cur.animateBones = (cur.animateBones ?? "").Trim();
        // ENFORCED OWNERSHIP (mirror of AnimationLabWindow.RebaseOnRegistry): the ANIMATION fields belong to the
        // Animation Lab — before baking, always take their freshest saved values from the registry so a stale Factory
        // copy can't clobber what the Lab configured (a Factory bake once silently dropped the Lab's Fix-100×,
        // shipping a 100×-giant soldier). This window contributes everything else (model file, transform, size, …).
        {
            var regE = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == cur.resourceName);
            if (regE != null)
            {
                cur.animClip = regE.animClip; cur.animateBones = regE.animateBones; cur.animUnitFix = regE.animUnitFix;
                cur.convertRig = regE.convertRig;
                cur.deployConvert = regE.deployConvert; cur.deployStart = regE.deployStart; cur.deployEnd = regE.deployEnd;
                cur.deployStrip = regE.deployStrip; cur.deployReadyFrame = regE.deployReadyFrame; cur.deployLegScale = regE.deployLegScale; cur.deployBarrelScale = regE.deployBarrelScale;
                cur.deployRecoil = regE.deployRecoil; cur.deployRecoilStep = regE.deployRecoilStep; cur.deployRecoilMag = regE.deployRecoilMag; cur.deployArcR = regE.deployArcR; cur.deployRecoilReturn = regE.deployRecoilReturn; cur.deploySlamDeg = regE.deploySlamDeg; cur.deploySlamSettle = regE.deploySlamSettle;
                cur.animStateDriven = regE.animStateDriven; cur.animClipMove = regE.animClipMove; cur.animClipAfter = regE.animClipAfter; cur.animClipAttack = regE.animClipAttack; cur.animClipCombat = regE.animClipCombat; cur.animClipPreMove = regE.animClipPreMove; cur.animClipIdle = regE.animClipIdle; cur.animClipIdleAlt = regE.animClipIdleAlt; cur.animClipIdleAlt2 = regE.animClipIdleAlt2;
                cur.clipMove = regE.clipMove; cur.clipAfter = regE.clipAfter; cur.clipAttack = regE.clipAttack; cur.clipCombat = regE.clipCombat; cur.clipPreMove = regE.clipPreMove; cur.clipIdle = regE.clipIdle; cur.clipIdleAlt = regE.clipIdleAlt; cur.clipIdleAlt2 = regE.clipIdleAlt2; cur.idleAltInterval = regE.idleAltInterval;
                cur.attackRepeats = regE.attackRepeats; cur.clearAimLayer = regE.clearAimLayer;
                cur.handPropName = regE.handPropName; cur.handPropGuid = regE.handPropGuid; cur.handPropMat = regE.handPropMat; cur.handPropBone = regE.handPropBone; cur.handPropAngles = regE.handPropAngles;
                cur.fireOnAttack = regE.fireOnAttack; cur.deployOnStop = regE.deployOnStop;
                cur.deployPoseTime = regE.deployPoseTime; cur.deploySpeed = regE.deploySpeed; cur.recoilSpeed = regE.recoilSpeed;
                // Unit Retexture / Unit Sound ownership — same rule as the Lab fields: this window can't even display
                // these, so it must never write its stale clone of them. Without this, a Factory re-bake silently
                // reverted a skin/tint/engine-sound configured after the entry was loaded here (review 2026-07-19).
                cur.desaturate = regE.desaturate; cur.tintR = regE.tintR; cur.tintG = regE.tintG; cur.tintB = regE.tintB;
                cur.textureFile = regE.textureFile;
                cur.engineSound = regE.engineSound; cur.engineStartEvent = regE.engineStartEvent; cur.engineStopEvent = regE.engineStopEvent;
                cur.soundFile = regE.soundFile; cur.soundStartFile = regE.soundStartFile; cur.soundStopFile = regE.soundStopFile;
                cur.soundVolume = regE.soundVolume; cur.soundStartVolume = regE.soundStartVolume; cur.soundStopVolume = regE.soundStopVolume;
                if (regE.animated) cur.animated = true;
            }
        }
        // GUARD against a silent animated->static downgrade (the "howitzers on their side" incident). Two layers:
        // (1) the ENTRY carries animation config (clip/behaviors) -> it IS animated; self-heal the flag, no dialog.
        // (2) only the FILE has animation (a fresh rigged model, no config yet) -> unticked may be deliberate; ask.
        // A static bake would strip clip + behaviors and bake the (animated-path-ignored) Rotation offset into the
        // mesh, so the unit renders tipped over — never let that happen silently.
        EnsureAnimProbe(cur.modelFile);
        if (!cur.animated && LooksAnimated(cur))
        {
            cur.animated = true;
            Debug.Log("[Factory] " + cur.resourceName + ": re-marked ANIMATED before bake (entry carries animation config).");
        }
        if (!cur.animated && animProbeState == 1 &&
            !EditorUtility.DisplayDialog("Bake static?",
                "This model contains animation, but 'Animated (own rig + clip)' is UNTICKED.\n\n" +
                "Baking now produces a STATIC model: no clip, no fire/deploy behaviors, and the Rotation offset gets " +
                "baked into the mesh.\n\nBake static anyway?",
                "Bake static", "Cancel"))
        { status = "Bake cancelled — tick 'Animated (own rig + clip)' to bake the animated version."; return; }
        var cfg = ConfigFor(cur);
        if (cfg.animated)
        {
            // Geometry caching is AUTOMATIC on the animated path: the Blender step re-runs exactly when one of its
            // inputs changed (rotation/tris/clip/bones/material/model), regardless of the checkbox — the checkbox's
            // only meaning here is 'keep the hand-edited extracted texture' (cfg.keepTexture, set in ConfigFor).
            cfg.reuseExtracted = !AnimatedSlimInputsChanged(cur);
            if (!cfg.reuseExtracted) Debug.Log("[Factory] " + cur.resourceName + ": Blender-step settings changed — re-slimming (automatic).");
        }
        var r = cfg.animated ? UniversalBaker.BuildAnimated(cfg) : UniversalBaker.Build(cfg);
        if (!r.ok) { status = "Bake FAILED: " + r.error; return; }
        cur.skel = ModelRegistry.ParseGuid(r.skeletonGuid);
        cur.atlas = ModelRegistry.ParseGuid(r.atlasGuid);
        cur.clip = cfg.animated ? ModelRegistry.ParseGuid(r.clipGuid) : new int[4];   // static models carry {0,0,0,0}
        cur.clipMove = cfg.animated && cfg.animStateDriven ? ModelRegistry.ParseGuid(r.clipMoveGuid) : new int[4];
        cur.clipAfter = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipAfterGuid) ? ModelRegistry.ParseGuid(r.clipAfterGuid) : new int[4];
        cur.clipAttack = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipAttackGuid) ? ModelRegistry.ParseGuid(r.clipAttackGuid) : new int[4];
        cur.clipCombat = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipCombatGuid) ? ModelRegistry.ParseGuid(r.clipCombatGuid) : new int[4];
        cur.clipPreMove = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipPreMoveGuid) ? ModelRegistry.ParseGuid(r.clipPreMoveGuid) : new int[4];
        cur.clipIdle = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipIdleGuid) ? ModelRegistry.ParseGuid(r.clipIdleGuid) : new int[4];   // was DROPPED — a Factory bake shipped a dead idle-override GUID (the "forgot to deploy" trap); mirrors the Lab's capture
        cur.clipIdleAlt = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipIdleAltGuid) ? ModelRegistry.ParseGuid(r.clipIdleAltGuid) : new int[4];
        cur.clipIdleAlt2 = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipIdleAlt2Guid) ? ModelRegistry.ParseGuid(r.clipIdleAlt2Guid) : new int[4];
        bool saved = ModelRegistry.Upsert(cur);
        RefreshList();
        selected = System.Array.IndexOf(existing, cur.resourceName); if (selected < 0) selected = 0;
        if (!saved)
        {
            // The asset baked, but writing the registry entry failed (Save logged why). Say so plainly instead of a false
            // "Baked ✓" — otherwise the user assumes it's registered when the plugin will never see it. Re-bake retries.
            status = $"Baked '{cur.resourceName}', but the REGISTRY SAVE FAILED (see Console). The asset is baked; close " +
                     "whatever's locking enc_models.json (AV / indexer / the running game) and re-bake to write the entry.";
            Debug.LogError("[Factory] " + status);
            LoadPreview(cur.resourceName, forceReimport: true);
            return;
        }
        status = cfg.animated
            ? $"Baked ANIMATED '{cur.resourceName}' -> '{cur.pawnDescription}'\nskeleton {r.skeletonGuid}\nclip {r.clipGuid}\nNow rebuild the mod + relaunch."
            : $"Baked '{cur.resourceName}' -> '{cur.pawnDescription}'  (raw bbox {r.bbox})\nskeleton {r.skeletonGuid}\nNow rebuild the mod + relaunch.";
        Debug.Log("[Factory] " + status);
        LoadPreview(cur.resourceName, forceReimport: true);   // show the just-baked model (force reimport so it isn't stale)
        AnimationLabWindow.RebuildFitPreviews();              // rebuild the Lab's (fit) preview from the fresh assets
    }
}

// Searchable popup of every PresentationPawnDefinition name (built-in search box). Picking sets the pawn description.
class PawnDropdown : AdvancedDropdown
{
    readonly string[] names; readonly Action<string> onPick; readonly Dictionary<int, string> map = new Dictionary<int, string>();
    public PawnDropdown(AdvancedDropdownState s, string[] names, Action<string> onPick) : base(s)
    { this.names = names; this.onPick = onPick; minimumSize = new Vector2(300, 420); }
    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem("Pawn definitions (" + names.Length + ")");
        foreach (var n in names) { var it = new AdvancedDropdownItem(n); root.AddChild(it); map[it.id] = n; }
        return root;
    }
    protected override void ItemSelected(AdvancedDropdownItem item) { if (map.TryGetValue(item.id, out var n)) onPick(n); }
}

// Searchable dropdown over parallel label/value arrays (label shown, value returned). Used for the Clip and Bone pickers.
class StringDropdown : AdvancedDropdown
{
    readonly string[] labels, values; readonly string title; readonly Action<string> onPick;
    readonly Dictionary<int, string> map = new Dictionary<int, string>();
    public StringDropdown(AdvancedDropdownState s, string[] labels, string[] values, string title, Action<string> onPick) : base(s)
    { this.labels = labels; this.values = values; this.title = title; this.onPick = onPick; minimumSize = new Vector2(260, 320); }
    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem(title + " (" + labels.Length + ")");
        for (int i = 0; i < labels.Length; i++) { var it = new AdvancedDropdownItem(labels[i]); root.AddChild(it); map[it.id] = values[i]; }
        return root;
    }
    protected override void ItemSelected(AdvancedDropdownItem item) { if (map.TryGetValue(item.id, out var v)) onPick(v); }
}
