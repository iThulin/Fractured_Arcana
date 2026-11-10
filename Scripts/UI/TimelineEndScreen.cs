using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// TimelineEndScreen.cs
//
// Purpose:        Where a timeline ends and the next begins (TE1,
//                 2026-11-10). One screen for every ending: the last
//                 Anchor broken, the Convergence lost or held, the
//                 reading let go at the Conjunction. It says what
//                 ended, what the guild keeps, what is unmade, lets
//                 the last gold raise what already stands, and takes
//                 the next reading's discipline and Regalia.
//
//                 It replaces the ritual the standalone campus screen
//                 carried (the title bar's "Begin the Next Reading",
//                 CampusExpeditionPanel's picker and
//                 CampusScreen.BeginNextCycle), and it is the only
//                 caller of SaveManager.BeginNewCycle.
//
//                 An eligible discipline can still be declared here,
//                 because its teacher is part of the timeline that is
//                 ending: its practical is sat from this screen and
//                 returns to it (DisciplineExam).
// Layer:          UI
// Collaborators:  SaveManager (BeginNewCycle), TimelineAnchors,
//                 CycleInitializer (the new world), DeclarationService,
//                 DisciplineExam, SchoolMasteryService, RegaliaService,
//                 CampusConstruction (last works), QuestTracker,
//                 EncounterRouter and StrategicView (the ways in).
// ============================================================

/// <summary>The end of a reading and the start of the next (see the file header).</summary>
public partial class TimelineEndScreen : Control
{
    public const string ScenePath = "res://Scenes/UI/TimelineEnd.tscn";
    private const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    private const string TitleScenePath = "res://Scenes/UI/TitleScreen.tscn";
    private const int ShardsInTheSeal = 6;
    private const int UnfinishedShown = 6;

    private Control _content;
    private string _selectedSchool = "";
    private string _notice = "";
    private bool _transitioning;
    private bool _carryChosen;
    private readonly List<string> _carry = new();
    private Button _beginButton;

    /// <summary>LoopRecord.Outcome for the reading that is ending.</summary>
    public static string OutcomeOf(CycleState cycle)
    {
        if (cycle?.TimelineLost == true)
        {
            return TimelineAnchors.OutcomeWizardFallen;
        }
        return cycle?.Convergence?.Outcome switch
        {
            "Victory" => "Victory",
            "Defeat" => "ConvergenceDefeat",
            _ => "Abandoned",
        };
    }

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUI));
    }

    private void Rebuild()
    {
        if (_content != null && IsInstanceValid(_content))
        {
            _content.QueueFree();
        }
        _content = null;
        CallDeferred(nameof(BuildUI));
    }

    // ── Build ───────────────────────────────────────────────────────────
    private void BuildUI()
    {
        var save = SaveManager.ActiveSave;
        if (save?.Cycle == null || save.Ledger == null)
        {
            GetTree().ChangeSceneToFile(TitleScenePath);
            return;
        }
        PlayerSession.CycleEndedByConjunction = true;

        // A practical sat from this screen comes back to it.
        var exam = DisciplineExam.ConsumeReturn(ScenePath);
        if (exam != null)
        {
            _notice = exam.Declared
                ? $"{exam.School} unlocked. You can play it next timeline."
                : exam.Result == "pass" && !string.IsNullOrEmpty(exam.Toast)
                    ? exam.Toast
                    : $"You did not pass the {exam.School} exam. You can try again.";
            if (exam.Declared)
            {
                _selectedSchool = exam.School;
            }
        }

        // Settle progression before anything here counts what is owned.
        ProgressionSweep.Run(save);

        var cycle = save.Cycle;
        string outcome = OutcomeOf(cycle);

        _content = new Control { Name = "Content" };
        _content.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_content);

        var backdrop = new ColorRect { Color = UITheme.BgBase.Darkened(0.45f) };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        _content.AddChild(backdrop);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 64);
        margin.AddThemeConstantOverride("margin_right", 64);
        margin.AddThemeConstantOverride("margin_top", 36);
        margin.AddThemeConstantOverride("margin_bottom", 32);
        _content.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        BuildHeader(root, cycle, outcome);

        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 18);
        root.AddChild(columns);
        BuildKeeps(Column(columns, "You Keep", UITheme.Gold), save);
        BuildUnmade(Column(columns, "You Lose", UITheme.TextSecondary), save);
        BuildLastWorks(Column(columns, "Spend Before You Go", UITheme.Success), save);

        BuildNextReading(root, save);
        BuildBottomBar(root);
    }

    private void BuildHeader(VBoxContainer root, CycleState cycle, string outcome)
    {
        string title, cause;
        switch (outcome)
        {
            case TimelineAnchors.OutcomeWizardFallen:
                title = "Timeline Lost";
                cause = "Your wizard was defeated with no Anchors left, so this timeline is over.";
                break;
            case "ConvergenceDefeat":
                title = "Timeline Lost";
                cause = "You lost the final battle at the Convergence, so this timeline is over.";
                break;
            case "Victory":
                title = "Timeline Won";
                cause = "You won the final battle at the Convergence and chose to start a new timeline.";
                break;
            default:
                title = "Timeline Ended";
                cause = "You chose to end this timeline at the Grand Conjunction.";
                break;
        }

        root.AddChild(MakeLabel(title, UITheme.FontSizeTitle, UITheme.Gold, center: true));
        root.AddChild(MakeLabel(cause + " Your guild keeps everything in the left column. Everything in the middle column is reset.",
            UITheme.CampusBodyFontSize, UITheme.TextPrimary, center: true));

        int span = Math.Max(1, cycle.Calendar?.LunationsPerCycle ?? 12);
        int lunation = Math.Clamp(cycle.Calendar?.CurrentLunation ?? 1, 1, span);
        string record = $"Timeline {cycle.CycleNumber}  ·  Year {cycle.CampaignYear}  ·  " +
                        $"Moon {lunation} of {span}  ·  Played as {cycle.SelectedSchool}  ·  " +
                        $"{cycle.TotalRuns} expedition{(cycle.TotalRuns == 1 ? "" : "s")}";
        root.AddChild(MakeLabel(record, UITheme.CampusSmallFontSize, UITheme.TextSecondary, center: true));
        root.AddChild(new HSeparator());
    }

    private void BuildKeeps(VBoxContainer col, GuildSaveData save)
    {
        var declared = DeclarationService.DeclaredSchools(save) ?? new List<string>();
        AddLine(col, declared.Count > 0
            ? $"Unlocked disciplines: {string.Join(", ", declared)}"
            : $"Unlocked disciplines: {DeclarationService.StartingSchool}");

        var mastery = new List<string>();
        foreach (var (school, points) in SchoolMasteryService.Ranked(save))
        {
            if (points > 0 && mastery.Count < 4)
            {
                mastery.Add($"{school} {points}");
            }
        }
        AddLine(col, mastery.Count > 0 ? $"Mastery: {string.Join(", ", mastery)}" : "Mastery: none yet");

        int built = 0;
        foreach (var b in save.Buildings)
        {
            if (b != null && b.Tier > 0)
            {
                built++;
            }
        }
        AddLine(col, $"Cards unlocked: {save.Ledger.UnlockedCardBlueprintIds?.Count ?? 0}");
        AddLine(col, $"Regalia: {RegaliaService.Owned(save).Count}");
        AddLine(col, $"Shards: {RegaliaService.ShardsCollected(save)} of {ShardsInTheSeal}");
        AddLine(col, $"Campus buildings: {built}");
        AddLine(col, $"Lore entries: {save.Ledger.UnlockedLoreEntries?.Count ?? 0}");
        AddLine(col, $"Anchors next timeline: {TimelineAnchors.Max(save)}", UITheme.Gold);
    }

    private void BuildUnmade(VBoxContainer col, GuildSaveData save)
    {
        int company = 0;
        if (save.Cycle.Companions != null)
        {
            foreach (var c in save.Cycle.Companions)
            {
                if (c != null && c.IsRecruited)
                {
                    company++;
                }
            }
        }
        AddLine(col, "The world map and everything on it: kingdoms, wars and discoveries.");
        AddLine(col, "Your deck. You start again with a starter deck.");
        AddLine(col, "Your items.");
        AddLine(col, $"Your companions ({company}).");
        AddLine(col, $"Your gold ({save.Gold}) and materials ({save.BuildMaterials}). Spend them on the right first.");

        var unfinished = new List<string>();
        foreach (var q in QuestLoader.LoadAll())
        {
            if (q != null && q.EffectiveLayer == "Timeline"
                && QuestTracker.StatusOf(q, save) == QuestStatus.Active)
            {
                unfinished.Add(q.Title);
            }
        }
        if (unfinished.Count == 0)
        {
            return;
        }
        AddLine(col, "Unfinished quests:", UITheme.TextSecondary);
        for (int i = 0; i < unfinished.Count && i < UnfinishedShown; i++)
        {
            AddLine(col, "   " + unfinished[i]);
        }
        if (unfinished.Count > UnfinishedShown)
        {
            AddLine(col, $"   and {unfinished.Count - UnfinishedShown} more", UITheme.TextSecondary);
        }
    }

    /// <summary>Gold does not cross; a tier bought here does. Only buildings already
    /// standing are offered: a new one needs a site, and that is the hub's work.</summary>
    private void BuildLastWorks(VBoxContainer col, GuildSaveData save)
    {
        AddLine(col, "Gold and materials do not carry over. Building upgrades do. Upgrade a building here to keep the value.", UITheme.TextSecondary);
        AddLine(col, $"You have {save.Gold} gold and {save.BuildMaterials} materials.", UITheme.Gold);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        col.AddChild(scroll);
        var pad = new MarginContainer
        {
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        scroll.AddChild(pad);
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 6);
        pad.AddChild(list);

        int offered = 0;
        foreach (var b in save.Buildings)
        {
            if (b == null || b.Tier <= 0)
            {
                continue;
            }
            var template = BuildingDatabase.GetTemplate(b.Id);
            if (template == null || b.Tier >= template.MaxTier)
            {
                continue;
            }
            int next = b.Tier + 1;
            var tier = template.Tiers.Find(t => t.Tier == next);
            if (tier == null)
            {
                continue;
            }
            string reason = CampusConstruction.CannotBuildReason(save, b.Id);
            var btn = new Button
            {
                Text = $"Upgrade {template.Name} to tier {next}: {tier.GoldCost} gold, {tier.EffectiveMaterialsCost} materials",
                Disabled = reason != null,
                TooltipText = reason ?? tier.Description,
                CustomMinimumSize = new Vector2(0, 36),
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: false);
            string id = b.Id;
            string name = template.Name;
            btn.Pressed += () =>
            {
                if (_transitioning)
                {
                    return;
                }
                _notice = CampusConstruction.TryBuildOrUpgrade(SaveManager.ActiveSave, id)
                    ? $"{name} upgraded to tier {next}. This is permanent."
                    : $"{name} could not be upgraded.";
                Rebuild();
            };
            list.AddChild(btn);
            if (reason != null)
            {
                list.AddChild(MakeLabel("   Cannot upgrade: " + reason, UITheme.CampusSmallFontSize, UITheme.TextSecondary));
            }
            offered++;
        }
        if (offered == 0)
        {
            AddLine(list, "No building can be upgraded right now.", UITheme.TextSecondary);
        }
    }

    private void BuildNextReading(VBoxContainer root, GuildSaveData save)
    {
        root.AddChild(new HSeparator());
        root.AddChild(MakeLabel("Start the Next Timeline", UITheme.CampusSectionFontSize, UITheme.Gold, center: true));
        root.AddChild(MakeLabel(
            "Choose a discipline to play. You can only choose disciplines you have unlocked. You start with that discipline's starter deck.",
            UITheme.CampusSmallFontSize, UITheme.TextSecondary, center: true));

        BuildRegalia(root, save);

        string previous = save.Cycle.SelectedSchool ?? "";
        if (string.IsNullOrEmpty(_selectedSchool) || !DeclarationService.IsDeclared(save, _selectedSchool))
        {
            _selectedSchool = DeclarationService.IsDeclared(save, previous)
                ? previous
                : DeclarationService.StartingSchool;
        }

        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        root.AddChild(grid);

        var group = new ButtonGroup();
        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
        {
            string name = school.ToString();
            int points = SchoolMasteryService.Points(save, name);
            string suffix = points > 0 ? $"   ·   {points} mastery" : "";
            var status = DeclarationService.Evaluate(save, name);
            bool declared = DeclarationService.IsDeclared(save, name);

            var btn = new Button { CustomMinimumSize = new Vector2(300, 44) };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            string captured = name;

            if (declared)
            {
                btn.Text = (name == previous ? $"{name}  (played last time)" : name) + suffix;
                btn.ToggleMode = true;
                btn.ButtonGroup = group;
                btn.ButtonPressed = name == _selectedSchool;
                UITheme.ApplyButtonStyle(btn, isPrimary: true);
                btn.Toggled += (bool pressed) =>
                {
                    if (pressed)
                    {
                        _selectedSchool = captured;
                        RefreshBeginButton();
                    }
                };
            }
            else if (status.Eligible)
            {
                bool practical = DisciplineExam.HasExam(name) && !DisciplineExam.HasPassed(save, name);
                btn.Text = practical ? $"{name}: take the exam to unlock" : $"{name}: unlock now";
                btn.TooltipText = "You meet the requirements to unlock this discipline. Its teacher is lost when this timeline ends, so unlock it before you start the next one.";
                UITheme.ApplyButtonStyle(btn, isPrimary: false);
                btn.Pressed += () => OnDeclarePressed(captured);
            }
            else
            {
                btn.Text = $"{name}  (locked){suffix}";
                btn.Disabled = true;
                btn.TooltipText = status.Blocker ?? "";
                UITheme.ApplyButtonStyle(btn, isPrimary: false);
                btn.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            }
            grid.AddChild(btn);
        }
    }

    /// <summary>The Regalia carry: owned forever, K may cross. Staged, not written, because
    /// BeginNewCycle replaces the cycle; RegaliaService.SeedCarriedIntoDeck reads the staging.</summary>
    private void BuildRegalia(VBoxContainer root, GuildSaveData save)
    {
        var owned = RegaliaService.Owned(save);
        if (owned.Count == 0)
        {
            return;
        }
        int k = RegaliaService.MaxCarry(save);
        var header = MakeLabel("", UITheme.CampusSmallFontSize, UITheme.Gold, center: true);
        root.AddChild(header);
        var row = new HFlowContainer
        {
            Alignment = FlowContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("h_separation", 8);
        row.AddThemeConstantOverride("v_separation", 6);
        root.AddChild(row);

        if (!_carryChosen)
        {
            _carry.Clear();
            foreach (var bp in owned)
            {
                if (_carry.Count < k)
                {
                    _carry.Add(bp.Id);
                }
            }
            _carryChosen = true;
        }

        void RefreshHeader() => header.Text = $"Regalia to bring: {_carry.Count} of {k} selected ({owned.Count} owned)";
        bool suppress = false;

        foreach (var bp in owned)
        {
            string id = bp.Id;
            string label = bp.Prebuilt?.CardName;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = id;
            }
            var btn = new Button
            {
                Text = label,
                ToggleMode = true,
                ButtonPressed = _carry.Contains(id),
                CustomMinimumSize = new Vector2(220, 34),
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: false);
            var capturedBtn = btn;
            btn.Toggled += (bool pressed) =>
            {
                if (suppress)
                {
                    return;
                }
                if (pressed)
                {
                    if (_carry.Count >= k)
                    {
                        suppress = true;
                        capturedBtn.ButtonPressed = false;
                        suppress = false;
                        header.Text = $"You can bring only {k}. Deselect one first.";
                        return;
                    }
                    if (!_carry.Contains(id))
                    {
                        _carry.Add(id);
                    }
                }
                else
                {
                    _carry.Remove(id);
                }
                RefreshHeader();
                RegaliaService.StagePendingCarry(_carry);
            };
            row.AddChild(btn);
        }
        RefreshHeader();
        RegaliaService.StagePendingCarry(_carry);
    }

    private void BuildBottomBar(VBoxContainer root)
    {
        var notice = MakeLabel(_notice, UITheme.CampusSmallFontSize, UITheme.Success, center: true);
        root.AddChild(notice);

        var bar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        bar.AddThemeConstantOverride("separation", 16);
        root.AddChild(bar);

        var quit = new Button { Text = "Save and Quit to Title", CustomMinimumSize = new Vector2(240, 46) };
        quit.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(quit, isPrimary: false);
        quit.Pressed += () =>
        {
            if (_transitioning)
            {
                return;
            }
            SaveManager.Save();
            GetTree().ChangeSceneToFile(TitleScenePath);
        };
        bar.AddChild(quit);

        _beginButton = new Button { CustomMinimumSize = new Vector2(420, 46) };
        _beginButton.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        UITheme.ApplyButtonStyle(_beginButton, isPrimary: true);
        _beginButton.Pressed += OnBeginPressed;
        bar.AddChild(_beginButton);
        RefreshBeginButton();
    }

    private void RefreshBeginButton()
    {
        if (_beginButton != null && IsInstanceValid(_beginButton))
        {
            _beginButton.Text = $"Start Next Timeline as {_selectedSchool}";
        }
    }

    // ── Verbs ───────────────────────────────────────────────────────────
    private void OnDeclarePressed(string school)
    {
        if (_transitioning)
        {
            return;
        }
        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            return;
        }
        if (DisciplineExam.HasExam(school) && !DisciplineExam.HasPassed(save, school))
        {
            if (DisciplineExam.Sit(this, save, school))
            {
                _transitioning = true;   // the scene is changing to the examination floor
                return;
            }
            _notice = $"The {school} exam cannot be started right now.";
            Rebuild();
            return;
        }
        if (DeclarationService.Declare(save, school))
        {
            SaveManager.Save();
            _selectedSchool = school;
            _notice = $"{school} unlocked. You can play it next timeline.";
        }
        else
        {
            _notice = DeclarationService.Evaluate(save, school).Blocker ?? $"{school} cannot be unlocked right now.";
        }
        Rebuild();
    }

    /// <summary>End this timeline and open the next: archive it, replace the cycle, seed the
    /// deck, build the world, and land in the home city. What CampusScreen.BeginNextCycle did.</summary>
    private void OnBeginPressed()
    {
        // The button stays live until the scene changes, and a second press would archive a
        // second record and seed a second deck.
        if (_transitioning)
        {
            return;
        }
        var save = SaveManager.ActiveSave;
        if (save?.Cycle == null || string.IsNullOrEmpty(_selectedSchool))
        {
            return;
        }
        _transitioning = true;

        string school = _selectedSchool;
        string outcome = OutcomeOf(save.Cycle);
        string path = save.Cycle.Convergence?.Path ?? "";
        if (SaveManager.BeginNewCycle(school, outcome, path) == null)
        {
            _transitioning = false;
            _notice = "The next timeline could not be started. Nothing was changed.";
            Rebuild();
            return;
        }
        PlayerSession.CycleEndedByConjunction = false;
        if (Enum.TryParse<CardSchool>(school, out var parsed))
        {
            PlayerSession.SelectedSchool = parsed;
        }

        CycleInitializer.EnsureCycleWorld();
        save = SaveManager.ActiveSave;
        QuestTracker.SyncCompletions(save);
        CompanionUnlocks.Sync(save);
        PlayerSession.ClearRunState();
        BuildingEffectApplier.CalculateRunBonuses(save);
        BuildingEffectApplier.ApplyCampusEffects(save);

        PlayerSession.StartInCityOnOpen = true;
        SceneTransition.Go(GetTree(), StrategicScenePath, "New Timeline",
            $"Timeline {save.Cycle.CycleNumber} begins.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private static Label MakeLabel(string text, int size, Color color, bool center = false)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static void AddLine(VBoxContainer col, string text) => AddLine(col, text, UITheme.TextPrimary);

    private static void AddLine(VBoxContainer col, string text, Color color)
    {
        col.AddChild(MakeLabel(text, UITheme.CampusBodyFontSize, color));
    }

    /// <summary>One framed column with its heading; returns the box its lines go in.</summary>
    private static VBoxContainer Column(HBoxContainer row, string title, Color border)
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, border));
        row.AddChild(panel);

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 18);
        pad.AddThemeConstantOverride("margin_right", 18);
        pad.AddThemeConstantOverride("margin_top", 14);
        pad.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(pad);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        pad.AddChild(box);
        box.AddChild(MakeLabel(title, UITheme.CampusSectionFontSize, border));
        return box;
    }
}
