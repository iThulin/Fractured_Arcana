using Godot;
using System;

// ============================================================
// HomeBuildingPanelHost.cs
//
// Purpose:        Floats a SINGLE campus panel over the live
//                 strategic/city view, so clicking a building in
//                 city view opens its menu in place (the world
//                 stays visible behind) instead of swapping to the
//                 full-screen CampusScene overlay. Closing returns
//                 to the city view, not the world map.
// Layer:          UI (strategic view)
// Collaborators:  StrategicView.cs (constructs one, from
//                 OnHomeBuildingPicked), CampusPanel.cs + the
//                 Campus*Panel bodies it hosts, CampusContext.cs
//                 (the seam it builds for the panel), UITheme.cs
// See:            claude/HANDOFF_phase2_campus_and_next.md
//                 (Immediate next step: retire the overlay)
// ============================================================

/// <summary>A CanvasLayer that hosts one <see cref="CampusPanel"/> as a right-docked card
/// over the live strategic scene. The city stays rendered behind it; the panel closes back
/// to the city view rather than the world.
///
/// <para><b>Why only some panels.</b> A floated panel is handed a <see cref="CampusContext"/>
/// built here, NOT by <c>CampusScreen</c>, so it cannot reach the shell's cycle lifecycle. The
/// panels whose context surface is fully satisfiable without that lifecycle (Guild, Companions,
/// Armory, Training, Records; they only use Save / Host / RefreshGold / RequestRefreshAll /
/// EnsureSaveSeeded) float here. Expedition (BeginNextCycle, EnterStrategicMap) and Quests /
/// Council (ShowNarrative with its persist-on-completion wiring) genuinely need the shell, so
/// <see cref="CanFloat"/> rejects them and StrategicView keeps routing those to the full overlay
/// until that machinery is generalized (Phase 3). Reconstructing half of it here would be a
/// parallel system that looks correct and silently drops persistence.</para></summary>
public sealed partial class HomeBuildingPanelHost : CanvasLayer
{
    /// <summary>Width of the docked panel strip, in px. Matches the Campus-tab list dock so the
    /// floated panel reads as the same surface the tab bar used to show.</summary>
    private const int CardWidth = 680;   // 560 → 680 (2026-08-19): subscreen content (disciplines table, save slots) was clipping tight

    private CampusPanelId? _panelId;  // null = no system panel (upgrade strip only)
    private string _title = "";
    private string _buildingId = "";  // non-empty → the tier/upgrade strip renders
    private Node _panelHost;          // what the panel reaches through (scene changes, dialogs)
    private Action _onClosed;         // fired on close so StrategicView returns to city view
    private CampusPanel _panel;

    // Upgrade strip widgets (relabeled after a purchase)
    private Label _tierLabel;
    private Button _upgradeBtn;

    /// <summary>True when <paramref name="id"/> is a panel this host can float today, i.e. one
    /// whose <see cref="CampusContext"/> needs no shell cycle/narrative lifecycle. Guard every
    /// call site with this; <see cref="CreatePanel"/> throws for anything else.</summary>
    public static bool CanFloat(CampusPanelId id) => id switch
    {
        CampusPanelId.Guild
            or CampusPanelId.Companions
            or CampusPanelId.Armory
            or CampusPanelId.Training
            or CampusPanelId.Records
            or CampusPanelId.Workshop
            or CampusPanelId.Quests => true,   // session-one extraction (2026-08-13)
        _ => false,
    };

    /// <summary>Build a host for panel <paramref name="id"/>. The caller adds it to the tree;
    /// the overlay itself is built in <see cref="_Ready"/> (deferred, per Godot 4.6 compat
    /// rules; see README §8). <paramref name="panelHost"/> is passed to the panel as
    /// <see cref="CampusContext.Host"/>; <paramref name="onClosed"/> runs when the player
    /// closes the panel.</summary>
    public static HomeBuildingPanelHost Create(Node panelHost, CampusPanelId? id, string title,
        Action onClosed, string buildingId = "",
        Action<NarrativeEncounterData> showNarrative = null,
        Action onBuildingChanged = null)
    {
        if (id.HasValue && !CanFloat(id.Value))
            throw new ArgumentOutOfRangeException(nameof(id), id, "panel is not floatable; guard with CanFloat");
        return new HomeBuildingPanelHost
        {
            Name = "HomeBuildingPanelHost",
            Layer = 50,
            _panelId = id,
            _title = title,
            _buildingId = buildingId ?? "",
            _panelHost = panelHost,
            _onClosed = onClosed,
            _showNarrative = showNarrative,
            _onBuildingChanged = onBuildingChanged,
        };
    }

    private Action<NarrativeEncounterData> _showNarrative;

    /// <summary>Fired after a tier purchase so the host can re-stamp visuals
    /// (the 3D grounds' building meshes are tier-keyed).</summary>
    private Action _onBuildingChanged;

    /// <summary>Refresh the hosted panel from outside, e.g. after a floated
    /// narrative resolves (the host applied the outcome; the panel re-reads).</summary>
    public void RefreshHostedPanel() => _panel?.Refresh();

    public override void _Ready() => CallDeferred(nameof(BuildOverlay));

    private void BuildOverlay()
    {
        // (2026-08-13, Magos request) The catcher is the CARD, not the screen:
        // clicks beside the card reach the atlas, so picking another building
        // SWAPS the panel instead of requiring close-then-click. The card
        // itself still stops input (PanelContainer + explicit filter), so
        // nothing leaks through the panel body to the grid behind it.
        var card = new PanelContainer
        {
            Name = "PanelCard",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        card.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        card.OffsetLeft = -CardWidth;
        // Below the global top bar (2026-08-19): the HUD now stays visible in city
        // view and its CanvasLayer draws over this card, so a full-height card had
        // its header (name + ✕ Close) buried under the bar.
        card.OffsetTop = HudManager.BarHeight;
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = UITheme.BgBase });
        AddChild(card);

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

        // Upgrade strip (2026-08-13): tier + upgrade verb, right under the
        // header, for ANY building this host opens: the city view's missing
        // upgrade path. Purchase goes through the one CampusConstruction core.
        if (!string.IsNullOrEmpty(_buildingId))
        {
            var strip = new HBoxContainer();
            strip.AddThemeConstantOverride("separation", 10);
            vbox.AddChild(strip);

            _tierLabel = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _tierLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            _tierLabel.AddThemeColorOverride("font_color", UITheme.TextDim);
            strip.AddChild(_tierLabel);

            _upgradeBtn = new Button();
            _upgradeBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            _upgradeBtn.Pressed += OnUpgradePressed;
            strip.AddChild(_upgradeBtn);

            vbox.AddChild(new HSeparator());
            RefreshUpgradeStrip();
        }

        // Panel body host. CampusPanel.Build fills a ScrollContainer (its contract).
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(scroll);

        // A live toast host; the CampusContext ctor requires one. The floatable panels don't
        // push toasts, but keep it valid and layered on top rather than passing null.
        var toasts = new ToastManager { Name = "FloatingPanelToasts" };
        AddChild(toasts);

        // The seam. Host routes scene changes / confirm dialogs. For a single floated panel,
        // "refresh all" means "redraw me" (panels call it from button handlers after a mutation).
        // refreshGold MUST be a no-op here: several panels (Armory, Training) call Ctx.RefreshGold
        // from INSIDE their Refresh(), so wiring it to _panel.Refresh() recurses to a stack
        // overflow. The float has no separate gold readout, so there is nothing to repaint;
        // gold-dependent widgets redraw via requestRefreshAll after a purchase. The lifecycle
        // verbs are unreachable for CanFloat panels, so they get inert fallbacks (EnterStrategicMap
        // closes back to the city) rather than half-built shell logic.
        var ctx = new CampusContext(
            host: _panelHost,
            toasts: toasts,
            showNarrative: enc => _showNarrative?.Invoke(enc),   // session one: real host
            requestRefreshAll: () => _panel?.Refresh(),
            refreshGold: () => { },
            enterStrategicMap: Close,
            beginNextCycle: _ => { },
            ensureSaveSeeded: () => { });

        _panel = _panelId.HasValue ? CreatePanel(_panelId.Value) : null;
        if (_panel == null) return;   // strip-only host (building with no system panel)
        _panel.Build(scroll, ctx);
        _panel.Refresh();
    }

    /// <summary>Repaint the tier label + upgrade button from the save, at
    /// build and after each purchase.</summary>
    private void RefreshUpgradeStrip()
    {
        if (_tierLabel == null || _upgradeBtn == null) return;
        var save = SaveManager.ActiveSave;
        var template = BuildingDatabase.GetTemplate(_buildingId);
        BuildingSaveData bs = null;
        if (save != null)
            foreach (var b in save.Buildings)
                if (b.Id == _buildingId) { bs = b; break; }

        if (save == null || template == null || bs == null)
        {
            _tierLabel.Text = "";
            _upgradeBtn.Visible = false;
            return;
        }

        var tierData = template.Tiers.Find(t => t.Tier == bs.Tier);
        _tierLabel.Text = $"Tier {bs.Tier}/{template.MaxTier}" +
                          (tierData != null && !string.IsNullOrEmpty(tierData.DisplayName)
                              ? $": {tierData.DisplayName}" : "");

        string reason = CampusConstruction.CannotBuildReason(save, _buildingId);
        var next = template.Tiers.Find(t => t.Tier == bs.Tier + 1);
        if (bs.Tier >= template.MaxTier || next == null)
        {
            _upgradeBtn.Visible = false;
            return;
        }
        _upgradeBtn.Visible = true;
        _upgradeBtn.Text = reason == null
            ? $"Upgrade ({next.GoldCost}g + {next.EffectiveMaterialsCost} mats)"
            : reason;
        _upgradeBtn.Disabled = reason != null;
        UITheme.ApplyButtonStyle(_upgradeBtn, isPrimary: reason == null);
    }

    private void OnUpgradePressed()
    {
        if (CampusConstruction.TryBuildOrUpgrade(SaveManager.ActiveSave, _buildingId))
        {
            RefreshUpgradeStrip();
            _panel?.Refresh();   // tier-gated panel content (e.g. Workshop verbs) updates live
            _onBuildingChanged?.Invoke();   // tier-keyed mesh re-stamps (2026-08-13)
        }
    }

    /// <summary>Close the floated panel and hand control back to the city view via
    /// <see cref="_onClosed"/>. Idempotent: the callback fires at most once.</summary>
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
        CampusPanelId.Workshop   => new CampusWorkshopPanel(),
        CampusPanelId.Quests     => new CampusQuestsPanel(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "not a floatable panel"),
    };
}
