using Godot;
using System;

// ============================================================
// HomeBuildingPanelHost.cs
//
// Purpose:        Floats a SINGLE campus panel over the live
//                 strategic/city view, so clicking a building in
//                 city view opens its menu in place — the world
//                 stays visible behind — instead of swapping to the
//                 full-screen CampusScene overlay. Closing returns
//                 to the city view, not the world map.
// Layer:          UI (strategic view)
// Collaborators:  StrategicView.cs (constructs one, from
//                 OnHomeBuildingPicked), CampusPanel.cs + the
//                 Campus*Panel bodies it hosts, CampusContext.cs
//                 (the seam it builds for the panel), UITheme.cs
// See:            claude/HANDOFF_phase2_campus_and_next.md
//                 (Immediate next step — retire the overlay)
// ============================================================

/// <summary>A CanvasLayer that hosts one <see cref="CampusPanel"/> as a right-docked card
/// over the live strategic scene. The city stays rendered behind it; the panel closes back
/// to the city view rather than the world.
///
/// <para><b>Why only some panels.</b> A floated panel is handed a <see cref="CampusContext"/>
/// built here, NOT by <c>CampusScreen</c>, so it cannot reach the shell's cycle lifecycle. The
/// panels whose context surface is fully satisfiable without that lifecycle — Guild, Companions,
/// Armory, Training, Records (they only use Save / Host / RefreshGold / RequestRefreshAll /
/// EnsureSaveSeeded) — float here. Expedition (BeginNextCycle, EnterStrategicMap) and Quests /
/// Council (ShowNarrative with its persist-on-completion wiring) genuinely need the shell, so
/// <see cref="CanFloat"/> rejects them and StrategicView keeps routing those to the full overlay
/// until that machinery is generalized (Phase 3). Reconstructing half of it here would be a
/// parallel system that looks correct and silently drops persistence.</para></summary>
public sealed partial class HomeBuildingPanelHost : CanvasLayer
{
    /// <summary>Width of the docked panel strip, in px. Matches the Campus-tab list dock so the
    /// floated panel reads as the same surface the tab bar used to show.</summary>
    private const int CardWidth = 560;

    private CampusPanelId _panelId;
    private string _title = "";
    private Node _panelHost;          // what the panel reaches through (scene changes, dialogs)
    private Action _onClosed;         // fired on close so StrategicView returns to city view
    private CampusPanel _panel;

    /// <summary>True when <paramref name="id"/> is a panel this host can float today — i.e. one
    /// whose <see cref="CampusContext"/> needs no shell cycle/narrative lifecycle. Guard every
    /// call site with this; <see cref="CreatePanel"/> throws for anything else.</summary>
    public static bool CanFloat(CampusPanelId id) => id switch
    {
        CampusPanelId.Guild
            or CampusPanelId.Companions
            or CampusPanelId.Armory
            or CampusPanelId.Training
            or CampusPanelId.Records => true,
        _ => false,
    };

    /// <summary>Build a host for panel <paramref name="id"/>. The caller adds it to the tree;
    /// the overlay itself is built in <see cref="_Ready"/> (deferred, per Godot 4.6 compat
    /// rules — README §8). <paramref name="panelHost"/> is passed to the panel as
    /// <see cref="CampusContext.Host"/>; <paramref name="onClosed"/> runs when the player
    /// closes the panel.</summary>
    public static HomeBuildingPanelHost Create(Node panelHost, CampusPanelId id, string title, Action onClosed)
    {
        if (!CanFloat(id))
            throw new ArgumentOutOfRangeException(nameof(id), id, "panel is not floatable — guard with CanFloat");
        return new HomeBuildingPanelHost
        {
            Name = "HomeBuildingPanelHost",
            Layer = 50,
            _panelId = id,
            _title = title,
            _panelHost = panelHost,
            _onClosed = onClosed,
        };
    }

    public override void _Ready() => CallDeferred(nameof(BuildOverlay));

    private void BuildOverlay()
    {
        // Full-rect input catcher: the world stays VISIBLE, but clicks off the card must not
        // fall through to the hex grid / camera behind it. MouseFilter.Stop is load-bearing —
        // the same lesson as CampusScreen._dimBackdrop. The card is a CHILD of the catcher, so
        // the panel's own buttons still receive events (children are hit-tested first).
        var catcher = new Control { Name = "InputCatcher" };
        catcher.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        catcher.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(catcher);

        // Right-docked card, full height, CardWidth wide.
        var card = new PanelContainer { Name = "PanelCard" };
        card.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        card.OffsetLeft = -CardWidth;
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = UITheme.BgBase });
        catcher.AddChild(card);

        var margins = new MarginContainer();
        margins.AddThemeConstantOverride("margin_left", 16);
        margins.AddThemeConstantOverride("margin_right", 16);
        margins.AddThemeConstantOverride("margin_top", 12);
        margins.AddThemeConstantOverride("margin_bottom", 12);
        card.AddChild(margins);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margins.AddChild(vbox);

        // Header: diegetic building name + close.
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, 40) };
        vbox.AddChild(header);

        var titleLbl = new Label
        {
            Text = _title,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        titleLbl.AddThemeColorOverride("font_color", UITheme.CampusTitleColor);
        header.AddChild(titleLbl);

        var closeBtn = new Button { Text = "✕  Close" };
        closeBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(closeBtn, isPrimary: false);
        closeBtn.Pressed += Close;
        header.AddChild(closeBtn);

        // Panel body host. CampusPanel.Build fills a ScrollContainer (its contract).
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(scroll);

        // A live toast host — the CampusContext ctor requires one. The floatable panels don't
        // push toasts, but keep it valid and layered on top rather than passing null.
        var toasts = new ToastManager { Name = "FloatingPanelToasts" };
        AddChild(toasts);

        // The seam. Host routes scene changes / confirm dialogs. For a single floated panel,
        // "refresh all" means "redraw me" (panels call it from button handlers after a mutation).
        // refreshGold MUST be a no-op here: several panels (Armory, Training) call Ctx.RefreshGold
        // from INSIDE their Refresh(), so wiring it to _panel.Refresh() recurses to a stack
        // overflow. The float has no separate gold readout, so there is nothing to repaint —
        // gold-dependent widgets redraw via requestRefreshAll after a purchase. The lifecycle
        // verbs are unreachable for CanFloat panels, so they get inert fallbacks (EnterStrategicMap
        // closes back to the city) rather than half-built shell logic.
        var ctx = new CampusContext(
            host: _panelHost,
            toasts: toasts,
            showNarrative: _ => { },
            requestRefreshAll: () => _panel?.Refresh(),
            refreshGold: () => { },
            enterStrategicMap: Close,
            beginNextCycle: _ => { },
            ensureSaveSeeded: () => { });

        _panel = CreatePanel(_panelId);
        _panel.Build(scroll, ctx);
        _panel.Refresh();
    }

    /// <summary>Close the floated panel and hand control back to the city view via
    /// <see cref="_onClosed"/>. Idempotent — the callback fires at most once.</summary>
    public void Close()
    {
        var cb = _onClosed;
        _onClosed = null;
        cb?.Invoke();
        QueueFree();
    }

    /// <summary>Instantiate the panel body for a floatable id. Throws for non-floatable ids so
    /// a missed <see cref="CanFloat"/> guard fails loudly instead of opening the wrong room.</summary>
    private static CampusPanel CreatePanel(CampusPanelId id) => id switch
    {
        CampusPanelId.Guild      => new CampusGuildPanel(),
        CampusPanelId.Companions => new CampusCompanionsPanel(),
        CampusPanelId.Armory     => new CampusArmoryPanel(),
        CampusPanelId.Training   => new CampusTrainingPanel(),
        CampusPanelId.Records    => new CampusRecordsPanel(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "not a floatable panel"),
    };
}
