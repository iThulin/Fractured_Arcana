using Godot;
using System.Collections.Generic;

// ============================================================
// CampusConstruction.cs
//
// Purpose:        The build/upgrade purchase core, extracted from
//                 CampusScreen.TryBuildOrUpgrade so the strategic
//                 city view can construct buildings without the
//                 full-screen campus overlay (the Phase-2 "build
//                 in place" gap Magos reported 2026-08-13). One
//                 purchase path — the campus tab and the city
//                 construct card both call this.
// Layer:          System (campus)
// Collaborators:  BuildingDatabase (templates/costs),
//                 GuildSaveData.Buildings (= Ledger.Buildings),
//                 CampusScreen (existing caller),
//                 StrategicView construct card (new caller).
// ============================================================

/// <summary>Stateless purchase logic for building tiers. Placement is a
/// separate concern (CampusGridManager.PlaceBuilding) — a building can be
/// purchased unplaced and placed later, or placed then purchased; effects
/// gate on IsFunctional (Tier &gt; 0 &amp;&amp; IsPlaced) either way.</summary>
public static class CampusConstruction
{
    /// <summary>Buildings not yet raised (Tier 0), with their tier-1 data —
    /// the city construct card's list. Includes unaffordable ones (the UI
    /// greys them; hiding them hides the goal).</summary>
    public static List<(BuildingSaveData save, Building template)> Unbuilt(GuildSaveData save)
    {
        var result = new List<(BuildingSaveData, Building)>();
        if (save == null) return result;
        foreach (var b in save.Buildings)
        {
            if (b.Tier > 0) continue;
            var t = BuildingDatabase.GetTemplate(b.Id);
            if (t != null) result.Add((b, t));
        }
        return result;
    }

    /// <summary>Null when tier-up is purchasable right now; else the
    /// player-readable reason (cost, cap, or a missing prerequisite).</summary>
    public static string CannotBuildReason(GuildSaveData save, string buildingId)
    {
        if (save == null) return "No save.";
        var template = BuildingDatabase.GetTemplate(buildingId);
        if (template == null) return "Unknown building.";

        BuildingSaveData bs = null;
        foreach (var b in save.Buildings)
            if (b.Id == buildingId) { bs = b; break; }
        if (bs == null) return "Not in the ledger.";

        int nextTier = bs.Tier + 1;
        if (nextTier > template.MaxTier) return "Already at its final tier.";
        var tierData = template.Tiers.Find(t => t.Tier == nextTier);
        if (tierData == null) return "No tier data.";

        foreach (var reqId in tierData.RequiredBuildings)
        {
            bool found = false;
            foreach (var b in save.Buildings)
                if (b.Id == reqId && b.Tier > 0) { found = true; break; }
            if (!found)
            {
                var reqT = BuildingDatabase.GetTemplate(reqId);
                return $"Requires {reqT?.Name ?? reqId}.";
            }
        }

        if (save.Gold < tierData.GoldCost)
            return $"Needs {tierData.GoldCost}g.";
        if (save.BuildMaterials < tierData.EffectiveMaterialsCost)
            return $"Needs {tierData.EffectiveMaterialsCost} materials.";
        return null;
    }

    /// <summary>Purchase the next tier (1 = fresh construction). The exact
    /// logic CampusScreen.TryBuildOrUpgrade carried, verbatim in behavior:
    /// cost + cap + prerequisite gates, integrity refill on a fresh build.
    /// Does NOT touch placement and does NOT refresh any UI — callers own
    /// both.</summary>
    public static bool TryBuildOrUpgrade(GuildSaveData save, string buildingId)
    {
        if (save == null) return false;
        var template = BuildingDatabase.GetTemplate(buildingId);
        if (template == null) return false;

        BuildingSaveData buildingSave = null;
        foreach (var b in save.Buildings)
            if (b.Id == buildingId) { buildingSave = b; break; }
        if (buildingSave == null) return false;

        int nextTier = buildingSave.Tier + 1;
        if (nextTier > template.MaxTier) return false;
        var tierData = template.Tiers.Find(t => t.Tier == nextTier);
        if (tierData == null || save.Gold < tierData.GoldCost
            || save.BuildMaterials < tierData.EffectiveMaterialsCost)
            return false;

        foreach (var reqId in tierData.RequiredBuildings)
        {
            bool found = false;
            foreach (var b in save.Buildings)
                if (b.Id == reqId && b.Tier > 0) { found = true; break; }
            if (!found) return false;
        }

        save.Gold -= tierData.GoldCost;
        save.BuildMaterials -= tierData.EffectiveMaterialsCost;
        if (buildingSave.Tier == 0)
            buildingSave.CurrentIntegrity = buildingSave.MaxIntegrity; // fresh build starts at full HP
        buildingSave.Tier = nextTier;

        // Campus-persistent effects recompute on every purchase (2026-08-13):
        // the city-view upgrade path must apply party-size growth etc.
        // immediately, not on the next campus visit. Pure recompute — safe
        // from any caller.
        BuildingEffectApplier.ApplyCampusEffects(save);

        SaveManager.Save();
        GD.Print($"[Construction] {buildingSave.Name} tier {nextTier}. " +
                 $"Gold: {save.Gold}, Materials: {save.BuildMaterials}");
        return true;
    }

    /// <summary>Undo the most recent tier purchase — the city construct card's
    /// escape hatch when siting fails AFTER the buy (PlaceBuilding requires
    /// Tier &gt; 0, so purchase must precede placement; a paid-but-unplaceable
    /// building refunds rather than stranding gold). Only steps back one tier
    /// and refunds exactly that tier's costs.</summary>
    public static void RefundTier(GuildSaveData save, string buildingId)
    {
        if (save == null) return;
        var template = BuildingDatabase.GetTemplate(buildingId);
        if (template == null) return;

        BuildingSaveData bs = null;
        foreach (var b in save.Buildings)
            if (b.Id == buildingId) { bs = b; break; }
        if (bs == null || bs.Tier <= 0) return;

        var tierData = template.Tiers.Find(t => t.Tier == bs.Tier);
        if (tierData != null)
        {
            save.Gold += tierData.GoldCost;
            save.BuildMaterials += tierData.EffectiveMaterialsCost;
        }
        bs.Tier -= 1;
        SaveManager.Save();
        GD.Print($"[Construction] {bs.Name} refunded to tier {bs.Tier}.");
    }
}
