using System;

// ============================================================
// KingdomTickSimulation.Castle.cs  (partial of KingdomTickSimulation)
//
// Purpose:        The war that comes to the castle (docs/provinces_v1_9_
//                 bastion.md, slice PV4, X4). The castle fights only in
//                 sieges and assaults, and a front is one: at the new
//                 moon, when a front contests the province the castle
//                 stands in, the aggressor's soldiers fall on its camp,
//                 unless the aggressor's court is Friendly or Allied to
//                 the guild, or Campward wards the camp (PR72). The
//                 assault is CastleThreats' own: the crews scatter, the
//                 resupply is set back, and a castle defence is owed.
//                 One assault at a time, as CastleThreats has it.
// Layer:          System
// Collaborators:  KingdomTickSimulation.cs (Tick calls CastleInTheWar
//                 after the fronts advance), KingdomTickSimulation.
//                 Fronts.cs (FrontsWalk, the news helpers),
//                 CastleThreats.cs (ForceThreat), CastleManeuvers.cs
//                 (CampWarded), CastleHops.cs (CastleProvince),
//                 CouncilTick.cs (CouncilQueries.StanceFor),
//                 ProvinceSaveAssert.Bastion.cs
// See:            docs/provinces_v1_9_bastion.md section 6
// ============================================================

public static partial class KingdomTickSimulation
{
    /// <summary>A front contesting the castle's province comes for its camp (PR72). Runs at
    /// the new moon, after the fronts advance.</summary>
    internal static void CastleInTheWar(CycleState cycle, Func<string, string> fd = null)
    {
        if (!FrontsWalk(cycle) || !cycle.CastleParked || cycle.Warfronts == null
            || !string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
        {
            return;
        }
        int castle = CastleHops.CastleProvince(cycle);
        if (castle < 0)
            return;
        var here = cycle.World.Provinces[castle];
        foreach (var wf in cycle.Warfronts)
        {
            if (wf == null || wf.Closed || wf.IsCacheSiege || wf.ProvinceId != here.Id)
                continue;
            var stance = CouncilQueries.StanceFor(cycle, wf.AggressorKingdomId);
            if (stance == KingdomStance.Friendly || stance == KingdomStance.Allied)
                continue;   // an army that likes the guild passes its camp by
            string aggressor = AggressorName(cycle, wf, fd);
            if (CastleManeuvers.CampWarded(cycle))
            {
                Report(cycle, $"The war reaches {ProvinceControl.Name(here)}, but the wards hold around the castle's camp.");
                return;
            }
            var result = CastleThreats.ForceThreat(cycle, CastleThreatKind.KingdomAssault, wf.AggressorKingdomId);
            if (result.Happened)
            {
                Report(cycle, $"{ProvinceControl.LossMark}The war comes to the castle: {aggressor}'s soldiers fall on "
                              + $"the camp in {ProvinceControl.Name(here)}. The crews scatter: the resupply stands at "
                              + $"{WorldClock.ResupplyDaysLeft(cycle)} day(s), and the camp must be defended.");
            }
            return;   // one assault at a time
        }
    }
}
