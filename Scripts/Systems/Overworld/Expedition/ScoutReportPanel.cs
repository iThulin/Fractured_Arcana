using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ScoutReportPanel.cs
//
// Purpose:        Modal pre-combat scouting report shown when
//                 the player steps onto a Combat POI hex.
//                 Displays encounter tier, terrain type, and a
//                 tallied enemy roster before the player commits
//                 to the fight. Two buttons: Engage / Retreat.
//                 S4: also the Identify seam (overworld_spell
//                 _system §7b) — ShowIntel() renders the same
//                 roster read-only from afar: no Engage, Retreat
//                 relabeled Close, and the stored handlers are
//                 NOT fired (they belong to a pending on-hex
//                 encounter, which an intel view must not touch).
// Layer:          UI
// Collaborators:  EncounterDefinition.cs (data source),
//                 UnitRegistry.cs (threat labels/colors),
//                 UITheme.cs (styling),
//                 OverworldRunManager.cs (callback owner)
// See:            README §5a — POI engagement rules
// ============================================================

/// <summary>
/// Pre-combat scouting panel. Call <see cref="Show"/> with a built
/// <see cref="EncounterDefinition"/>; <see cref="OnEngage"/> fires if
/// the player commits, <see cref="OnRetreat"/> if they back off.
/// </summary>
public partial class ScoutReportPanel : Control
{
    /// <summary>Fired when the player clicks Engage.</summary>
    public Action OnEngage;

    /// <summary>Fired when the player clicks Retreat.</summary>
    public Action OnRetreat;

    // ── Layout nodes ────────────────────────────────────────────────────
    private Panel _backdrop;
    private Panel _panel;
    private Label _titleLabel;
    private Label _terrainLabel;
    private Label _tierLabel;
    private VBoxContainer _enemyList;
    private Label _stepNoteLabel;
    private Button _engageButton;
    private Button _retreatButton;

    public override void _Ready()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;
        MouseFilter = MouseFilterEnum.Stop; // swallow clicks behind the panel
        Visible = false;

        // ── Dim backdrop ────────────────────────────────────────────────
        _backdrop = new Panel { AnchorRight = 1f, AnchorBottom = 1f };
        var backdropStyle = new StyleBoxFlat { BgColor = UITheme.NarrativeBackdrop };
        _backdrop.AddThemeStyleboxOverride("panel", backdropStyle);
        AddChild(_backdrop);

        // ── Main panel ──────────────────────────────────────────────────
        _panel = new Panel
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
            OffsetLeft = -280,
            OffsetTop = -250,
            OffsetRight = 280,
            OffsetBottom = 250,
        };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = UITheme.NarrativePanelBg,
            BorderColor = UITheme.POICombat,   // red border signals danger
            BorderWidthTop = UITheme.BorderWidth + 1,
            BorderWidthBottom = UITheme.BorderWidth + 1,
            BorderWidthLeft = UITheme.BorderWidth + 1,
            BorderWidthRight = UITheme.BorderWidth + 1,
            CornerRadiusTopLeft = UITheme.NarrativePanelCorner,
            CornerRadiusTopRight = UITheme.NarrativePanelCorner,
            CornerRadiusBottomLeft = UITheme.NarrativePanelCorner,
            CornerRadiusBottomRight = UITheme.NarrativePanelCorner,
        };
        _panel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_panel);

        // ── Inner layout ────────────────────────────────────────────────
        var layout = new VBoxContainer
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 24,
            OffsetTop = 24,
            OffsetRight = -24,
            OffsetBottom = -24,
        };
        layout.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(layout);

        // Title — "SCOUTING REPORT"
        _titleLabel = new Label
        {
            Text = "SCOUTING REPORT",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", UITheme.NarrativeTitleFontSize);
        _titleLabel.AddThemeColorOverride("font_color", UITheme.POICombat);
        layout.AddChild(_titleLabel);

        layout.AddChild(new HSeparator());

        // Encounter name row
        var contextRow = new HBoxContainer();
        contextRow.AddThemeConstantOverride("separation", 24);
        layout.AddChild(contextRow);

        _terrainLabel = MakeBodyLabel(contextRow, expandFill: true);
        _tierLabel = MakeBodyLabel(contextRow, expandFill: true);

        layout.AddChild(new HSeparator());

        // Enemy roster header
        var rosterHeader = new Label { Text = "ENEMY FORCES" };
        rosterHeader.AddThemeFontSizeOverride("font_size", UITheme.NarrativeBodyFontSize);
        rosterHeader.AddThemeColorOverride("font_color", UITheme.NarrativeTitleColor);
        layout.AddChild(rosterHeader);

        _enemyList = new VBoxContainer();
        _enemyList.AddThemeConstantOverride("separation", 5);
        layout.AddChild(_enemyList);

        layout.AddChild(new HSeparator());

        // Step note (shows step cost context)
        _stepNoteLabel = MakeBodyLabel(layout);

        // Spacer
        layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        // ── Button row ───────────────────────────────────────────────────
        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", 16);
        layout.AddChild(buttonRow);

        buttonRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _retreatButton = MakeButton("Retreat", UITheme.NarrativeBodyColor);
        _retreatButton.Pressed += OnRetreatPressed;
        buttonRow.AddChild(_retreatButton);

        _engageButton = MakeButton("Engage", UITheme.POICombat);
        _engageButton.Pressed += OnEngagePressed;
        buttonRow.AddChild(_engageButton);

        buttonRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Public API
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Populate and display the panel.
    /// </summary>
    /// <param name="encounter">Pre-built encounter definition from EncounterPoolLoader.</param>
    /// <param name="terrainName">Human-readable terrain name (e.g. "Forest").</param>
    /// <param name="stepCostPaid">
    /// Movement cost the party paid to reach this hex. Used to remind the
    /// player they've already committed steps before seeing the panel.
    /// Pass 0 to suppress the note.
    /// </param>
    public void Show(EncounterDefinition encounter, string terrainName, int stepCostPaid)
    {
        _intelMode = false;
        _titleLabel.Text = "SCOUTING REPORT";
        _engageButton.Visible = true;
        _retreatButton.Text = "Retreat";
        Populate(encounter, terrainName);

        // ── Step note ────────────────────────────────────────────────────
        _stepNoteLabel.Text = stepCostPaid > 0
            ? $"You entered this territory ({stepCostPaid} fuel already burned)."
            : "";

        Visible = true;
    }

    /// <summary>S4 (Identify): the same report, read from afar — no Engage
    /// (the party isn't on the hex), Retreat becomes Close, and the stored
    /// OnEngage/OnRetreat handlers are left untouched and unfired.</summary>
    public void ShowIntel(EncounterDefinition encounter, string terrainName, string note)
    {
        _intelMode = true;
        _titleLabel.Text = "ARCANE SIGHT";
        _engageButton.Visible = false;
        _retreatButton.Text = "Close";
        Populate(encounter, terrainName);
        _stepNoteLabel.Text = note ?? "";
        Visible = true;
    }

    private bool _intelMode = false;

    private void Populate(EncounterDefinition encounter, string terrainName)
    {
        _terrainLabel.Text = $"Terrain:  {terrainName}";
        _tierLabel.Text = $"Threat:   {TierLabel(encounter.Tier)}";

        // ── Enemy roster ─────────────────────────────────────────────────
        // Clear previous children
        foreach (var child in _enemyList.GetChildren())
            child.QueueFree();

        // Tally by display name so duplicates show "Soldier ×2" etc.
        // U2: labels/colors come from the unit definition — unique unit names
        // flow through to scout reports for free (units doc §11).
        var tally = new Dictionary<string, (int count, Color color)>();
        foreach (var slot in encounter.Enemies)
        {
            var def = UnitRegistry.Get(slot.UnitId);
            string label = def.ThreatLabel;
            Color color = def.BodyColor;
            if (tally.TryGetValue(label, out var entry))
                tally[label] = (entry.count + 1, color);
            else
                tally[label] = (1, color);
        }

        foreach (var kvp in tally)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            // Color swatch — fixed size, no wrapper needed
            var swatch = new ColorRect
            {
                Color = kvp.Value.color,
                CustomMinimumSize = new Vector2(14, 14),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            row.AddChild(swatch);

            string entryText = kvp.Value.count > 1
                ? $"{kvp.Key}  x{kvp.Value.count}"
                : kvp.Key;

            var entryLabel = new Label
            {
                Text = entryText,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.Off,
            };
            entryLabel.AddThemeFontSizeOverride("font_size", UITheme.NarrativeBodyFontSize);
            entryLabel.AddThemeColorOverride("font_color", UITheme.NarrativeBodyColor);
            row.AddChild(entryLabel);

            _enemyList.AddChild(row);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Button handlers
    // ════════════════════════════════════════════════════════════════════════

    private void OnEngagePressed()
    {
        Visible = false;
        if (_intelMode)
            return; // unreachable (button hidden) — belt and braces
        OnEngage?.Invoke();
    }

    private void OnRetreatPressed()
    {
        Visible = false;
        // S4: in intel mode the stored handlers belong to a pending on-hex
        // encounter (or are stale) — an Identify view must not fire them.
        if (_intelMode)
            return;
        OnRetreat?.Invoke();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════════

    private static string TierLabel(EncounterTier tier) => tier switch
    {
        EncounterTier.Skirmish => "Skirmish  (Easy)",
        EncounterTier.Battle => "Battle    (Moderate)",
        EncounterTier.Siege => "Siege     (Hard)",
        EncounterTier.Ambush => "Ambush    (Surprise)",
        EncounterTier.Boss => "BOSS      (Climax)",
        _ => tier.ToString(),
    };

    private static Label MakeBodyLabel(Node parent, bool expandFill = false)
    {
        var lbl = new Label { AutowrapMode = TextServer.AutowrapMode.Off };
        if (expandFill)
            lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.AddThemeFontSizeOverride("font_size", UITheme.NarrativeBodyFontSize);
        lbl.AddThemeColorOverride("font_color", UITheme.NarrativeBodyColor);
        parent.AddChild(lbl);
        return lbl;
    }

    private static Button MakeButton(string text, Color accentColor)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(130, 42),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.NarrativeBodyFontSize);
        btn.AddThemeColorOverride("font_color", accentColor);
        return btn;
    }
}
