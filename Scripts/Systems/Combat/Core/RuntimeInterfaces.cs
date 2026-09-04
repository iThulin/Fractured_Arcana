using System;
using System.Collections.Generic;
using Godot;

// ============================================================
// RuntimeInterfaces.cs
//
// Purpose:        Core runtime interfaces and shared types
//                 referenced by the card scripting system:
//                 Entity, ICost (+ ManaCost), ICondition (+
//                 AlwaysCondition), TargetSet, EffectSnapshot,
//                 ITargetSelector. Cards' typed cost/condition/
//                 target arrays use these contracts.
// Layer:          Runtime
// Collaborators:  ScriptingInterfaces.cs (companion IEffect /
//                 IPredicate types), CardRuntime.cs (Ability
//                 holds ICost[] etc.), Effect.cs, Unit.cs
//                 (referenced via GameState.ActiveCasterUnit)
// See:            README §5; card schema fields map onto these
// ============================================================

/// <summary>Lightweight identity tag used to distinguish "Player A" vs "Player B" in the rules engine. Real units are <see cref="Unit"/> instances; an Entity is the level above that, the controller.</summary>
public sealed class Entity { public string Name = "Player"; }

public interface ICost { bool CanPay(GameState s, Entity caster); void Pay(GameState s, Entity caster); }
public sealed class ManaCost : ICost
{
    public int Amount;
    public ManaCost(int a) { Amount = a; }

    /// <summary>U3e (tithe_aura): the price actually charged, after the enemy mana
    /// tax. ONE function, called from BOTH CanPay and Pay, because a tax applied at payment
    /// but not at affordability lets the player cast a spell they cannot afford and
    /// fall to zero mana having "paid" four. (The existing DISCOUNT deliberately does
    /// NOT live here: RulesManager pays full price and refunds afterwards, so a
    /// discount can never make an unaffordable spell castable. A tax cannot use that
    /// shape, which is why it goes in the cost object instead of the cast path.)
    ///
    /// Rulings baked in, both 2026-07-28:
    /// - PLAYER-SIDE ONLY. Enemies never route through ManaCost; their casts go via
    ///   ApplyCasterRider. But AI/scripted casts DO reach the Entity fallback below,
    ///   and taxing those would be silent friendly fire.
    /// - CLAMPED TO MaxMana (spec §9 decision 1). Unclamped, a 3-cost half under a +1
    ///   tithe at MaxMana 3 becomes literally UNCASTABLE: a lockout on the top of the
    ///   curve, not a tax on it. Clamped, the tithe bites exactly where the player
    ///   reads it as a tax: it deletes the two-spell turn and leaves the one big spell
    ///   payable. A half already costing MaxMana is therefore untaxed, by design.
    /// - Free (0-cost) halves stay free. A half with no ManaCost in its Costs array is
    ///   untaxable by construction, so taxing 0-cost halves but not cost-less ones
    ///   would be a distinction the player cannot see.</summary>
    public static int EffectiveAmount(GameState s, int baseAmount)
    {
        if (s == null || baseAmount <= 0)
            return baseAmount;

        int amount = baseAmount;
        var u = s.ActiveCasterUnit;

        int tax = s.PlayerSpellCostIncrease;
        if (tax > 0 && u != null && u.IsPlayerControlled)
        {
            int ceiling = Math.Max(baseAmount, u.Stats.MaxMana);
            amount = Math.Min(baseAmount + tax, ceiling);
        }

        // Per-card discount (2026-07-29): the card being priced is pinned on
        // GameState.CostContextCard by the cast path and the UI provider, the same
        // one-formula-for-CanPay-Pay-and-pips discipline the tithe established.
        // Applied AFTER the tithe (a taxed, discounted card nets out), floored at 0.
        // Free halves stay free and cannot go negative: a discount is not a refund.
        int discount = s.GetCardDiscount(s.CostContextCard);
        if (discount > 0)
            amount = Math.Max(0, amount - discount);

        return amount;
    }

    public bool CanPay(GameState s, Entity caster)
    {
        int due = EffectiveAmount(s, Amount);

        // Active unit's mana is authoritative
        if (s.ActiveCasterUnit != null)
        {
            int available = s.ActiveCasterUnit.Stats.Mana;
            // Time Bank (2026-07-10): during the enemy phase, banked Foresight
            // backs the cost 1:1. Only Reactions are castable then, so the
            // phase flag alone gates it.
            if (s.EnemyPhaseContext && s.ActiveCasterUnit.Attunement is FateAttunement fateAvail)
                available += fateAvail.Charges;
            return available >= due;
        }

        // Fallback for AI / scripted casts that don't set ActiveCasterUnit
        return s.Mana.TryGetValue(caster, out var m) && m >= due;
    }

    public void Pay(GameState s, Entity caster)
    {
        int due = EffectiveAmount(s, Amount);

        if (s.ActiveCasterUnit != null)
        {
            var u = s.ActiveCasterUnit;
            if (due > Amount)
                GD.Print($"[Tithe] {u.Name} pays {due} for a {Amount}-cost half (+{due - Amount} tithe).");

            int fromMana = Math.Min(due, u.Stats.Mana);
            if (fromMana > 0)
                u.TrySpendMana(fromMana);

            int shortfall = due - fromMana;
            if (shortfall > 0 && s.EnemyPhaseContext && u.Attunement is FateAttunement fatePay)
            {
                fatePay.SpendCharges(shortfall);
                GD.Print($"[TimeBank] {u.Name} pays {shortfall} from Foresight (bank now {fatePay.Charges}).");
            }

            // keep the dict in sync for any legacy reads
            if (s.Mana.ContainsKey(caster))
                s.Mana[caster] = u.Stats.Mana;
        }
        else if (s.Mana.ContainsKey(caster))
        {
            s.Mana[caster] -= due;
        }
    }
}

public interface ICondition { bool IsSatisfied(GameState s, Entity caster); }
public sealed class AlwaysCondition : ICondition { public bool IsSatisfied(GameState s, Entity c) => true; }

public sealed class TargetSet
{
    public List<object> Items = new();

    /// <summary>How the cast that built this set travels (see <see cref="Delivery"/>).
    /// Set by the selector; read by DealDamageEffect so Bolt cards feed cover armour.
    /// Untyped for every selector that does not say otherwise, which keeps every
    /// pre-cover cast byte-for-byte unchanged.</summary>
    public Delivery Delivery = Delivery.Untyped;

    /// <summary>For Burst casts: every tile the fill reached, obstacles included, so
    /// damage effects can erode breakable cover in the blast (map_pressure_v2).</summary>
    public HashSet<Vector2I> BurstTiles;
}
public sealed class EffectSnapshot
{
    public float DamageMultiplier = 1.0f;

    /// <summary>Choose-one (2026-07-29): which option of a ChooseOneEffect this cast
    /// selected. Lives on the snapshot, not on GameState, because the snapshot
    /// travels with the StackItem: a Reaction cast while this spell waits on the
    /// stack cannot clobber it. -1 = no choice was made (AI cast, headless test);
    /// ChooseOneEffect resolves option 0 and says so.</summary>
    public int ChosenOption = -1;
}

public interface ITargetSelector { bool Select(GameState s, Entity caster, out TargetSet targets); }

