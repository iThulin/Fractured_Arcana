using Godot;
using System;
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

                // Aggregate feature flags
                if (tierData.UnlocksFeatures != null)
                {
                    foreach (var feature in tierData.UnlocksFeatures)
                    {
                        PlayerSession.SetFeature(feature);
                        GD.Print($"[Building] Feature unlocked: {feature}");
                    }
                }

                if (tierData.SlotCostReduction > 0)
                    PlayerSession.CardSlotCost = Math.Max(0,
                        PlayerSession.CardSlotCost - tierData.SlotCostReduction);

                if (tierData.DisenchantSplinterBonus > 0)
                    PlayerSession.DisenchantSplinterBonus += tierData.DisenchantSplinterBonus;
            }
        }

        if (bonuses.BonusHP > 0 || bonuses.BonusSteps > 0 || bonuses.BonusGold > 0)
            GD.Print($"[Buildings] Run bonuses: +{bonuses.BonusHP}HP, " +
                     $"+{bonuses.BonusSteps}Steps, +{bonuses.BonusGold}Gold, " +
                     $"{bonuses.PreRevealHexCount} pre-reveals");
        return bonuses;
    }

    /// <summary>
    /// Apply campus-persistent effects (not run bonuses) — e.g., MinDeckSize.
    /// Call from CampusScreen after buildings are loaded.
    /// </summary>
    public static void ApplyCampusEffects(GuildSaveData save)
    {
        if (save == null) return;

        // Reset to default before recomputing
        save.MinDeckSize = 5;

        foreach (var buildingSave in save.Buildings)
        {
            if (buildingSave.Tier <= 0) continue;

            // Accumulate all tiers up to current (effects stack)
            var template = BuildingDatabase.GetTemplate(buildingSave.Id);
            if (template == null) continue;

            foreach (var tier in template.Tiers)
            {
                if (tier.Tier > buildingSave.Tier) continue;

                if (tier.UnlocksFeatures != null &&
                    tier.UnlocksFeatures.Contains("deck_floor_reduced"))
                {
                    save.MinDeckSize = Math.Max(3, save.MinDeckSize - 2);
                }
            }
        }
    }
}