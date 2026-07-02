using Godot;
using System;

// ============================================================
// LedgerPanel.cs
//
// Purpose:        The mid-expedition Favor Ledger (§4a): a
//                 read-only listing of every favor in the
//                 bidirectional ledger, plus exactly ONE action —
//                 call in a favor. Eligibility rules live in
//                 ExpeditionManager (they need live expedition
//                 state: party position, HP, patrols); this panel
//                 is deliberately dumb — it renders rows and
//                 forwards clicks.
//
//                 Mac/Windows Compatibility rules honored:
//                   - UI built via CallDeferred(nameof(BuildUI))
//                     from _Ready, never directly in _Ready
//                   - ScrollContainer is a plain VBox child (the
//                     TabContainer restriction doesn't apply)
//                   - All colors sourced from UITheme
// Layer:          UI
// Collaborators:  ExpeditionManager.cs (owner; supplies the
//                 eligibility delegate + call-in handler),
//                 CouncilState.cs (Favor / court lookups via
//                 SaveManager.ActiveSave.Cycle.Council)
// See:            court_council_system_v1_1.docx §4, §4a
// ============================================================

/// <summary>Expedition HUD panel: the favor ledger, read-only plus the
/// single call-in action. Toggled by the HUD Ledger button.</summary>
public partial class LedgerPanel : PanelContainer
{
    /// <summary>Returns null if the favor is callable right now, else a short
    /// human-readable reason it isn't. Set by ExpeditionManager.</summary>
    public Func<Favor, string> GetIneligibilityReason;

    /// <summary>Invoked when the player calls in an eligible favor.
    /// Set by ExpeditionManager.</summary>
    public Action<Favor> OnCallIn;

    private VBoxContainer _rows;
    private bool _built;
    private bool _pendingOpen;

    public override void _Ready()
    {
        Visible = false;
        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        // Centered panel over the expedition view.
        AnchorLeft = 0.5f;
        AnchorTop = 0.5f;
        AnchorRight = 0.5f;
        AnchorBottom = 0.5f;
        GrowHorizontal = Control.GrowDirection.Both;
        GrowVertical = Control.GrowDirection.Both;
        OffsetLeft = -310;
        OffsetRight = 310;
        OffsetTop = -230;
        OffsetBottom = 230;

        var style = new StyleBoxFlat
        {
            BgColor = UITheme.OverworldHudBg,
            BorderColor = UITheme.OverworldHudBorder,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        AddThemeStyleboxOverride("panel", style);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        // Header: title + close.
        var header = new HBoxContainer();
        vbox.AddChild(header);

        var title = new Label
        {
            Text = "The Favor Ledger",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize + 4);
        title.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        header.AddChild(title);

        var close = new Button { Text = "Close" };
        close.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += Close;
        header.AddChild(close);

        vbox.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        vbox.AddChild(scroll);

        _rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_rows);

        _built = true;
        if (_pendingOpen)
        {
            _pendingOpen = false;
            Open();
        }
    }

    // ── Open / close ─────────────────────────────────────────────────────

    public void Toggle()
    {
        if (Visible)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public void Open()
    {
        if (!_built)
        {
            _pendingOpen = true;
            return;
        }
        RefreshRows();
        Visible = true;
    }

    public void Close() => Visible = false;

    // ── Rows ─────────────────────────────────────────────────────────────

    /// <summary>Rebuild the row list from the live ledger. Called on Open and
    /// after every call-in.</summary>
    public void RefreshRows()
    {
        if (!_built)
        {
            return;
        }

        foreach (Node child in _rows.GetChildren())
        {
            child.QueueFree();
        }

        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null || council.Ledger.Count == 0)
        {
            var empty = new Label
            {
                Text = "The ledger is empty. Favors are earned at court — " +
                       "Petition a Welcome court to mint one.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            empty.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
            empty.Modulate = UITheme.OverworldInfoLabelTint;
            _rows.AddChild(empty);
            return;
        }

        foreach (var favor in council.Ledger)
        {
            _rows.AddChild(BuildRow(council, favor));
        }
    }

    private Control BuildRow(CouncilState council, Favor favor)
    {
        var rowPanel = new PanelContainer();
        var rowStyle = new StyleBoxFlat
        {
            BgColor = UITheme.OverworldHudBg,
            BorderColor = UITheme.OverworldHudBorder,
            BorderWidthBottom = 1,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        rowPanel.AddThemeStyleboxOverride("panel", rowStyle);

        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        rowPanel.AddChild(h);

        var textBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        h.AddChild(textBox);

        string direction = favor.OwedToGuild ? "owed to the guild" : "owed BY the guild";
        string weight = favor.IsMajor ? "major" : "minor";
        var cycle = SaveManager.ActiveSave?.Cycle;
        string kingdomName = cycle != null
            ? CouncilTick.CourtDisplayName(cycle, favor.KingdomId)
            : favor.KingdomId;
        var line1 = new Label
        {
            Text = $"{favor.Type} ({weight}) — {direction} — " +
                   $"{CourtierName(council, favor)}, {kingdomName}  ·  minted L{favor.LunationMinted}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        line1.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        line1.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        textBox.AddChild(line1);

        if (!string.IsNullOrEmpty(favor.SourceDescription))
        {
            var line2 = new Label
            {
                Text = favor.SourceDescription,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            line2.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
            line2.Modulate = UITheme.OverworldInfoLabelTint;
            textBox.AddChild(line2);
        }

        string reason = GetIneligibilityReason?.Invoke(favor);
        if (reason == null)
        {
            var call = new Button { Text = "Call In" };
            call.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
            UITheme.ApplyButtonStyle(call, isPrimary: true);
            call.Pressed += () => OnCallIn?.Invoke(favor);
            h.AddChild(call);
        }
        else
        {
            var why = new Label
            {
                Text = reason,
                VerticalAlignment = VerticalAlignment.Center,
            };
            why.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
            why.Modulate = UITheme.OverworldInfoLabelTint;
            h.AddChild(why);
        }

        return rowPanel;
    }

    private static string CourtierName(CouncilState council, Favor favor)
    {
        if (council.Courts != null &&
            council.Courts.TryGetValue(favor.KingdomId, out var court))
        {
            var c = court.GetCourtier(favor.CourtierId);
            if (c != null)
            {
                return c.DisplayName;
            }
        }
        return favor.CourtierId;
    }
}
