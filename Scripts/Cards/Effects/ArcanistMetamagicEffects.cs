using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ArcanistMetamagicEffects.cs
//
// Purpose:        The Reviser's metamagic (class_identity_arcanist_v1 §3B,
//                 slice 14c): modifiers on the NEXT spell, built on the
//                 PersistentEffect OnSpellCast / OnSpellResolved pair that
//                 QueuedSpellModifier already uses.
//                 • widen: a single-target spell also hits every enemy
//                   adjacent to its target
//                 • twin: a single-target spell also hits the nearest other
//                   enemy within 3, each at 60%
//                 • bonus damage, bonus per buff on the target (Eldritch
//                   Catalyst)
//                 • ally mode (Peer Review): the next spell an ALLY casts
//                   within 3 of you
//                 • ally-cast payoff (Peer Review's bottom)
//                 • Extend: +range and no line of sight, read by
//                   CombatManager's targeting checks
// Layer:          Effects
// Collaborators:  PersistentEffect, CombatManager (OnSpellCast loop, range
//                 checks), Unit.NextSpellRangeBonus / NextSpellIgnoresLos
// See:            docs/class_identity_arcanist_v1.md §3B
// ============================================================

/// <summary>One queued metamagic. Lasts until the matching spell resolves, or until the
/// owner's next turn begins (TurnsRemaining 1).</summary>
public sealed class MetamagicModifier : PersistentEffect
{
    public Unit OwnerUnit;
    public string Label = "Metamagic";
    /// <summary>Applies to the next spell an ally (another unit on the owner's side)
    /// casts within <see cref="AllyRadius"/> of the owner, instead of the owner's own.</summary>
    public bool AllyOnly;
    public int AllyRadius = 3;
    public bool Widen, Twin;
    public int BonusDamage, BonusPerBuff;

    private bool _armed;
    private int _bonusApplied;
    private Unit _armedCaster;

    public override void Tick(GameState s) { TurnsRemaining--; }

    private bool Applies(GameState s, Unit caster)
    {
        if (caster == null || OwnerUnit == null || !GodotObject.IsInstanceValid(OwnerUnit))
            return false;
        if (!AllyOnly)
            return caster == OwnerUnit;
        if (caster == OwnerUnit || caster.TeamId != OwnerUnit.TeamId
            || caster.CurrentTile == null || OwnerUnit.CurrentTile == null)
            return false;
        var grid = s?.Grid;
        return grid == null || grid.Distance(caster.CurrentTile.Axial, OwnerUnit.CurrentTile.Axial) <= AllyRadius;
    }

    /// <summary>The single enemy this cast targets, or null when it is not single-target.</summary>
    private static Unit SingleEnemy(GameState s, Unit caster, TargetSet targets)
    {
        if (targets?.Items == null)
            return null;
        var units = new List<Unit>();
        foreach (var o in targets.Items)
        {
            var u = TargetingHelpers.ResolveUnit(s, o);
            if (u != null && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive && !units.Contains(u))
                units.Add(u);
        }
        return units.Count == 1 && units[0].TeamId != caster.TeamId ? units[0] : null;
    }

    public override void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (_armed || IsExpired || !Applies(s, casterUnit))
            return;
        var single = SingleEnemy(s, casterUnit, targets);
        if ((Widen || Twin) && single == null)
            return;   // waits for a single-target spell

        _armed = true;
        _armedCaster = casterUnit;
        var parts = new List<string>();

        int bonus = BonusDamage;
        if (BonusPerBuff > 0 && single != null)
        {
            int buffs = ChronoStatuses.Buffs.Count(single.HasStatus);
            bonus += BonusPerBuff * buffs;
            if (buffs > 0) parts.Add($"+{BonusPerBuff * buffs} for {buffs} buff(s)");
        }
        if (bonus > 0)
        {
            casterUnit.BonusSpellDamage += bonus;
            _bonusApplied = bonus;
            if (BonusDamage > 0) parts.Add($"+{BonusDamage} damage");
        }

        var grid = s.Grid;
        if (Widen && single?.CurrentTile != null && grid != null)
        {
            int added = 0;
            foreach (var u in s.UnitsInPlay.ToList())
            {
                if (u == null || u == single || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive
                    || u.TeamId == casterUnit.TeamId || u.CurrentTile == null)
                    continue;
                if (grid.Distance(u.CurrentTile.Axial, single.CurrentTile.Axial) != 1)
                    continue;
                targets.Items.Add(u);
                added++;
            }
            parts.Add($"widened to {added} more");
        }
        if (Twin && single?.CurrentTile != null && grid != null)
        {
            var second = s.UnitsInPlay
                .Where(u => u != null && u != single && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive
                            && u.TeamId != casterUnit.TeamId && u.CurrentTile != null
                            && grid.Distance(u.CurrentTile.Axial, single.CurrentTile.Axial) <= 3)
                .OrderBy(u => grid.Distance(u.CurrentTile.Axial, single.CurrentTile.Axial))
                .FirstOrDefault();
            if (second != null)
            {
                targets.Items.Add(second);
                var snap = s.Stack?.PeekTop()?.Snapshot;
                if (snap != null)
                    snap.DamageMultiplier *= 0.6f;
                parts.Add($"twinned onto {second.Name} at 60%");
            }
        }
        s.Log($"[{Label}] {string.Join(", ", parts)}.");
    }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (!_armed || casterUnit != _armedCaster)
            return;
        _armed = false;
        if (_bonusApplied > 0)
            casterUnit.BonusSpellDamage -= _bonusApplied;
        _bonusApplied = 0;
        TurnsRemaining = 0;   // spent
    }
}

/// <summary>Peer Review's bottom: this round, whenever an ally casts a spell within 3 of
/// you, gain Charge and draw (up to <see cref="Cap"/> times).</summary>
public sealed class AllyCastPayoff : PersistentEffect
{
    public Unit OwnerUnit;
    public int Charge, Draw, Cap, Radius = 3;
    private int _paid;

    public override void Tick(GameState s) { TurnsRemaining--; }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (_paid >= Cap || OwnerUnit == null || !GodotObject.IsInstanceValid(OwnerUnit) || casterUnit == null
            || casterUnit == OwnerUnit || casterUnit.TeamId != OwnerUnit.TeamId
            || casterUnit.CurrentTile == null || OwnerUnit.CurrentTile == null)
            return;
        var grid = s?.Grid;
        if (grid != null && grid.Distance(casterUnit.CurrentTile.Axial, OwnerUnit.CurrentTile.Axial) > Radius)
            return;
        _paid++;
        if (Charge > 0 && OwnerUnit.Attunement is ArcaneAttunement arc)
            arc.Add(Charge);
        if (Draw > 0 && OwnerUnit.DeckData != null)
        {
            OwnerUnit.DeckData.Draw(Draw);
            s.OnDrawCards?.Invoke(OwnerUnit);
        }
        s.Log($"[PeerReview] {OwnerUnit.Name} reviews {casterUnit.Name}'s spell: +{Charge} Charge, draw {Draw}.");
    }
}

/// <summary>Queue a metamagic on your (or an ally's) next spell.
/// JSON: { "type": "metamagic", "widen": bool, "twin": bool, "bonus_damage": n,
///         "bonus_per_buff": n, "ally": bool, "radius": n }</summary>
public sealed class MetamagicEffect : EffectBase
{
    public bool Widen, Twin, Ally;
    public int BonusDamage, BonusPerBuff, Radius;
    public string Label;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null || CombatSim.Active)
            return;
        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new MetamagicModifier
        {
            OwnerUnit = owner,
            Owner = caster,
            TurnsRemaining = 1,
            Label = string.IsNullOrEmpty(Label) ? "Metamagic" : Label,
            Widen = Widen,
            Twin = Twin,
            AllyOnly = Ally,
            AllyRadius = Radius > 0 ? Radius : 3,
            BonusDamage = BonusDamage,
            BonusPerBuff = BonusPerBuff,
        });
        s.Log(Ally
            ? $"[{Label}] The next spell an ally casts within {(Radius > 0 ? Radius : 3)} of {owner.Name} is revised."
            : $"[{Label}] {owner.Name}'s next spell is revised.");
    }
}

/// <summary>Peer Review's bottom. JSON: { "type": "ally_cast_payoff", "charge": n, "draw": n, "cap": n }</summary>
public sealed class AllyCastPayoffEffect : EffectBase
{
    public int Charge, Draw, Cap;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null || CombatSim.Active)
            return;
        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new AllyCastPayoff
        {
            OwnerUnit = owner, Owner = caster, TurnsRemaining = 1,
            Charge = Charge, Draw = Draw, Cap = Math.Max(1, Cap),
        });
        s.Log($"[PeerReview] This round, each spell an ally casts within 3 of {owner.Name} pays {Charge} Charge and {Draw} card(s).");
    }
}

/// <summary>Extend: your next spell this turn gains +range and ignores line of sight.
/// JSON: { "type": "extend", "range": n }</summary>
public sealed class ExtendEffect : EffectBase
{
    public int Range;
    public ExtendEffect(int range) { Range = Math.Max(1, range); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null || CombatSim.Active)
            return;
        owner.NextSpellRangeBonus = Math.Max(owner.NextSpellRangeBonus, Range);
        owner.NextSpellIgnoresLos = true;
        s.Log($"[Extend] {owner.Name}'s next spell this turn has +{owner.NextSpellRangeBonus} range and ignores line of sight.");
    }
}
