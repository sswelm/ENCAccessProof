using System;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // FIRING-ON-ATTACK (complete). A bombard raises SimulationEvent_ArtilleryStrikeStarted; we read the ArtilleryStrike's
    // StrikerUnit.UnitDefinition, match it to our registry entry, and enqueue the firing unit/army GUID so ONLY that pawn
    // plays the clip once (barrel elevates on the shot) — per-instance, resolved on the main thread. See docs/Firing-On-Attack.md.
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
    }

    [HarmonyPatch] internal static class Hk_ArtilleryStrike
    {
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
                    // Enqueue the firer's GUID(s) so only THAT pawn animates (per-instance). We're on the sim thread here (no
                    // Unity access) — Plugin.Update drains the queue, resolves the PresentationUnit, and records its pawn
                    // positions. Enqueue BOTH the unit's and the army's GUID: the on-map presentation entity is an army, so
                    // PresentationUnit.GUID may be the army's — matching either covers it. (SimulationEntityGUID -> long.)
                    long uguid = UniversalInject.GuidToLong(FireProbe.Member(unit, "GUID"));
                    long aguid = UniversalInject.GuidToLong(FireProbe.Member(FireProbe.Member(__1, "StrikerArmy"), "GUID"));
                    if (uguid != 0) entry.fireGuidQueue.Enqueue(uguid);
                    if (aguid != 0 && aguid != uguid) entry.fireGuidQueue.Enqueue(aguid);
                    if (uguid != 0 || aguid != 0) Plugin.Log.LogInfo($"[Fire] *** OUR MODEL '{entry.resourceName}' FIRED (unit {uguid}, army {aguid}, empire={emp} targetTile={tile}) — queued");
                    else Plugin.Log.LogWarning($"[Fire] '{entry.resourceName}' fired but GUIDs unreadable — can't target the instance");
                }
                else
                    Plugin.Log.LogInfo($"[Fire] >>> ArtilleryStrikeStarted FIRED (not ours): {unitDef}");
            }
            catch (Exception e) { Plugin.Log.LogError("[Fire] artillery postfix: " + e); }
        }
    }

    // STATE-DRIVEN ATTACK trigger (Phase 2). Every pawn ranged shot funnels through
    // PawnRangedFightSequence.InitializeCommon(shooter, ...) — all five constructors call it (decompiled,
    // Assembly-CSharp) — so one postfix covers battle volleys, unit-target shots, and district bombards. The
    // sequence is built on the presentation/main thread, so the handler reads the shooter's Transform directly.
    [HarmonyPatch] internal static class Hk_PawnRangedFight
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PawnRangedFightSequence");
            var m = t != null ? AccessTools.Method(t, "InitializeCommon") : null;
            if (m != null) Plugin.Log.LogInfo("[Fire] hooked PawnRangedFightSequence (state-driven attack)");
            else Plugin.Log.LogWarning("[Fire] NOT found: PawnRangedFightSequence.InitializeCommon — state-driven attack clips won't trigger");
            return m;
        }
        // InitializeCommon(PresentationPawn shooter, bool dies, bool delay, bool miss, float projectileSpread)
        static void Postfix(object __0)
        {
            try { UniversalInject.OnPawnAttack(__0, "ranged shot"); }
            catch (Exception e) { Plugin.Log.LogError("[Fire] ranged-fight postfix: " + e); }
        }
    }

    // STATE-DRIVEN MELEE ATTACK trigger (2026-07-22): close-combat units (the Abomination animal) never fire a ranged
    // shot, so PawnRangedFightSequence never runs and their attack clip stayed silent (movement + the donor's maul/scratch
    // SOUND played, no bite animation). Two false starts before the right seam:
    //   1. PawnMeleeFightSequence's ctor — a STRUCT, Harmony caught it unreliably.
    //   2. PawnActionMeleeStartFight.StartPawnAction() — runs ONCE per fight START, not per swing (log: 1 fire, 1 ATTACK,
    //      while the fight had several swings/FX). The pose system was proven correct there (1 fire -> exactly 1 ATTACK).
    // The REAL per-swing method is PawnActionMeleeStartFight.StartPairMeleeAttack() — called once per fight SEQUENCE
    // (= per swing) from StartNewSequence, and it runs striker.AttackFSM.Start(...) (the actual bite). Read the private
    // `striker` field (the pawn swinging THIS sequence — it's set to pair.AttackerPawn OR pair.DefenderPawn for a
    // retaliation, NOT always this.Pawn), and arm that pawn's clip. Per-pawn restart in OnPawnAttack handles fast swings.
    [HarmonyPatch] internal static class Hk_PawnMeleeFight
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PawnActionMeleeStartFight");
            var m = t != null ? AccessTools.Method(t, "StartPairMeleeAttack") : null;
            if (m != null) Plugin.Log.LogInfo("[Fire] hooked PawnActionMeleeStartFight.StartPairMeleeAttack (per-swing melee attack)");
            else Plugin.Log.LogWarning("[Fire] NOT found: PawnActionMeleeStartFight.StartPairMeleeAttack — melee attack clips won't trigger");
            return m;
        }
        static void Postfix(object __instance)
        {
            try
            {
                var striker = FireProbe.Member(__instance, "striker");   // the pawn actually swinging this sequence
                if (striker != null) UniversalInject.OnPawnAttack(striker, "melee swing");
            }
            catch (Exception e) { Plugin.Log.LogError("[Fire] melee-swing postfix: " + e); }
        }
    }

    // ---- ADJACENT-ATTACK ROTATION DIAGNOSTIC (2026-07-21): which facing actions run for OUR units, and does the
    // rotation FSM ever start? A custom unit turns to a ranged target but not an adjacent one — these three postfixes
    // localize where the adjacent path drops the turn. Quiet outside fights; filtered to registry units. ----
    [HarmonyPatch] internal static class Hk_RotDiag_FaceEnemy
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.UnitActionFaceEnemy");
            var m = t != null ? AccessTools.Method(t, "StartUnitAction") : null;
            if (m == null) Plugin.Log.LogWarning("[Rot] NOT found: UnitActionFaceEnemy.StartUnitAction (diagnostic off)");
            return m;
        }
        static void Postfix(object __instance)
        {
            try { UniversalInject.OnUnitActionDiag(__instance, "FaceEnemy.Start"); } catch { }
        }
    }

    [HarmonyPatch] internal static class Hk_RotDiag_LookAt
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.UnitActionLookAt");
            var m = t != null ? AccessTools.Method(t, "StartUnitAction") : null;
            if (m == null) Plugin.Log.LogWarning("[Rot] NOT found: UnitActionLookAt.StartUnitAction (diagnostic off)");
            return m;
        }
        static void Postfix(object __instance)
        {
            try { UniversalInject.OnUnitActionDiag(__instance, "LookAt.Start"); } catch { }
        }
    }

    // Both StartDirectionToLook overloads (Vector3 direction — the LookAt/adjacent path — and the float-angle one
    // StartRotate forwards to, if present) so range and adjacent report through the same line.
    [HarmonyPatch] internal static class Hk_RotDiag_FsmStart
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.RotationPawnStateMachine");
            int n = 0;
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "StartDirectionToLook") { n++; yield return m; }
            if (n == 0) Plugin.Log.LogWarning("[Rot] NOT found: RotationPawnStateMachine.StartDirectionToLook (diagnostic off)");
            else Plugin.Log.LogInfo($"[Rot] rotation diagnostic hooked ({n} FSM overload(s))");
        }
        static void Postfix(object __instance, int __result)
        {
            try { UniversalInject.OnRotationStartDiag(__instance, __result); } catch { }
        }
    }
}
