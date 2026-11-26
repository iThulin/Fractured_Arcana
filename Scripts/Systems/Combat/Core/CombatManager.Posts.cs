using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// CombatManager.Posts.cs
//
// Purpose:        Combat overhaul stage C3: enemies that hold a post.
//                 A slay fight's leader stays where it stands and
//                 makes the party come to it. A reach fight posts one
//                 warden on the marked ground. Both strike anything
//                 that comes near the post, and both leave it for good
//                 once they are hurt.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.EnemyIntents.cs (the planner table,
//                   ExecuteGuardIntent, MoveTowardTile, PlanSoldier),
//                 CombatManager.Outcome.cs (the slay leader),
//                 CombatManager.Reach.cs (the marked ground),
//                 CombatManager.cs (SpawnAndPlaceEnemies),
//                 UIContent.cs (the behaviour line)
// See:            docs/combat_c3_enemy_posts_v1.md
//
// The rule is stateless on purpose. PlanAllEnemyIntents runs more than
// once per round, so a planner that remembered "I was hit" would lose
// it on the second pass. "Below full health" can be read any number of
// times and gives the same answer.
// ============================================================

public partial class CombatManager
{
    /// <summary>Runtime-only behaviour key. Never authored in unit data: a post is
    /// assigned per fight, by the objective, to a unit that already has a routine.</summary>
    private const string HoldPostKey = "hold_post";

    /// <summary>A post-holder strikes any unit within this many tiles of its post.
    /// Starting value.</summary>
    private const int PostLeash = 3;

    /// <summary>A reach fight posts a warden only when this many enemies took the
    /// field, so someone is still left to contest the middle.</summary>
    private const int ReachWardenMinEnemies = 3;

    private readonly Dictionary<Unit, Vector2I> _posts = new();

    /// <summary>From SpawnAndPlaceEnemies, after the slay leader is bound.</summary>
    private void AssignEnemyPosts()
    {
        if (SlayActive && _slayTarget != null && CanHoldPost(_slayTarget))
        {
            GivePost(_slayTarget, _slayTarget.CurrentTile.Axial);
            combatUI?.AppendActionLog(
                $"── {_slayTargetName} holds its ground. It strikes anything within {PostLeash} tiles, " +
                "and comes for you once it is hurt. ──");
        }

        AnnounceObstruction();   // C8 (CombatManager.Obstructions.cs)

        if (ReachActive)
        {
            PostReachWarden();
            PostReachPicket();
            if (_reachBarricadePieces > 0)
                combatUI?.AppendActionLog(
                    "A barricade stands in front of the far exit, with one gate. The Gate Winch is inside it. " +
                    "If the gate is shut, break a piece to get in. " +
                    "A Powder Cask breaks easily and bursts on anything next to it.");
            AssignGateKeeper();   // C9: the enemy mans the winch (CombatManager.GateKeeper.cs)
        }

        AssignSignalRunner();   // C9: after the posts, so a post-holder is never the runner (CombatManager.SignalFire.cs)
    }

    /// <summary>A reach fight fields extra enemies in front of the far exit: half
    /// the roster again, rounded down, at least 1 and at most this many.</summary>
    private const int ReachPicketMax = 3;

    /// <summary>Spawns the picket: copies of the fight's own roster, placed just
    /// outside the barricade on the side the party comes from. A unit that can hold
    /// a post holds one there. Spawned here and not added to the encounter, so a
    /// fight that is started again does not grow its roster each time.</summary>
    private void PostReachPicket()
    {
        var roster = EncounterContextCarrier.Current?.Enemies;
        if (roster == null || roster.Count == 0 || grid == null || _objective == null)
            return;
        int want = Mathf.Clamp(roster.Count / 2, 1, ReachPicketMax);

        Vector2I centre = grid.ResolveRecipeCoord(_objective.ZoneAnchor);
        Vector2I mid = grid.RecipeMidpoint;
        int near = Mathf.Max(1, _objective.ZoneRadius) + ReachBarricadeGap + 1;
        var spots = new List<TileData>();
        foreach (var td in grid.Tiles.Values)
        {
            if (td == null || !td.IsWalkable || td.IsBlocked || td.IsOccupied)
                continue;
            int d = grid.Distance(td.Axial, centre);
            if (d < near || d > near + 1)
                continue;
            spots.Add(td);
        }
        spots.Sort((a, b) => grid.Distance(a.Axial, mid).CompareTo(grid.Distance(b.Axial, mid)));

        int spawned = 0;
        int posted = 0;
        for (int i = 0; i < spots.Count && spawned < want; i++)
        {
            var slot = roster[spawned % roster.Count];
            var unit = SpawnRegistryUnit(slot.UnitId, spots[i], teamId: 1,
                                         difficultyMult: slot.DifficultyMult,
                                         isMidFightSummon: false);
            if (unit == null)
                continue;
            spawned++;
            if (CanHoldPost(unit))
            {
                GivePost(unit, spots[i].Axial);
                posted++;
            }
        }
        GD.Print($"[Posts] reach picket: {spawned} of {want} spawned, {posted} posted.");
        if (spawned > 0)
        {
            combatUI?.AppendActionLog(
                spawned == 1
                    ? "1 more enemy stands watch in front of the far exit."
                    : $"{spawned} more enemies stand watch in front of the far exit.");
            RefreshEnemyRoster();
        }
    }

    /// <summary>Only a plain melee unit takes a post. A ranged unit already keeps its
    /// distance, and a unit with a scripted cycle or a planted role keeps its own routine.</summary>
    private static bool CanHoldPost(Unit unit)
    {
        if (unit == null || !IsInstanceValid(unit) || unit.Stats == null || !unit.Stats.IsAlive)
            return false;
        if (unit.CurrentTile == null || unit.IsMapObject || unit.IsStructure)
            return false;
        if (unit.AttackRange > 1)
            return false;
        if (unit.IntentCycle != null && unit.IntentCycle.Count > 0)
            return false;
        if (unit.HasBehaviorTag("immobile") || unit.HasBehaviorTag("bulwark"))
            return false;
        return unit.BehaviorKey == "melee_advance"
            || unit.BehaviorKey == "melee_target_highest_hp"
            || unit.BehaviorKey == "hold_until_near"
            || unit.BehaviorKey == "melee_hunt_wounded";
    }

    private void GivePost(Unit unit, Vector2I tile)
    {
        _posts[unit] = tile;
        unit.BehaviorKey = HoldPostKey;
        GD.Print($"[Posts] {unit.Name} holds a post at {tile}.");
    }

    /// <summary>Moves the toughest eligible enemy onto the marked ground and posts it there.</summary>
    private void PostReachWarden()
    {
        int fielded = 0;
        Unit warden = null;
        foreach (var u in enemyUnits)
        {
            if (u == null || !IsInstanceValid(u) || u.Stats == null || !u.Stats.IsAlive)
                continue;
            if (u.IsMapObject || u.IsStructure || u.IsMidFightSummon)
                continue;
            fielded++;
            if (!CanHoldPost(u))
                continue;
            if (warden == null || u.Stats.MaxHealth > warden.Stats.MaxHealth)
                warden = u;
        }
        if (warden == null || fielded < ReachWardenMinEnemies)
            return;

        EnsureObjectiveZone();
        if (_objectiveZone == null || _objectiveZone.Count == 0)
            return;

        Vector2I centre = grid.ResolveRecipeCoord(_objective.ZoneAnchor);
        TileData best = null;
        int bestDist = int.MaxValue;
        foreach (var coord in _objectiveZone)
        {
            var td = grid.GetTile(coord);
            if (td == null || !td.IsWalkable || td.IsBlocked || td.IsOccupied)
                continue;
            int d = grid.Distance(coord, centre);
            if (d < bestDist)
            {
                bestDist = d;
                best = td;
            }
        }
        if (best == null)
            return;

        warden.PlaceOnTile(best);
        GivePost(warden, best.Axial);
        string who = string.IsNullOrEmpty(warden.DisplayName) ? warden.Name.ToString() : warden.DisplayName;
        combatUI?.AppendActionLog(
            $"── {who} guards the far exit. It strikes anything within {PostLeash} tiles, " +
            "and leaves its post once it is hurt. ──");
    }

    /// <summary>The hold_post planner. Three cases, in order: hurt, so fight as an
    /// ordinary soldier from now on; someone is near the post, so strike them; nobody
    /// is near, so stand (or walk back) and do nothing else.</summary>
    private EnemyIntent PlanHoldPost(Unit enemy)
    {
        if (enemy?.CurrentTile == null || !_posts.TryGetValue(enemy, out Vector2I post))
            return PlanSoldier(enemy);

        if (enemy.Stats.Health < enemy.Stats.MaxHealth)
            return PlanSoldier(enemy);

        Unit mark = null;
        int markDist = int.MaxValue;
        foreach (var p in playerUnits)
        {
            if (p == null || !IsInstanceValid(p) || p.Stats == null || !p.Stats.IsAlive)
                continue;
            if (p.CurrentTile == null || p.HasStatus("untargetable"))
                continue;
            if (grid.Distance(p.CurrentTile.Axial, post) > PostLeash)
                continue;
            int d = grid.Distance(p.CurrentTile.Axial, enemy.CurrentTile.Axial);
            if (d < markDist)
            {
                markDist = d;
                mark = p;
            }
        }

        if (mark != null)
        {
            Vector2I markTile = mark.CurrentTile.Axial;
            int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5;
            return new EnemyIntent
            {
                Kind = IntentKind.Attack,
                TargetUnit = mark,
                TargetTile = markTile,
                ThreatTiles = { markTile },
                Value = dmg,
                BaseValue = dmg
            };
        }

        // Guard with no armour: standing on a post must not farm a damage pool
        // (the 2026-07-27 defender finding). ExecuteGuardIntent hands this to
        // TryExecuteHoldPost below.
        return new EnemyIntent
        {
            Kind = IntentKind.Guard,
            TargetUnit = enemy,
            Value = 0,
            BaseValue = 0
        };
    }

    /// <summary>From the head of ExecuteGuardIntent. True when the unit is a post-holder
    /// and its guard beat was handled here: walk back to the post if it was drawn or
    /// pushed off it, and gain nothing.</summary>
    private async Task<bool> TryExecuteHoldPost(Unit enemy)
    {
        if (enemy == null || enemy.BehaviorKey != HoldPostKey
            || !_posts.TryGetValue(enemy, out Vector2I post))
            return false;
        if (enemy.CurrentTile == null || enemy.CurrentTile.Axial == post)
            return true;
        if (!MayMove(enemy, out _))
            return true;

        await MoveTowardTile(enemy, post, quiet: true);   // stops beside the goal
        if (IsValidActor(enemy) && enemy.CurrentTile != null
            && grid.Distance(enemy.CurrentTile.Axial, post) == 1)
        {
            var home = grid.GetTile(post);
            if (home != null && !home.IsOccupied)
                enemy.TryMoveTo(grid, home);
        }
        combatUI?.AppendActionLog($"{enemy.Name} returns to its post.");
        return true;
    }
}
