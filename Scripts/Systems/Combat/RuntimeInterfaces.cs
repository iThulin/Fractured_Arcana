using System.Collections.Generic;

public sealed class Entity { public string Name = "Player"; }

public interface ICost { bool CanPay(GameState s, Entity caster); void Pay(GameState s, Entity caster); }
public sealed class ManaCost : ICost
{
    public int Amount;
    public ManaCost(int a) { Amount = a; }

    public bool CanPay(GameState s, Entity caster)
    {
        // Active unit's mana is authoritative
        if (s.ActiveCasterUnit != null)
            return s.ActiveCasterUnit.Stats.Mana >= Amount;

        // Fallback for AI / scripted casts that don't set ActiveCasterUnit
        return s.Mana.TryGetValue(caster, out var m) && m >= Amount;
    }

    public void Pay(GameState s, Entity caster)
    {
        if (s.ActiveCasterUnit != null)
        {
            s.ActiveCasterUnit.TrySpendMana(Amount);
            // keep the dict in sync for any legacy reads
            if (s.Mana.ContainsKey(caster))
                s.Mana[caster] = s.ActiveCasterUnit.Stats.Mana;
        }
        else if (s.Mana.ContainsKey(caster))
        {
            s.Mana[caster] -= Amount;
        }
    }
}

public interface ICondition { bool IsSatisfied(GameState s, Entity caster); }
public sealed class AlwaysCondition : ICondition { public bool IsSatisfied(GameState s, Entity c) => true; }

public sealed class TargetSet { public List<object> Items = new(); }
public sealed class EffectSnapshot { }

public interface ITargetSelector { bool Select(GameState s, Entity caster, out TargetSet targets); }

