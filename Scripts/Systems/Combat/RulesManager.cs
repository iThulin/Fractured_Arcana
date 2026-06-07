using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// RulesManager.cs
//
// Purpose:        The combat rules engine. Hosts the GameEvent /
//                 EventBus, the stack-based card resolution
//                 (GameStack, StackItem), the priority manager,
//                 and the Resolver that pops the stack, applies
//                 effects, fires attunement bonuses, and routes
//                 cards into discard or exile.
// Layer:          System
// Collaborators:  GameStateManager.cs (the state these mutate),
//                 ScriptingInterfaces.cs (IEffect / IPredicate
//                 contracts), AttunementResolver.cs (called
//                 post-resolve), Effect.cs / CompositeEffects.cs
//                 (the effects this drives), CombatManager.cs
//                 (top-level caller)
// See:            README §3 — Architecture (stack-based resolution
//                 model is MTG-derived)
// ============================================================

/// <summary>One event published on the combat <see cref="EventBus"/>. Free-form Type string + arbitrary payload. Used for animation triggers, log routing, and UI refresh hooks rather than rules-critical signalling.</summary>
public sealed class GameEvent
{
    public string Type;
    public object Payload;
}

public sealed class EventBus
{
    public event Action<GameEvent> OnEvent;
    public void Emit(string type, object payload = null) => OnEvent?.Invoke(new GameEvent { Type = type, Payload = payload });
}

public sealed class StackItem
{
    public Ability Ability;
    public Entity Caster;
    public TargetSet Targets;
    public EffectSnapshot Snapshot;
    public Card SourceCard;
}

public sealed class GameStack
{
    private readonly Stack<StackItem> _stack = new();
    public bool IsEmpty => _stack.Count == 0;
    public void Push(StackItem i) => _stack.Push(i);
    public StackItem Pop() => _stack.Pop();
    public IEnumerable<StackItem> Items => _stack;
}

public sealed class PriorityManager
{
    public Entity Active;
    public Entity PriorityHolder;
    private int _passes = 0;
    public void ResetForNewStep(Entity active) { Active = active; PriorityHolder = active; _passes = 0; }
    public void OnStackItemAdded() { _passes = 0; }
    public bool PassPriority(GameState s)
    {
        _passes++;
        PriorityHolder = (PriorityHolder == s.PlayerA) ? s.PlayerB : s.PlayerA;
        if (_passes >= 2 && s.Stack.IsEmpty)
        { s.AdvanceStep(); _passes = 0; return true; }
        return false;
    }
}

public sealed class Resolver
{
    private readonly EventBus _bus; private readonly GameStack _stack;
    public Resolver(EventBus bus, GameStack stack) { _bus = bus; _stack = stack; }
    public void ResolveTop(GameState s)
    {
        if (_stack.IsEmpty)
            return;
        var item = _stack.Pop();
        s.LastResolvedItem = item;

        foreach (var eff in item.Ability.Effects)
            eff.Resolve(s, item.Caster, item.Targets, item.Snapshot);

        _bus.Emit("AbilityResolved", item);

        if (item.Ability is CardHalf half && half.ConsumesCardOnResolve)
            s.MoveCardToGraveyard(item.Caster, half.OwnerCard);
    }
}

public static class Rules
{

    public static bool CanCast(Ability a, GameState s, Entity caster)
    {
        if (a.Speed == PlaySpeed.Sorcery && s.Step != "Main")
            return false;

        if (a.Speed == PlaySpeed.Reaction)
        {
            var casterUnit = s.UnitsInPlay?.Find(u => u != null && u.Name == caster.Name);
            if (casterUnit?.Attunement is FateAttunement fate && fate.HasFreeReaction)
            {
                // Free Reaction — skip the CanPlay cost check for this path only.
                // ConsumeFreeReaction() is called in TryCastWithTargets after Pay().
                return true;
            }
        }

        if (!a.CanPlay(s, caster))
            return false;
        return true;
    }
    public static bool TryCast(Ability a, GameState s, Entity caster)
    {
        if (!CanCast(a, s, caster))
        { s.Log("Cast failed (timing/conditions/cost)."); return false; }

        TargetSet targets = null;
        if (a.Targeting != null && !a.Targeting.Select(s, caster, out targets))
            return false;

        foreach (var c in a.Costs)
            c.Pay(s, caster);

        if (a.Speed == PlaySpeed.Reaction)
        {
            var casterUnit = s.UnitsInPlay?.Find(u => u != null && u.Name == caster.Name);
            if (casterUnit?.Attunement is FateAttunement fate && fate.HasFreeReaction)
                fate.ConsumeFreeReaction();
        }

        var snap = (a as CardHalf)?.MakeSnapshot(s, caster) ?? new EffectSnapshot();
        var item = new StackItem { Ability = a, Caster = caster, Targets = targets, Snapshot = snap };

        s.Stack.Push(item);
        s.Priority.OnStackItemAdded();
        s.Bus.Emit("AbilityCast", item);
        s.Log($"Cast → {a.Name} [{a.Speed}] (stack size {s.StackCount()})");

        s.SpellsCastThisTurn++;

        return true;
    }

    public static bool TryCastWithTargets(Ability a, GameState s, Entity caster, TargetSet targets, Card sourceCard)
    {
        if (!CanCast(a, s, caster))
        {
            s.Log("Cast failed (timing/conditions/cost).");
            return false;
        }

        if (a.Targeting != null)
        {
            bool isAreaSpell = a.Targeting is SelectAreaTarget
                            || a.Targeting is SelectConeTarget
                            || a.Targeting is SelectLineTarget
                            || a.Targeting is SelectRingTarget
                            || a.Targeting is SelectGlobalTarget;

            if (!isAreaSpell && (targets == null || targets.Items == null || targets.Items.Count == 0))
            {
                s.Log("Cast failed (missing targets).");
                return false;
            }

            // For area spells, ensure targets is at least non-null
            if (targets == null)
                targets = new TargetSet();
        }
        else
        {
            targets = null;
        }

        int manaDiscount = 0;

        // ── Equipment: first-card reduction ────────────────────────────────────────
        if (s.ActiveCasterUnit != null && !s.ActiveCasterUnit.Stats.HasPlayedCardThisTurn)
        {
            foreach (var (tag, value) in s.ActiveCasterUnit.EquipmentPassives)
            {
                if (tag == ItemPassiveTag.FirstCardCostReduction)
                    manaDiscount += value;
            }
        }

        // ── Foresight: Instant/Reaction cost reduction at Foresight >= 2 ───────────
        if ((a.Speed == PlaySpeed.Instant || a.Speed == PlaySpeed.Reaction)
            && s.ActiveCasterUnit?.Attunement is FateAttunement fate)
        {
            manaDiscount += fate.GetInstantCostReduction();
        }

        // Pay at full price, then refund the discount.
        // This avoids needing to mutate ManaCost internals.
        foreach (var c in a.Costs)
            c.Pay(s, caster);

        if (manaDiscount > 0 && s.Mana.ContainsKey(caster))
        {
            int maxMana = s.ActiveCasterUnit?.Stats.MaxMana ?? 5;
            s.Mana[caster] = Math.Min(s.Mana[caster] + manaDiscount, maxMana);
            s.Log($"[CostReduction] Refunded {manaDiscount} mana (discount applied).");
        }

        var snap = (a as CardHalf)?.MakeSnapshot(s, caster) ?? new EffectSnapshot();
        var item = new StackItem { Ability = a, Caster = caster, Targets = targets, Snapshot = snap, SourceCard = sourceCard };

        s.Stack.Push(item);
        s.Priority.OnStackItemAdded();
        s.Bus.Emit("AbilityCast", item);
        s.Log($"Cast (preselected) → {a.Name} [{a.Speed}] (stack size {s.StackCount()})");
        return true;
    }
}