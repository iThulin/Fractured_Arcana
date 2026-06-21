using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// ============================================================
// CombatManager.Constructs.cs
//
// Purpose:        Tinker construct subsystem as a partial of
//                 CombatManager. Adds the end-of-player-turn
//                 construct phase (auto-targeting + auto-attack,
//                 mirroring RunEnemyTurn), Heat burnout
//                 resolution, the nearest-enemy helper for
//                 player-team units, the Schematics increment
//                 hook, and the spawn-time configuration of a
//                 construct's stats/behavior.
// Layer:          System
// Collaborators:  Unit.Construct.cs (fields), TinkerAttunement.cs
//                 (Schematics), ConstructRegistry.cs (cap),
//                 CombatManager.cs (PerformRangedAttack /
//                 MoveToward / IsValidActor / CheckCombatEnd /
//                 grid / playerUnits / enemyUnits / combatUI /
//                 schoolAttunementUI / RegisterSummonHandler)
//
// WIRING REQUIRED in CombatManager.cs (see accompanying notes):
//   1. EndPlayerTurn  → make async, await RunConstructPhase()
//                       immediately before StartEnemyTurn().
//   2. HandleUnitDeath → after HonoredDeadService.RecordDeath,
//                        `if (unit.IsConstruct) RegisterConstructLoss(unit);`
//   3. RegisterSummonHandler → add the Tinker unit kinds + the
//                        cap check + ConfigureTinkerConstruct call.
// ============================================================

public partial class CombatManager
{
    // ── Per-kind stat table ─────────────────────────────────────────

    private struct TConStat
    {
        public int Hp, Speed, Armor, Dmg, Range, Setup, Burnout;
        public bool Immobile;
    }

    /// <summary>
    /// Returns true if the unit kind is a Tinker construct. Note: the Tinker
    /// "Colossus" and "Shield Wall" use distinct ids (tinker_colossus /
    /// tinker_barrier) to avoid colliding with the existing Elementalist
    /// colossus and shield_wall summons.
    /// </summary>
    private static bool IsTinkerConstructKind(string kind) => kind.ToLowerInvariant() switch
    {
        "drone" or "turret" or "cannon" or "grand_turret" or "siege_engine"
        or "sentinel" or "lattice_node" or "familiar" or "tinker_barrier"
        or "tinker_colossus" => true,
        _ => false
    };

    private static TConStat TinkerConstructStats(string kind) => kind.ToLowerInvariant() switch
    {
        //                                    Hp  Spd Arm Dmg Rng Setup  Immobile  Burnout
        "drone" => new TConStat { Hp = 6, Speed = 1, Armor = 0, Dmg = 3, Range = 2, Setup = 0, Immobile = false, Burnout = 3 },
        "turret" => new TConStat { Hp = 10, Speed = 0, Armor = 0, Dmg = 5, Range = 3, Setup = 0, Immobile = true, Burnout = 4 },
        "cannon" => new TConStat { Hp = 8, Speed = 0, Armor = 0, Dmg = 8, Range = 5, Setup = 1, Immobile = true, Burnout = 4 },
        "grand_turret" => new TConStat { Hp = 16, Speed = 0, Armor = 0, Dmg = 6, Range = 4, Setup = 1, Immobile = true, Burnout = 5 },
        "siege_engine" => new TConStat { Hp = 20, Speed = 0, Armor = 0, Dmg = 10, Range = 6, Setup = 2, Immobile = true, Burnout = 6 },
        "sentinel" => new TConStat { Hp = 8, Speed = 0, Armor = 0, Dmg = 1, Range = 1, Setup = 0, Immobile = true, Burnout = 3 },
        "lattice_node" => new TConStat { Hp = 6, Speed = 0, Armor = 0, Dmg = 0, Range = 0, Setup = 0, Immobile = true, Burnout = 3 },
        "familiar" => new TConStat { Hp = 12, Speed = 2, Armor = 0, Dmg = 3, Range = 1, Setup = 0, Immobile = false, Burnout = 4 },
        "tinker_barrier" => new TConStat { Hp = 8, Speed = 0, Armor = 0, Dmg = 0, Range = 0, Setup = 0, Immobile = true, Burnout = 0 },
        "tinker_colossus" => new TConStat { Hp = 40, Speed = 2, Armor = 0, Dmg = 13, Range = 3, Setup = 0, Immobile = false, Burnout = 8 },
        _ => new TConStat { Hp = 6, Speed = 0, Armor = 0, Dmg = 0, Range = 1, Setup = 0, Immobile = true, Burnout = 3 },
    };

    // ── Spawn-time configuration ────────────────────────────────────

    /// <summary>
    /// Stamps construct identity, behavior, and Heat onto a freshly spawned unit.
    /// Call from the summon handler AFTER AddChild/PlaceOnTile, for Tinker kinds only.
    /// The HP Schematics bonus is folded in earlier (StartMaxHealth must be set
    /// before _Ready); the damage bonus is applied here.
    /// </summary>
    private void ConfigureTinkerConstruct(Unit unit, string kind, int teamId, int schematicBonus)
    {
        if (unit == null)
            return;

        var st = TinkerConstructStats(kind);

        unit.IsConstruct = true;
        unit.SummonerTeamId = teamId;
        unit.AttackDamage = st.Dmg > 0 ? st.Dmg + schematicBonus : 0;
        unit.AttackRange = st.Range;
        unit.SetupTurnsRemaining = st.Setup;
        unit.IsImmobileConstruct = st.Immobile;
        unit.BurnoutThreshold = st.Burnout;
        unit.MoveRange = st.Immobile ? 0 : Math.Max(1, st.Speed);

        // Wire death into the standard pipeline so HandleUnitDeath fires —
        // this is what increments Schematics and runs selection cleanup.
        // (Summons are not otherwise subscribed to OnDied.)
        unit.OnDied += HandleUnitDeath;

        GD.Print($"[Construct] {unit.Name} ready — DMG:{unit.AttackDamage} RNG:{unit.AttackRange} " +
                 $"setup:{unit.SetupTurnsRemaining} immobile:{unit.IsImmobileConstruct} " +
                 $"burnout:{unit.BurnoutThreshold} (schematic +{schematicBonus}).");
    }

    /// <summary>Schematics deploy bonus for the given team (0 if no Tinker unit present).</summary>
    private int GetSchematicBonus(int teamId)
    {
        foreach (var u in playerUnits)
            if (u?.Attunement is TinkerAttunement t && u.TeamId == teamId)
                return t.DeployBonus;
        return 0;
    }

    /// <summary>Live construct cap for the given team (TinkerAttunement.ConstructCap, else default).</summary>
    private int GetConstructCap(int teamId)
    {
        foreach (var u in playerUnits)
            if (u?.Attunement is TinkerAttunement t && u.TeamId == teamId)
                return t.ConstructCap;
        return ConstructRegistry.DefaultCap;
    }

    // ── Schematics increment on construct loss ──────────────────────

    /// <summary>
    /// Called from HandleUnitDeath when a construct dies (any cause, including
    /// Heat burnout). Bumps the owner's Schematics tier so the next construct
    /// enters stronger.
    /// </summary>
    private void RegisterConstructLoss(Unit construct)
    {
        if (construct == null)
            return;

        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u))
                continue;
            if (u.Attunement is TinkerAttunement schem && u.TeamId == construct.SummonerTeamId)
            {
                schem.RegisterConstructDestroyed();
                GD.Print($"[Schematics] {u.Name} learns from a lost construct — Tier {schem.Tier}.");
                combatUI?.AppendActionLog($"[Schematics] Tier {schem.Tier} — the next build is stronger.");
                schoolAttunementUI?.Refresh();
                return;
            }
        }
    }

    // ── The construct phase ─────────────────────────────────────────

    /// <summary>
    /// Resolves every player-owned construct's action. Runs at the end of the
    /// player turn (after the player has had the chance to deploy, Overclock,
    /// or Redeploy) and before the enemy turn. Mirrors RunEnemyTurn but targets
    /// the enemy team.
    /// </summary>
    private async Task RunConstructPhase()
    {
        var constructs = playerUnits
            .Where(u => u != null && IsInstanceValid(u) && u.IsConstruct && u.Stats.IsAlive)
            .ToList();

        if (constructs.Count == 0)
            return;

        foreach (var c in constructs)
        {
            if (!IsValidActor(c))
                continue;

            // Setup delay — calibrating constructs skip (and count down).
            if (c.SetupTurnsRemaining > 0)
            {
                c.SetupTurnsRemaining--;
                combatUI?.AppendActionLog($"{c.Name} calibrating ({c.SetupTurnsRemaining} turn(s) left).");
                continue;
            }

            var target = FindNearestEnemyTo(c);
            if (target == null)
                continue;

            int activations = c.ActsTwiceThisTurn ? 2 : 1;
            for (int i = 0; i < activations; i++)
            {
                if (!IsValidActor(c) || !IsValidActor(target))
                    break;

                int dist = grid.Distance(c.CurrentTile, target.CurrentTile);

                if (dist > c.AttackRange)
                {
                    if (c.IsImmobileConstruct)
                        break;                 // emplacement can't reposition
                    await MoveToward(c, target);
                    if (!IsValidActor(c) || !IsValidActor(target))
                        break;
                    dist = grid.Distance(c.CurrentTile, target.CurrentTile);
                }

                if (dist <= c.AttackRange && c.AttackDamage > 0)
                    await PerformRangedAttack(c, target, bonusDamage: c.Heat);
                else
                    break;                     // out of range or no weapon
            }

            c.ActsTwiceThisTurn = false;

            // Corrupted variant: acting builds Heat on its own.
            if (c.PassiveHeat && IsValidActor(c))
                c.AddHeat(1);

            // Burnout check — Heat at/over threshold detonates the construct.
            if (IsValidActor(c) && c.BurnoutThreshold > 0 && c.Heat >= c.BurnoutThreshold)
                await BurnoutConstruct(c);

            if (CheckCombatEnd())
                return;
        }

        PruneDeadUnits();
    }

    /// <summary>Detonates an overheated construct: small AoE to adjacent enemies, then destroys it (feeding Schematics via the death pipeline).</summary>
    private async Task BurnoutConstruct(Unit c)
    {
        if (c?.CurrentTile == null)
            return;

        const int detonation = 4;

        combatUI?.AppendActionLog($"{c.Name} overheats and detonates!");
        GD.Print($"[Burnout] {c.Name} detonates (Heat {c.Heat}/{c.BurnoutThreshold}).");

        var center = c.CurrentTile.Axial;
        foreach (var n in grid.GetNeighbors(center))
        {
            var occ = grid.GetTile(n)?.Occupant;
            if (occ != null && occ.Stats.IsAlive && occ.TeamId != c.SummonerTeamId)
            {
                occ.ApplyDamage(detonation);
                combatUI?.AppendActionLog($"  {occ.Name} takes {detonation} from the blast.");
            }
        }

        c.KillFromEffect();   // → OnDied → HandleUnitDeath → RegisterConstructLoss
        await ToSignal(GetTree().CreateTimer(0.2f), "timeout");
    }

    // ── Targeting helper (player-team → enemy) ──────────────────────

    /// <summary>Nearest living enemy unit to the given construct, by hex distance. Mirror of FindNearestPlayerUnit for the opposite team.</summary>
    private Unit FindNearestEnemyTo(Unit source)
    {
        if (source == null || !IsInstanceValid(source) || source.CurrentTile == null)
            return null;

        Unit best = null;
        int bestDist = int.MaxValue;
        foreach (var enemy in enemyUnits)
        {
            if (enemy == null || !IsInstanceValid(enemy))
                continue;
            if (!enemy.Stats.IsAlive || enemy.CurrentTile == null)
                continue;

            int dist = grid.Distance(source.CurrentTile, enemy.CurrentTile);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = enemy;
            }
        }
        return best;
    }
}
