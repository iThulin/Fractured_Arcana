using Godot;

// ============================================================
// StrategicDebug.cs
//
// Purpose:        Test levers for the strategic layer — the one
//                 subsystem that could previously only be
//                 exercised by playing a full cycle out. Three
//                 actions:
//                   ForceConjunction() — end the cycle NOW.
//                   OweLunations(n)    — advance the calendar n
//                                        lunations with the real
//                                        per-lunation world tick.
//                   PrimeWarfront()    — push a border to the
//                                        boil-over threshold so
//                                        the next tick opens a
//                                        warfront.
//                 None of these reimplement game logic: each sets
//                 the state the shipped path already reads, so a
//                 forced run exercises the SAME code an organic
//                 one would. That is the whole point — a lever
//                 that fakes the outcome tests nothing.
// Layer:          System (debug)
// Collaborators:  CalendarState.cs (ConjunctionForced),
//                 CycleState.cs (PendingStraggleLunations,
//                 Kingdoms, Warfronts), StrategicView.cs
//                 (ProcessPendingStraggle / RunLunationTick /
//                 ShowConjunction consume all three),
//                 KingdomTickSimulation.cs (WarfrontOpenThreshold)
// See:            docs/build_order_v4.md §3 item 2 (the strategic
//                 smoke tests, built 2026-07-21 and never run),
//                 §7 (SCAFFOLDING — remove before any external
//                 build), docs/convergence_finale_spec_v1.md §13
//                 (I1 cannot be verified without ForceConjunction).
//
// Usage: wired to the CampusScreen debug panel, or call directly:
//   StrategicDebug.ForceConjunction();
//   StrategicDebug.OweLunations(3);
//   StrategicDebug.PrimeWarfront();
//
// EVERY lever takes effect on the NEXT strategic-map load —
// StrategicView._Ready is where ProcessPendingStraggle and the
// ConjunctionReached check run. Press, then walk to the map.
// ============================================================

/// <summary>Debug levers for the strategic layer. Scaffolding — listed in
/// build_order_v4 §7 for removal before any build someone else plays.</summary>
public static class StrategicDebug
{
    /// <summary>End the cycle at the next strategic-map load, regardless of
    /// how many lunations remain. Writes CalendarState.ConjunctionForced —
    /// a flag that shipped 2026-07-21 with a definition, a reader
    /// (ConjunctionReached) and NO writer until this method.
    ///
    /// <para>This is the lever the finale is built on: `StrategicView`'s
    /// Conjunction gate (convergence_finale_spec_v1 §3, deliverable I1) is
    /// otherwise reachable only by spending 12 deploys, which guarantees it
    /// ships unverified.</para></summary>
    public static void ForceConjunction()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            GD.Print("[StrategicDebug] No active cycle — load or start a game first.");
            return;
        }
        if (cycle.Calendar.ConjunctionReached)
        {
            GD.Print("[StrategicDebug] The Conjunction is already reached " +
                     $"(lunation {cycle.Calendar.CurrentLunation} / " +
                     $"{cycle.Calendar.LunationsPerCycle}, forced={cycle.Calendar.ConjunctionForced}).");
            return;
        }

        cycle.Calendar.ConjunctionForced = true;
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print("[StrategicDebug] Conjunction FORCED — was at lunation " +
                 $"{cycle.Calendar.CurrentLunation} / {cycle.Calendar.LunationsPerCycle} " +
                 $"({cycle.Calendar.LunationsRemaining} remaining). " +
                 "Return to the strategic map; the Conjunction beat plays on load.");
    }

    /// <summary>Advance the calendar by <paramref name="n"/> lunations, running
    /// the REAL per-lunation world tick for each (council echoes, corruption
    /// tide, infirmary, kingdom simulation, warfront advance/resolve).
    ///
    /// <para>Implemented by adding to CycleState.PendingStraggleLunations — the
    /// emergency-extraction debt channel — so StrategicView.ProcessPendingStraggle
    /// does the work on the next map load. No tick logic is duplicated here, and
    /// the Conjunction check at the end of that method still fires, so walking
    /// the calendar past 12 this way behaves exactly like playing it out.</para></summary>
    public static void OweLunations(int n = 1)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            GD.Print("[StrategicDebug] No active cycle — load or start a game first.");
            return;
        }
        if (n <= 0)
            return;

        cycle.PendingStraggleLunations += n;
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print($"[StrategicDebug] Owed +{n} lunation(s) — now at " +
                 $"{cycle.Calendar.CurrentLunation} / {cycle.Calendar.LunationsPerCycle}, " +
                 $"{cycle.PendingStraggleLunations} pending. " +
                 "Return to the strategic map; each ticks the world in full.");
    }

    /// <summary>Push the hottest border to the boil-over threshold so the NEXT
    /// lunation tick opens a warfront through KingdomTickSimulation's own
    /// OpenWarfront path (marker, Advance bar, three-sided intervention dialog).
    ///
    /// <para>Picks the (defender, aggressor) pair with the highest live
    /// BorderPressure, preferring an aggressor that actually has a controlling
    /// faction — the tick's own requirement. Pressure entries only exist after
    /// at least one tick has run, so on a brand-new world this reports empty:
    /// owe a lunation first, then prime.</para></summary>
    public static void PrimeWarfront()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            GD.Print("[StrategicDebug] No active cycle — load or start a game first.");
            return;
        }

        string bestDefender = null, bestAggressor = null;
        int bestPressure = -1;
        bool sawAnyEntry = false;

        foreach (var kv in cycle.Kingdoms)
        {
            var k = kv.Value;
            if (k?.BorderPressure == null)
                continue;
            // Already at war: the tick refuses a second front for this kingdom.
            if (HasOpenFront(cycle, kv.Key))
                continue;

            foreach (var bp in k.BorderPressure)
            {
                sawAnyEntry = true;
                string aggressorFaction =
                    cycle.Kingdoms.TryGetValue(bp.Key, out var nk) ? nk.ControllingFactionId : "";
                if (string.IsNullOrEmpty(aggressorFaction))
                    continue;   // the tick skips factionless aggressors
                if (bp.Value > bestPressure)
                {
                    bestPressure = bp.Value;
                    bestDefender = kv.Key;
                    bestAggressor = bp.Key;
                }
            }
        }

        if (bestDefender == null)
        {
            GD.Print("[StrategicDebug] No primeable border found" +
                     (sawAnyEntry
                        ? " — every candidate's aggressor has no controlling faction, " +
                          "or every kingdom already has an open front."
                        : " — BorderPressure is empty. Owe a lunation first, then prime.") +
                     $" (open warfronts: {cycle.Warfronts?.Count ?? 0})");
            return;
        }

        cycle.Kingdoms[bestDefender].BorderPressure[bestAggressor] =
            KingdomTickSimulation.WarfrontOpenThreshold;
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print($"[StrategicDebug] Primed '{bestAggressor}' → '{bestDefender}' " +
                 $"at pressure {KingdomTickSimulation.WarfrontOpenThreshold} " +
                 $"(was {bestPressure}). Owe a lunation and return to the map — " +
                 "the tick opens the warfront and plants the ⚔ marker.");
    }

    private static bool HasOpenFront(CycleState cycle, string kingdomId)
    {
        var fronts = cycle.Warfronts;
        if (fronts == null)
            return false;
        foreach (var w in fronts)
            if (w.DefenderKingdomId == kingdomId || w.AggressorKingdomId == kingdomId)
                return true;
        return false;
    }
}
