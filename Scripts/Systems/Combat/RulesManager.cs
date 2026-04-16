using Godot;
using System;
using System.Collections.Generic;

public sealed class GameEvent {
    public string Type;
    public object Payload;
}

public sealed class EventBus {
    public event Action<GameEvent> OnEvent;
    public void Emit(string type, object payload=null) => OnEvent?.Invoke(new GameEvent{Type=type, Payload=payload});
}

public sealed class StackItem {
    public Ability Ability;
    public Entity Caster;
    public TargetSet Targets;
    public EffectSnapshot Snapshot;
    public Card SourceCard;
}

public sealed class GameStack {
    private readonly Stack<StackItem> _stack = new();
    public bool IsEmpty => _stack.Count == 0;
    public void Push(StackItem i) => _stack.Push(i);
    public StackItem Pop() => _stack.Pop();
    public IEnumerable<StackItem> Items => _stack;
}

public sealed class PriorityManager {
    public Entity Active;
    public Entity PriorityHolder;
    private int _passes = 0;
    public void ResetForNewStep(Entity active){ Active=active; PriorityHolder=active; _passes=0; }
    public void OnStackItemAdded(){ _passes = 0; }
    public bool PassPriority(GameState s){
        _passes++;
        PriorityHolder = (PriorityHolder == s.PlayerA) ? s.PlayerB : s.PlayerA;
        if (_passes >= 2 && s.Stack.IsEmpty) { s.AdvanceStep(); _passes = 0; return true; }
        return false;
    }
}

public sealed class Resolver {
    private readonly EventBus _bus; private readonly GameStack _stack;
    public Resolver(EventBus bus, GameStack stack){ _bus=bus; _stack=stack; }
    public void ResolveTop(GameState s){
        if (_stack.IsEmpty) return;
        var item = _stack.Pop();

        foreach (var eff in item.Ability.Effects)
            eff.Resolve(s, item.Caster, item.Targets, item.Snapshot);

        _bus.Emit("AbilityResolved", item);

        if (item.Ability is CardHalf half && half.ConsumesCardOnResolve)
            s.MoveCardToGraveyard(item.Caster, half.OwnerCard);
    }
}

public static class Rules {
    
    public static bool CanCast(Ability a, GameState s, Entity caster){
        if (a.Speed == PlaySpeed.Sorcery && s.Step != "Main") return false;
        if (!a.CanPlay(s, caster)) return false;
        return true;
    }
    public static bool TryCast(Ability a, GameState s, Entity caster){
        if (!CanCast(a, s, caster)) { s.Log("Cast failed (timing/conditions/cost)."); return false; }

        TargetSet targets = null;
        if (a.Targeting != null && !a.Targeting.Select(s, caster, out targets)) return false;

        foreach (var c in a.Costs) c.Pay(s, caster);

        var snap = (a as CardHalf)?.MakeSnapshot(s, caster) ?? new EffectSnapshot();
        var item = new StackItem{ Ability=a, Caster=caster, Targets=targets, Snapshot=snap };

        s.Stack.Push(item);
        s.Priority.OnStackItemAdded();
        s.Bus.Emit("AbilityCast", item);
        s.Log($"Cast → {a.Name} [{a.Speed}] (stack size {s.StackCount()})");
        return true;
    }

    public static bool TryCastWithTargets(Ability a, GameState s, Entity caster, TargetSet targets)
    {
        if (!CanCast(a, s, caster))
        {
            s.Log("Cast failed (timing/conditions/cost).");
            return false;
        }

        // If the ability expects targets, require them
        if (a.Targeting != null)
        {
            if (targets == null || targets.Items == null || targets.Items.Count == 0)
            {
                s.Log("Cast failed (missing targets).");
                return false;
            }
        }
        else
        {
            // If ability expects no targets, ignore any provided
            targets = null;
        }

        foreach (var c in a.Costs) c.Pay(s, caster);

        var snap = (a as CardHalf)?.MakeSnapshot(s, caster) ?? new EffectSnapshot();
        var item = new StackItem { Ability = a, Caster = caster, Targets = targets, Snapshot = snap};

        s.Stack.Push(item);
        s.Priority.OnStackItemAdded();
        s.Bus.Emit("AbilityCast", item);
        s.Log($"Cast (preselected) → {a.Name} [{a.Speed}] (stack size {s.StackCount()})");
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
            if (targets == null || targets.Items == null || targets.Items.Count == 0)
            {
                s.Log("Cast failed (missing targets).");
                return false;
            }
        }
        else
        {
            targets = null;
        }

        foreach (var c in a.Costs) c.Pay(s, caster);

        var snap = (a as CardHalf)?.MakeSnapshot(s, caster) ?? new EffectSnapshot();
        var item = new StackItem { Ability = a, Caster = caster, Targets = targets, Snapshot = snap, SourceCard = sourceCard };

        s.Stack.Push(item);
        s.Priority.OnStackItemAdded();
        s.Bus.Emit("AbilityCast", item);
        s.Log($"Cast (preselected) → {a.Name} [{a.Speed}] (stack size {s.StackCount()})");
        return true;
    }
}