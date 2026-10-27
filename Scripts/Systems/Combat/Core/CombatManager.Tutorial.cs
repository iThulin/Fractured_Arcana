using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// CombatManager.Tutorial.cs
//
// Purpose:        A scripted fight's checklist (ruled 2026-10-26 for the
//                 prologue trial, 2026-10-27 for the practicals): steps for
//                 the player to do, each with a counter in the objective
//                 banner. The steps are data (a fight's "steps" in
//                 prologue_fights.json, read through
//                 PrologueDirector.StepsFor). A step's id is one the engine
//                 counts (TutorialStepIds):
//                   walk     the player moves a unit to another tile
//                   damage   damage lands on enemies: shield, armor and
//                            health (Unit.DamageLandedThisCombat)
//                   dodge    an enemy strike locked onto a player unit's
//                            tile lands on it empty, or is wasted, after
//                            that unit left it
//                   reflex   a Reflex half cast in a priority window
//                            during the enemy turn
//                   block    an enemy strike lands on a player unit's
//                            shield or armor
//                   negate   an enemy's action is cancelled outright
//                            (Counterspell)
//                   riposte  a player unit strikes back at an enemy that
//                            hit it (Riposte)
//                   mark     an arcane mark on an enemy pays out (Chalk
//                            Mark, then any spell that hits it)
//                   turn_spells
//                            the most spells cast in one player turn, as
//                            its progress (Count is the target)
//                 or a moment (TutorialMomentKeys): a key the fight raises
//                 through NoteMoment. Most are the keys the C4 pass already
//                 offers the Register at each school's verb
//                 (RegisterManager.Fire raises every key here first); a few
//                 are raised directly where no bark speaks.
//
//                 How a checklist ends depends on the fight:
//                 - With no pass unit (the trial), the fight is won once
//                   every step is done, at the first settled moment of the
//                   player's turn.
//                 - A fight that ends when a named unit falls (a practical:
//                   EncounterRouter.EndOnUnitFall) wards that unit instead
//                   (ruled 2026-10-27): its health bar splits into one even
//                   section per step, each ward keeps a section from taking
//                   damage (Unit.WardFloor), and each step done breaks one
//                   ward. When the last breaks, breaking the unit is the
//                   pass (NoteScriptedUnitFall).
//                 Neither has a round cap (ruled 2026-10-27): the fight
//                 also ends on the floor, or when the candidate yields
//                 (PauseMenu), which the director reads as the bell.
// Layer:          Combat / runtime
// Collaborators:  PrologueDirector (StepsFor), EncounterRouter
//                 (ScriptedFightId, SuppressRewards, EndOnUnitFall),
//                 CombatManager.cs (_Process, _ExitTree, TryMoveSelectedUnit,
//                 the cast tail, ResolveRetaliation),
//                 CombatManager.Objectives.cs (InitObjectiveState arms it,
//                 ObjectiveBannerText shows it, CheckCombatEnd declares
//                 it), CombatManager.EnemyIntents.cs (ResolveStrike, a
//                 wasted shot or release, the negate branch),
//                 CombatManager.Scripted.cs (NoteScriptedUnitFall passes
//                 the fight), Unit.cs (DamageLandedThisCombat, the wards),
//                 UnitNameplate.cs (the sections), GameState
//                 (MarksSpentOnEnemies), RegisterManager (Fire raises the
//                 moments; practical.warded and practical.released), the
//                 direct moment sites (ElementReactions.ApplyBrittle,
//                 AlmanacEntry.Fire, GlyphManager.OnGlyphFired,
//                 AdvanceAllSpiritsEffect, ThornlashEffect, ResolveStrike)
// ============================================================

public partial class CombatManager
{
    public const string TutorialStepWalk = "walk";
    public const string TutorialStepDamage = "damage";
    public const string TutorialStepDodge = "dodge";
    public const string TutorialStepReflex = "reflex";
    public const string TutorialStepBlock = "block";
    public const string TutorialStepNegate = "negate";
    public const string TutorialStepRiposte = "riposte";
    public const string TutorialStepMark = "mark";
    public const string TutorialStepTurnSpells = "turn_spells";

    /// <summary>The moments a checklist step can name, each a key raised through NoteMoment.
    /// The C4 keys are the ones the engine offers the Register at each school's verb; the
    /// ones marked "direct" have no bark and are raised where they happen.</summary>
    public static readonly string[] TutorialMomentKeys =
    {
        // Elementalist
        "reaction.brittle",            // frost and storm meet on one tile (ElementReactions)
        "elementalist.brittle_hit",    // direct: a hit on an enemy standing on Brittle
        "elementalist.burst",          // an element charged to four (ElementalAttunement)
        // Arcanist
        "arc.first_charge",            // Charge banked (ArcaneAttunement.Add)
        "arc.next_spell",              // a next-spell modifier queued (Spell Weave)
        // Chronomancer
        "chrono.lands",                // direct: a scheduled spell lands on an enemy (AlmanacEntry.Fire)
        "chrono.lookahead",            // an enemy's next move read (Read Ahead, Glimpse)
        "chrono.defer",                // an enemy attack deferred (Hold That Thought)
        // Enchanter
        "ench.glyph_sprung",           // direct: an enemy springs a player's glyph (GlyphManager)
        "ench.first_name",             // a Name written (Geas)
        "ench.name_broken",            // a Name paid
        "ench.compel",                 // a compelled walk
        // Necromancer
        "necro.first_memorial",        // a memorial left
        "necro.first_spirit",          // a spirit raised (Final Escort)
        "necro.spirit_strikes",        // direct: a player spirit strikes (The Procession)
        // Druid
        "druid.first_root",            // a unit rooted (Mire, Snare)
        "druid.thorn_grown",           // direct: a thornlash hit with growth under the enemy
        "druid.first_wildlife",        // wildlife called (Wolves Come)
        // Tinker
        "tinker.first_construct",      // a player construct placed
        "tinker.construct_fires",      // a construct acted on its own
        "tinker.construct_struck",     // direct: an enemy strike lands on a player construct
        "tinker.first_link",           // a Conduit Link made
    };

    /// <summary>Every step id the engine counts: its own, then the moments. A step with any
    /// other id is shown but never completes, so it is left out of the completion test and the
    /// ward count; the prologue's assert rejects it.</summary>
    public static readonly string[] TutorialStepIds = BuildTutorialStepIds();

    private static string[] BuildTutorialStepIds()
    {
        var ids = new List<string>
        {
            TutorialStepWalk, TutorialStepDamage, TutorialStepDodge, TutorialStepReflex,
            TutorialStepBlock, TutorialStepNegate, TutorialStepRiposte, TutorialStepMark,
            TutorialStepTurnSpells,
        };
        ids.AddRange(TutorialMomentKeys);
        return ids.ToArray();
    }

    private sealed class TutorialStep
    {
        public string Id = "";
        public string Label = "";
        public int Count = 1;
        public int Progress;
        public bool Known;
        public bool Done => Progress >= Count;
    }

    /// <summary>The checklist listening for moments: one fight at a time.</summary>
    private static CombatManager _momentListener;

    /// <summary>This fight's steps, or null when it has none.</summary>
    private List<TutorialStep> _tutorialSteps;

    /// <summary>Latched once the checklist is finished: the fight won (the trial) or the last
    /// ward broken (a practical). Nothing is counted after it.</summary>
    private bool _tutorialComplete;

    /// <summary>Damage already counted, per enemy: its DamageLandedThisCombat at the last look.</summary>
    private readonly Dictionary<Unit, int> _tutorialDamageSeen = new();

    /// <summary>Arcane marks already counted: GameState.MarksSpentOnEnemies at the last look.</summary>
    private int _tutorialMarksSeen;

    /// <summary>Spells cast in the current player turn (the cast tail counts them), and the round
    /// they belong to.</summary>
    private int _tutorialTurnSpells;
    private int _tutorialTurnSpellsRound = -1;

    /// <summary>Each enemy's current plan as first seen: the tile it locked and the player unit
    /// standing there when it was planned. Plans are locked at the end of the enemy turn, before
    /// the player can move, so the first look sees the unit still on the tile.</summary>
    private readonly Dictionary<Unit, (EnemyIntent intent, Vector2I tile, Unit aimedAt)> _tutorialAims = new();

    /// <summary>The definition id of the unit the checklist wards (the fight's pass unit), or
    /// empty when the checklist wins the fight instead.</summary>
    private string _tutorialWardId = "";

    /// <summary>The ward count: one per step the engine counts.</summary>
    private int _tutorialWardSections;

    /// <summary>Latched once the wards first stand on their unit, so the Register's note and the
    /// log line come once.</summary>
    private bool _tutorialWardsRaised;

    private bool TutorialStepsArmed => _tutorialSteps != null;

    private bool TutorialWards => !string.IsNullOrEmpty(_tutorialWardId) && _tutorialWardSections > 0;

    /// <summary>From InitObjectiveState: this fight's steps from the director, by the router's
    /// scripted fight id, and the unit they ward (the router's EndOnUnitFall). Clears any
    /// previous fight's first.</summary>
    private void ArmTutorialSteps()
    {
        _tutorialSteps = null;
        _tutorialComplete = false;
        _tutorialDamageSeen.Clear();
        _tutorialAims.Clear();
        _tutorialMarksSeen = State?.MarksSpentOnEnemies ?? 0;
        _tutorialTurnSpells = 0;
        _tutorialTurnSpellsRound = -1;
        _tutorialWardId = "";
        _tutorialWardSections = 0;
        _tutorialWardsRaised = false;
        StopTutorialMoments();
        if (!ScriptedFight)
            return;
        var router = EncounterRouter.Instance;
        var defs = PrologueDirector.StepsFor(router?.ScriptedFightId);
        if (defs == null || defs.Count == 0)
            return;

        var steps = new List<TutorialStep>();
        foreach (var d in defs)
        {
            if (d == null || string.IsNullOrEmpty(d.Id))
                continue;
            bool known = Array.IndexOf(TutorialStepIds, d.Id) >= 0;
            if (!known)
                GD.PushWarning($"[Tutorial] Step '{d.Id}' is not one the engine counts. It is shown but never completes.");
            steps.Add(new TutorialStep
            {
                Id = d.Id,
                Label = string.IsNullOrEmpty(d.Label) ? d.Id : d.Label,
                Count = Math.Max(1, d.Count),
                Known = known,
            });
        }
        if (steps.Count == 0)
            return;
        _tutorialSteps = steps;
        _momentListener = this;
        _tutorialWardId = router?.EndOnUnitFall ?? "";
        _tutorialWardSections = _tutorialWardId.Length > 0 ? steps.FindAll(s => s.Known).Count : 0;
        GD.Print($"[Tutorial] Armed {steps.Count} step(s): "
                 + string.Join(", ", steps.ConvertAll(s => $"{s.Id} x{s.Count}"))
                 + (TutorialWards ? $". {_tutorialWardId} is warded {_tutorialWardSections} time(s)." : "."));
    }

    /// <summary>From _ExitTree, and before a new fight arms: this fight stops listening for moments.</summary>
    private void StopTutorialMoments()
    {
        if (_momentListener == this)
            _momentListener = null;
    }

    /// <summary>A named moment of the fight. RegisterManager.Fire raises every key it is
    /// offered here first, and the direct sites raise theirs (TutorialMomentKeys). A step whose
    /// id is the key counts it. Ignored inside a preview (CombatSim) and when no checklist is
    /// listening.</summary>
    public static void NoteMoment(string key)
    {
        if (CombatSim.Active || string.IsNullOrEmpty(key))
            return;
        var cm = _momentListener;
        if (cm == null || !IsInstanceValid(cm) || cm._tutorialSteps == null)
            return;
        cm.AdvanceTutorialStep(key, 1);
    }

    /// <summary>Per frame, from the top of _Process: count the damage that landed and the marks
    /// that paid out, note each enemy's new plan, keep the wards, and finish the checklist once
    /// every step is done: the last ward breaks (a practical), or the fight is won once the turn
    /// is settled (the trial).</summary>
    private void RefreshTutorialSteps()
    {
        if (_tutorialSteps == null || _tutorialComplete)
            return;

        int landed = 0;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e))
                continue;
            int now = e.DamageLandedThisCombat;
            int was = _tutorialDamageSeen.TryGetValue(e, out var w) ? w : 0;
            if (now > was)
            {
                landed += now - was;
                _tutorialDamageSeen[e] = now;
            }
        }
        if (landed > 0)
            AdvanceTutorialStep(TutorialStepDamage, landed);

        int marks = State?.MarksSpentOnEnemies ?? 0;
        if (marks > _tutorialMarksSeen)
        {
            AdvanceTutorialStep(TutorialStepMark, marks - _tutorialMarksSeen);
            _tutorialMarksSeen = marks;
        }

        SnapshotTutorialAims();
        KeepTutorialWards();

        if (!AllTutorialStepsDone())
            return;

        // A practical: the steps break the wards. They do not win the fight.
        if (TutorialWards)
        {
            BreakLastTutorialWard();
            return;
        }

        // Settled: the player's own turn, no window open, nothing waiting to resolve. A step
        // finished in the enemy turn wins at the start of the next player turn.
        if (currentPhase != CombatPhase.PlayerTurn || isInDeploymentPhase || _priorityWindowOpen
            || (State != null && TriggersOutstanding))
            return;

        _tutorialComplete = true;
        _objectiveVictory = true;
        GD.Print("[Tutorial] Every step is done. The fight is won.");
        combatUI?.AppendActionLog("── Every step is done. The bell stills. ──");
        CheckCombatEnd();
    }

    /// <summary>Wards still standing: one for each step the engine counts that is not yet done.</summary>
    private int TutorialWardsStanding()
    {
        int standing = 0;
        foreach (var s in _tutorialSteps)
            if (s.Known && !s.Done)
                standing++;
        return standing;
    }

    /// <summary>The warded unit's name for the log, or "the apparatus" when none stands.</summary>
    private string TutorialWardedName()
    {
        foreach (var e in enemyUnits)
            if (e != null && IsInstanceValid(e) && e.Stats.IsAlive && e.DefinitionId == _tutorialWardId)
                return e.Name;
        return "the apparatus";
    }

    /// <summary>Each frame while steps remain: the warded unit carries one section per step and
    /// one ward per step not yet done (Unit.WardSections, Unit.WardsStanding), so a unit that
    /// spawns late is warded the moment it stands. The first time the wards stand, the log and
    /// the Register say what they are.</summary>
    private void KeepTutorialWards()
    {
        if (!TutorialWards)
            return;
        int standing = TutorialWardsStanding();
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || !e.Stats.IsAlive || e.IsDeathQueued
                || e.DefinitionId != _tutorialWardId)
                continue;
            if (e.WardSections == _tutorialWardSections && e.WardsStanding == standing)
                continue;
            e.WardSections = _tutorialWardSections;
            e.WardsStanding = standing;
            e.RefreshHealthBar();
            if (!_tutorialWardsRaised)
            {
                _tutorialWardsRaised = true;
                GD.Print($"[Tutorial] {e.Name} is warded: {standing} of {_tutorialWardSections} section(s).");
                combatUI?.AppendActionLog($"── {e.Name} is warded {_tutorialWardSections} times. Each step breaks one ward. ──");
                RegisterManager.Fire("practical.warded");
            }
        }
    }

    /// <summary>Every step is done in a fight with a pass unit: the last ward breaks, and breaking
    /// the unit now passes the fight (NoteScriptedUnitFall). The checklist stops counting.</summary>
    private void BreakLastTutorialWard()
    {
        _tutorialComplete = true;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || e.DefinitionId != _tutorialWardId)
                continue;
            e.WardSections = _tutorialWardSections;
            e.WardsStanding = 0;
            e.RefreshHealthBar();
        }
        string goal = string.IsNullOrEmpty(_objective?.Description) ? "It can fall now" : _objective.Description;
        GD.Print($"[Tutorial] Every step is done. The last ward on {_tutorialWardId} breaks.");
        combatUI?.AppendActionLog($"── Every step is done, and the last ward breaks. {goal}. ──");
        RegisterManager.Fire("practical.released");
        RefreshObjectiveBanner();
    }

    /// <summary>From TryMoveSelectedUnit, after a move succeeded.</summary>
    private void NoteTutorialMove(Unit mover)
    {
        if (mover != null && mover.TeamId == 0 && mover.IsPlayerControlled)
            AdvanceTutorialStep(TutorialStepWalk, 1);
    }

    /// <summary>From the cast tail, after a cast succeeded: a Reflex half cast in a priority
    /// window during the enemy turn has answered it; a cast on the player's own turn counts
    /// toward that turn's spells.</summary>
    private void NoteTutorialCast(CardHalf half)
    {
        if (half == null || _tutorialSteps == null)
            return;
        if (half.Speed == PlaySpeed.Reflex && _priorityWindowOpen && currentPhase == CombatPhase.EnemyTurn)
            AdvanceTutorialStep(TutorialStepReflex, 1);
        if (currentPhase == CombatPhase.PlayerTurn)
        {
            if (_tutorialTurnSpellsRound != roundNumber)
            {
                _tutorialTurnSpellsRound = roundNumber;
                _tutorialTurnSpells = 0;
            }
            _tutorialTurnSpells++;
            RaiseTutorialStepTo(TutorialStepTurnSpells, _tutorialTurnSpells);
        }
    }

    /// <summary>From ResolveStrike's hit branch: an enemy strike landed on a player unit, and its
    /// shield or armor took some of it. <paramref name="guardBefore"/> is the unit's shield plus
    /// armor just before the blow.</summary>
    private void NoteTutorialBlock(Unit attacker, Unit victim, int guardBefore)
    {
        if (_tutorialSteps == null || victim == null || !IsInstanceValid(victim) || victim.TeamId != 0)
            return;
        if (attacker != null && IsInstanceValid(attacker) && attacker.TeamId == 0)
            return;
        if (victim.Stats.Shield + victim.Stats.Armor < guardBefore)
            AdvanceTutorialStep(TutorialStepBlock, 1);
    }

    /// <summary>From RunEnemyTurn's negate branch: an enemy's action was cancelled outright.</summary>
    private void NoteTutorialNegate(Unit enemy)
    {
        if (enemy != null && IsInstanceValid(enemy) && enemy.TeamId != 0)
            AdvanceTutorialStep(TutorialStepNegate, 1);
    }

    /// <summary>From ResolveRetaliation: a player unit struck back at the enemy that hit it.</summary>
    private void NoteTutorialRiposte(Unit defender, Unit attacker)
    {
        if (defender != null && IsInstanceValid(defender) && defender.TeamId == 0
            && attacker != null && IsInstanceValid(attacker) && attacker.TeamId != 0)
            AdvanceTutorialStep(TutorialStepRiposte, 1);
    }

    /// <summary>From ResolveStrike, when a strike lands on a tile with nobody on it, and from a
    /// shot or a release wasted out of range or sight: a dodge when the tile was locked onto a
    /// player unit that has since left it. The stack route names that unit
    /// (<paramref name="originalVictim"/>); the other routes do not, so the plan's first look
    /// supplies it.</summary>
    private void NoteTutorialWhiff(Unit attacker, Vector2I tile, Unit originalVictim)
    {
        if (_tutorialSteps == null || attacker == null || !IsInstanceValid(attacker) || attacker.TeamId == 0)
            return;
        bool dodged = StoodAndLeft(originalVictim, tile)
            || (_tutorialAims.TryGetValue(attacker, out var aim) && aim.tile == tile && StoodAndLeft(aim.aimedAt, tile));
        if (dodged)
            AdvanceTutorialStep(TutorialStepDodge, 1);
    }

    private static bool StoodAndLeft(Unit unit, Vector2I tile) =>
        unit != null && IsInstanceValid(unit) && unit.Stats.IsAlive && unit.TeamId == 0
        && unit.CurrentTile != null && unit.CurrentTile.Axial != tile;

    /// <summary>The first look at each enemy's current plan: the tile it locked (a release
    /// lands on the channel's tile) and the living player unit standing there. A spell still on
    /// its way to the same tile (a channel's release, a channel held another round, a negated
    /// release planned again) is still meant for whoever stood there when it was first aimed,
    /// even when they stepped off before this plan was made.</summary>
    private void SnapshotTutorialAims()
    {
        if (grid == null)
            return;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || !e.Stats.IsAlive)
                continue;
            var intent = e.CurrentIntent;
            if (intent == null)
                continue;
            bool seenBefore = _tutorialAims.TryGetValue(e, out var seen);
            if (seenBefore && ReferenceEquals(seen.intent, intent))
                continue;
            Vector2I? aim = (intent.Kind == IntentKind.Release ? e.ChannelTile : null) ?? intent.TargetTile;
            Unit at = null;
            if (aim.HasValue)
            {
                var occupant = grid.GetTile(aim.Value)?.Occupant;
                if (occupant != null && IsInstanceValid(occupant) && occupant.Stats.IsAlive && occupant.TeamId == 0)
                    at = occupant;
            }
            if (at == null && aim.HasValue && seenBefore && seen.intent != null && seen.tile == aim.Value
                && IsSpellUnderway(seen.intent.Kind) && IsSpellUnderway(intent.Kind))
                at = seen.aimedAt;
            _tutorialAims[e] = (intent, aim ?? default, at);
        }
    }

    private static bool IsSpellUnderway(IntentKind kind) =>
        kind == IntentKind.Channel || kind == IntentKind.Release;

    private void AdvanceTutorialStep(string id, int amount)
    {
        if (_tutorialSteps == null || _tutorialComplete || amount <= 0)
            return;
        bool changed = false;
        foreach (var s in _tutorialSteps)
        {
            if (s.Id != id || s.Done)
                continue;
            s.Progress = Math.Min(s.Count, s.Progress + amount);
            changed = true;
            if (s.Done)
                LogTutorialStepDone(s);
        }
        if (changed)
            RefreshObjectiveBanner();
    }

    /// <summary>A step whose progress is a best-so-far (turn_spells): raise it to
    /// <paramref name="value"/> when that is more.</summary>
    private void RaiseTutorialStepTo(string id, int value)
    {
        if (_tutorialSteps == null || _tutorialComplete)
            return;
        bool changed = false;
        foreach (var s in _tutorialSteps)
        {
            if (s.Id != id || s.Done || value <= s.Progress)
                continue;
            s.Progress = Math.Min(s.Count, value);
            changed = true;
            if (s.Done)
                LogTutorialStepDone(s);
        }
        if (changed)
            RefreshObjectiveBanner();
    }

    /// <summary>A step was just done: say so, and, where the checklist wards a unit, that one of
    /// its wards broke (the last ward's line comes from BreakLastTutorialWard).</summary>
    private void LogTutorialStepDone(TutorialStep s)
    {
        GD.Print($"[Tutorial] Step done: {s.Id} ({s.Label}).");
        combatUI?.AppendActionLog($"── Done: {s.Label}. ──");
        if (!TutorialWards || !s.Known)
            return;
        int standing = TutorialWardsStanding();
        if (standing > 0)
            combatUI?.AppendActionLog($"── A ward on {TutorialWardedName()} breaks: {standing} left. ──");
    }

    private bool AllTutorialStepsDone()
    {
        bool any = false;
        foreach (var s in _tutorialSteps)
        {
            if (!s.Known)
                continue;
            if (!s.Done)
                return false;
            any = true;
        }
        return any;
    }

    /// <summary>The turn panel's objective text while a checklist is armed: the objective's
    /// label (with the round against the cap, the survive line's own format, when the fight has
    /// one), then one indented line per step with its counter, or "done". Sized for the right
    /// column (CombatUI).</summary>
    private string TutorialBannerText()
    {
        string label = string.IsNullOrEmpty(_objective?.Description) ? "Steps" : _objective.Description;
        var sb = new StringBuilder(label);
        if (_objective != null && _objective.Rounds > 0)
            sb.Append($": round {Mathf.Min(roundNumber, _objective.Rounds)} / {_objective.Rounds}");
        foreach (var s in _tutorialSteps)
            sb.Append("\n  ").Append(s.Label).Append("   ").Append(s.Done ? "done" : $"{s.Progress} / {s.Count}");
        return sb.ToString();
    }
}
