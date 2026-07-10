// ModelFactoryWindow.cs (ENC editor) — Tools > Universal Model Factory.
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

    [MenuItem("Tools/Universal Model Factory")]
    static void Open()
    {
        var w = GetWindow<ModelFactoryWindow>(false, "Universal Model Factory");
        w.minSize = new Vector2(500, 470);
        w.RefreshList();
    }

    void OnEnable() { RefreshList(); LoadPreview(cur.resourceName); }
    void OnDisable() { if (previewEditor != null) { UnityEngine.Object.DestroyImmediate(previewEditor); previewEditor = null; } }

    // Load the baked prefab (animated <name>_Preview, else static <name>_Model) and build an interactive preview editor.
    // forceReimport: after a bake, the static path overwrites the mesh/prefab IN PLACE, so Unity can serve the preview a
    // stale cached copy until a manual reimport. Force a synchronous reimport of the mesh + prefab so the preview is current.
    void LoadPreview(string name, bool forceReimport = false)
    {
        if (previewEditor != null) { UnityEngine.Object.DestroyImmediate(previewEditor); previewEditor = null; }
        previewFor = name ?? "";
        if (string.IsNullOrEmpty(name)) return;
        string animPath = "Assets/Resources/" + name + "/" + name + "_Preview.prefab";
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
        var names = ModelRegistry.Load().Select(e => e.resourceName).ToList();
        names.Insert(0, "<New>");
        existing = names.ToArray();
        if (selected >= existing.Length) selected = 0;
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        // Widen the label column so the longer labels ("Position offset (Z = waterline)", "Double-sided (single-sided/CAD)",
        // "Animated (own rig + clip)") aren't clipped. Scales with window width so fields still get room when widened.
        EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.42f, 210f, 320f);
        GUILayout.Space(10f);
        EditorGUILayout.LabelField("Universal Model Factory", EditorStyles.boldLabel);
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
                    var name = cur.resourceName;
                    if (!string.IsNullOrEmpty(name) &&
                        EditorUtility.DisplayDialog("Remove model",
                            $"Remove '{name}' from the registry? The plugin will stop injecting it on next launch. " +
                            "(The baked skeleton/atlas assets stay in the project.)", "Remove", "Cancel"))
                    {
                        ModelRegistry.Remove(name);
                        selected = 0; cur = new ModelDef(); RefreshList(); GUI.FocusControl(null);
                        status = $"Removed '{name}' from the registry.";
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
                var p = EditorUtility.OpenFilePanel("Select 3D model", "", "glb,gltf,obj,fbx,blend");
                if (!string.IsNullOrEmpty(p)) cur.modelFile = p;
            }
        }
        if ((cur.modelFile ?? "").ToLowerInvariant().EndsWith(".blend") && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox(".blend import needs Blender installed (auto-detected). Install it, or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Animation", EditorStyles.miniBoldLabel);
        EnsureAnimProbe(cur.modelFile);
        bool noAnim = animProbeState == 2;
        if (noAnim) cur.animated = false;   // a static model can't drive the animated path
        using (new EditorGUI.DisabledScope(noAnim))
            cur.animated = EditorGUILayout.Toggle(new GUIContent("Animated (own rig + clip)",
            "Bake from the model's OWN armature + animation clip so it plays its own motion in-game (e.g. a drone's " +
            "propellers spin) — instead of the static single-bone vehicle rig. The model MUST be rigged with a skeletal " +
            "animation (glb/fbx/blend). Blender slims it (join + decimate, keep the armature + first clip), Unity bakes an " +
            "Amplitude Skeleton + ClipCollection, and the plugin drives the pawn's pose onto it. Needs Blender (auto-detected)."), cur.animated);
        // Clip name — free text + a Pick button that lists the clips read from the model (glb/gltf; no Blender).
        using (new EditorGUI.DisabledScope(!cur.animated))
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.animClip = EditorGUILayout.TextField(new GUIContent("Clip name",
                "Which animation to bake when the model has several clips — e.g. 'hover'. Use Pick to choose from the clips " +
                "found in the model. Leave EMPTY to use the model's assigned/first clip."), cur.animClip ?? "");
            using (new EditorGUI.DisabledScope(animClips.Count == 0))
                if (GUILayout.Button(new GUIContent("Pick", animClips.Count == 0 ? "No clips readable from this model (glb/gltf only) — type the name" : null), GUILayout.Width(70)))
                {
                    var r = GUILayoutUtility.GetLastRect();
                    var arr = animClips.ToArray();
                    new StringDropdown(new AdvancedDropdownState(), arr, arr, "Clips", n => { cur.animClip = n; Repaint(); }).Show(r);
                }
        }
        // Animate only bones — free text + a Pick that appends a bone-name prefix (grouped, with counts) from the model.
        using (new EditorGUI.DisabledScope(!cur.animated))
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
        // Blender is a HARD dependency for the animated path (rig-slim + clip bake); glbconv can't emit a rigged FBX.
        // Warn as soon as an animated model is detected — not only after ticking — so a Blender-less adopter knows upfront.
        // (Detection itself needs no Blender, so the checkbox stays usable; only Bake will fail until Blender is present.)
        if ((cur.animated || animProbeState == 1) && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("The Animated path needs Blender (to slim the rig + bake the clip) — it wasn't found. " +
                "Install Blender (auto-detected under Program Files) or set EditorPrefs 'ENC.blenderPath' to blender.exe. " +
                "Detection above works without Blender, but Bake will fail until it's installed.", MessageType.Warning);
        if (cur.animated)
            EditorGUILayout.HelpBox("Animated mode uses Size + Reduce-to-tris; the static Mesh/shading options below " +
                "(normals, winding, double-sided, height UVs, convert grid) don't apply.", MessageType.None);
        else if (noAnim)
            EditorGUILayout.HelpBox("No animation found in this model — the Animated option is disabled. Pick a rigged " +
                "glb / gltf / fbx (with a skeletal clip) to enable it, or use the static bake.", MessageType.None);
        else if (animProbeState == 1)
            EditorGUILayout.HelpBox("Animation detected in this model — tick 'Animated' to bake its own rig + clip.", MessageType.None);

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
        // you're about to delete). IntSlider (not a bare field) to show where you sit vs the ~25k per-model ceiling.
        // Range 0..50000: 0 = off, default 24000 ≈ mid, and the max is generous headroom (values much above the ceiling
        // overflow the shared buffer anyway). The slider keeps an editable number box.
        cur.targetTris = EditorGUILayout.IntSlider(new GUIContent("Reduce to ~tris (0 = off)",
            "Quadric-decimate a heavy model to about this many triangles (via Blender) before baking, to fit the engine's " +
            "SHARED buffer (one budget across ALL injected models + the game's own fx meshes). Runs AFTER 'Strip parts', so " +
            "the budget covers only the geometry you keep. Default 24000 (halves to " +
            "12000 under double-sided — the confirmed best LCAC bake, just under the ~25k per-model ceiling). It's a " +
            "CEILING, not a quota: a model already under it passes through untouched (never " +
            "upscaled). Toggling Double-sided automatically HALVES the effective target (it doubles the baked geometry), so " +
            "you set this once and just flip Double-sided on/off. Preserves thin parts (per-object). 0 = no reduction. " +
            "Needs Blender (auto-detected)."), cur.targetTris, 0, 50000);
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

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Texture / import", EditorStyles.miniBoldLabel);
        cur.reuseExtracted = EditorGUILayout.Toggle(new GUIContent("Reuse extracted files", "Skip re-importing the model file and reuse the OBJ/albedo already in the resource folder. Tick this after hand-editing the extracted texture (e.g. in paint.net) so your fix survives the bake."), cur.reuseExtracted);

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

    void OnSelectResource()
    {
        if (selected <= 0) { cur = new ModelDef(); status = ""; LoadPreview(null); return; }
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == existing[selected]);
        if (e == null) return;
        cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone so edits don't mutate the stored copy
        status = "Loaded '" + e.resourceName + "'. Edit + Bake; leave Model file empty to re-bake with new settings.";
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
    static (List<string>, List<KeyValuePair<string, int>>) InspectModel(string file)
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
            catch   // truncated / odd JSON -> zero-dependency bracket-matching fallback (glTF-specific)
            {
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
    static string[] GatherPawnNames()
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
    static string DeriveResourceName(string pawnName)
    {
        if (string.IsNullOrEmpty(pawnName)) return "";
        var parts = pawnName.Split('_');
        int end = parts.Length - 1;
        if (end > 0 && int.TryParse(parts[end], out _)) end--;
        return end >= 0 ? parts[end] : pawnName;
    }

    void DoBake()
    {
        // Tear down the preview editor BEFORE baking. The baked prefab has an Animator, so the preview is a GameObjectInspector
        // with a live animator preview; the bake's delete-first (DeleteAsset _Model.prefab) would then null its target mid-bake,
        // and SaveAsPrefabAsset's OnPostprocessAllAssets fires InstantiateForAnimatorPreview(null) -> ArgumentException. Destroying
        // it up front means nothing watches the prefab while it's deleted; we rebuild the preview after the bake.
        LoadPreview(null);
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
        var cfg = new BakeConfig
        {
            resourceName = cur.resourceName, modelFile = cur.modelFile, pawnDescription = cur.pawnDescription,
            rotationEuler = cur.rotation, positionOffset = cur.position, size = cur.size,
            normals = (NormalsMode)cur.normalsMode, smoothingAngle = cur.smoothingAngle, convertGrid = cur.convertGrid,
            reuseExtracted = cur.reuseExtracted, doubleSided = cur.doubleSided, windingFix = cur.windingFix, heightUV = cur.heightUV, targetTris = cur.targetTris,
            albedoBrightness = cur.albedoBrightness, albedoSaturation = cur.albedoSaturation, keepBlack = cur.keepBlack,
            stripParts = cur.stripParts,
            animated = cur.animated, animClip = cur.animClip, animateBones = cur.animateBones
        };
        var r = cfg.animated ? UniversalBaker.BuildAnimated(cfg) : UniversalBaker.Build(cfg);
        if (!r.ok) { status = "Bake FAILED: " + r.error; return; }
        cur.skel = ModelRegistry.ParseGuid(r.skeletonGuid);
        cur.atlas = ModelRegistry.ParseGuid(r.atlasGuid);
        cur.clip = cfg.animated ? ModelRegistry.ParseGuid(r.clipGuid) : new int[4];   // static models carry {0,0,0,0}
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
