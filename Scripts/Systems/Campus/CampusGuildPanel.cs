using Godot;
using System;
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
        RefreshSlots();
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
    }

    private void RefreshGuildResultPanel()
    {
        if (_guildResultContainer == null)
            return;
        foreach (var child in _guildResultContainer.GetChildren())
            child.QueueFree();

        if (!RunResultData.HasResults)
            return;

        bool won = RunResultData.ReachedObjective;

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

        AddResult("Gold:", $"{RunResultData.GoldEarned}");
        AddResult("Splinters:", $"{RunResultData.ArcaneSplinters}");
        AddResult("Encounters:", $"{RunResultData.EncountersWon}");
        AddResult("HP:", $"{RunResultData.HPRemaining}");

        RunResultData.Clear();
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

        var assertUnitsBtn = new Button
        {
            Text = "Assert Units",
            CustomMinimumSize = new Vector2(140, 28),
        };
        assertUnitsBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        assertUnitsBtn.Pressed += () => UnitRegistry.AssertParityAndRoundTrip();
        grid.AddChild(assertUnitsBtn);

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
