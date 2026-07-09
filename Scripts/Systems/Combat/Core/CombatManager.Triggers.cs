using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// CombatManager.Triggers.cs  (partial of CombatManager)  (U3)
//
// Purpose:        The enemy trigger bus — RULED stack-first (R3):
//                 triggered enemy abilities ENTER the RulesManager
//                 stack as first-class, visible, respondable objects.
//                 They do not resolve immediately.
//
//                 FLOW:
//                 1. Game events (deaths, later attacks/spawns/turn
//                    ends) QUEUE triggers via QueueDeathTriggers etc.
//                    Queuing is synchronous and allocation-only —
//                    safe from any call site including OnDied.
//                 2. DrainTriggerStackAsync() pushes queued triggers
//                    as StackItems, then loops: PRIORITY WINDOW →
//                    ResolveTop, until the stack is empty. The human
//                    player receives priority before EACH resolution;
//                    the AI auto-passes (one-directional in v1).
//                 3. Drain points: start + tail of RunEnemyTurn,
//                    after each enemy's ExecuteIntent, and after the
//                    player's own cast resolution (kills during the
//                    player turn).
//
//                 ANTI-CLICK-FATIGUE (units doc §5): the priority
//                 window auto-passes with ZERO clicks unless the
//                 player holds a castable Reaction-speed card or has
//                 set a stop (PlayerSession.DebugStopOnTriggers).
//                 A fight the player cannot interact with must cost
//                 zero extra clicks.
//
//                 RESPONSE CASTS: while a window is open, the card
//                 drop path accepts Reaction-speed halves only, and
//                 skips its own stack drain — the cast lands ON TOP
//                 of the trigger and this loop resolves it first
//                 (LIFO). Sorcery/Instant drops are rejected with a
//                 log line.
//
//                 SCOPE RULINGS (logged):
//                 - Only the DEATH call sites are wired in U3 (the
//                   conductor roster needs onDeath + onAllyDeath).
//                   The bus + taxonomy support the full §5 table;
//                   U4+ rosters add their call sites when their keys
//                   land. Per build_order §8: U3 scope is exactly
//                   one roster's keys — resist landing U4 "while in
//                   there".
//                 - onAllyDeath is a taxonomy addition: §5 lists
//                   OnDeath (self); Requiem is specced "(OnDeath of
//                   any ally)". A distinct trigger string keeps the
//                   JSON declarative instead of overloading OnDeath.
//                 - Trigger contexts capture name/tile/team AT QUEUE
//                   TIME: by resolution the source node may be
//                   QueueFree'd (PruneDeadUnits). Handlers must not
//                   dereference dead Unit nodes.
//                 - CheckCombatEnd defers while triggers are pending
//                   or on the stack: killing The Final Service last
//                   must not declare victory before Deathburst
//                   resolves and the Honored Dead rise.
//                 - Response-hand check reads the ACTIVE deck only
//                   (deckManager.Hand). Multi-unit reaction hands are
//                   V3 UX territory; logged as a known limitation.
//
// Layer:          System (combat rules)
// Collaborators:  RulesManager.cs (GameStack/PriorityManager/Resolver),
//                 UnitDefinition.cs (UnitAbilityDef), Unit.cs,
//                 CombatManager.cs (HandleUnitDeath queues; summon
//                 seam spawns), CombatUI.cs (priority prompt),
//                 GameStateManager.cs (OnSummonRequested).
// See:            archmage_unique_units §5 (R3) · build_order_v3 §4 (U3)
// ============================================================

/// <summary>A triggered enemy ability on the stack. Derives from Ability so the
/// existing GameStack/Resolver pipeline carries it like any card half: free,
/// untargeted, effects resolve top-to-bottom.</summary>
public sealed class EnemyTriggeredAbility : Ability
{
    /// <summary>Display source for stack/log lines, e.g. "The Final Service".</summary>
    public string SourceName = "";

    public EnemyTriggeredAbility(string name, string sourceName, IEffect effect)
    {
        Name = name;
        SourceName = sourceName;
        Speed = PlaySpeed.Instant;
        Effects = new[] { effect };
    }
}

public partial class CombatManager
{
    // ── Trigger queue ────────────────────────────────────────────────────────

    /// <summary>One queued trigger, context captured at queue time (the source
    /// node may be freed before resolution).</summary>
    private sealed class QueuedTrigger
    {
        public UnitAbilityDef Def;
        public string SourceName;
        public int SourceTeam;
        public Vector2I SourceTile;
        /// <summary>Living carrier for abilities that mutate their owner (Requiem).
        /// Null for abilities whose owner is the dead unit (Deathburst).</summary>
        public Unit Carrier;
    }

    private readonly List<QueuedTrigger> _pendingTriggers = new();
    private bool _triggerDrainRunning = false;

    // ── Priority window state ────────────────────────────────────────────────

    private bool _priorityWindowOpen = false;
    private bool _priorityPassed = false;

    /// <summary>True while a priority window is open — the card drop path reads
    /// this to allow Reaction-speed responses and to leave stack draining to the
    /// trigger loop.</summary>
    public bool PriorityWindowOpen => _priorityWindowOpen;

    /// <summary>Wired to CombatUI's Pass button.</summary>
    public void OnPriorityPassPressed() => _priorityPassed = true;

    // ── Queue call sites ─────────────────────────────────────────────────────

    /// <summary>Queues death-driven triggers for a unit that just died: the unit's
    /// own onDeath abilities, then every living ALLY's onAllyDeath abilities.
    /// Called from HandleUnitDeath BEFORE removal/prune. Synchronous; the drain
    /// happens at the next safe async point.</summary>
    private void QueueDeathTriggers(Unit dead)
    {
        if (dead == null || dead.CurrentTile == null)
            return;

        // Own onDeath (Deathburst) — context is the corpse's tile and team.
        foreach (var ab in dead.Abilities)
        {
            if (!string.Equals(ab.Trigger, "onDeath", StringComparison.OrdinalIgnoreCase))
                continue;
            _pendingTriggers.Add(new QueuedTrigger
            {
                Def = ab,
                SourceName = dead.Name,
                SourceTeam = dead.TeamId,
                SourceTile = dead.CurrentTile.Axial,
                Carrier = null,
            });
        }

        // Allies' onAllyDeath (Requiem) — carrier must still be alive.
        foreach (var ally in enemyUnits)
        {
            if (ally == null || ally == dead || !IsInstanceValid(ally) || !ally.Stats.IsAlive)
                continue;
            if (ally.TeamId != dead.TeamId)
                continue;
            foreach (var ab in ally.Abilities)
            {
                if (!string.Equals(ab.Trigger, "onAllyDeath", StringComparison.OrdinalIgnoreCase))
                    continue;
                _pendingTriggers.Add(new QueuedTrigger
                {
                    Def = ab,
                    SourceName = ally.Name,
                    SourceTeam = ally.TeamId,
                    SourceTile = ally.CurrentTile?.Axial ?? Vector2I.Zero,
                    Carrier = ally,
                });
            }
        }
    }

    /// <summary>True when death triggers are queued or on the stack — combat-end
    /// evaluation defers until the stack settles.</summary>
    private bool TriggersOutstanding => _pendingTriggers.Count > 0 || !State.Stack.IsEmpty;

    // ── Handler map (ability Key → IEffect factory) ──────────────────────────
    // Keep in sync with UnitRegistry.AssertParityAndRoundTrip's knownAbilityKeys.
    // Adding a roster key (U4+) = one entry here + one effect class below.

    private IEffect BuildAbilityEffect(QueuedTrigger t)
    {
        switch (t.Def.Key.ToLowerInvariant())
        {
            case "requiem":
                return new RequiemEffect(t.Carrier, t.Def.GetIntParam("amount", 2), this);
            case "deathburst":
                return new DeathburstEffect(t.SourceTile, t.SourceTeam, t.SourceName,
                    t.Def.GetIntParam("count", 2),
                    t.Def.GetStringParam("unit", "conductor_honored_dead"), this);
            default:
                GD.PrintErr($"[Triggers] Unknown ability key '{t.Def.Key}' on {t.SourceName} — skipped. " +
                            "(Registry assertion should have caught this.)");
                return null;
        }
    }

    // ── Drain loop: push queued → (priority window → resolve) until empty ────

    /// <summary>Kicks a drain if one isn't already running. Safe to call from
    /// sync contexts (fire-and-forget; runs on the scene tree via ToSignal).</summary>
    private void KickTriggerDrain()
    {
        if (_triggerDrainRunning || (_pendingTriggers.Count == 0 && State.Stack.IsEmpty))
            return;
        _ = DrainTriggerStackAsync();
    }

    /// <summary>Pushes all queued triggers as stack objects, then resolves the
    /// stack top-down with a priority window before EACH resolution (R3).</summary>
    private async Task DrainTriggerStackAsync()
    {
        if (_triggerDrainRunning)
            return;
        _triggerDrainRunning = true;
        try
        {
            // Push every queued trigger (queue order = trigger order; LIFO stack
            // means the LAST queued resolves FIRST — and any response the player
            // casts lands on top of all of them).
            while (_pendingTriggers.Count > 0)
            {
                var t = _pendingTriggers[0];
                _pendingTriggers.RemoveAt(0);

                // Carrier died between queue and push → ability fizzles (Requiem
                // on a Wake-Keeper that was itself killed by the same sweep).
                if (t.Carrier != null && (!IsInstanceValid(t.Carrier) || !t.Carrier.Stats.IsAlive))
                {
                    string fizzle = $"[Stack] {t.Def.Name} ({t.SourceName}) fizzles — its source is gone.";
                    GD.Print(fizzle);
                    combatUI?.AppendActionLog(fizzle);
                    continue;
                }

                var effect = BuildAbilityEffect(t);
                if (effect == null)
                    continue;

                var ability = new EnemyTriggeredAbility(t.Def.Name, t.SourceName, effect);
                State.Stack.Push(new StackItem
                {
                    Ability = ability,
                    Caster = Opp,
                    Targets = null,
                    Snapshot = new EffectSnapshot(),
                });
                State.Priority.OnStackItemAdded();

                string entered = $"[Stack] {t.Def.Name} ({t.SourceName}) enters the stack (size {State.StackCount()}).";
                GD.Print(entered);
                combatUI?.AppendActionLog(entered);
            }

            // Resolve with a priority window before each item.
            while (!State.Stack.IsEmpty)
            {
                var top = State.Stack.PeekTop();
                string topName = top?.Ability?.Name ?? "?";

                await OpenTriggerPriorityWindow(topName);

                // The response may have COUNTERED/changed things — re-check.
                if (State.Stack.IsEmpty)
                    break;

                GD.Print($"[Stack] Resolving {State.Stack.PeekTop()?.Ability?.Name} " +
                         $"(size before: {State.StackCount()}).");
                State.Resolver.ResolveTop(State);

                // A resolution can kill units (future keys) → queue more triggers.
                while (_pendingTriggers.Count > 0)
                {
                    var t = _pendingTriggers[0];
                    _pendingTriggers.RemoveAt(0);
                    if (t.Carrier != null && (!IsInstanceValid(t.Carrier) || !t.Carrier.Stats.IsAlive))
                        continue;
                    var eff = BuildAbilityEffect(t);
                    if (eff == null)
                        continue;
                    State.Stack.Push(new StackItem
                    {
                        Ability = new EnemyTriggeredAbility(t.Def.Name, t.SourceName, eff),
                        Caster = Opp,
                        Snapshot = new EffectSnapshot(),
                    });
                    State.Priority.OnStackItemAdded();
                    string entered = $"[Stack] {t.Def.Name} ({t.SourceName}) enters the stack (size {State.StackCount()}).";
                    GD.Print(entered);
                    combatUI?.AppendActionLog(entered);
                }
            }
        }
        finally
        {
            _triggerDrainRunning = false;
        }

        // The stack settled — evaluate combat end that was deferred while
        // triggers were outstanding (Final Service killed last, etc.).
        CheckCombatEnd();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
    }

    // ── Priority window ──────────────────────────────────────────────────────

    /// <summary>The R3 window: the human player gets priority before each trigger
    /// resolution. Auto-passes with zero clicks unless the player holds a castable
    /// Reaction-speed card in the ACTIVE deck's hand, or has set a stop.</summary>
    private async Task OpenTriggerPriorityWindow(string topName)
    {
        bool stopSet = PlayerSession.DebugStopOnTriggers;
        bool holdsResponse = PlayerHoldsCastableReaction();

        if (!stopSet && !holdsResponse)
        {
            GD.Print($"[Priority] auto-pass on {topName} (no response held).");
            return;
        }

        _priorityWindowOpen = true;
        _priorityPassed = false;
        string why = holdsResponse ? "you hold a response" : "stop set";
        GD.Print($"[Priority] window OPEN on {topName} ({why}).");
        combatUI?.ShowPriorityPrompt($"{topName} on the stack — respond (Reaction) or pass");

        while (!_priorityPassed)
        {
            // Keep the window until the player passes, per R3: priority is
            // theirs until surrendered (a response cast does not auto-pass —
            // they may hold another Reaction).
            await ToSignal(GetTree(), "process_frame");
        }

        _priorityWindowOpen = false;
        combatUI?.HidePriorityPrompt();
        GD.Print($"[Priority] passed on {topName}.");
    }

    /// <summary>Castable = Reaction speed AND the active caster can pay. Reads the
    /// active deck's hand only (v1 limitation, logged in the header).</summary>
    private bool PlayerHoldsCastableReaction()
    {
        if (deckManager == null)
            return false;

        foreach (var card in deckManager.Hand)
        {
            if (card == null)
                continue;
            foreach (var half in new[] { card.TopHalf, card.BottomHalf })
            {
                if (half == null || half.Speed != PlaySpeed.Reaction)
                    continue;
                if (half.CanPlay(State, Me))
                    return true;
            }
        }
        return false;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// U3 ability effects — the Long Table's keys
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Requiem (Wake-Keeper): +N AttackDamage, stacking, when any ally dies.
/// Every death is added to the tab; the longer the fight grinds, the steeper the
/// bill. Mutates the carrier's per-unit AttackDamage — its next planned intent
/// telegraphs the new value automatically.</summary>
public sealed class RequiemEffect : IEffect
{
    private readonly Unit _carrier;
    private readonly int _amount;
    private readonly CombatManager _cm;
    public RequiemEffect(Unit carrier, int amount, CombatManager cm)
    { _carrier = carrier; _amount = amount; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Buff" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive)
        {
            s.Log("[Requiem] carrier gone — no effect.");
            return;
        }
        _carrier.AttackDamage += _amount;
        string msg = $"[{_carrier.Name}] Requiem: +{_amount} damage (now {_carrier.AttackDamage}).";
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>Deathburst (The Final Service): on death, spawns N units on free tiles
/// adjacent to the corpse via the existing summon seam. The service ends; the
/// guests arrive.</summary>
public sealed class DeathburstEffect : IEffect
{
    private readonly Vector2I _tile;
    private readonly int _team;
    private readonly string _sourceName;
    private readonly int _count;
    private readonly string _unitId;
    private readonly CombatManager _cm;

    public DeathburstEffect(Vector2I tile, int team, string sourceName,
                            int count, string unitId, CombatManager cm)
    { _tile = tile; _team = team; _sourceName = sourceName; _count = count; _unitId = unitId; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Summon" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s.OnSummonRequested == null || s.Grid == null)
        {
            s.Log("[Deathburst] no summon seam — no effect.");
            return;
        }

        int spawned = 0;
        foreach (var neighbor in s.Grid.GetNeighbors(_tile))
        {
            if (spawned >= _count)
                break;
            var td = s.Grid.GetTile(neighbor);
            if (td == null || !td.IsWalkable || td.IsBlocked || td.IsOccupied)
                continue;
            var unit = s.OnSummonRequested(_unitId, td, _team);
            if (unit != null)
                spawned++;
        }

        string msg = spawned > 0
            ? $"[{_sourceName}] Deathburst: the guests arrive — {spawned} risen."
            : $"[{_sourceName}] Deathburst: no room at the table — nothing rises.";
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}
