using System;

public enum PlaySpeed { Sorcery, Instant, Reaction }

public sealed class Card
{
    public string CardName;
    public Guid InstanceId = Guid.NewGuid();
    public CardHalf TopHalf;
    public CardHalf BottomHalf;
    public CardRarity Rarity;
}

public abstract class Ability
{
    public string Name;
    public PlaySpeed Speed = PlaySpeed.Sorcery;
    public ICost[] Costs = Array.Empty<ICost>();
    public ICondition[] Conditions = Array.Empty<ICondition>();
    public ITargetSelector Targeting; // null if no targets
    public IEffect[] Effects = Array.Empty<IEffect>();

    public virtual bool CanPlay(GameState s, Entity caster)
    {
        foreach (var cond in Conditions)
            if (!cond.IsSatisfied(s, caster)) return false;

        foreach (var cost in Costs)
            if (!cost.CanPay(s, caster)) return false;

        return true;
    }
}

public sealed class CardHalf : Ability
{
    public Card OwnerCard;
    public bool ConsumesCardOnResolve = true;
    public Func<GameState, Entity, EffectSnapshot> MakeSnapshot = (s, c) => new EffectSnapshot();
    public CardHalf ChannelVariant; // optional
    public string RulesText = "";
    public CardSchool School;

    /// <summary>
    /// Element tags for this spell half (e.g. "fire", "ice", "storm", "earth").
    /// Used for element pip display and gameplay interactions.
    /// </summary>
    public string[] Tags = Array.Empty<string>();

    public int ManaCost
    {
        get
        {
            foreach (var c in Costs)
                if (c is ManaCost m) return m.Amount;
            return 0;
        }
    }
}
