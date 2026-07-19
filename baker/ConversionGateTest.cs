// ConversionGateTest.cs (ENC editor) — Tools > ENC > Tests > Bake Conversion Gate Test.
// The FOURTH regression guard (Factory-Manual §11): asserts the raw-rig CONVERSION invariants that the animated
// runtime silently requires (established by decompiling Amplitude's bake + runtime, and by the Combine-soldier
// campaign — each was once violated, each produced an in-game failure that took hours to diagnose by hand):
//   1. every baked bone's BindPose/Local scale == 1        (a scale sandwich displaces deep chains)
//   2. every bone's ParentIndex < its own index            (bones are sorted by NAME; consumers assume topological)
//   3. every clip curve entry is ROTATION-only             (translations don't survive Amplitude's clip format)
//   4. the clip actually carries the animation             (per-bone entries + a real frame count)
// Two fixtures, two menu items:
//   (litmus)            — the deterministic synthetic 12-deep chain (Tools/make_litmus.py, built via Blender on
//                         demand). Fast-ish, always available, covers the mechanics.
//   (registry converted models) — THE REAL RIGS: every registry model on the conversion path (animated +
//                         convertRig, e.g. the Combine soldier's location-keyed auto-rig — 62 bones, 342 frames,
//                         the full rest-normalization) baked under a throwaway name. The strongest net; needs the
//                         source model files on disk.
// Both bake through the SAME ConfigFor route as the Bake button and clean up after themselves; the registry is
// never touched. Slow (real Blender bakes) — pre-commit checks after touching rig_anim.py / UniversalBaker.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class ConversionGateTest
{
    const string PREFIX = "__convgate__";

    [MenuItem("Tools/ENC/Tests/Bake Conversion Gate Test (litmus)")]
    public static void RunLitmus()
    {
        // --- fixture: synthesize the litmus rig if it isn't cached ---
        string litmus = Path.Combine(Path.GetTempPath(), "enc_litmus.glb");
        if (!File.Exists(litmus))
        {
            string script = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Tools", "make_litmus.py");
            if (!File.Exists(script)) { Debug.LogError("[ConvGate] Tools/make_litmus.py missing"); return; }
            string blender = UniversalBaker.FindBlender();
            if (string.IsNullOrEmpty(blender)) { Debug.LogError("[ConvGate] Blender not found — the gate needs it"); return; }
            var psi = new System.Diagnostics.ProcessStartInfo(blender, $"-b --python \"{script}\" -- \"{litmus}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(180000); }
            if (!File.Exists(litmus)) { Debug.LogError("[ConvGate] litmus synthesis produced no GLB"); return; }
        }
        var def = new ModelDef
        {
            resourceName = "litmus", pawnDescription = "__convgate_dummy__", modelFile = litmus.Replace('\\', '/'),
            animated = true, convertRig = true, animClip = "", rotation = new Vector3(90f, 0f, 0f), size = 2f,
            targetTris = 5000, atlasMaxDim = 256, materialMode = MaterialMode.Auto
        };
        int fails = BakeAndAssert(def);
        Debug.Log(fails == 0
            ? "[ConvGate] LITMUS PASS — conversion invariants hold (all scales 1, parents before children, rotation-only clip)."
            : $"[ConvGate] LITMUS: {fails} FAILURE(S) — the conversion pipeline regressed; see errors above.");
    }

    [MenuItem("Tools/ENC/Tests/Bake Conversion Gate Test (registry converted models)")]
    public static void RunRegistryConverted()
    {
        // The REAL adversarial fixtures: every registry model on the conversion path (the Combine soldier's
        // location-keyed ValveBiped being the canonical one). Skips models whose source file is gone.
        var defs = ModelRegistry.Load().Where(d => d.animated && d.convertRig
                                               && !d.resourceName.StartsWith(PREFIX)).ToList();
        if (defs.Count == 0) { Debug.LogWarning("[ConvGate] no converted models in the registry (animated + 'Convert raw rig') — nothing to test."); return; }
        int total = 0, tested = 0;
        foreach (var src in defs)
        {
            if (string.IsNullOrWhiteSpace(src.modelFile) || !File.Exists(src.modelFile))
            { Debug.LogWarning($"[ConvGate] SKIP {src.resourceName} — source model file not on disk ({src.modelFile})"); continue; }
            var clone = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(src));   // never mutate the real entry
            int fails = BakeAndAssert(clone);
            Debug.Log(fails == 0
                ? $"[ConvGate] {src.resourceName}: PASS (full conversion on the real rig)"
                : $"[ConvGate] {src.resourceName}: {fails} FAILURE(S) — see errors above.");
            total += fails; tested++;
        }
        Debug.Log($"[ConvGate] registry converted models: {tested} tested, {total} total failure(s).");
    }

    // Bake `def` under a throwaway name via the SAME ConfigFor route as the Bake button, assert every conversion
    // invariant on the baked Amplitude assets (reflection — they're Amplitude's types), clean up. Returns failures.
    static int BakeAndAssert(ModelDef def)
    {
        string testName = PREFIX + def.resourceName;
        def.resourceName = testName;
        int fails = 0;
        try
        {
            var cfg = ModelFactoryWindow.ConfigFor(def);
            var r = UniversalBaker.BuildAnimated(cfg);
            if (!r.ok) { Debug.LogError($"[ConvGate] {testName}: bake errored: " + r.error); return 1; }

            // --- skeleton invariants: scale-1 everywhere + parents before children ---
            var skel = AssetDatabase.LoadAllAssetsAtPath($"Assets/Resources/{testName}_Skeleton.asset")
                .FirstOrDefault(o => o != null && o.GetType().Name == "Skeleton");
            int boneCount = 0;
            if (skel == null) { Debug.LogError($"[ConvGate] {testName}: no baked Skeleton asset"); fails++; }
            else
            {
                var bones = (Array)skel.GetType().GetField("BoneInfos", BindingFlags.Public | BindingFlags.Instance)?.GetValue(skel);
                if (bones == null || bones.Length == 0) { Debug.LogError($"[ConvGate] {testName}: Skeleton has no BoneInfos"); fails++; }
                else
                {
                    boneCount = bones.Length;
                    for (int i = 0; i < bones.Length; i++)
                    {
                        object bi = bones.GetValue(i);
                        float sBind = TrsScale(Member(bi, "BindPose")), sLocal = TrsScale(Member(bi, "Local"));
                        if (Mathf.Abs(sBind - 1f) > 0.01f || Mathf.Abs(sLocal - 1f) > 0.01f)
                        { Debug.LogError($"[ConvGate] {testName}: bone {i} scale != 1 (bind {sBind:0.####}, local {sLocal:0.####}) — the scale sandwich is back"); fails++; }
                        int parent = Convert.ToInt32(Member(bi, "ParentIndex"));
                        if (parent >= i) { Debug.LogError($"[ConvGate] {testName}: bone {i} has ParentIndex {parent} (parents must sort before children)"); fails++; }
                    }
                }
            }

            // --- clip invariants: rotation-only, one entry per bone, a real frame count ---
            var clips = AssetDatabase.LoadAllAssetsAtPath($"Assets/Resources/{testName}_Clips.asset")
                .FirstOrDefault(o => o != null && o.GetType().Name == "ClipCollection");
            if (clips == null) { Debug.LogError($"[ConvGate] {testName}: no baked ClipCollection asset"); fails++; }
            else
            {
                var curves = (Array)clips.GetType().GetProperty("AnimationClipCurveEntries")?.GetValue(clips);
                if (curves == null || curves.Length == 0) { Debug.LogError($"[ConvGate] {testName}: no clip curve entries"); fails++; }
                else
                {
                    if (boneCount > 0 && curves.Length != boneCount)
                    { Debug.LogError($"[ConvGate] {testName}: {curves.Length} curve entries for {boneCount} bones — the runtime addresses animId+boneIndex, these MUST match"); fails++; }
                    foreach (object e in curves)
                    {
                        string fmt = Member(e, "EncodingFormat")?.ToString() ?? "?";
                        if (fmt != "Rotation" && fmt != "Fixe")
                        { Debug.LogError($"[ConvGate] {testName}: bone {Member(e, "BoneIndex")} encoded as {fmt} (conversion must yield rotation-only clips)"); fails++; }
                    }
                }
                var entries = (Array)clips.GetType().GetProperty("AnimationClipEntries")?.GetValue(clips);
                if (entries != null && entries.Length > 0)
                {
                    int frameCount = Convert.ToInt32(Member(entries.GetValue(0), "FrameCount"));
                    if (frameCount < 2) { Debug.LogError($"[ConvGate] {testName}: clip FrameCount {frameCount} — the animation didn't bake"); fails++; }
                }
                else { Debug.LogError($"[ConvGate] {testName}: no ClipEntry (the animation is missing entirely)"); fails++; }
            }
            return fails;
        }
        catch (Exception ex) { Debug.LogError($"[ConvGate] {testName}: exception {ex.GetType().Name}: {ex.Message}"); return fails + 1; }
        finally
        {
            foreach (var suffix in new[] { "_Skeleton.asset", "_Clips.asset", "_ClipsPoseData.bytes", "_Atlas.asset" })
                AssetDatabase.DeleteAsset($"Assets/Resources/{testName}{suffix}");
            AssetDatabase.DeleteAsset($"Assets/FactorySource/{testName}");
            AssetDatabase.Refresh();
        }
    }

    static object Member(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        return (object)t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
            ?? t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);
    }

    // TRS.Scale as float regardless of the exact TRS type
    static float TrsScale(object trs)
    {
        var s = Member(trs, "Scale");
        try { return Convert.ToSingle(s); } catch { return 1f; }
    }
}
