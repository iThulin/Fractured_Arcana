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
//                 - Response-hand check scans EVERY living arcane
//                   unit's hand against ITS OWN mana (2026-07-09;
//                   retires the active-deck-only v1 limitation —
//                   which, with State.Mana[Me] synced to the
//                   SELECTED unit, made the window nearly unopenable
//                   in real play). The window auto-selects the
//                   responder; clicking a friendly unit mid-window
//                   switches. A POOLED response strip (all castable
//                   Reactions across units in one surface) remains
//                   V3 tranche-2 UX.
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
    /// <summary>One-line effect description for the V3 stack strip (§7c).</summary>
    public string IntelLine = "";

    public EnemyTriggeredAbility(string name, string sourceName, IEffect effect, string intelLine = "")
    {
        Name = name;
        SourceName = sourceName;
        IntelLine = intelLine;
        Speed = PlaySpeed.Reflex;
        Effects = new[] { effect };
    }
}

public partial class CombatManager
{
    // ── Trigger queue ────────────────────────────────────────────────────────

    /// <summary>One queued trigger, context captured at queue time (the source
    /// node may be freed before resolution). Serves BOTH enemy ability triggers
    /// (Def-keyed) and Q2 item triggers (ItemKey-keyed) — one queue, one drain,
    /// one dispatcher (BuildTriggeredEffect).</summary>
    private sealed class QueuedTrigger
    {
        // ── Enemy ability path ──
        public UnitAbilityDef Def;
        public int SourceTeam;
        public Vector2I SourceTile;

        // ── Shared ──
        public string SourceName;
        /// <summary>Living carrier for abilities that mutate their owner (Requiem)
        /// or that carry an item (item triggers). Null for abilities whose owner
        /// is the dead unit (Deathburst).</summary>
        public Unit Carrier;

        // ── Q2 item path (set → this is an item trigger, not an enemy Def) ──
        public string ItemKey;        // effect key when Def == null
        public int ItemValue;         // magnitude
        public Unit ItemTarget;       // captured target (apply_bleed)
        public string DisplayName;    // item name for stack + log
        public bool PlayerControlled; // caster side: true → Me, false → Opp

        /// <summary>Key routed to the shared dispatcher: enemy Def.Key or item key.</summary>
        public string DispatchKey => Def != null ? Def.Key : ItemKey;
        public string StackName => Def != null ? Def.Name : DisplayName;
        public string IntelLine => Def != null ? Def.IntelDescription : "(item)";
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

    /// <summary>Wired to CombatUI's Respond button (§7c): an explicit affordance —
    /// selects the unit holding a castable Reflex so its hand is up and its mana
    /// is synced. Casting stays drag-to-cast; Respond never resolves anything.</summary>
    public void OnPriorityRespondPressed()
    {
        if (!_priorityWindowOpen)
            return;
        var responder = FindReactionResponder();
        if (responder == null)
            return;
        if (responder != selectedUnit)
            SelectUnit(responder);
        combatUI?.AppendActionLog($"{responder.Name} readies a response — drag a Reflex card onto its target.");
    }

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

    // ── The shared dispatcher (ability key → IEffect factory) ────────────────
    // ONE handler map for enemy abilities AND Q2 item passives (§7a: "the same
    // handler map enemy abilities use"). Keep enemy keys in sync with
    // UnitRegistry.AssertParityAndRoundTrip's knownAbilityKeys. Adding a key =
    // one case here + one effect class below.

    private IEffect BuildTriggeredEffect(QueuedTrigger t)
    {
        switch (t.DispatchKey.ToLowerInvariant())
        {
            // ── Enemy roster keys (U3) ──
            case "requiem":
                return new RequiemEffect(t.Carrier, t.Def.GetIntParam("amount", 2), this);
            case "deathburst":
                return new DeathburstEffect(t.SourceTile, t.SourceTeam, t.SourceName,
                    t.Def.GetIntParam("count", 2),
                    t.Def.GetStringParam("unit", "conductor_honored_dead"), this);

            // ── Item passive keys (Q2, §7a) ──
            case "shield_self":
                return new ItemShieldSelfEffect(t.ItemValue, t.Carrier, t.SourceName, this);
            case "apply_bleed":
                return new ItemBleedOnAttackEffect(t.ItemValue, t.ItemTarget, t.SourceName, this);

            default:
                GD.PrintErr($"[Triggers] Unknown trigger key '{t.DispatchKey}' on {t.SourceName} — skipped.");
                return null;
        }
    }

    // ── Q2 item-trigger call sites ───────────────────────────────────────────

    /// <summary>Fires a unit's onSpawn item abilities INLINE at spawn — combat
    /// hasn't started, so there is no priority window and no possible response
    /// (§5's initial-state carve-out). Still routes through the shared
    /// dispatcher + log grammar, satisfying the §7a "one dispatcher" rule.</summary>
    private void FireItemSpawnTriggers(Unit unit)
    {
        if (unit?.ItemAbilities == null)
            return;
        foreach (var ab in unit.ItemAbilities)
        {
            if (!string.Equals(ab.Trigger, "onSpawn", StringComparison.OrdinalIgnoreCase))
                continue;
            var t = new QueuedTrigger
            {
                Carrier = unit, SourceName = ab.SourceName,
                ItemKey = ab.Key, ItemValue = ab.Value,
            };
            var eff = BuildTriggeredEffect(t);
            eff?.Resolve(State, unit.IsPlayerControlled ? Me : Opp, null, new EffectSnapshot());
        }
    }

    /// <summary>Queues a unit's onAttack item abilities onto the trigger stack
    /// after its attack resolves (§7a: item procs ride the stack, auto-passing).
    /// Target captured at queue time. The caller kicks the drain.</summary>
    private void QueueItemAttackTriggers(Unit attacker, Unit target)
    {
        if (attacker?.ItemAbilities == null || target == null || !target.Stats.IsAlive)
            return;
        foreach (var ab in attacker.ItemAbilities)
        {
            if (!string.Equals(ab.Trigger, "onAttack", StringComparison.OrdinalIgnoreCase))
                continue;
            _pendingTriggers.Add(new QueuedTrigger
            {
                Carrier = attacker,
                SourceName = ab.SourceName,
                ItemKey = ab.Key,
                ItemValue = ab.Value,
                ItemTarget = target,
                DisplayName = ab.SourceName,
                PlayerControlled = attacker.IsPlayerControlled,
            });
        }
    }

    /// <summary>Recomputes item AURAS (§5: states, not stack events). Called at
    /// the start of each player turn. Regen auras heal adjacent allies — a pure
    /// per-turn event, so no accumulation bookkeeping.</summary>
    private void ApplyItemAuras()
    {
        foreach (var granter in playerUnits)
        {
            if (granter == null || !IsInstanceValid(granter) || !granter.Stats.IsAlive
                || granter.CurrentTile == null || granter.ItemAbilities == null)
                continue;
            foreach (var ab in granter.ItemAbilities)
            {
                if (!string.Equals(ab.Trigger, "aura", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(ab.Key, "regen_aura", StringComparison.OrdinalIgnoreCase))
                    continue;

                int healed = 0;
                foreach (var neighbor in grid.GetNeighbors(granter.CurrentTile.Axial))
                {
                    var occ = grid.GetTile(neighbor)?.Occupant;
                    if (occ == null || occ == granter || !occ.Stats.IsAlive)
                        continue;
                    if (occ.TeamId != granter.TeamId)
                        continue;
                    if (occ.Stats.Health >= occ.Stats.MaxHealth)
                        continue;
                    occ.Stats.Health = Math.Min(occ.Stats.MaxHealth, occ.Stats.Health + ab.Value);
                    occ.RefreshHealthBar();
                    healed++;
                }
                if (healed > 0)
                {
                    string msg = UIContent.FormatLogLine(ab.SourceName, "Aura",
                        $"+{ab.Value} HP to {healed} ally(ies)");
                    GD.Print(msg);
                    AppendCombatLog(msg);
                }
            }
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
                    string fizzle = $"[Stack] {t.StackName} ({t.SourceName}) fizzles — its source is gone.";
                    GD.Print(fizzle);
                    combatUI?.AppendActionLog(fizzle);
                    continue;
                }

                var effect = BuildTriggeredEffect(t);
                if (effect == null)
                    continue;

                var ability = new EnemyTriggeredAbility(t.StackName, t.SourceName, effect, t.IntelLine);
                State.Stack.Push(new StackItem
                {
                    Ability = ability,
                    Caster = t.PlayerControlled ? Me : Opp,
                    Targets = null,
                    Snapshot = new EffectSnapshot(),
                });
                State.Priority.OnStackItemAdded();

                string entered = $"[Stack] {t.StackName} ({t.SourceName}) enters the stack (size {State.StackCount()}).";
                GD.Print(entered);
                combatUI?.AppendActionLog(entered);
            }

            // Resolve with a priority window before each item.
            // (2026-07-10 UX) One Pass covers the whole exchange: after the player
            // passes, further windows are skipped while the stack only SHRINKS.
            // Anything newly pushed (deaths mid-resolution) re-opens priority.
            int windowSkipAtOrBelow = -1;
            while (!State.Stack.IsEmpty)
            {
                var top = State.Stack.PeekTop();
                string topName = top?.Ability?.Name ?? "?";

                // V3 (§7c): the strip renders whatever is on the stack — even
                // during auto-pass it plays through visibly with zero input.
                combatUI?.ShowStackStrip(BuildStackSnapshot(), interactive: false);

                // §7c stops override the one-Pass-covers-the-exchange ruling
                // (2026-07-10): a set stop reopens the window before EVERY
                // matching resolution — that is what "stop" promises.
                if (State.StackCount() > windowSkipAtOrBelow
                    || PlayerSession.DebugStopOnTriggers
                    || StopSetFor(CategorizeStackItem(top)))
                {
                    await OpenTriggerPriorityWindow(topName);
                    windowSkipAtOrBelow = State.StackCount();
                }

                // The response may have COUNTERED/changed things — re-check.
                if (State.Stack.IsEmpty)
                    break;

                combatUI?.ShowStackStrip(BuildStackSnapshot(), interactive: false);
                GD.Print($"[Stack] Resolving {State.Stack.PeekTop()?.Ability?.Name} " +
                         $"(size before: {State.StackCount()}).");
                State.Resolver.ResolveTop(State);

                // Readability beat — the strip is legible while it plays through.
                await ToSignal(GetTree().CreateTimer(0.3f), "timeout");

                // A resolution can kill units (future keys) → queue more triggers.
                while (_pendingTriggers.Count > 0)
                {
                    var t = _pendingTriggers[0];
                    _pendingTriggers.RemoveAt(0);
                    if (t.Carrier != null && (!IsInstanceValid(t.Carrier) || !t.Carrier.Stats.IsAlive))
                        continue;
                    var eff = BuildTriggeredEffect(t);
                    if (eff == null)
                        continue;
                    State.Stack.Push(new StackItem
                    {
                        Ability = new EnemyTriggeredAbility(t.StackName, t.SourceName, eff, t.IntelLine),
                        Caster = t.PlayerControlled ? Me : Opp,
                        Snapshot = new EffectSnapshot(),
                    });
                    State.Priority.OnStackItemAdded();
                    windowSkipAtOrBelow = -1;   // new object → priority re-arms
                    string entered = $"[Stack] {t.StackName} ({t.SourceName}) enters the stack (size {State.StackCount()}).";
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
        combatUI?.HideStackStrip();
        CheckCombatEnd();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
    }

    /// <summary>V3: (source, name, one-line effect) per stack object, top-down.
    /// Enemy triggers carry their own source + intel; card halves show the
    /// caster's name and the half name.</summary>
    private List<(string, string, string)> BuildStackSnapshot()
    {
        var items = new List<(string, string, string)>();
        foreach (var item in State.Stack.Items)   // Stack.Items iterates top-down
        {
            if (item?.Ability is EnemyTriggeredAbility eta)
                items.Add((eta.SourceName, eta.Name, eta.IntelLine));
            else if (item?.Ability != null)
                items.Add((item.Caster?.Name ?? "?", item.Ability.Name, "(response)"));
        }
        return items;
    }

    // ── Stack stops (§7c) ────────────────────────────────────────────────────

    /// <summary>Stop categories for the strip-header toggles.</summary>
    private enum StackStopCategory { Strike, EnemyAbility, ItemProc, Response }

    /// <summary>Buckets a stack object for the per-type stops: enemy strikes,
    /// item procs (Item* effect classes), everything else trigger-borne counts
    /// as an enemy ability. Player-cast responses never stop on themselves.</summary>
    private static StackStopCategory CategorizeStackItem(StackItem item)
    {
        if (item?.Ability is EnemyTriggeredAbility eta)
        {
            var eff = eta.Effects != null && eta.Effects.Length > 0 ? eta.Effects[0] : null;
            if (eff is EnemyStrikeEffect)
                return StackStopCategory.Strike;
            if (eff != null && eff.GetType().Name.StartsWith("Item", StringComparison.Ordinal))
                return StackStopCategory.ItemProc;
            return StackStopCategory.EnemyAbility;
        }
        return StackStopCategory.Response;
    }

    private static bool StopSetFor(StackStopCategory cat) => cat switch
    {
        StackStopCategory.Strike       => PlayerSession.StopOnStrikes,
        StackStopCategory.EnemyAbility => PlayerSession.StopOnEnemyAbilities,
        StackStopCategory.ItemProc     => PlayerSession.StopOnItemProcs,
        _                              => false,
    };

    // ── Priority window ──────────────────────────────────────────────────────

    /// <summary>The R3 window: the human player gets priority before each trigger
    /// resolution. Auto-passes with zero clicks unless the player holds a castable
    /// Reaction-speed card in the ACTIVE deck's hand, or has set a stop.</summary>
    private async Task OpenTriggerPriorityWindow(string topName)
    {
        // §7c stops: the debug lever still stops on everything; the player-facing
        // toggles stop per category of the object about to resolve.
        bool stopSet = PlayerSession.DebugStopOnTriggers
            || StopSetFor(CategorizeStackItem(State.Stack.PeekTop()));
        var responder = FindReactionResponder();
        bool holdsResponse = responder != null;

        if (!stopSet && !holdsResponse)
        {
            GD.Print($"[Priority] auto-pass on {topName} (no response held).");
            return;
        }

        _priorityWindowOpen = true;
        _priorityPassed = false;
        string why = holdsResponse ? $"{responder.Name} holds a response" : "stop set";
        GD.Print($"[Priority] window OPEN on {topName} ({why}).");

        // §7c: while the window is open, non-Reflex halves in the hand darken +
        // desaturate — only castable responses read as live.
        deckUiManager?.SetReactionWindow(true);

        // Auto-select the responder so their hand is on screen and their mana is
        // synced (SelectUnit does both). Click any other friendly mid-window to
        // switch responders — see OnLeftMouseReleased's window branch.
        if (responder != null && responder != selectedUnit)
        {
            GD.Print($"[Priority] auto-selected {responder.Name} (holds a response).");
            combatUI?.AppendActionLog($"{responder.Name} can respond — cast a Reflex, or Pass to resolve.");
            SelectUnit(responder);
            if (currentPhase != CombatPhase.PlayerTurn)
                ClearMoveTiles();   // enemy-phase window: no move affordance
        }
        // V3 (§7c): the strip itself is the window — Pass + Respond enabled
        // (Respond greys out unless a castable Reflex is actually in hand).
        combatUI?.ShowStackStrip(BuildStackSnapshot(), interactive: true, canRespond: holdsResponse);

        int lastCount = State.StackCount();
        while (!_priorityPassed)
        {
            // Keep the window until the player passes, per R3: priority is
            // theirs until surrendered (a response cast does not auto-pass —
            // they may hold another Reaction).
            await ToSignal(GetTree(), "process_frame");

            // Re-render on stack growth so a cast response appears immediately.
            int c = State.StackCount();
            if (c != lastCount)
            {
                lastCount = c;
                var nextResponder = FindReactionResponder();
                combatUI?.ShowStackStrip(BuildStackSnapshot(), interactive: true,
                    canRespond: nextResponder != null);

                // (2026-07-10 UX) The cast just landed. If no further castable
                // response is held, don't demand a Pass click — close the window.
                if (!stopSet && nextResponder == null)
                {
                    GD.Print("[Priority] auto-close — no further responses held.");
                    _priorityPassed = true;
                }
            }
        }

        _priorityWindowOpen = false;
        deckUiManager?.SetReactionWindow(false);   // §7c: restore normal card read
        combatUI?.ShowStackStrip(BuildStackSnapshot(), interactive: false);
        GD.Print($"[Priority] passed on {topName}.");
    }

    /// <summary>True when ANY living arcane player unit holds a castable Reaction.
    /// (2026-07-09: the active-deck-only v1 limitation is retired — the gate now
    /// scans every hand with per-unit mana. Pooled response strip stays V3 UX.)</summary>
    private bool PlayerHoldsCastableReaction() => FindReactionResponder() != null;

    /// <summary>First living player unit holding a castable Reaction-speed half.
    /// The selected unit wins ties so auto-select never yanks a valid selection;
    /// otherwise party order. Null when nobody can respond.</summary>
    private Unit FindReactionResponder()
    {
        if (selectedUnit != null && IsInstanceValid(selectedUnit)
            && selectedUnit.Stats.IsAlive && UnitHoldsCastableReaction(selectedUnit))
            return selectedUnit;

        foreach (var unit in playerUnits)
        {
            if (unit == null || unit == selectedUnit || !IsInstanceValid(unit) || !unit.Stats.IsAlive)
                continue;
            if (UnitHoldsCastableReaction(unit))
                return unit;
        }
        return null;
    }

    /// <summary>Castable = Reaction speed AND this unit can act AND this unit can
    /// pay from ITS OWN mana (or holds a Fate free reaction, mirroring
    /// Rules.CanCast's bypass so the window opens whenever the cast would land).</summary>
    private bool UnitHoldsCastableReaction(Unit unit)
    {
        if (unit.IsMartial || unit.DeckData == null || !unit.CanAct())
            return false;

        bool freeReaction = unit.Attunement is FateAttunement fate && fate.HasFreeReaction;

        foreach (var card in unit.DeckData.Hand)
        {
            if (card == null)
                continue;
            foreach (var half in new[] { card.TopHalf, card.BottomHalf })
            {
                if (half == null || half.Speed == PlaySpeed.Studied)   // only Reflexes respond
                    continue;
                if (freeReaction || UnitCanPlay(half, unit))
                    return true;
            }
        }
        return false;
    }

    /// <summary>half.CanPlay evaluated against a SPECIFIC unit's mana. ManaCost.CanPay
    /// treats State.ActiveCasterUnit as authoritative (RuntimeInterfaces.cs), so pin
    /// it for the check and restore — without this, the fallback reads State.Mana[Me],
    /// which SelectUnit syncs to whatever unit happens to be selected (the root cause
    /// of the v3 checklist-4 defect). Conditions are pure; CanPay only reads.</summary>
    private bool UnitCanPlay(CardHalf half, Unit unit)
    {
        var prev = State.ActiveCasterUnit;
        State.ActiveCasterUnit = unit;
        try { return half.CanPlay(State, Me); }
        finally { State.ActiveCasterUnit = prev; }
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
        int stacks = _carrier.AbilityUseCounts.TryGetValue("requiem", out var n) ? n + 1 : 1;
        _carrier.AbilityUseCounts["requiem"] = stacks;
        // V3 (§9): fixed grammar via FormatLogLine — [Source] Ability: effect (state).
        string msg = UIContent.FormatLogLine(_carrier.Name, "Requiem",
            $"+{_amount} damage", $"{stacks} stack{(stacks == 1 ? "" : "s")}, now {_carrier.AttackDamage}");
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

        // V3 (§9): fixed grammar via FormatLogLine.
        string msg = spawned > 0
            ? UIContent.FormatLogLine(_sourceName, "Deathburst", $"{spawned} Honored Dead rise")
            : UIContent.FormatLogLine(_sourceName, "Deathburst", "no room at the table — nothing rises");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}


/// <summary>An enemy strike as a first-class stack object (R3 follow-on, 2026-07-10).
/// Dodge = vacate the tile before resolution (the occupant is re-read); Redirect = a
/// Reaction replaced StackItem.Targets with a DIFFERENT unit (RedirectEffect). The
/// original victim still being listed just means nobody redirected.</summary>
public sealed class EnemyStrikeEffect : IEffect
{
    private readonly CombatManager _cm;
    private readonly Unit _attacker;
    private readonly Vector2I _tile;
    private readonly int _damage;
    private readonly bool _ranged;
    private readonly Unit _originalVictim;

    public EnemyStrikeEffect(CombatManager cm, Unit attacker, Vector2I tile,
                             int damage, bool ranged, Unit originalVictim)
    {
        _cm = cm; _attacker = attacker; _tile = tile;
        _damage = damage; _ranged = ranged; _originalVictim = originalVictim;
    }

    public string[] Tags { get; private set; } = { "Ability", "Damage" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        Unit listed = null;
        if (targets?.Items != null)
            foreach (var o in targets.Items)
                if (o is Unit u) { listed = u; break; }

        Unit redirected = (listed != null && listed != _originalVictim) ? listed : null;
        // §9: pass the originally-listed victim so a vacated tile logs as a Dodge
        // reaction line rather than the generic empty-ground whiff.
        _cm.ResolveStrike(_attacker, _tile, _damage, _ranged, redirected, _originalVictim);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// Q2 item-trigger effects — §7a: items are abilities the wearer carries, on the
// SAME dispatcher + stack + log grammar as enemy abilities.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>onSpawn item effect: the wearer gains N shield at combat start
/// (the "shield-on-combat-start is OnSpawn" example from §7a). Resolves inline
/// at spawn — see FireItemSpawnTriggers.</summary>
public sealed class ItemShieldSelfEffect : IEffect
{
    private readonly int _amount;
    private readonly Unit _carrier;
    private readonly string _source;
    private readonly CombatManager _cm;
    public ItemShieldSelfEffect(int amount, Unit carrier, string source, CombatManager cm)
    { _amount = amount; _carrier = carrier; _source = source; _cm = cm; }

    public string[] Tags { get; private set; } = { "Item", "Shield" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive)
            return;
        _carrier.Stats.Shield += _amount;
        _carrier.RefreshHealthBar();
        string msg = UIContent.FormatLogLine(_source, "Ward", $"+{_amount} shield to {_carrier.Name}");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>onAttack item effect: the wearer's melee attack applies Bleed to the
/// struck target (the "Duelist's Brand: Bleed applied" example from §7a). Rides
/// the stack, auto-passing. Bleed ticks in ProcessStatusEffects.</summary>
public sealed class ItemBleedOnAttackEffect : IEffect
{
    private readonly int _turns;
    private readonly Unit _target;
    private readonly string _source;
    private readonly CombatManager _cm;
    public ItemBleedOnAttackEffect(int turns, Unit target, string source, CombatManager cm)
    { _turns = turns; _target = target; _source = source; _cm = cm; }

    public string[] Tags { get; private set; } = { "Item", "Debuff" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_target == null || !GodotObject.IsInstanceValid(_target) || !_target.Stats.IsAlive)
            return;
        _target.ApplyStatus("bleed", _turns);
        string msg = UIContent.FormatLogLine(_source, "Bleed",
            $"applied to {_target.Name}", $"{_turns} turn{(_turns == 1 ? "" : "s")}");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}
