using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ArcanistFinaleEffects.cs
//
// Purpose:        The last Arcanist cards (class_identity_arcanist_v1 §3,
//                 slice 14e):
//                 • damage_per_spells_cast: Run-On Sentence
//                 • humane_clause: the next spell ignores its drawbacks
//                   (self-damage, damage to allies, Rooted, Charge lockout)
//                 • strip_buffs: every buff, Charge per buff
//                 • revised_edition: the legendary. Ally spells resolving
//                   within 3 are copied (the half cast) for 1 Charge; enemy
//                   blasts landing within 3 are copied back at the nearest
//                   enemy (CombatManager.Annotator.cs)
// Layer:          Effects
// Collaborators:  Unit.ApplyDamage / ApplyStatus (HumaneClause reads),
//                 ArcaneAttunement (lock bypass), GameState.LastResolvedItem
// See:            docs/class_identity_arcanist_v1.md §3A, §3C, Legendary
// ============================================================

/// <summary>The Humane Clause's window: set while the protected spell resolves.</summary>
public static class HumaneClause
{
    public static Unit ActiveCaster;

    /// <summary>True when this unit is on the protected caster's side during the window.</summary>
    public static bool Shields(Unit u) =>
        ActiveCaster != null && u != null && GodotObject.IsInstanceValid(ActiveCaster) && u.TeamId == ActiveCaster.TeamId;
}

/// <summary>Deal per-spell damage: <see cref="Per"/> x spells you have cast this turn
/// (this one included). JSON: { "type": "damage_per_spells_cast", "per": n }</summary>
public sealed class DamagePerSpellsCastEffect : EffectBase
{
    public int Per;
    public DamagePerSpellsCastEffect(int per) { Per = Math.Max(1, per); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        int n = Math.Max(1, s?.SpellsCastThisTurn ?? 1) * Per;
        new DealDamageEffect(n).Resolve(s, caster, targets, snap);
    }
}

/// <summary>Your next spell this turn ignores its drawbacks.
/// JSON: { "type": "humane_clause", "charge": n }</summary>
public sealed class HumaneClauseEffect : EffectBase
{
    public int Charge;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null || CombatSim.Active)
            return;
        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new HumaneClauseModifier { OwnerUnit = owner, Owner = caster, TurnsRemaining = 1 });
        if (Charge > 0 && owner.Attunement is ArcaneAttunement arc)
            arc.Add(Charge);
        s.Log($"[HumaneClause] {owner.Name}'s next spell this turn ignores its drawbacks.");
    }
}

public sealed class HumaneClauseModifier : PersistentEffect
{
    public Unit OwnerUnit;
    private bool _armed;

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (HumaneClause.ActiveCaster == OwnerUnit)
            HumaneClause.ActiveCaster = null;   // the protected spell never resolved
    }

    public override void OnSpellCast(GameState s, Unit caster, TargetSet targets)
    {
        if (_armed || IsExpired || caster != OwnerUnit)
            return;
        _armed = true;
        HumaneClause.ActiveCaster = caster;
        s.Log("[HumaneClause] This spell's drawbacks are struck out.");
    }

    public override void OnSpellResolved(GameState s, Unit caster, TargetSet targets)
    {
        if (!_armed || caster != OwnerUnit)
            return;
        _armed = false;
        HumaneClause.ActiveCaster = null;
        TurnsRemaining = 0;
    }
}

/// <summary>Strip every buff from each target; gain Charge per buff stripped.
/// JSON: { "type": "strip_buffs", "charge_per": n }</summary>
public sealed class StripBuffsEffect : EffectBase
{
    public int ChargePer;
    public StripBuffsEffect(int chargePer) { ChargePer = Math.Max(0, chargePer); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets?.Items == null || CombatSim.Active)
            return;
        var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
        int total = 0;
        foreach (var o in targets.Items)
        {
            var u = ResolveTargetUnit(s, o);
            if (u == null || !GodotObject.IsInstanceValid(u) || u.TeamId == (owner?.TeamId ?? 0))
                continue;
            foreach (var b in ChronoStatuses.Buffs)
                if (u.HasStatus(b))
                {
                    u.RemoveStatus(b);
                    total++;
                }
            u.RefreshHealthBar();
        }
        if (total > 0 && ChargePer > 0 && owner?.Attunement is ArcaneAttunement arc)
            arc.Add(total * ChargePer);
        s.Log($"[StripBuffs] {total} buff(s) struck out" + (ChargePer > 0 ? $": +{total * ChargePer} Charge." : "."));
    }
}

/// <summary>The Revised Edition: for the rest of the fight, an ally's spell resolving within
/// <see cref="Radius"/> of you is copied (the half that was cast, at the same targets) for 1 Charge;
/// an enemy blast landing within <see cref="Radius"/> is copied back (CombatManager).
/// JSON: { "type": "revised_edition", "radius": n, "bonus": n }</summary>
public sealed class RevisedEditionEffect : EffectBase
{
    public int Radius = 3, Bonus;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null || CombatSim.Active)
            return;
        owner.RevisedEditionRadius = Math.Max(owner.RevisedEditionRadius, Radius);
        owner.RevisedEditionBonus = Math.Max(owner.RevisedEditionBonus, Bonus);
        s.ActiveEffects ??= new List<PersistentEffect>();
        if (!s.ActiveEffects.Any(e => e is RevisedEditionHook h && h.OwnerUnit == owner))
            s.ActiveEffects.Add(new RevisedEditionHook { OwnerUnit = owner, Owner = caster, TurnsRemaining = 99999 });
        s.Log($"[RevisedEdition] For the rest of the fight, spells resolving within {owner.RevisedEditionRadius} of {owner.Name} can be copied for 1 Charge.");
    }
}

public sealed class RevisedEditionHook : PersistentEffect
{
    public Unit OwnerUnit;
    private static bool _copying;

    public override void Tick(GameState s) { }

    public override void OnSpellResolved(GameState s, Unit caster, TargetSet targets)
    {
        if (_copying || OwnerUnit == null || !GodotObject.IsInstanceValid(OwnerUnit) || !OwnerUnit.Stats.IsAlive
            || caster == null || caster == OwnerUnit || caster.TeamId != OwnerUnit.TeamId
            || caster.CurrentTile == null || OwnerUnit.CurrentTile == null || s?.Grid == null)
            return;
        if (s.Grid.Distance(caster.CurrentTile.Axial, OwnerUnit.CurrentTile.Axial) > OwnerUnit.RevisedEditionRadius)
            return;
        if (OwnerUnit.Attunement is not ArcaneAttunement arc || arc.Charge < 1)
            return;
        // Copy the half that was cast: its targets were chosen for it. (A top-half copy at
        // a bottom half's targets could aim a bolt at the ally who cast a self spell.)
        var half = s.LastResolvedItem?.Ability as CardHalf;
        if (half?.Effects == null || half.Targeting is SelectTwoStepTarget)
            return;

        arc.Spend(1);
        var prevCaster = s.ActiveCasterUnit;
        var prevName = s.ResolvingAbilityName;
        _copying = true;
        try
        {
            s.ActiveCasterUnit = OwnerUnit;
            s.ResolvingAbilityName = half.Name;
            OwnerUnit.BonusSpellDamage += OwnerUnit.RevisedEditionBonus;
            var snap = new EffectSnapshot();
            foreach (var e in half.Effects)
                e?.Resolve(s, s.PlayerA, targets, snap);
            s.Log($"[RevisedEdition] {OwnerUnit.Name} copies {caster.Name}'s {half.Name} (-1 Charge).");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RevisedEdition] copy of {half.Name} threw: {ex.Message}");
        }
        finally
        {
            OwnerUnit.BonusSpellDamage -= OwnerUnit.RevisedEditionBonus;
            s.ActiveCasterUnit = prevCaster;
            s.ResolvingAbilityName = prevName;
            _copying = false;
        }
    }
}
