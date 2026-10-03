using Godot;

// ============================================================
// CombatManager.TimeSight.cs
//
// Purpose:        Chronomancer engine pieces that read or record
//                 time (class_identity_chronomancer_v1 §2b, §2d, §2e):
//                 • Lookahead: every planned intent carries a
//                   forecast of the enemy's NEXT beat (its kind and a
//                   short description). Hidden until a lookahead
//                   reveal (Glimpse) or The Fixed Hour's permanent
//                   sight. Read from the beat script (IntentCycle) or,
//                   with no script, from the unit's behaviour key; a
//                   channel's next beat is always its release. It is a
//                   forecast of the plan, not of the board: where it
//                   strikes is decided when it plans.
//                 • Round-start snapshot: each unit's tile and HP when
//                   the round began (Rewind, Recoil, Return to the
//                   Moment read it).
//                 • Overdrawn bank: the start-of-turn HP loss.
// Layer:          Combat
// Collaborators:  PlanAllEnemyIntents (ForecastNextBeat),
//                 StartPlayerTurn (SnapshotRoundStart,
//                 ApplyOverdraftLoss), EnemyIntent.Next*, Unit,
//                 FateAttunement, TimeBankEffects.cs
// See:            docs/class_identity_chronomancer_v1.md §2
// ============================================================

public partial class CombatManager
{
    /// <summary>HP lost per point the Time Bank is below zero, at the start of each turn.</summary>
    public const int OverdraftHpPerPoint = 2;

    // ── Lookahead (§2d) ─────────────────────────────────────────────────

    /// <summary>Fills <see cref="EnemyIntent.NextKind"/> / <see cref="EnemyIntent.NextNote"/>
    /// for the enemy's current plan, and shows it at once if the enemy is under permanent
    /// lookahead. Pure read of the script; plans nothing.</summary>
    private void ForecastNextBeat(Unit enemy)
    {
        var intent = enemy?.CurrentIntent;
        if (intent == null)
            return;
        intent.NextRevealed = enemy.IntentLookaheadPermanent;

        if (IsRitualist(enemy))
        {
            intent.NextKind = IntentKind.Unknown;
            intent.NextNote = "continues its ritual";
            return;
        }
        if (intent.Kind == IntentKind.Channel)
        {
            intent.NextKind = IntentKind.Release;
            intent.NextNote = "releases the charged blast";
            return;
        }

        string key = NextCycleKeyFor(enemy);
        (intent.NextKind, intent.NextNote) = (key ?? "").ToLowerInvariant() switch
        {
            "melee_advance"           => (IntentKind.Attack, "advances and strikes the nearest target"),
            "melee_target_highest_hp" => (IntentKind.Attack, "goes for the highest-HP target"),
            "melee_hunt_wounded"      => (IntentKind.Attack, "hunts the most wounded target"),
            "hunt_ward"               => (IntentKind.Attack, "goes after the warded objective"),
            "hold_until_near"         => (IntentKind.Guard, "holds until something comes near"),
            "hold_ground"             => (IntentKind.Guard, "holds its ground"),
            "ranged_kite"             => (IntentKind.RangedAttack, "shoots and keeps its distance"),
            "ranged_charge"           => intent.Kind == IntentKind.Release
                                            ? (IntentKind.Channel, "begins charging another blast")
                                            : (IntentKind.Channel, "begins charging a blast"),
            "imbue"                   => (IntentKind.Imbue, "writes its element onto the ground"),
            "shove"                   => (IntentKind.Shove, "shoves a target along a path"),
            "warp_channeler"          => (IntentKind.Channel, "prepares a warp"),
            _                         => (IntentKind.Unknown, "its next move is unclear"),
        };
    }

    /// <summary>The planner key for the beat AFTER the current one. Mirrors
    /// CycleKeyFor with the index one beat on (a channel does not advance the script,
    /// which ForecastNextBeat handles before calling this).</summary>
    private static string NextCycleKeyFor(Unit enemy)
    {
        int n = enemy.IntentCycle?.Count ?? 0;
        if (n == 0)
            return enemy.BehaviorKey;
        int next = enemy.IntentCycleIndex + 1;
        if (!enemy.CycleLoops && next >= n)
            return enemy.BehaviorKey;
        return enemy.IntentCycle[next % n];
    }

    // ── Round-start snapshot (§2e) ──────────────────────────────────────

    /// <summary>Records every living unit's tile and HP as the round begins.</summary>
    private void SnapshotRoundStart()
    {
        foreach (var u in playerUnits)
            SnapshotOne(u);
        foreach (var u in enemyUnits)
            SnapshotOne(u);
    }

    private static void SnapshotOne(Unit u)
    {
        if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
            return;
        u.RoundStartTile = u.CurrentTile.Axial;
        u.RoundStartHp = u.Stats.Health;
    }

    // ── Overdrawn Time Bank (§2b) ───────────────────────────────────────

    /// <summary>Start of the unit's turn: lose 2 HP per point the bank is below zero.
    /// HP loss, not damage: shield and armor do not absorb it.</summary>
    private void ApplyOverdraftLoss(Unit unit, FateAttunement fate)
    {
        if (unit == null || fate == null || !fate.IsOverdrawn || !unit.Stats.IsAlive)
            return;

        int loss = OverdraftHpPerPoint * -fate.Charges;
        unit.Stats.Health -= loss;
        string msg = $"{unit.Name} is Overdrawn ({fate.Charges}) and loses {loss} HP.";
        GD.Print($"[TimeBank] {msg}");
        combatUI?.AppendActionLog(msg);

        if (unit.Stats.Health <= 0)
            unit.KillFromEffect();
        else
            unit.RefreshHealthBar();
    }
}
