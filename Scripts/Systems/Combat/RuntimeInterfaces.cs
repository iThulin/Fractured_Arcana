using System.Collections.Generic;

public sealed class Entity { public string Name = "Player"; }

public interface ICost { bool CanPay(GameState s, Entity caster); void Pay(GameState s, Entity caster); }
public sealed class ManaCost : ICost
{
    public int Amount;
    public ManaCost(int a) { Amount = a; }
    public bool CanPay(GameState s, Entity caster) => s.Mana[caster] >= Amount;
    public void Pay(GameState s, Entity caster) { s.Mana[caster] -= Amount; }
    
}

public interface ICondition { bool IsSatisfied(GameState s, Entity caster); }
public sealed class AlwaysCondition : ICondition { public bool IsSatisfied(GameState s, Entity c) => true; }

public sealed class TargetSet { public List<object> Items = new(); }
public sealed class EffectSnapshot { }

public interface ITargetSelector { bool Select(GameState s, Entity caster, out TargetSet targets); }

public interface IEffect
{
    string[] Tags { get; }
    IEffect WithTag(string tag);
    void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap);
}