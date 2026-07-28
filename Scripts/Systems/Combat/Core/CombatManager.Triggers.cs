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

        /// <summary>U3b: the unit this trigger happened TO — the struck victim for
        /// onAttack, null for triggers with no second party. Deliberately separate
        /// from ItemTarget so the enemy-Def path never reads item-only fields (the
        /// exact mistake that made shield_self/apply_bleed silent no-ops on units).</summary>
        public Unit TargetUnit;

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

    /// <summary>U3b: queues one unit's abilities matching <paramref name="trigger"/>.
    /// The single entry point for every NON-death enemy trigger. onDeath/onAllyDeath
    /// keep their bespoke helper because their context is a corpse — the carrier is
    /// null, or is somebody else — which this signature cannot express.</summary>
    private void QueueAbilityTriggers(Unit carrier, string trigger, Unit target = null)
    {
        if (carrier == null || !IsInstanceValid(carrier) || carrier.Abilities == null)
            return;
        foreach (var ab in carrier.Abilities)
        {
            if (!string.Equals(ab.Trigger, trigger, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ShouldQueueTrigger(ab, carrier, target))
                continue;
            _pendingTriggers.Add(new QueuedTrigger
            {
                Def = ab,
                SourceName = carrier.Name,
                SourceTeam = carrier.TeamId,
                SourceTile = carrier.CurrentTile?.Axial ?? Vector2I.Zero,
                Carrier = carrier,
                TargetUnit = target,
            });
        }
    }

    /// <summary>Queue-time viability gate (2026-07-28, playtest PT-U3-3). An ability
    /// whose precondition ALREADY fails should never become a stack object: under R3
    /// every trigger opens a priority window, so a trigger that cannot possibly do
    /// anything costs the player a beat of attention and then silently evaporates.
    /// Playtest reported exactly that — a thornback pushing a trigger onto the stack
    /// for a distant caster it could never reach, and for a striker it could not
    /// identify at all.
    ///
    /// This does NOT replace the resolution-time check. Both exist, for different
    /// reasons: this one suppresses dead stack objects using the board as it stands
    /// now; the check inside ThornsEffect re-tests adjacency after the window closes,
    /// which is what lets the player shove the retaliator away in response and dodge
    /// the counter. Filtering here and trusting it there would break that counterplay.</summary>
    private static bool ShouldQueueTrigger(UnitAbilityDef ab, Unit carrier, Unit target)
    {
        if (string.Equals(ab.Key, "retaliate", StringComparison.OrdinalIgnoreCase))
        {
            // Needs a living, locatable striker standing next to the carrier.
            if (target == null || !IsInstanceValid(target) || !target.Stats.IsAlive)
                return false;
            if (target.CurrentTile == null || carrier.CurrentTile == null)
                return false;
            return HexGridManager.AxialDistance(target.CurrentTile.Axial,
                                                carrier.CurrentTile.Axial) <= 1;
        }

        // U3e redact: a martial has no deck and a spent hand has nothing to take.
        // Same reasoning as retaliate — a stack object that cannot possibly do
        // anything still costs the player a beat of attention under R3.
        if (string.Equals(ab.Key, "redact", StringComparison.OrdinalIgnoreCase))
        {
            if (target == null || !IsInstanceValid(target) || !target.Stats.IsAlive)
                return false;
            return target.DeckData != null && target.DeckData.Hand.Count > 0;
        }

        return true;
    }

    /// <summary>U3b: onStruck call site, wired to Unit.OnStruck at spawn. Fires only
    /// when the unit LOST HP and SURVIVED — a fully-absorbed hit is not a wound, and
    /// a fatal one is onDeath's business.</summary>
    private void HandleUnitStruck(Unit struck, int hpLoss, Unit source)
    {
        QueueAbilityTriggers(struck, "onStruck", source);

        // Riposte re-hook (2026-07-28). ResolveRetaliation was called from
        // PerformAttack / PerformRangedAttack — but the U2 intent-AI migration routed
        // every enemy attack through ExecuteIntent -> StrikeTile -> ResolveStrike, and
        // left those two methods behind. PerformAttack now has ZERO callers;
        // PerformRangedAttack has exactly one (Tinker constructs). So the player's
        // Riposte card fired only against a construct's shot and never against the
        // enemy AI — an orphaned hook, not dead code.
        //
        // Unit.OnStruck (U3b) is the correct home: it fires on ANY damage with a known
        // source, so Riposte now answers melee, ranged, spells and constructs alike,
        // through ONE call site that cannot be orphaned by a future AI refactor. Both
        // legacy calls were removed to avoid a double-fire.
        //
        // Terminates: ResolveRetaliation deals its damage with NO source, so the
        // victim's own OnStruck sees source == null and returns — no ping-pong.
        ResolveRetaliation(struck, source);
    }

    /// <summary>U3b: onSpawn call site. Fires INLINE, mirroring FireItemSpawnTriggers
    /// and §5's initial-state carve-out — at deployment there is no priority window to
    /// open and nothing to respond with. Still routes through the one dispatcher.
    /// KNOWN DIVERGENCE: a unit spawned mid-combat (Deathburst, summon_cadence) also
    /// resolves its onSpawn un-respondably. Revisit if a spawn ability ever needs to
    /// be answerable.</summary>
    private void FireEnemySpawnTriggers(Unit unit)
    {
        if (unit?.Abilities == null)
            return;
        foreach (var ab in unit.Abilities)
        {
            if (!string.Equals(ab.Trigger, "onSpawn", StringComparison.OrdinalIgnoreCase))
                continue;
            var eff = BuildTriggeredEffect(new QueuedTrigger
            {
                Def = ab, SourceName = unit.Name, SourceTeam = unit.TeamId,
                SourceTile = unit.CurrentTile?.Axial ?? Vector2I.Zero, Carrier = unit,
            });
            eff?.Resolve(State, unit.IsPlayerControlled ? Me : Opp, null, new EffectSnapshot());
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
            // U3b VERIFICATION SCAFFOLD — remove with debug_trigger_probe before ship.
            case "debug_echo":
                return new DebugEchoEffect(t.Def?.Trigger ?? "?", t.SourceName,
                                           t.TargetUnit, roundNumber, this);
            // ── U3c defensive shapes ──
            // chitin / veil are AURAS — states, not events (units doc §5). They are
            // cached on Unit at spawn and read inline in the damage path; they never
            // reach this dispatcher, and the registry exempts them from needing a case.
            case "retaliate":
                return new ThornsEffect(t.Carrier, t.TargetUnit,
                                           t.Def.GetIntParam("amount", 3), this);
            case "regrowth":
                return new RegrowthEffect(t.Carrier, t.Def.GetIntParam("threshold", 20), this);
            case "mode_shift":
                return new ModeShiftEffect(t.Carrier, t.Def.GetIntParam("threshold", 25),
                                           t.Def.GetStringParam("profile", ""), this);
            // ── U3d composition ──
            // bodyguard is an AURA (Unit.BodyguardedBy, recomputed in ApplyEnemyAuras)
            // and therefore has no case here — see auraKeys in UnitRegistry.
            case "ritual":
                return new RitualEffect(t.Carrier, t.Def.GetIntParam("amount", 1),
                                        t.Def.GetIntParam("cap", 3), this);
            case "summon_cadence":
                return new SummonCadenceEffect(t.Carrier, t.Def.GetIntParam("count", 1),
                                               t.Def.GetStringParam("unit", ""), this);
            case "field_repair":
                return new FieldRepairEffect(t.Carrier, t.Def.GetIntParam("amount", 3), this);
            // ── U3e resource denial ──
            // tithe_aura / school_grudge / action_tax / binding_geas / hand_cap are
            // AURAS and have no case here — see LivingAuraCarriers and the §5
            // states-not-events rule. Only the two EVENT-driven Axis A keys reach the
            // dispatcher.
            case "redact":
                return new RedactEffect(t.Carrier, t.TargetUnit,
                                        t.Def.GetIntParam("count", 1), this);
            case "overdraw_ward":
                return new OverdrawWardEffect(t.Carrier, t.Def.GetIntParam("n", 4), this);
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
    /// <summary>U3d: recomputes RADIUS auras on the enemy side — auras that affect
    /// OTHER units, as opposed to the U3c self-auras (chitin/veil) which are cached on
    /// the unit at spawn. Full clear-then-reassign, so a guard dying, moving, or being
    /// displaced is reflected without any teardown bookkeeping. Called at the start of
    /// each player turn and at the head of the enemy phase; cheap (O(enemies^2) over a
    /// handful of units) and idempotent, so extra calls are harmless.
    ///
    /// Bounded to ONE HOP: a unit that itself carries bodyguard is never assigned a
    /// guard, so redirection cannot chain and Unit.ApplyDamage cannot recurse.</summary>
    private void ApplyEnemyAuras()
    {
        if (enemyUnits == null || State == null)
            return;                       // called from HandleUnitDeath, which can fire
                                          // during teardown before/after combat state exists
        foreach (var u in enemyUnits)
            if (u != null && IsInstanceValid(u))
                u.BodyguardedBy = null;

        // U3e tithe_aura: a GLOBAL (unpositioned) aura, unlike bodyguard's radius.
        // Recomputed from scratch every pass — killing the warden must drop the tax
        // in the same frame the corpse hits the floor, which is why HandleUnitDeath
        // calls this too. Multiple wardens stack additively; the clamp in
        // ManaCost.EffectiveAmount is what stops that from becoming a lockout.
        int tithe = 0;
        foreach (var u in enemyUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.Abilities == null)
                continue;
            foreach (var ab in u.Abilities)
                if (string.Equals(ab.Trigger, "aura", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ab.Key, "tithe_aura", StringComparison.OrdinalIgnoreCase))
                    tithe += Math.Max(0, ab.GetIntParam("amount", 1));
        }
        if (tithe != State.PlayerSpellCostIncrease)
        {
            string msg = tithe > 0
                ? UIContent.FormatLogLine("Tithe", "Tithe", $"your spells cost +{tithe} mana")
                : UIContent.FormatLogLine("Tithe", "Tithe", "lifted — your spells cost their printed price");
            GD.Print(msg);
            AppendCombatLog(msg);
        }
        State.PlayerSpellCostIncrease = tithe;

        // U3e hand_cap (PT-U3e-2): a global aura that lowers every player unit's hand
        // ceiling while the carrier lives. It needs no machinery of its own — the two
        // existing readers of MaxHandSize do all the work, and between them they are
        // exactly the mechanic that was asked for:
        //   · DiscardOverflowCards (EndPlayerTurn) forces the excess out, oldest first
        //     — which IS the card the player has been saving
        //   · DrawToFull (StartPlayerTurn) then refills only to the lowered cap
        // Recomputed from BaseMaxHandSize rather than adjusted in place, so a dead
        // carrier restores the cap without any teardown bookkeeping.
        //
        // Floored at 1: a player holding no cards has no game, and unlike action_tax
        // there is no "stand somewhere else" answer to a global aura.
        int handCap = 0;
        foreach (var (carrier, ab) in LivingAuraCarriers("hand_cap"))
            handCap += Math.Max(0, ab.GetIntParam("amount", 1));
        if (playerUnits != null)
        {
            foreach (var pu in playerUnits)
            {
                if (pu?.DeckData == null)
                    continue;
                int want = Math.Max(1, pu.DeckData.BaseMaxHandSize - handCap);
                if (want == pu.DeckData.MaxHandSize)
                    continue;
                pu.DeckData.MaxHandSize = want;
                string hm = UIContent.FormatLogLine(pu.Name, "Hand Limit",
                    handCap > 0 ? $"capped at {want}" : $"restored to {want}",
                    $"base {pu.DeckData.BaseMaxHandSize}");
                GD.Print(hm);
                AppendCombatLog(hm);
            }
            deckManager?.RefreshDiscardFlags();   // repaint the overflow amber NOW
        }

        deckUiManager?.RefreshAffordability();   // the hand must re-read as un/affordable NOW

        foreach (var guard in enemyUnits)
        {
            if (guard == null || !IsInstanceValid(guard) || !guard.Stats.IsAlive
                || guard.CurrentTile == null || guard.Abilities == null)
                continue;
            foreach (var ab in guard.Abilities)
            {
                if (!string.Equals(ab.Trigger, "aura", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(ab.Key, "bodyguard", StringComparison.OrdinalIgnoreCase))
                    continue;
                int radius = Math.Max(1, ab.GetIntParam("radius", 1));
                foreach (var ward in enemyUnits)
                {
                    if (ward == null || ward == guard || !IsInstanceValid(ward)
                        || !ward.Stats.IsAlive || ward.CurrentTile == null)
                        continue;
                    if (ward.TeamId != guard.TeamId)
                        continue;
                    if (ward.BodyguardedBy != null)
                        continue;                       // first guard wins, deterministically
                    if (CarriesBodyguard(ward))
                        continue;                       // guards are never guarded — one hop
                    if (HexGridManager.AxialDistance(guard.CurrentTile.Axial,
                                                     ward.CurrentTile.Axial) > radius)
                        continue;
                    ward.BodyguardedBy = guard;
                }
            }
        }
    }

    /// <summary>True when this unit carries a bodyguard aura of its own.</summary>
    private static bool CarriesBodyguard(Unit u)
    {
        if (u?.Abilities == null)
            return false;
        foreach (var ab in u.Abilities)
            if (string.Equals(ab.Key, "bodyguard", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ════════════════════════════════════════════════════════════════════════
    // U3e — Axis A: resource denial. Every key below is an AURA (units doc §5:
    // states, not events), so NONE of them queue a stack object or open a
    // priority window. That is not an optimisation, it is the ruling: these fire
    // on movement and on every card cast, and a window per cast would be the
    // single worst click-fatigue regression available in this codebase (§10).
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Enumerates the living enemies carrying an aura ability with this key,
    /// yielding (carrier, def). The one place that pairing is spelled out, so the four
    /// Axis A handlers cannot drift on the "trigger must be aura" check.</summary>
    private IEnumerable<(Unit carrier, UnitAbilityDef def)> LivingAuraCarriers(string key)
    {
        if (enemyUnits == null)
            yield break;
        foreach (var u in enemyUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.Abilities == null)
                continue;
            foreach (var ab in u.Abilities)
            {
                if (!string.Equals(ab.Trigger, "aura", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(ab.Key, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return (u, ab);
            }
        }
    }

    /// <summary>U3e action_tax: player units standing inside a taxer's radius begin
    /// their turn short of action points. Called from StartPlayerTurn AFTER StartTurn,
    /// status ticks and hazard damage — everything that legitimately zeroes AP has
    /// already run, so this can only ever subtract.
    ///
    /// Two rulings, both load-bearing:
    /// - It NEVER RAISES AP. A frozen/stunned unit sits at 0 after StartTurn; a naive
    ///   `Max(1, Max - tax)` would hand it a point back and quietly cure the status.
    ///   The floor is computed from what the unit actually has, not from its maximum.
    /// - A unit that had AP keeps AT LEAST ONE. Movement costs AP, so taxing a unit to
    ///   zero would trap it inside the radius with no way to answer the aura — and
    ///   "stand somewhere else" is the entire counterplay this key sells. A full-turn
    ///   lockout from a passive aura is a stun, and stuns are cards, not weather.</summary>
    private void ApplyEnemyActionTax()
    {
        if (playerUnits == null || enemyUnits == null)
            return;
        foreach (var victim in playerUnits)
        {
            if (victim == null || !IsInstanceValid(victim) || !victim.Stats.IsAlive
                || victim.CurrentTile == null)
                continue;

            int tax = 0; string taxerName = null;
            foreach (var (carrier, ab) in LivingAuraCarriers("action_tax"))
            {
                if (carrier.CurrentTile == null)
                    continue;
                int radius = Math.Max(1, ab.GetIntParam("radius", 2));
                if (HexGridManager.AxialDistance(carrier.CurrentTile.Axial,
                                                 victim.CurrentTile.Axial) > radius)
                    continue;
                tax += Math.Max(0, ab.GetIntParam("amount", 1));
                taxerName ??= carrier.Name;
            }
            if (tax <= 0 || victim.CurrentActionPoints <= 0)
                continue;

            int floor = 1;                       // it had AP, so it keeps a way out
            int before = victim.CurrentActionPoints;
            victim.CurrentActionPoints = Math.Max(floor, before - tax);
            int lost = before - victim.CurrentActionPoints;
            if (lost <= 0)
                continue;                    // already at the floor — nothing to report
            victim.RefreshHealthBar();
            string msg = UIContent.FormatLogLine(taxerName ?? "Aura", "Action Tax",
                $"-{lost} AP to {victim.Name}", $"{victim.CurrentActionPoints}/{victim.MaxActionPoints}");
            GD.Print(msg);
            AppendCombatLog(msg);
        }
    }

    /// <summary>U3e binding_geas: a player unit that walks takes damage on arrival.
    /// Wired to Unit.OnMoved at spawn — once per COMMITTED move, i.e. once per AP
    /// spent, which for a 3-AP martial crossing the board is three ticks. That is the
    /// intended read ("stand and fight"), and it is also why the authored amount must
    /// stay small; see the tuning note on debug_geas.json.
    ///
    /// Enemy movement is exempt: an aura that damaged its own side every step would
    /// make the AI suicide into it, and the key is a player-facing tax by definition.
    /// Radius comes from the CARRIER'S tile at arrival time, so stepping out of the
    /// field on the last point of AP genuinely escapes the last tick.</summary>
    private void HandleUnitMoved(Unit mover)
    {
        if (CombatSim.Active)
            return;                       // preview runs mutate nothing (R22)
        if (mover == null || !IsInstanceValid(mover) || !mover.Stats.IsAlive
            || !mover.IsPlayerControlled || mover.CurrentTile == null)
            return;

        foreach (var (carrier, ab) in LivingAuraCarriers("binding_geas"))
        {
            if (carrier.CurrentTile == null)
                continue;
            int radius = ab.GetIntParam("radius", 0);
            if (radius > 0 && HexGridManager.AxialDistance(carrier.CurrentTile.Axial,
                                                           mover.CurrentTile.Axial) > radius)
                continue;                 // radius 0 (the default) = board-wide
            int amount = Math.Max(0, ab.GetIntParam("amount", 2));
            if (amount <= 0)
            {
                GD.Print($"[Geas] {carrier.Name} has amount 0 — authored as a no-op.");
                continue;
            }
            string msg = UIContent.FormatLogLine(carrier.Name, "Binding Geas",
                $"{amount} damage → {mover.Name}", "moved");
            GD.Print(msg);
            AppendCombatLog(msg);
            // Sourced to the CARRIER so veil/retaliate/thorns all read it correctly,
            // and so a geas tick can never be credited to whoever happens to be
            // mid-resolution (the AmbientDamageSource trap from U3c).
            mover.ApplyDamage(amount, carrier);
            if (!mover.Stats.IsAlive)
                break;                    // walked into a grave; stop taxing the corpse
        }
    }

    /// <summary>U3e school_grudge: the carrier grows permanently stronger every time
    /// the player casts a half of the named school. The Gremlin Nob — it makes ONE
    /// school actively bad for ONE fight, which is the first thing in this game that
    /// gives attunement a downside.
    ///
    /// Reads CardHalf.School, NOT Card.School: CardRuntime notes a half may belong to
    /// a different school than its parent card, and the HALF is what was cast. Driven
    /// off the "AbilityCast" bus event, which fires at PUSH time — so the grudge lands
    /// before the spell resolves and the player sees the cause and the effect in the
    /// same beat.
    ///
    /// Deliberately NOT a stack object. Under R3 a queued trigger opens a priority
    /// window; this fires on every single card the player plays, and a window per card
    /// would break the anti-click-fatigue rule so badly it would end the phase.</summary>
    private void ApplySchoolGrudge(StackItem item)
    {
        if (item?.Ability is not CardHalf half)
            return;                       // enemy triggers and non-card abilities: nothing to resent
        if (item.CasterUnit == null || !IsInstanceValid(item.CasterUnit)
            || !item.CasterUnit.IsPlayerControlled)
        {
            // Not silent: a player cast that arrives with no CasterUnit means some
            // cast path forgot to set it, and "the grudge is broken" would otherwise
            // be indistinguishable from "nobody cast anything" (U3c lesson 3).
            if (item.Caster == Me)
                GD.Print($"[Grudge] '{half.Name}' cast with no CasterUnit — grudge cannot attribute it.");
            return;
        }

        foreach (var (carrier, ab) in LivingAuraCarriers("school_grudge"))
        {
            string want = ab.GetStringParam("school", "");
            CardSchool school;

            // U3e revision (PT-U3e-3): the authored value may be the literal "player",
            // meaning "whichever school the CASTING UNIT belongs to". A fixed school is
            // either always-on or never-on depending on the run — most decks are one
            // school with splashes — so a fixed grudge is a coin flip made at authoring
            // time. Resolved per cast, it is always live and always answerable.
            //
            // Against the CASTER's school, not the wizard's: a splashed half from
            // another school does not feed the grudge, so playing your off-school cards
            // is the counterplay. That is the whole design — it makes ONE school bad for
            // ONE fight, which is the first downside attunement has ever had.
            if (string.Equals(want, "player", StringComparison.OrdinalIgnoreCase))
            {
                school = item.CasterUnit.School;
            }
            else if (!Enum.TryParse<CardSchool>(want, ignoreCase: true, out school))
            {
                GD.PrintErr($"[Grudge] {carrier.Name}: '{want}' is not a CardSchool — grudge never fires.");
                continue;
            }
            if (half.School != school)
                continue;
            int amount = Math.Max(0, ab.GetIntParam("amount", 2));
            carrier.AttackDamage += amount;
            int stacks = carrier.AbilityUseCounts.TryGetValue("school_grudge", out var n) ? n + 1 : 1;
            carrier.AbilityUseCounts["school_grudge"] = stacks;
            string msg = UIContent.FormatLogLine(carrier.Name, "Grudge",
                $"+{amount} damage — you cast {school}",
                $"{stacks} stack{(stacks == 1 ? "" : "s")}, now {carrier.AttackDamage}");
            GD.Print(msg);
            AppendCombatLog(msg);
        }
        RefreshEnemyRoster();
    }

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

            // (2026-07-28, PT-U3e-4) HARD BAIL: priority is priority OVER SOMETHING.
            // If the stack empties underneath this window there is nothing left to
            // respond to, and holding the window is a deadlock — the loop exits only
            // on _priorityPassed, and with a stop set the auto-close branch below can
            // never fire. RunEnemyTurn then awaits a drain that never returns and the
            // phase banner hangs forever.
            //
            // The reported repro was pressing Enter/Space during a trigger window,
            // which reached the DEBUG ResolveTop() and drained the stack out from
            // under the loop. That input path is now blocked at the source as well —
            // both fixes ship, because "no input can empty the stack" is a claim about
            // every future call site and this is a claim about this loop.
            if (State.Stack.IsEmpty)
            {
                GD.Print($"[Priority] window closed on {topName} — the stack emptied underneath it.");
                break;
            }

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

    /// <summary>U3e: repaints the hand after something OTHER than the player changed
    /// it. DeckManager's own discard path drives `_activeDeck` — the deck of whoever
    /// is selected — so calling DiscardCard for a non-selected unit would take the
    /// card from the WRONG hand. Redact therefore mutates UnitDeckData directly and
    /// calls this, which repaints only when the victim is the unit actually on
    /// screen; every other unit's hand is redrawn by SelectUnit when the player
    /// switches to it.</summary>
    internal void RefreshHandFor(Unit victim)
    {
        if (victim == null || victim != selectedUnit)
            return;
        deckUiManager?.SafeRefreshUI();
        RefreshDeckCounts();
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

/// <summary>Ritual (U3d): every living ally gains +N attack damage, cumulatively,
/// each time this fires. The Cultist — it makes kill PRIORITY a real decision instead
/// of "hit the nearest thing". CAPPED (spec §9 open decision 2): uncapped, a six-round
/// siege ends at +6 on every enemy, and it compounds with requiem, which already
/// stacks. The cap is reported in the log line so the player can see the ceiling.</summary>
public sealed class RitualEffect : IEffect
{
    private readonly Unit _carrier;
    private readonly int _amount, _cap;
    private readonly CombatManager _cm;
    public RitualEffect(Unit carrier, int amount, int cap, CombatManager cm)
    { _carrier = carrier; _amount = amount; _cap = cap; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Buff" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive)
            return;
        int given = _carrier.AbilityUseCounts.TryGetValue("ritual", out var n) ? n : 0;
        if (given >= _cap)
        {
            s.Log($"[Ritual] {_carrier.Name} is at its ceiling (+{given}) — no further escalation.");
            return;
        }
        int step = Math.Min(_amount, _cap - given);
        int buffed = 0;
        foreach (var u in s.UnitsInPlay)
        {
            if (u == null || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive)
                continue;
            if (u.TeamId != _carrier.TeamId)
                continue;
            u.AttackDamage += step;
            buffed++;
        }
        _carrier.AbilityUseCounts["ritual"] = given + step;
        string msg = UIContent.FormatLogLine(_carrier.Name, "Ritual",
            $"+{step} damage to {buffed} all{(buffed == 1 ? "y" : "ies")}",
            $"{given + step}/{_cap} total");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>Summon Cadence (U3d): spawns on a CLOCK rather than on death. This is
/// deathburst's missing half — deathburst only fires once the unit is already dead, so
/// the player never sees pressure building and cannot choose to race it. A telegraphed
/// cadence is a decision; a posthumous surprise is not. Reuses the same summon seam.</summary>
public sealed class SummonCadenceEffect : IEffect
{
    private readonly Unit _carrier;
    private readonly int _count;
    private readonly string _unitId;
    private readonly CombatManager _cm;
    public SummonCadenceEffect(Unit carrier, int count, string unitId, CombatManager cm)
    { _carrier = carrier; _count = count; _unitId = unitId; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Summon" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive
            || _carrier.CurrentTile == null)
            return;
        if (s.OnSummonRequested == null || s.Grid == null || string.IsNullOrEmpty(_unitId))
        {
            s.Log("[SummonCadence] no summon seam or no unit id — no effect.");
            return;
        }
        int spawned = 0;
        foreach (var neighbor in s.Grid.GetNeighbors(_carrier.CurrentTile.Axial))
        {
            if (spawned >= _count)
                break;
            var td = s.Grid.GetTile(neighbor);
            if (td == null || !td.IsWalkable || td.IsBlocked || td.IsOccupied)
                continue;
            if (s.OnSummonRequested(_unitId, td, _carrier.TeamId) != null)
                spawned++;
        }
        string msg = spawned > 0
            ? UIContent.FormatLogLine(_carrier.Name, "Assembly", $"{spawned} more come online")
            : UIContent.FormatLogLine(_carrier.Name, "Assembly", "no room to deploy — nothing arrives");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>Field Repair (U3d): armour to the most-wounded living ally on a cadence.
/// Generalises the `forge` branch of ApplyCasterRider out of the caster path and into
/// the key catalog, so a non-caster can carry the Tinker's verb.</summary>
public sealed class FieldRepairEffect : IEffect
{
    private readonly Unit _carrier;
    private readonly int _amount;
    private readonly CombatManager _cm;
    public FieldRepairEffect(Unit carrier, int amount, CombatManager cm)
    { _carrier = carrier; _amount = amount; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Buff" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive)
            return;
        Unit best = null; float worst = -1f;
        foreach (var u in s.UnitsInPlay)
        {
            if (u == null || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive)
                continue;
            if (u.TeamId != _carrier.TeamId || u.Stats.MaxHealth <= 0)
                continue;
            float missing = 1f - (float)u.Stats.Health / u.Stats.MaxHealth;
            if (missing > worst) { worst = missing; best = u; }
        }
        if (best == null || worst <= 0f)
        {
            s.Log($"[FieldRepair] {_carrier.Name} finds nothing to patch.");
            return;
        }
        best.Stats.Armor += _amount;
        best.RefreshHealthBar();
        string msg = UIContent.FormatLogLine(_carrier.Name, "Field Repair",
            $"+{_amount} armour → {best.Name}");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>Thorns — the enemy `retaliate` ability key (U3c). Named ThornsEffect,
/// NOT RetaliateEffect: Effect.cs already owns that name for the player's Riposte
/// card, which arms Unit.RetaliateDamage. The two are separate mechanics today —
/// see the note on ResolveRetaliation. A unit that answers being hit in melee. Fires on
/// onStruck; the striker is carried as the trigger's TargetUnit. Adjacency is
/// re-checked at RESOLUTION, not at queue time — displacement in response (the
/// Enchanter's whole kit) legitimately dodges the counter, which is the counterplay.
/// Damage is dealt with no source, so two retaliators cannot ping-pong forever.</summary>
public sealed class ThornsEffect : IEffect
{
    private readonly Unit _carrier, _striker;
    private readonly int _amount;
    private readonly CombatManager _cm;
    public ThornsEffect(Unit carrier, Unit striker, int amount, CombatManager cm)
    { _carrier = carrier; _striker = striker; _amount = amount; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Damage" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive)
            return;
        if (_striker == null || !GodotObject.IsInstanceValid(_striker) || !_striker.Stats.IsAlive)
        {
            // Was a SILENT return until 2026-07-28. A null striker means the damage
            // path did not name its source (DoT, terrain — or a call site that has not
            // been taught to pass one), and saying nothing made that indistinguishable
            // from "the ability is broken". It cost a playtest cycle to find.
            s.Log("[Retaliate] no identifiable striker — nothing to answer.");
            return;
        }
        if (_striker.CurrentTile == null || _carrier.CurrentTile == null)
            return;
        if (HexGridManager.AxialDistance(_striker.CurrentTile.Axial, _carrier.CurrentTile.Axial) > 1)
        {
            s.Log($"[Retaliate] {_striker.Name} struck from outside reach — no answer.");
            return;
        }
        string msg = UIContent.FormatLogLine(_carrier.Name, "Retaliate",
            $"{_amount} damage → {_striker.Name}");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
        _striker.ApplyDamage(_amount);    // no source: retaliation cannot be retaliated
    }
}

/// <summary>Regrowth (U3c): heals to FULL at the end of its action unless it took at
/// least `threshold` HP of damage this round. Punishes spreading damage across the
/// board and rewards committing to one target — the opposite lesson from chitin.</summary>
public sealed class RegrowthEffect : IEffect
{
    private readonly Unit _carrier;
    private readonly int _threshold;
    private readonly CombatManager _cm;
    public RegrowthEffect(Unit carrier, int threshold, CombatManager cm)
    { _carrier = carrier; _threshold = threshold; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Heal" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive)
            return;
        if (_carrier.DamageTakenThisRound >= _threshold)
        {
            string held = UIContent.FormatLogLine(_carrier.Name, "Regrowth",
                "cut too deep to close", $"{_carrier.DamageTakenThisRound}/{_threshold} this round");
            GD.Print(held);
            _cm?.AppendCombatLog(held);
            return;
        }
        int missing = _carrier.Stats.MaxHealth - _carrier.Stats.Health;
        if (missing <= 0)
            return;
        _carrier.Stats.Health = _carrier.Stats.MaxHealth;
        _carrier.RefreshHealthBar();
        string msg = UIContent.FormatLogLine(_carrier.Name, "Regrowth",
            $"knits shut (+{missing} HP)", $"needed {_threshold} in one round");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>Mode Shift (U3c): once cumulative combat damage crosses `threshold`, the
/// unit adopts another UnitDefinition's profile — stats, behaviour key and tags. Once
/// per combat. The swap is QUEUED here and applied at the head of the unit's next
/// activation (CombatManager.EnemyIntents), never mid-turn: §7a P2 says the UI states
/// rules and never lies about the turn in progress, and a unit that transformed
/// between telegraph and execution would be exactly that lie.</summary>
public sealed class ModeShiftEffect : IEffect
{
    private readonly Unit _carrier;
    private readonly int _threshold;
    private readonly string _profile;
    private readonly CombatManager _cm;
    public ModeShiftEffect(Unit carrier, int threshold, string profile, CombatManager cm)
    { _carrier = carrier; _threshold = threshold; _profile = profile; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Buff" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive)
            return;
        if (_carrier.HasModeShifted || !string.IsNullOrEmpty(_carrier.PendingProfileId))
            return;                                   // already shifted, or already armed
        if (_carrier.DamageTakenThisCombat < _threshold)
            return;
        if (string.IsNullOrEmpty(_profile) || UnitRegistry.Get(_profile) == null)
        {
            GD.PrintErr($"[ModeShift] {_carrier.Name}: profile '{_profile}' does not resolve — no shift.");
            return;
        }
        _carrier.PendingProfileId = _profile;
        string msg = UIContent.FormatLogLine(_carrier.Name, "Mode Shift",
            "something gives way", $"{_carrier.DamageTakenThisCombat}/{_threshold} — it changes next turn");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>Redact (U3e): the struck unit loses N random cards from its HAND. The
/// Censor — the only key in the game that attacks a decision the player has already
/// made rather than a resource they were going to spend.
///
/// Per-unit decks make this naturally targeted: it takes from whoever was hit, not
/// from a party-wide pool, so who you leave in reach is the counterplay.
///
/// Does NOT go through DeckManager.DiscardCard. That method operates on `_activeDeck`
/// — the SELECTED unit's deck — so calling it for an unselected victim would silently
/// discard from the wrong hand. UnitDeckData.Discard is the per-unit primitive; the
/// UI is repainted through CombatManager.RefreshHandFor, which no-ops unless the
/// victim is the unit currently on screen.
///
/// Ruling 2026-07-28 (spec §10 flagged this as the first key to cut): SHIPPED, at
/// count 1, elite-gated.
///
/// REVISED after playtest PT-U3e-2: the card is EXILED, not discarded. As a discard it
/// was mechanically free — the hand refills to MaxHandSize at every turn start, so the
/// player lost a card of *selection* and zero tempo, and the card came back in two
/// shuffles anyway. Exiled, it is attrition against a 10-card deck: the draw pile the
/// player will cycle through for the rest of the fight is permanently one card thinner,
/// and the one it lost is the one they were holding on purpose.
///
/// The tempo half of hand denial is now a SEPARATE key — `hand_cap` — because the two
/// are different mechanics that happened to share a name.</summary>
public sealed class RedactEffect : IEffect
{
    private readonly Unit _carrier, _victim;
    private readonly int _count;
    private readonly CombatManager _cm;
    private static readonly Random Rng = new();

    public RedactEffect(Unit carrier, Unit victim, int count, CombatManager cm)
    { _carrier = carrier; _victim = victim; _count = count; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Debuff" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        string who = _carrier != null && GodotObject.IsInstanceValid(_carrier) ? _carrier.Name : "Redact";

        // Every early return says why. The queue-time gate already suppresses the
        // hopeless cases; anything reaching here and doing nothing means the board
        // changed inside the priority window, and that must be legible (U3c lesson 3).
        if (_victim == null || !GodotObject.IsInstanceValid(_victim) || !_victim.Stats.IsAlive)
        {
            s.Log($"[Redact] {who}: the struck unit is gone — nothing to take.");
            return;
        }
        if (_victim.DeckData == null)
        {
            s.Log($"[Redact] {who}: {_victim.Name} is a martial and holds no cards.");
            return;
        }
        var hand = _victim.DeckData.Hand;
        if (hand.Count == 0)
        {
            s.Log($"[Redact] {who}: {_victim.Name}'s hand is already empty.");
            return;
        }

        int taken = 0;
        var names = new List<string>();
        for (int i = 0; i < _count && hand.Count > 0; i++)
        {
            var card = hand[Rng.Next(hand.Count)];
            names.Add(card.CardName ?? card.TopHalf?.Name ?? "a card");
            if (!_victim.DeckData.ExileFromHand(card))
            {
                s.Log($"[Redact] {who}: '{card.CardName}' was not in hand — nothing taken.");
                break;
            }
            taken++;
        }
        _cm?.RefreshHandFor(_victim);

        string msg = UIContent.FormatLogLine(who, "Redact",
            $"{_victim.Name} loses {string.Join(", ", names)} — burned out of the deck",
            $"{hand.Count} in hand, {_victim.DeckData.TotalCards} cards left");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>Overdraw Ward (U3e): if the player played N or more cards this round, the
/// ward takes a SECOND activation next round. The Time Eater — it prices the burst
/// turn, which is the one turn structure this game otherwise never punishes.
///
/// Reads GameState.SpellsCastThisTurn, which already exists and already resets at
/// player-turn start; no new counter. Fires at onTurnEnd during the enemy phase, so
/// the count it reads is the player turn that just finished — exactly "this round".
///
/// Arms a flag rather than acting: the extra activation is a whole turn's worth of
/// threat and must be TELEGRAPHED, not sprung. The under-threshold case logs too —
/// "three of four" is the information that makes the fourth card a decision.</summary>
public sealed class OverdrawWardEffect : IEffect
{
    private readonly Unit _carrier;
    private readonly int _n;
    private readonly CombatManager _cm;
    public OverdrawWardEffect(Unit carrier, int n, CombatManager cm)
    { _carrier = carrier; _n = n; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Buff" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_carrier == null || !GodotObject.IsInstanceValid(_carrier) || !_carrier.Stats.IsAlive)
            return;
        int cast = s.SpellsCastThisTurn;
        if (cast < _n)
        {
            string under = UIContent.FormatLogLine(_carrier.Name, "Overdraw Ward",
                "the hour holds", $"{cast}/{_n} cards");
            GD.Print(under);
            _cm?.AppendCombatLog(under);
            return;
        }
        if (_carrier.ExtraActivationPending)
            return;                                   // already armed; do not stack
        _carrier.ExtraActivationPending = true;
        string msg = UIContent.FormatLogLine(_carrier.Name, "Overdraw Ward",
            "you spent too much time — it acts twice next round", $"{cast}/{_n} cards");
        GD.Print(msg);
        _cm?.AppendCombatLog(msg);
    }
}

/// <summary>U3b VERIFICATION SCAFFOLD — remove with the debug_trigger_probe unit.
/// Announces that a trigger fired, with its round and victim. This is the phase's
/// exit criterion made executable: author one echo per trigger, play one fight, read
/// the log. A trigger that fires twice, at the wrong moment, or not at all is visible
/// immediately instead of surfacing as a mystery in some later ability.</summary>
public sealed class DebugEchoEffect : IEffect
{
    private readonly string _trigger, _sourceName;
    private readonly Unit _target;
    private readonly int _round;
    private readonly CombatManager _cm;
    public DebugEchoEffect(string trigger, string sourceName, Unit target, int round, CombatManager cm)
    { _trigger = trigger; _sourceName = sourceName; _target = target; _round = round; _cm = cm; }

    public string[] Tags { get; private set; } = { "Ability", "Debug" };
    public IEffect WithTag(string tag) { Tags = new[] { tag }; return this; }
    public IEnumerable<IEffect> Children => Array.Empty<IEffect>();

    public void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        string tgt = _target != null && GodotObject.IsInstanceValid(_target) ? $" -> {_target.Name}" : "";
        string msg = UIContent.FormatLogLine(_sourceName, "Echo", $"{_trigger} fired{tgt}", $"round {_round}");
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
