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

    /// <summary>The UNIT that cast this item, captured at cast time (2026-07-09).
    /// The Entity alone can't identify the caster — PlayerA covers the whole
    /// party. Resolver.ResolveTop pins GameState.ActiveCasterUnit to this for
    /// the resolution, so stack-deferred casts (Reaction responses in trigger
    /// windows) resolve centered on their actual caster. Null for enemy
    /// triggered abilities and AI casts.</summary>
    public Unit CasterUnit;
}

public sealed class GameStack
{
    private readonly Stack<StackItem> _stack = new();
    public bool IsEmpty => _stack.Count == 0;
    public void Push(StackItem i) => _stack.Push(i);
    public StackItem Pop() => _stack.Pop();
    public IEnumerable<StackItem> Items => _stack;

    /// <summary>
    /// Returns the top item without popping it, or null if the stack is empty.
    /// RedirectEffect mutates Targets on the returned reference directly.
    /// </summary>
    public StackItem PeekTop() => !IsEmpty ? _stack.Peek() : null;

    /// <summary>
    /// Reverses the resolution order of all items on the stack.
    /// The item that would have resolved last now resolves first.
    /// Used by ReverseStackEffect.
    /// </summary>
    public void Reverse()
    {
        if (_stack.Count < 2)
            return;

        // Collect top → bottom, then re-push bottom → top
        var items = new List<StackItem>(_stack); // [top ... bottom]
        _stack.Clear();
        // Iterate top-to-bottom; push each → last push lands on top
        // We want original bottom on top, so don't reverse the list:
        // Push in the same order [top ... bottom] → bottom ends up on top ✓
        foreach (var item in items)
            _stack.Push(item);

        GD.Print($"[GameStack] Reversed. Top item is now '{_stack.Peek()?.Ability?.Name}'.");
    }
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

        // Pin the acting unit for this resolution (2026-07-09): effects and
        // targeting resolve "the caster" through ActiveCasterUnit. For casts
        // resolved immediately it is already set (and identical); for casts
        // that sat on the stack (Reaction responses) it was cleared after the
        // cast, and without the pin resolution fell back to the MAIN player
        // character. Restored afterward so nested/queued resolutions are clean.
        var prevCaster = s.ActiveCasterUnit;
        if (item.CasterUnit != null && GodotObject.IsInstanceValid(item.CasterUnit))
            s.ActiveCasterUnit = item.CasterUnit;
        try
        {
            foreach (var eff in item.Ability.Effects)
                eff.Resolve(s, item.Caster, item.Targets, item.Snapshot);
        }
        finally
        {
            s.ActiveCasterUnit = prevCaster;
        }

        // Set AFTER the effect loop (2026-07-10): "last resolved" must mean the
        // PREVIOUS spell while an item is resolving. Assigning before the loop
        // made rewind_last / echo / replicate effects re-resolve their own item
        // (infinite recursion -> stack-overflow crash with no log).
        s.LastResolvedItem = item;

        _bus.Emit("AbilityResolved", item);

        if (item.Ability is CardHalf half && half.ConsumesCardOnResolve)
            s.MoveCardToGraveyard(item.Caster, half.OwnerCard);
    }
}

public static class Rules
{

    public static bool CanCast(Ability a, GameState s, Entity caster)
    {
        if (a.Speed == PlaySpeed.Studied && s.Step != "Main")
            return false;

        if (a.Speed != PlaySpeed.Studied && s.EnemyPhaseContext)
        {
            var casterUnit = s.UnitsInPlay?.Find(u => u != null && u.Name == caster.Name);
            if (casterUnit?.Attunement is FateAttunement fate && fate.HasFreeReaction)
            {
                // Free response (full bank) — skip the cost check for this path only.
                // Enemy-phase gated so a leftover grant can't discount your own turn.
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

        if (a.Speed != PlaySpeed.Studied && s.EnemyPhaseContext)
        {
            var casterUnit = s.UnitsInPlay?.Find(u => u != null && u.Name == caster.Name);
            if (casterUnit?.Attunement is FateAttunement fate && fate.HasFreeReaction)
                fate.ConsumeFreeReaction();
        }

        var snap = (a as CardHalf)?.MakeSnapshot(s, caster) ?? new EffectSnapshot();
        var item = new StackItem { Ability = a, Caster = caster, Targets = targets, Snapshot = snap,
                                   CasterUnit = s.ActiveCasterUnit };

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

        // (2026-07-10 Time Bank tuning): the Foresight >= 2 Instant/Reaction discount
        // is RETIRED — cheaper casts fed leftover mana straight back into the bank,
        // a feedback loop that kept the bank pegged at 4 (playtest-confirmed).

        // Full bank free response (2026-07-10): the preselected path never
        // honored HasFreeReaction — it bypassed the affordability check but
        // still paid, and never consumed the grant. Now: skip payment entirely
        // and consume. Enemy-phase + non-Sorcery gated, mirroring CanCast.
        bool freeResponse = false;
        if (a.Speed != PlaySpeed.Studied && s.EnemyPhaseContext)
        {
            var freeUnit = s.UnitsInPlay?.Find(u => u != null && u.Name == caster.Name);
            if (freeUnit?.Attunement is FateAttunement freeFate && freeFate.HasFreeReaction)
            {
                freeResponse = true;
                freeFate.ConsumeFreeReaction();
                s.Log($"[TimeBank] Full-bank free response — {a.Name} costs nothing.");
            }
        }

        // Pay at full price, then refund the discount.
        // This avoids needing to mutate ManaCost internals.
        if (!freeResponse)
            foreach (var c in a.Costs)
                c.Pay(s, caster);

        if (manaDiscount > 0 && s.Mana.ContainsKey(caster))
        {
            int maxMana = s.ActiveCasterUnit?.Stats.MaxMana ?? 5;
            s.Mana[caster] = Math.Min(s.Mana[caster] + manaDiscount, maxMana);
            s.Log($"[CostReduction] Refunded {manaDiscount} mana (discount applied).");
        }

        // ── Chronomancer: consume queued cost reduction ──────────────────────────
        if (s.NextSpellCostReduction > 0)
        {
            manaDiscount += s.NextSpellCostReduction;
            s.NextSpellCostReduction = 0; // single-use, consumed here
            s.Log($"[CostModify] Applied {manaDiscount} mana discount.");
        }

        var snap = (a as CardHalf)?.MakeSnapshot(s, caster) ?? new EffectSnapshot();
        var item = new StackItem { Ability = a, Caster = caster, Targets = targets, Snapshot = snap,
                                   SourceCard = sourceCard, CasterUnit = s.ActiveCasterUnit };

        s.Stack.Push(item);
        s.Priority.OnStackItemAdded();
        s.Bus.Emit("AbilityCast", item);
        s.Log($"Cast (preselected) → {a.Name} [{a.Speed}] (stack size {s.StackCount()})");
        return true;
    }
}