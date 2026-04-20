using System.Collections.Generic;

// ============================================================
// Core scripting interfaces for the composable card system.
//
// IEffect is kept compatible with your existing interface but
// adds a Children accessor so composite effects are traversable
// (useful for logging, UI previews, and debugging).
//
// IPredicate is new — it represents a yes/no question about
// game state that Conditional/Filter effects can ask.
// ============================================================

// Extended IEffect. Your existing non-composite effects only need
// to return Array.Empty<IEffect>() from Children and they work.
public interface IEffect
{
    string[] Tags { get; }
    IEffect WithTag(string tag);
    void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap);

    // NEW: for composite effects. Leaf effects return empty.
    IEnumerable<IEffect> Children { get; }
}

// Predicates answer a yes/no question given the current cast context.
// They do not mutate state. They are used by ConditionalEffect,
// filtered targeting, triggers, and conditional costs.
public interface IPredicate
{
    bool Evaluate(PredicateContext ctx);
}

// Context passed to predicates. Carries everything a predicate
// might need to ask questions about: the game, the caster, the
// chosen targets, and (for triggers and post-effect conditionals)
// the result of the most recently resolved effect.
public sealed class PredicateContext
{
    public GameState Game;
    public Entity Caster;
    public TargetSet Targets;
    public EffectSnapshot Snapshot;

    // Set by the effect that just resolved, so downstream
    // conditionals can ask "was that lethal?" / "did it hit?"
    public EffectResult LastResult;
}

// What an effect reports back after resolving. Purely for other
// effects to read — the stack doesn't care about this.
public sealed class EffectResult
{
    public int DamageDealt;
    public bool WasLethal;
    public int TargetsHit;
    public List<object> SpawnedThings = new(); // corpses, summons, tiles
}
