using Godot;
using System;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusExpeditionPanel.cs
//
// Purpose:        The Expedition tab — world status, the door out
//                 to the strategic map, the post-Conjunction school
//                 picker, and (interim home) the Scriptorium.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 OverworldSpellRegistry.cs, SpellAcquisition.cs,
//                 PlayerSession.cs (CycleEndedByConjunction)
// See:            overworld_spell_system §8a (scroll pricing);
//                 docs/campus_tab_extraction_v1.md — Phase 2
// ============================================================

/// <summary>Expedition tab: where a cycle is surveyed and where it ends.
///
/// <para><b>What deliberately did NOT move here.</b> This cluster also contained
/// <c>EnsureCycleWorld</c> (world generation, corruption/kingdom sim resets, echo seeding,
/// roster rotation) and <c>BeginNextCycle</c> (archive a LoopRecord, replace CycleState,
/// reseed the deck). Those are cycle LIFECYCLE, not tab UI — they mutate the save far
/// beyond anything this panel displays, and <c>EnsureCycleWorld</c>'s own comment says it
/// belongs in a dedicated CycleInitializer. Pulling them into a UI class would have moved
/// them further from that destination. They stayed on CampusScreen and this panel reaches
/// them as named verbs on <see cref="CampusContext"/>.</para>
///
/// <para><b>Interim home for the Scriptorium.</b> R8 confirmed the Scribe's Tower as scroll
/// crafting's real owner; it sits on this tab until the campus rework moves it. When it
/// does, it lifts out of here as its own panel — which is part of why the scroll list is a
/// self-contained method rather than woven into the world-status refresh.</para>
///
/// <para>Extracted from <c>CampusScreen</c> on 2026-08-03. Rendering, wording and the
/// scribe transaction are unchanged.</para></summary>
public sealed class CampusExpeditionPanel : CampusPanel
{
    private Label _worldStatus;
    private VBoxContainer _scriptoriumList;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 24);
        scroll.AddChild(margins);
        var layout = MakeVBox(16);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Set Out");

        var hint = new Label
        {
            Text = "The world stands as one map for this cycle. Open the strategic map " +
                   "to choose a staging point and launch a bounded expedition. Explore " +
                   "outward, secure outposts to unlock new staging grounds, and illuminate " +
                   "the world before the Grand Conjunction forces the final confrontation.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        hint.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(hint);

        layout.AddChild(new HSeparator());

        // ── World status panel ───────────────────────────────────────────
        var statusPanel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        statusPanel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Violet));
        layout.AddChild(statusPanel);

        var statusMargin = new MarginContainer();
        statusMargin.AddThemeConstantOverride("margin_left", 18);
        statusMargin.AddThemeConstantOverride("margin_right", 18);
        statusMargin.AddThemeConstantOverride("margin_top", 14);
        statusMargin.AddThemeConstantOverride("margin_bottom", 14);
        statusPanel.AddChild(statusMargin);

        _worldStatus = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _worldStatus.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _worldStatus.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        statusMargin.AddChild(_worldStatus);

        layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        // ── Launch button ────────────────────────────────────────────────
        var launchBtn = MakeButton("Open Strategic Map", 260, 52, UITheme.CampusBodyFontSize);
        launchBtn.Pressed += OnOpenStrategicMap;
        var btnRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        btnRow.AddChild(launchBtn);
        layout.AddChild(btnRow);

        // ── S4: Scriptorium — scroll crafting (overworld_spell_system §8a) ──
        // INTERIM HOME: R8 confirmed the Scribe's Tower as scroll crafting's
        // owner, but the campus is mid-rework (R6: no building dependencies
        // in v1), so the Scriptorium sits ungated on this tab until the
        // rework gates/moves it. Price = SpellAcquisition.ScrollGoldCost —
        // THE §8a balance lever; scrolls bypass the Essence economy.
        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Scriptorium — Scrolls");

        var scrollHint = new Label
        {
            Text = "A scroll holds one cast of a spell the guild knows — usable by any " +
                   "school, consuming no Essence, spent on use. Overt magic on a scroll " +
                   "is still witnessed.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        scrollHint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        scrollHint.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(scrollHint);

        _scriptoriumList = MakeVBox(6);
        layout.AddChild(_scriptoriumList);

        RefreshScriptorium();
        Refresh();
    }

    public override void Refresh()
    {
        if (_worldStatus == null)
            return;

        var save = Ctx?.Save;
        if (save == null)
        {
            _worldStatus.Text = "No guild loaded. Select a save slot first.";
            return;
        }

        var cycle = save.Cycle;
        bool worldExists = cycle?.World != null && cycle.World.Tiles.Length > 0;

        if (!worldExists)
        {
            _worldStatus.Text =
                $"Cycle {cycle?.CycleNumber ?? 1}: a new timeline awaits generation. " +
                "Opening the strategic map will weave the world.";
            return;
        }

        // Summarize discovery progress + staging options.
        var world = cycle.World;
        int explored = 0, charted = 0;
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            var d = world.Tiles[i].Discovery;
            if (d == TileDiscovery.Explored)
                explored++;
            else if (d == TileDiscovery.Charted)
                charted++;
        }
        float pct = world.Tiles.Length > 0 ? explored * 100f / world.Tiles.Length : 0f;
        int staging = 0;
        foreach (var sp in world.StagingPoints)
            if (sp.Available)
                staging++;
        int discoveredPois = 0;
        foreach (var p in world.Pois)
            if (p.Discovered)
                discoveredPois++;

        _worldStatus.Text =
            $"Cycle {cycle.CycleNumber}  ·  World {world.Width}×{world.Height}\n" +
            $"Illuminated: {pct:F1}%  ({explored} tiles explored, {charted} charted)\n" +
            $"Staging points available: {staging}\n" +
            $"Points of interest discovered: {discoveredPois}";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scriptorium (interim home — see class remarks)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>S4: rebuild the Scriptorium rows — one per scribable spell
    /// (the wizard's school innates + every learned spell; Attunements can't
    /// be scribed, and Emulate has nothing to remember on parchment).</summary>
    private void RefreshScriptorium()
    {
        if (_scriptoriumList == null)
            return;
        foreach (var child in _scriptoriumList.GetChildren())
            child.QueueFree();

        var save = Ctx?.Save;
        var grim = save?.Cycle?.Grimoire;
        if (grim == null)
            return;
        OverworldSpellRegistry.EnsureLoaded();

        // Scribable = school innates + known list, minus Attunements/Emulate.
        var scribable = new List<OverworldSpellDefinition>();
        void AddDef(OverworldSpellDefinition d)
        {
            if (d != null && !d.IsAttunement && d.EffectKey != "emulate" && !scribable.Contains(d))
                scribable.Add(d);
        }
        foreach (var innate in OverworldSpellRegistry.InnatesFor(save.Cycle.SelectedSchool))
            AddDef(innate);
        foreach (var id in grim.KnownSpellIds)
            AddDef(OverworldSpellRegistry.Get(id));
        scribable.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (scribable.Count == 0)
        {
            var none = new Label
            {
                Text = "The guild knows nothing worth scribing yet — spells are learned " +
                       "afield (lore sites, cordial deals, the dead).",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            none.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            none.Modulate = UITheme.CampusSubtleText;
            _scriptoriumList.AddChild(none);
            return;
        }

        foreach (var def in scribable)
        {
            int cost = SpellAcquisition.ScrollGoldCost(def);
            grim.ScrollInventory.TryGetValue(def.Id, out int held);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            _scriptoriumList.AddChild(row);

            var name = new Label
            {
                Text = $"{def.Name}  ·  {def.Magnitude}" + (held > 0 ? $"  ·  ×{held} held" : ""),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = def.Description,
            };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            name.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            row.AddChild(name);

            var craftBtn = MakeButton($"Scribe — {cost} g", 150, 34,
                UITheme.CampusSmallFontSize, isPrimary: false);
            craftBtn.Disabled = save.Gold < cost;
            string id = def.Id; // capture per-iteration
            craftBtn.Pressed += () =>
            {
                var s = Ctx?.Save;
                var g = s?.Cycle?.Grimoire;
                if (s == null || g == null || s.Gold < cost)
                    return;
                s.Gold -= cost;
                g.ScrollInventory[id] = g.ScrollInventory.TryGetValue(id, out int n) ? n + 1 : 1;
                SaveManager.MarkDirty();
                GD.Print($"[Scriptorium] Scribed '{id}' for {cost}g " +
                         $"(held ×{g.ScrollInventory[id]}, gold {s.Gold}).");
                // Narrow refresh, not RequestRefreshAll: scribing changes gold and this
                // list, nothing else. A full refresh would rebuild eight panels and drop
                // the player's scroll position mid-shopping.
                Ctx.RefreshGold?.Invoke();
                RefreshScriptorium();
            };
            row.AddChild(craftBtn);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Leaving campus
    // ═══════════════════════════════════════════════════════════════════════

    private void OnOpenStrategicMap()
    {
        if (Ctx?.Save == null)
        {
            GD.Print("[Campus] No save loaded — cannot open strategic map.");
            return;
        }

        // If the last cycle ended at the Grand Conjunction, begin a new cycle first —
        // with school reselection (Option A: unlocked blueprints, campus, mastery, and
        // essence persist in the ledger; the deck resets to a starter).
        if (PlayerSession.CycleEndedByConjunction)
        {
            ShowNewCycleSchoolPicker();
            return;
        }

        Ctx.EnterStrategicMap?.Invoke();
    }

    /// <summary>After a Conjunction, let the player choose the next cycle's school
    /// (the same school is allowed — they keep their unlocked card pool either way,
    /// but the deck rebuilds from a starter). Then begin the new cycle and open the
    /// freshly generated world.</summary>
    private void ShowNewCycleSchoolPicker()
    {
        var layer = new CanvasLayer { Name = "NewCycleUI" };
        // Parented to the shell, not to this panel's container: the picker must cover the
        // whole screen and outlive the tab it was opened from.
        Ctx.Host.AddChild(layer);

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.0f, 0.04f, 0.92f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(backdrop);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -280,
            OffsetRight = 280,
            OffsetTop = -200,
            OffsetBottom = 200,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        layer.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        var vbox = MakeVBox(14);
        margin.AddChild(vbox);

        var title = new Label { Text = "A New Timeline" };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        var body = new Label
        {
            Text = "Kassian weaves the world anew. Choose the school of this cycle. " +
                   "Everything you have learned — your card knowledge, your campus, your " +
                   "mastery — endures. Your deck begins again from its foundations.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        body.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        body.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        vbox.AddChild(body);

        vbox.AddChild(new HSeparator());

        string previousSchool = Ctx.Save.Cycle.SelectedSchool;

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        vbox.AddChild(grid);

        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
        {
            string schoolName = school.ToString();
            bool isPrevious = schoolName == previousSchool;

            var btn = new Button
            {
                Text = isPrevious ? $"{schoolName}  (again)" : schoolName,
                CustomMinimumSize = new Vector2(230, 44),
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: isPrevious);

            string captured = schoolName;
            // The panel frees its own picker; the shell owns only the cycle transition.
            // QueueFree is deferred to end-of-frame either way, so freeing before the
            // transition rather than inside it is not a behaviour change.
            btn.Pressed += () =>
            {
                layer.QueueFree();
                Ctx.BeginNextCycle?.Invoke(captured);
            };
            grid.AddChild(btn);
        }
    }
}
