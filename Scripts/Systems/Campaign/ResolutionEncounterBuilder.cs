using Godot;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// ResolutionEncounterBuilder.cs
//
// Purpose:        Step 9 (quest_hooks_compendium §7). Builds the
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
    /// <summary>Difficulty multiplier base for archmage resolution boss fights,
    /// the campaign's hardest authored combats short of the finale.</summary>
    private const float ResolutionDifficultyMult = 1.8f;

    // ── Authored audiences (Data/Encounters/resolutions.json) ───────────
    // Authored encounters carry ArchmageId + choices with ResolutionKind and
    // take precedence over the code-built fallback below. Standard encounter
    // JSON schema (camelCase, fields included), same as every other pool.
    private const string AuthoredPath = "res://Data/Encounters/resolutions.json";
    private static Dictionary<string, NarrativeEncounterData> _authored;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    private static void EnsureAuthoredLoaded()
    {
        if (_authored != null) return;
        _authored = new Dictionary<string, NarrativeEncounterData>(
            System.StringComparer.OrdinalIgnoreCase);
        if (!FileAccess.FileExists(AuthoredPath)) return;
        try
        {
            using var file = FileAccess.Open(AuthoredPath, FileAccess.ModeFlags.Read);
            if (file == null) return;
            var list = JsonSerializer.Deserialize<List<NarrativeEncounterData>>(
                file.GetAsText(), JsonOptions);
            if (list == null) return;
            foreach (var enc in list)
                if (enc != null && !string.IsNullOrEmpty(enc.ArchmageId))
                    _authored[enc.ArchmageId] = enc;
            GD.Print($"[Resolution] Loaded {_authored.Count} authored audience(s).");
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[Resolution] Failed to load {AuthoredPath}: {e.Message}");
        }
    }

    /// <summary>Testing hook: drop the authored cache so edited JSON reloads.</summary>
    public static void ClearCache() => _authored = null;

    /// <summary>The audience gate (Step 9 gating ruling, 2026-07-22): an
    /// audience requires (1) the archmage not already resolved, (2) having MET
    /// them, which is the dossier met flag, stamped the first time you cross
    /// their forces (Eternal, survives the unmake), and (3) engagement THIS
    /// cycle, meaning disposition Neutral, which only happens once sentiment has moved off
    /// zero. You cannot resolve a stranger. Returns the blocking reason for
    /// the disabled button label.</summary>
    public static (bool canSeek, string reason) AudienceGate(GuildSaveData save, string archmageId)
    {
        var campaign = save?.Cycle?.Campaign;
        if (campaign == null || string.IsNullOrEmpty(archmageId)) return (false, "-");

        var disp = campaign.GetDisposition(archmageId);
        if (disp != ArchmageDisposition.Unknown && disp != ArchmageDisposition.Neutral)
            return (false, disp.ToString()); // already resolved

        if (!DossierService.IsMet(save, archmageId))
            return (false, "Not yet met");

        if (disp == ArchmageDisposition.Unknown)
            return (false, "No dealings yet");

        return (true, "");
    }

    /// <summary>True when the archmage can still be resolved (not yet
    /// Allied/Coerced/Overthrown/Corrupted). The audience BUTTON uses the
    /// stricter <see cref="AudienceGate"/>; this remains the encounter-level
    /// sanity check.</summary>
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

        // Authored audience wins when one exists for this archmage.
        EnsureAuthoredLoaded();
        if (_authored.TryGetValue(archmageId, out var authored))
            return authored;

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

        // Keep the body tight: the reception (who they are to you right now),
        // one line of the mentor's read on how they judge, and the stakes.
        // The full dossier prose (def.Description) belongs to the mentor panel
        // and dossier cards, not this moment.
        string title = string.IsNullOrEmpty(def.Title) ? def.DisplayName : def.Title;
        string body = $"{title}. {standing}{corruptionLine}";
        if (def.PersonalityNotes != null && def.PersonalityNotes.Count > 0)
            body += $"\n\n{def.PersonalityNotes[0]}";
        body += "\n\nThis is the moment the campaign has been bending toward. " +
                "However it ends, it ends today. Or you withdraw, and it waits.";

        var enc = new NarrativeEncounterData
        {
            Id = $"resolution_{archmageId}",
            Title = $"An Audience with {def.DisplayName}",
            Body = body,
            ArchmageId = archmageId,
        };

        enc.Choices.Add(new EncounterChoice
        {
            Label = $"Unite: pledge {def.FactionName} to the guild's cause",
            ResultText = def.PostUniteDialogue != null && def.PostUniteDialogue.Count > 0
                ? def.PostUniteDialogue[0]
                : $"{def.DisplayName} takes your measure one last time, then extends a hand.",
            ResolutionKind = "unite",
        });
        enc.Choices.Add(new EncounterChoice
        {
            Label = "Coerce: press them into a forced accord",
            ResultText = $"{def.DisplayName} yields, hollow-eyed. The accord will hold. " +
                         "It will never be forgiven.",
            ResolutionKind = "coerce",
        });
        enc.Choices.Add(new EncounterChoice
        {
            Label = "Overthrow: take their seat by force",
            ResultText = $"{def.DisplayName} rises. The room goes quiet the way a held breath is quiet.",
            ResolutionKind = "overthrow",
        });
        enc.Choices.Add(new EncounterChoice
        {
            Label = "Withdraw: this is not the day",
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
