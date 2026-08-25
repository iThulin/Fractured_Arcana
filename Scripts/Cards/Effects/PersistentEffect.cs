using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// PersistentEffect.cs
//
// Purpose:        Persistent effects: zones, auras, and other
//                 state-machines that live across turns and tick
//                 at the start of each player turn. Spawned by
//                 leaf effects (CreateMaelstromEffect,
//                 AvatarTransformEffect) and tracked on
//                 GameState.ActiveEffects.
// Layer:          Effects
// Collaborators:  GameState.cs (ActiveEffects list, Tick driver),
//                 CompositeEffects.cs (CreateMaelstromEffect and
//                 AvatarTransformEffect spawn instances here),
//                 Effect.cs (DealDamageEffect queries
//                 AvatarAuraEffect for the bonus damage stack),
//                 ElementalAttunement.cs (ElementTag mapping)
// See:            README §6, Persistent Effects,
//                 README §6, Elemental Attunement
// ============================================================

/// <summary>
/// Abstract base for any effect that ticks across turns. <see cref="Tick"/> is invoked
/// once per player turn by the combat loop; the implementation is responsible for
/// decrementing <see cref="TurnsRemaining"/>. The combat loop garbage-collects entries
/// where <see cref="IsExpired"/> is true.
/// </summary>
public abstract class PersistentEffect
{
    /// <summary>Turns this effect has left before it should be culled. Implementations decrement this in <see cref="Tick"/>.</summary>
    public int TurnsRemaining;

    /// <summary>The casting Entity. Used to determine team affiliation for friendly-fire filtering.</summary>
    public Entity Owner;

    /// <summary>Called once per player turn at start-of-turn. Implementation must decrement <see cref="TurnsRemaining"/>.</summary>
    public abstract void Tick(GameState s);

    /// <summary>Called after a spell is pushed to the stack but before its effects resolve. Use to set BonusDamage etc. Override in subclasses.</summary>
    public virtual void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets) { }
    /// <summary>Called after a spell's effects have fully resolved. Use for echoes, mana refunds, charge spending. Override in subclasses.</summary>
    public virtual void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets) { }

    /// <summary>True once <see cref="TurnsRemaining"/> reaches 0. The combat loop garbage-collects expired entries.</summary>
    public bool IsExpired => TurnsRemaining <= 0;
}

// School-specific persistent effects live in their school's effects file:
// ElementalistEffects.cs, NecromancerEffects.cs, ArcanistEffects.cs,
// EnchanterEffects.cs, ChronomancerEffects.cs, Tinker*.cs.

/// <summary>Multi-turn movespeed buff, tracked as a PersistentEffect. Because
/// <c>BonusMoveRange</c> resets every <c>StartTurn</c> and this ticks AFTER that reset
/// (StartTurn precedes the ActiveEffects tick in the turn-start sequence), the buff is
/// RE-APPLIED each turn for its duration. No cleanup subtract is needed: expiry just
/// stops the re-application, and the reset clears the value. The cast-time grant covers
/// the first turn; this covers turns 2..N.</summary>
public class MovementBuffEffect : PersistentEffect
{
    private readonly Unit _unit;
    private readonly int _amount;

    public MovementBuffEffect(Unit unit, int amount, int turns)
    {
        _unit = unit;
        _amount = amount;
        TurnsRemaining = turns;
        Owner = null; // not owner-keyed
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (TurnsRemaining >= 1 && _unit != null && Godot.GodotObject.IsInstanceValid(_unit))
        {
            _unit.Stats.BonusMoveRange += _amount;   // re-apply for this turn (after StartTurn reset)
            _unit.RefreshHealthBar();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  SHARED HELPER
// ─────────────────────────────────────────────────────────────────────────────

internal static class CastModifierHelpers
{
    /// <summary>Reads the mana cost from a resolved StackItem's cost list. Returns 0 on failure.</summary>
    internal static int ReadManaCost(StackItem item)
    {
        if (item?.Ability?.Costs == null)
            return 0;
        int total = 0;
        foreach (var c in item.Ability.Costs)
            if (c is ManaCost mc)
                total += mc.Amount;
        return total;
    }
}