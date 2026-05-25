using Godot;
using System.Collections.Generic;

// ============================================================
// BuildingEffectApplier.cs
//
// Purpose:        Reads the guild's built buildings (current tier
//                 per building) and aggregates their effect
//                 bonuses into a RunBonuses struct that the run
//                 manager consumes at run start.
// Layer:          System
// Collaborators:  GuildSaveData.cs (Buildings list),
//                 BuildingDatabase.cs (tier data lookup),
//                 OverworldRunManager.cs (caller)
// See:            README §4.4 (Adding a Building) for tier
//                 effect fields
// ============================================================

/// <summary>Stateless aggregator that walks the guild's built buildings, looks up each one's current-tier effect bonuses, and rolls them up into a <see cref="RunBonuses"/> struct for the run manager to apply at run start.</summary>
public static class BuildingEffectApplier
{
    public struct RunBonuses
    {
        public int BonusHP;
        public int BonusSteps;
        public int BonusGold;
        public int PreRevealHexCount;
        public int NegotiationTokenCount;
        public string NegotiationTokenType;
        public HashSet<string> UnlockedFeatures;
    }

    /// <summary>
    /// Calculate all run bonuses from built buildings.
    /// </summary>
    public static RunBonuses CalculateRunBonuses(GuildSaveData save)
{
    var bonuses = new RunBonuses();
    bonuses.UnlockedFeatures = new HashSet<string>(); // NEW
    if (save == null) return bonuses;

    foreach (var buildingSave in save.Buildings)
    {
        if (buildingSave.Tier <= 0) continue;

        // Aggregate ALL built tiers, not just current —
        // a Tier 2 building should carry Tier 1 flags too.
        var template = BuildingDatabase.GetTemplate(buildingSave.Id);
        if (template == null) continue;

        for (int t = 1; t <= buildingSave.Tier; t++)
        {
            var tierData = template.Tiers.Find(td => td.Tier == t);
            if (tierData == null) continue;

            bonuses.BonusHP += tierData.BonusStartingHP;
            bonuses.BonusSteps += tierData.BonusStartingSteps;
            bonuses.BonusGold += tierData.BonusStartingGold;
            bonuses.PreRevealHexCount += tierData.PreRevealHexCount;

            if (tierData.BonusNegotiationTokens > 0 &&
                !string.IsNullOrEmpty(tierData.BonusTokenType))
            {
                bonuses.NegotiationTokenCount += tierData.BonusNegotiationTokens;
                bonuses.NegotiationTokenType = tierData.BonusTokenType;
            }

            // NEW — aggregate feature flags
            if (tierData.UnlocksFeatures != null)
                foreach (var feature in tierData.UnlocksFeatures)
                    bonuses.UnlockedFeatures.Add(feature);
        }
    }

    GD.Print($"[Buildings] Unlocked features: {string.Join(", ", bonuses.UnlockedFeatures)}");
    return bonuses;
}
}