using Godot;
using System;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusExpeditionPanel.cs
//
// Purpose:        The Expedition tab: world status, the door out
//                 to the strategic map, the post-Conjunction school
//                 picker. (The Scriptorium moved to the Scribe's
//                 Tower, CampusScribePanel, 2026-09-29.)
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 OverworldSpellRegistry.cs, SpellAcquisition.cs,
//                 PlayerSession.cs (CycleEndedByConjunction)
// See:            overworld_spell_system §8a (scroll pricing);
//                 docs/campus_tab_extraction_v1.md, Phase 2
// ============================================================

/// <summary>Expedition tab: where a cycle is surveyed and where it ends.
///
/// <para><b>What deliberately did NOT move here.</b> This cluster also contained
/// <c>EnsureCycleWorld</c> (world generation, corruption/kingdom sim resets, echo seeding,
/// roster rotation) and <c>BeginNextCycle</c> (archive a LoopRecord, replace CycleState,
/// reseed the deck). Those are cycle LIFECYCLE, not tab UI. They mutate the save far
/// beyond anything this panel displays, and <c>EnsureCycleWorld</c>'s own comment says it
/// belongs in a dedicated CycleInitializer. Pulling them into a UI class would have moved
/// them further from that destination. They stayed on CampusScreen and this panel reaches
/// them as named verbs on <see cref="CampusContext"/>.</para>
///
/// <para><b>The Scriptorium has moved.</b> R8 named the Scribe's Tower as scroll crafting's
/// owner; on 2026-09-29 it lifted out of here into <c>CampusScribePanel</c> as planned.</para>
///
/// <para>Extracted from <c>CampusScreen</c> on 2026-08-03. Rendering, wording and the
/// scribe transaction are unchanged.</para></summary>
public sealed class CampusExpeditionPanel : CampusPanel
{
    private Label _worldStatus;

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

        // Scroll crafting moved to the Scribe's Tower (2026-09-29, spec D7),
        // its promised home; see CampusScribePanel.
        layout.AddChild(new HSeparator());
        var scribeNote = new Label
        {
            Text = "Scrolls are scribed at the Scribe's Tower.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        scribeNote.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        scribeNote.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(scribeNote);

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
    // Leaving campus
    // ═══════════════════════════════════════════════════════════════════════

    private void OnOpenStrategicMap()
    {
        if (Ctx?.Save == null)
        {
            GD.Print("[Campus] No save loaded, cannot open strategic map.");
            return;
        }

        // If the last cycle ended at the Grand Conjunction, begin a new cycle first,
        // with school reselection (Option A: unlocked blueprints, campus, mastery, and
        // essence persist in the ledger; the deck resets to a starter).
        if (PlayerSession.CycleEndedByConjunction)
        {
            // TE1: the ended reading's ritual is its own screen now.
            Ctx.Host.GetTree().ChangeSceneToFile(TimelineEndScreen.ScenePath);
            return;
        }

        Ctx.EnterStrategicMap?.Invoke();
    }
}
