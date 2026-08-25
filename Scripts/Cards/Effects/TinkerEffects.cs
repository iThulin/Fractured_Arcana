using System;
using System.Linq;

// ============================================================
// TinkerEffects.cs
//
// Purpose:        Tinker school IEffect / IPredicate
//                 implementations that don't depend on the
//                 Conduit Link layer or persistent effects.
//                 Each is registered in CardScriptRegistry
//                 .RegisterBuiltins() (see accompanying block).
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase), ScriptingInterfaces.cs
//                 (IEffect / IPredicate / PredicateContext),
//                 ConstructRegistry.cs, TinkerAttunement.cs,
//                 Unit.Construct.cs, GameState.cs
// Notes:          The casting Unit is read from GameState
//                 .ActiveCasterUnit (set by CombatManager before
//                 resolution). Effects ignore EffectSnapshot.
// ============================================================

/// <summary>Shared helpers for resolving Tinker targets and the casting context.</summary>
internal static class TinkerFx
{
    public static Unit ResolveUnit(GameState s, object obj)
    {
        if (obj is Unit u) return u;
        if (obj is TileData td) return td.Occupant;
        if (obj is HexTile tv) return s?.Grid?.GetTile(tv.Axial)?.Occupant;
        return null;
    }

    public static int CasterTeam(GameState s) => s?.ActiveCasterUnit?.TeamId ?? 0;

    public static TinkerAttunement Schematics(GameState s) =>
        s?.ActiveCasterUnit?.Attunement as TinkerAttunement;

    /// <summary>First target in the set that is a living friendly construct of the caster.</summary>
    public static Unit FirstFriendlyConstruct(GameState s, TargetSet targets)
    {
        if (targets == null) return null;
        int team = CasterTeam(s);
        foreach (var obj in targets.Items)
        {
            var u = ResolveUnit(s, obj);
            if (u != null && u.IsConstruct && u.SummonerTeamId == team && u.Stats.IsAlive)
                return u;
        }
        return null;
    }
}

// ── Overclock ───────────────────────────────────────────────────────
/// <summary>Targets a friendly construct: it acts twice this construct phase and gains Heat. Heat is the opt-in push toward burnout.</summary>
public sealed class OverclockEffect : EffectBase
{
    private readonly int _heat;
    public OverclockEffect(int heat) { _heat = heat; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var target = TinkerFx.FirstFriendlyConstruct(s, targets);
        if (target == null)
        {
            s?.Log("[Overclock] No friendly construct targeted.");
            return;
        }
        target.ActsTwiceThisTurn = true;
        target.AddHeat(_heat);
        s?.Log($"[Overclock] {target.Name} will act twice (+{_heat} Heat → {target.Heat}/{target.BurnoutThreshold}).");
    }
}

// ── Salvage ─────────────────────────────────────────────────────────
/// <summary>Destroys a friendly construct; the caster gains armor equal to its remaining HP and draws cards.</summary>
public sealed class SalvageConstructEffect : EffectBase
{
    private readonly int _draw;
    public SalvageConstructEffect(int draw) { _draw = draw; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var target = TinkerFx.FirstFriendlyConstruct(s, targets);
        if (target == null)
        {
            s?.Log("[Salvage] No friendly construct targeted.");
            return;
        }

        int salvaged = target.Stats.Health;
        var casterUnit = s?.ActiveCasterUnit;
        if (casterUnit != null)
        {
            casterUnit.Stats.Armor += salvaged;
            casterUnit.RefreshHealthBar();
            if (_draw > 0 && casterUnit.DeckData != null)
            {
                casterUnit.DeckData.Draw(_draw);
                s.OnDrawCards?.Invoke(casterUnit);
            }
        }

        target.KillFromEffect();
        s?.Log($"[Salvage] {target.Name} stripped for {salvaged} armor" +
               (_draw > 0 ? $" and {_draw} card(s)." : "."));
    }
}

// ── Emergency Scuttle ───────────────────────────────────────────────
/// <summary>Destroys all of the caster's constructs; each detonates for AoE damage to adjacent enemies.</summary>
public sealed class ScuttleConstructsEffect : EffectBase
{
    private readonly int _blast;
    public ScuttleConstructsEffect(int blast) { _blast = blast; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        int team = TinkerFx.CasterTeam(s);
        var constructs = ConstructRegistry.All(s, team).ToList();

        foreach (var c in constructs)
        {
            if (c?.CurrentTile != null && s.Grid != null)
            {
                foreach (var n in s.Grid.GetNeighbors(c.CurrentTile.Axial))
                {
                    var occ = s.Grid.GetTile(n)?.Occupant;
                    if (occ != null && occ.Stats.IsAlive && occ.TeamId != team)
                        occ.ApplyDamage(_blast);
                }
            }
            c.KillFromEffect();
        }

        s?.Log($"[Scuttle] Detonated {constructs.Count} construct(s) for {_blast} each.");
    }
}

// ── Repair Pulse ────────────────────────────────────────────────────
/// <summary>Heals all of the caster's constructs by a flat amount (clamped to max HP).</summary>
public sealed class RepairConstructsEffect : EffectBase
{
    private readonly int _amount;
    public RepairConstructsEffect(int amount) { _amount = amount; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        int team = TinkerFx.CasterTeam(s);
        int healed = 0;
        foreach (var c in ConstructRegistry.All(s, team).ToList())
        {
            c.Stats.Health = Math.Min(c.Stats.MaxHealth, c.Stats.Health + _amount);
            c.RefreshHealthBar();
            healed++;
        }
        s?.Log($"[Repair] Mended {healed} construct(s) for {_amount}.");
    }
}

// ── Capacity ────────────────────────────────────────────────────────
/// <summary>Raises the caster's simultaneous-construct cap (use a large value for "unlimited").</summary>
public sealed class SetConstructCapEffect : EffectBase
{
    private readonly int _cap;
    public SetConstructCapEffect(int cap) { _cap = cap; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var att = TinkerFx.Schematics(s);
        if (att == null)
        {
            s?.Log("[Capacity] Caster has no Schematics ledger.");
            return;
        }
        att.ConstructCap = Math.Max(att.ConstructCap, _cap);
        s?.Log($"[Capacity] Construct cap raised to {att.ConstructCap}.");
    }
}

// ── Master Schematic ────────────────────────────────────────────────
/// <summary>Queues a temporary deploy bonus: the next N constructs enter with +amount HP and primary stat.</summary>
public sealed class MasterSchematicEffect : EffectBase
{
    private readonly int _charges, _amount;
    public MasterSchematicEffect(int charges, int amount) { _charges = charges; _amount = amount; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var att = TinkerFx.Schematics(s);
        if (att == null)
        {
            s?.Log("[MasterSchematic] Caster has no Schematics ledger.");
            return;
        }
        att.AddPendingBonus(_charges, _amount);
        s?.Log($"[MasterSchematic] Next {_charges} construct(s) deploy with +{_amount}.");
    }
}

// ── Full Salvo ──────────────────────────────────────────────────────
/// <summary>
/// Every ready construct the caster controls immediately fires at the nearest
/// living enemy within its attack range (Heat bonus included). Constructs still
/// in setup, or with no enemy in range, hold fire. Does not consume the normal
/// construct-phase activation.
/// </summary>
public sealed class ConstructVolleyEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;

        int team = TinkerFx.CasterTeam(s);
        int shots = 0;

        foreach (var c in ConstructRegistry.All(s, team).ToList())
        {
            if (c.SetupTurnsRemaining > 0 || c.AttackDamage <= 0 || c.CurrentTile == null)
                continue;

            Unit target = null;
            int bestDist = int.MaxValue;
            foreach (var u in s.UnitsInPlay)
            {
                if (u == null || !u.Stats.IsAlive || u.TeamId == team || u.CurrentTile == null)
                    continue;
                int d = s.Grid.Distance(c.CurrentTile.Axial, u.CurrentTile.Axial);
                if (d <= c.AttackRange && d < bestDist)
                {
                    bestDist = d;
                    target = u;
                }
            }

            if (target == null)
                continue;

            int dmg = c.AttackDamage + c.Heat;
            target.ApplyDamage(dmg);
            shots++;
            s.Log($"[Salvo] {c.Name} fires at {target.Name} for {dmg}.");
        }

        s?.Log($"[Salvo] {shots} construct(s) opened fire.");
    }
}

// ── Conduit Feedback ────────────────────────────────────────────────
/// <summary>Damages the target for a per-construct amount times the caster's live construct count, optionally capped.</summary>
public sealed class DamagePerConstructEffect : EffectBase
{
    private readonly int _per, _max;

    public DamagePerConstructEffect(int per, int max)
    {
        _per = Math.Max(0, per);
        _max = max;   // 0 = uncapped
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        Unit target = null;
        if (targets != null)
        {
            foreach (var o in targets.Items)
            {
                var u = TinkerFx.ResolveUnit(s, o);
                if (u != null && u.Stats.IsAlive) { target = u; break; }
            }
        }

        if (target == null)
        {
            s?.Log("[Feedback] No target.");
            return;
        }

        int count = ConstructRegistry.Count(s, TinkerFx.CasterTeam(s));
        int dmg = _per * count;
        if (_max > 0)
            dmg = Math.Min(dmg, _max);

        if (dmg <= 0)
        {
            s?.Log("[Feedback] No constructs. The feedback loop is open.");
            return;
        }

        target.ApplyDamage(dmg);
        s?.Log($"[Feedback] {count} construct(s) discharge into {target.Name} for {dmg}.");
    }
}

// ── Etched Masterwork ───────────────────────────────────────────────
/// <summary>Permanently improves a friendly construct: +HP (max and current), +attack damage, +attack range.</summary>
public sealed class EnhanceConstructEffect : EffectBase
{
    private readonly int _hp, _damage, _range;

    public EnhanceConstructEffect(int hp, int damage, int range)
    {
        _hp = Math.Max(0, hp);
        _damage = Math.Max(0, damage);
        _range = Math.Max(0, range);
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var target = TinkerFx.FirstFriendlyConstruct(s, targets);
        if (target == null)
        {
            s?.Log("[Masterwork] No friendly construct targeted.");
            return;
        }

        target.Stats.MaxHealth += _hp;
        target.Stats.Health += _hp;
        target.AttackDamage += _damage;
        target.AttackRange += _range;
        target.RefreshHealthBar();

        s?.Log($"[Masterwork] {target.Name} rebuilt: +{_hp} HP, +{_damage} DMG, +{_range} RNG.");
    }
}

// ── Predicates ──────────────────────────────────────────────────────
/// <summary>True if the caster controls at least one construct.</summary>
public sealed class HasConstructPredicate : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
        => ConstructRegistry.Has(ctx?.Game, ctx?.Game?.ActiveCasterUnit?.TeamId ?? 0);
}

/// <summary>True if the caster controls at least `min` constructs.</summary>
public sealed class ConstructCountPredicate : IPredicate
{
    private readonly int _min;
    public ConstructCountPredicate(int min) { _min = min; }

    public bool Evaluate(PredicateContext ctx)
        => ConstructRegistry.Count(ctx?.Game, ctx?.Game?.ActiveCasterUnit?.TeamId ?? 0) >= _min;
}

/// <summary>True if the current primary target stands within 1 tile of a construct the caster controls.</summary>
public sealed class TargetAdjacentToConstructPredicate : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
    {
        var s = ctx?.Game;
        if (s?.Grid == null || ctx.Targets == null)
            return false;

        Unit target = null;
        foreach (var o in ctx.Targets.Items)
        {
            var u = TinkerFx.ResolveUnit(s, o);
            if (u != null && u.Stats.IsAlive) { target = u; break; }
        }
        if (target?.CurrentTile == null)
            return false;

        int team = s.ActiveCasterUnit?.TeamId ?? 0;
        foreach (var c in ConstructRegistry.All(s, team))
        {
            if (c.CurrentTile != null &&
                s.Grid.Distance(target.CurrentTile.Axial, c.CurrentTile.Axial) <= 1)
                return true;
        }
        return false;
    }
}
