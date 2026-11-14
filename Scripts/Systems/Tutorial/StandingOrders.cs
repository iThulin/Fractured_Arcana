using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// StandingOrders.cs
//
// Purpose:        The quest Standing Orders (Standing Orders O1b,
//                 docs/standing_orders_o1_interval_rules_v1.md section
//                 6; docs/standing_orders_first_reading_v1.md sections
//                 4 and 7): the interval's five chapters as five ordered
//                 steps, walked while the interval is. A fire,
//                 provisions, a quorum, a doorway, standing orders.
//
//                 It is the first lunation's machine (FirstLunation.cs),
//                 copied with its own counters (interval:) and ticks
//                 (interval_step_), so the first lunation's debug forget
//                 cannot touch them (D153).
//
//                 A step is done when its tick is on the save (Tick, at
//                 the verb's own site, from O2 on), or when the save's
//                 own state says so: the interval begun (the fire, since
//                 the gate's "Go out." begins it), the door set, the
//                 orders adopted. Settle writes those ticks as it sees
//                 them, so a step stays done after the reading ends.
//
//                 While the interval walks, the top bar's Next line
//                 (HudManager) names the first step not yet done, and
//                 the first lunation's line yields. Clicking it picks
//                 the castle and says where the step's place lies
//                 (StrategicView.Tutorial.cs).
//
//                 Walked to the end, the quest is stamped and the finish
//                 is noted on this computer, so a later guild is offered
//                 the skip (U17; the gate that offers it is O2's).
// Layer:          System (static)
// Collaborators:  Data/Quests/standing_orders.json (the steps' words,
//                 in order), Interval.cs (the walk and the door),
//                 QuestTracker (the interval: counters), QuestLogView
//                 (the category), HudManager (the Next line),
//                 StrategicView.Tutorial.cs (where the line goes),
//                 IntervalSaveAssert (runs the assert),
//                 CampusGuildPanel (the debug buttons)
// ============================================================

/// <summary>The interval's quest: its steps, their state and the Next line (see the file header).</summary>
public static class StandingOrders
{
    public const string QuestId = "q_standing_orders";

    /// <summary>The quest's category in the log: first while the interval walks, last once it is done.</summary>
    public const string Category = "Standing Orders";

    /// <summary>The quest log's counter family: "interval:&lt;step&gt;", 1 once the step is done.</summary>
    public const string CounterPrefix = "interval:";

    /// <summary>A step's tick on the save: an Eternal flag, so it holds after the reading ends.</summary>
    private const string TickPrefix = "interval_step_";

    /// <summary>The skip (U17): every step counts done.</summary>
    public const string SkippedFlag = "interval_skipped";

    /// <summary>This save's finish is on the profile file, and the quest is stamped.</summary>
    private const string NotedFlag = "interval_noted";

    /// <summary>The founding's "So ordered." (O6): the orders are adopted.</summary>
    public const string AdoptedFlag = "standing_orders_adopted";

    /// <summary>The profile file the skip is offered from: one line once the interval has been walked to
    /// the end on this computer.</summary>
    public const string CompletedFile = "user://interval_completed.txt";
    private const string CompletedLine = "interval";
    private const string AssertFile = "user://interval_completed.assert.txt";

    // ── The steps, in the order they are walked (one per chapter) ────────
    public const string Hearth = "hearth";   // I, a fire
    public const string Store = "store";     // II, provisions
    public const string Quorum = "quorum";   // III, a quorum
    public const string Door = "door";       // IV, a doorway
    public const string Orders = "orders";   // V, standing orders

    private static readonly string[] Steps = { Hearth, Store, Quorum, Door, Orders };

    public static int StepCount => Steps.Length;

    public static bool IsStep(string step) => IndexOf(step) >= 0;

    /// <summary>The step's place in the order, from 0, or -1.</summary>
    public static int IndexOf(string step) => step == null ? -1 : System.Array.IndexOf(Steps, step);

    // ════════════════════════════════════════════════════════════════════════
    //  Where the save stands
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The quest is open once the interval has begun on this guild.</summary>
    public static bool Unlocked(GuildSaveData save) =>
        save?.Ledger != null && HasMeta(save, Interval.BegunFlag);

    public static bool Skipped(GuildSaveData save) => HasMeta(save, SkippedFlag);

    /// <summary>The step is done: ticked, skipped, the quest stamped complete, or the save's own state
    /// says so.</summary>
    public static bool Done(GuildSaveData save, string step)
    {
        if (save?.Ledger == null || !IsStep(step))
        {
            return false;
        }
        if (Skipped(save) || HasMeta(save, TickPrefix + step) || Stamped(save))
        {
            return true;
        }
        return SeenInState(save, step);
    }

    /// <summary>The quest log's counter (QuestTracker, "interval:&lt;step&gt;"): 1 once done.</summary>
    public static int Count(string step, GuildSaveData save) => Done(save, step) ? 1 : 0;

    /// <summary>Every step is done, by walking or by the skip.</summary>
    public static bool AllDone(GuildSaveData save)
    {
        foreach (var s in Steps)
        {
            if (!Done(save, s))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The first step not yet done, while the interval walks; otherwise null: not begun,
    /// skipped, not walking, or every step done.</summary>
    public static string Current(GuildSaveData save)
    {
        if (!Unlocked(save) || Skipped(save) || !Interval.Walking(save.Cycle))
        {
            return null;
        }
        foreach (var s in Steps)
        {
            if (!Done(save, s))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>Every step is done by walking it (not by the skip).</summary>
    public static bool Finished(GuildSaveData save) => Unlocked(save) && !Skipped(save) && AllDone(save);

    /// <summary>The quest log shows the quest while the interval walks and once it is walked to the end
    /// (D161). Not before the interval begins, once it is skipped, or when the interval ended part way
    /// (the debug panel's End).</summary>
    public static bool ShownInLog(GuildSaveData save) =>
        Unlocked(save) && !Skipped(save) && (Interval.Walking(save.Cycle) || AllDone(save));

    private static bool Stamped(GuildSaveData save) =>
        save?.Ledger?.CompletedQuestIds?.Contains(QuestId) ?? false;

    /// <summary>The state that already says a step was done, for the steps that have it (D158).</summary>
    private static bool SeenInState(GuildSaveData save, string step)
    {
        switch (step)
        {
            case Hearth:
                return Unlocked(save);   // the gate's "Go out." begins the interval, with the castle lit
            case Door:
                return save.Cycle?.Interval?.DoorSet ?? false;
            case Orders:
                return HasMeta(save, AdoptedFlag);
        }
        return false;   // the store and the quorum: their chapters tick them at their own sites (O3, O4)
    }

    /// <summary>The step's words: its objective's text in the quest file, or the id.</summary>
    public static string Label(string step)
    {
        var quest = Quest();
        if (quest?.Objectives != null)
        {
            foreach (var o in quest.Objectives)
            {
                if (o != null && o.Counter == CounterPrefix + step && !string.IsNullOrEmpty(o.Text))
                {
                    return o.Text;
                }
            }
        }
        return step ?? "";
    }

    private static QuestDefinition Quest()
    {
        foreach (var q in QuestLoader.LoadAll())
        {
            if (q != null && q.Id == QuestId)
            {
                return q;
            }
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Ticks
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The verb behind <paramref name="step"/> was just done, at its own site (the chapters, O2
    /// to O6). Written only while the interval walks.</summary>
    public static void Tick(string step) => Tick(SaveManager.ActiveSave, step);

    /// <summary>As <see cref="Tick(string)"/>, on a given save: the store's provisions (O3) and their
    /// assert on a scratch save.</summary>
    public static void Tick(GuildSaveData save, string step)
    {
        int at = IndexOf(step);
        if (at < 0 || save == null || !Unlocked(save) || Skipped(save) || !Interval.Walking(save.Cycle)
            || HasMeta(save, TickPrefix + step))
        {
            return;
        }
        SetMeta(save, TickPrefix + step);
        SaveManager.MarkDirty();
        GD.Print($"[StandingOrders] Step {at + 1} of {Steps.Length} done: {step}.");
    }

    /// <summary>Write the ticks the save's own state has earned (so they hold after the reading ends),
    /// and once every step is walked, stamp the quest and note the finish on this computer. Called by the
    /// top bar a couple of times a second.</summary>
    public static void Settle(GuildSaveData save)
    {
        if (!Unlocked(save) || Skipped(save))
        {
            return;
        }
        bool changed = false;
        foreach (var s in Steps)
        {
            if (HasMeta(save, TickPrefix + s) || !SeenInState(save, s))
            {
                continue;
            }
            SetMeta(save, TickPrefix + s);
            changed = true;
            GD.Print($"[StandingOrders] Step {IndexOf(s) + 1} of {Steps.Length} done: {s} (seen as it stands).");
        }
        if (!HasMeta(save, NotedFlag) && AllDone(save))
        {
            SetMeta(save, NotedFlag);
            changed = true;
            QuestTracker.SyncCompletions(save);
            if (!ReadLines(CompletedFile).Contains(CompletedLine))
            {
                AppendLine(CompletedFile, CompletedLine);
            }
            GD.Print("[StandingOrders] The interval's quest is walked to the end, and noted on this computer.");
        }
        if (changed)
        {
            SaveManager.MarkDirty();
        }
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
        {
            return null;
        }
        canGo = FirstLunation.CanGoFrom(scene);
        tooltip = $"Standing Orders, step {IndexOf(step) + 1} of {Steps.Length}."
                  + (canGo ? " Click to go there." : "");
        return $"Next: {Label(step)}";
    }

    /// <summary>The Next line is clicked: go to the current step. On the hub the step is handed to it;
    /// from a screen that can leave, the hub comes up first and takes the step at its first quiet
    /// moment. While the door is shut, the hub rises from the castle rather than the city.</summary>
    public static void Go(SceneTree tree)
    {
        string step = Current(SaveManager.ActiveSave);
        var scene = tree?.CurrentScene;
        if (step == null || scene == null || !FirstLunation.CanGoFrom(scene))
        {
            return;
        }
        if (scene is StrategicView hub)
        {
            hub.StandingOrdersGo(step);
            return;
        }
        PlayerSession.StartInCityOnOpen = true;
        _pendingGo = step;
        tree.ChangeSceneToFile(FirstLunation.StrategicScenePath);
    }

    /// <summary>The step the Next line asked for from another screen, once, for the hub.</summary>
    public static bool TakePendingGo(out string step)
    {
        step = _pendingGo;
        _pendingGo = null;
        return !string.IsNullOrEmpty(step) && step == Current(SaveManager.ActiveSave);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The skip (U17, offered at the gate from O2 on)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The gate offers the skip: this guild has not ended or skipped the interval, and the
    /// interval has been walked to the end on this computer before.</summary>
    public static bool SkipOffered(GuildSaveData save) =>
        save?.Ledger != null && !Skipped(save) && !HasMeta(save, Interval.DoneFlag)
        && ReadLines(CompletedFile).Contains(CompletedLine);

    /// <summary>Every step counts done, and the interval is over for this guild: ended where it walks (the
    /// door set, the castle home, the moon running), or marked over where it has not begun. The
    /// founding's end state is O6's to add (D162).</summary>
    public static void Skip(GuildSaveData save)
    {
        if (save?.Ledger == null)
        {
            return;
        }
        SetMeta(save, SkippedFlag);
        if (Interval.Walking(save.Cycle))
        {
            Interval.End(save);
        }
        else
        {
            SetMeta(save, Interval.DoneFlag);
        }
        SaveManager.MarkDirty();
        GD.Print("[StandingOrders] Skipped: every step counts done, and the interval is over for this guild.");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Debug (the campus debug panel, D165)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Take back this guild's ticks, its skip, its stamp, its note and the adopted orders, so the
    /// quest is walked again with the interval. The profile file stays as it is. Returns how many flags
    /// went.</summary>
    public static int DebugForget(GuildSaveData save)
    {
        var flags = save?.Ledger?.MetaNarrativeFlags;
        if (flags == null)
        {
            return 0;
        }
        int n = flags.RemoveAll(f => f != null && (f.StartsWith(TickPrefix, StringComparison.Ordinal)
                                                   || f == SkippedFlag || f == NotedFlag || f == AdoptedFlag
                                                   || f == IntervalStore.ItineraryFlag));
        save.Ledger.CompletedQuestIds?.Remove(QuestId);
        SaveManager.MarkDirty();
        GD.Print($"[StandingOrders] Debug: {n} flag(s) taken back. The quest is walked again with the interval.");
        return n;
    }

    /// <summary>Do the current step as its chapter will (the chapters are O2 to O6): the store is taken
    /// back and the castle provisioned (O3), the quorum is ticked, the door is set at {home}, the orders
    /// are adopted. Returns the step, or null when there is none (the interval not walking, or every
    /// step done).</summary>
    public static string DebugAdvance(GuildSaveData save)
    {
        string step = Current(save);
        if (step == null)
        {
            GD.Print("[StandingOrders] Debug: there is no step to do. Walk the interval first.");
            return null;
        }
        switch (step)
        {
            case Store:
                IntervalStore.ProvisionByDebug(save);   // O3 (D196): the store taken back, the castle provisioned
                break;
            case Door:
                Interval.SetDoor(save);
                break;
            case Orders:
                SetMeta(save, AdoptedFlag);
                break;
            default:
                SetMeta(save, TickPrefix + step);
                break;
        }
        SaveManager.MarkDirty();
        GD.Print($"[StandingOrders] Debug: step {IndexOf(step) + 1} of {Steps.Length} ({step}) done, as its chapter will do it.");
        Settle(save);
        return step;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The assert
    // ════════════════════════════════════════════════════════════════════════

    private static bool _asserted;

    /// <summary><see cref="AssertFailures"/>, once per process, printed as [Standing Orders Assert] lines.
    /// The hub calls it on the way in; Assert Round-Trips runs the same checks.</summary>
    public static void AssertOnce()
    {
        if (_asserted)
        {
            return;
        }
        _asserted = true;
        var fails = AssertFailures();
        foreach (var f in fails)
        {
            GD.PushError($"[Standing Orders Assert] FAIL: {f}");
        }
        if (fails.Count == 0)
        {
            GD.Print($"[Standing Orders Assert] PASS: {Steps.Length} steps, the quest, the counters on scratch "
                     + "saves, the first moon's yield and the profile file.");
        }
    }

    /// <summary>The quest's checks. Nothing here touches the real profile file or the active save.
    /// <list type="number">
    /// <item>The quest is there, Eternal, in its category, opened by the interval's start, and its
    /// objectives are the steps in order, each an interval: counter with words.</item>
    /// <item>On scratch saves: a save that has not begun walks nothing; a walking save starts at the store
    /// (the fire is its start); a tick, the door and the orders each move it on; every step finishes it;
    /// the skip counts every step done; a save whose interval ended part way has no step and is not
    /// shown; and the quest log's counters read the same.</item>
    /// <item>The first lunation's Next line yields while the interval walks, and only then.</item>
    /// <item>The profile file writes and reads (on a scratch file beside it).</item>
    /// </list></summary>
    public static List<string> AssertFailures()
    {
        var fails = new List<string>();

        // 1. The quest.
        var quest = Quest();
        if (quest == null)
        {
            fails.Add($"the quest {QuestId} is missing (Data/Quests/standing_orders.json)");
        }
        else
        {
            if (!quest.Permanent || quest.EffectiveLayer != "Eternal")
            {
                fails.Add($"{QuestId} is not a permanent, Eternal quest, so it would come back every reading");
            }
            if (quest.Category != Category)
            {
                fails.Add($"{QuestId} is in category '{quest.Category}', not '{Category}'");
            }
            if (quest.RequiredFlag != Interval.BegunFlag)
            {
                fails.Add($"{QuestId} unlocks on '{quest.RequiredFlag}', not the interval's start ({Interval.BegunFlag})");
            }
            var objectives = quest.Objectives ?? new List<QuestObjective>();
            if (objectives.Count != Steps.Length)
            {
                fails.Add($"{QuestId} has {objectives.Count} objective(s), not the {Steps.Length} steps");
            }
            for (int i = 0; i < Math.Min(objectives.Count, Steps.Length); i++)
            {
                var o = objectives[i];
                if (o?.Counter != CounterPrefix + Steps[i])
                {
                    fails.Add($"{QuestId}: objective {i + 1} counts '{o?.Counter}', not {CounterPrefix}{Steps[i]}");
                }
                else if (string.IsNullOrEmpty(o.Text))
                {
                    fails.Add($"{QuestId}: objective {i + 1} ({Steps[i]}) has no words");
                }
                if (o != null && (!string.IsNullOrEmpty(o.Flag) || !string.IsNullOrEmpty(o.Lore)))
                {
                    fails.Add($"{QuestId}: objective {i + 1} sets a flag or lore, which would win over its counter");
                }
            }
        }

        // 2. The counters, on scratch saves that are never saved.
        GuildSaveData Probe(bool walking, params string[] flags)
        {
            var probe = new GuildSaveData();
            foreach (var flag in flags)
            {
                SetMeta(probe, flag);
            }
            probe.Cycle.Interval.Active = walking;
            return probe;
        }
        string begun = Interval.BegunFlag;
        var before = Probe(false);
        if (Unlocked(before) || Current(before) != null || Done(before, Hearth) || ShownInLog(before))
        {
            fails.Add("a save that has not begun the interval walks its quest");
        }
        var fresh = Probe(true, begun);
        if (Current(fresh) != Store)
        {
            fails.Add($"a save walking the interval starts at '{Current(fresh)}', not {Store} (the fire is its start)");
        }
        if (!ShownInLog(fresh))
        {
            fails.Add("a save walking the interval does not show its quest in the log");
        }
        var stored = Probe(true, begun, TickPrefix + Store);
        if (Current(stored) != Quorum)
        {
            fails.Add($"a save that ticked {Store} is at '{Current(stored)}', not {Quorum}");
        }
        var doored = Probe(true, begun, TickPrefix + Store, TickPrefix + Quorum);
        doored.Cycle.Interval.DoorSet = true;
        if (Current(doored) != Orders)
        {
            fails.Add($"a save with the door set is at '{Current(doored)}', not {Orders}");
        }
        var all = Probe(true, begun, TickPrefix + Store, TickPrefix + Quorum, AdoptedFlag);
        all.Cycle.Interval.DoorSet = true;
        if (Current(all) != null || !Finished(all) || !ShownInLog(all))
        {
            fails.Add($"a save with every step done is at '{Current(all)}', not finished and shown");
        }
        var skipped = Probe(true, begun, SkippedFlag);
        if (Current(skipped) != null || Finished(skipped) || ShownInLog(skipped))
        {
            fails.Add("a skipped save still has a step, reads as walked, or shows in the log");
        }
        foreach (var s in Steps)
        {
            if (!Done(skipped, s))
            {
                fails.Add($"a skipped save has {s} not done");
            }
        }
        var ended = Probe(false, begun, TickPrefix + Store);
        if (Current(ended) != null || ShownInLog(ended))
        {
            fails.Add("a save whose interval ended part way still has a step, or shows in the log");
        }
        if (quest?.Objectives != null)
        {
            foreach (var o in quest.Objectives)
            {
                if (o == null || string.IsNullOrEmpty(o.Counter))
                {
                    continue;
                }
                var (have, need) = QuestTracker.CounterProgress(o, all);
                var (none, _) = QuestTracker.CounterProgress(o, fresh);
                int expectFresh = o.Counter == CounterPrefix + Hearth ? 1 : 0;
                if (need != 1 || have != 1 || none != expectFresh)
                {
                    fails.Add($"the quest log reads {o.Counter} as {none}/{need} on a fresh walk and {have}/{need} "
                              + $"when done, not {expectFresh}/1 and 1/1");
                }
            }
        }

        // 3. The first lunation's Next line yields while the interval walks, and only then.
        string founded = PrologueDirector.FoundingDoneFlag;
        if (FirstLunation.NextLine(Probe(true, founded, begun), null, out _, out _) != null)
        {
            fails.Add("the first moon's Next line shows while the interval walks");
        }
        if (FirstLunation.NextLine(Probe(false, founded), null, out _, out _) == null)
        {
            fails.Add("the first moon's Next line is gone with no interval walking");
        }

        // 4. The profile file: write, read and match.
        DeleteUserFile(AssertFile);
        AppendLine(AssertFile, CompletedLine);
        AppendLine(AssertFile, CompletedLine);
        var back = ReadLines(AssertFile);
        if (back.Count != 1 || !back.Contains(CompletedLine))
        {
            fails.Add($"the profile file read back {back.Count} line(s), not the 1 written");
        }
        DeleteUserFile(AssertFile);
        if (FileAccess.FileExists(AssertFile))
        {
            fails.Add($"the scratch file {AssertFile} could not be removed");
        }
        return fails;
    }

    // ── Small helpers ────────────────────────────────────────────────────

    private static bool HasMeta(GuildSaveData save, string flag) =>
        !string.IsNullOrEmpty(flag) && (save?.Ledger?.MetaNarrativeFlags?.Contains(flag) ?? false);

    private static void SetMeta(GuildSaveData save, string flag)
    {
        if (string.IsNullOrEmpty(flag) || save?.Ledger == null)
        {
            return;
        }
        save.Ledger.MetaNarrativeFlags ??= new List<string>();
        if (!save.Ledger.MetaNarrativeFlags.Contains(flag))
        {
            save.Ledger.MetaNarrativeFlags.Add(flag);
        }
    }

    private static HashSet<string> ReadLines(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!FileAccess.FileExists(path))
        {
            return set;
        }
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            return set;
        }
        while (!f.EofReached())
        {
            string line = f.GetLine().Trim();
            if (line.Length > 0)
            {
                set.Add(line);
            }
        }
        return set;
    }

    private static void AppendLine(string path, string line)
    {
        if (ReadLines(path).Contains(line))
        {
            return;
        }
        using var f = FileAccess.FileExists(path)
            ? FileAccess.Open(path, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            return;
        }
        f.SeekEnd();
        f.StoreLine(line);
    }

    private static void DeleteUserFile(string path)
    {
        if (FileAccess.FileExists(path))
        {
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }
    }
}
