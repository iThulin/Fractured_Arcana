using Godot;

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
    }

    /// <summary>
    /// Calculate all run bonuses from built buildings.
    /// </summary>
    public static RunBonuses CalculateRunBonuses(GuildSaveData save)
    {
        var bonuses = new RunBonuses();
        if (save == null) return bonuses;

        foreach (var buildingSave in save.Buildings)
        {
            if (buildingSave.Tier <= 0) continue;

            var tierData = BuildingDatabase.GetCurrentTierData(buildingSave.Id, save);
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

            if (tierData.UnlocksFeatures != null)
            {
                foreach (var feature in tierData.UnlocksFeatures)
                    GD.Print($"[Building] Feature unlocked: {feature}");
            }
        }

        if (bonuses.BonusHP > 0 || bonuses.BonusSteps > 0 || bonuses.BonusGold > 0)
        {
            GD.Print($"[Buildings] Run bonuses: " +
                     $"+{bonuses.BonusHP}HP, +{bonuses.BonusSteps}Steps, " +
                     $"+{bonuses.BonusGold}Gold, " +
                     $"{bonuses.PreRevealHexCount} pre-reveals");
        }

        return bonuses;
    }
}