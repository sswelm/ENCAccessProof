// ModelFactoryWindow.cs (ENC editor) — Tools > Universal Model Factory.
// Create a NEW 3D resource or pick an existing one, choose a target pawn description + a model file (.glb/.obj/.fbx),
// and configure EVERYTHING we learned makes a model work: rotation, position (z = waterline), size, normals mode,
// smoothing angle, conversion grid. Press Bake -> skeleton + atlas + a JSON registry entry the in-game plugin reads.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class ModelFactoryWindow : EditorWindow
{
    ModelDef cur = new ModelDef();
    int selected;                       // 0 = <New>, else index into `existing`
    string[] existing = { "<New>" };
    string status = "";
    Vector2 scroll;
    bool showSettings;

    [MenuItem("Tools/Universal Model Factory")]
    static void Open()
    {
        var w = GetWindow<ModelFactoryWindow>(false, "Universal Model Factory");
        w.minSize = new Vector2(480, 470);
        w.RefreshList();
    }

    void OnEnable() { RefreshList(); }

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
        GUILayout.Space(10f);
        EditorGUILayout.LabelField("Universal Model Factory", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        DrawSettings();
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            int sel = EditorGUILayout.Popup("3D resource", selected, existing);
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) RefreshList();
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
        EditorGUILayout.LabelField("Transform", EditorStyles.miniBoldLabel);
        cur.rotation = EditorGUILayout.Vector3Field("Rotation offset (XYZ)", cur.rotation);
        cur.position = EditorGUILayout.Vector3Field("Position offset (Z = waterline)", cur.position);
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.size = EditorGUILayout.FloatField(new GUIContent("Size (units)", "Length of the model's longest axis, in world units"), cur.size, GUILayout.Width(220));
            GUILayout.FlexibleSpace();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Mesh / shading", EditorStyles.miniBoldLabel);
        cur.normalsMode = (int)(NormalsMode)EditorGUILayout.EnumPopup("Normals", (NormalsMode)cur.normalsMode);
        using (new EditorGUI.DisabledScope(cur.normalsMode != (int)NormalsMode.Recalculate))
            cur.smoothingAngle = EditorGUILayout.Slider("Smoothing angle", cur.smoothingAngle, 0f, 180f);
        cur.convertGrid = EditorGUILayout.IntField("Convert grid (0 = faithful UVs)", cur.convertGrid);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Texture / import", EditorStyles.miniBoldLabel);
        cur.reuseExtracted = EditorGUILayout.Toggle(new GUIContent("Reuse extracted files", "Skip re-importing the model file and reuse the OBJ/albedo already in the resource folder. Tick this after hand-editing the extracted texture (e.g. in paint.net) so your fix survives the bake."), cur.reuseExtracted);

        EditorGUILayout.Space();
        // A brand-new resource (<New>) has nothing to re-bake, so it also needs a model file; an existing one may leave
        // Model file empty to re-bake with new settings. Missing either target field greys Bake out with a reason.
        bool isNew = selected <= 0;
        bool canBake = !string.IsNullOrWhiteSpace(cur.resourceName)
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
                isNew ? "New resource: set Resource name, Pawn description and a Model file to bake."
                      : "Set Resource name and Pawn description to bake.", MessageType.Warning);

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        EditorGUILayout.HelpBox(
            "Bake imports the model, bakes a skeleton + atlas, and writes the JSON registry the in-game plugin reads.\n" +
            "• Formats: GLB / glTF / OBJ / FBX, and .blend (auto-converted via installed Blender).\n" +
            "• Model file empty = re-bake the existing resource with new settings (fast iteration).\n" +
            "• Normals: KeepModel = the artist's; Recalculate = hard edges via smoothing angle; Faceted = fully flat.\n" +
            "• Convert grid: 0 keeps UV seams (textured models); >0 decimates (heavy untextured meshes).\n" +
            "Registry: " + ModelRegistry.RegistryPath, MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    // Game-path settings: auto-detected Humankind BepInEx/config, with a manual override for odd install layouts.
    void DrawSettings()
    {
        string resolved = ModelRegistry.ConfigDir;
        bool exists = System.IO.Directory.Exists(resolved);
        showSettings = EditorGUILayout.Foldout(showSettings, "Settings — game path" + (exists ? "" : "  ⚠ not found"), true);
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
    }

    void OnSelectResource()
    {
        if (selected <= 0) { cur = new ModelDef(); status = ""; return; }
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == existing[selected]);
        if (e == null) return;
        cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone so edits don't mutate the stored copy
        status = "Loaded '" + e.resourceName + "'. Edit + Bake; leave Model file empty to re-bake with new settings.";
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
        var cfg = new BakeConfig
        {
            resourceName = cur.resourceName.Trim(), modelFile = (cur.modelFile ?? "").Trim(), pawnDescription = cur.pawnDescription.Trim(),
            rotationEuler = cur.rotation, positionOffset = cur.position, size = cur.size,
            normals = (NormalsMode)cur.normalsMode, smoothingAngle = cur.smoothingAngle, convertGrid = cur.convertGrid,
            reuseExtracted = cur.reuseExtracted
        };
        var r = UniversalBaker.Build(cfg);
        if (!r.ok) { status = "Bake FAILED: " + r.error; return; }
        cur.skel = ModelRegistry.ParseGuid(r.skeletonGuid);
        cur.atlas = ModelRegistry.ParseGuid(r.atlasGuid);
        ModelRegistry.Upsert(cur);
        RefreshList();
        selected = System.Array.IndexOf(existing, cur.resourceName); if (selected < 0) selected = 0;
        status = $"Baked '{cur.resourceName}' -> '{cur.pawnDescription}'  (raw bbox {r.bbox})\nskeleton {r.skeletonGuid}\nNow rebuild the mod + relaunch.";
        Debug.Log("[Factory] " + status);
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
