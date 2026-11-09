using Godot;
using System;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusGuildPanel.cs
//
// Purpose:        The Grand Hall's panel: Disciplines (the roll of the
//                 eight schools, each school's practical, declaring).
//                 Since 2026-11-05 it holds nothing else: the save slots
//                 went to the title screen, the debug panel to Settings,
//                 the charters to each building's card, the card screens
//                 to the Menu, the guild's name and seat to the Menu's
//                 top line, and the last expedition to a line on the map.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 DeclarationService.cs, DisciplineExam.cs,
//                 ChronicleScheduler (the conferral)
// See:            docs/guild_screen_retirement_v1.md (part B),
//                 docs/campus_tab_extraction_v1.md (Phase 2)
// ============================================================

/// <summary>The Disciplines panel, the room the Grand Hall opens. Its id stays
/// <c>CampusPanelId.Guild</c>, so routing and saves are untouched. It was the Guild tab, the
/// last and largest extraction from CampusScreen, and held whatever had no other home; on
/// 2026-11-05 everything but the disciplines left for a home of its own.
///
/// <para>Extracted from <c>CampusScreen</c> on 2026-08-03.</para></summary>
public sealed class CampusGuildPanel : CampusPanel
{
    private VBoxContainer _declarationContainer;
    /// <summary>The school whose practical (R7e, DisciplineExam) is open under its row, or "".</summary>
    private string _examOpenSchool = "";

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(16);
        margins.AddChild(layout);

        // ── Discipline, filled by RefreshDeclarations() ─────────────────
        // The Grand Hall hosts this panel, and the building's own description
        // already promises "school of study". It is also the room the conferral
        // stopped in, which is why declaring happens here and nowhere else.
        AddSectionHeader(layout, "Disciplines: what you can play, and why");
        _declarationContainer = MakeVBox(8);
        layout.AddChild(_declarationContainer);
    }

    public override void Refresh()
    {
        RefreshDeclarations();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Declare a Discipline (design doc §7)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One row per non-Adept school: declared, declarable, or locked with the
    /// single most actionable reason why. Eligibility is recomputed on every
    /// refresh because it depends on cycle state (companions, dispositions) that
    /// changes underneath this panel. R7e (2026-10-26): the last step is the
    /// school's practical (DisciplineExam), opened under its row, and the stated
    /// intention shows as the first goal until it is declared.
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
                   "Declaring takes a teacher, that school's mastery and the Grand Hall. " +
                   "Then you sit its practical.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        intro.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        intro.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        _declarationContainer.AddChild(intro);

        var tally = new Label { Text = $"{declaredCount} of 8 disciplines yours." };
        tally.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        tally.AddThemeColorOverride("font_color", UITheme.Gold);
        _declarationContainer.AddChild(tally);

        // R7e: the stated intention is the first goal, shown until it is declared.
        string goal = IntendedSchool(save);
        if (goal.Length > 0 && goal != DeclarationService.StartingSchool && !DeclarationService.IsDeclared(save, goal))
        {
            var goalLbl = new Label
            {
                Text = $"Your first goal: {goal}, the discipline you intend to be named into.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            goalLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            goalLbl.AddThemeColorOverride("font_color", UITheme.GoldDim);
            _declarationContainer.AddChild(goalLbl);
        }

        // Adept first, since it is always playable and never declared. Skipping it entirely
        // read as a hole in the list during playtest.
        _declarationContainer.AddChild(BuildAdeptRow());

        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
        {
            string name = school.ToString();
            if (name == DeclarationService.StartingSchool) continue;   // row above

            _declarationContainer.AddChild(BuildDeclarationRow(save, name));
            if (name == _examOpenSchool)
                _declarationContainer.AddChild(BuildExamCard(save, name));
        }
    }

    /// <summary>The stated intention's school (R7b), "" when unstated or never asked.</summary>
    private static string IntendedSchool(GuildSaveData save)
    {
        var flags = save?.Ledger?.MetaNarrativeFlags;
        if (flags == null)
            return "";
        foreach (var name in Enum.GetNames(typeof(CardSchool)))
            if (flags.Contains($"stated_intention_{name.ToLowerInvariant()}"))
                return name;
        return "";
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
    /// two facts that matter, unlock status and whether a teacher exists).
    /// Glyph + name + a fixed status line: "Teacher … · Mastery x/y · Grand Hall …"
    /// for undeclared schools, "Declared · Mastery n" for declared ones. The
    /// Declare button appears only when everything is met.
    /// </summary>
    private Control BuildDeclarationRow(GuildSaveData save, string school)
    {
        var status = DeclarationService.Evaluate(save, school);
        if (status.Eligible && !status.Declared)
            RegisterManager.Fire("campus.first_declaration");   // S5: taught when first met

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

        // The fixed status line: the same three items in the same order on every
        // undeclared row, so standing is comparable at a glance.
        bool hasHall = DeclarationService.HasGrandHall(save);
        bool hasTeacher = status.FacultySource != null;
        bool hasMastery = status.MasteryPoints >= status.MasteryRequired;
        bool hasExam = DisciplineExam.HasExam(school);
        bool passed = hasExam && DisciplineExam.HasPassed(save, school);
        detail.Text =
            $"Teacher {(hasTeacher ? "✓ " + status.FacultySource : "-")}   ·   " +
            $"Mastery {status.MasteryPoints}/{status.MasteryRequired}{(hasMastery ? " ✓" : "")}   ·   " +
            $"Grand Hall {(hasHall ? "✓" : "-")}" +
            (hasExam ? $"   ·   Practical {(passed ? "✓" : "-")}" : "");
        detail.AddThemeColorOverride("font_color",
            status.Eligible ? UITheme.HealthGreen : UITheme.CampusSubtleText);
        row.AddChild(detail);

        if (status.Eligible)
        {
            // R7e: with the gate met, the practical comes first, unless it is already passed.
            bool examFirst = hasExam && !passed;
            var btn = MakeButton(examFirst ? "Sit the practical" : $"Declare {school}", 170, 34,
                                 UITheme.CampusSmallFontSize);
            string captured = school;
            if (examFirst)
            {
                btn.TooltipText = $"Pass the {school} practical to declare it. If you miss, you may sit it again.";
                btn.Pressed += () => ToggleExamCard(captured);
            }
            else
                btn.Pressed += () => OnDeclarePressed(captured);
            row.AddChild(btn);
        }

        return row;
    }

    /// <summary>Open or close a school's practical under its row.</summary>
    private void ToggleExamCard(string school)
    {
        _examOpenSchool = _examOpenSchool == school ? "" : school;
        RefreshDeclarations();
    }

    /// <summary>The practical's page under its row (DisciplineExam.OpeningPanel): the teacher
    /// setting it and giving the brief in their own voice (E2), then Begin or Not yet.</summary>
    private Control BuildExamCard(GuildSaveData save, string school)
    {
        var page = DisciplineExam.OpeningPanel(save, school);
        if (page == null)
            return MakeStubLabel($"The page for the {school} practical is missing from the data.");

        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.BgCard,
            BorderColor = UITheme.GoldDim,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(UITheme.CornerRadius);
        card.AddThemeStyleboxOverride("panel", style);

        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        card.AddChild(box);

        var title = new Label { Text = page.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        box.AddChild(title);

        var body = new Label
        {
            Text = page.Body,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        body.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        body.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        box.AddChild(body);

        string beginLabel = page.Choices.Count > 0 && !string.IsNullOrEmpty(page.Choices[0]?.Label)
            ? page.Choices[0].Label : "Begin.";
        string laterLabel = page.Choices.Count > 1 && !string.IsNullOrEmpty(page.Choices[1]?.Label)
            ? page.Choices[1].Label : "Not yet.";
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        box.AddChild(buttons);
        string captured = school;
        var beginBtn = MakeButton(beginLabel, 190, 34, UITheme.CampusSmallFontSize);
        beginBtn.Pressed += () => OnSitPressed(captured);
        buttons.AddChild(beginBtn);
        var laterBtn = MakeButton(laterLabel, 120, 34, UITheme.CampusSmallFontSize, isPrimary: false);
        laterBtn.Pressed += () => ToggleExamCard(captured);
        buttons.AddChild(laterBtn);
        return card;
    }

    /// <summary>Begin: the practical launches, and the scene changes to the examination floor.
    /// Its return is consumed by the scene it left (DisciplineExam.ConsumeReturn).</summary>
    private void OnSitPressed(string school)
    {
        var save = Ctx.Save;
        if (save == null) return;
        if (DisciplineExam.Sit(Ctx.Host, save, school))
            return;
        Ctx.Toasts?.Push($"The {school} practical cannot be sat just now.", QuestToastKind.Progress);
        _examOpenSchool = "";
        RefreshDeclarations();
    }

    private void OnDeclarePressed(string school)
    {
        var save = Ctx.Save;
        if (save == null) return;

        // R7e: a school with a practical is declared by passing it (DisciplineExam), so a stale
        // Declare press opens the practical instead.
        if (DisciplineExam.HasExam(school) && !DisciplineExam.HasPassed(save, school))
        {
            _examOpenSchool = school;
            RefreshDeclarations();
            return;
        }

        // Capture the teacher before declaring, because Evaluate reports FacultySource
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

        // C1: when the story data is in, the chronicle's conferral (Part I, I-5) replaces the
        // dialog below, through the host's narrative panel and applier.
        var conferral = ChronicleScheduler.RaiseDeclaration(school);
        if (conferral != null && Ctx.ShowNarrative != null)
        {
            SaveManager.SaveIfDirty();
            Ctx.ShowNarrative(conferral);
            Ctx.RequestRefreshAll?.Invoke();
            return;
        }

        // The Provost's sentence from Beat 2, finally finishing, in the voice of
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
}
