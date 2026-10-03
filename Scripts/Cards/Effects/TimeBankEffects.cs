using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// TimeBankEffects.cs
//
// Purpose:        Chronomancer effects added with the Overdrawn
//                 Time Bank and the round-start snapshot
//                 (class_identity_chronomancer_v1 §2b, §2e):
//                 • LoseForesightEffect: "Lose N Foresight (you may
//                   go Overdrawn)". Unlike spending, losing can take
//                   the bank below zero, down to the floor.
//                 • ReturnToRoundStartEffect: units go back to the
//                   tile (and optionally the HP) they had when this
//                   round began. Rewind, Recoil, Return to the Moment.
// Layer:          Effects
// Collaborators:  FateAttunement (LoseCharges), Unit.RoundStartTile /
//                 RoundStartHp (CombatManager.TimeSight.cs records
//                 them), RegisterManager (first Overdrawn note)
// See:            docs/class_identity_chronomancer_v1.md §2b, §2e
// ============================================================

/// <summary>
/// Lose N Foresight; the bank may go Overdrawn (below 0, floor
/// <see cref="FateAttunement.MinCharges"/>). While Overdrawn, the caster loses 2 HP per
/// point below zero at the start of each of its turns and gets no free Reflex.
/// JSON: { "type": "lose_foresight", "amount": n }
/// </summary>
public sealed class LoseForesightEffect : EffectBase
{
    public int Amount;
    public LoseForesightEffect(int amount) { Amount = Math.Max(0, amount); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var unit = s?.ActiveCasterUnit;
        if (unit?.Attunement is not FateAttunement fate)
        {
            s?.Log("[LoseForesight] No Time Bank on the caster. No-op.");
            return;
        }

        int before = fate.Charges;
        int lost = fate.LoseCharges(Amount);
        s.Log($"[LoseForesight] {unit.Name} loses {lost} Foresight: {before} -> {fate.Charges}"
              + (lost < Amount ? $" (the bank cannot go below {FateAttunement.MinCharges})." : "."));

        if (fate.IsOverdrawn)
            RegisterManager.Fire("chrono.overdrawn");
    }
}

/// <summary>
/// Returns units to the tile they held when this round began (and, with
/// <see cref="RestoreHp"/>, to the HP they had then).
/// <list type="bullet">
/// <item>Targets: each targeted unit; <see cref="AlliesOnly"/> ignores enemies;
/// <see cref="AllAllies"/> takes every living ally instead; <see cref="Radius"/> adds
/// every enemy within that many tiles of a targeted enemy.</item>
/// <item>If the start tile is taken by another unit, the mover stays put; with
/// <see cref="CollisionDamage"/> both units take that much.</item>
/// <item><see cref="ForesightCost"/> is spent when anything returned.</item>
/// </list>
/// JSON: { "type": "return_to_round_start", "restore_hp": bool, "allies_only": bool,
///         "all_allies": bool, "radius": n, "collision_damage": n, "foresight_cost": n }
/// </summary>
public sealed class ReturnToRoundStartEffect : EffectBase
{
    public bool RestoreHp;
    public bool AlliesOnly;
    public bool AllAllies;
    public int Radius;
    public int CollisionDamage;
    public int ForesightCost;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = s.ActiveCasterUnit;
        int casterTeam = casterUnit?.TeamId ?? 0;

        var movers = new List<Unit>();
        var seen = new HashSet<Unit>();
        void Add(Unit u)
        {
            if (u != null && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive && seen.Add(u))
                movers.Add(u);
        }

        if (AllAllies && s.UnitsInPlay != null)
        {
            foreach (var u in s.UnitsInPlay)
                if (u != null && u.TeamId == casterTeam)
                    Add(u);
        }
        else if (targets?.Items != null)
        {
            foreach (var obj in targets.Items)
            {
                var u = ResolveTargetUnit(s, obj);
                if (u == null)
                    continue;
                if (AlliesOnly && u.TeamId != casterTeam)
                {
                    s.Log($"[ReturnToRoundStart] {u.Name} is not an ally. It is not returned.");
                    continue;
                }
                Add(u);
            }

            if (Radius > 0 && s.UnitsInPlay != null)
            {
                var centers = new List<Unit>(movers);
                foreach (var u in s.UnitsInPlay)
                {
                    if (u == null || u.TeamId == casterTeam || u.CurrentTile == null || seen.Contains(u))
                        continue;
                    foreach (var c in centers)
                    {
                        if (c.TeamId != casterTeam && c.CurrentTile != null
                            && s.Grid.Distance(c.CurrentTile.Axial, u.CurrentTile.Axial) <= Radius)
                        {
                            Add(u);
                            break;
                        }
                    }
                }
            }
        }

        int returned = 0;
        foreach (var u in movers)
        {
            if (!u.Stats.IsAlive)
                continue;
            if (ReturnOne(s, u))
                returned++;
        }

        if (returned > 0 && ForesightCost > 0 && casterUnit?.Attunement is FateAttunement fate)
            fate.SpendCharges(ForesightCost);
        if (movers.Count == 0)
            s.Log("[ReturnToRoundStart] Nothing to return.");
    }

    /// <summary>True when the unit changed tile or HP.</summary>
    private bool ReturnOne(GameState s, Unit u)
    {
        bool changed = false;

        if (!u.RoundStartTile.HasValue)
        {
            s.Log($"[ReturnToRoundStart] {u.Name} has no start-of-round position (it arrived this round).");
        }
        else if (u.CurrentTile == null || u.CurrentTile.Axial != u.RoundStartTile.Value)
        {
            var dest = s.Grid.GetTile(u.RoundStartTile.Value);
            var occupant = dest?.Occupant;
            if (dest == null)
            {
                s.Log($"[ReturnToRoundStart] {u.Name}'s start tile is gone.");
            }
            else if (occupant != null && occupant != u)
            {
                if (CollisionDamage > 0 && occupant.Stats.IsAlive)
                {
                    s.Log($"[ReturnToRoundStart] {occupant.Name} stands on {u.Name}'s start tile: both take {CollisionDamage}.");
                    u.ApplyDamage(CollisionDamage);
                    if (occupant.Stats.IsAlive)
                        occupant.ApplyDamage(CollisionDamage);
                    changed = true;
                }
                else
                {
                    s.Log($"[ReturnToRoundStart] {occupant.Name} stands on {u.Name}'s start tile. It stays put.");
                }
            }
            else if (!dest.CanEnter(u))
            {
                s.Log($"[ReturnToRoundStart] {u.Name}'s start tile is blocked. It stays put.");
            }
            else
            {
                u.PlaceOnTile(dest);
                s.Log($"[ReturnToRoundStart] {u.Name} returns to {dest.Axial}.");
                changed = true;
            }
        }

        if (RestoreHp && u.Stats.IsAlive && u.RoundStartHp > 0)
        {
            int hp = Math.Clamp(u.RoundStartHp, 1, u.Stats.MaxHealth);
            if (hp != u.Stats.Health)
            {
                s.Log($"[ReturnToRoundStart] {u.Name}'s HP returns to {hp} (was {u.Stats.Health}).");
                u.Stats.Health = hp;
                u.RefreshHealthBar();
                changed = true;
            }
        }
        return changed;
    }
}
