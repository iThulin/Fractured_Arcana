using Godot;

/// <summary>
/// Reads the player's built buildings and applies their effects to a run.
/// Called by RunManager at the start of each run.
/// Stateless — just reads GuildSaveData and modifies RunManager values.
/// </summary>
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