using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ENCAccessProof
{
    // All access logic, fully reflection-based so it can't break the build on a namespace guess.
    internal static class Prober
    {
        internal static object AnimMgr;                         // cached AnimationManager (set by the load hook)
        internal static readonly List<string> Report = new List<string>();

        internal static void RunScan()
        {
            Report.Clear();
            void Add(string s) { Report.Add(s); Plugin.Log.LogInfo("[ENCProof] " + s); }

            Add($"--- scan (target mod: {Plugin.TargetMod.Value}) ---");

            // 1) ENGINE REGISTRY — proof we can read live, loaded game data at runtime
            try
            {
                var content = AnimMgr != null
                    ? AccessTools.Property(AnimMgr.GetType(), "Content")?.GetValue(AnimMgr)
                    : null;
                if (content != null)
                {
                    int Count(string f) { var a = AccessTools.Field(content.GetType(), f)?.GetValue(content) as Array; return a?.Length ?? -1; }
                    Add($"AnimationManager registry reachable: " +
                        $"MeshCollections={Count("MeshCollections")}, " +
                        $"AnimatorOverrideControllers={Count("AnimatorOverrideControllers")}, " +
                        $"AnimationClipCollections={Count("AnimationClipCollections")}");
                    Add($"   live meshCollections list count: {RuntimeListCount(AnimMgr)}");
                }
                else
                {
                    Add("AnimationManager not captured yet — start/load a game, then Re-scan.");
                }
            }
            catch (Exception e) { Add("registry read error: " + e.Message); }

            // 2) TARGET-MOD ASSETS — proof we can reach the configured mod's OWN assets
            try
            {
                string filter = Plugin.AssetNameFilter.Value;
                var db = ResolveDatabase("Amplitude.Mercury.Data.World.PresentationPawnDefinition");
                if (db != null)
                {
                    int total = 0; var hits = new List<string>();
                    foreach (var el in db)
                    {
                        total++;
                        var name = (el as UnityEngine.Object)?.name;
                        if (!string.IsNullOrEmpty(name) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                            hits.Add(name);
                    }
                    Add($"PresentationPawnDefinition DB: {total} entries; {hits.Count} match \"{filter}\":");
                    foreach (var h in hits) Add("   • " + h);
                    if (hits.Count > 0) Add($">>> Confirmed access to '{Plugin.TargetMod.Value}' assets. <<<");
                }
                else
                {
                    Add("PresentationPawnDefinition DB not resolved — load a game, then Re-scan.");
                }
            }
            catch (Exception e) { Add("db probe error: " + e.Message); }
        }

        // WRITE PROOF: create an empty MeshCollection and add it to the LIVE registry via the public
        // RegisterMeshCollection. If the live meshCollections list count goes up, we've proven we can
        // write to the registry the engine refuses to expose to data mods. (Harmless placeholder — nothing
        // references it, so it just proves the call path; real baked skeletons come next.)
        // NOTE: this MUTATES shared engine state — the placeholder STAYS in the live registry until you restart the
        // game (there's no unregister). It's inert (nothing references it), but this is a diagnostic side effect, not a
        // dry run. Only press it when proving the write path, not casually.
        internal static void TestWrite()
        {
            Report.Clear();
            void Add(string s) { Report.Add(s); Plugin.Log.LogInfo("[ENCProof] " + s); }

            Add("--- WRITE test: RegisterMeshCollection ---");
            if (AnimMgr == null) { Add("No AnimationManager captured — load a game first."); return; }
            try
            {
                int before = RuntimeListCount(AnimMgr);
                var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
                if (mcType == null) { Add("MeshCollection type not found."); return; }
                var mc = ScriptableObject.CreateInstance(mcType);                 // empty placeholder
                var reg = AccessTools.Method(AnimMgr.GetType(), "RegisterMeshCollection");
                if (reg == null) { Add("RegisterMeshCollection method not found."); return; }
                reg.Invoke(AnimMgr, new object[] { mc });
                int after = RuntimeListCount(AnimMgr);
                Add($"live meshCollections list: {before} -> {after}   " +
                    (after > before ? ">>> WRITE PROVEN — we added to the live registry. <<<" : "(no change)"));
                if (after > before) Add("(note: the placeholder stays in the live registry until you restart the game.)");
            }
            catch (Exception e) { Add("write test error: " + e.Message); }
        }

        internal static int RuntimeListCount(object mgr)
        {
            var list = AccessTools.Field(mgr.GetType(), "meshCollections")?.GetValue(mgr) as ICollection;
            return list?.Count ?? -1;
        }

        // DISCOVERY: list the registered skeletons (repoint targets) and flag any airship-like model.
        // Decides milestone 3: repoint to an existing model (easy) vs bake a custom one.
        private static readonly string[] AirshipTerms = { "zeppelin", "airship", "balloon", "blimp", "dirigible", "aerostat" };
        private static bool IsAirship(string n) => !string.IsNullOrEmpty(n) && Array.Exists(AirshipTerms, t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);

        internal static void ScanModels()
        {
            Report.Clear();
            void Add(string s) { Report.Add(s); Plugin.Log.LogInfo("[ENCProof] " + s); }

            Add("--- model / skeleton discovery ---");

            // 1) the registered skeletons themselves (each MeshCollection = a model we could repoint to)
            try
            {
                var list = AnimMgr != null ? AccessTools.Field(AnimMgr.GetType(), "meshCollections")?.GetValue(AnimMgr) as IEnumerable : null;
                if (list != null)
                {
                    var airships = new List<string>(); var nonEquip = new List<string>(); int equip = 0;
                    foreach (var mc in list)
                    {
                        var name = (mc as UnityEngine.Object)?.name;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (IsAirship(name)) airships.Add(name);
                        if (name.StartsWith("EQ", StringComparison.OrdinalIgnoreCase)) equip++;     // humanoid equipment noise
                        else nonEquip.Add(name);
                    }
                    Add($"Registered skeletons: {equip} equipment (EQ_*) + {nonEquip.Count} units/vehicles; airship-like: {airships.Count}");
                    foreach (var a in airships) Add("   ✈ " + a);
                    Add("   --- ALL unit/vehicle skeletons (the repoint candidates) ---");
                    nonEquip.Sort();
                    foreach (var n in nonEquip) Add("      - " + n);
                }
                else Add("meshCollections list not available — load a game first.");
            }
            catch (Exception e) { Add("skeleton scan error: " + e.Message); }

            // 2) PresentationPawnDescription DB (the Description Templates we repoint to), if queryable
            try
            {
                var db = ResolveDatabase("Amplitude.Mercury.Data.World.PresentationPawnDescription");
                if (db != null)
                {
                    int total = 0; var airships = new List<string>();
                    foreach (var el in db)
                    {
                        total++;
                        var name = (el as UnityEngine.Object)?.name;
                        if (IsAirship(name)) airships.Add(name);
                    }
                    Add($"PresentationPawnDescription DB: {total}; airship-like: {airships.Count}");
                    foreach (var a in airships) Add("   ✈ " + a);
                    if (airships.Count == 0) Add("   (no airship Description in DB)");
                }
            }
            catch (Exception e) { Add("description scan error: " + e.Message); }
        }

        // DIAGNOSTIC: dump the formation/count fields of the unit's PresentationUnitDefinition,
        // to find what makes a single zeppelin render two planes.
        internal static void DumpUnitDef()
        {
            Report.Clear();
            void Add(string s) { Report.Add(s); Plugin.Log.LogInfo("[ENCProof] " + s); }
            Add("--- PresentationUnitDefinition dump (formation/count fields) ---");
            try
            {
                var db = ResolveDatabase("Amplitude.Mercury.Data.World.PresentationUnitDefinition");
                if (db == null) { Add("PresentationUnitDefinition DB not resolved."); return; }
                string filter = Plugin.AssetNameFilter.Value;
                int dumped = 0;
                foreach (var el in db)
                {
                    var name = (el as UnityEngine.Object)?.name;
                    if (string.IsNullOrEmpty(name) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Add($"=== {name} ===");
                    foreach (var f in AllFieldsDeep(el.GetType()))
                    {
                        string ln = f.Name.ToLowerInvariant();
                        bool numeric = f.FieldType.IsPrimitive || f.FieldType.IsEnum;
                        bool interesting = numeric || ln.Contains("formation") || ln.Contains("count") || ln.Contains("squadron")
                                           || ln.Contains("number") || ln.Contains("size") || ln.Contains("element") || ln.Contains("instance") || ln.Contains("model");
                        if (!interesting) continue;
                        object v = null; try { v = f.GetValue(el); } catch { }
                        Add($"   {f.Name} ({f.FieldType.Name}) = {((v as UnityEngine.Object)?.name ?? v?.ToString() ?? "null")}");
                    }
                    if (++dumped >= 1) break;
                }
                if (dumped == 0) Add($"no PresentationUnitDefinition matching '{filter}'.");
            }
            catch (Exception e) { Add("dump error: " + e.Message); }
        }

        // DIAGNOSTIC: resolve the zeppelin's PresentationFormationDefinition and dump its arrays/counts
        // (the array whose length is 2 is almost certainly the dummy/position list).
        internal static void DumpFormation()
        {
            Report.Clear();
            void Add(string s) { Report.Add(s); Plugin.Log.LogInfo("[ENCProof] " + s); }
            Add("--- formation definition dump ---");
            try
            {
                var udb = ResolveDatabase("Amplitude.Mercury.Data.World.PresentationUnitDefinition");
                if (udb == null) { Add("unit-def DB not resolved."); return; }
                string filter = Plugin.AssetNameFilter.Value;
                object unitDef = null;
                foreach (var el in udb)
                {
                    var n = (el as UnityEngine.Object)?.name;
                    if (!string.IsNullOrEmpty(n) && n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) { unitDef = el; break; }
                }
                if (unitDef == null) { Add($"no unit def matching '{filter}'."); return; }

                var refObj = AccessTools.Field(unitDef.GetType(), "PresentationFormationDefinition")?.GetValue(unitDef);
                if (refObj == null) { Add("formation reference is null."); return; }

                // dump the reference internals so we can see how it points to the formation
                Add("formation ref internals:");
                string refName = null;
                foreach (var f in AllFieldsDeep(refObj.GetType()))
                {
                    object v = null; try { v = f.GetValue(refObj); } catch { }
                    string disp = (v as UnityEngine.Object)?.name ?? v?.ToString() ?? "null";
                    Add($"   F {f.Name} ({f.FieldType.Name}) = {disp}");
                    if (refName == null && f.FieldType == typeof(string) && !string.IsNullOrEmpty(disp)) refName = disp;
                }
                foreach (var p in refObj.GetType().GetProperties())
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object v = null; try { v = p.GetValue(refObj); } catch { }
                    string disp = (v as UnityEngine.Object)?.name ?? v?.ToString() ?? "null";
                    Add($"   P {p.Name} ({p.PropertyType.Name}) = {disp}");
                    if (refName == null && p.PropertyType == typeof(string) && !string.IsNullOrEmpty(disp)) refName = disp;
                }

                object formation = ResolveRef(refObj);
                if (formation == null && refName != null)
                {
                    var fdb = ResolveDatabase("Amplitude.Mercury.Data.World.PresentationFormationDefinition");
                    if (fdb != null) foreach (var el in fdb) if ((el as UnityEngine.Object)?.name == refName) { formation = el; break; }
                }
                if (formation == null) { Add("could not resolve formation object."); return; }

                Add($"=== formation: {(formation as UnityEngine.Object)?.name} ===");
                foreach (var f in AllFieldsDeep(formation.GetType()))
                {
                    object v = null; try { v = f.GetValue(formation); } catch { }
                    string ln = f.Name.ToLowerInvariant();
                    bool show = v is Array || f.FieldType.IsPrimitive || f.FieldType.IsEnum
                                || ln.Contains("count") || ln.Contains("position") || ln.Contains("dummy") || ln.Contains("element") || ln.Contains("number") || ln.Contains("size");
                    if (!show) continue;
                    string disp = v is Array arr ? $"[{arr.Length}]" : ((v as UnityEngine.Object)?.name ?? v?.ToString() ?? "null");
                    Add($"   {f.Name} ({f.FieldType.Name}) = {disp}");
                }
            }
            catch (Exception e) { Add("dump error: " + e.Message); }
        }

        private static object ResolveRef(object reference)
        {
            if (reference == null) return null;
            var t = reference.GetType();
            foreach (var fn in new[] { "element", "Element" })
            { var f = AccessTools.Field(t, fn); if (f != null) try { var r = f.GetValue(reference); if (r != null) return r; } catch { } }
            foreach (var mn in new[] { "GetElement", "ToElement", "GetValue", "Resolve" })
            { var m = t.GetMethod(mn, Type.EmptyTypes); if (m != null && m.ReturnType != typeof(void)) try { var r = m.Invoke(reference, null); if (r != null && !(r is bool)) return r; } catch { } }
            foreach (var pn in new[] { "Element", "Value", "Definition" })
            { var p = t.GetProperty(pn); if (p != null) try { var r = p.GetValue(reference); if (r != null) return r; } catch { } }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<System.Reflection.FieldInfo> AllFieldsDeep(Type t)
        {
            var seen = new HashSet<string>();
            for (var b = t; b != null && b != typeof(object); b = b.BaseType)
                foreach (var f in b.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                    if (seen.Add(f.Name)) yield return f;
        }

        internal static object FindPawnDef(string filter)
        {
            var db = ResolveDatabase("Amplitude.Mercury.Data.World.PresentationPawnDefinition");
            if (db == null) return null;
            foreach (var el in db)
            {
                var n = (el as UnityEngine.Object)?.name;
                if (!string.IsNullOrEmpty(n) && n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return el;
            }
            return null;
        }

        // Databases.GetDatabase(<elementType>), tried defensively across candidate namespaces.
        private static IEnumerable ResolveDatabase(string elementTypeFullName)
        {
            var elemType = AccessTools.TypeByName(elementTypeFullName);
            if (elemType == null) return null;
            foreach (var dbTypeName in new[] { "Amplitude.Framework.Databases", "Amplitude.Mercury.Databases", "Amplitude.Databases" })
            {
                var dbType = AccessTools.TypeByName(dbTypeName);
                var getDb = dbType != null ? AccessTools.Method(dbType, "GetDatabase", new[] { typeof(Type) }) : null;
                if (getDb == null) continue;
                try { if (getDb.Invoke(null, new object[] { elemType }) is IEnumerable e) return e; } catch { }
            }
            return null;
        }
    }
}
