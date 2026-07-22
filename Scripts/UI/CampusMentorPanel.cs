using Godot;
using System.Collections.Generic;

// ============================================================
// CampusMentorPanel.cs
//
// Purpose:        Step 9 (quest_hooks_compendium §7) — the campus
//                 mentor's counsel on the seven archmagi: who they
//                 are (personality trait + roleplay note), what the
//                 dossiers have revealed of their weaknesses (gated
//                 by DossierService hint flags), and which
//                 resolution approach is currently within reach
//                 (read live from CampaignState.ResolutionOptions).
//                 Referenced-by-design from CampaignState's mentor
//                 fields (MentorVisitCount / MentorHintsDelivered).
// Layer:          UI
// Collaborators:  ArchmageRegistry.cs (hint content),
//                 DossierService.cs (hint gating),
//                 CampaignState.cs (approach gating + mentor state),
//                 CampusScreen.cs (host — Council tab)
// ============================================================

/// <summary>The mentor's standing counsel on each placed archmage. Rebuilt on each Council tab open via <see cref="Build"/>.</summary>
public partial class CampusMentorPanel : VBoxContainer
{
    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);
    }

    /// <summary>Clear and rebuild the counsel from current save state. Also
    /// advances CampaignState.MentorVisitCount (the note shown per archmage
    /// rotates with repeat visits) and records delivered hint ids.</summary>
    public void Build(GuildSaveData save)
    {
        foreach (var child in GetChildren())
            child.QueueFree();

        var campaign = save?.Cycle?.Campaign;
        if (campaign == null)
        {
            AddChild(MakeDim("The mentor has nothing to say — no campaign in progress."));
            return;
        }

        var header = new Label
        {
            Text = "MENTOR'S COUNSEL",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        header.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        header.AddThemeColorOverride("font_color", UITheme.POINarrative);
        AddChild(header);

        AddChild(MakeDim("\"Seven seats, seven tempers. Listen before you knock.\""));

        int visit = campaign.MentorVisitCount;

        foreach (var pair in campaign.RegionArchmageMap)
        {
            string id = pair.Value;
            if (string.IsNullOrEmpty(id)) continue;
            var def = ArchmageRegistry.Get(id);
            if (def == null || def.IsVillainFaction) continue;

            var card = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.AddThemeConstantOverride("separation", 2);

            // Name — faction-colored — plus the single-word trait.
            var name = new Label { Text = $"{def.DisplayName} — {def.PersonalityTrait}" };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            name.AddThemeColorOverride("font_color", new Color(def.FactionColorHex));
            card.AddChild(name);

            // One personality note, rotating with repeat visits.
            if (def.PersonalityNotes != null && def.PersonalityNotes.Count > 0)
            {
                string note = def.PersonalityNotes[visit % def.PersonalityNotes.Count];
                card.AddChild(MakeBody(note));
            }

            // Weakness hints — only what the dossiers have earned.
            int revealed = DossierService.HintsRevealed(save, id);
            if (def.WeaknessHints != null && def.WeaknessHints.Count > 0)
            {
                int shown = System.Math.Min(revealed, def.WeaknessHints.Count);
                for (int i = 0; i < shown; i++)
                {
                    card.AddChild(MakeBody($"• {def.WeaknessHints[i]}"));
                    string hintId = $"{id}_weakness_{i}";
                    if (!campaign.MentorHintsDelivered.Contains(hintId))
                        campaign.MentorHintsDelivered.Add(hintId);
                }
                if (shown < def.WeaknessHints.Count)
                    card.AddChild(MakeDim($"❖ {def.WeaknessHints.Count - shown} more in the dossier, unearned."));
            }

            // Approach counsel — live read of what resolution is in reach.
            var disp = campaign.GetDisposition(id);
            string approach = disp switch
            {
                ArchmageDisposition.Allied     => "United. The seat stands with you.",
                ArchmageDisposition.Coerced    => "Coerced. The accord holds; the grudge holds longer.",
                ArchmageDisposition.Overthrown => "Overthrown. The seat is empty; the shard answers you.",
                ArchmageDisposition.Corrupted  => "Lost to the Astrologer. Only the finale can answer this now.",
                _ => ApproachLine(campaign, id, save),
            };
            var approachLbl = MakeBody(approach);
            approachLbl.AddThemeColorOverride("font_color", UITheme.POINarrative);
            card.AddChild(approachLbl);

            AddChild(card);
            AddChild(new HSeparator());
        }

        campaign.MentorVisitCount = visit + 1;
        SaveManager.MarkDirty();
    }

    private static string ApproachLine(CampaignState campaign, string id, GuildSaveData save)
    {
        var (canUnite, canCoerce, _) = campaign.ResolutionOptions(id, save != null ? save.HasFlag : null);
        int sentiment = campaign.GetSentiment(id);
        if (canUnite)
            return "An alliance is within reach — seek the audience before corruption closes the door.";
        if (canCoerce)
            return "Too little trust for union, but enough standing — and enough known — to press a forced accord.";
        if (sentiment < -20)
            return "You are past words with this one. Only the overthrow remains — or patient repair.";
        // In the coerce window but missing leverage, or corruption-blocked.
        return DossierService.HintsRevealed(save, id) < 2
            ? "Pressure needs a place to press. Fill the dossier before you try to bend this one."
            : "Corruption has narrowed the road. Cleanse their lands, or prepare for force.";
    }

    private Label MakeBody(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
        l.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        return l;
    }

    private Label MakeDim(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
        l.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        return l;
    }
}
