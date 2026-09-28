using System.Collections.Generic;
using Godot;

// ============================================================
// EdgeRules.cs
//
// Purpose:        The Edge resource for martial units (Martial
//                 Maneuvers & Edge spec v1, sections 3 and 5).
//                 Stances earn Edge through their EdgeTrigger;
//                 maneuvers (M2+) spend it. All gain, spend, cap,
//                 decay and trigger rules live here so balance
//                 has exactly one home.
// Layer:          Rules
// Collaborators:  Unit.cs (Edge state fields),
//                 StanceDefinition.cs (EdgeTrigger, EdgeTriggerValue,
//                 EdgeTriggerStatus, EdgeStartBonus),
//                 CombatManager.cs (combat start, turn start/end,
//                 enemy activation counter, and
//                 ResolveMartialAttack, the martial attack resolver)
//
// The decay window
// ----------------
// Idle decay asks "did this unit gain or spend Edge since the end
// of its previous turn?". The window therefore closes at the END
// of the unit's own turn, not at turn start. That way Edge earned
// during the enemy phase (Defensive, Guardian) counts: a Defensive
// fighter who was struck three times and then passes still keeps
// its Edge. The per-round gain cap uses the same window.
//
// A trigger that fires while the unit is capped (or after the
// per-round cap is spent) still counts as activity: it marks the
// window through EdgeTriggerFiredThisWindow, so a capped Reckless
// fighter does not oscillate 5, 4, 5 on a trigger that keeps firing.
//
// Per-turn bookkeeping (first-hit claim, momentum) resets at the
// unit's turn start. Every per-turn flag set here is reset here,
// per the 2026-08-05 turn-flag audit.
// ============================================================

/// <summary>Action categories for the Skirmish momentum trigger.</summary>
public enum EdgeActionKind
{
    None,
    Move,
    Attack,
    Maneuver,
    Item,
    StanceSwitch,
}

public static class EdgeRules
{
    // ── Tuning (spec §3, launch values) ──────────────────────────────────
    public const int BaseCap = 5;
    public const int CapAtTrainingTier3 = 6;
    public const int StartingEdge = 1;
    public const int MaxGainPerRound = 3;
    public const int HonedThreshold = 3;       // used from M2 (weapon-class riders)
    public const int MomentumMaxGainsPerTurn = 2;
    public const int VigilantMaxPerTurn = 2;

    /// <summary>True for units that run the Edge economy: classed martials only (R8).</summary>
    public static bool UsesEdge(Unit u)
        => u != null && u.IsMartial && u.MartialClass != MartialClass.None;

    public static bool IsHoned(Unit u)
        => UsesEdge(u) && u.Edge >= HonedThreshold;

    // ── Lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    /// Call once per unit at combat start, after ActiveStance is set.
    /// Edge never carries between fights.
    /// </summary>
    public static void ResetForCombat(Unit u, int trainingGroundsTier)
    {
        if (u == null)
            return;

        ClearWindow(u);
        ClearTurnBookkeeping(u);
        u.EdgeStruckClaimedActivation = -1;
        u.EdgeGuardClaimedActivation = -1;

        if (!UsesEdge(u))
        {
            u.Edge = 0;
            u.MaxEdge = 0;
            return;
        }

        u.MaxEdge = trainingGroundsTier >= 3 ? CapAtTrainingTier3 : BaseCap;
        int start = StartingEdge + (u.ActiveStance?.EdgeStartBonus ?? 0);
        u.Edge = Mathf.Clamp(start, 0, u.MaxEdge);
    }

    /// <summary>
    /// Call at the start of each player turn for every friendly unit,
    /// beside the existing HasSwitchedStanceThisTurn reset.
    /// Resets per-turn bookkeeping, then fires turn-start triggers.
    /// </summary>
    public static void OnTurnStart(Unit u)
    {
        if (u == null)
            return;

        ClearTurnBookkeeping(u);
        if (!UsesEdge(u) || u.ActiveStance == null)
            return;

        var s = u.ActiveStance;
        switch (s.EdgeTrigger)
        {
            case EdgeTrigger.TurnStart:
                TryGain(u, s.EdgeTriggerValue, "turn start");
                break;
            case EdgeTrigger.TurnStartBelowHalfHp:
                if (u.Stats.Health * 2 < u.Stats.MaxHealth)
                    TryGain(u, s.EdgeTriggerValue, "below half HP");
                break;
        }
    }

    /// <summary>
    /// Call at the end of the player turn for every friendly unit,
    /// BEFORE the enemy phase starts. Fires end-of-turn triggers,
    /// then applies idle decay and closes the window.
    /// </summary>
    public static void OnTurnEnd(Unit u, int counteredIntents = 0)
    {
        if (u == null)
            return;

        if (UsesEdge(u) && u.ActiveStance != null
            && u.ActiveStance.EdgeTrigger == EdgeTrigger.TurnEndNoMove
            && u.TilesMovedThisTurn == 0)
        {
            TryGain(u, u.ActiveStance.EdgeTriggerValue, "held position");
        }

        // Vigilant (M5): the caller counts the visible intents this unit stands
        // ready to counter; at most two pay.
        if (UsesEdge(u) && u.ActiveStance != null
            && u.ActiveStance.EdgeTrigger == EdgeTrigger.TurnEndCounteredIntents
            && counteredIntents > 0)
        {
            int n = Mathf.Min(VigilantMaxPerTurn, counteredIntents);
            TryGain(u, n * u.ActiveStance.EdgeTriggerValue, $"countering {n} intent(s)");
        }

        if (UsesEdge(u)
            && u.EdgeGainedThisWindow == 0
            && u.EdgeSpentThisWindow == 0
            && !u.EdgeTriggerFiredThisWindow
            && u.Edge > 0)
        {
            u.Edge -= 1;
            GD.Print($"[Edge] {u.Name} loses 1 Edge (idle) → {u.Edge}/{u.MaxEdge}");
            u.RefreshHealthBar();
        }

        ClearWindow(u);
    }

    // ── Core gain / spend ────────────────────────────────────────────────

    /// <summary>
    /// Adds Edge, respecting the cap and the per-round gain cap.
    /// Returns the amount actually gained.
    /// </summary>
    public static int TryGain(Unit u, int amount, string reason)
    {
        if (!UsesEdge(u) || amount <= 0)
            return 0;

        u.EdgeTriggerFiredThisWindow = true;   // activity, even if clamped to 0 below

        int roundRoom = MaxGainPerRound - u.EdgeGainedThisWindow;
        int capRoom = u.MaxEdge - u.Edge;
        int gained = Mathf.Min(amount, Mathf.Min(roundRoom, capRoom));
        if (gained <= 0)
            return 0;

        u.Edge += gained;
        u.EdgeGainedThisWindow += gained;
        GD.Print($"[Edge] {u.Name} +{gained} ({reason}) → {u.Edge}/{u.MaxEdge}");
        u.RefreshHealthBar();
        return gained;
    }

    /// <summary>Spends Edge if the unit has enough. Returns false and spends nothing otherwise.</summary>
    public static bool TrySpend(Unit u, int amount)
    {
        if (!UsesEdge(u) || amount < 0 || u.Edge < amount)
            return false;
        if (amount == 0)
            return true;

        u.Edge -= amount;
        u.EdgeSpentThisWindow += amount;
        GD.Print($"[Edge] {u.Name} spends {amount} → {u.Edge}/{u.MaxEdge}");
        u.RefreshHealthBar();
        return true;
    }

    /// <summary>
    /// Returns Edge without counting as a gain (spec §7a rule 2: an unfired
    /// reaction's refund does not prevent idle decay). Used from M4.
    /// </summary>
    public static void Refund(Unit u, int amount)
    {
        if (!UsesEdge(u) || amount <= 0)
            return;
        u.Edge = Mathf.Min(u.MaxEdge, u.Edge + amount);
        GD.Print($"[Edge] {u.Name} refunded {amount} → {u.Edge}/{u.MaxEdge}");
        u.RefreshHealthBar();
    }

    // ── Trigger hooks (called by the resolver / CombatManager) ───────────

    /// <summary>
    /// Call once per martial attack action, after targets are chosen and
    /// BEFORE damage and on-hit statuses are applied, so "undamaged" and
    /// "already has status" read the pre-hit state.
    /// </summary>
    public static void OnAttackHits(Unit attacker, IReadOnlyList<Unit> targetsHit, bool placeOpenings = true)
    {
        if (!UsesEdge(attacker) || attacker.ActiveStance == null
            || targetsHit == null || targetsHit.Count == 0)
            return;

        var s = attacker.ActiveStance;
        switch (s.EdgeTrigger)
        {
            case EdgeTrigger.FirstHitEachTurn:
                if (!attacker.EdgeFirstHitClaimedThisTurn)
                {
                    attacker.EdgeFirstHitClaimedThisTurn = true;
                    TryGain(attacker, s.EdgeTriggerValue, "first hit");
                }
                break;

            case EdgeTrigger.PerExtraTargetHit:
                if (targetsHit.Count > 1)
                    TryGain(attacker, (targetsHit.Count - 1) * s.EdgeTriggerValue, "multi-hit");
                break;

            case EdgeTrigger.HitUndamagedTarget:
                // Approximation: full HP stands in for "undamaged this combat".
                // A healed-to-full target counts; accepted for launch.
                foreach (var t in targetsHit)
                {
                    if (t != null && t.Stats.Health >= t.Stats.MaxHealth)
                    {
                        TryGain(attacker, s.EdgeTriggerValue, "fresh target");
                        break;
                    }
                }
                break;

            case EdgeTrigger.Openings:
                // Opportunist (M5): no Edge here. Each target struck carries one
                // more Opening; maneuvers against it spend them as Edge. A maneuver
                // that just SPENT Openings places none (placeOpenings false), or a
                // 1-cost maneuver would refund itself forever.
                if (!placeOpenings)
                    break;
                foreach (var t in targetsHit)
                {
                    if (t == null || t.IsPlayerControlled || !t.Stats.IsAlive)
                        continue;
                    t.Openings += s.EdgeTriggerValue;
                    GD.Print($"[Openings] {t.Name} now carries {t.Openings}.");
                }
                break;

            case EdgeTrigger.HitStatusedTarget:
                if (string.IsNullOrEmpty(s.EdgeTriggerStatus))
                    break;
                foreach (var t in targetsHit)
                {
                    if (t != null && t.Stats.StatusEffects.ContainsKey(s.EdgeTriggerStatus))
                    {
                        TryGain(attacker, s.EdgeTriggerValue, $"hit {s.EdgeTriggerStatus} target");
                        break;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Call when an enemy attack damages a friendly unit. activationId is
    /// CombatManager's enemy-activation counter; Defensive gains at most
    /// once per enemy activation.
    /// </summary>
    public static void OnStruck(Unit defender, int activationId)
    {
        if (!UsesEdge(defender) || defender.ActiveStance == null)
            return;
        if (defender.ActiveStance.EdgeTrigger != EdgeTrigger.WhenStruck)
            return;
        if (defender.EdgeStruckClaimedActivation == activationId)
            return;

        defender.EdgeStruckClaimedActivation = activationId;
        TryGain(defender, defender.ActiveStance.EdgeTriggerValue, "struck");
    }

    /// <summary>
    /// Call when an enemy attack targets a friendly unit, passing the friendly
    /// units adjacent to that target (the caller owns hex adjacency).
    /// Guardian gains at most once per enemy activation.
    /// </summary>
    public static void OnAllyAttacked(Unit attackedAlly, IEnumerable<Unit> adjacentFriendlies, int activationId)
    {
        if (adjacentFriendlies == null)
            return;

        foreach (var g in adjacentFriendlies)
        {
            if (g == null || g == attackedAlly || !UsesEdge(g) || g.ActiveStance == null)
                continue;
            if (g.ActiveStance.EdgeTrigger != EdgeTrigger.AdjacentAllyAttacked)
                continue;
            if (g.EdgeGuardClaimedActivation == activationId)
                continue;

            g.EdgeGuardClaimedActivation = activationId;
            TryGain(g, g.ActiveStance.EdgeTriggerValue, $"guarding {attackedAlly?.Name}");
        }
    }

    /// <summary>
    /// Call after any friendly attack lands on a target that carried "marked"
    /// before the hit. Every OTHER friendly martial in a Marked-trigger stance
    /// gains. Marker ownership is not tracked; any Marked-stance ally benefits.
    /// </summary>
    public static void OnAllyHitMarkedTarget(Unit hitter, IEnumerable<Unit> friendlies)
    {
        if (friendlies == null)
            return;

        foreach (var m in friendlies)
        {
            if (m == null || m == hitter || !UsesEdge(m) || m.ActiveStance == null)
                continue;
            if (m.ActiveStance.EdgeTrigger != EdgeTrigger.AllyHitsMarkedTarget)
                continue;

            TryGain(m, m.ActiveStance.EdgeTriggerValue, "ally hit marked target");
        }
    }

    /// <summary>
    /// Call once per player-issued action (one move command, one attack, one
    /// stance switch...). Drives the Skirmish momentum trigger.
    /// </summary>
    public static void OnAction(Unit u, EdgeActionKind kind)
    {
        if (u == null || kind == EdgeActionKind.None)
            return;

        if (UsesEdge(u) && u.ActiveStance != null
            && u.ActiveStance.EdgeTrigger == EdgeTrigger.Momentum
            && u.EdgeLastActionKind != EdgeActionKind.None
            && u.EdgeLastActionKind != kind
            && u.EdgeMomentumGainsThisTurn < MomentumMaxGainsPerTurn)
        {
            if (TryGain(u, u.ActiveStance.EdgeTriggerValue, "momentum") > 0)
                u.EdgeMomentumGainsThisTurn++;
        }

        u.EdgeLastActionKind = kind;
    }

    // ── Internals ────────────────────────────────────────────────────────

    private static void ClearWindow(Unit u)
    {
        u.EdgeGainedThisWindow = 0;
        u.EdgeSpentThisWindow = 0;
        u.EdgeTriggerFiredThisWindow = false;
    }

    private static void ClearTurnBookkeeping(Unit u)
    {
        u.EdgeFirstHitClaimedThisTurn = false;
        u.EdgeMomentumGainsThisTurn = 0;
        u.EdgeLastActionKind = EdgeActionKind.None;
    }
}
