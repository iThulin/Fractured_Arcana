using Godot;
using System;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusGuildPanel.cs
//
// Purpose:        The Guild tab — save slots, guild identity,
//                 last-run result, the card/deck screen doors,
//                 and the debug panel.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 SaveManager.cs, BuildingEffectApplier.cs,
//                 PlayerSession.cs
// See:            docs/campus_tab_extraction_v1.md — Phase 2
// ============================================================

/// <summary>Guild tab. The last and largest extraction, and the least homogeneous: it mixes
/// three unrelated things that only share a tab because a tab was the only place to put them.
///
/// <para><b>Save slots do not belong on the campus map.</b> Slot selection, creation and
/// deletion are CHROME — you cannot put "load a save" behind a building, because the building
/// only exists once a save is loaded. They live here now because they render into this tab
/// today; in Phase 3 <see cref="RefreshSlots"/> and <see cref="SelectSlot"/> lift out to the
/// Menu overlay alongside settings and quit, and what remains of this panel is what the Grand
/// Hall actually opens: guild identity and the record of your cycles.</para>
///
/// <para><b>The debug panel is also chrome</b> and should follow the slots out, not sit behind
/// a diegetic door.</para>
///
/// <para>Extracted from <c>CampusScreen</c> on 2026-08-03. The one deliberate deletion is
/// noted inline in <see cref="SelectSlot"/>.</para></summary>
public sealed class CampusGuildPanel : CampusPanel
{
    /// <summary>Which slot row renders as active. Public because the shell seeds it from
    /// SaveManager.ActiveSlot when a save is already loaded at BuildUI time.</summary>
    public int SelectedSlot = -1;

    private VBoxContainer _slotContainer;
    private Label _summaryLabel;
    private CheckBox _debugCheckbox;
    private PanelContainer _debugPanel;
    private OptionButton _forceEncounterDropdown;
    private VBoxContainer _guildIdentityContainer;
    private VBoxContainer _guildResultContainer;
    private VBoxContainer _declarationContainer;

    // Last-run result snapshot (see RefreshGuildResultPanel): RunResultData is
    // CONSUMED on first read because StrategicView's warfront resolution keys on
    // HasResults per run — but Refresh() fires twice on campus boot (RefreshAll +
    // the tab-switch case), so rendering directly from RunResultData showed the
    // banner for exactly one frame and then destroyed it. Cache what was consumed.
    private bool _resultCached;
    private bool _resultWon;
    private int _resultGold, _resultSplinters, _resultEncounters, _resultHp;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(16);
        margins.AddChild(layout);

        // ── Guild identity — filled by RefreshGuildTab() ─────────────────
        _guildIdentityContainer = MakeVBox(0);
        layout.AddChild(_guildIdentityContainer);

        // ── Last run result — filled by RefreshGuildTab() ────────────────
        _guildResultContainer = MakeVBox(0);
        layout.AddChild(_guildResultContainer);

        // ── Discipline — filled by RefreshDeclarations() ─────────────────
        // The Grand Hall hosts this panel, and the building's own description
        // already promises "school of study". It is also the room the conferral
        // stopped in, which is why declaring happens here and nowhere else.
        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Disciplines — what you can play, and why");
        _declarationContainer = MakeVBox(8);
        layout.AddChild(_declarationContainer);

        // ── Save slots ───────────────────────────────────────────────────
        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Save Slots");
        _slotContainer = MakeVBox(6);
        layout.AddChild(_slotContainer);

        // ── Card management ──────────────────────────────────────────────
        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Cards");

        var cardRow = new HBoxContainer();
        cardRow.AddThemeConstantOverride("separation", 10);
        cardRow.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        layout.AddChild(cardRow);

        var libBtn = MakeButton("Card Library", 160, 40, 15);
        libBtn.Pressed += () => Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/UI/CardLibrary.tscn");
        cardRow.AddChild(libBtn);

        var deckBtn = MakeButton("Manage Deck", 160, 40, 15);
        deckBtn.Pressed += () => Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/UI/DeckEditor.tscn");
        cardRow.AddChild(deckBtn);

        var upgradeBtn = MakeButton("Upgrade Cards", 160, 40, 15);
        upgradeBtn.Pressed += () => Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/UI/CardUpgradeScreen.tscn");
        cardRow.AddChild(upgradeBtn);

        // ── Debug ────────────────────────────────────────────────────────
        layout.AddChild(new HSeparator());

        _debugCheckbox = new CheckBox
        {
            Text = "Debug Mode",
            ButtonPressed = PlayerSession.DebugMode,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        layout.AddChild(_debugCheckbox);

        _debugPanel = BuildDebugPanel();
        _debugPanel.Visible = PlayerSession.DebugMode;
        layout.AddChild(_debugPanel);

        _debugCheckbox.Toggled += (on) =>
        {
            PlayerSession.DebugMode = on;
            _debugPanel.Visible = on;
            if (!on)
            {
                PlayerSession.NoFog = false;
                PlayerSession.UnlimitedSteps = false;
                PlayerSession.GodModeHP = false;
                PlayerSession.StartWithGold = false;
                PlayerSession.StartWithSplinters = false;
                PlayerSession.SkipDeployment = false;
                PlayerSession.ForceNextEncounterType = -1;
                PlayerSession.DebugRevealStrategicMap = false;
                PlayerSession.DebugGrantStagingArmed = false;
            }
        };

        _summaryLabel = new Label { Visible = false };
        layout.AddChild(_summaryLabel);

    }

    public override void Refresh()
    {
        RefreshGuildIdentityPanel();
        RefreshGuildResultPanel();
        RefreshDeclarations();
        RefreshSlots();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Declare a Discipline (design doc §7)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One row per non-Adept school: declared, declarable, or locked with the
    /// single most actionable reason why. Eligibility is recomputed on every
    /// refresh because it depends on cycle state (companions, dispositions) that
    /// changes underneath this panel.
    /// </summary>
    private void RefreshDeclarations()
    {
        if (_declarationContainer == null) return;
        foreach (var child in _declarationContainer.GetChildren())
            child.QueueFree();

        var save = Ctx.Save;
        if (save == null)
        {
            _declarationContainer.AddChild(MakeStubLabel("No guild loaded."));
            return;
        }

        int declaredCount = DeclarationService.DeclaredSchools(save).Count;   // includes Adept

        // One line, not a paragraph (2026-08-04 playtest: "way too much text").
        // The checklist rows below carry the specifics.
        var intro = new Label
        {
            Text = "Declared disciplines are permanent and playable at every new timeline. " +
                   "Declaring takes a teacher, that school's mastery, and the Grand Hall.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        intro.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        intro.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        _declarationContainer.AddChild(intro);

        var tally = new Label { Text = $"{declaredCount} of 8 disciplines yours." };
        tally.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        tally.AddThemeColorOverride("font_color", UITheme.Gold);
        _declarationContainer.AddChild(tally);

        // Adept first — always playable, never declared. Skipping it entirely
        // read as a hole in the list during playtest.
        _declarationContainer.AddChild(BuildAdeptRow());

        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
        {
            string name = school.ToString();
            if (name == DeclarationService.StartingSchool) continue;   // row above

            _declarationContainer.AddChild(BuildDeclarationRow(save, name));
        }
    }

    /// <summary>The one row that needs no evaluation: where every guild begins.</summary>
    private Control BuildAdeptRow()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var mark = new Label { Text = "✦", CustomMinimumSize = new Vector2(24, 0) };
        mark.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        mark.AddThemeColorOverride("font_color", UITheme.Gold);
        row.AddChild(mark);

        var nameLbl = new Label { Text = "Adept", CustomMinimumSize = new Vector2(130, 0) };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        nameLbl.AddThemeColorOverride("font_color", UITheme.Gold);
        row.AddChild(nameLbl);

        var detail = new Label
        {
            Text = "Always yours.",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        detail.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        detail.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        row.AddChild(detail);
        return row;
    }

    /// <summary>
    /// One school, ONE line (2026-08-04 playtest: the prose blockers buried the
    /// two facts that matter — unlock status and whether a teacher exists).
    /// Glyph + name + a fixed status line: "Teacher … · Mastery x/y · Grand Hall …"
    /// for undeclared schools, "Declared · Mastery n" for declared ones. The
    /// Declare button appears only when everything is met.
    /// </summary>
    private Control BuildDeclarationRow(GuildSaveData save, string school)
    {
        var status = DeclarationService.Evaluate(save, school);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // Status glyph: ✦ declared · → declarable now · 🔒 locked
        var mark = new Label
        {
            Text = status.Declared ? "✦" : status.Eligible ? "→" : "🔒",
            CustomMinimumSize = new Vector2(24, 0),
        };
        mark.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        mark.AddThemeColorOverride("font_color",
            status.Declared ? UITheme.Gold
            : status.Eligible ? UITheme.HealthGreen
            : UITheme.CampusSubtleText);
        row.AddChild(mark);

        var nameLbl = new Label
        {
            Text = school,
            CustomMinimumSize = new Vector2(130, 0),
        };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        nameLbl.AddThemeColorOverride("font_color",
            status.Declared ? UITheme.Gold : UITheme.TextPrimary);
        row.AddChild(nameLbl);

        var detail = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        detail.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);

        if (status.Declared)
        {
            detail.Text = $"Declared  ·  Mastery {status.MasteryPoints}";
            detail.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
            row.AddChild(detail);
            return row;
        }

        // The fixed status line — same three items in the same order on every
        // undeclared row, so standing is comparable at a glance.
        bool hasHall = DeclarationService.HasGrandHall(save);
        bool hasTeacher = status.FacultySource != null;
        bool hasMastery = status.MasteryPoints >= status.MasteryRequired;
        detail.Text =
            $"Teacher {(hasTeacher ? "✓ " + status.FacultySource : "—")}   ·   " +
            $"Mastery {status.MasteryPoints}/{status.MasteryRequired}{(hasMastery ? " ✓" : "")}   ·   " +
            $"Grand Hall {(hasHall ? "✓" : "—")}";
        detail.AddThemeColorOverride("font_color",
            status.Eligible ? UITheme.HealthGreen : UITheme.CampusSubtleText);
        row.AddChild(detail);

        if (status.Eligible)
        {
            var btn = MakeButton($"Declare {school}", 170, 34, UITheme.CampusSmallFontSize);
            string captured = school;
            btn.Pressed += () => OnDeclarePressed(captured);
            row.AddChild(btn);
        }

        return row;
    }

    private void OnDeclarePressed(string school)
    {
        var save = Ctx.Save;
        if (save == null) return;

        // Capture the teacher before declaring — Evaluate reports FacultySource
        // for a declared school too, but reading it first keeps the conferral
        // line honest if the roster shifts underneath us.
        string faculty = DeclarationService.FindFacultySource(save, school);

        if (!DeclarationService.Declare(save, school))
        {
            // Declare re-checks and logs its own reason. The button should not
            // have been reachable, so just resync the panel.
            RefreshDeclarations();
            return;
        }

        SaveManager.Save();

        // The Provost's sentence from Beat 2, finally finishing — in the voice of
        // whoever stayed to speak it. The player spends this moment seven times.
        var dialog = new AcceptDialog
        {
            Title = "The Conferral",
            DialogText = DeclarationService.ConferralLine(school, faculty),
        };
        Ctx.Host.AddChild(dialog);
        dialog.Confirmed += () => dialog.QueueFree();
        dialog.Canceled += () => dialog.QueueFree();
        dialog.PopupCentered();

        Ctx.RequestRefreshAll?.Invoke();
    }

    private void RefreshGuildIdentityPanel()
    {
        if (_guildIdentityContainer == null)
            return;
        foreach (var child in _guildIdentityContainer.GetChildren())
            child.QueueFree();

        var save = Ctx.Save;

        var identityPanel = new PanelContainer();
        var identityStyle = new StyleBoxFlat
        {
            BgColor = UITheme.BgRaised,
            BorderColor = save != null ? UITheme.Violet : UITheme.NeutralDim,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        identityPanel.AddThemeStyleboxOverride("panel", identityStyle);
        identityPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _guildIdentityContainer.AddChild(identityPanel);

        var identityMargin = new MarginContainer();
        identityMargin.AddThemeConstantOverride("margin_left", 20);
        identityMargin.AddThemeConstantOverride("margin_right", 20);
        identityMargin.AddThemeConstantOverride("margin_top", 14);
        identityMargin.AddThemeConstantOverride("margin_bottom", 14);
        identityPanel.AddChild(identityMargin);

        var identityVBox = MakeVBox(6);
        identityMargin.AddChild(identityVBox);

        if (save == null)
        {
            var noSaveLabel = new Label
            {
                Text = "No guild selected — choose a save slot below.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            noSaveLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            noSaveLabel.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
            identityVBox.AddChild(noSaveLabel);
            return;
        }

        // Guild name + school badge
        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 12);
        identityVBox.AddChild(nameRow);

        var guildNameLabel = new Label
        {
            Text = save.GuildName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        guildNameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize + 2);
        guildNameLabel.AddThemeColorOverride("font_color", UITheme.Gold);
        nameRow.AddChild(guildNameLabel);

        var schoolBadge = new Label { Text = save.SelectedSchool };
        schoolBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        schoolBadge.AddThemeColorOverride("font_color", UITheme.Violet);
        nameRow.AddChild(schoolBadge);

        // Stats row
        var statsRow = new HBoxContainer();
        statsRow.AddThemeConstantOverride("separation", 24);
        identityVBox.AddChild(statsRow);

        void AddStat(string label, string value)
        {
            var col = MakeVBox(2);
            var lbl = new Label { Text = label };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            lbl.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
            col.AddChild(lbl);
            var val = new Label { Text = value };
            val.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            val.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            col.AddChild(val);
            statsRow.AddChild(col);
        }

        AddStat("RUNS", $"{save.TotalRuns}");
        AddStat("WON", $"{save.RunsWon}");
        AddStat("LOST", $"{save.RunsLost}");
        AddStat("GOLD EARNED", $"{save.TotalGoldEarned}");
        AddStat("REGION", save.CurrentRegionId.Replace("_", " ").ToUpper());

        // Phase 2 — where the guild is SITED in the world: home city + region +
        // the resolved campus dock. Full-width line (not a stat column) so the
        // longer text doesn't crowd the stats row. Falls back to the founding
        // realm before the cycle's world is generated.
        var seatLbl = new Label { Text = $"SEAT  ·  {SeatDescription(save)}" };
        seatLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        seatLbl.AddThemeColorOverride("font_color", UITheme.Violet);
        identityVBox.AddChild(seatLbl);
    }

    /// <summary>Where the guild is sited in the world (Phase 2). Prefers the live
    /// home city + region once the cycle's world exists; before that, the founding
    /// realm. Appends the resolved campus entry dock.</summary>
    private static string SeatDescription(GuildSaveData save)
    {
        string dock = save?.Ledger?.CampusMap?.EntryDockType;
        string dockSuffix = string.IsNullOrEmpty(dock) ? "" : $"  ·  {dock}";

        var world = save?.Cycle?.World;
        if (world != null && world.Tiles.Length > 0 && world.InBounds(world.HomeX, world.HomeY))
        {
            string kid = world.GetTile(world.HomeX, world.HomeY).KingdomId;
            string region = "";
            if (!string.IsNullOrEmpty(kid) && save.Cycle.Kingdoms != null &&
                save.Cycle.Kingdoms.TryGetValue(kid, out var ks))
                region = !string.IsNullOrEmpty(ks.DisplayName) ? ks.DisplayName : ks.TemplateRegionId;

            var home = world.SettlementAt(world.HomeX, world.HomeY);
            string city = home != null && !string.IsNullOrEmpty(home.Name) ? home.Name : "";

            string place =
                !string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(region) ? $"{city}, {region}"
                : !string.IsNullOrEmpty(region) ? region
                : !string.IsNullOrEmpty(city) ? city
                : "the frontier";
            return $"{place}{dockSuffix}";
        }

        string realm = save?.Ledger?.FoundingScenario?.DisplayName;
        return string.IsNullOrEmpty(realm)
            ? $"to be established{dockSuffix}"
            : $"{realm} (unentered){dockSuffix}";
    }

    private void RefreshGuildResultPanel()
    {
        if (_guildResultContainer == null)
            return;
        foreach (var child in _guildResultContainer.GetChildren())
            child.QueueFree();

        // Consume-then-cache: take the fresh result if one arrived, then always
        // render from the cache so a second Refresh in the same session rebuilds
        // the banner instead of erasing it.
        if (RunResultData.HasResults)
        {
            _resultCached = true;
            _resultWon = RunResultData.ReachedObjective;
            _resultGold = RunResultData.GoldEarned;
            _resultSplinters = RunResultData.ArcaneSplinters;
            _resultEncounters = RunResultData.EncountersWon;
            _resultHp = RunResultData.HPRemaining;
            RunResultData.Clear();
        }

        if (!_resultCached)
            return;

        bool won = _resultWon;

        var resultPanel = new PanelContainer();
        var resultStyle = new StyleBoxFlat
        {
            BgColor = won
                ? new Color(0.05f, 0.18f, 0.05f, 0.9f)
                : new Color(0.18f, 0.05f, 0.05f, 0.9f),
            BorderColor = won ? UITheme.Success : UITheme.Danger,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 3,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
        };
        resultPanel.AddThemeStyleboxOverride("panel", resultStyle);
        resultPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _guildResultContainer.AddChild(resultPanel);

        var resultMargin = new MarginContainer();
        resultMargin.AddThemeConstantOverride("margin_left", 16);
        resultMargin.AddThemeConstantOverride("margin_right", 16);
        resultMargin.AddThemeConstantOverride("margin_top", 10);
        resultMargin.AddThemeConstantOverride("margin_bottom", 10);
        resultPanel.AddChild(resultMargin);

        var resultVBox = MakeVBox(6);
        resultMargin.AddChild(resultVBox);

        var resultTitle = new Label
        {
            Text = won ? "✓  Last Expedition — Success" : "✗  Last Expedition — Failed",
        };
        resultTitle.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        resultTitle.AddThemeColorOverride("font_color", won ? UITheme.Success : UITheme.Danger);
        resultVBox.AddChild(resultTitle);

        var resultRow = new HBoxContainer();
        resultRow.AddThemeConstantOverride("separation", 20);
        resultVBox.AddChild(resultRow);

        void AddResult(string label, string value)
        {
            var lbl = new Label { Text = $"{label}  {value}" };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            lbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            resultRow.AddChild(lbl);
        }

        AddResult("Gold:", $"{_resultGold}");
        AddResult("Splinters:", $"{_resultSplinters}");
        AddResult("Encounters:", $"{_resultEncounters}");
        AddResult("HP:", $"{_resultHp}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tab builders
    // ═══════════════════════════════════════════════════════════════════════

    private PanelContainer BuildDebugPanel()
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.DebugPanelBg,
            BorderColor = UITheme.DebugPanelBorder,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            ContentMarginLeft = UITheme.PaddingNormal + 4,
            ContentMarginRight = UITheme.PaddingNormal + 4,
            ContentMarginTop = UITheme.PaddingNormal,
            ContentMarginBottom = UITheme.PaddingNormal,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 20);
        grid.AddThemeConstantOverride("v_separation", 6);
        panel.AddChild(grid);

        CheckBox MakeDebugCheck(string label, bool current, Action<bool> onChange)
        {
            var cb = new CheckBox { Text = label, ButtonPressed = current };
            cb.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            cb.Toggled += (on) => onChange(on);
            return cb;
        }

        grid.AddChild(MakeDebugCheck("No Fog in Expedition", PlayerSession.NoFog,
            on => PlayerSession.NoFog = on));
        grid.AddChild(MakeDebugCheck("Unlimited Steps", PlayerSession.UnlimitedSteps,
            on => PlayerSession.UnlimitedSteps = on));
        grid.AddChild(MakeDebugCheck("God Mode HP", PlayerSession.GodModeHP,
            on => PlayerSession.GodModeHP = on));
        grid.AddChild(MakeDebugCheck("Start With Gold", PlayerSession.StartWithGold,
            on => PlayerSession.StartWithGold = on));
        grid.AddChild(MakeDebugCheck("Start With Splinters", PlayerSession.StartWithSplinters,
            on => PlayerSession.StartWithSplinters = on));
        grid.AddChild(MakeDebugCheck("Skip Deployment", PlayerSession.SkipDeployment,
            on => PlayerSession.SkipDeployment = on));
        grid.AddChild(MakeDebugCheck("Reveal Strategic Map", PlayerSession.DebugRevealStrategicMap,
            on => PlayerSession.DebugRevealStrategicMap = on));
        grid.AddChild(MakeDebugCheck("No Enemy Ambushes", PlayerSession.DebugNoAmbush,
            on => PlayerSession.DebugNoAmbush = on));
        grid.AddChild(MakeDebugCheck("Grant Staging (press G in expedition)",
            PlayerSession.DebugGrantStagingArmed,
            on => PlayerSession.DebugGrantStagingArmed = on));

        var forceLabel = new Label { Text = "Force Next POI:" };
        forceLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        grid.AddChild(forceLabel);

        _forceEncounterDropdown = new OptionButton { CustomMinimumSize = new Vector2(140, 28) };
        _forceEncounterDropdown.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _forceEncounterDropdown.AddItem("None (normal)", -1);
        _forceEncounterDropdown.AddItem("Combat", (int)OverworldHex.POIType.Combat);
        _forceEncounterDropdown.AddItem("Rest", (int)OverworldHex.POIType.Rest);
        _forceEncounterDropdown.AddItem("Narrative", (int)OverworldHex.POIType.Narrative);
        _forceEncounterDropdown.AddItem("Negotiation", (int)OverworldHex.POIType.Negotiation);
        _forceEncounterDropdown.Selected = 0;
        _forceEncounterDropdown.ItemSelected += (idx) =>
            PlayerSession.ForceNextEncounterType =
                _forceEncounterDropdown.GetItemId((int)idx);
            grid.AddChild(_forceEncounterDropdown);

        // ── C4 verification dumps (CouncilDebug.cs) ──────────────────────
        var dumpEchoesBtn = new Button
        {
            Text = "Dump Echoes",
            CustomMinimumSize = new Vector2(140, 28),
        };
        dumpEchoesBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        dumpEchoesBtn.Pressed += () => CouncilDebug.DumpEchoes();
        grid.AddChild(dumpEchoesBtn);

        var dumpRegardBtn = new Button
        {
            Text = "Dump Regard",
            CustomMinimumSize = new Vector2(140, 28),
        };
        dumpRegardBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        dumpRegardBtn.Pressed += () => CouncilDebug.DumpRegard();
        grid.AddChild(dumpRegardBtn);

        // ── Save-adjacency round-trip assertions (CouncilSaveAssert.cs) ──
        var assertRtBtn = new Button
        {
            Text = "Assert Round-Trips",
            CustomMinimumSize = new Vector2(140, 28),
        };
        assertRtBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        assertRtBtn.Pressed += () => CouncilSaveAssert.AssertAll();
        grid.AddChild(assertRtBtn);

        // ── Espionage E2 verification (ShadowTick / ConcordDebug.cs) ─────
        var plantWatcherBtn = new Button
        {
            Text = "Plant Watcher",
            CustomMinimumSize = new Vector2(140, 28),
        };
        plantWatcherBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        plantWatcherBtn.Pressed += () => ConcordDebug.DebugPlantWatcher();
        grid.AddChild(plantWatcherBtn);

        var contactConcordBtn = new Button
        {
            Text = "Contact Concord",
            CustomMinimumSize = new Vector2(140, 28),
        };
        contactConcordBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        contactConcordBtn.Pressed += () => ConcordDebug.DebugContactConcord();
        grid.AddChild(contactConcordBtn);

        var dumpShadowBtn = new Button
        {
            Text = "Dump Shadow",
            CustomMinimumSize = new Vector2(140, 28),
        };
        dumpShadowBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        dumpShadowBtn.Pressed += () =>
        {
            ConcordDebug.DumpNodes();
            ConcordDebug.DumpShadow();
        };
        grid.AddChild(dumpShadowBtn);

        // ── Espionage E3 marketplace (ShadowMarket / ConcordDebug.cs) ────
        var grantFavorBtn = new Button
        {
            Text = "+50 Favor",
            CustomMinimumSize = new Vector2(140, 28),
        };
        grantFavorBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        grantFavorBtn.Pressed += () => ConcordDebug.DebugGrantFavor();
        grid.AddChild(grantFavorBtn);

        var sellSecretBtn = new Button
        {
            Text = "Sell Secret",
            CustomMinimumSize = new Vector2(140, 28),
        };
        sellSecretBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        sellSecretBtn.Pressed += () => ConcordDebug.DebugSellSecret();
        grid.AddChild(sellSecretBtn);

        var buyPlantBtn = new Button
        {
            Text = "Buy: Plant",
            CustomMinimumSize = new Vector2(140, 28),
        };
        buyPlantBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        buyPlantBtn.Pressed += () => ConcordDebug.DebugCommissionPlant();
        grid.AddChild(buyPlantBtn);

        var buyIntelBtn = new Button
        {
            Text = "Buy: Intel",
            CustomMinimumSize = new Vector2(140, 28),
        };
        buyIntelBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        buyIntelBtn.Pressed += () => ConcordDebug.DebugCommissionIntel();
        grid.AddChild(buyIntelBtn);

        var buyTheftBtn = new Button
        {
            Text = "Buy: Theft",
            CustomMinimumSize = new Vector2(140, 28),
        };
        buyTheftBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        buyTheftBtn.Pressed += () => ConcordDebug.DebugCommissionTheft();
        grid.AddChild(buyTheftBtn);

        // ── Espionage E4 sabotage & false echoes (ShadowOps / ShadowMarket) ─
        var plantSaboteurBtn = new Button
        {
            Text = "Plant Saboteur",
            CustomMinimumSize = new Vector2(140, 28),
        };
        plantSaboteurBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        plantSaboteurBtn.Pressed += () => ConcordDebug.DebugPlantSaboteur();
        grid.AddChild(plantSaboteurBtn);

        var saboteurStrikeBtn = new Button
        {
            Text = "Saboteur Strike",
            CustomMinimumSize = new Vector2(140, 28),
        };
        saboteurStrikeBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        saboteurStrikeBtn.Pressed += () => ConcordDebug.DebugSaboteurStrike();
        grid.AddChild(saboteurStrikeBtn);

        var forgeEchoBtn = new Button
        {
            Text = "Forge Echo",
            CustomMinimumSize = new Vector2(140, 28),
        };
        forgeEchoBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        forgeEchoBtn.Pressed += () => ConcordDebug.DebugForgeEcho();
        grid.AddChild(forgeEchoBtn);

        var buySabotageSiegeBtn = new Button
        {
            Text = "Buy: Sabotage Siege",
            CustomMinimumSize = new Vector2(140, 28),
        };
        buySabotageSiegeBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        buySabotageSiegeBtn.Pressed += () => ConcordDebug.DebugBuySabotageSiege();
        grid.AddChild(buySabotageSiegeBtn);

        var buySabotageCorrBtn = new Button
        {
            Text = "Buy: Sabotage Corr.",
            CustomMinimumSize = new Vector2(140, 28),
        };
        buySabotageCorrBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        buySabotageCorrBtn.Pressed += () => ConcordDebug.DebugBuySabotageCorruption();
        grid.AddChild(buySabotageCorrBtn);

        // ── Espionage E5 shadow war (ShadowMarket / CouncilTick) ─────────
        var forceMarkedBtn = new Button
        {
            Text = "Marked → 9",
            CustomMinimumSize = new Vector2(140, 28),
        };
        forceMarkedBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        forceMarkedBtn.Pressed += () => ConcordDebug.DebugForceMarked();
        grid.AddChild(forceMarkedBtn);

        var outbidBtn = new Button
        {
            Text = "Outbid",
            CustomMinimumSize = new Vector2(140, 28),
        };
        outbidBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        outbidBtn.Pressed += () => ConcordDebug.DebugOutbid();
        grid.AddChild(outbidBtn);

        var imprisonBtn = new Button
        {
            Text = "Imprison Envoy",
            CustomMinimumSize = new Vector2(140, 28),
        };
        imprisonBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        imprisonBtn.Pressed += () => ConcordDebug.DebugImprisonEnvoy();
        grid.AddChild(imprisonBtn);

        var buyExtractionBtn = new Button
        {
            Text = "Buy: Extraction",
            CustomMinimumSize = new Vector2(140, 28),
        };
        buyExtractionBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        buyExtractionBtn.Pressed += () => ConcordDebug.DebugBuyExtraction();
        grid.AddChild(buyExtractionBtn);

        // ── Espionage E6 Tier C + the spine (ShadowMarket / ShadowOps) ───
        var undercroftBtn = new Button
        {
            Text = "Undercroft +1",
            CustomMinimumSize = new Vector2(140, 28),
        };
        undercroftBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        undercroftBtn.Pressed += () => ConcordDebug.DebugUndercroftUp();
        grid.AddChild(undercroftBtn);

        var exfilBtn = new Button
        {
            Text = "Exfiltrate",
            CustomMinimumSize = new Vector2(140, 28),
        };
        exfilBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        exfilBtn.Pressed += () => ConcordDebug.DebugExfiltrate();
        grid.AddChild(exfilBtn);

        var buyAssassinationBtn = new Button
        {
            Text = "Buy: Assassination",
            CustomMinimumSize = new Vector2(140, 28),
        };
        buyAssassinationBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        buyAssassinationBtn.Pressed += () => ConcordDebug.DebugBuyAssassination();
        grid.AddChild(buyAssassinationBtn);

        var assertUnitsBtn = new Button
        {
            Text = "Assert Units",
            CustomMinimumSize = new Vector2(140, 28),
        };
        assertUnitsBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        assertUnitsBtn.Pressed += () => UnitRegistry.AssertParityAndRoundTrip();
        grid.AddChild(assertUnitsBtn);

        // ── Strategic-layer levers (Scripts/Dev/StrategicDebug.cs) ───────
        // The strategic systems shipped 2026-07-21 and had never been run
        // in-engine as of 08-06 because a warfront and a Conjunction could
        // only be reached by playing a full cycle out. All three take effect
        // on the next strategic-map load.
        Button MakeDebugAction(string label, Action onPress)
        {
            var b = new Button { Text = label, CustomMinimumSize = new Vector2(140, 28) };
            b.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            b.Pressed += onPress;
            return b;
        }

        grid.AddChild(MakeDebugAction("Force Conjunction", StrategicDebug.ForceConjunction));
        grid.AddChild(MakeDebugAction("Owe +1 Lunation", () => StrategicDebug.OweLunations(1)));
        grid.AddChild(MakeDebugAction("Owe +3 Lunations", () => StrategicDebug.OweLunations(3)));
        grid.AddChild(MakeDebugAction("Prime Warfront", StrategicDebug.PrimeWarfront));
        grid.AddChild(MakeDebugAction("Resolve All Seats", StrategicDebug.ResolveAllSeats));

        var combatDebugBtn = new Button
        {
            Text = "Combat Debug",
            CustomMinimumSize = new Vector2(140, 28),
        };
        combatDebugBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        combatDebugBtn.Pressed += () => CombatDebugLauncher.Toggle(grid);
        grid.AddChild(combatDebugBtn);

        var assertDeckBtn = new Button
        {
            Text = "Assert Deck Split",
            CustomMinimumSize = new Vector2(140, 28),
        };
        assertDeckBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        assertDeckBtn.Pressed += () => CombatDebugLauncher.AssertDeckSplit();
        grid.AddChild(assertDeckBtn);

        // ── Progression bypasses ─────────────────────────────────────────
        // The faculty gate and the unlock gate are both slow by design, which
        // makes anything downstream of them tedious to test. These three skip
        // straight to the end state. They write to the EternalLedger and save
        // immediately, so they are PERMANENT for that guild — use a scratch slot.
        Button DebugGrant(string text, Action<GuildSaveData> apply)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(140, 28) };
            b.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            b.Pressed += () =>
            {
                var save = Ctx.Save;
                if (save?.Ledger == null) { GD.PrintErr("[Debug] No save loaded."); return; }
                apply(save);
                SaveManager.Save();
                Ctx.RequestRefreshAll?.Invoke();
            };
            grid.AddChild(b);
            return b;
        }

        DebugGrant("Declare All Schools", save =>
        {
            save.Ledger.MetaNarrativeFlags ??= new List<string>();
            int n = 0;
            foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
            {
                string flag = DeclarationService.DeclaredFlag(s.ToString());
                if (save.Ledger.MetaNarrativeFlags.Contains(flag)) continue;
                save.Ledger.MetaNarrativeFlags.Add(flag);
                n++;
            }
            GD.Print($"[Debug] Declared {n} additional discipline(s). Every school is now " +
                     $"selectable at the next cycle.");
        });

        DebugGrant("Unlock All Cards", save =>
        {
            save.Ledger.UnlockedCardBlueprintIds ??= new List<string>();
            var known = new HashSet<string>(save.Ledger.UnlockedCardBlueprintIds,
                                            StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var bp in CardDatabase.Blueprints)
            {
                if (!known.Add(bp.Id)) continue;
                save.Ledger.UnlockedCardBlueprintIds.Add(bp.Id);
                n++;
            }
            // Legendaries are included on purpose: DraftablePool drops them from
            // the draft regardless, so this only makes the card library honest.
            GD.Print($"[Debug] Unlocked {n} blueprint(s) — " +
                     $"{save.Ledger.UnlockedCardBlueprintIds.Count} known. " +
                     $"Legendaries remain undraftable (they are Regalia).");
        });

        DebugGrant("Learn All Spells", save =>
        {
            OverworldSpellRegistry.EnsureLoaded();
            save.Cycle ??= new CycleState();
            save.Cycle.Grimoire ??= new GrimoireState();
            save.Cycle.Grimoire.KnownSpellIds ??= new List<string>();

            var known = new HashSet<string>(save.Cycle.Grimoire.KnownSpellIds, StringComparer.Ordinal);
            int n = 0;
            foreach (var id in OverworldSpellRegistry.All.Keys)
            {
                if (!known.Add(id)) continue;
                save.Cycle.Grimoire.KnownSpellIds.Add(id);
                n++;
            }
            // Cycle-scoped by design — the Grimoire dies with the timeline
            // (overworld_spell_system_v1_1 §5), so this lasts the current cycle only.
            GD.Print($"[Debug] Learned {n} overworld spell(s) — " +
                     $"{save.Cycle.Grimoire.KnownSpellIds.Count} known this cycle.");
        });

        return panel;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Refresh methods
    // ═══════════════════════════════════════════════════════════════════════

    public void RefreshSlots()
    {
        if (_slotContainer == null)
            return;
        foreach (var child in _slotContainer.GetChildren())
            child.QueueFree();

        var slots = SaveManager.GetAllSlotInfo();
        foreach (var slot in slots)
        {
            bool isActive = slot.Slot == SelectedSlot;

            var card = new PanelContainer();
            card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            var cardStyle = new StyleBoxFlat
            {
                BgColor = isActive
                    ? new Color(0.10f, 0.18f, 0.10f, 0.9f)
                    : UITheme.BgRaised,
                BorderColor = isActive ? UITheme.Success : UITheme.NeutralDim,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderWidthLeft = isActive ? 3 : 1,
                BorderWidthRight = 1,
                CornerRadiusTopLeft = 5,
                CornerRadiusTopRight = 5,
                CornerRadiusBottomLeft = 5,
                CornerRadiusBottomRight = 5,
            };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var cardMargin = new MarginContainer();
            cardMargin.AddThemeConstantOverride("margin_left", 16);
            cardMargin.AddThemeConstantOverride("margin_right", 16);
            cardMargin.AddThemeConstantOverride("margin_top", 10);
            cardMargin.AddThemeConstantOverride("margin_bottom", 10);
            card.AddChild(cardMargin);

            var cardRow = new HBoxContainer();
            cardRow.AddThemeConstantOverride("separation", 16);
            cardMargin.AddChild(cardRow);

            // Left: slot info
            var infoCol = MakeVBox(4);
            infoCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            cardRow.AddChild(infoCol);

            if (slot.IsEmpty)
            {
                var emptyLabel = new Label { Text = $"Slot {slot.Slot + 1}  —  Empty" };
                emptyLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
                emptyLabel.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                infoCol.AddChild(emptyLabel);

                var newGameHint = new Label { Text = "Click to start a new guild" };
                newGameHint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                newGameHint.AddThemeColorOverride("font_color", UITheme.TextDim);
                infoCol.AddChild(newGameHint);
            }
            else
            {
                // Name + school row
                var nameRow = new HBoxContainer();
                nameRow.AddThemeConstantOverride("separation", 10);
                infoCol.AddChild(nameRow);

                var slotNum = new Label { Text = $"[{slot.Slot + 1}]" };
                slotNum.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                slotNum.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                nameRow.AddChild(slotNum);

                var nameLabel = new Label { Text = slot.GuildName };
                nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
                nameLabel.AddThemeColorOverride("font_color",
                    isActive ? Colors.White : UITheme.TextPrimary);
                nameRow.AddChild(nameLabel);

                var schoolBadge = new Label { Text = slot.School };
                schoolBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                schoolBadge.AddThemeColorOverride("font_color", UITheme.Violet);
                nameRow.AddChild(schoolBadge);

                if (isActive)
                {
                    var activeBadge = new Label { Text = "● Active" };
                    activeBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                    activeBadge.AddThemeColorOverride("font_color", UITheme.Success);
                    nameRow.AddChild(activeBadge);
                }

                // Stats row
                var statsRow = new HBoxContainer();
                statsRow.AddThemeConstantOverride("separation", 20);
                infoCol.AddChild(statsRow);

                void AddMiniStat(string label, string value)
                {
                    var lbl = new Label { Text = $"{label}  {value}" };
                    lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                    lbl.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                    statsRow.AddChild(lbl);
                }

                AddMiniStat("Gold:", $"{slot.Gold}");
                AddMiniStat("Runs:", $"{slot.TotalRuns}");
                if (!string.IsNullOrEmpty(slot.LastPlayed))
                    AddMiniStat("Last played:", slot.LastPlayed[..10]); // date only
            }

            // Right: action buttons
            var btnCol = MakeVBox(4);
            btnCol.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            cardRow.AddChild(btnCol);

            int capturedSlot = slot.Slot;
            bool isEmpty = slot.IsEmpty;

            var loadBtn = new Button
            {
                Text = slot.IsEmpty ? "New Game" : (isActive ? "Reload" : "Load"),
                CustomMinimumSize = new Vector2(90, 32),
            };
            loadBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(loadBtn, isPrimary: !isActive);
            loadBtn.Pressed += () => SelectSlot(capturedSlot, isEmpty);
            btnCol.AddChild(loadBtn);

            if (!slot.IsEmpty)
            {
                var delBtn = new Button
                {
                    Text = "Delete",
                    CustomMinimumSize = new Vector2(90, 28),
                };
                delBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                UITheme.ApplyButtonStyle(delBtn, isPrimary: false);
                delBtn.AddThemeColorOverride("font_color", UITheme.Danger);
                delBtn.Pressed += () =>
                {
                    SaveManager.DeleteSlot(capturedSlot);
                    SelectedSlot = -1;
                    Ctx.RequestRefreshAll?.Invoke();
                };
                btnCol.AddChild(delBtn);
            }

            _slotContainer.AddChild(card);
        }
    }

    private void SelectSlot(int slot, bool isEmpty)
    {
        if (isEmpty)
        {
            PlayerSession.PendingNewGameSlot = slot;
            Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/UI/NewGameScreen.tscn");
            return;
        }
        else
        {
            SaveManager.Load(slot);
            if (Enum.TryParse<CardSchool>(Ctx.Save.SelectedSchool, out var school))
                PlayerSession.SelectedSchool = school;
        }
        SelectedSlot = slot;
        Ctx.EnsureSaveSeeded?.Invoke();
        BuildingEffectApplier.ApplyCampusEffects(Ctx.Save);
        Ctx.RequestRefreshAll?.Invoke();
        Ctx.RefreshGold?.Invoke();
        // The explicit _armoryPanel/_trainingPanel refreshes that used to sit here were
        // dropped: RequestRefreshAll two lines above already fans out to both. Verified
        // against CampusScreen.RefreshAll, which calls each panel's Refresh directly.
        GD.Print($"Selected slot {slot}");
    }
}
