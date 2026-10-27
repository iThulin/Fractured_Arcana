using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// DisciplineExam.cs
//
// Purpose:        The examination that declares a discipline (R7e, ruled
//                 2026-10-26: each school's practical becomes the Grand
//                 Hall exam that declares it). Once the faculty gate is
//                 met (a teacher, the school's mastery, the Grand Hall),
//                 the school's row in the Disciplines section offers its
//                 practical from the prologue. A pass declares the school
//                 and shows the conferral. A miss shows the teacher's note,
//                 and the school stays open to sit again, as often as the
//                 player likes (E1). Adept has no examination: every guild
//                 begins there.
//
//                 Rulings E1 to E6 (2026-10-26): retries are free (E1);
//                 the teacher sets the practical and gives the brief in
//                 their own voice (E2: Data/Story/declaration_exams.json,
//                 shown through ChronicleScheduler.Page); it is sat in the
//                 Grand Hall set (E3: each practical's examMapRecipe); the
//                 apparatus is fitted to the wizard (E4: ExamScaling,
//                 below, applied at spawn by CombatManager.Exam.cs); the
//                 stated intention's school is a quest (E5:
//                 Data/Quests/first_goal.json); a practical passed in an
//                 older save's prologue earns no credit (E6).
//
//                 G1 (ruled the same day): the wizard's equipment is built
//                 fresh from the Armory when the page opens and when the
//                 exam launches, as a deploy builds it (Fitting), so the
//                 exam fields, and is fitted to, what the Armory shows.
//                 Never during an expedition: mid-run, the run's loadout
//                 stands.
//
//                 The fight returns to the scene it was sat from (the
//                 strategic hub, or the standalone campus), and that
//                 scene's host consumes it and shows the outcome:
//                 StrategicView.Exam.cs, or CampusScreen's
//                 ConsumeCampusCombatReturn.
// Layer:          System (static)
// Collaborators:  CampusGuildPanel.cs (the Disciplines rows),
//                 PrologueDirector.cs (HasPractical, PracticalScript,
//                 PracticalTitle, LaunchExam, ExamOutcome),
//                 DeclarationService.cs (Evaluate, Declare, ConferralLine),
//                 ChronicleScheduler (Page, FacultyIdFor, RaiseDeclaration),
//                 EncounterRouter (the return, ExamScaling),
//                 EquipmentLoadout (what the wizard fields),
//                 CombatManager.Exam.cs, StrategicView.Exam.cs,
//                 CampusScreen.cs, QuestTracker (HasPassed)
// See:            docs/narrative_frame_addendum_r7e_intention_first_goal.md,
//                 docs/declaration_exams_v1.md
// ============================================================

/// <summary>The declaration exams. Static on purpose: the exam out on the router has to outlive
/// the scene change to the fight and back, and both hubs call
/// <see cref="PrologueDirector.ClearScriptedSession"/> on the way in, before their returns are
/// consumed, which clears the router's scripted fight id.</summary>
public static class DisciplineExam
{
    /// <summary>Meta flag per school, lower case: its practical was passed. The declaration
    /// follows at once; the flag lets a pass whose declaration was refused declare later
    /// without sitting the practical again.</summary>
    public const string PassedFlagPrefix = "declaration_exam_passed_";

    /// <summary>The story file holding the teachers' lines (E2), and its two pages.</summary>
    public const string StoryFile = "declaration_exams";
    public const string OpenPageId = "declaration_exam_open";
    public const string MissedPageId = "declaration_exam_missed";

    /// <summary>What came back from an exam, for the host to show.</summary>
    public sealed class Outcome
    {
        public string School = "";
        /// <summary>"pass", "time" or "floor", by the practical's own rule.</summary>
        public string Result = "";
        public bool Declared;
        /// <summary>The conferral on a pass, the teacher's note on a miss. Null when there is
        /// nothing to show but the toast.</summary>
        public NarrativeEncounterData Panel;
        public string Toast = "";
        public QuestToastKind ToastKind = QuestToastKind.Progress;
    }

    private static string _pendingSchool = "";
    private static string _pendingScene = "";

    /// <summary>True when declaring <paramref name="school"/> takes its examination: any school
    /// but Adept whose practical is in the data.</summary>
    public static bool HasExam(string school) =>
        !string.IsNullOrWhiteSpace(school)
        && !string.Equals(school.Trim(), DeclarationService.StartingSchool, StringComparison.OrdinalIgnoreCase)
        && PrologueDirector.HasPractical(school);

    /// <summary>True when this save has passed <paramref name="school"/>'s practical.</summary>
    public static bool HasPassed(GuildSaveData save, string school) =>
        save?.Ledger?.MetaNarrativeFlags?.Contains(PassedFlag(school)) ?? false;

    /// <summary>The page the Disciplines row shows before the exam: the teacher setting it, the
    /// brief in their voice, and its two choices' labels (begin, and not yet). Null when its
    /// data is missing.</summary>
    public static NarrativeEncounterData OpeningPanel(GuildSaveData save, string school) =>
        save == null ? null : ChronicleScheduler.Page(StoryFile, OpenPageId, PageArgs(save, school, Fitting(save)));

    /// <summary>The page after a miss: the practical's note in the teacher's voice, and their word on
    /// sitting it again. Null when its data is missing.</summary>
    public static NarrativeEncounterData MissedPanel(GuildSaveData save, string school) =>
        save == null ? null : ChronicleScheduler.Page(StoryFile, MissedPageId, PageArgs(save, school, ExamScaling.ForWizard()));

    /// <summary>Sit the exam: re-check the gate, fit the apparatus to the wizard, then launch the
    /// practical, returning to the scene <paramref name="host"/> is in. False, with nothing
    /// changed, when it cannot.</summary>
    public static bool Sit(Node host, GuildSaveData save, string school)
    {
        if (host == null || !host.IsInsideTree() || save?.Ledger == null || !HasExam(school))
            return false;
        var status = DeclarationService.Evaluate(save, school);
        if (status.Declared || !status.Eligible)
        {
            GD.PrintErr($"[Exam] The {school} practical cannot be sat: " +
                        (status.Declared ? "it is already declared." : status.Blocker));
            return false;
        }

        var tree = host.GetTree();
        string scene = ReturnSceneFor(tree);
        var scaling = Fitting(save);
        _pendingSchool = school.Trim();
        _pendingScene = scene;
        if (!PrologueDirector.LaunchExam(tree, school, save, scene, scaling))
        {
            _pendingSchool = "";
            _pendingScene = "";
            GD.PrintErr($"[Exam] The {school} practical could not launch.");
            return false;
        }
        // The hub lands back in the home city, where the Grand Hall stands.
        if (scene == PrologueDirector.StrategicScenePath)
            PlayerSession.StartInCityOnOpen = true;
        GD.Print($"[Exam] Sitting the {school} practical (back to {scene}); {scaling}.");
        return true;
    }

    /// <summary>Consume the exam's return on <paramref name="scenePath"/>: record the result,
    /// declare on a pass, and hand back what to show. Null when no exam sat from this scene is
    /// waiting on the router.</summary>
    public static Outcome ConsumeReturn(string scenePath)
    {
        var router = EncounterRouter.Instance;
        if (_pendingSchool.Length == 0 || _pendingScene != scenePath || router == null
            || !router.HasPendingReturn || router.ReturnSceneOverride != scenePath)
            return null;

        string school = _pendingSchool;
        _pendingSchool = "";
        _pendingScene = "";
        bool won = router.CombatWon;
        var fallen = new List<string>(router.FallenEnemyUnitIds);
        router.HasPendingReturn = false;
        router.ReturnSceneOverride = "";
        router.SavedCombatGuardianKey = "";
        router.SavedResolutionArchmageId = "";
        router.SavedCombatFamilyKills = new Dictionary<string, int>();   // the apparatus is no faction
        PrologueDirector.ClearScriptedSession();

        var outcome = new Outcome
        {
            School = school,
            Result = PrologueDirector.ExamOutcome(school, won, fallen),
        };
        var save = SaveManager.ActiveSave;
        if (save?.Ledger == null)
            return outcome;

        if (outcome.Result == "pass")
        {
            save.Ledger.MetaNarrativeFlags ??= new List<string>();
            if (!save.Ledger.MetaNarrativeFlags.Contains(PassedFlag(school)))
                save.Ledger.MetaNarrativeFlags.Add(PassedFlag(school));
            SaveManager.MarkDirty();

            // The teacher is read before declaring, as the Disciplines row's own path does.
            string faculty = DeclarationService.FindFacultySource(save, school);
            outcome.Declared = DeclarationService.Declare(save, school);
            SaveManager.Save();
            if (outcome.Declared)
            {
                outcome.Panel = ChronicleScheduler.RaiseDeclaration(school) ?? FallbackConferral(school, faculty);
                outcome.Toast = $"{school} declared.";
                outcome.ToastKind = QuestToastKind.Unlock;
            }
            else
            {
                // Declare logged why. The pass stands, so the row declares without the
                // practical once the gate is met again.
                outcome.Toast = $"The {school} practical is passed. {DeclarationService.Evaluate(save, school).Blocker}".Trim();
            }
        }
        else
        {
            outcome.Panel = MissedPanel(save, school);
            outcome.Toast = $"The {school} practical is not passed. You may sit it again.";
        }
        SaveManager.SaveIfDirty();
        GD.Print($"[Exam] The {school} practical came back: {outcome.Result} (won {won}), declared {outcome.Declared}.");
        return outcome;
    }

    /// <summary>The pages' arguments (E2): the school and its teacher, which the story lines
    /// match on; the practical's title and the note for a miss; the fitted plate and mend
    /// numbers in words, which the briefs print (E4); and whether the apparatus was fitted.</summary>
    private static Dictionary<string, string> PageArgs(GuildSaveData save, string school, ExamScaling scaling)
    {
        var script = PrologueDirector.PracticalScript(school);
        var apparatus = ApparatusDefinition(script);
        return new Dictionary<string, string>
        {
            ["school"] = (school ?? "").Trim().ToLowerInvariant(),
            ["faculty"] = ChronicleScheduler.FacultyIdFor(save, school),
            ["exam_name"] = PrologueDirector.PracticalTitle(school),
            ["exam_remark"] = script?.RemarkTime ?? "",
            ["exam_plate"] = NumberWord(scaling.Plate(AbilityParam(apparatus, "chitin", "amount", 3))),
            ["exam_mend"] = NumberWord(scaling.Mend(AbilityParam(apparatus, "regrowth", "threshold", 20))),
            ["fitted"] = scaling.Fitted ? "true" : "false",
        };
    }

    /// <summary>G1: the fitting for this save's wizard, from a loadout built fresh. Loadouts are
    /// otherwise built only at a deploy and in debug fights, so a campus fight would field the
    /// last run's equipment, or none on a fresh boot. This builds them as a deploy does
    /// (EquipmentLoadout.BuildForRun, with the run roster), so the page's numbers, the fight's
    /// fitting and the wizard's own equipment all come from the Armory as it stands. Never during
    /// an expedition: mid-run, the run's loadout stands and the fitting reads it.</summary>
    private static ExamScaling Fitting(GuildSaveData save)
    {
        var armory = save?.Cycle?.Armory;
        if (armory != null && !PlayerSession.IsOnExpedition)
            EquipmentLoadout.BuildForRun(armory, "wizard", CompanionRoster.RunRosterIds(save) ?? new List<string>());
        return ExamScaling.ForWizard();
    }

    /// <summary>The practical's pass unit (the apparatus) as the registry defines it, or null.</summary>
    private static UnitDefinition ApparatusDefinition(PrologueFightScript script)
    {
        string id = script?.PassUnit ?? "";
        return id.Length > 0 && UnitRegistry.TryResolveId(id, out var resolved) ? UnitRegistry.Get(resolved) : null;
    }

    /// <summary>An integer parameter of the apparatus's first ability with this key, or the
    /// fallback (the handlers' own defaults).</summary>
    private static int AbilityParam(UnitDefinition def, string key, string param, int fallback)
    {
        if (def?.Abilities != null)
            foreach (var ab in def.Abilities)
                if (ab != null && string.Equals(ab.Key, key, StringComparison.OrdinalIgnoreCase))
                    return ab.GetIntParam(param, fallback);
        return fallback;
    }

    private static readonly string[] Ones =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    };
    private static readonly string[] Tens =
        { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    /// <summary>A number as the briefs write it ("three", "twenty-five"); digits from 100 up.</summary>
    public static string NumberWord(int n)
    {
        if (n < 0 || n >= 100)
            return n.ToString();
        if (n < 20)
            return Ones[n];
        int t = n / 10, o = n % 10;
        return o == 0 ? Tens[t] : $"{Tens[t]}-{Ones[o]}";
    }

    private static string PassedFlag(string school) =>
        PassedFlagPrefix + (school ?? "").Trim().ToLowerInvariant();

    /// <summary>The scene the exam returns to: the standalone campus when it was sat there, else
    /// the strategic hub, which also hosts the campus overlay and the city's floated panels.</summary>
    private static string ReturnSceneFor(SceneTree tree)
    {
        string current = tree?.CurrentScene?.SceneFilePath ?? "";
        return current == PrologueDirector.CampusScenePath
            ? PrologueDirector.CampusScenePath
            : PrologueDirector.StrategicScenePath;
    }

    /// <summary>The conferral when the chronicle has no story data for it: the Provost's line,
    /// finishing (DeclarationService.ConferralLine), as the Disciplines row's dialog had it.</summary>
    private static NarrativeEncounterData FallbackConferral(string school, string faculty)
    {
        var panel = new NarrativeEncounterData
        {
            Id = "declaration_conferral_fallback",
            Title = "The Conferral",
            Body = DeclarationService.ConferralLine(school, faculty),
        };
        panel.Choices.Add(new EncounterChoice { Label = "Let it be written.", ResultText = "" });
        return panel;
    }
}

/// <summary>E4 (ruled 2026-10-26): a declaration exam's apparatus fitted to the wizard, so a
/// wizard who has grown since Commencement still has to use the school's verb. The practicals
/// were tuned for the candidate of the prologue: 20 health, 3 mana, no armor, no shield, no
/// spell damage. Computed from the equipment the wizard will field (<see cref="EquipmentLoadout"/>,
/// built fresh by DisciplineExam.Fitting, G1), carried on the router to the fight, and applied at
/// spawn (CombatManager.Exam.cs); the brief prints the same numbers (DisciplineExam.PageArgs).
/// <list type="bullet">
/// <item>Health: the apparatus's health grows with the wizard's damage, mana over 3 times
/// (4 plus spell damage) over 4, at most threefold. A plated apparatus (chitin) takes the
/// spell damage on its plate instead, so its health counts mana only.</item>
/// <item>Plate: chitin grows by the wizard's spell damage, so plating still has to be met
/// with the school's verb.</item>
/// <item>Mend: a mending apparatus's threshold grows with its health.</item>
/// <item>Blows: an apparatus that strikes hits as hard, against the wizard's health and
/// shield, as it did against the candidate's 20, plus the wizard's armor.</item>
/// <item>Practice dummies (role "summon") are not fitted: they are the rack's material.</item>
/// </list></summary>
public sealed class ExamScaling
{
    public const int BaseHealth = 20;
    public const int BaseMana = 3;
    public const float BaseHit = 4f;
    public const float MaxScale = 3f;

    public int ManaBonus;
    public int HitBonus;
    public int HealthBonus;
    public int ShieldBonus;
    public int ArmorBonus;

    public float ManaScale => (BaseMana + ManaBonus) / (float)BaseMana;
    public float HitScale => (BaseHit + HitBonus) / BaseHit;
    public float DamageScale => Mathf.Max(1f, (BaseHealth + HealthBonus + ShieldBonus) / (float)BaseHealth);

    /// <summary>True when the wizard fields anything the candidate did not.</summary>
    public bool Fitted => ManaBonus > 0 || HitBonus > 0 || HealthBonus > 0 || ShieldBonus > 0 || ArmorBonus > 0;

    public float HealthScale(bool plated) =>
        Mathf.Clamp(plated ? ManaScale : ManaScale * HitScale, 1f, MaxScale);

    public int Health(int baseHealth, bool plated) =>
        Mathf.Max(1, Mathf.RoundToInt(baseHealth * HealthScale(plated)));

    public int Damage(int baseDamage) =>
        baseDamage <= 0 ? baseDamage : Mathf.RoundToInt(baseDamage * DamageScale) + ArmorBonus;

    public int Plate(int baseAmount) => baseAmount + HitBonus;

    public int Mend(int baseThreshold) => Mathf.RoundToInt(baseThreshold * HealthScale(false));

    /// <summary>From the wizard's equipment as it will field (EquipmentLoadout.Get("wizard"),
    /// the loadout CombatManager.ApplyEquipmentLoadout applies). With none, the wizard fields as
    /// the candidate did and nothing is fitted.</summary>
    public static ExamScaling ForWizard()
    {
        var s = new ExamScaling();
        var loadout = EquipmentLoadout.Get("wizard");
        if (loadout == null)
            return s;
        s.ManaBonus = Math.Max(0, loadout.BonusMaxMana);
        s.HitBonus = Math.Max(0, loadout.BonusSpellDamage);
        s.HealthBonus = Math.Max(0, loadout.BonusMaxHP);
        s.ArmorBonus = Math.Max(0, loadout.BonusArmor);
        if (loadout.Passives != null)
            foreach (var (tag, value, _) in loadout.Passives)
                if (tag == ItemPassiveTag.StartCombatWithShield)
                    s.ShieldBonus += Math.Max(0, value);
        return s;
    }

    public override string ToString() => Fitted
        ? $"fitted to the wizard: health x{HealthScale(false):0.00} (x{HealthScale(true):0.00} plated), " +
          $"plate +{HitBonus}, blows x{DamageScale:0.00} +{ArmorBonus}"
        : "the wizard fields as the candidate did: not fitted";
}
