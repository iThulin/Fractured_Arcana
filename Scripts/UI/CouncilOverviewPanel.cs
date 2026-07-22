using Godot;
using System.Collections.Generic;

// ============================================================
// CouncilOverviewPanel.cs
//
// Purpose:        Council overview UI — shows each archmage's
//                 sentiment, disposition, kingdom, and corruption
//                 level in a single panel. Designed to embed in
//                 the campus screen (a new "Council" tab) or the
//                 strategic map sidebar. Each archmage row shows
//                 a sentiment bar (-100 to +100) with color-coded
//                 zones: green (favor), neutral, red (corruption).
// Layer:          UI
// Collaborators:  CampaignState.cs (data source),
//                 ArchmageRegistry.cs (definitions),
//                 ArchmageDefinition.cs (display names, colors),
//                 CampusScreen.cs (host — council tab)
// See:            quest_hooks_compendium_v1.md §7 Step 8
// ============================================================

/// <summary>Panel showing archmage dispositions, sentiments, and kingdom
/// corruption at a glance. One row per placed archmage.</summary>
public partial class CouncilOverviewPanel : VBoxContainer
{
    private readonly List<ArchmageRow> _rows = new();

    /// <summary>Build the panel from the current campaign state.</summary>
    public void Build(GuildSaveData save)
    {
        foreach (var child in GetChildren())
            child.QueueFree();
        _rows.Clear();

        var campaign = save?.Cycle?.Campaign;
        if (campaign == null) return;

        // Header
        var header = new Label
        {
            Text = "COUNCIL OF ARCHMAGES",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        header.AddThemeFontSizeOverride("font_size", 16);
        header.AddThemeColorOverride("font_color", UITheme.POINarrative);
        AddChild(header);

        AddChild(new HSeparator());

        // One row per placed archmage (skip the Astrologer)
        foreach (var kvp in campaign.RegionArchmageMap)
        {
            string regionId = kvp.Key;
            string archmageId = kvp.Value;
            if (string.IsNullOrEmpty(archmageId)) continue;

            var def = ArchmageRegistry.Get(archmageId);
            if (def == null || def.IsVillainFaction) continue;

            var row = new ArchmageRow();
            row.Build(archmageId, regionId, campaign, def);
            _rows.Add(row);
            AddChild(row);
            AddChild(new HSeparator());
        }
    }

    /// <summary>Refresh sentiment/corruption values without rebuilding the tree.</summary>
    public void Refresh(GuildSaveData save)
    {
        var campaign = save?.Cycle?.Campaign;
        if (campaign == null) return;

        foreach (var row in _rows)
            row.Refresh(campaign);
    }
}

/// <summary>One archmage's row in the council overview: name, school,
/// sentiment bar, disposition badge, corruption indicator.</summary>
public partial class ArchmageRow : HBoxContainer
{
    private string _archmageId;
    private string _regionId;
    private Label _nameLabel;
    private Label _dispositionBadge;
    private Label _sentimentLabel;
    private ProgressBar _sentimentBar;
    private Label _corruptionLabel;

    public void Build(string archmageId, string regionId,
                      CampaignState campaign, ArchmageDefinition def)
    {
        _archmageId = archmageId;
        _regionId = regionId;

        CustomMinimumSize = new Vector2(0, 40);
        AddThemeConstantOverride("separation", 8);

        // Name + school column
        var nameCol = new VBoxContainer();
        _nameLabel = new Label
        {
            Text = def.DisplayName,
            CustomMinimumSize = new Vector2(140, 0),
        };
        _nameLabel.AddThemeFontSizeOverride("font_size", 13);
        _nameLabel.AddThemeColorOverride("font_color", new Color(def.FactionColorHex));
        nameCol.AddChild(_nameLabel);

        var schoolLabel = new Label
        {
            Text = def.School,
            CustomMinimumSize = new Vector2(140, 0),
        };
        schoolLabel.AddThemeFontSizeOverride("font_size", 10);
        schoolLabel.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        nameCol.AddChild(schoolLabel);
        AddChild(nameCol);

        // Sentiment bar
        _sentimentBar = new ProgressBar
        {
            MinValue = -100,
            MaxValue = 100,
            Value = campaign.GetSentiment(archmageId),
            CustomMinimumSize = new Vector2(160, 20),
            ShowPercentage = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        AddChild(_sentimentBar);

        // Sentiment numeric label
        _sentimentLabel = new Label
        {
            CustomMinimumSize = new Vector2(40, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _sentimentLabel.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_sentimentLabel);

        // Disposition badge
        _dispositionBadge = new Label
        {
            CustomMinimumSize = new Vector2(90, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _dispositionBadge.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_dispositionBadge);

        // Corruption indicator
        _corruptionLabel = new Label
        {
            CustomMinimumSize = new Vector2(60, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _corruptionLabel.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_corruptionLabel);

        Refresh(campaign);
    }

    public void Refresh(CampaignState campaign)
    {
        int sentiment = campaign.GetSentiment(_archmageId);
        var disposition = campaign.GetDisposition(_archmageId);
        int corruption = campaign.GetCorruption(_regionId);

        // Sentiment bar
        _sentimentBar.Value = sentiment;

        // Sentiment label with sign
        string sign = sentiment > 0 ? "+" : "";
        _sentimentLabel.Text = $"{sign}{sentiment}";
        _sentimentLabel.RemoveThemeColorOverride("font_color");
        if (sentiment > 20)
            _sentimentLabel.AddThemeColorOverride("font_color", UITheme.HealthGreen);
        else if (sentiment < -20)
            _sentimentLabel.AddThemeColorOverride("font_color", UITheme.HealthRed);
        else
            _sentimentLabel.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);

        // Disposition badge
        _dispositionBadge.Text = disposition.ToString();
        _dispositionBadge.RemoveThemeColorOverride("font_color");
        Color badgeColor = disposition switch
        {
            ArchmageDisposition.Allied => UITheme.HealthGreen,
            ArchmageDisposition.Coerced => UITheme.Warning,
            ArchmageDisposition.Overthrown => UITheme.Violet,
            ArchmageDisposition.Corrupted => UITheme.HealthRed,
            _ => UITheme.NegotiationHiddenTerm,
        };
        _dispositionBadge.AddThemeColorOverride("font_color", badgeColor);

        // Corruption
        string corruptText = corruption switch
        {
            0 => "Clean",
            1 => "Tainted",
            2 => "Spreading",
            3 => "Consumed",
            _ => $"Lv{corruption}",
        };
        _corruptionLabel.Text = corruptText;
        _corruptionLabel.RemoveThemeColorOverride("font_color");
        Color corruptColor = corruption switch
        {
            0 => UITheme.NegotiationHiddenTerm,
            1 => UITheme.Warning,
            2 => new Color("#CC5500"),
            _ => UITheme.HealthRed,
        };
        _corruptionLabel.AddThemeColorOverride("font_color", corruptColor);
    }
}
