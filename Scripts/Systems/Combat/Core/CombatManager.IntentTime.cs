using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// ============================================================
// CombatManager.IntentTime.cs
//
// Purpose:        Runtime for Defer and Advance
//                 (class_identity_chronomancer_v1 §2a).
//                 • Defer flags an enemy's current attack. In its
//                   activation the enemy still moves (a melee unit
//                   walks toward its mark) but does not strike; the
//                   attack is queued and lands at the end of the
//                   round, after every enemy has acted, in speed
//                   order, on the tile it was locked onto. Whoever
//                   stands there then is hit: a player, nobody, or
//                   another enemy. A dead attacker's attack is lost.
//                 • Advance lands the attack NOW on its locked tile;
//                   the enemy's activation then runs without it.
//                 Timed attacks resolve without range, sight or AP
//                 checks: the blow was already thrown, only its
//                 moment moved. Melee lands at its base value (no
//                 charge or pack bonus, which need the swing itself).
//                 Timeable intents: melee and ranged attacks, imbue
//                 spells, and the generic channel release. Ritual and
//                 warp releases run bespoke scripts and refuse.
// Layer:          Combat
// Collaborators:  IntentTimeEffects.cs (DeferIntentEffect,
//                 AdvanceIntentEffect), RunEnemyTurn (activation and
//                 end-of-round hooks), ResolveStrike, RegisterManager
// See:            docs/class_identity_chronomancer_v1.md §2a
// ============================================================

public partial class CombatManager
{
    /// <summary>Deferred attacks queued during this enemy phase, resolved after it.</summary>
    private readonly List<(Unit enemy, EnemyIntent intent)> _deferredAttacks = new();

    /// <summary>The enemy whose activation is running without its blow (Deferred or
    /// Spent): StrikeTile and the melee strike block skip it, everything else in its
    /// normal intent (approach, charge sprint, kiting, flanking, skirmish retreat) runs.</summary>
    private Unit _holdStrikeFor;

    /// <summary>The enemy whose activation is running (null outside the enemy phase).
    /// Its attack is already under way, so it can no longer be moved in time.</summary>
    private Unit _actingEnemy;

    private void InstallIntentTime()
    {
        _deferredAttacks.Clear();
        _actingEnemy = null;
        IntentTime.DeferHook = DeferAttack;
        IntentTime.AdvanceHook = AdvanceAttack;
        IntentTime.RevealHook = RevealForEffect;
    }

    /// <summary>peek_intent's seam (Glimpse, Survey, Flare, Arcane Sight). Lookahead
    /// also shows the enemy's NEXT intent (CombatManager.TimeSight.cs); permanent keeps
    /// whatever was revealed visible for the rest of the fight.</summary>
    private bool RevealForEffect(Unit enemy, bool lookahead, bool permanent)
    {
        if (!IsValidActor(enemy) || enemy.CurrentIntent == null)
            return false;
        if (lookahead)
        {
            enemy.CurrentIntent.NextRevealed = true;
            if (permanent)
                enemy.IntentLookaheadPermanent = true;
            RegisterManager.Fire("chrono.lookahead");
        }
        RevealIntent(enemy, markPermanent: permanent);
        return true;
    }

    private void UninstallIntentTime()
    {
        if (IntentTime.DeferHook == DeferAttack)
            IntentTime.DeferHook = null;
        if (IntentTime.AdvanceHook == AdvanceAttack)
            IntentTime.AdvanceHook = null;
        if (IntentTime.RevealHook == RevealForEffect)
            IntentTime.RevealHook = null;
    }

    // ── Rules ───────────────────────────────────────────────────────────

    /// <summary>Null when <paramref name="enemy"/>'s current intent can be moved in time;
    /// otherwise the reason it can't.</summary>
    private string WhyNotTimeable(Unit enemy)
    {
        if (!IsValidActor(enemy))
            return "it cannot act";
        var intent = enemy.CurrentIntent;
        if (intent == null)
            return "it has no attack planned";
        if (enemy == _actingEnemy)
            return "its attack is already under way";

        switch (intent.Kind)
        {
            case IntentKind.Attack:
            case IntentKind.RangedAttack:
                return intent.TargetTile == null ? "its attack has no locked tile" : null;
            case IntentKind.Imbue:
                return intent.ImbueElement == TileElementType.None || intent.ThreatTiles == null || intent.ThreatTiles.Count == 0
                    ? "its spell has no locked ground" : null;
            case IntentKind.Release:
                if (IsRitualist(enemy) || IsWarpChanneler(enemy))
                    return "that ritual cannot be moved in time";
                return (enemy.ChannelTile ?? intent.TargetTile) == null ? "its blast has no locked tile" : null;
            default:
                return "it is not attacking this round";
        }
    }

    private string DeferAttack(Unit enemy)
    {
        string why = WhyNotTimeable(enemy);
        if (why != null)
            return why;
        if (enemy.AttackTiming == IntentTiming.Deferred)
            return "its attack is already deferred";
        if (enemy.AttackTiming == IntentTiming.Spent)
            return "its attack has already landed";

        enemy.AttackTiming = IntentTiming.Deferred;
        string msg = $"{enemy.Name}'s attack is deferred. It lands at the end of the round on {DescribeLockedGround(enemy.CurrentIntent, enemy)}.";
        GD.Print($"[Defer] {msg}");
        combatUI?.AppendActionLog(msg);

        UpdateIntentDisplay(enemy);
        RefreshThreatTiles();
        RegisterManager.Fire("chrono.defer");
        return null;
    }

    private string AdvanceAttack(Unit enemy)
    {
        string why = WhyNotTimeable(enemy);
        if (why != null)
            return why;
        if (enemy.AttackTiming == IntentTiming.Spent)
            return "its attack has already landed";

        // A deferred attack can still be called forward. If the enemy phase already
        // queued it, take it back out so it does not land twice.
        _deferredAttacks.RemoveAll(e => e.enemy == enemy);

        enemy.AttackTiming = IntentTiming.Spent;
        string msg = $"{enemy.Name}'s attack is advanced and lands now on {DescribeLockedGround(enemy.CurrentIntent, enemy)}.";
        GD.Print($"[Advance] {msg}");
        combatUI?.AppendActionLog(msg);

        ResolveTimedAttack(enemy, enemy.CurrentIntent);

        // Kills and onAttack triggers queue in ResolveStrike; pump them the way any
        // other player-turn damage does, then repaint everything the blow touched.
        KickTriggerDrain();
        UpdateIntentDisplay(enemy);
        RefreshThreatTiles();
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        RegisterManager.Fire("chrono.advance");
        return null;
    }

    private static string DescribeLockedGround(EnemyIntent intent, Unit enemy)
    {
        if (intent == null)
            return "its locked tile";
        if (intent.Kind == IntentKind.Imbue)
            return $"its {intent.ThreatTiles.Count} marked tile(s)";
        var t = (intent.Kind == IntentKind.Release ? enemy.ChannelTile : null) ?? intent.TargetTile;
        return t.HasValue ? $"({t.Value.X}, {t.Value.Y})" : "its locked tile";
    }

    /// <summary>Lands a timed attack on its locked ground with no range, sight or AP
    /// check. Synchronous: the strike's trigger fallout is queued, and the caller pumps it.</summary>
    private void ResolveTimedAttack(Unit enemy, EnemyIntent intent)
    {
        if (enemy == null || intent == null)
            return;

        switch (intent.Kind)
        {
            case IntentKind.Attack:
                if (intent.TargetTile.HasValue)
                    ResolveStrike(enemy, intent.TargetTile.Value, intent.BaseValue, ranged: false, redirected: null);
                break;

            case IntentKind.RangedAttack:
                if (intent.TargetTile.HasValue)
                    ResolveStrike(enemy, intent.TargetTile.Value, intent.Value, ranged: true, redirected: null);
                break;

            case IntentKind.Release:
            {
                Vector2I? locked = enemy.ChannelTile ?? intent.TargetTile;
                enemy.ChannelTile = null;
                enemy.RemoveStatus("wizard_charging");
                enemy.ChannelDelayRemaining = 0;
                if (!locked.HasValue)
                    break;
                combatUI?.AppendActionLog($"{enemy.Name}'s charged blast lands.");
                ResolveStrike(enemy, locked.Value, intent.Value, ranged: true, redirected: null);
                ApplyCasterRider(enemy, LastStrikeVictim);
                break;
            }

            case IntentKind.Imbue:
            {
                int count = 0;
                foreach (var coord in intent.ThreatTiles)
                {
                    if (!IsImbuableTile(coord))
                        continue;
                    TileEntryReactions.ImbueTile(grid.GetTile(coord), intent.ImbueElement, 1f, enemy);
                    count++;
                }
                combatUI?.AppendActionLog($"{enemy.Name}'s spell lands: {count} tile(s) imbued with {intent.ImbueElement}.");
                break;
            }
        }
    }

    // ── Enemy phase hooks ───────────────────────────────────────────────

    /// <summary>
    /// Runs an activation whose attack was moved in time. Returns false for a normal
    /// activation (the caller runs ExecuteIntent). A deferred attack is queued for the
    /// end of the round; a spent one is simply gone. Either way the enemy still moves:
    /// a melee unit walks toward its mark, everything else holds its ground.
    /// </summary>
    private async Task<bool> RunTimedActivation(Unit enemy)
    {
        if (enemy.AttackTiming == IntentTiming.Normal)
            return false;

        var intent = enemy.CurrentIntent;
        if (enemy.AttackTiming == IntentTiming.Deferred && intent != null)
        {
            _deferredAttacks.Add((enemy, intent));
            string held = $"{enemy.Name} moves, its attack held until the end of the round.";
            GD.Print($"[Defer] {held}");
            combatUI?.AppendActionLog(held);
        }
        else
        {
            string spent = $"{enemy.Name} acts without its attack; it has already landed.";
            GD.Print($"[Advance] {spent}");
            combatUI?.AppendActionLog(spent);
        }

        // 2026-10-03 (slice 8b): the enemy runs its own intent with the blow held, so
        // it moves exactly as it planned to: melee approach and charge, ranged kiting,
        // flanking for a clear shot, a skirmisher's retreat. Before this only a melee
        // attacker out of reach moved, and everyone else stood still, which made a
        // Deferred round look like nothing happened. Channels, imbues and shoves keep
        // their old behaviour: they have no movement step to replay.
        if (intent != null && IsValidActor(enemy)
            && (intent.Kind == IntentKind.Attack || intent.Kind == IntentKind.RangedAttack))
        {
            _holdStrikeFor = enemy;
            try
            {
                if (intent.Kind == IntentKind.Attack)
                    await ExecuteMeleeIntent(enemy, intent);
                else
                    await ExecuteRangedIntent(enemy, intent);
            }
            finally
            {
                _holdStrikeFor = null;
            }
        }
        return true;
    }

    /// <summary>End of round (after every enemy activation, before the next player turn):
    /// every deferred attack lands on its locked ground, fastest attacker first.
    /// Returns true when the fight ended.</summary>
    private async Task<bool> ResolveDeferredAttacksAsync()
    {
        if (_deferredAttacks.Count == 0)
            return false;

        var due = _deferredAttacks
            .OrderByDescending(e => e.enemy != null && IsInstanceValid(e.enemy) ? e.enemy.Stats.BaseSpeed : 0)
            .ToList();
        _deferredAttacks.Clear();

        foreach (var (enemy, intent) in due)
        {
            if (!IsValidActor(enemy))
            {
                string lost = "A deferred attack is lost: its attacker has fallen.";
                GD.Print($"[Defer] {lost}");
                combatUI?.AppendActionLog(lost);
                continue;
            }

            string lands = $"{enemy.Name}'s deferred attack lands on {DescribeLockedGround(intent, enemy)}.";
            GD.Print($"[Defer] {lands}");
            combatUI?.AppendActionLog(lands);
            // 2026-10-03: the camera used to start gliding to the attacker and the blow
            // resolved the same frame, so the player never saw it land. Frame the attacker
            // AND its locked tile, let the glide arrive, strike, then hold on the result.
            var lockedAt = (intent.Kind == IntentKind.Release ? enemy.ChannelTile : null) ?? intent.TargetTile;
            var lockedView = lockedAt.HasValue ? grid.GetTileView(lockedAt.Value) : null;
            if (CombatCamera != null)
            {
                if (lockedView != null)
                    CombatCamera.FocusOn((enemy.GlobalPosition + lockedView.GlobalPosition) * 0.5f);
                else
                    CombatCamera.FocusOn(enemy);
            }
            await ToSignal(GetTree().CreateTimer(0.6f), "timeout");
            if (!IsValidActor(enemy))
                continue;

            ResolveTimedAttack(enemy, intent);
            await DrainTriggerStackAsync();
            RefreshSelectedUnitUI();
            RefreshEnemyRoster();
            RefreshPlayerUnitBar();
            if (CheckCombatEnd())
                return true;
            await ToSignal(GetTree().CreateTimer(0.8f), "timeout");
        }
        return false;
    }
}
