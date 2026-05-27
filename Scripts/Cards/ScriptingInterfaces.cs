using System.Collections.Generic;

// ============================================================
// ScriptingInterfaces.cs
//
// Purpose:        Core scripting interfaces for the composable
//                 card system — IEffect, IPredicate, plus the
//                 PredicateContext and EffectResult helpers that
//                 carry data between sibling effects in a
//                 sequence.
// Layer:          Runtime
// Collaborators:  Effect.cs, CompositeEffects.cs (IEffect
//                 implementations), CorePredicates.cs
//                 (IPredicate implementations),
//                 JsonCardLoader.cs (resolves JSON to these
//                 interfaces), CardRuntime.cs (CardHalf holds
//                 IEffect/IPredicate arrays)
// See:            README §5.4 (Effect Types),
//                 README §5.5 (Predicate Types)
// ============================================================

/// <summary>
/// Contract every effect implements. <see cref="Resolve"/> applies the effect to the
/// game state; <see cref="Children"/> exposes nested effects for composite types so
/// the tree can be walked for logging, UI previews, and debugging. Leaf effects return
/// <c>Array.Empty&lt;IEffect&gt;()</c> from <see cref="Children"/>.
/// </summary>
public interface IEffect
{
    /// <summary>Tags annotating this effect (e.g. "Damage", "Movement"). Used for filtering, log routing, and statistics.</summary>
    string[] Tags { get; }

    /// <summary>Fluent helper: sets the tag and returns <c>this</c>. Used during registration to inline tag declarations.</summary>
    IEffect WithTag(string tag);

    /// <summary>Apply the effect to game state. Should be deterministic given <paramref name="snap"/>; do not re-read live state for the parameters captured at cast time.</summary>
    void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap);

    /// <summary>Nested child effects for composite types. Leaf effects return empty.</summary>
    IEnumerable<IEffect> Children { get; }
}

/// <summary>
/// Contract for predicates — pure functions over game state that return yes/no. Predicates
/// must not mutate state. Used by <c>ConditionalEffect</c>, filtered targeting, triggers,
/// and conditional costs.
/// </summary>
public interface IPredicate
{
    /// <summary>Evaluate the predicate against the given context. Must be pure (no side effects).</summary>
    bool Evaluate(PredicateContext ctx);
}

/// <summary>
/// Bag of state passed to predicates and threaded between sibling effects in a
/// <c>SequenceEffect</c>. Carries the game, the caster, the current targets,
/// the cast-time snapshot, and <see cref="LastResult"/> — populated by the most
/// recently resolved effect so downstream predicates can ask "was that lethal?".
/// </summary>
public sealed class PredicateContext
{
    /// <summary>Current game state.</summary>
    public GameState Game;

    /// <summary>The casting Entity.</summary>
    public Entity Caster;

    /// <summary>Current target set. Replaced by <c>RetargetEffect</c> when chaining into a new targeter.</summary>
    public TargetSet Targets;

    /// <summary>Previous target set in a sequence cast.</summary>
    public TargetSet LastRetargetedTargets = null;

    /// <summary>Cast-time snapshot of any values the effect captured at cast (target position, attunement counts, etc.).</summary>
    public EffectSnapshot Snapshot;

    /// <summary>Result reported by the effect that just resolved in this sequence. Read by predicates like <c>was_lethal</c>. Null at the start of a sequence.</summary>
    public EffectResult LastResult;
}

/// <summary>Data an effect reports back to its parent sequence for downstream predicates and triggers. The combat stack itself does not consume this — only sibling effects.</summary>
public sealed class EffectResult
{
    /// <summary>Total damage this effect dealt (summed across all targets).</summary>
    public int DamageDealt;

    /// <summary>True when this effect's damage killed at least one target.</summary>
    public bool WasLethal;

    /// <summary>Number of targets the effect actually hit (after filters).</summary>
    public int TargetsHit;

    /// <summary>Anything new the effect put on the board — corpses, summoned units, glyph tiles, etc. Walked by triggers and follow-up effects.</summary>
    public List<object> SpawnedThings = new();
}
