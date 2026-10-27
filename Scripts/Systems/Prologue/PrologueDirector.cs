using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

// ============================================================
// PrologueDirector.cs
//
// Purpose:        The intro director (wire item C4,
//                 docs/prologue_script_v1.md, section 7). Runs
//                 Commencement for a new save: the stated intention
//                 (stated on the founding screen and read back by the
//                 Examiner, R7d; asked, for a save founded without one),
//                 the skip offers, the two-part examination (the duel,
//                 then the common practical, which every candidate sits
//                 since R7e),
//                 Commencement, the Sundering, the Long Second and the
//                 Margin in the examination hall (PrologueScreen); the
//                 Anchorhold and the Door at the first quiet moment in
//                 the home city (ChronicleScheduler.DrainIfQuiet); and
//                 Legible on the first sortie (ExpeditionManager).
//
//                 The seven school practicals are also the examinations
//                 that declare those schools in the Grand Hall (R7e):
//                 HasPractical, PracticalScript, PracticalTitle, LaunchExam
//                 and ExamOutcome, for DisciplineExam. An exam is sat in
//                 its practical's Grand Hall recipe (examMapRecipe, E3)
//                 with its apparatus fitted to the wizard (ExamScaling, E4).
//
//                 Every step resumes from the prologue's meta flags. The
//                 panels' own flags mark most steps; the director adds
//                 a few of its own where a step sets none: the begun
//                 marker, each fight's result, part one done, the
//                 Anchorhold seen and the profile noted.
// Layer:          System (static)
// Collaborators:  PrologueScreen.cs (the hall), ChronicleScheduler.cs
//                 (the world panels, the global tokens),
//                 EncounterRouter (the fight round trip and its
//                 scripted fields), PlayerSession (the scripted deck),
//                 RegisterManager (PrologueMode), CombatManager.Scripted.cs
//                 (the engine hooks), CombatManager.Tutorial.cs (a
//                 fight's checklist, StepsFor), NewGameScreen
//                 (RecordFoundingIntention), SaveManager.NewGame (the
//                 begun marker), DisciplineExam (the declaration exams)
// See:            docs/prologue_script_v1.md (section 1 Flow, section 5
//                 Tokens, section 7 C4), docs/story_wiring_addendum_slice2.md
// ============================================================

/// <summary>One entry of Data/Encounters/prologue_fights.json: the encounter and the
/// director's script for it.</summary>
public sealed class PrologueFightEntry
{
    public string Id = "";
    public PrologueEncounterData Encounter = new();
    public PrologueFightScript Script = new();
}

/// <summary>The encounter half of a prologue fight, read as written and turned into an
/// <see cref="EncounterDefinition"/> by the director. Read through its own shape rather
/// than straight into EncounterDefinition, so the tier needs no enum converter and the
/// enemy slots need no struct binding.</summary>
public sealed class PrologueEncounterData
{
    public string Id = "";
    public string DisplayName = "";
    public string Tier = "Skirmish";
    public string RegionId = "";
    public string TerrainType = "";
    public string MapRecipe = "";
    public float DifficultyMult = 1f;
    public List<PrologueEnemySlot> Enemies = new();
    public PrologueObjective Objective = null;
    public List<PrologueWave> Waves = new();
}

public sealed class PrologueEnemySlot
{
    public string UnitId = "";
    public float DifficultyMult = 1f;
}

public sealed class PrologueObjective
{
    public string Kind = "";
    /// <summary>survive: won at the end of this round. 0 is no cap: the checklist fights run until
    /// they are done, the candidate falls or the candidate yields (ruled 2026-10-27). The
    /// director builds the objective itself, so the general loader's "survive needs rounds" rule
    /// does not apply here.</summary>
    public int Rounds = 0;
    public string WardUnitId = "";
    public string Description = "";
}

public sealed class PrologueWave
{
    public int Round = 3;
    public List<PrologueEnemySlot> Enemies = new();
    public string Announce = "";
}

/// <summary>One step of a fight's checklist (CombatManager.Tutorial.cs): an id the engine
/// counts (CombatManager.TutorialStepIds: its own, such as walk, dodge, mark or turn_spells, and
/// the school moments, such as arc.first_charge), the label the banner shows, and how many it
/// takes.</summary>
public sealed class PrologueStep
{
    public string Id = "";
    public string Label = "";
    public int Count = 1;
}

/// <summary>The director's half of a prologue fight (section 7, C4 item 5).</summary>
public sealed class PrologueFightScript
{
    public string Part = "";
    /// <summary>The CardSchool the wizard plays (practicals only).</summary>
    public string School = "";
    /// <summary>"wizard_only": no companions and no companion cards.</summary>
    public string Party = "";
    public bool NoDefeat = false;
    public bool SuppressRewards = false;
    public bool SuppressDeeds = false;
    /// <summary>"persistent" (the save's deck), "folio" (this fight's) or "folio_sat" (the
    /// folio of the practical this save sat, with no innate cards).</summary>
    public string Deck = "";
    public List<string> Folio = new();
    public List<string> Innate = new();
    public string PassUnit = "";
    public bool EndOnPassUnit = false;
    public string RegisterMode = "";
    public string RemarkPass = "";
    public string RemarkTime = "";
    public string OnWinClean = "";
    public string OnWin = "";
    public string OnLoss = "";
    public string OnPass = "";
    public string OnTime = "";
    public string OnAnyOutcome = "";
    /// <summary>The fight's checklist (the trial, ruled 2026-10-26; the practicals,
    /// 2026-10-27). With no pass unit the fight is won once every step is done; with one, the
    /// pass unit is warded, one ward per step, and each step done breaks one. Neither has a
    /// round cap (2026-10-27): the fight also ends on the floor, or when the candidate yields
    /// (PauseMenu). Empty for every other fight.</summary>
    public List<PrologueStep> Steps = new();
    /// <summary>The player places the party, as in a fight outside the prologue (the common
    /// practical, 2026-10-27). Every other scripted fight confirms deployment itself.</summary>
    public bool PlayerDeploys = false;
    /// <summary>The opening-hand sculpt is offered, as in a fight outside the prologue (the
    /// common practical, 2026-10-27). Every other scripted fight keeps the hand it deals.</summary>
    public bool OfferOpeningHand = false;
    /// <summary>A school practical's map when it is sat as the Grand Hall's examination for the
    /// school (E3: "grand_hall", or "grand_hall_frost" for the Frost Plate). Empty for every
    /// fight that is not an examination.</summary>
    public string ExamMapRecipe = "";
}

/// <summary>Process-wide intro director. Every method is safe with no save loaded and with
/// the prologue's data missing: it returns null or "world", and the hall closes the
/// prologue as a full skip rather than leave the save stuck.</summary>
public static class PrologueDirector
{
    // ── Paths ────────────────────────────────────────────────────────────
    public const string ScenePath = "res://Scenes/Prologue/PrologueScene.tscn";
    public const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    public const string CampusScenePath = "res://Scenes/Campus/CampusScene.tscn";
    private const string PanelFile = "res://Data/Encounters/prologue.json";
    private const string FightFile = "res://Data/Encounters/prologue_fights.json";

    /// <summary>Profile-wide, never in the save: one practical fight id per line, written when
    /// a save's prologue completes. The skips read it (P-2, P-3).</summary>
    private const string CompletedFile = "user://prologue_completed.txt";

    // ── Steps the hall acts on (besides panel ids) ───────────────────────
    public const string StepWorld = "world";
    public const string StepSkips = "skips";
    public const string FightPrefix = "fight:";

    // ── Panel and fight ids (Data/Encounters/prologue.json, prologue_fights.json) ──
    public const string IntentionPanelId = "prologue_trial_intention";
    /// <summary>The Examiner reads back the intention stated on the founding screen.</summary>
    public const string IntentionReadPanelId = "prologue_trial_intention_read";
    public const string RulesPanelId = "prologue_trial_rules";
    public const string RulesWaivedPanelId = "prologue_trial_rules_waived";
    public const string ApparatusPanelId = "prologue_practical_apparatus";
    public const string BriefPanelPrefix = "prologue_practical_brief_";
    public const string CommencementPanelId = "prologue_commencement";
    public const string SunderingOpenPanelId = "prologue_sundering_open";
    public const string SunderingCastPanelId = "prologue_sundering_cast";
    public const string LongSecondPanelId = "prologue_long_second";
    public const string MarginPanelId = "prologue_the_margin";
    public const string AnchorholdPanelId = "prologue_anchorhold";
    public const string DoorPanelId = "prologue_the_door";
    public const string LegiblePanelId = "prologue_legible";
    public const string RecapPanelId = "prologue_skip_recap";
    public const string TrialFightId = "prologue_trial";
    public const string SunderingFightId = "prologue_sundering";
    public const string PracticalFightPrefix = "prologue_practical_";
    /// <summary>The practical every candidate sits in the prologue (R7e): the common one.</summary>
    public const string CommonPracticalSchool = "adept";

    // ── The director's own meta flags (the panels set the rest) ──────────
    /// <summary>Written by SaveManager.NewGame. A save without it predates the prologue and
    /// reads the full skip's recap instead (ChronicleScheduler's bridge, B-1).</summary>
    public const string BegunFlag = "prologue_begun";
    /// <summary>The intention was stated on the founding screen (RecordFoundingIntention), so the
    /// Examiner reads it back instead of asking.</summary>
    public const string FoundingIntentionFlag = "stated_intention_founding";
    private const string IntentionReadFlag = "prologue_intention_read";
    private const string PartOneDoneFlag = "prologue_part_one_done";
    private const string AnchorholdSeenFlag = "prologue_anchorhold_seen";
    private const string ProfileNotedFlag = "prologue_profile_noted";
    private const string ResultSunderingFlag = "prologue_result_sundering";
    private const string ResultPartOnePrefix = "prologue_result_part_one_";     // clean, win, floor
    private const string ResultPracticalPrefix = "prologue_result_practical_";  // pass, time, floor

    // ── Token fallbacks (section 5) ──────────────────────────────────────
    private const string TraitorLineFallback =
        "Don't look at me like that. He showed me the ending. I am only choosing where to stand when it arrives.";
    private const string RemarkFallback = "Adequate.";
    private const string PracticalNameFallback = "your practical";
    private const string IntentionRemarkFallback = "'Noted.'";

    /// <summary>The recap's flags, used only when prologue.json is missing and the prologue
    /// has to close as a skip.</summary>
    private static readonly string[] FallbackRecapFlags =
    {
        "prologue_trial_done", "prologue_commencement_done", "prologue_sundering_done",
        "prologue_long_second_seen", "prologue_register_taken", "prologue_complete", "prologue_skipped",
    };

    private static readonly Regex TokenRx = new(@"\{([a-z_][a-z0-9_]*)\}", RegexOptions.Compiled);

    // Mirrors NarrativeEncounterLoader, so the panel file reads exactly as the pools do.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    private static Dictionary<string, NarrativeEncounterData> _panels;
    private static Dictionary<string, PrologueFightEntry> _fights;

    // ════════════════════════════════════════════════════════════════════════
    //  Where the save stands
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True while a new save is still inside the hall (P1 to P4): StrategicView sends
    /// it back to the prologue scene.</summary>
    public static bool NeedsHall(GuildSaveData save) =>
        save?.Ledger != null && HasMeta(save, BegunFlag)
        && !save.HasFlag("prologue_register_taken") && !save.HasFlag("prologue_complete");

    /// <summary>The hall's next step, read from the flags alone, so a save closed at any
    /// point resumes where it stood: a panel id, <see cref="StepSkips"/> or
    /// <see cref="StepWorld"/>. A fight resumes at the panel that launches it.</summary>
    public static string NextStep(GuildSaveData save)
    {
        if (save?.Ledger == null || !HasMeta(save, BegunFlag)
            || save.HasFlag("prologue_complete") || save.HasFlag("prologue_register_taken"))
            return StepWorld;
        if (!save.HasFlag("stated_intention_set"))
            return IntentionPanelId;
        if (HasMeta(save, FoundingIntentionFlag) && !HasMeta(save, IntentionReadFlag))
            return IntentionReadPanelId;
        if (!PartOneDone(save))
        {
            string r = PartOneResult(save);
            return r.Length > 0 ? PartOneVerdict(r) : StepSkips;
        }
        string school = PracticalSchool(save);
        if (!HasMeta(save, $"prologue_practical_{school}"))
            return ApparatusPanelId;
        string pr = PracticalResult(save);
        if (pr.Length == 0)
            return BriefPanelPrefix + school;
        if (!save.HasFlag("prologue_trial_done"))
            return PracticalVerdict(save, pr);
        if (!save.HasFlag("prologue_commencement_done"))
            return CommencementPanelId;
        if (!HasMeta(save, ResultSunderingFlag))
            return SunderingOpenPanelId;
        if (!save.HasFlag("prologue_sundering_done"))
            return SunderingCastPanelId;
        if (!save.HasFlag("prologue_long_second_seen"))
            return LongSecondPanelId;
        return MarginPanelId;
    }

    /// <summary>The step after <paramref name="panelId"/> was answered (its choice already
    /// applied). Panels that set no flag chain here; every other step reads the flags.</summary>
    public static string After(string panelId, GuildSaveData save)
    {
        switch (panelId)
        {
            case IntentionPanelId:
            case IntentionReadPanelId:
                return StepSkips;
            case RulesPanelId:
                return FightPrefix + TrialFightId;
            case ApparatusPanelId:
                return BriefPanelPrefix + PracticalSchool(save);
            case SunderingOpenPanelId:
                return FightPrefix + SunderingFightId;
            case MarginPanelId:
            case RecapPanelId:
                return StepWorld;
        }
        if (!string.IsNullOrEmpty(panelId) && panelId.StartsWith(BriefPanelPrefix, StringComparison.Ordinal))
            return FightPrefix + PracticalFightId(save);
        return NextStep(save);
    }

    /// <summary>The Register's mode while <paramref name="panelId"/> is up (section 4.1):
    /// "trial" until the Sundering, "sundering" through the last clause, empty from the
    /// Long Second on.</summary>
    public static string RegisterModeFor(string panelId)
    {
        switch (panelId)
        {
            case SunderingCastPanelId:
                return "sundering";
            case LongSecondPanelId:
            case MarginPanelId:
            case AnchorholdPanelId:
            case DoorPanelId:
            case LegiblePanelId:
            case RecapPanelId:
                return "";
        }
        return "trial";
    }

    /// <summary>The practical this save sits in the prologue: the common one ("adept") for every
    /// candidate since R7e (2026-10-26). The stated intention is the first goal instead, and its
    /// school's practical is the Grand Hall's examination for it (DisciplineExam). A save that
    /// began a school's practical under the old rule (R7b as amended 2026-10-13) keeps it, so it
    /// resumes, and later reads, as it was sat.</summary>
    public static string PracticalSchool(GuildSaveData save)
    {
        if (save == null)
            return CommonPracticalSchool;
        foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
        {
            string key = s.ToString().ToLowerInvariant();
            if (key != CommonPracticalSchool && HasMeta(save, $"prologue_practical_{key}"))
                return key;
        }
        return CommonPracticalSchool;
    }

    public static string PracticalFightId(GuildSaveData save) => PracticalFightPrefix + PracticalSchool(save);

    /// <summary>The apparatus the practical is sat against ("The Frost Plate"): its brief's title.</summary>
    public static string PracticalName(GuildSaveData save)
    {
        var brief = RawPanel(BriefPanelPrefix + PracticalSchool(save));
        return NonEmpty(brief?.Title, PracticalNameFallback);
    }

    /// <summary>True when <paramref name="school"/> has a practical, fight and brief. Every school
    /// does; the seven besides Adept sit theirs in the Grand Hall to declare it (R7e).</summary>
    public static bool HasPractical(string school)
    {
        string key = SchoolKey(school);
        return key.Length > 0 && Fight(PracticalFightPrefix + key) != null && RawPanel(BriefPanelPrefix + key) != null;
    }

    /// <summary>A school's practical script (read only: its pass unit, folio, remarks and exam
    /// map), or null when the school has none.</summary>
    public static PrologueFightScript PracticalScript(string school)
    {
        string key = SchoolKey(school);
        return key.Length > 0 ? Fight(PracticalFightPrefix + key)?.Script : null;
    }

    /// <summary>A school's practical by its apparatus ("The Frost Plate"): its brief's title.</summary>
    public static string PracticalTitle(string school)
    {
        string key = SchoolKey(school);
        var brief = key.Length > 0 ? RawPanel(BriefPanelPrefix + key) : null;
        return NonEmpty(brief?.Title, PracticalNameFallback);
    }

    /// <summary>A CardSchool name in lower case, any case in, or "" when it names none. Names
    /// only, as IntentionKey: a number never parses as a school here.</summary>
    private static string SchoolKey(string school)
    {
        string v = (school ?? "").Trim();
        foreach (var name in Enum.GetNames(typeof(CardSchool)))
            if (string.Equals(name, v, StringComparison.OrdinalIgnoreCase))
                return name.ToLowerInvariant();
        return "";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Panels
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>A display copy of a prologue panel with its tokens filled (section 5). Where a
    /// step sets no flag of its own, the director adds one to every choice: part one's
    /// verdicts mark part one done, the Anchorhold marks itself seen, and the full skip's
    /// recap writes the practical this save sits (the common one since R7e; wiring doc, found
    /// item 4), so {practical_name} resolves later. Null when the panel is missing.</summary>
    public static NarrativeEncounterData Panel(string id, GuildSaveData save)
    {
        var raw = RawPanel(id);
        if (raw == null || save == null)
            return null;
        var add = new List<string>();
        if (IsPartOneVerdict(id))
            add.Add(PartOneDoneFlag);
        if (id == AnchorholdPanelId)
            add.Add(AnchorholdSeenFlag);
        if (id == IntentionReadPanelId)
            add.Add(IntentionReadFlag);
        if (id == RecapPanelId && HasMeta(save, BegunFlag))
            add.Add($"prologue_practical_{PracticalSchool(save)}");
        return Render(raw, save, add);
    }

    /// <summary>P5 at a quiet moment in the home city (screen "campus"): the Anchorhold, then
    /// the Door, whose choice completes the prologue. Null for any other moment or save.</summary>
    public static NarrativeEncounterData WorldPanel(GuildSaveData save, string screen)
    {
        if (save?.Ledger == null || screen != "campus" || !HasMeta(save, BegunFlag)
            || !save.HasFlag("prologue_register_taken") || save.HasFlag("prologue_complete"))
            return null;
        return Panel(HasMeta(save, AnchorholdSeenFlag) ? DoorPanelId : AnchorholdPanelId, save);
    }

    /// <summary>P6 on the first sortie once the prologue is complete (or skipped): Legible.
    /// Its choice sets chronicle_first_sortie.</summary>
    public static NarrativeEncounterData LegiblePanel(GuildSaveData save)
    {
        if (save?.Ledger == null || !save.HasFlag("prologue_complete") || save.HasFlag("chronicle_first_sortie"))
            return null;
        return Panel(LegiblePanelId, save);
    }

    /// <summary>Apply a prologue choice the way the narrative appliers do: timeline flags,
    /// meta flags and lore. The prologue grants no lore (P-1); the line is there for parity.</summary>
    public static void ApplyChoice(GuildSaveData save, EncounterChoice choice)
    {
        if (save?.Ledger == null || choice == null)
            return;
        if (choice.SetFlags != null)
            foreach (var flag in choice.SetFlags)
                save.SetFlag(flag);
        if (choice.SetMetaFlags != null)
            foreach (var flag in choice.SetMetaFlags)
                SetMeta(save, flag);
        if (!string.IsNullOrEmpty(choice.LoreId) && !save.UnlockedLoreEntries.Contains(choice.LoreId))
            save.UnlockedLoreEntries.Add(choice.LoreId);
        SaveManager.MarkDirty();
    }

    /// <summary>The intention a new save stated on the founding screen (R7b as amended
    /// 2026-10-26): a CardSchool name, any case, or "unstated". Writes the meta flags the asking
    /// panel's choices write, plus the founding marker, so the Examiner reads it back. False,
    /// and nothing written, when the save already has an intention or the value names none.</summary>
    public static bool RecordFoundingIntention(GuildSaveData save, string intention)
    {
        if (save?.Ledger == null || save.HasFlag("stated_intention_set"))
            return false;
        string key = IntentionKey(intention);
        if (key.Length == 0)
        {
            GD.PushWarning($"[Prologue] '{intention}' is not a school or \"unstated\"; the Examiner will ask.");
            return false;
        }
        SetMeta(save, "stated_intention_set");
        SetMeta(save, $"stated_intention_{key}");
        SetMeta(save, FoundingIntentionFlag);
        SaveManager.MarkDirty();
        GD.Print($"[Prologue] Stated intention at founding: {key}.");
        return true;
    }

    /// <summary>"unstated", a CardSchool name in lower case, or empty when the value is
    /// neither. Names only: a number never parses as a school here.</summary>
    private static string IntentionKey(string value)
    {
        string v = (value ?? "").Trim();
        if (string.Equals(v, "unstated", StringComparison.OrdinalIgnoreCase))
            return "unstated";
        foreach (var name in Enum.GetNames(typeof(CardSchool)))
            if (string.Equals(name, v, StringComparison.OrdinalIgnoreCase))
                return name.ToLowerInvariant();
        return "";
    }

    /// <summary>A fight's checklist (CombatManager.Tutorial.cs), or null when it has none.</summary>
    public static IReadOnlyList<PrologueStep> StepsFor(string fightId)
    {
        var steps = Fight(fightId)?.Script?.Steps;
        return steps != null && steps.Count > 0 ? steps : null;
    }

    /// <summary>The fight's script hands deployment to the player (CombatManager.Scripted.cs).</summary>
    public static bool DeploysByHand(string fightId) => Fight(fightId)?.Script?.PlayerDeploys == true;

    /// <summary>The fight's script offers the opening-hand sculpt (CombatManager.Scripted.cs).</summary>
    public static bool OffersOpeningHand(string fightId) => Fight(fightId)?.Script?.OfferOpeningHand == true;

    /// <summary>Close the prologue as a full skip: the recap's flags, and an unstated
    /// intention when none was given. The hall's last resort when its data is missing.</summary>
    public static void ForceComplete(GuildSaveData save)
    {
        if (save?.Ledger == null)
            return;
        var recap = RawPanel(RecapPanelId);
        var flags = recap != null && recap.Choices != null && recap.Choices.Count > 0
                    && recap.Choices[0]?.SetMetaFlags != null && recap.Choices[0].SetMetaFlags.Count > 0
            ? (IEnumerable<string>)recap.Choices[0].SetMetaFlags
            : FallbackRecapFlags;
        foreach (var flag in flags)
            SetMeta(save, flag);
        if (!save.HasFlag("stated_intention_set"))
        {
            SetMeta(save, "stated_intention_set");
            SetMeta(save, "stated_intention_unstated");
        }
        SaveManager.MarkDirty();
        GD.PushWarning("[Prologue] The prologue closed as a full skip because its data is missing.");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Skips (P-2, P-3): the profile file
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>P-2: the full skip is offered only for a practical already completed on this
    /// computer: the common one, for every save since R7e.</summary>
    public static bool FullSkipOffered(GuildSaveData save) => ReadProfile().Contains(PracticalFightId(save));

    /// <summary>P-3: part one can be waived once any prologue is complete on this computer.</summary>
    public static bool PartOneSkipOffered() => ReadProfile().Count > 0;

    /// <summary>Once a new save's prologue is complete (at the Door, or by the recap), list its
    /// practical in the profile file. Called at every quiet moment and when the hall closes;
    /// the noted flag keeps it to once per save.</summary>
    public static void NoteProfileCompletion(GuildSaveData save)
    {
        if (save?.Ledger == null || !HasMeta(save, BegunFlag) || !save.HasFlag("prologue_complete")
            || HasMeta(save, ProfileNotedFlag))
            return;
        string id = PracticalFightId(save);
        if (!ReadProfile().Contains(id))
            AppendProfileLine(id);
        SetMeta(save, ProfileNotedFlag);
        SaveManager.MarkDirty();
        GD.Print($"[Prologue] {id} is complete on this computer.");
    }

    private static HashSet<string> ReadProfile(string path = CompletedFile)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!FileAccess.FileExists(path))
            return set;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
            return set;
        while (!f.EofReached())
        {
            string line = f.GetLine().Trim();
            if (line.Length > 0)
                set.Add(line);
        }
        return set;
    }

    private static void AppendProfileLine(string id, string path = CompletedFile)
    {
        using var f = FileAccess.FileExists(path)
            ? FileAccess.Open(path, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
            return;
        f.SeekEnd();
        f.StoreLine(id);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Fights
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Launch a prologue fight through the campus round-trip pattern (C4 item 5): the
    /// scripted deck on PlayerSession, the scripted fields on the router, the Register's mode,
    /// and a return to the hall whatever the outcome. False when the fight cannot launch.</summary>
    public static bool Launch(SceneTree tree, string fightId, GuildSaveData save) =>
        LaunchFight(tree, fightId, save, ScenePath, null);

    /// <summary>Launch a school's practical as the Grand Hall's examination for it (R7e,
    /// DisciplineExam): its folio, party and pass unit as the prologue deals them, and no rewards,
    /// but the Register in its own voice rather than the trial's minutes, the Grand Hall's map
    /// (the script's examMapRecipe, E3), the apparatus fitted to the wizard
    /// (<paramref name="scaling"/>, E4), and a return to <paramref name="returnScene"/>, whose host
    /// consumes it. False when it cannot launch.</summary>
    public static bool LaunchExam(SceneTree tree, string school, GuildSaveData save, string returnScene,
                                  ExamScaling scaling)
    {
        string key = SchoolKey(school);
        if (key.Length == 0 || string.IsNullOrEmpty(returnScene))
            return false;
        string fightId = PracticalFightPrefix + key;
        return LaunchFight(tree, fightId, save, returnScene, "", Fight(fightId)?.Script?.ExamMapRecipe, scaling);
    }

    /// <summary>The launch itself: the hall's fights return to the hall in the script's Register
    /// mode, on their own map; an exam names its own return and mode (<paramref name="registerMode"/>,
    /// when not null), its map (<paramref name="mapRecipe"/>, when not empty) and its fitting
    /// (<paramref name="examScaling"/>, which every other fight clears).</summary>
    private static bool LaunchFight(SceneTree tree, string fightId, GuildSaveData save, string returnScene,
                                    string registerMode, string mapRecipe = null, ExamScaling examScaling = null)
    {
        var entry = Fight(fightId);
        if (tree == null || save == null || entry?.Encounter == null)
            return false;
        var def = BuildEncounter(entry.Encounter);
        if (def.Enemies.Count == 0)
            return false;
        if (!string.IsNullOrEmpty(mapRecipe))
            def.MapRecipe = mapRecipe;
        var script = entry.Script ?? new PrologueFightScript();

        // The deck (hook a) and the school it plays (hook b).
        PlayerSession.ScriptedDeck = null;
        PlayerSession.ScriptedInnate = null;
        PlayerSession.ScriptedSchool = null;
        switch ((script.Deck ?? "").Trim().ToLowerInvariant())
        {
            case "folio":
                PlayerSession.ScriptedDeck = new List<string>(script.Folio ?? new List<string>());
                PlayerSession.ScriptedInnate = new List<string>(script.Innate ?? new List<string>());
                PlayerSession.ScriptedSchool = ParseSchool(script.School);
                break;
            case "folio_sat":
                var sat = Fight(PracticalFightId(save))?.Script;
                if (sat != null && sat.Folio != null && sat.Folio.Count > 0)
                {
                    PlayerSession.ScriptedDeck = new List<string>(sat.Folio);
                    PlayerSession.ScriptedInnate = new List<string>();
                    PlayerSession.ScriptedSchool = ParseSchool(sat.School);
                }
                break;
        }
        PlayerSession.ScriptedWizardOnly = string.Equals(script.Party, "wizard_only", StringComparison.OrdinalIgnoreCase);
        PlayerSession.IsOnExpedition = false;
        PlayerSession.ExpeditionRunKind = ExpeditionRunKind.Castle;
        PlayerDeckSave.UseDebugDeck = false;

        // The router, created lazily as the campus does (CampusScreen.LaunchCampusCombat).
        if (EncounterRouter.Instance == null)
            tree.Root.AddChild(new EncounterRouter { Name = "EncounterRouter" });
        var router = EncounterRouter.Instance;
        if (router == null)
            return false;
        router.HasPendingReturn = false;
        router.SavedCombatWasPatrolAmbush = false;
        router.SavedCombatPatrolArchmageId = "";
        router.SavedCombatGuardianKey = "";
        router.SavedCombatArchmageId = "";
        router.SavedResolutionArchmageId = "";
        router.SavedWeather = WeatherType.Clear;
        router.ReturnSceneOverride = returnScene;
        router.SetCurrentTier(def.Tier);
        router.SuppressRewards = script.SuppressRewards || script.NoDefeat;
        router.EndOnUnitFall = script.EndOnPassUnit ? (script.PassUnit ?? "") : "";
        router.ScriptedFightId = fightId;
        router.ScriptedReturnTitle = def.DisplayName ?? "";
        router.ScriptedFightYielded = false;
        router.FallenEnemyUnitIds.Clear();
        router.LastWizardHealth = -1;
        router.LastWizardMaxHealth = -1;
        router.ExamFitting = examScaling;   // E4: an exam's fitting, null for every other fight

        // hook e: the Register minutes the trial and is silent in the Sundering. An exam keeps
        // the Register's own voice: the candidate has carried it since the Long Second.
        RegisterManager.PrologueMode = registerMode ?? script.RegisterMode ?? "";

        SaveManager.SaveIfDirty();
        EncounterContextCarrier.Set(def);
        EncounterContextCarrier.SetContext(def.TerrainType, def.Tier);
        GD.Print($"[Prologue] Launching {fightId} (deck {script.Deck}, mode {RegisterManager.PrologueMode}, "
                 + $"map {def.MapRecipe}, back to {returnScene}).");
        SceneTransition.Go(tree, router.CombatScenePath, def.DisplayName ?? "", def.Objective?.Description ?? "");
        return true;
    }

    /// <summary>Consume a prologue fight's return: record its result as a meta flag (so the
    /// verdict survives a quit), clear the scripted session, and give the next panel. Null
    /// when nothing of the prologue's is waiting on the router.</summary>
    public static string ConsumeReturn(GuildSaveData save)
    {
        var router = EncounterRouter.Instance;
        if (save?.Ledger == null || router == null || !router.HasPendingReturn
            || router.ReturnSceneOverride != ScenePath)
            return null;

        string fightId = router.ScriptedFightId ?? "";
        bool won = router.CombatWon;
        var fallen = new List<string>(router.FallenEnemyUnitIds);
        bool unhurt = router.LastWizardMaxHealth > 0 && router.LastWizardHealth >= router.LastWizardMaxHealth;
        bool yielded = router.ScriptedFightYielded;

        router.HasPendingReturn = false;
        router.ReturnSceneOverride = "";
        router.SavedCombatFamilyKills = new Dictionary<string, int>();   // P-1: no Marginalia
        ClearScriptedSession();

        var entry = Fight(fightId);
        if (entry == null)
            return null;   // not a fight this director knows: resume from the flags
        var script = entry.Script ?? new PrologueFightScript();

        string next = null;
        if (fightId == TrialFightId)
        {
            // A yield (PauseMenu) is her plain verdict: the bell, not the floor.
            string r = yielded ? "win" : !won ? "floor" : unhurt ? "clean" : "win";
            SetMeta(save, ResultPartOnePrefix + r);
            next = PartOneVerdict(r);
        }
        else if (fightId == SunderingFightId)
        {
            SetMeta(save, ResultSunderingFlag);
            next = NonEmpty(script.OnAnyOutcome, SunderingCastPanelId);
        }
        else if (fightId.StartsWith(PracticalFightPrefix, StringComparison.Ordinal))
        {
            string r = PracticalOutcome(script, won, fallen, yielded);
            SetMeta(save, ResultPracticalPrefix + r);
            next = PracticalVerdict(save, r);
        }
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print($"[Prologue] {fightId} came back (won {won}{(yielded ? ", yielded" : "")}); next {next}.");
        return next;
    }

    /// <summary>Drop every scripted-fight setting and the Register's prologue mode. The hubs
    /// (StrategicView, CampusScreen) call it on the way in, so a prologue fight abandoned part
    /// way can never leak into an ordinary one; the hall calls it when a fight returns.</summary>
    public static void ClearScriptedSession()
    {
        PlayerSession.ScriptedDeck = null;
        PlayerSession.ScriptedInnate = null;
        PlayerSession.ScriptedSchool = null;
        PlayerSession.ScriptedWizardOnly = false;
        var router = EncounterRouter.Instance;
        if (router != null)
        {
            router.SuppressRewards = false;
            router.EndOnUnitFall = "";
            router.ScriptedFightId = "";
            router.ScriptedReturnTitle = "";
            router.ExamFitting = null;
            router.ScriptedFightYielded = false;
        }
        RegisterManager.PrologueMode = "";
    }

    private static EncounterDefinition BuildEncounter(PrologueEncounterData d)
    {
        var def = new EncounterDefinition
        {
            Id = d.Id ?? "",
            DisplayName = d.DisplayName ?? "",
            Tier = Enum.TryParse<EncounterTier>(d.Tier ?? "", true, out var tier) ? tier : EncounterTier.Skirmish,
            RegionId = d.RegionId ?? "",
            TerrainType = d.TerrainType ?? "",
            MapRecipe = d.MapRecipe ?? "",
            DifficultyMult = d.DifficultyMult,
        };
        if (d.Enemies != null)
            foreach (var slot in d.Enemies)
                def.Enemies.Add(Slot(slot));
        if (d.Objective != null && !string.IsNullOrEmpty(d.Objective.Kind))
            def.Objective = new CombatObjectiveDef
            {
                Kind = d.Objective.Kind,
                Rounds = d.Objective.Rounds,
                WardUnitId = d.Objective.WardUnitId ?? "",
                Description = d.Objective.Description ?? "",
            };
        if (d.Waves != null)
            foreach (var w in d.Waves)
            {
                if (w == null)
                    continue;
                var wave = new ReinforcementWave { Round = w.Round, Announce = w.Announce ?? "" };
                if (w.Enemies != null)
                    foreach (var slot in w.Enemies)
                        wave.Enemies.Add(Slot(slot));
                def.Waves.Add(wave);
            }
        return def;
    }

    private static EnemySlot Slot(PrologueEnemySlot s)
    {
        string id = s?.UnitId ?? "";
        if (UnitRegistry.TryResolveId(id, out var resolved) && !string.IsNullOrEmpty(resolved))
            id = resolved;
        return new EnemySlot(id, s?.DifficultyMult ?? 1f);
    }

    private static CardSchool? ParseSchool(string school) =>
        Enum.TryParse<CardSchool>(school ?? "", true, out var s) ? s : (CardSchool?)null;

    // ════════════════════════════════════════════════════════════════════════
    //  Results and verdicts
    // ════════════════════════════════════════════════════════════════════════

    private static bool PartOneDone(GuildSaveData save) =>
        HasMeta(save, PartOneDoneFlag) || save.HasFlag("prologue_trial_part_one_waived");

    private static string PartOneResult(GuildSaveData save)
    {
        foreach (var r in new[] { "clean", "win", "floor" })
            if (HasMeta(save, ResultPartOnePrefix + r))
                return r;
        return "";
    }

    /// <summary>A practical's result from its return: "pass" when its pass unit fell, "time" when
    /// the fight ended on its rounds or the candidate yielded (the bell; a practical has had no
    /// round cap since 2026-10-27, so a yield is the usual way to it), "floor" when the wizard
    /// went down.</summary>
    private static string PracticalOutcome(PrologueFightScript script, bool won, ICollection<string> fallen,
                                           bool yielded = false)
    {
        bool passed = !string.IsNullOrEmpty(script?.PassUnit) && fallen != null && fallen.Contains(script.PassUnit);
        return passed ? "pass" : won || yielded ? "time" : "floor";
    }

    /// <summary>A declaration exam's result (DisciplineExam), by the practical's own rule.</summary>
    public static string ExamOutcome(string school, bool won, ICollection<string> fallen, bool yielded = false) =>
        PracticalOutcome(Fight(PracticalFightPrefix + SchoolKey(school))?.Script, won, fallen, yielded);

    private static string PracticalResult(GuildSaveData save)
    {
        foreach (var r in new[] { "pass", "time", "floor" })
            if (HasMeta(save, ResultPracticalPrefix + r))
                return r;
        return "";
    }

    /// <summary>The duel's verdict panel for a result, from its script (onWinClean, onWin, onLoss).</summary>
    private static string PartOneVerdict(string result)
    {
        var s = Fight(TrialFightId)?.Script;
        return result switch
        {
            "clean" => NonEmpty(s?.OnWinClean, "prologue_trial_part_one_clean"),
            "win" => NonEmpty(s?.OnWin, "prologue_trial_part_one"),
            _ => NonEmpty(s?.OnLoss, "prologue_trial_part_one_floor"),
        };
    }

    /// <summary>The practical's verdict panel for a result, from its script (onPass, onTime, onLoss).</summary>
    private static string PracticalVerdict(GuildSaveData save, string result) =>
        PracticalVerdictFor(Fight(PracticalFightId(save))?.Script, result);

    private static string PracticalVerdictFor(PrologueFightScript s, string result) => result switch
    {
        "pass" => NonEmpty(s?.OnPass, "prologue_practical_after_pass"),
        "time" => NonEmpty(s?.OnTime, "prologue_practical_after_time"),
        _ => NonEmpty(s?.OnLoss, "prologue_practical_after_floor"),
    };

    private static bool IsPartOneVerdict(string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        return id == PartOneVerdict("clean") || id == PartOneVerdict("win") || id == PartOneVerdict("floor");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Tokens (section 5)
    // ════════════════════════════════════════════════════════════════════════

    private static string Token(GuildSaveData save, string name)
    {
        switch (name)
        {
            case "traitor_line":
                return NonEmpty(ArchmageRegistry.Get(save.Ledger?.CoConspiratorId ?? "")?.IntroBetrayalLine,
                                TraitorLineFallback);
            case "stated_intention_phrase":
                return $"Stated intention: {IntentionDisplay(save)}.";
            case "intention_on_file":
                return HasMeta(save, "stated_intention_unstated") ? "left blank" : IntentionDisplay(save);
            case "intention_remark":
                return IntentionRemark(save);
            case "practical_remark":
                return PracticalRemark(save);
            case "practical_name":
                return PracticalName(save);
        }
        // wizard_name, guild_name, traitor_full, traitor_short: the chronicle's globals.
        return ChronicleScheduler.Token(name);
    }

    /// <summary>The stated intention as the Margin writes it: a school's name, or "none given".</summary>
    private static string IntentionDisplay(GuildSaveData save)
    {
        if (HasMeta(save, "stated_intention_unstated"))
            return "none given";
        foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
            if (HasMeta(save, $"stated_intention_{s.ToString().ToLowerInvariant()}"))
                return s.ToString();
        return "none given";
    }

    /// <summary>{intention_remark}: the Examiner's answer to this save's intention, taken from the
    /// asking panel's choice that sets the same stated_intention_* flag, so the question and the
    /// read-back share one set of lines.</summary>
    private static string IntentionRemark(GuildSaveData save)
    {
        string key = HasMeta(save, "stated_intention_unstated") ? "unstated" : "";
        if (key.Length == 0)
            foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
            {
                string k = s.ToString().ToLowerInvariant();
                if (HasMeta(save, $"stated_intention_{k}"))
                {
                    key = k;
                    break;
                }
            }
        return NonEmpty(RemarkForIntention(key), IntentionRemarkFallback);
    }

    /// <summary>The asking panel's answer for one intention key, or null.</summary>
    private static string RemarkForIntention(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        string flag = $"stated_intention_{key}";
        var asking = RawPanel(IntentionPanelId);
        if (asking?.Choices != null)
            foreach (var c in asking.Choices)
                if (c?.SetMetaFlags != null && c.SetMetaFlags.Contains(flag))
                    return c.ResultText;
        return null;
    }

    /// <summary>{practical_remark}: the practical's remarkPass or remarkTime for this save's result.</summary>
    private static string PracticalRemark(GuildSaveData save)
    {
        var s = Fight(PracticalFightId(save))?.Script;
        string r = PracticalResult(save);
        string remark = r == "pass" ? s?.RemarkPass : r == "time" ? s?.RemarkTime : null;
        return NonEmpty(remark, RemarkFallback);
    }

    /// <summary>A copy of the panel with every piece of text filled; extra meta flags ride on
    /// every choice. The cached panel is never mutated. A token with no value stays literal,
    /// so a gap is visible (section 5).</summary>
    private static NarrativeEncounterData Render(NarrativeEncounterData panel, GuildSaveData save,
                                                 List<string> addMetaFlags)
    {
        string Fill(string text) => string.IsNullOrEmpty(text)
            ? text ?? ""
            : TokenRx.Replace(text, m => Token(save, m.Groups[1].Value) ?? m.Value);

        var clone = new NarrativeEncounterData
        {
            Id = panel.Id,
            Title = Fill(panel.Title),
            Body = Fill(panel.Body),
            TerrainTags = panel.TerrainTags != null ? new List<string>(panel.TerrainTags) : new List<string>(),
            RegionTags = panel.RegionTags != null ? new List<string>(panel.RegionTags) : new List<string>(),
            RequiredFlag = panel.RequiredFlag ?? "",
            ArchmageId = panel.ArchmageId ?? "",
        };
        if (panel.Choices != null)
            foreach (var c in panel.Choices)
            {
                if (c == null)
                    continue;
                var copy = c.Clone();
                copy.Label = Fill(c.Label);
                copy.ResultText = Fill(c.ResultText);
                if (addMetaFlags != null)
                    foreach (var flag in addMetaFlags)
                        if (!copy.SetMetaFlags.Contains(flag))
                            copy.SetMetaFlags.Add(flag);
                clone.Choices.Add(copy);
            }
        return clone;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Loading
    // ════════════════════════════════════════════════════════════════════════

    private static NarrativeEncounterData RawPanel(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(id) && _panels.TryGetValue(id, out var p) ? p : null;
    }

    private static PrologueFightEntry Fight(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(id) && _fights.TryGetValue(id, out var f) ? f : null;
    }

    private static void EnsureLoaded()
    {
        if (_panels != null)
            return;
        _panels = new Dictionary<string, NarrativeEncounterData>();
        _fights = new Dictionary<string, PrologueFightEntry>();

        var panels = ReadList<NarrativeEncounterData>(PanelFile);
        if (panels != null)
            foreach (var p in panels)
                if (p != null && !string.IsNullOrEmpty(p.Id) && !_panels.ContainsKey(p.Id))
                    _panels[p.Id] = p;

        var fights = ReadList<PrologueFightEntry>(FightFile);
        if (fights != null)
            foreach (var f in fights)
                if (f != null && !string.IsNullOrEmpty(f.Id) && !_fights.ContainsKey(f.Id))
                    _fights[f.Id] = f;

        GD.Print($"PrologueDirector: loaded {_panels.Count} panel(s) and {_fights.Count} fight(s).");
    }

    private static List<T> ReadList<T>(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            GD.PushError($"PrologueDirector: {path} is missing.");
            return null;
        }
        try
        {
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f == null)
                return null;
            return JsonSerializer.Deserialize<List<T>>(f.GetAsText(), JsonOptions);
        }
        catch (Exception e)
        {
            GD.PrintErr($"PrologueDirector: error loading {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>Testing hook: drop the loaded data so edited JSON reloads.</summary>
    public static void ClearCache()
    {
        _panels = null;
        _fights = null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Asserts (C4 item 9)
    // ════════════════════════════════════════════════════════════════════════

    private const int FolioSize = 10;
    private const int OpeningHand = 5;
    private const string TrialKeyPrefix = "trial.";
    private const string RegisterProbeKey = "combat.basics.hand";

    /// <summary>The profile round trip writes here, never to the real list, so the check can
    /// never offer a skip nobody earned.</summary>
    private const string AssertProfileFile = "user://prologue_completed.assert.txt";

    private static bool _asserted;

    /// <summary><see cref="AssertRoundTrip"/>, once per process. The hall calls it on the way in.</summary>
    public static void AssertRoundTripOnce()
    {
        if (_asserted)
            return;
        _asserted = true;
        AssertRoundTrip();
    }

    /// <summary>C4 item 9: the prologue's round-trip checks, printed as [Prologue Assert] lines
    /// (errors pushed on failure). Nothing here shows a bark, marks one seen or touches the real
    /// profile file.
    /// <list type="number">
    /// <item>Every panel and fight the director names is present, every school the intention
    /// can state has its practical, and every unit the fights name resolves.</item>
    /// <item>Every practical's folio, dealt by the fight's own builder, is 10 cards with an
    /// opening hand equal to its innate list.</item>
    /// <item>The profile file writes, reads and matches (on a scratch file beside it).</item>
    /// <item>In the trial, combat.basics.hand fires its trial minute, and showing that minute
    /// marks both seen; in the Sundering nothing fires; every trial minute shadows a generic
    /// bark and none trails off.</item>
    /// </list>
    /// Also wired to the campus debug panel as "Assert Prologue".</summary>
    public static bool AssertRoundTrip()
    {
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");   // idempotent; the bootstrap has usually run it
        EnsureLoaded();
        var fails = new List<string>();

        // 1. The data the director names.
        foreach (var id in new[]
                 {
                     IntentionPanelId, IntentionReadPanelId, RulesPanelId, RulesWaivedPanelId, ApparatusPanelId, CommencementPanelId,
                     SunderingOpenPanelId, SunderingCastPanelId, LongSecondPanelId, MarginPanelId,
                     AnchorholdPanelId, DoorPanelId, LegiblePanelId, RecapPanelId,
                 })
            if (RawPanel(id) == null)
                fails.Add($"panel {id} is missing");
        foreach (var id in new[] { TrialFightId, SunderingFightId })
            if (Fight(id) == null)
                fails.Add($"fight {id} is missing");
        foreach (var r in new[] { "clean", "win", "floor" })
            if (RawPanel(PartOneVerdict(r)) == null)
                fails.Add($"part one's {r} verdict {PartOneVerdict(r)} is missing");
        string sunderingNext = NonEmpty(Fight(SunderingFightId)?.Script?.OnAnyOutcome, SunderingCastPanelId);
        if (RawPanel(sunderingNext) == null)
            fails.Add($"the Sundering's verdict {sunderingNext} is missing");

        foreach (var kv in _fights)
        {
            var enc = kv.Value?.Encounter;
            if (enc == null)
            {
                fails.Add($"fight {kv.Key} has no encounter");
                continue;
            }
            var slots = new List<PrologueEnemySlot>(enc.Enemies ?? new List<PrologueEnemySlot>());
            if (enc.Waves != null)
                foreach (var w in enc.Waves)
                    if (w?.Enemies != null)
                        slots.AddRange(w.Enemies);
            if ((enc.Enemies?.Count ?? 0) == 0)
                fails.Add($"fight {kv.Key} starts with no enemies");
            foreach (var slot in slots)
                if (!UnitRegistry.TryResolveId(slot?.UnitId ?? "", out _))
                    fails.Add($"fight {kv.Key}: unit '{slot?.UnitId}' does not resolve");
            string ward = enc.Objective?.WardUnitId ?? "";
            if (ward.Length > 0 && !UnitRegistry.TryResolveId(ward, out _))
                fails.Add($"fight {kv.Key}: ward '{ward}' does not resolve");
            if (!Enum.TryParse<EncounterTier>(enc.Tier ?? "", true, out _))
                fails.Add($"fight {kv.Key}: tier '{enc.Tier}' is not an EncounterTier");
            string pass = kv.Value.Script?.PassUnit ?? "";
            if (pass.Length > 0 && !slots.Exists(s => s?.UnitId == pass))
                fails.Add($"fight {kv.Key}: its pass unit '{pass}' is not one of its enemies");
        }

        // The schools the intention can state, each with its practical: the common one, which
        // every candidate sits (unstated included), and the seven the Grand Hall examines (R7e).
        var schools = new List<string>();
        var intention = RawPanel(IntentionPanelId);
        if (intention?.Choices != null)
            foreach (var c in intention.Choices)
                foreach (var flag in c?.SetMetaFlags ?? new List<string>())
                {
                    if (flag == null || flag == "stated_intention_set"
                        || !flag.StartsWith("stated_intention_", StringComparison.Ordinal))
                        continue;
                    string s = flag.Substring("stated_intention_".Length);
                    if (s == "unstated")
                        s = "adept";
                    if (!schools.Contains(s))
                        schools.Add(s);
                }
        int schoolCount = Enum.GetValues(typeof(CardSchool)).Length;
        if (schools.Count != schoolCount)
            fails.Add($"the intention panel states {schools.Count} practical(s), not one for each of the {schoolCount} schools");

        // The read-back answers every intention the founding screen can state with the asking
        // panel's own line.
        var keys = new List<string> { "unstated" };
        foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
            keys.Add(s.ToString().ToLowerInvariant());
        foreach (var key in keys)
            if (string.IsNullOrEmpty(RemarkForIntention(key)))
                fails.Add($"the read-back has no answer for intention '{key}' (no choice on {IntentionPanelId} sets stated_intention_{key})");

        // Checklists: every step is one the engine counts. A fight whose pass unit ends it wards
        // that unit, one ward per step (Tutorial.cs). None needs a round cap (ruled 2026-10-27):
        // each ends on its pass unit or its checklist, on the floor, or on a yield (PauseMenu).
        int checklists = 0;
        foreach (var kv in _fights)
        {
            var steps = kv.Value?.Script?.Steps;
            if (steps == null || steps.Count == 0)
                continue;
            checklists++;
            foreach (var step in steps)
            {
                if (step == null || Array.IndexOf(CombatManager.TutorialStepIds, step.Id ?? "") < 0)
                    fails.Add($"fight {kv.Key}: step '{step?.Id}' is not one the engine counts ({string.Join(", ", CombatManager.TutorialStepIds)})");
                else if (step.Count < 1 || string.IsNullOrEmpty(step.Label))
                    fails.Add($"fight {kv.Key}: step '{step.Id}' needs a label and a count of at least 1");
            }
        }

        // 2. Each practical: its panels, its school, and its folio as the fight deals it.
        int folios = 0;
        int examPages = 0;
        foreach (var school in schools)
        {
            string fightId = PracticalFightPrefix + school;
            var script = Fight(fightId)?.Script;
            if (script == null)
            {
                fails.Add($"fight {fightId} is missing");
                continue;
            }
            if (RawPanel(BriefPanelPrefix + school) == null)
                fails.Add($"panel {BriefPanelPrefix + school} is missing");
            foreach (var r in new[] { "pass", "time", "floor" })
                if (RawPanel(PracticalVerdictFor(script, r)) == null)
                    fails.Add($"{fightId}: its {r} verdict {PracticalVerdictFor(script, r)} is missing");
            var playedAs = ParseSchool(script.School);
            if (playedAs == null)
                fails.Add($"{fightId}: school '{script.School}' is not a CardSchool");
            if (string.IsNullOrEmpty(script.PassUnit) || !script.EndOnPassUnit)
                fails.Add($"{fightId}: no pass unit ends it, so it can only run out of time");
            // E2 and E3: a school practical is also its Grand Hall examination, sat on the hall's
            // map, and its two pages render from the teachers' lines with no token left literal.
            // The pages need a loaded save outside the prologue, so they are checked only then.
            if (school != CommonPracticalSchool)
            {
                MapRecipeRegistry.EnsureLoaded();
                string examMap = script.ExamMapRecipe ?? "";
                if (examMap.Length == 0 || MapRecipeRegistry.Get(examMap) == null)
                    fails.Add($"{fightId}: its examination map '{examMap}' (examMapRecipe) is not a recipe");
                var live = SaveManager.ActiveSave;
                if (live != null && string.IsNullOrEmpty(RegisterManager.PrologueMode))
                {
                    foreach (var (pageId, page, choices) in new[]
                             {
                                 (DisciplineExam.OpenPageId, DisciplineExam.OpeningPanel(live, school), 2),
                                 (DisciplineExam.MissedPageId, DisciplineExam.MissedPanel(live, school), 1),
                             })
                    {
                        if (page == null || page.Choices.Count < choices)
                            fails.Add($"{fightId}: its examination page {pageId} is missing or has fewer than {choices} choice(s)");
                        else if ((page.Title + "\n" + page.Body).Contains('{'))
                            fails.Add($"{fightId}: its examination page {pageId} leaves a token unfilled");
                        else
                            examPages++;
                    }
                }
            }

            var folio = script.Folio ?? new List<string>();
            var innate = script.Innate ?? new List<string>();
            var cards = CombatManager.BuildScriptedDeck(folio, innate);
            int dealt = cards?.Count ?? 0;
            if (folio.Count != FolioSize || dealt != FolioSize)
                fails.Add($"{fightId}: the folio lists {folio.Count} card(s) and deals {dealt}, not {FolioSize}");
            if (innate.Count != OpeningHand)
                fails.Add($"{fightId}: {innate.Count} innate card(s), not {OpeningHand}");
            if (cards == null)
                continue;
            var deck = new UnitDeckData(playedAs ?? CardSchool.Adept, OpeningHand);
            deck.Initialize(cards);
            var got = deck.Draw(innate.Count).ConvertAll(card => card?.BlueprintId ?? "");
            var want = new List<string>(innate);
            got.Sort(StringComparer.Ordinal);
            want.Sort(StringComparer.Ordinal);
            if (string.Join(",", got) != string.Join(",", want))
                fails.Add($"{fightId}: the opening hand is [{string.Join(", ", got)}], not its innate [{string.Join(", ", want)}]");
            else
                folios++;
        }

        // 3. The profile file: write, read and match.
        DeleteUserFile(AssertProfileFile);
        AppendProfileLine("prologue_practical_assert_a", AssertProfileFile);
        AppendProfileLine("prologue_practical_assert_b", AssertProfileFile);
        AppendProfileLine("prologue_practical_assert_a", AssertProfileFile);
        var back = ReadProfile(AssertProfileFile);
        if (back.Count != 2 || !back.Contains("prologue_practical_assert_a") || !back.Contains("prologue_practical_assert_b"))
            fails.Add($"the profile file read back {back.Count} id(s), not the 2 written");
        DeleteUserFile(AssertProfileFile);
        if (FileAccess.FileExists(AssertProfileFile))
            fails.Add($"the scratch file {AssertProfileFile} could not be removed");

        // 4. The Register's modes (section 4.1, P-4, P-5), resolved without showing anything.
        string trialProbe = TrialKeyPrefix + RegisterProbeKey;
        string inTrial = RegisterManager.ResolveForMode(RegisterProbeKey, "trial");
        if (inTrial != trialProbe)
            fails.Add($"in the trial, {RegisterProbeKey} fires {inTrial ?? "nothing"}, not {trialProbe}");
        if (RegisterManager.ResolveForMode(RegisterProbeKey, "sundering") != null
            || RegisterManager.ResolveForMode(trialProbe, "sundering") != null)
            fails.Add("in the Sundering, a key still fires");
        if (RegisterManager.ResolveForMode(RegisterProbeKey, "") != RegisterProbeKey)
            fails.Add($"outside the prologue, {RegisterProbeKey} does not fire as itself");
        var minutes = RegisterBarks.For(trialProbe);
        var generics = RegisterBarks.For(RegisterProbeKey);
        if (minutes.Count == 0 || generics.Count == 0)
            fails.Add($"barks.json lacks {RegisterProbeKey} or its trial minute");
        else
        {
            if (!minutes[0].Once)
                fails.Add($"{trialProbe} is not a once bark, so showing it would not mark it seen");
            if (!RegisterManager.GenericSeenIds(minutes[0]).Contains(generics[0].SeenId))
                fails.Add($"showing {trialProbe} would not mark {RegisterProbeKey} seen (P-4)");
        }
        int trialMinutes = 0;
        foreach (var bark in RegisterBarks.All)
        {
            if (bark?.Key == null || !bark.Key.StartsWith(TrialKeyPrefix, StringComparison.Ordinal))
                continue;
            trialMinutes++;
            if (RegisterBarks.For(bark.Key.Substring(TrialKeyPrefix.Length)).Count == 0)
                fails.Add($"{bark.Key} shadows no generic bark");
            foreach (var line in bark.Lines ?? new List<string>())
                if (line != null && (line.Contains("...") || line.Contains('…')))
                    fails.Add($"{bark.Key} trails off; a trial minute writes complete sentences");
        }

        foreach (var f in fails)
            GD.PushError($"[Prologue Assert] FAIL: {f}");
        if (fails.Count == 0)
            GD.Print($"[Prologue Assert] PASS: {_panels.Count} panels, {_fights.Count} fights, {folios} folios, "
                     + $"{checklists} checklist(s), {examPages} exam page(s), {trialMinutes} trial minutes, "
                     + "the profile file and the Register's modes.");
        return fails.Count == 0;
    }

    private static void DeleteUserFile(string path)
    {
        if (FileAccess.FileExists(path))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
    }

    // ── Small helpers ────────────────────────────────────────────────────

    private static bool HasMeta(GuildSaveData save, string flag) =>
        !string.IsNullOrEmpty(flag) && (save?.Ledger?.MetaNarrativeFlags?.Contains(flag) ?? false);

    private static void SetMeta(GuildSaveData save, string flag)
    {
        if (string.IsNullOrEmpty(flag) || save?.Ledger == null)
            return;
        save.Ledger.MetaNarrativeFlags ??= new List<string>();
        if (!save.Ledger.MetaNarrativeFlags.Contains(flag))
            save.Ledger.MetaNarrativeFlags.Add(flag);
    }

    private static string NonEmpty(string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;
}
