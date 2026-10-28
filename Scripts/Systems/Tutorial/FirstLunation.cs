using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// FirstLunation.cs
//
// Purpose:        The first lunation, the strategic tutorial (S5,
//                 docs/sundering_long_second_founding_v1.md sections 7
//                 and 18). A guided quest of ten ordered steps, walked
//                 from the founding until every step is done: the ledger,
//                 the forces, the Armory, the cards, the world, a sortie,
//                 a won fight, the way home, the new moon and the quest
//                 log.
//
//                 Each step is a counter in the quest log's tutorial:
//                 family, read here. A step is done when its tick is on
//                 the save (Tick, written at the verb's own site), or when
//                 the save's own state already says so: a sortie launched
//                 or ended, an expedition fight won, a new moon passed.
//                 Settle writes those ticks as it sees them, so a step
//                 stays done in every reading after. The Register explains
//                 each step once, the first time it is done.
//
//                 The top bar's Next line (HudManager) names the first step
//                 not yet done. Clicking it goes there: Go hands the step to
//                 the hub (StrategicView.Tutorial.cs), or brings the hub up
//                 first from a screen that can leave.
//
//                 A guild founded after the first lunation has been finished
//                 on this computer is offered a skip at the founding
//                 (FoundingStage), like the prologue's own.
// Layer:          System (static)
// Collaborators:  Data/Quests/first_lunation.json (the steps' words, in
//                 order), QuestTracker (the tutorial: counters),
//                 QuestLogView (the category, shown while it is walked),
//                 HudManager (the Next line), StrategicView.Tutorial.cs
//                 (where the Next line goes), RegisterManager (the explain
//                 barks), FoundingStage (the skip), PrologueDirector
//                 (FoundingDoneFlag, and the assert)
// ============================================================

/// <summary>The first lunation's steps, their state and the Next line (see the file header).</summary>
public static class FirstLunation
{
    public const string QuestId = "q_first_lunation";
    /// <summary>The quest's category in the log: shown first, and only while it is being walked.</summary>
    public const string Category = "First Lunation";
    /// <summary>The quest log's counter family: "tutorial:&lt;step&gt;", 1 once the step is done.</summary>
    public const string CounterPrefix = "tutorial:";
    /// <summary>A step's tick on the save: a meta flag, so it holds in every reading after.</summary>
    private const string TickPrefix = "tutorial_";
    /// <summary>The founding's skip: every step counts done.</summary>
    public const string SkippedFlag = "tutorial_first_lunation_skipped";
    /// <summary>This save's finish is on the profile file, and the quest is stamped.</summary>
    private const string NotedFlag = "tutorial_first_lunation_noted";

    /// <summary>The profile file the skip is offered from: one line once the first lunation has been
    /// finished on this computer.</summary>
    public const string CompletedFile = "user://first_lunation_completed.txt";
    private const string CompletedLine = "first_lunation";
    private const string AssertFile = "user://first_lunation_completed.assert.txt";

    public const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    public const string DeckEditorScenePath = "res://Scenes/UI/DeckEditor.tscn";

    // ── The steps, in the order they are walked ──────────────────────────
    public const string Ledger = "ledger";
    public const string Forces = "forces";
    public const string Equip = "equip";
    public const string Deck = "deck";
    public const string World = "world";
    public const string Sortie = "sortie";
    public const string Fight = "fight";
    public const string Home = "home";
    public const string Moon = "moon";
    public const string Quests = "quests";

    /// <summary>One step: its id, the explain bark it raises when it is first done (empty when the
    /// verb's own site raises one already), and the bark that teaches it, for the assert.</summary>
    private readonly struct StepDef
    {
        public readonly string Id;
        public readonly string Bark;
        public readonly string TaughtBy;

        public StepDef(string id, string bark, string taughtBy)
        {
            Id = id;
            Bark = bark;
            TaughtBy = taughtBy;
        }
    }

    private static readonly StepDef[] Steps =
    {
        new(Ledger, "campus.first_ledger", "campus.first_ledger"),
        new(Forces, "strategic.first_forces", "strategic.first_forces"),
        new(Equip, "campus.first_armory", "campus.first_armory"),
        new(Deck, "campus.first_deck", "campus.first_deck"),
        new(World, "strategic.first_world", "strategic.first_world"),
        new(Sortie, "", "strategic.first_sortie"),       // Legible, on the first crossing
        new(Fight, "", "combat.deployment"),             // the fight's own explanations
        new(Home, "strategic.first_return", "strategic.first_return"),
        new(Moon, "", "strategic.first_lunation"),       // RunLunationTick raises it
        new(Quests, "strategic.first_quests", "strategic.first_quests"),
    };

    /// <summary>The explanations taught when first met rather than as steps (section 18.3): the eight
    /// new ones and the charter, then the six that were already in the book. For the assert.</summary>
    public static readonly string[] MetBarks =
    {
        "campus.first_charter", "campus.first_declaration", "campus.first_build", "campus.first_landmark",
        "strategic.first_march", "strategic.first_dive", "strategic.first_depot", "strategic.first_warfront",
        "strategic.first_city_services",
        "strategic.first_posting", "strategic.first_waystone", "strategic.first_council",
        "strategic.first_negotiation", "strategic.first_shard_zone", "cycle.first_unmaking",
    };

    /// <summary>Every step id, in order.</summary>
    public static IReadOnlyList<string> StepIds
    {
        get
        {
            var ids = new List<string>(Steps.Length);
            foreach (var s in Steps)
                ids.Add(s.Id);
            return ids;
        }
    }

    public static int StepCount => Steps.Length;

    public static bool IsStep(string step) => IndexOf(step) >= 0;

    /// <summary>The step's place in the order, from 0, or -1.</summary>
    public static int IndexOf(string step)
    {
        for (int i = 0; i < Steps.Length; i++)
            if (Steps[i].Id == step)
                return i;
        return -1;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Where the save stands
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The first lunation is walked by a guild founded in the story (S4's founding), and by
    /// no save from before it.</summary>
    public static bool Unlocked(GuildSaveData save) =>
        save?.Ledger != null && HasMeta(save, PrologueDirector.FoundingDoneFlag);

    public static bool Skipped(GuildSaveData save) => HasMeta(save, SkippedFlag);

    /// <summary>The step is done: ticked, skipped, the quest stamped complete, or the save's own state
    /// says so (a sortie launched or ended, an expedition fight won, a new moon passed).</summary>
    public static bool Done(GuildSaveData save, string step)
    {
        if (save?.Ledger == null || !IsStep(step))
            return false;
        if (Skipped(save) || HasMeta(save, TickPrefix + step)
            || (save.Ledger.CompletedQuestIds?.Contains(QuestId) ?? false))
            return true;
        return SeenInState(save, step);
    }

    /// <summary>The quest log's counter (QuestTracker, "tutorial:&lt;step&gt;"): 1 once done.</summary>
    public static int Count(string step, GuildSaveData save) => Done(save, step) ? 1 : 0;

    /// <summary>The first step not yet done, or null: locked, skipped, or every step done.</summary>
    public static string Current(GuildSaveData save)
    {
        if (!Unlocked(save) || Skipped(save))
            return null;
        foreach (var s in Steps)
            if (!Done(save, s.Id))
                return s.Id;
        return null;
    }

    /// <summary>Every step is done by walking it (not by the skip).</summary>
    public static bool Finished(GuildSaveData save) => Unlocked(save) && !Skipped(save) && Current(save) == null;

    /// <summary>The state that already says a step was done, for the steps that have it.</summary>
    private static bool SeenInState(GuildSaveData save, string step)
    {
        var cycle = save.Cycle;
        switch (step)
        {
            case Sortie:
                return (cycle?.TotalRuns ?? 0) > 0 || SortieOut(cycle);
            case Fight:
                return save.Ledger.DeedCounts != null
                       && save.Ledger.DeedCounts.TryGetValue("combat_won", out int won) && won > 0;
            case Home:
                return (cycle?.TotalRuns ?? 0) > 0;   // every way a sortie ends counts it
            case Moon:
                return cycle != null && (cycle.CycleNumber > 1 || (cycle.Calendar?.CurrentLunation ?? 1) > 1);
        }
        return false;
    }

    /// <summary>A force is in the field now: the castle or any party.</summary>
    private static bool SortieOut(CycleState cycle)
    {
        if (cycle == null)
            return false;
        if (cycle.CastleSortie != null && cycle.CastleSortie.Active)
            return true;
        if (cycle.FieldParties != null)
            foreach (var p in cycle.FieldParties)
                if (p?.Sortie != null && p.Sortie.Active)
                    return true;
        return false;
    }

    /// <summary>The step's words: its objective's text in the quest file, or the id.</summary>
    public static string Label(string step)
    {
        var quest = Quest();
        if (quest?.Objectives != null)
            foreach (var o in quest.Objectives)
                if (o != null && o.Counter == CounterPrefix + step && !string.IsNullOrEmpty(o.Text))
                    return o.Text;
        return step ?? "";
    }

    private static QuestDefinition Quest()
    {
        foreach (var q in QuestLoader.LoadAll())
            if (q != null && q.Id == QuestId)
                return q;
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Ticks
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The verb behind <paramref name="step"/> was just done, at its own site. With a guild
    /// loaded, the step's explanation is offered to the Register whatever the save (it shows once on
    /// this computer), and the tick is written while the save is walking the first lunation.</summary>
    public static void Tick(string step)
    {
        int at = IndexOf(step);
        var save = SaveManager.ActiveSave;
        if (at < 0 || save == null)
            return;   // no guild loaded (the campus's slot picker): nothing to explain yet
        if (Steps[at].Bark.Length > 0)
            RegisterManager.Fire(Steps[at].Bark);
        if (!Unlocked(save) || Skipped(save) || HasMeta(save, TickPrefix + step))
            return;
        SetMeta(save, TickPrefix + step);
        SaveManager.MarkDirty();
        GD.Print($"[FirstLunation] Step {at + 1} of {Steps.Length} done: {step}.");
    }

    /// <summary>A campus panel was shown (the city's floated panel, or the campus screen's tab): the
    /// Grand Hall's ledger and the quest log are steps.</summary>
    public static void NotePanelShown(CampusPanelId? panel)
    {
        if (panel == CampusPanelId.Guild)
            Tick(Ledger);
        else if (panel == CampusPanelId.Quests)
            Tick(Quests);
    }

    /// <summary>Write the ticks the save's own state has earned (so they hold in later readings), and
    /// the world step when <paramref name="scene"/> is the hub showing the world map (the founding opens
    /// it there); raise those steps' explanations as they are first seen; and once every step is walked,
    /// stamp the quest and note the finish on this computer. Called by the top bar a couple of times a
    /// second.</summary>
    public static void Settle(GuildSaveData save, Node scene = null)
    {
        if (!Unlocked(save) || Skipped(save))
            return;
        bool changed = false;
        foreach (var s in Steps)
        {
            if (HasMeta(save, TickPrefix + s.Id))
                continue;
            bool seen = s.Id == World
                ? scene is StrategicView hub && hub.OnWorldMap
                : SeenInState(save, s.Id);
            if (!seen)
                continue;
            SetMeta(save, TickPrefix + s.Id);
            changed = true;
            if (s.Bark.Length > 0)
                RegisterManager.Fire(s.Bark);
            GD.Print($"[FirstLunation] Step {IndexOf(s.Id) + 1} of {Steps.Length} done: {s.Id} (seen as it stands).");
        }
        if (!HasMeta(save, NotedFlag) && Current(save) == null)
        {
            SetMeta(save, NotedFlag);
            changed = true;
            QuestTracker.SyncCompletions(save);
            var profile = ReadLines(CompletedFile);
            if (!profile.Contains(CompletedLine))
                AppendLine(CompletedFile, CompletedLine);
            GD.Print("[FirstLunation] The first lunation is finished, and noted on this computer.");
        }
        if (changed)
            SaveManager.MarkDirty();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The Next line
    // ════════════════════════════════════════════════════════════════════════

    private static string _pendingGo;

    /// <summary>The top bar's line for the current step, or null when there is none: "Next: ...", its
    /// tooltip, and whether clicking it can go there from <paramref name="scene"/>.</summary>
    public static string NextLine(GuildSaveData save, Node scene, out string tooltip, out bool canGo)
    {
        tooltip = "";
        canGo = false;
        string step = Current(save);
        if (step == null)
            return null;
        canGo = CanGoFrom(scene);
        tooltip = $"The first lunation, step {IndexOf(step) + 1} of {Steps.Length}."
                  + (canGo ? " Click to go there." : "");
        return $"Next: {Label(step)}";
    }

    /// <summary>The Next line is clicked: go to the current step. On the hub the step is handed to it;
    /// from a screen that can leave (a card screen, the campus) the hub comes up first, in the city, and
    /// takes the step at its first quiet moment. The Sanctum is gone to directly.</summary>
    public static void Go(SceneTree tree)
    {
        string step = Current(SaveManager.ActiveSave);
        var scene = tree?.CurrentScene;
        if (step == null || scene == null || !CanGoFrom(scene))
            return;
        if (scene is StrategicView hub)
        {
            hub.FirstLunationGo(step);
            return;
        }
        PlayerSession.StartInCityOnOpen = true;
        if (step == Deck)
        {
            tree.ChangeSceneToFile(DeckEditorScenePath);
            return;
        }
        _pendingGo = step;
        tree.ChangeSceneToFile(StrategicScenePath);
    }

    /// <summary>The step the Next line asked for from another screen, once, for the hub.</summary>
    public static bool TakePendingGo(out string step)
    {
        step = _pendingGo;
        _pendingGo = null;
        return !string.IsNullOrEmpty(step) && step == Current(SaveManager.ActiveSave);
    }

    /// <summary>Clicking can go somewhere from here: the hub, or a screen that is safe to leave. Not a
    /// fight, an expedition, a negotiation or the prologue, which are left their own way.</summary>
    public static bool CanGoFrom(Node scene)
    {
        if (scene == null)
            return false;
        if (scene is StrategicView)
            return true;
        string lower = (scene.SceneFilePath ?? "").ToLowerInvariant();
        return lower.Length > 0
               && !lower.Contains("expedition") && !lower.Contains("battlefield") && !lower.Contains("combat")
               && !lower.Contains("negotiation") && !lower.Contains("prologue");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The skip (offered at the founding)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The founding offers the skip: this save is walking the first lunation and has steps
    /// left, and the first lunation has been finished on this computer before.</summary>
    public static bool SkipOffered(GuildSaveData save) =>
        Unlocked(save) && !Skipped(save) && Current(save) != null && ReadLines(CompletedFile).Contains(CompletedLine);

    /// <summary>Every step counts done for this save.</summary>
    public static void Skip(GuildSaveData save)
    {
        if (save?.Ledger == null)
            return;
        SetMeta(save, SkippedFlag);
        SaveManager.MarkDirty();
        GD.Print("[FirstLunation] Skipped: every step counts done.");
    }

    /// <summary>Debug (the campus debug panel): take this save's ticks, its skip and its stamp back, so
    /// the first lunation is walked again. The steps the save's own state shows (a sortie, a won fight,
    /// a new moon) tick again at once, and the Register's explanations stay seen on this computer.
    /// Returns how many flags went.</summary>
    public static int DebugForget(GuildSaveData save)
    {
        var flags = save?.Ledger?.MetaNarrativeFlags;
        if (flags == null)
            return 0;
        int n = flags.RemoveAll(f => f != null && f.StartsWith(TickPrefix, StringComparison.Ordinal));
        save.Ledger.CompletedQuestIds?.Remove(QuestId);
        SaveManager.MarkDirty();
        GD.Print($"[FirstLunation] Debug: {n} flag(s) taken back. The first lunation is walked again"
                 + (Unlocked(save) ? "." : ", once this save has been founded in the story."));
        return n;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The assert
    // ════════════════════════════════════════════════════════════════════════

    private static bool _asserted;

    /// <summary><see cref="AssertFailures"/>, once per process, printed as [First Lunation Assert] lines.
    /// The hub calls it on the way in; the prologue's assert runs the same checks.</summary>
    public static void AssertOnce()
    {
        if (_asserted)
            return;
        _asserted = true;
        var fails = AssertFailures();
        foreach (var f in fails)
            GD.PushError($"[First Lunation Assert] FAIL: {f}");
        if (fails.Count == 0)
            GD.Print($"[First Lunation Assert] PASS: {Steps.Length} steps, the quest, {Steps.Length + MetBarks.Length} "
                     + "explanations, the counters on scratch saves and the profile file.");
    }

    /// <summary>The first lunation's checks. Nothing here shows a bark, marks one seen or touches the
    /// real profile file or the active save.
    /// <list type="number">
    /// <item>The quest is there, Eternal, in its category, gated on the founding, and its objectives are
    /// the steps in order, each a tutorial: counter with words.</item>
    /// <item>Every explanation the steps and the first meetings raise is an explain bark shown once.</item>
    /// <item>On scratch saves: a founded save starts at the first step, a tick moves it on, every tick
    /// finishes it, the skip counts every step done, the save's own state counts where it should, a save
    /// from before the founding walks nothing, and the quest log's counters read the same.</item>
    /// <item>The profile file writes and reads (on a scratch file beside it).</item>
    /// </list></summary>
    public static List<string> AssertFailures()
    {
        var fails = new List<string>();

        // 1. The quest.
        var quest = Quest();
        if (quest == null)
        {
            fails.Add($"the quest {QuestId} is missing (Data/Quests/first_lunation.json)");
        }
        else
        {
            if (!quest.Permanent || quest.EffectiveLayer != "Eternal")
                fails.Add($"{QuestId} is not a permanent, Eternal quest, so it would come back every reading");
            if (quest.Category != Category)
                fails.Add($"{QuestId} is in category '{quest.Category}', not '{Category}'");
            if (quest.RequiredFlag != PrologueDirector.FoundingDoneFlag)
                fails.Add($"{QuestId} unlocks on '{quest.RequiredFlag}', not the founding ({PrologueDirector.FoundingDoneFlag})");
            var objectives = quest.Objectives ?? new List<QuestObjective>();
            if (objectives.Count != Steps.Length)
                fails.Add($"{QuestId} has {objectives.Count} objective(s), not the {Steps.Length} steps");
            for (int i = 0; i < Math.Min(objectives.Count, Steps.Length); i++)
            {
                var o = objectives[i];
                if (o?.Counter != CounterPrefix + Steps[i].Id)
                    fails.Add($"{QuestId}: objective {i + 1} counts '{o?.Counter}', not {CounterPrefix}{Steps[i].Id}");
                else if (string.IsNullOrEmpty(o.Text))
                    fails.Add($"{QuestId}: objective {i + 1} ({Steps[i].Id}) has no words");
                if (o != null && (!string.IsNullOrEmpty(o.Flag) || !string.IsNullOrEmpty(o.Lore)))
                    fails.Add($"{QuestId}: objective {i + 1} sets a flag or lore, which would win over its counter");
            }
        }

        // 2. The explanations.
        var keys = new List<string>();
        foreach (var s in Steps)
        {
            if (s.Bark.Length > 0 && s.Bark != s.TaughtBy)
                fails.Add($"step {s.Id} raises {s.Bark} but is taught by {s.TaughtBy}");
            keys.Add(s.TaughtBy);
        }
        keys.AddRange(MetBarks);
        foreach (var key in keys)
        {
            var barks = RegisterBarks.For(key);
            if (barks.Count == 0)
                fails.Add($"the explanation {key} is not in {RegisterBarks.DefaultPath}");
            else if (barks[0].Category != RegisterCategory.Explain || !barks[0].Once)
                fails.Add($"{key} is not an explain bark shown once");
            else if (string.IsNullOrEmpty(barks[0].Note) || barks[0].Lines == null || barks[0].Lines.Count == 0)
                fails.Add($"{key} has no line or no note");
        }

        // 3. The counters, on scratch saves that are never saved.
        GuildSaveData Probe(params string[] flags)
        {
            var probe = new GuildSaveData();
            foreach (var flag in flags)
                SetMeta(probe, flag);
            return probe;
        }
        string founded = PrologueDirector.FoundingDoneFlag;
        var fresh = Probe(founded);
        if (Current(fresh) != Steps[0].Id)
            fails.Add($"a founded save starts at '{Current(fresh)}', not {Steps[0].Id}");
        var ticked = Probe(founded, TickPrefix + Steps[0].Id);
        if (Current(ticked) != Steps[1].Id)
            fails.Add($"a save that ticked {Steps[0].Id} is at '{Current(ticked)}', not {Steps[1].Id}");
        var all = Probe(founded);
        foreach (var s in Steps)
            SetMeta(all, TickPrefix + s.Id);
        if (Current(all) != null || !Finished(all))
            fails.Add($"a save with every tick is at '{Current(all)}', not finished");
        var skipped = Probe(founded, SkippedFlag);
        if (Current(skipped) != null || Finished(skipped))
            fails.Add("a skipped save still has a step, or reads as walked");
        foreach (var s in Steps)
            if (!Done(skipped, s.Id))
                fails.Add($"a skipped save has {s.Id} not done");
        if (Current(Probe()) != null || Unlocked(Probe()))
            fails.Add("a save from before the founding walks the first lunation");
        var lived = Probe(founded);
        lived.Cycle.TotalRuns = 1;
        lived.Cycle.Calendar.CurrentLunation = 2;
        lived.Ledger.DeedCounts["combat_won"] = 1;
        foreach (var id in new[] { Sortie, Fight, Home, Moon })
            if (!Done(lived, id))
                fails.Add($"a save whose state shows {id} does not count it done");
        foreach (var id in new[] { Ledger, Forces, Equip, Deck, World, Quests })
            if (Done(lived, id))
                fails.Add($"{id} counts done from the save's state alone, but only its own site ticks it");
        if (quest?.Objectives != null)
            foreach (var o in quest.Objectives)
            {
                if (o == null || string.IsNullOrEmpty(o.Counter))
                    continue;
                var (have, need) = QuestTracker.CounterProgress(o, all);
                var (none, _) = QuestTracker.CounterProgress(o, fresh);
                if (need != 1 || have != 1 || none != 0)
                    fails.Add($"the quest log reads {o.Counter} as {none}/{need} fresh and {have}/{need} ticked, not 0/1 and 1/1");
            }

        // 4. The profile file: write, read and match.
        DeleteUserFile(AssertFile);
        AppendLine(AssertFile, CompletedLine);
        AppendLine(AssertFile, CompletedLine);
        var back = ReadLines(AssertFile);
        if (back.Count != 1 || !back.Contains(CompletedLine))
            fails.Add($"the profile file read back {back.Count} line(s), not the 1 written");
        DeleteUserFile(AssertFile);
        if (FileAccess.FileExists(AssertFile))
            fails.Add($"the scratch file {AssertFile} could not be removed");
        return fails;
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

    private static HashSet<string> ReadLines(string path)
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

    private static void AppendLine(string path, string line)
    {
        if (ReadLines(path).Contains(line))
            return;
        using var f = FileAccess.FileExists(path)
            ? FileAccess.Open(path, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
            return;
        f.SeekEnd();
        f.StoreLine(line);
    }

    private static void DeleteUserFile(string path)
    {
        if (FileAccess.FileExists(path))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
    }
}
