using System.Collections.Generic;
using Godot;

// ============================================================
// Composite effects. These are the heart of the new system.
// With Sequence, Conditional, and ForEach, every card in your
// deck can be expressed as a tree of primitives.
// ============================================================

// Resolve a list of child effects in order. The result of the LAST
// child becomes the sequence's result (so "Deal damage, then check
// if lethal" works — the conditional sees the damage's result).
public sealed class SequenceEffect : EffectBase
{
    public IEffect[] Steps;

    public SequenceEffect(params IEffect[] steps) { Steps = steps; }

    public override IEnumerable<IEffect> Children => Steps;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Targets = targets, Snapshot = snap };
        ResolveWithResult(ctx);
    }

    public override EffectResult ResolveWithResult(PredicateContext ctx)
    {
        EffectResult last = new();
        foreach (var step in Steps)
        {
            if (step is EffectBase eb)
            {
                last = eb.ResolveWithResult(ctx);
                ctx.LastResult = last;
            }
            else
            {
                // Fallback for any IEffect that doesn't use EffectBase
                step.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
                last = new EffectResult();
            }
        }
        return last;
    }
}

// Branch on a predicate. Then-branch required, else-branch optional.
// This is how "If target adjacent to corpse, deal 6 instead" works.
public sealed class ConditionalEffect : EffectBase
{
    public IPredicate If;
    public IEffect Then;
    public IEffect Else; // may be null

    public ConditionalEffect(IPredicate pred, IEffect thenEff, IEffect elseEff = null)
    {
        If = pred; Then = thenEff; Else = elseEff;
    }

    public override IEnumerable<IEffect> Children
    {
        get
        {
            yield return Then;
            if (Else != null) yield return Else;
        }
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Targets = targets, Snapshot = snap };
        ResolveWithResult(ctx);
    }

    public override EffectResult ResolveWithResult(PredicateContext ctx)
    {
        bool branch = If.Evaluate(ctx);
        s_LogBranch(ctx, branch);

        var chosen = branch ? Then : Else;
        if (chosen == null) return new EffectResult();

        if (chosen is EffectBase eb) return eb.ResolveWithResult(ctx);
        chosen.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
        return new EffectResult();
    }

    private static void s_LogBranch(PredicateContext ctx, bool taken)
    {
        ctx.Game?.Log($"[Conditional] predicate={taken} -> {(taken ? "THEN" : "ELSE")}");
    }
}

// Apply an effect once per target in the current target set.
// Useful for "Deal 4 damage to each enemy within 2 tiles".
public sealed class ForEachTargetEffect : EffectBase
{
    public IEffect PerTarget;

    public ForEachTargetEffect(IEffect per) { PerTarget = per; }

    public override IEnumerable<IEffect> Children { get { yield return PerTarget; } }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Snapshot = snap };
        foreach (var item in targets.Items)
        {
            // Wrap single target so nested effects see exactly one target.
            var single = new TargetSet();
            single.Items.Add(item);
            ctx.Targets = single;
            ctx.Caster = caster;

            if (PerTarget is EffectBase eb) eb.ResolveWithResult(ctx);
            else PerTarget.Resolve(s, caster, single, snap);
        }
    }
}

// Do nothing. Useful as a placeholder in JSON while you're sketching.
public sealed class EmptyEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap) { }
}
