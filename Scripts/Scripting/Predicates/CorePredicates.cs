using System.Collections.Generic;

// ============================================================
// Predicate library — the "questions you ask about game state."
//
// These are deliberately simple. They take a PredicateContext,
// return bool, and don't mutate anything.
//
// STUBBED: several predicates below reference game systems that
// don't exist yet (corpses, tile types, adjacency). Each one is
// marked with a TODO showing exactly what you need to wire up.
// They return 'false' safely until then, so loading a card that
// uses them won't crash — it'll just never take the 'then' branch.
// ============================================================

// Always true. Useful default and for testing.
public sealed class AlwaysTrue : IPredicate
{
    public bool Evaluate(PredicateContext ctx) => true;
}

// Logical combinators. With these you can build any boolean
// expression from simpler predicates.
public sealed class AndPredicate : IPredicate
{
    public IPredicate[] Parts;
    public AndPredicate(params IPredicate[] parts) { Parts = parts; }
    public bool Evaluate(PredicateContext ctx)
    {
        foreach (var p in Parts) if (!p.Evaluate(ctx)) return false;
        return true;
    }
}

public sealed class OrPredicate : IPredicate
{
    public IPredicate[] Parts;
    public OrPredicate(params IPredicate[] parts) { Parts = parts; }
    public bool Evaluate(PredicateContext ctx)
    {
        foreach (var p in Parts) if (p.Evaluate(ctx)) return true;
        return false;
    }
}

public sealed class NotPredicate : IPredicate
{
    public IPredicate Inner;
    public NotPredicate(IPredicate inner) { Inner = inner; }
    public bool Evaluate(PredicateContext ctx) => !Inner.Evaluate(ctx);
}

// "Was the last effect's damage lethal?" Used by cards like
// Bone Shatter: "Deal 5 damage. If lethal leave a corpse and summon skeleton."
public sealed class LastEffectWasLethal : IPredicate
{
    public bool Evaluate(PredicateContext ctx) => ctx.LastResult?.WasLethal ?? false;
}

// "Is the first target adjacent to a tile of the given type?"
// Used by Bone Bolt, Soul Rend ("If target is standing on shadow terrain..."),
// and many others.
//
// TODO: wire GameState.Grid.GetAdjacentTiles(pos) and read tile.TileType
//       or tile.HasImbue(kind) depending on how you model corpses/shadow/fire.
public sealed class TargetAdjacentToTile : IPredicate
{
    public string TileType;
    public TargetAdjacentToTile(string tileType) { TileType = tileType; }

    public bool Evaluate(PredicateContext ctx)
    {
        if (ctx.Targets == null || ctx.Targets.Items.Count == 0) return false;
        // TODO: replace with real grid lookup
        // var firstTarget = ctx.Targets.Items[0];
        // var pos = GetPositionOf(firstTarget);
        // foreach (var t in ctx.Game.Grid.GetAdjacentTiles(pos))
        //     if (t.TileType == TileType) return true;
        return false;
    }
}

// "Is the first target standing ON a tile of the given type?"
public sealed class TargetOnTile : IPredicate
{
    public string TileType;
    public TargetOnTile(string tileType) { TileType = tileType; }

    public bool Evaluate(PredicateContext ctx)
    {
        if (ctx.Targets == null || ctx.Targets.Items.Count == 0) return false;
        // TODO: replace with real grid lookup.
        // var pos = GetPositionOf(ctx.Targets.Items[0]);
        // var tile = ctx.Game.Grid.GetTile(pos);
        // return tile != null && tile.TileType == TileType;
        return false;
    }
}

// "How many tiles of this type exist on the board, compared to N?"
// Used by Marrow Shield ("Gain armor equal to corpses on the board").
public sealed class CountOfTileAtLeast : IPredicate
{
    public string TileType;
    public int AtLeast;
    public CountOfTileAtLeast(string tileType, int atLeast)
    {
        TileType = tileType; AtLeast = atLeast;
    }

    public bool Evaluate(PredicateContext ctx)
    {
        // TODO: ctx.Game.Grid.CountTilesOfType(TileType) >= AtLeast;
        return false;
    }
}

// "Is this being cast as a Channel?" Replaces your separate
// ChannelVariant system if you want — or keep both.
public sealed class IsChanneled : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
    {
        // TODO: set a flag in PredicateContext when the channel variant is used.
        return false;
    }
}
