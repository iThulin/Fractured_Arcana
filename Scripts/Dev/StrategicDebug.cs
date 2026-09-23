using Godot;

// ============================================================
// StrategicDebug.cs
//
// Purpose:        Test levers for the strategic layer, the one
//                 subsystem that could previously only be
//                 exercised by playing a full cycle out. Three
//                 actions:
//                   ForceConjunction():  end the cycle NOW.
//                   OweLunations(n):     advance the calendar n
//                                        lunations with the real
//                                        per-lunation world tick.
//                   PrimeWarfront():     push a border to the
//                                        boil-over threshold so
//                                        the next tick opens a
//                                        warfront.
//                 None of these reimplement game logic: each sets
//                 the state the shipped path already reads, so a
//                 forced run exercises the SAME code an organic
//                 one would. That is the whole point. A lever
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
//                 §7 (SCAFFOLDING, remove before any external
//                 build), docs/convergence_finale_spec_v1.md §13
//                 (I1 cannot be verified without ForceConjunction).
//
// Usage: wired to the CampusScreen debug panel, or call directly:
//   StrategicDebug.ForceConjunction();
//   StrategicDebug.OweLunations(3);
//   StrategicDebug.PrimeWarfront();
//
// EVERY lever takes effect on the NEXT strategic-map load.
// StrategicView._Ready is where ProcessPendingStraggle and the
// ConjunctionReached check run. Press, then walk to the map.
// ============================================================

/// <summary>Debug levers for the strategic layer. Scaffolding, listed in
/// build_order_v4 §7 for removal before any build someone else plays.</summary>
public static class StrategicDebug
{
    /// <summary>End the cycle at the next strategic-map load, regardless of
    /// how many lunations remain. Writes CalendarState.ConjunctionForced,
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
            GD.Print("[StrategicDebug] No active cycle. Load or start a game first.");
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
        GD.Print("[StrategicDebug] Conjunction FORCED. Was at lunation " +
                 $"{cycle.Calendar.CurrentLunation} / {cycle.Calendar.LunationsPerCycle} " +
                 $"({cycle.Calendar.LunationsRemaining} remaining). " +
                 "Return to the strategic map; the Conjunction beat plays on load.");
    }

    /// <summary>Advance the calendar by <paramref name="n"/> lunations, running
    /// the REAL per-lunation world tick for each (council echoes, corruption
    /// tide, infirmary, kingdom simulation, warfront advance/resolve).
    ///
    /// <para>Implemented by adding to CycleState.PendingStraggleLunations (the
    /// emergency-extraction debt channel), so StrategicView.ProcessPendingStraggle
    /// does the work on the next map load. No tick logic is duplicated here, and
    /// the Conjunction check at the end of that method still fires, so walking
    /// the calendar past 12 this way behaves exactly like playing it out.</para></summary>
    public static void OweLunations(int n = 1)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            GD.Print("[StrategicDebug] No active cycle. Load or start a game first.");
            return;
        }
        if (n <= 0)
            return;

        cycle.PendingStraggleLunations += n;
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print($"[StrategicDebug] Owed +{n} lunation(s), now at " +
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
    /// faction, which is the tick's own requirement. Pressure entries only exist after
    /// at least one tick has run, so on a brand-new world this reports empty:
    /// owe a lunation first, then prime.</para></summary>
    public static void PrimeWarfront()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            GD.Print("[StrategicDebug] No active cycle. Load or start a game first.");
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
                        ? ": every candidate's aggressor has no controlling faction, " +
                          "or every kingdom already has an open front."
                        : ": BorderPressure is empty. Owe a lunation first, then prime.") +
                     $" (open warfronts: {cycle.Warfronts?.Count ?? 0})");
            return;
        }

        cycle.Kingdoms[bestDefender].BorderPressure[bestAggressor] =
            KingdomTickSimulation.WarfrontOpenThreshold;
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print($"[StrategicDebug] Primed '{bestAggressor}' → '{bestDefender}' " +
                 $"at pressure {KingdomTickSimulation.WarfrontOpenThreshold} " +
                 $"(was {bestPressure}). Owe a lunation and return to the map. " +
                 "The tick opens the warfront and plants the ⚔ marker.");
    }

    /// <summary>Resolve every unresolved archmage seat so
    /// <c>CampaignState.AllArchmagiResolved()</c> returns true and the Conjunction
    /// becomes the Convergence (I1 gate, convergence_finale_spec_v1 §3).
    ///
    /// <para>Spreads the four resolved dispositions deterministically by index
    /// rather than slamming everything to one value, because the finale reads all
    /// four differently (spec §5): Allied grants its archmage's power for the whole
    /// event, Coerced grants it at half strength, Overthrown grants a one-use shard
    /// invocation and removes them from the fight, and Corrupted puts them in the
    /// room ON THE OTHER SIDE. A lever that set everything Corrupted would make the
    /// hardest case the only testable one; a lever that set everything Allied would
    /// never exercise the enemy path. This gives one of each per four seats.</para>
    ///
    /// <para>Already-resolved seats are skipped. That guard lives HERE, not in
    /// CampaignState.SetDisposition, whose own guard only blocks downgrades to
    /// Neutral. Passing it a resolved seat would happily overwrite real play.</para></summary>
    public static void ResolveAllSeats()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var campaign = cycle?.Campaign;
        if (campaign == null)
        {
            GD.Print("[StrategicDebug] No active campaign. Load or start a game first.");
            return;
        }

        var spread = new[]
        {
            ArchmageDisposition.Allied,
            ArchmageDisposition.Coerced,
            ArchmageDisposition.Overthrown,
            ArchmageDisposition.Corrupted,
        };

        int changed = 0, already = 0, i = 0;
        foreach (var kv in campaign.RegionArchmageMap)
        {
            string archmageId = kv.Value;
            if (string.IsNullOrEmpty(archmageId))
                continue;   // unoccupied region, and AllArchmagiResolved skips these too

            var current = campaign.GetDisposition(archmageId);
            if (current != ArchmageDisposition.Unknown && current != ArchmageDisposition.Neutral)
            { already++; i++; continue; }

            var next = spread[i % spread.Length];
            campaign.SetDisposition(archmageId, next);
            GD.Print($"[StrategicDebug]   {kv.Key} / {archmageId}: {current} -> {next}");
            changed++;
            i++;
        }

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print($"[StrategicDebug] Seats resolved: {changed} set, {already} already resolved. " +
                 $"AllArchmagiResolved = {campaign.AllArchmagiResolved()}. " +
                 "Force Conjunction, then return to the strategic map. The Anchorhold opens.");
    }

    /// <summary>Print the Expedition v2 state: the live anchor list the field
    /// party can travel to, the lunation budget, and who is posted where. This
    /// is a READ, not a lever: it changes nothing and fakes nothing, it just
    /// makes the data layer observable before anything renders it.
    ///
    /// <para>The anchor list is the union ExpeditionAnchors builds over the
    /// world tables (staging points, discovered shard zones, the parked castle,
    /// built waypoints), so what prints here is exactly what a field-party
    /// destination picker would offer.</para></summary>
    public static void DumpExpeditionState()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            GD.PrintErr("[StrategicDebug] No active cycle.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== EXPEDITION v2 STATE ===");

        sb.AppendLine(cycle.CastleX >= 0 && cycle.CastleY >= 0
            ? $"Castle: ({cycle.CastleX},{cycle.CastleY}) " +
              (cycle.CastleParked
                  ? $"PARKED since lunation {cycle.CastleParkedLunation}"
                  : "at dock")
            : "Castle: not yet on the map this cycle");
        sb.AppendLine($"Hold: {(cycle.CastleHold == null || cycle.CastleHold.IsEmpty ? "empty" : cycle.CastleHold.ToString())}");
        sb.AppendLine($"Furnace: {cycle.CastleFuel}/{cycle.CastleMaxFuel} " +
                      $"(march range {CastleMarch.RangeInTiles(cycle)} tile(s) at {CastleMarch.FuelPerTile}/tile)");
        sb.AppendLine(cycle.CastleRepairLunations > 0
            ? $"Resupply: {cycle.CastleRepairLunations} lunation(s) left, castle EXPOSED"
            : "Resupply: complete");
        if (!string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
        {
            sb.AppendLine($"Castle defence owed against '{cycle.PendingCastleAssaultKingdomId}'");
        }

        var turn = cycle.ExpeditionTurn;
        sb.AppendLine(turn == null
            ? "Turn budget: absent"
            : $"Turn budget: lunation {turn.Lunation}, castle move " +
              $"{(turn.CastleMoveSpent ? "spent" : "available")}, " +
              $"dives {turn.DivesSpent}/{turn.DivesPerLunation} " +
              $"({turn.DivesRemaining} left)");

        var anchors = ExpeditionAnchors.All(cycle);
        sb.AppendLine($"Anchors: {anchors.Count}");
        foreach (var a in anchors)
        {
            string charges = a.IsPermanent ? "permanent" : $"{a.ChargesLeft} charge(s)";
            sb.AppendLine($"  [{a.Kind}] {a.Name} ({a.X},{a.Y}) {charges}" +
                          $"{(a.Available ? "" : " UNAVAILABLE")}  key={a.Key}");
        }

        int campus = 0, crew = 0, field = 0;
        if (cycle.Companions != null)
        {
            foreach (var c in cycle.Companions)
            {
                if (c == null || !c.IsRecruited)
                {
                    continue;
                }
                if (c.Posting == CompanionPosting.Crew) crew++;
                else if (c.Posting == CompanionPosting.Field) field++;
                else campus++;
            }
        }
        sb.AppendLine($"Postings: {crew} crew, {field} field, {campus} campus " +
                      $"(MaxPartySize {cycle.MaxPartySize}, MaxFieldParties {cycle.MaxFieldParties})");

        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p == null)
                {
                    continue;
                }
                sb.AppendLine($"  {p.Id} '{p.Name}': {p.State}, at '{p.AnchorKey}'" +
                              $"{(string.IsNullOrEmpty(p.DestinationAnchorKey) ? "" : $" bound for '{p.DestinationAnchorKey}' in {p.TravelPhasesRemaining} phase(s)")}" +
                              $", members [{string.Join(", ", p.MemberCompanionIds ?? new System.Collections.Generic.List<string>())}]");
            }
        }

        sb.AppendLine($"Signature pool: {cycle.SignaturePool?.Count ?? 0}/" +
                      $"{cycle.SignaturePool?.MaxPoolSize ?? 0}, " +
                      $"{cycle.SignatureGrants?.Count ?? 0} wizard grant(s)");

        GD.Print(sb.ToString());
    }

    /// <summary>Bring a raid or an assault down on the parked castle now.
    /// Uses the SAME resolution the lunation roll uses, so a forced threat costs
    /// exactly what an organic one does. No lever reimplements game logic.</summary>
    public static void ForceCastleThreat(CastleThreatKind kind)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            GD.PrintErr("[StrategicDebug] No active cycle.");
            return;
        }
        if (!cycle.CastleParked)
        {
            GD.PrintErr("[StrategicDebug] The castle is not parked, so nothing can come for it.");
            return;
        }

        var result = CastleThreats.ForceThreat(cycle, kind);
        if (!result.Happened)
        {
            GD.Print("[StrategicDebug] Nothing came.");
            return;
        }
        cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
        cycle.PendingSiegeReports.Add(result.Report);
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print($"[StrategicDebug] {result.Kind}: {result.Report}");
    }

    /// <summary>March the parked castle to the nearest OTHER staging point it
    /// can afford. Exercises the real CastleMarch path, refusals included, so
    /// the lever cannot pass a march the game would reject.</summary>
    public static void MarchCastleToNearestAnchor()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.World?.StagingPoints == null)
        {
            GD.PrintErr("[StrategicDebug] No world or staging points.");
            return;
        }

        StagingPoint best = null;
        int bestDist = int.MaxValue;
        foreach (var sp in cycle.World.StagingPoints)
        {
            if (sp == null || (sp.X == cycle.CastleX && sp.Y == cycle.CastleY))
            {
                continue;
            }
            int d = cycle.World.HexDistance(cycle.CastleX, cycle.CastleY, sp.X, sp.Y);
            if (d < bestDist)
            {
                bestDist = d;
                best = sp;
            }
        }

        if (best == null)
        {
            GD.PrintErr("[StrategicDebug] Nowhere else to march to.");
            return;
        }

        if (!CastleMarch.CanMarchTo(cycle, best.X, best.Y, out string why))
        {
            GD.Print($"[StrategicDebug] March refused: {why}");
            return;
        }

        string line = CastleMarch.Execute(cycle, best.X, best.Y, "The castle");
        if (!string.IsNullOrEmpty(line))
        {
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(line);
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
            GD.Print($"[StrategicDebug] {line}");
        }
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
