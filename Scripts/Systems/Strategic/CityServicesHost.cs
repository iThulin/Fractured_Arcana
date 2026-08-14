using Godot;
using System;

// ============================================================
// CityServicesHost.cs
//
// Purpose:        Floats a visited enemy CAPITAL's services menu over
//                 the live strategic/city view — the Phase 3 "services"
//                 verb (shops / recruit / quests). A shell for now: the
//                 sections are placeholders, wired to nothing, so the
//                 interaction loop (enter capital → services panel →
//                 close back to the city) exists before the individual
//                 services are built out (P3.2+).
// Layer:          UI (strategic view)
// Collaborators:  StrategicView.cs (opens one when a capital is
//                 entered, closes it on leave), WorldAtlas3D.cs
//                 (ActiveCity), UITheme.cs
// See:            docs/world_locales_and_founding_spec_v1.md §4.2
//                 (city = a MODE offering services/siege/explore)
// ============================================================

/// <summary>A CanvasLayer that hosts a visited capital's "services" menu as a right-docked card
/// over the live city view. Mirrors <see cref="HomeBuildingPanelHost"/>, but for an NPC city the
/// content is a fixed set of service sections (Market / Recruit / Quests) rather than a hosted
/// campus panel — the guild's campus panels are bound to the guild's own save data and do not apply
/// to someone else's city. This is the SHELL: sections are placeholders until each service is built.</summary>
public sealed partial class CityServicesHost : CanvasLayer
{
    private const int CardWidth = 520;

    private string _cityName = "";
    private WorldSettlement _city;   // K3: Steward pricing + hall quality need the settlement
    private Action _onClosed;

    private VBoxContainer _recruitBox;   // rebuilt after each hire
    private Label _goldLabel;

    /// <summary>Build a services host for a visited city. The caller adds it to the tree; the UI is
    /// built in <see cref="_Ready"/> (deferred, per Godot 4.6 compat). <paramref name="onClosed"/>
    /// runs when the player closes it (StrategicView returns to the city view). <paramref name="city"/>
    /// may be null defensively; the Recruit section degrades to a placeholder without it.</summary>
    public static CityServicesHost Create(string cityName, WorldSettlement city, Action onClosed)
        => new CityServicesHost
        {
            Name = "CityServicesHost",
            Layer = 50,
            _cityName = cityName ?? "",
            _city = city,
            _onClosed = onClosed,
        };

    public override void _Ready() => CallDeferred(nameof(BuildOverlay));

    private void BuildOverlay()
    {
        // Full-rect input catcher: the city stays VISIBLE behind, but clicks off the card must not
        // reach the hex grid / camera (same lesson as the building panel + campus dimmer).
        var catcher = new Control { Name = "InputCatcher" };
        catcher.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        catcher.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(catcher);

        var card = new PanelContainer { Name = "ServicesCard" };
        card.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        card.OffsetLeft = -CardWidth;
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = UITheme.BgBase });
        catcher.AddChild(card);

        var margins = new MarginContainer();
        margins.AddThemeConstantOverride("margin_left", 18);
        margins.AddThemeConstantOverride("margin_right", 18);
        margins.AddThemeConstantOverride("margin_top", 14);
        margins.AddThemeConstantOverride("margin_bottom", 14);
        card.AddChild(margins);

        // (2026-08-13, Magos) The live Market + Hiring Hall outgrew the
        // screen — the section stack scrolls now (construct-card pattern).
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        margins.AddChild(scroll);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        vbox.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(vbox);

        // Header: city name + close.
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, 44) };
        vbox.AddChild(header);

        var titleLbl = new Label
        {
            Text = string.IsNullOrEmpty(_cityName) ? "City" : _cityName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        titleLbl.AddThemeColorOverride("font_color", UITheme.Gold);
        header.AddChild(titleLbl);

        var closeBtn = new Button { Text = "✕  Close" };
        closeBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(closeBtn, isPrimary: false);
        closeBtn.Pressed += Close;
        header.AddChild(closeBtn);

        var sub = new Label { Text = "A foreign capital. What business brings you here?" };
        sub.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(sub);

        // Service sections. Recruit (K3) and Market (Q4) are LIVE; Quests
        // remains a placeholder until its service is built.
        BuildMarketSection(vbox);
        BuildRecruitSection(vbox);
        AddService(vbox, "Quests", "Take contracts posted on the capital's board.");
    }

    // ── Q4: the city market (companion_item_systems v2.1 §7c) ────────────

    private VBoxContainer _marketBox;

    /// <summary>The live Market section: this lunation's shelf as item rows
    /// with Steward-priced Buy buttons. Stock from CityMarketService (lazy
    /// per-lunation refresh, persisted, no Legendaries — Auction House rule).</summary>
    private void BuildMarketSection(VBoxContainer parent)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (_city == null || cycle == null)
        {
            AddService(parent, "Market", "Buy items and cards from the city's traders.");
            return;
        }

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UITheme.BgCard,
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        parent.AddChild(panel);

        var section = new VBoxContainer();
        section.AddThemeConstantOverride("separation", 6);
        panel.AddChild(section);

        var title = new Label { Text = "Market" };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        section.AddChild(title);

        _marketBox = new VBoxContainer();
        _marketBox.AddThemeConstantOverride("separation", 6);
        section.AddChild(_marketBox);

        PopulateMarket();
    }

    /// <summary>(Re)fill the shelf — called at build and after each purchase,
    /// so stock, prices, and the hall's gold readout stay honest together.</summary>
    private void PopulateMarket()
    {
        if (_marketBox == null) return;
        foreach (var child in _marketBox.GetChildren())
            child.QueueFree();

        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle == null) return;

        var market = CityMarketService.GetOrRefresh(cycle, _city);
        if (market == null || market.StockItemIds.Count == 0)
        {
            var empty = new Label { Text = "The shelves are bare this lunation." };
            empty.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            empty.AddThemeColorOverride("font_color", UITheme.TextDim);
            _marketBox.AddChild(empty);
            return;
        }

        foreach (var itemId in new System.Collections.Generic.List<string>(market.StockItemIds))
        {
            var def = ItemDatabase.Get(itemId);
            if (def == null) continue;
            int price = CityMarketService.Price(cycle, _city, def);
            string capturedId = itemId;

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            _marketBox.AddChild(row);

            var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddChild(info);

            var name = new Label { Text = $"{def.Name}  ·  {def.Rarity} {def.Slot}" };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            name.AddThemeColorOverride("font_color", UITheme.RarityColor(def.Rarity));
            info.AddChild(name);

            var desc = new Label { Text = def.Description };
            desc.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            desc.AddThemeColorOverride("font_color", UITheme.TextDim);
            desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            info.AddChild(desc);

            var buy = new Button
            {
                Text = $"Buy ({price}g)",
                Disabled = save.Gold < price,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            buy.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(buy, isPrimary: save.Gold >= price);
            buy.Pressed += () =>
            {
                var msg = CityMarketService.TryBuy(cycle, _city, market, capturedId);
                if (msg != null) GD.Print($"[Market] {msg}");
                PopulateMarket();
                PopulateRecruits(); // shared gold readout stays honest
            };
            row.AddChild(buy);
        }
    }

    // ── K3: the hiring hall (companion_item_systems v2.1 §5a) ────────────

    /// <summary>The live Recruit section: this lunation's candidates as shared
    /// dossier cards with a priced Hire button each. Stock comes from
    /// HiringHallService (lazy per-lunation refresh, persisted).</summary>
    private void BuildRecruitSection(VBoxContainer parent)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (_city == null || cycle == null)
        {
            AddService(parent, "Recruit", "Hire mercenaries and sell-swords garrisoned here.");
            return;
        }

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UITheme.BgCard,
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        parent.AddChild(panel);

        var section = new VBoxContainer();
        section.AddThemeConstantOverride("separation", 6);
        panel.AddChild(section);

        var head = new HBoxContainer();
        section.AddChild(head);

        var title = new Label
        {
            Text = "Hiring Hall",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        head.AddChild(title);

        _goldLabel = new Label();
        _goldLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _goldLabel.AddThemeColorOverride("font_color", UITheme.Gold);
        head.AddChild(_goldLabel);

        _recruitBox = new VBoxContainer();
        _recruitBox.AddThemeConstantOverride("separation", 8);
        section.AddChild(_recruitBox);

        PopulateRecruits();
    }

    /// <summary>(Re)fill the candidate list — called at build and after each
    /// hire, so prices, gold, and the stock all stay honest without closing
    /// the menu.</summary>
    private void PopulateRecruits()
    {
        if (_recruitBox == null) return;
        foreach (var child in _recruitBox.GetChildren())
            child.QueueFree();

        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle == null) return;

        _goldLabel.Text = $"{save.Gold}g";

        var hall = HiringHallService.GetOrRefresh(cycle, _city);
        if (hall == null || hall.Candidates.Count == 0)
        {
            var empty = new Label { Text = "No one worth hiring this lunation. The hall refills as the moons turn." };
            empty.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            empty.AddThemeColorOverride("font_color", UITheme.TextDim);
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _recruitBox.AddChild(empty);
            return;
        }

        foreach (var c in hall.Candidates)
        {
            int price = HiringHallService.HirePrice(cycle, _city, c);
            string capturedId = c.Id;
            var card = CompanionDossier.Build(
                c,
                actionText: $"Hire ({price}g)",
                actionEnabled: save.Gold >= price,
                onAction: () =>
                {
                    var toast = HiringHallService.TryHire(cycle, _city, hall, capturedId);
                    if (toast != null) GD.Print($"[HiringHall] {toast}");
                    PopulateRecruits();
                });
            _recruitBox.AddChild(card);
        }
    }

    /// <summary>One service row: a header + a one-line description + a disabled "Coming soon" button.
    /// A placeholder until the service is implemented (P3.2+); it establishes the menu layout.</summary>
    private static void AddService(VBoxContainer parent, string title, string desc)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UITheme.BgCard,
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        parent.AddChild(panel);

        var row = new VBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        panel.AddChild(row);

        var head = new Label { Text = title };
        head.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        row.AddChild(head);

        var body = new Label { Text = desc };
        body.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(body);

        var btn = new Button { Text = "Coming soon", Disabled = true };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        row.AddChild(btn);
    }

    /// <summary>Close the services menu and hand control back to the city view via
    /// <see cref="_onClosed"/>. Idempotent — the callback fires at most once.</summary>
    public void Close()
    {
        var cb = _onClosed;
        _onClosed = null;
        cb?.Invoke();
        QueueFree();
    }
}
