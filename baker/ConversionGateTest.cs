// ConversionGateTest.cs (ENC editor) — Tools > ENC > Bake Conversion Gate Test (litmus).
// The FOURTH regression guard (Factory-Manual §11): asserts the raw-rig CONVERSION invariants that the animated
// runtime silently requires (established by decompiling Amplitude's bake + runtime, and by the Combine-soldier
// campaign — each was once violated, each produced an in-game failure that took hours to diagnose by hand):
//   1. every baked bone's BindPose/Local scale == 1        (a scale sandwich displaces deep chains)
//   2. every bone's ParentIndex < its own index            (bones are sorted by NAME; consumers assume topological)
//   3. every clip curve entry is ROTATION-only             (translations don't survive Amplitude's clip format)
//   4. the bake completes and produces non-empty assets
// Fixture: the deterministic LITMUS RIG (Tools/make_litmus.py — a 12-deep bone chain of cubes), synthesized on
// demand via Blender, baked under a throwaway name through the SAME ConfigFor route as the Bake button, asserted,
// and cleaned up. Slow (a real Blender bake) — a pre-commit check after touching rig_anim.py / UniversalBaker.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class ConversionGateTest
{
    const string Name = "__convgate__";

    [MenuItem("Tools/ENC/Bake Conversion Gate Test (litmus)")]
    public static void Run()
    {
        int fails = 0;
        try
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

            // --- bake through the same route as the Bake button, with the CONVERSION path active (rotation != 0) ---
            var def = new ModelDef
            {
                resourceName = Name, pawnDescription = "__convgate_dummy__", modelFile = litmus.Replace('\\', '/'),
                animated = true, animClip = "", rotation = new Vector3(90f, 0f, 0f), size = 2f,
                targetTris = 5000, atlasMaxDim = 256, materialMode = MaterialMode.Auto
            };
            var cfg = ModelFactoryWindow.ConfigFor(def);
            var r = UniversalBaker.BuildAnimated(cfg);
            if (!r.ok) { Debug.LogError("[ConvGate] FAIL — bake errored: " + r.error); return; }

            // --- gate 1+2: skeleton invariants (reflection — BoneInfos is Amplitude's type) ---
            var skel = AssetDatabase.LoadAllAssetsAtPath($"Assets/Resources/{Name}_Skeleton.asset")
                .FirstOrDefault(o => o != null && o.GetType().Name == "Skeleton");
            if (skel == null) { Debug.LogError("[ConvGate] FAIL — no baked Skeleton asset"); fails++; }
            else
            {
                var bones = (Array)skel.GetType().GetField("BoneInfos", BindingFlags.Public | BindingFlags.Instance)?.GetValue(skel);
                if (bones == null || bones.Length == 0) { Debug.LogError("[ConvGate] FAIL — Skeleton has no BoneInfos"); fails++; }
                else
                {
                    for (int i = 0; i < bones.Length; i++)
                    {
                        object bi = bones.GetValue(i);
                        float sBind = TrsScale(Member(bi, "BindPose")), sLocal = TrsScale(Member(bi, "Local"));
                        if (Mathf.Abs(sBind - 1f) > 0.01f || Mathf.Abs(sLocal - 1f) > 0.01f)
                        { Debug.LogError($"[ConvGate] FAIL — bone {i} scale != 1 (bind {sBind:0.####}, local {sLocal:0.####}): the scale sandwich is back"); fails++; }
                        int parent = Convert.ToInt32(Member(bi, "ParentIndex"));
                        if (parent >= i) { Debug.LogError($"[ConvGate] FAIL — bone {i} has ParentIndex {parent} (parents must sort before children)"); fails++; }
                    }
                }
            }

            // --- gate 3: clip is rotation-only ---
            var clips = AssetDatabase.LoadAllAssetsAtPath($"Assets/Resources/{Name}_Clips.asset")
                .FirstOrDefault(o => o != null && o.GetType().Name == "ClipCollection");
            if (clips == null) { Debug.LogError("[ConvGate] FAIL — no baked ClipCollection asset"); fails++; }
            else
            {
                var entries = (Array)clips.GetType().GetProperty("AnimationClipCurveEntries")?.GetValue(clips);
                if (entries == null || entries.Length == 0) { Debug.LogError("[ConvGate] FAIL — no clip curve entries"); fails++; }
                else
                {
                    foreach (object e in entries)
                    {
                        string fmt = Member(e, "EncodingFormat")?.ToString() ?? "?";
                        if (fmt != "Rotation" && fmt != "Fixe")
                        { Debug.LogError($"[ConvGate] FAIL — bone {Member(e, "BoneIndex")} encoded as {fmt} (conversion must yield rotation-only clips)"); fails++; }
                    }
                }
            }

            Debug.Log(fails == 0
                ? "[ConvGate] PASS — conversion invariants hold: all scales 1, parents before children, rotation-only clip."
                : $"[ConvGate] {fails} FAILURE(S) — the conversion pipeline regressed; see errors above.");
        }
        finally
        {
            // --- cleanup: throwaway assets only; the registry was never touched (we bypassed the window Upsert) ---
            foreach (var suffix in new[] { "_Skeleton", "_Clips", "_ClipsPoseData", "_Atlas" })
            {
                AssetDatabase.DeleteAsset($"Assets/Resources/{Name}{suffix}.asset");
                AssetDatabase.DeleteAsset($"Assets/Resources/{Name}{suffix}.bytes");
            }
            AssetDatabase.DeleteAsset($"Assets/Resources/{Name}_ClipsPoseData.bytes");
            AssetDatabase.DeleteAsset($"Assets/FactorySource/{Name}");
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

    // TRS.Scale as float regardless of exact TRS type
    static float TrsScale(object trs)
    {
        var s = Member(trs, "Scale");
        try { return Convert.ToSingle(s); } catch { return 1f; }
    }
}
