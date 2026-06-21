using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// ============================================================
// CombatManager.EnemyIntents.cs  (partial of CombatManager)
//
// Purpose:        The enemy intent system — Into-the-Breach-style
//                 telegraphed AI. Splits the old ActEnemyUnit flow
//                 into PLAN (end of enemy phase: every enemy decides
//                 and locks its action, visible to the player all
//                 turn) and EXECUTE (enemy phase: each unit carries
//                 out its locked plan, re-validated against the
//                 board the player just rearranged).
//
//                 LOCKING RULES:
//                 - Attacks / shots / channels are TILE-locked: the
//                   strike resolves against the planned tile and
//                   whatever stands on it at execution — including
//                   the enemy's own allies, or nothing. Repositioning
//                   and pushing things into/out of threat tiles is
//                   the core counterplay verb.
//                 - Guard/buff intents are UNIT-locked (self/ally).
//                 - Chase rule: melee units path toward the locked
//                   TILE, not the unit that used to be there.
//
//                 INFORMATION TIERS:
//                 - Intent KIND is always visible (glyph + "?").
//                 - Full details (value + threat tiles) require a
//                   reveal — RevealIntent / RevealAllIntents, the
//                   API the per-school Mage Sight cards will call.
//                 - Reveals last until the unit re-plans (one round)
//                   unless unit.IntentPermanentlyRevealed (the
//                   Adept/Namer "true name" hook).
//                 - To run FULLY hidden instead, set
//                   ShowIntentKindByDefault = false.
//
//                 CHRONOMANCER COMPATIBILITY (no effect changes):
//                 - PostponedTurns: the intent persists un-executed —
//                   the telegraphed strike visibly hangs for another
//                   round. Existing postpone effects work unchanged.
//                 - RedirectedChargeTile: consumed at execution as a
//                   retarget of the locked tile. Works on ANY
//                   tile-locked intent now, not just charges.
//                 - Decoys: planning uses the same targeting as
//                   before, so decoys now bait LOCKED attacks that
//                   keep swinging at the decoy's tile.
//
// Layer:          System (combat AI)
// Collaborators:  CombatManager.cs (main partial: enemy/player unit
//                 lists, movement helpers, UI refresh, FindNearest-
//                 PlayerUnit), Unit.cs (CurrentIntent, ChannelTile,
//                 intent display), HexTile.cs (SetThreatHighlight),
//                 UITheme.cs (TileThreat), CameraController (FocusOn)
//
// REMOVE from the main CombatManager file when adding this partial:
//   RunEnemyTurn, ActEnemyUnit, ActSoldier, ActBrute, ActDefender,
//   ActRanger, ActWizard.
// KEEP in the main file (this partial calls them):
//   MoveToDistance, MoveAwayFrom, CountAdjacentAllies, IsValidActor,
//   FindNearestPlayerUnit, ProcessStatusEffects, ApplyHazardDamage,
//   PerformAttack, PerformRangedAttack (legacy callers may remain).
// ============================================================

public enum IntentKind
{
    Attack,        // melee strike at a locked tile
    RangedAttack,  // ranged shot at a locked tile (LOS at execution)
    Channel,       // turn 1 of the wizard's two-turn blast (locked tile)
    Release,       // turn 2 — the blast lands on the locked tile
    Guard,         // defender reposition + self armor
    Unknown
}

/// <summary>One enemy's locked plan for the coming enemy phase.</summary>
public class EnemyIntent
{
    public IntentKind Kind = IntentKind.Unknown;
    /// <summary>Unit reference for orientation only (ranged kiting, chase fallback). Attacks resolve against TargetTile, never this.</summary>
    public Unit TargetUnit;
    /// <summary>The locked tile for Attack / RangedAttack / Channel / Release.</summary>
    public Vector2I? TargetTile;
    /// <summary>Tiles painted as threatened when revealed.</summary>
    public List<Vector2I> ThreatTiles = new();
    /// <summary>Damage / armor value shown when revealed.</summary>
    public int Value;
    /// <summary>Full details visible (value + threat tiles). Kind glyph shows regardless when ShowIntentKindByDefault.</summary>
    public bool Revealed;
}

public partial class CombatManager
{
    // ── Tuning / configuration ───────────────────────────────────────────────

    /// <summary>True (default): intent KIND glyph always visible, details need a reveal. False: everything hidden until revealed.</summary>
    public bool ShowIntentKindByDefault = true;

    /// <summary>Camera beat before each enemy acts, so the glide arrives before the action lands.</summary>
    private const float EnemyFocusBeat = 0.4f;

    /// <summary>Bonus damage on the wizard's released channel blast.</summary>
    private const int ChannelReleaseBonus = 3;

    /// <summary>Armor a Guard intent grants its owner.</summary>
    private const int GuardArmorValue = 2;

    // Glyphs chosen from ranges Label3D fonts reliably cover (the project
    // already renders ✦ ✧ ● ◆). Swap here if any draw as boxes.
    private static string IntentGlyph(IntentKind kind) => kind switch
    {
        IntentKind.Attack => "▲",
        IntentKind.RangedAttack => "►",
        IntentKind.Channel => "✦",
        IntentKind.Release => "✸",
        IntentKind.Guard => "◆",
        _ => "?"
    };

    private readonly HashSet<Vector2I> _paintedThreatTiles = new();

    // ════════════════════════════════════════════════════════════════════════
    // PLANNING — runs at the end of each enemy phase (and once after
    // deployment), so intents are visible for the entire player turn.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Locks a fresh intent for every living enemy and refreshes displays.
    /// Call from: (1) deployment confirmation / first player turn start,
    /// (2) the tail of RunEnemyTurn (already wired below). Safe to call
    /// redundantly.
    /// </summary>
    public void PlanAllEnemyIntents()
    {
        foreach (var enemy in enemyUnits)
        {
            if (!IsValidActor(enemy))
                continue;

            enemy.CurrentIntent = PlanIntent(enemy);

            if (enemy.CurrentIntent != null)
                enemy.CurrentIntent.Revealed = enemy.IntentPermanentlyRevealed;

            UpdateIntentDisplay(enemy);
        }

        RefreshThreatTiles();
    }

    private EnemyIntent PlanIntent(Unit enemy)
    {
        switch (enemy.EnemyArchetype)
        {
            case EnemyArchetype.Brute:
                return PlanBrute(enemy);
            case EnemyArchetype.Defender:
                return PlanDefender(enemy);
            case EnemyArchetype.Ranger:
                return PlanRanger(enemy);
            case EnemyArchetype.Wizard:
                return PlanWizard(enemy);
            case EnemyArchetype.Soldier:
            default:
                return PlanSoldier(enemy);
        }
    }

    private EnemyIntent PlanSoldier(Unit enemy)
    {
        var target = FindNearestPlayerUnit(enemy);
        if (target?.CurrentTile == null)
            return null;

        var tile = target.CurrentTile.Axial;
        return new EnemyIntent
        {
            Kind = IntentKind.Attack,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5
        };
    }

    private EnemyIntent PlanBrute(Unit enemy)
    {
        // Brute targeting: highest current HP among living player units.
        Unit target = null;
        int bestHp = -1;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (u.Stats.Health > bestHp)
            { bestHp = u.Stats.Health; target = u; }
        }

        if (target == null)
            return null;

        var tile = target.CurrentTile.Axial;
        return new EnemyIntent
        {
            Kind = IntentKind.Attack,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5
        };
    }

    private EnemyIntent PlanDefender(Unit enemy)
    {
        // Adjacent player at plan time → telegraph a locked strike on it.
        foreach (var neighbor in grid.GetNeighbors(enemy.CurrentTile.Axial))
        {
            var occ = grid.GetTile(neighbor)?.Occupant;
            if (occ != null && occ.TeamId != enemy.TeamId && occ.Stats.IsAlive)
            {
                return new EnemyIntent
                {
                    Kind = IntentKind.Attack,
                    TargetUnit = occ,
                    TargetTile = neighbor,
                    ThreatTiles = { neighbor },
                    Value = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5
                };
            }
        }

        // Otherwise: guard. Honest intent — no surprise attacks at execution.
        return new EnemyIntent
        {
            Kind = IntentKind.Guard,
            TargetUnit = enemy,
            Value = GuardArmorValue
        };
    }

    private EnemyIntent PlanRanger(Unit enemy)
    {
        var target = FindNearestPlayerUnit(enemy);
        if (target?.CurrentTile == null)
            return null;

        var tile = target.CurrentTile.Axial;
        return new EnemyIntent
        {
            Kind = IntentKind.RangedAttack,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = enemy.AttackDamage > 0 ? enemy.AttackDamage : 4
        };
    }

    private EnemyIntent PlanWizard(Unit enemy)
    {
        // Already channelling: the release is locked to the tile chosen when
        // the channel began — NOT re-aimed. Two full player turns of warning.
        if (enemy.HasStatus("wizard_charging") && enemy.ChannelTile.HasValue)
        {
            var locked = enemy.ChannelTile.Value;
            return new EnemyIntent
            {
                Kind = IntentKind.Release,
                TargetTile = locked,
                ThreatTiles = { locked },
                Value = (enemy.AttackDamage > 0 ? enemy.AttackDamage : 4) + ChannelReleaseBonus
            };
        }

        var target = FindNearestPlayerUnit(enemy);
        if (target?.CurrentTile == null)
            return null;

        var tile = target.CurrentTile.Axial;
        return new EnemyIntent
        {
            Kind = IntentKind.Channel,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = (enemy.AttackDamage > 0 ? enemy.AttackDamage : 4) + ChannelReleaseBonus
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // REVEAL API — what the per-school Mage Sight cards will call.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Fully reveals one enemy's intent (value + threat tiles). Lasts until it re-plans, or permanently if markPermanent (the Adept/Namer "true name" hook).</summary>
    public void RevealIntent(Unit enemy, bool markPermanent = false)
    {
        if (enemy?.CurrentIntent == null)
            return;

        enemy.CurrentIntent.Revealed = true;
        if (markPermanent)
            enemy.IntentPermanentlyRevealed = true;

        UpdateIntentDisplay(enemy);
        RefreshThreatTiles();
        combatUI?.AppendActionLog($"{enemy.Name}'s intent is revealed!");
    }

    /// <summary>Fully reveals every living enemy's intent for this round.</summary>
    public void RevealAllIntents()
    {
        foreach (var enemy in enemyUnits)
        {
            if (IsValidActor(enemy) && enemy.CurrentIntent != null)
            {
                enemy.CurrentIntent.Revealed = true;
                UpdateIntentDisplay(enemy);
            }
        }
        RefreshThreatTiles();
        combatUI?.AppendActionLog("All enemy intents are laid bare!");
    }

    // ════════════════════════════════════════════════════════════════════════
    // DISPLAY — intent glyph over each enemy + threat tile painting.
    // ════════════════════════════════════════════════════════════════════════

    private void UpdateIntentDisplay(Unit enemy)
    {
        if (enemy == null || !IsInstanceValid(enemy))
            return;

        var intent = enemy.CurrentIntent;
        if (intent == null)
        {
            enemy.ClearIntentDisplay();
            return;
        }

        bool showKind = ShowIntentKindByDefault || intent.Revealed;
        if (!showKind)
        {
            enemy.ClearIntentDisplay();
            return;
        }

        string glyph = IntentGlyph(intent.Kind);
        string value = intent.Revealed ? intent.Value.ToString() : "?";
        string suffix = enemy.PostponedTurns > 0 ? "…" : "";

        Color color = intent.Revealed
            ? new Color(1.0f, 0.55f, 0.45f)      // revealed — hot
            : new Color(0.85f, 0.85f, 0.85f);    // kind-only — neutral

        enemy.SetIntentDisplay($"{glyph} {value}{suffix}", color);
    }

    /// <summary>
    /// Repaints threat highlights from the union of all REVEALED intents.
    /// Public so death handling and player-side effects can refresh after
    /// changing the board (call from HandleUnitDeath).
    /// </summary>
    public void RefreshThreatTiles()
    {
        foreach (var coord in _paintedThreatTiles)
            grid?.GetTileView(coord)?.SetThreatHighlight(false);
        _paintedThreatTiles.Clear();

        foreach (var enemy in enemyUnits)
        {
            if (!IsValidActor(enemy) || enemy.CurrentIntent == null)
                continue;
            if (!enemy.CurrentIntent.Revealed)
                continue;

            foreach (var coord in enemy.CurrentIntent.ThreatTiles)
            {
                var view = grid?.GetTileView(coord);
                if (view != null)
                {
                    view.SetThreatHighlight(true);
                    _paintedThreatTiles.Add(coord);
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // EXECUTION — replaces the old RunEnemyTurn. Each unit carries out its
    // locked intent against the board as the player left it.
    // ════════════════════════════════════════════════════════════════════════

    private async Task RunEnemyTurn()
    {
        var snapshot = enemyUnits.ToList();
        foreach (var enemy in snapshot)
        {
            if (enemy == null || !IsInstanceValid(enemy) || !enemy.Stats.IsAlive)
                continue;

            CombatCamera?.FocusOn(enemy);
            await ToSignal(GetTree().CreateTimer(EnemyFocusBeat), "timeout");

            // ── Postpone (Chronomancer): the locked strike hangs un-executed ──
            if (enemy.PostponedTurns > 0)
            {
                enemy.PostponedTurns--;
                GD.Print($"{enemy.Name} is delayed — its strike hangs " +
                         $"({enemy.PostponedTurns} more turn(s)).");
                combatUI?.AppendActionLog($"{enemy.Name} is delayed!");
                UpdateIntentDisplay(enemy); // refresh the "…" suffix
                continue;                   // intent persists into next phase
            }

            // ── Disabled units lose their action; channels break ─────────────
            if (!enemy.CanAct())
            {
                string reason = enemy.HasStatus("bound") ? "bound"
                            : enemy.HasStatus("stunned") ? "stunned"
                            : "frozen";

                if (enemy.CurrentIntent?.Kind is IntentKind.Channel or IntentKind.Release)
                {
                    enemy.ChannelTile = null;
                    enemy.RemoveStatus("wizard_charging");
                    combatUI?.AppendActionLog($"{enemy.Name}'s channel is broken!");
                }

                GD.Print($"{enemy.Name} is {reason} — its plan fizzles.");
                combatUI?.AppendActionLog($"{enemy.Name} is {reason}!");
                enemy.CurrentIntent = null;
                enemy.ClearIntentDisplay();
                continue;
            }

            // ── Redirect (Chronomancer): retarget the locked tile ────────────
            if (enemy.RedirectedChargeTile.HasValue && enemy.CurrentIntent?.TargetTile != null)
            {
                var newTile = enemy.RedirectedChargeTile.Value;
                enemy.RedirectedChargeTile = null;
                enemy.CurrentIntent.TargetTile = newTile;
                enemy.CurrentIntent.ThreatTiles.Clear();
                enemy.CurrentIntent.ThreatTiles.Add(newTile);
                if (enemy.CurrentIntent.Kind == IntentKind.Release)
                    enemy.ChannelTile = newTile;
                GD.Print($"{enemy.Name}'s intent is redirected to {newTile}.");
                combatUI?.AppendActionLog($"{enemy.Name}'s strike is redirected!");
            }

            await ExecuteIntent(enemy);

            if (enemy != null && IsInstanceValid(enemy))
            {
                enemy.CurrentIntent = null;
                enemy.ClearIntentDisplay();
            }

            if (CheckCombatEnd())
                return;
        }

        GD.Print("=== Enemy Turn End ===");
        enemyPhaseRunning = false;

        // Lock next round's plans NOW so they're visible all player turn.
        PlanAllEnemyIntents();
    }

    private async Task ExecuteIntent(Unit enemy)
    {
        var intent = enemy.CurrentIntent;
        if (intent == null)
        {
            // No plan (spawned mid-round, or planning found no target) —
            // fall back to one fresh soldier-style decision, unannounced.
            intent = PlanSoldier(enemy);
            if (intent == null)
                return;
        }

        switch (intent.Kind)
        {
            case IntentKind.Attack:
                await ExecuteMeleeIntent(enemy, intent);
                break;
            case IntentKind.RangedAttack:
                await ExecuteRangedIntent(enemy, intent);
                break;
            case IntentKind.Channel:
                await ExecuteChannelStart(enemy, intent);
                break;
            case IntentKind.Release:
                await ExecuteChannelRelease(enemy, intent);
                break;
            case IntentKind.Guard:
                await ExecuteGuardIntent(enemy, intent);
                break;
        }
    }

    // ── Melee: chase the LOCKED TILE, strike whatever stands on it ──────────

    private async Task ExecuteMeleeIntent(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || intent.TargetTile == null)
            return;

        var tile = intent.TargetTile.Value;

        if (grid.Distance(enemy.CurrentTile.Axial, tile) > 1)
            await MoveTowardTile(enemy, tile);

        if (!IsValidActor(enemy))
            return;

        if (grid.Distance(enemy.CurrentTile.Axial, tile) <= 1)
            await StrikeTile(enemy, tile, intent.Value, ranged: false);
        else
            combatUI?.AppendActionLog($"{enemy.Name} can't reach its mark.");
    }

    // ── Ranged: kite relative to the remembered unit, shoot the LOCKED TILE ──

    private async Task ExecuteRangedIntent(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || intent.TargetTile == null)
            return;

        var tile = intent.TargetTile.Value;

        // Reposition relative to the living target if it still exists —
        // orientation only; the shot stays locked to the tile.
        if (IsValidActor(intent.TargetUnit))
        {
            int dist = grid.Distance(enemy.CurrentTile, intent.TargetUnit.CurrentTile);
            int preferred = enemy.AttackRange;
            int minDist = preferred - 1;

            if (dist < minDist)
                await MoveAwayFrom(enemy, intent.TargetUnit, minDist);
            else if (dist > enemy.AttackRange)
                await MoveToDistance(enemy, intent.TargetUnit, preferred);
        }

        if (!IsValidActor(enemy))
            return;

        int tileDist = grid.Distance(enemy.CurrentTile.Axial, tile);
        if (tileDist > enemy.AttackRange)
        {
            combatUI?.AppendActionLog($"{enemy.Name} — mark out of range, shot wasted.");
            return;
        }

        if (!grid.HasLineOfSight(enemy.CurrentTile.Axial, tile))
        {
            combatUI?.AppendActionLog($"{enemy.Name} has no line of sight!");
            return;
        }

        await StrikeTile(enemy, tile, intent.Value, ranged: true);
    }

    // ── Channel start: reposition, lock the tile, begin charging ────────────

    private async Task ExecuteChannelStart(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || intent.TargetTile == null)
            return;

        // Reposition relative to the remembered target (old wizard behaviour).
        if (IsValidActor(intent.TargetUnit))
        {
            int dist = grid.Distance(enemy.CurrentTile, intent.TargetUnit.CurrentTile);
            int preferred = enemy.AttackRange;

            if (dist < preferred)
                await MoveAwayFrom(enemy, intent.TargetUnit, preferred);
            else if (dist > preferred + 2)
                await MoveToDistance(enemy, intent.TargetUnit, preferred);
        }

        if (!IsValidActor(enemy))
            return;

        enemy.ChannelTile = intent.TargetTile;
        enemy.ApplyStatus("wizard_charging", 2);

        GD.Print($"{enemy.Name} begins channelling at {intent.TargetTile.Value}...");
        combatUI?.AppendActionLog($"{enemy.Name} begins channelling!");
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    // ── Channel release: the blast lands on the tile locked two phases ago ──

    private async Task ExecuteChannelRelease(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy))
            return;

        Vector2I? locked = enemy.ChannelTile ?? intent.TargetTile;
        enemy.ChannelTile = null;
        enemy.RemoveStatus("wizard_charging");

        if (locked == null)
            return;

        var tile = locked.Value;

        if (grid.Distance(enemy.CurrentTile.Axial, tile) > enemy.AttackRange ||
            !grid.HasLineOfSight(enemy.CurrentTile.Axial, tile))
        {
            string missMsg = $"{enemy.Name} — the blast point is beyond reach, charge wasted.";
            GD.Print(missMsg);
            combatUI?.AppendActionLog(missMsg);
            return;
        }

        GD.Print($"{enemy.Name} releases a charged blast!");
        combatUI?.AppendActionLog($"{enemy.Name} releases a charged blast!");

        var victim = grid.GetTile(tile)?.Occupant;
        await StrikeTile(enemy, tile, intent.Value, ranged: true);

        // Slow rider applies to whoever was actually hit.
        if (victim != null && IsInstanceValid(victim) && victim.Stats.IsAlive)
        {
            victim.ApplyStatus("slowed", 1);
            combatUI?.AppendActionLog($"{victim.Name} is slowed by arcane energy!");
        }
    }

    // ── Guard: defender repositioning + telegraphed armor ───────────────────

    private async Task ExecuteGuardIntent(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy))
            return;

        // Reposition toward the most allies (old defender logic, opportunistic
        // attack removed — Guard does exactly what it telegraphed, nothing else).
        Unit nearestAlly = null;
        int nearestAllyDist = int.MaxValue;
        foreach (var u in enemyUnits)
        {
            if (u == null || u == enemy || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            int d = grid.Distance(enemy.CurrentTile, u.CurrentTile);
            if (d < nearestAllyDist)
            { nearestAllyDist = d; nearestAlly = u; }
        }

        var moveOptions = grid.GetReachableTiles(enemy);
        Vector2I bestMove = enemy.CurrentTile.Axial;
        int bestAllyCount = CountAdjacentAllies(enemy, enemy.CurrentTile.Axial);

        foreach (var coord in moveOptions)
        {
            int allyCount = CountAdjacentAllies(enemy, coord);
            if (allyCount > bestAllyCount)
            {
                bestAllyCount = allyCount;
                bestMove = coord;
            }
        }

        if (bestAllyCount == 0 && nearestAlly != null)
        {
            var nextStep = grid.GetFirstStepToward(enemy, nearestAlly.CurrentTile.Axial);
            if (nextStep != null && enemy.TryMoveTo(grid, nextStep))
            {
                combatUI?.AppendActionLog($"{enemy.Name} moves to rejoin allies.");
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }
        }
        else if (bestMove != enemy.CurrentTile.Axial)
        {
            var tile = grid.GetTile(bestMove);
            if (tile != null && enemy.TryMoveTo(grid, tile))
            {
                combatUI?.AppendActionLog($"{enemy.Name} moves to protect allies.");
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }
        }

        enemy.Stats.Armor += intent.Value;
        enemy.RefreshHealthBar();
        combatUI?.AppendActionLog($"{enemy.Name} braces (+{intent.Value} armor).");
    }

    // ── Shared: tile-locked strike resolution ───────────────────────────────

    /// <summary>
    /// Resolves a locked strike against a TILE: hits whatever stands there —
    /// a player unit, the attacker's own ally (the push-into-harm payoff), or
    /// nothing (a visible whiff the player earned).
    /// </summary>
    private async Task StrikeTile(Unit attacker, Vector2I tile, int damage, bool ranged)
    {
        var victim = grid.GetTile(tile)?.Occupant;
        string verb = ranged ? "shoots" : "strikes";

        if (victim == null || !IsInstanceValid(victim) || !victim.Stats.IsAlive)
        {
            string whiff = $"{attacker.Name} {verb} at empty ground!";
            GD.Print(whiff);
            combatUI?.AppendActionLog(whiff);
        }
        else if (victim.TeamId == attacker.TeamId)
        {
            string ff = $"{attacker.Name} {verb} its own ally {victim.Name} for {damage}!";
            GD.Print(ff);
            combatUI?.AppendActionLog(ff);
            victim.ApplyDamage(damage);
        }
        else
        {
            string hit = $"{attacker.Name} {verb} {victim.Name} for {damage} damage.";
            GD.Print(hit);
            combatUI?.AppendActionLog(hit);
            victim.ApplyDamage(damage);
        }

        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        RefreshDeckCounts();
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    /// <summary>Move one step toward a coordinate (tile-chase variant of MoveToward).</summary>
    private async Task MoveTowardTile(Unit enemy, Vector2I goal)
    {
        if (!IsValidActor(enemy))
            return;

        var nextStep = grid.GetFirstStepToward(enemy, goal);
        if (nextStep == null)
            return;

        int pathCost = grid.GetMoveCostTo(enemy, nextStep);
        if (pathCost < 0 || pathCost > enemy.MoveRange)
            return;

        if (enemy.TryMoveTo(grid, nextStep))
        {
            combatUI?.AppendActionLog($"{enemy.Name} advances on its mark.");
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
        }
    }
}
