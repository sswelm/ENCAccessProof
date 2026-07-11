using System;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // FIRING-ON-ATTACK (complete). A bombard raises SimulationEvent_ArtilleryStrikeStarted; we read the ArtilleryStrike's
    // StrikerUnit.UnitDefinition, match it to our registry entry, and flag entry.fireRequested so the pose hook plays the
    // model's clip ONCE (barrel elevates on the shot). See docs/Firing-On-Attack.md.
    // Discovery history: this event was confirmed via a multi-event probe (BattleStarted/Ready/AirStrike/UnitDamage) —
    // only ArtilleryStrikeStarted fired for a unit bombard; those probes are removed now the hook is proven. To extend
    // firing-on-attack to bombers (AirStrikeStarted) or melee (BattleStarted), re-add a probe the same way and match the attacker.
    internal static class FireProbe
    {
        // Resolve a SimulationEvent's static Raise() and log whether the hook attached (so we know at patch time, not just
        // when it fires). All these events live in Amplitude.Mercury.Simulation.
        internal static MethodBase Resolve(string type, string label)
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Simulation." + type);
            var m = t != null ? AccessTools.Method(t, "Raise") : null;
            if (m != null) Plugin.Log.LogInfo("[Fire] hooked " + label);
            else Plugin.Log.LogWarning("[Fire] NOT found: " + type + ".Raise");
            return m;
        }
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static object Member(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var f = t.GetField(name, BF); if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, BF); if (p != null) return p.GetValue(o);
            return null;
        }
        internal static int Int(object o, string name) { var v = Member(o, name); return v == null ? int.MinValue : Convert.ToInt32(v); }

        // One-shot: dump an object's identifier-ish fields (name/def/guid/tile/pos/pawn/descriptor/empire/index) so we can
        // see how to match the firing Unit to our injected pawn. Best-effort; swallows per-field read errors.
        internal static void DumpIdentifiers(object o, string prefix)
        {
            if (o == null) { Plugin.Log.LogInfo(prefix + " = null"); return; }
            foreach (var f in o.GetType().GetFields(BF))
            {
                var n = f.Name.ToLowerInvariant();
                if (n.Contains("def") || n.Contains("guid") || n.Contains("tile") || n.Contains("pos") ||
                    n.Contains("pawn") || n.Contains("descriptor") || n.Contains("empire") || n.Contains("index") || n.Contains("name"))
                {
                    object v = null; try { v = f.GetValue(o); } catch { }
                    Plugin.Log.LogInfo($"{prefix}.{f.Name} = {(v == null ? "null" : v.ToString())}");
                }
            }
        }
    }

    // Each Postfix is arg-less on purpose: it works for any Raise() signature and just reports that the event fired.
    // Once we know WHICH event a bombard raises, Phase 2 reads that event's payload (attacker Unit/Army) to match the pawn.
    [HarmonyPatch] internal static class Hk_ArtilleryStrike
    {
        static bool dumped;
        static MethodBase TargetMethod() => FireProbe.Resolve("SimulationEvent_ArtilleryStrikeStarted", "ArtilleryStrikeStarted");
        // Raise(object sender, ArtilleryStrike strike) — __1 is the strike (StrikerUnit / StrikerArmy / TargetTileIndex).
        static void Postfix(object __1)
        {
            try
            {
                int emp = FireProbe.Int(__1, "AttackerEmpireIndex"), tile = FireProbe.Int(__1, "TargetTileIndex");
                object unit = FireProbe.Member(__1, "StrikerUnit");
                string unitDef = FireProbe.Member(unit, "UnitDefinition")?.ToString() ?? "";
                var entry = UniversalInject.FindEntryForUnitDefinition(unitDef);
                if (entry != null)
                {
                    entry.fireRequested = true;   // the pose hook consumes this to (re)start the one-shot clip on this model's pawns
                    Plugin.Log.LogInfo($"[Fire] *** OUR MODEL '{entry.resourceName}' FIRED (empire={emp} targetTile={tile}) — clip triggered");
                }
                else
                    Plugin.Log.LogInfo($"[Fire] >>> ArtilleryStrikeStarted FIRED (not ours): {unitDef}");
                if (!dumped) { dumped = true; FireProbe.DumpIdentifiers(__1, "[Fire] Strike"); FireProbe.DumpIdentifiers(unit, "[Fire] StrikerUnit"); }
            }
            catch (Exception e) { Plugin.Log.LogError("[Fire] artillery postfix: " + e); }
        }
    }
}
