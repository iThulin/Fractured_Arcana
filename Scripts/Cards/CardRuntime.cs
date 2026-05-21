using System;

// ============================================================
// CardRuntime.cs
//
// Purpose:        Runtime card model used by combat — Card, CardHalf,
//                 Ability, and the PlaySpeed enum. This is the file
//                 combat code talks to; CardData.cs is the legacy path.
// Layer:          Runtime
// Collaborators:  CardData.cs (enums), JsonCardLoader.cs (builds these),
//                 DeckManager.cs (zones / lifecycle),
//                 ScriptingInterfaces.cs (ICost, ICondition, IEffect, ITargetSelector),
//                 GameState.cs, Entity.cs
// See:            README §3 (Architecture Overview),
//                 README §5 (Card Schema Reference),
//                 README §7 — "CardData.cs vs CardRuntime.cs"
// ============================================================

/// <summary>
/// When an ability can resolve relative to the stack.
/// <c>Sorcery</c> may only be cast on your own turn at sorcery speed.
/// <c>Instant</c> may be cast at any time it is your priority.
/// <c>Reaction</c> may be cast in response to another effect resolving (used by channel variants).
/// </summary>
public enum PlaySpeed { Sorcery, Instant, Reaction }

/// <summary>
/// A physical card instance. Each Card has up to two halves (top / bottom) and a
/// per-instance <see cref="InstanceId"/> assigned at construction so multiple copies
/// of the same printed card can be tracked independently across zones.
/// </summary>
public sealed class Card
{
    /// <summary>Display name of the printed card. Used by UI, save data, and graveyard listings.</summary>
    public string CardName;

    /// <summary>
    /// Unique per-instance identifier. Distinct from <see cref="CardName"/> — two copies
    /// of the same card in a deck have the same CardName but different InstanceIds.
    /// </summary>
    public Guid InstanceId = Guid.NewGuid();

    /// <summary>
    /// The JSON id field this card was built from (e.g. "elementalist_static_field").
    /// Set by CardDatabase.Instantiate. Used by CastMasteryTracker.
    /// </summary>
    public string BlueprintId = "";

    /// <summary>Top half of the card. Always present.</summary>
    public CardHalf TopHalf;

    /// <summary>Bottom half of the card. Always present for split cards; may be null for single-effect designs.</summary>
    public CardHalf BottomHalf;

    /// <summary>Rarity tier — drives draft odds and UI border color.</summary>
    public CardRarity Rarity;
}

/// <summary>
/// Abstract base for anything castable. Concrete subclass is <see cref="CardHalf"/>;
/// future ability types (companion actions, building activations) may also derive
/// from this so they share the same cost / condition / targeting / effect pipeline.
/// </summary>
public abstract class Ability
{
    /// <summary>Display name for this ability.</summary>
    public string Name;

    /// <summary>Speed at which the ability may be played. See <see cref="PlaySpeed"/>.</summary>
    public PlaySpeed Speed = PlaySpeed.Sorcery;

    /// <summary>Costs that must be paid to cast (mana, life, exile-a-card, etc.). Empty array means free.</summary>
    public ICost[] Costs = Array.Empty<ICost>();

    /// <summary>Pre-cast validation checks (target on tile, caster has element nearby, etc.). Empty array means always legal.</summary>
    public ICondition[] Conditions = Array.Empty<ICondition>();

    /// <summary>Targeting selector for this ability. Null when the ability has no targets (e.g. self or global effects).</summary>
    public ITargetSelector Targeting;

    /// <summary>Ordered effects to resolve on cast. Effects within the array resolve top-to-bottom.</summary>
    public IEffect[] Effects = Array.Empty<IEffect>();

    /// <summary>
    /// Returns true when the caster currently satisfies every condition AND can pay every cost.
    /// Conditions are checked first by convention — they're cheaper than cost-payment validation.
    /// </summary>
    /// <param name="s">Active game state.</param>
    /// <param name="caster">The entity attempting to play this ability.</param>
    public virtual bool CanPlay(GameState s, Entity caster)
    {
        // NOTE: Conditions first, costs second. Cheaper checks gate the more expensive ones.
        foreach (var cond in Conditions)
            if (!cond.IsSatisfied(s, caster)) return false;

        foreach (var cost in Costs)
            if (!cost.CanPay(s, caster)) return false;

        return true;
    }
}

/// <summary>
/// One playable half of a <see cref="Card"/> (top or bottom). Holds rules text, school,
/// element tags, an optional channeled variant, and the snapshot function used to freeze
/// the effect's parameters at cast time so subsequent state changes don't mutate the
/// in-flight resolution.
/// </summary>
public sealed class CardHalf : Ability
{
    /// <summary>Back-pointer to the Card this half belongs to. Used for zone-transition bookkeeping.</summary>
    public Card OwnerCard;

    /// <summary>When true, resolving this half sends the owning Card to the graveyard. False for cards that bounce back to hand or stick around as persistent effects.</summary>
    public bool ConsumesCardOnResolve = true;

    /// <summary>
    /// Snapshot factory invoked at cast time. Captures any data the effect needs from
    /// the current game state (target position, attunement counts, etc.) so the resolution
    /// is deterministic even if the world changes between cast and resolve.
    /// </summary>
    public Func<GameState, Entity, EffectSnapshot> MakeSnapshot = (s, c) => new EffectSnapshot();

    /// <summary>
    /// Optional channeled variant of this half. When present, the player may pay this
    /// half's cost a second time at <see cref="PlaySpeed.Reaction"/> speed to swap in
    /// the channel variant's effect — usually a stronger or differently-shaped version
    /// of the base effect.
    /// </summary>
    public CardHalf ChannelVariant;

    /// <summary>Human-readable rules text shown on the card face. Authoritative source is the JSON's `rules_text` field.</summary>
    public string RulesText = "";

    /// <summary>School this half belongs to. May differ from the parent Card's primary school for multi-school cards.</summary>
    public CardSchool School;

    /// <summary>Pre-cast requirement keys (`"fire_tile"`, `"ally_adjacent"`, etc.) consulted by the UI to grey out illegal plays before <see cref="Ability.CanPlay"/> runs.</summary>
    public string[] Requirements;

    /// <summary>
    /// Element tags for this half (e.g. <c>"fire"</c>, <c>"ice"</c>, <c>"storm"</c>, <c>"stone"</c>).
    /// Read at cast time by the attunement system, by buff systems, and by element-aware
    /// targeting selectors. Empty array is valid — see schema documentation.
    /// </summary>
    public string[] Tags = Array.Empty<string>();

    /// <summary>
    /// Convenience accessor for the mana component of <see cref="Ability.Costs"/>.
    /// Returns 0 if no <see cref="ManaCost"/> is present in the cost array.
    /// </summary>
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
