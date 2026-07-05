using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// ============================================================
// CombatManager.Constructs.cs
//
// Purpose:        Tinker construct subsystem as a partial of
//                 CombatManager. End-of-player-turn construct
//                 phase (auto-target + auto-attack, mirroring
//                 RunEnemyTurn), construct auras, Grand Turret
//                 line attack, Colossus drone-spawn + death nova,
//                 Heat burnout resolution, Schematics increment
//                 hook, and spawn-time configuration.
// Layer:          System
// Collaborators:  Unit.Construct.cs (fields), TinkerAttunement.cs
//                 (Schematics), ConstructRegistry.cs (cap),
//                 CombatManager.cs (PerformRangedAttack /
//                 MoveToward / IsValidActor / CheckCombatEnd /
//                 grid / playerUnits / enemyUnits / combatUI /
//                 schoolAttunementUI / RegisterSummonHandler /
//                 RefreshEnemyRoster / RefreshPlayerUnitBar)
//
// WIRING REQUIRED in CombatManager.cs (see accompanying notes):
//   1. EndPlayerTurn  → await RunConstructPhase() on the path
//                       that leads to StartEnemyTurn().
//   2. HandleUnitDeath → after HonoredDeadService.RecordDeath,
//                        `if (unit.IsConstruct) RegisterConstructLoss(unit);`
//   3. RegisterSummonHandler → cap check + ConsumeDeployBonus +
//                        Tinker unit-kind cases + ConfigureTinkerConstruct.
// ============================================================

public partial class CombatManager
{
    private static readonly Vector2I[] HexDirs6 =
    {
        new(1, 0), new(1, -1), new(0, -1),
        new(-1, 0), new(-1, 1), new(0, 1)
    };

    // ── Per-kind stat table ─────────────────────────────────────────

    private struct TConStat
    {
        public int Hp, Speed, Armor, Dmg, Range, Setup, Burnout;
        public bool Immobile;
    }

    /// <summary>
    /// True if the unit kind is a Tinker construct. Note: the Tinker "Colossus"
    /// and "Shield Wall" use distinct ids (tinker_colossus / tinker_barrier) to
    /// avoid colliding with the existing Elementalist colossus and shield_wall.
    /// </summary>
    private static bool IsTinkerConstructKind(string kind) => kind.ToLowerInvariant() switch
    {
        "drone" or "turret" or "cannon" or "grand_turret" or "siege_engine"
        or "sentinel" or "lattice_node" or "familiar" or "tinker_barrier"
        or "tinker_colossus" or "foundry" => true,
        _ => false
    };

    private static TConStat TinkerConstructStats(string kind) => kind.ToLowerInvariant() switch
    {
        //                                    Hp  Spd Arm Dmg Rng Setup  Immobile  Burnout
        "drone"           => new TConStat { Hp = 6,  Speed = 1, Armor = 0, Dmg = 3,  Range = 2, Setup = 0, Immobile = false, Burnout = 3 },
        "turret"          => new TConStat { Hp = 10, Speed = 0, Armor = 0, Dmg = 5,  Range = 3, Setup = 0, Immobile = true,  Burnout = 4 },
        "cannon"          => new TConStat { Hp = 8,  Speed = 0, Armor = 0, Dmg = 8,  Range = 5, Setup = 1, Immobile = true,  Burnout = 4 },
        "grand_turret"    => new TConStat { Hp = 16, Speed = 0, Armor = 0, Dmg = 6,  Range = 4, Setup = 1, Immobile = true,  Burnout = 5 },
        "siege_engine"    => new TConStat { Hp = 20, Speed = 0, Armor = 0, Dmg = 10, Range = 6, Setup = 2, Immobile = true,  Burnout = 6 },
        "sentinel"        => new TConStat { Hp = 8,  Speed = 0, Armor = 0, Dmg = 1,  Range = 1, Setup = 0, Immobile = true,  Burnout = 3 },
        "lattice_node"    => new TConStat { Hp = 6,  Speed = 0, Armor = 0, Dmg = 0,  Range = 0, Setup = 0, Immobile = true,  Burnout = 3 },
        "familiar"        => new TConStat { Hp = 12, Speed = 2, Armor = 0, Dmg = 3,  Range = 1, Setup = 0, Immobile = false, Burnout = 4 },
        "tinker_barrier"  => new TConStat { Hp = 8,  Speed = 0, Armor = 0, Dmg = 0,  Range = 0, Setup = 0, Immobile = true,  Burnout = 0 },
        "tinker_colossus" => new TConStat { Hp = 40, Speed = 2, Armor = 0, Dmg = 13, Range = 3, Setup = 0, Immobile = false, Burnout = 8 },
        "foundry"         => new TConStat { Hp = 30, Speed = 0, Armor = 2, Dmg = 0,  Range = 0, Setup = 0, Immobile = true,  Burnout = 0 },
        _                 => new TConStat { Hp = 6,  Speed = 0, Armor = 0, Dmg = 0,  Range = 1, Setup = 0, Immobile = true,  Burnout = 3 },
    };

    // ── Spawn-time configuration ────────────────────────────────────

    /// <summary>
    /// Stamps construct identity, behavior, auras, and Heat onto a freshly spawned
    /// unit. Call from the summon handler AFTER AddChild/PlaceOnTile, for Tinker
    /// kinds only. The HP deploy bonus is folded in earlier (StartMaxHealth must be
    /// set before _Ready); the damage bonus is applied here.
    /// </summary>
    private void ConfigureTinkerConstruct(Unit unit, string kind, int teamId, int deployBonus)
    {
        if (unit == null)
            return;

        var st = TinkerConstructStats(kind);
        string k = kind.ToLowerInvariant();

        unit.IsConstruct = true;
        unit.SummonerTeamId = teamId;
        unit.AttackDamage = st.Dmg > 0 ? st.Dmg + deployBonus : 0;
        unit.AttackRange = st.Range;
        unit.SetupTurnsRemaining = st.Setup;
        unit.IsImmobileConstruct = st.Immobile;
        unit.BurnoutThreshold = st.Burnout;
        unit.MoveRange = st.Immobile ? 0 : Math.Max(1, st.Speed);

        unit.LineAttack = k == "grand_turret";
        unit.SpawnsDronesEachTurn = k == "tinker_colossus";
        unit.DeathNova = k == "tinker_colossus";

        if (k == "sentinel") { unit.AuraArmor = 2; unit.AuraArmorRange = 1; }
        if (k == "lattice_node") { unit.AuraDamage = 2; unit.AuraDamageRange = 2; }
        if (k == "foundry") { unit.AuraDamage = 2; unit.AuraDamageRange = 99; } // The Foundry: board-wide construct damage aura

        // Familiar: bind an echo aura that replays the owner's spells while it lives.
        if (k == "familiar")
        {
            var ownerUnit = State?.ActiveCasterUnit;
            Entity ownerEntity = teamId == 0 ? Me : Opp;
            if (State?.ActiveEffects != null && ownerUnit != null)
            {
                State.ActiveEffects.Add(new FamiliarEchoAura(unit, ownerEntity, ownerUnit));
                GD.Print($"[Familiar] {unit.Name} bound to echo {ownerUnit.Name}'s spells.");
            }
        }

        // Wire death into the standard pipeline so HandleUnitDeath fires —
        // this is what increments Schematics, runs the death nova, and cleans
        // up selection. (Summons are not otherwise subscribed to OnDied.)
        unit.OnDied += HandleUnitDeath;

        GD.Print($"[Construct] {unit.Name} ready — DMG:{unit.AttackDamage} RNG:{unit.AttackRange} " +
                 $"setup:{unit.SetupTurnsRemaining} immobile:{unit.IsImmobileConstruct} " +
                 $"burnout:{unit.BurnoutThreshold} (deploy +{deployBonus}).");
    }

    /// <summary>
    /// Total deploy bonus for the given team: Schematics tier + one consumed
    /// Master Schematic pending charge. Call once per construct spawn.
    /// </summary>
    private int ConsumeDeployBonus(int teamId)
    {
        foreach (var u in playerUnits)
            if (u?.Attunement is TinkerAttunement t && u.TeamId == teamId)
                return t.DeployBonus + t.ConsumePendingBonus();
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

    // ── Schematics increment + death nova on construct loss ─────────

    private void RegisterConstructLoss(Unit construct)
    {
        if (construct == null)
            return;

        // Death nova fires while CurrentTile is still valid (OnDied runs before Die()).
        if (construct.DeathNova)
            ConstructDeathNova(construct);

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

    private void ConstructDeathNova(Unit c)
    {
        if (c?.CurrentTile == null || grid == null)
            return;

        const int nova = 6;
        combatUI?.AppendActionLog($"{c.Name} collapses in a violent discharge!");
        foreach (var n in grid.GetNeighbors(c.CurrentTile.Axial))
        {
            var occ = grid.GetTile(n)?.Occupant;
            if (occ != null && occ.Stats.IsAlive && occ.TeamId != c.SummonerTeamId)
            {
                occ.ApplyDamage(nova);
                combatUI?.AppendActionLog($"  {occ.Name} takes {nova} from the blast.");
            }
        }
    }

    // ── The construct phase ─────────────────────────────────────────

    /// <summary>
    /// Resolves every player-owned construct's action. Runs at the end of the
    /// player turn (after the player has deployed / Overclocked / Redeployed) and
    /// before the enemy turn. Mirrors RunEnemyTurn but targets the enemy team.
    /// </summary>
    private async Task RunConstructPhase()
    {
        var constructs = playerUnits
            .Where(u => u != null && IsInstanceValid(u) && u.IsConstruct && u.Stats.IsAlive)
            .ToList();

        if (constructs.Count == 0)
            return;

        // Refresh auras up front so Lattice damage boosts the attacks below and
        // Sentinel armor is up for the enemy turn that follows.
        ApplyConstructAuras();

        foreach (var c in constructs)
        {
            if (!IsValidActor(c))
                continue;

            if (c.SetupTurnsRemaining > 0)
            {
                c.SetupTurnsRemaining--;
                combatUI?.AppendActionLog($"{c.Name} calibrating ({c.SetupTurnsRemaining} turn(s) left).");
                continue;
            }

            var target = FindNearestEnemyTo(c);
            if (target != null)
            {
                int activations = c.ActsTwiceThisTurn ? 2 : 1;
                for (int i = 0; i < activations; i++)
                {
                    if (!IsValidActor(c) || !IsValidActor(target))
                        break;

                    int dist = grid.Distance(c.CurrentTile, target.CurrentTile);

                    if (dist > c.AttackRange)
                    {
                        if (c.IsImmobileConstruct)
                            break;
                        await MoveToward(c, target);
                        if (!IsValidActor(c) || !IsValidActor(target))
                            break;
                        dist = grid.Distance(c.CurrentTile, target.CurrentTile);
                    }

                    if (dist <= c.AttackRange && c.AttackDamage > 0)
                    {
                        if (c.LineAttack)
                            await ConstructLineAttack(c, target);
                        else
                            await PerformRangedAttack(c, target, bonusDamage: c.Heat);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            c.ActsTwiceThisTurn = false;

            // Colossus: fabricate a free drone each activation.
            if (c.SpawnsDronesEachTurn && IsValidActor(c))
                ColossusSpawnDrone(c);

            // Corrupted variant: acting builds Heat on its own.
            if (c.PassiveHeat && IsValidActor(c))
                c.AddHeat(1);

            // Burnout — Heat at/over threshold detonates the construct.
            if (IsValidActor(c) && c.BurnoutThreshold > 0 && c.Heat >= c.BurnoutThreshold)
                await BurnoutConstruct(c);

            if (CheckCombatEnd())
                return;
        }

        PruneDeadUnits();
    }

    /// <summary>Grand Turret: damages every enemy along the best line toward the reference target, out to range.</summary>
    private async Task ConstructLineAttack(Unit c, Unit reference)
    {
        if (c?.CurrentTile == null || reference?.CurrentTile == null)
            return;

        var center = c.CurrentTile.Axial;

        Vector2I best = HexDirs6[0];
        int bestD = int.MaxValue;
        foreach (var d in HexDirs6)
        {
            int dd = grid.Distance(center + d, reference.CurrentTile.Axial);
            if (dd < bestD) { bestD = dd; best = d; }
        }

        int dmg = c.AttackDamage + c.Heat;
        combatUI?.AppendActionLog($"{c.Name} fires a piercing volley.");

        for (int step = 1; step <= c.AttackRange; step++)
        {
            var occ = grid.GetTile(center + best * step)?.Occupant;
            if (occ != null && occ.Stats.IsAlive && occ.TeamId != c.SummonerTeamId)
            {
                occ.ApplyDamage(dmg);
                combatUI?.AppendActionLog($"  {occ.Name} takes {dmg}.");
            }
        }

        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    /// <summary>Colossus: spawn a Drone on the first walkable, empty adjacent tile. Respects the construct cap (the summon handler enforces it).</summary>
    private void ColossusSpawnDrone(Unit c)
    {
        if (c?.CurrentTile == null || State?.OnSummonRequested == null)
            return;

        foreach (var n in grid.GetNeighbors(c.CurrentTile.Axial))
        {
            var td = grid.GetTile(n);
            if (td != null && td.IsWalkable && !td.IsBlocked && td.Occupant == null)
            {
                var spawned = State.OnSummonRequested("drone", td, c.SummonerTeamId);
                if (spawned != null)
                    combatUI?.AppendActionLog($"{c.Name} fabricates a drone.");
                return;
            }
        }
    }

    /// <summary>Detonates an overheated construct: small AoE to adjacent enemies, then destroys it (feeding Schematics via the death pipeline).</summary>
    private async Task BurnoutConstruct(Unit c)
    {
        if (c?.CurrentTile == null)
            return;

        const int detonation = 4;
        combatUI?.AppendActionLog($"{c.Name} overheats and detonates!");
        GD.Print($"[Burnout] {c.Name} detonates (Heat {c.Heat}/{c.BurnoutThreshold}).");

        foreach (var n in grid.GetNeighbors(c.CurrentTile.Axial))
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

    // ── Auras ───────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes Sentinel armor auras and Lattice damage auras for the player's
    /// units. Stack-safe: each round it first removes what it granted last round,
    /// then reapplies from current sources.
    /// </summary>
    private void ApplyConstructAuras()
    {
        // 1. Strip last round's aura contributions.
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u))
                continue;
            if (u.AuraArmorReceived != 0)
            {
                u.Stats.Armor = Math.Max(0, u.Stats.Armor - u.AuraArmorReceived);
                u.AuraArmorReceived = 0;
                u.RefreshHealthBar();
            }
            if (u.AuraDamageReceived != 0)
            {
                u.AttackDamage = Math.Max(0, u.AttackDamage - u.AuraDamageReceived);
                u.AuraDamageReceived = 0;
            }
        }

        // 2. Sentinel armor auras → friendly units in range.
        foreach (var src in playerUnits)
        {
            if (src == null || !IsValidActor(src) || src.AuraArmor <= 0)
                continue;
            foreach (var u in playerUnits)
            {
                if (u == null || u == src || !u.Stats.IsAlive || u.CurrentTile == null)
                    continue;
                if (u.TeamId != src.TeamId)
                    continue;
                if (grid.Distance(src.CurrentTile, u.CurrentTile) <= src.AuraArmorRange)
                {
                    u.Stats.Armor += src.AuraArmor;
                    u.AuraArmorReceived += src.AuraArmor;
                    u.RefreshHealthBar();
                }
            }
        }

        // 3. Lattice damage auras → friendly constructs in range.
        foreach (var src in playerUnits)
        {
            if (src == null || !IsValidActor(src) || src.AuraDamage <= 0)
                continue;
            foreach (var u in playerUnits)
            {
                if (u == null || u == src || !u.IsConstruct || !u.Stats.IsAlive || u.CurrentTile == null)
                    continue;
                if (grid.Distance(src.CurrentTile, u.CurrentTile) <= src.AuraDamageRange)
                {
                    u.AttackDamage += src.AuraDamage;
                    u.AuraDamageReceived += src.AuraDamage;
                }
            }
        }
    }

    // ── Targeting helper (player-team → enemy) ──────────────────────

    /// <summary>Nearest living enemy unit to the given construct, by hex distance.</summary>
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
