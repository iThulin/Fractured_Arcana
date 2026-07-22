using System.Collections.Generic;

// ============================================================
// ResolutionEncounterBuilder.cs
//
// Purpose:        Step 9 (quest_hooks_compendium §7) — builds the
//                 archmage RESOLUTION audience: a narrative
//                 encounter whose Unite / Coerce / Overthrow
//                 choices are gated by CampaignState.
//                 ResolutionOptions, plus the Overthrow boss
//                 combat definition. Content is assembled from
//                 ArchmageDefinition (description, personality
//                 notes, faction identity) so no per-archmage
//                 JSON authoring is required for v1; authored
//                 resolution encounters can replace this later
//                 by shipping encounters with ArchmageId +
//                 ResolutionKind fields set.
// Layer:          System (Campaign)
// Collaborators:  CampaignState.cs (gating + disposition),
//                 ArchmageRegistry.cs (definitions),
//                 NarrativeEncounterPanel.cs (display + gating),
//                 CampusScreen.cs / ExpeditionManager.cs (hosts)
// ============================================================

/// <summary>Builds resolution audiences and their Overthrow boss combats from archmage definitions.</summary>
public static class ResolutionEncounterBuilder
{
    /// <summary>Difficulty multiplier base for archmage resolution boss fights —
    /// the campaign's hardest authored combats short of the finale.</summary>
    private const float ResolutionDifficultyMult = 1.8f;

    /// <summary>True when the archmage can still be resolved (not yet
    /// Allied/Coerced/Overthrown/Corrupted).</summary>
    public static bool CanSeekAudience(CampaignState campaign, string archmageId)
    {
        if (campaign == null || string.IsNullOrEmpty(archmageId)) return false;
        var disp = campaign.GetDisposition(archmageId);
        return disp == ArchmageDisposition.Unknown || disp == ArchmageDisposition.Neutral;
    }

    /// <summary>Build the resolution audience encounter for an archmage, or null
    /// when no definition exists / the archmage is already resolved.</summary>
    public static NarrativeEncounterData BuildAudience(CampaignState campaign, string archmageId)
    {
        var def = ArchmageRegistry.Get(archmageId);
        if (def == null || campaign == null) return null;
        if (!CanSeekAudience(campaign, archmageId)) return null;

        int sentiment = campaign.GetSentiment(archmageId);
        string regionId = campaign.GetRegionForArchmage(archmageId);
        int corruption = campaign.GetCorruption(regionId);

        string standing = sentiment >= 40
            ? "They receive you as something close to an ally."
            : sentiment >= 0
                ? "They receive you with guarded courtesy."
                : sentiment >= -40
                    ? "They receive you coldly; your deeds have not favored them."
                    : "They receive you as a threat barely tolerated at the threshold.";

        string corruptionLine = corruption <= 0 ? ""
            : corruption == 1 ? " Something borrowed hangs at the edges of their speech."
            : corruption == 2 ? " The Astrologer's phrasings surface in their sentences, and they do not seem to notice."
            : " Their words are hardly their own any longer.";

        string note = def.PersonalityNotes != null && def.PersonalityNotes.Count > 0
            ? "\n\n" + def.PersonalityNotes[0]
            : "";

        var enc = new NarrativeEncounterData
        {
            Id = $"resolution_{archmageId}",
            Title = $"An Audience with {def.DisplayName}",
            Body = $"{def.Description}\n\n{standing}{corruptionLine}{note}\n\n" +
                   "This is the moment the campaign has been bending toward. " +
                   "However it ends, it ends today — or you withdraw, and it waits.",
            ArchmageId = archmageId,
        };

        enc.Choices.Add(new EncounterChoice
        {
            Label = $"Unite — pledge the {def.FactionName} to the guild's cause",
            ResultText = def.PostUniteDialogue != null && def.PostUniteDialogue.Count > 0
                ? def.PostUniteDialogue[0]
                : $"{def.DisplayName} takes your measure one last time — and extends a hand.",
            ResolutionKind = "unite",
        });
        enc.Choices.Add(new EncounterChoice
        {
            Label = "Coerce — press them into a forced accord",
            ResultText = $"{def.DisplayName} yields, hollow-eyed. The accord will hold. " +
                         "It will never be forgiven.",
            ResolutionKind = "coerce",
        });
        enc.Choices.Add(new EncounterChoice
        {
            Label = "Overthrow — take their seat by force",
            ResultText = $"{def.DisplayName} rises. The room goes quiet the way a held breath is quiet.",
            ResolutionKind = "overthrow",
        });
        enc.Choices.Add(new EncounterChoice
        {
            Label = "Withdraw — this is not the day",
            ResultText = "You step back from the threshold. The question keeps.",
        });

        return enc;
    }

    /// <summary>Build the Overthrow boss combat for an archmage. School-flavored
    /// archetype composition scaled by the archmage's boss health multiplier
    /// (betrayal variant when the player faces their own school) and the
    /// campaign-year escalation. Returns null if no units resolve.</summary>
    public static EncounterDefinition BuildOverthrowCombat(CampaignState campaign,
                                                           string archmageId,
                                                           string playerSchool)
    {
        var def = ArchmageRegistry.Get(archmageId);
        if (def == null) return null;

        string[] arch = (def.School ?? "").ToLowerInvariant() switch
        {
            "adept"        => new[] { "Wizard", "Soldier", "Wizard" },
            "arcanist"     => new[] { "Wizard", "Wizard", "Defender" },
            "druid"        => new[] { "Ranger", "Brute", "Ranger" },
            "elementalist" => new[] { "Wizard", "Brute", "Wizard" },
            "enchanter"    => new[] { "Wizard", "Defender", "Soldier" },
            "necromancer"  => new[] { "Brute", "Wizard", "Brute" },
            "tinker"       => new[] { "Defender", "Wizard", "Defender" },
            _              => new[] { "Wizard", "Brute", "Soldier" },
        };

        bool betrayal = campaign != null && campaign.IsSchoolBetrayal(archmageId, playerSchool);
        float healthMult = betrayal ? def.BetrayalBossHealthMult : def.StandardBossHealthMult;
        float mult = ResolutionDifficultyMult * healthMult *
                     CampaignEscalation.CombatDifficultyMult(SaveManager.ActiveSave?.Cycle);

        var combat = new EncounterDefinition
        {
            Id = $"resolution_boss_{archmageId}",
            DisplayName = def.DisplayName,
            Tier = EncounterTier.Boss,
            RegionId = campaign?.GetRegionForArchmage(archmageId) ?? "",
            TerrainType = "Plains",
            DifficultyMult = mult,
        };
        foreach (var a in arch)
            if (UnitRegistry.TryResolveId(a, out var uid))
                combat.Enemies.Add(new EnemySlot(uid, mult));

        return combat.Enemies.Count > 0 ? combat : null;
    }
}
