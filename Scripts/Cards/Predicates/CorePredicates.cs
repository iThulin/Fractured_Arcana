using System.Collections.Generic;

// ============================================================
// Predicate library 
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

// "Was the last effect's damage lethal?"
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

// "Is the primary target adjacent to the caster?"
public sealed class TargetAdjacentToCaster : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
    {
        if (ctx.Game?.Grid == null || ctx.Targets == null || ctx.Targets.Items.Count == 0)
            return false;

        var casterUnit = ctx.Game.ActiveCasterUnit;
        if (casterUnit?.CurrentTile == null) return false;

        var firstTarget = ctx.Targets.Items[0];
        TileData targetTile = null;

        if (firstTarget is Unit u) targetTile = u.CurrentTile;
        else if (firstTarget is TileData td) targetTile = td;

        if (targetTile == null) return false;

        return ctx.Game.Grid.Distance(casterUnit.CurrentTile.Axial, targetTile.Axial) <= 1;
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

// "Is the caster standing on a specific terrain or element type?"
// Checks both TerrainType and ElementType so it works for:
//   "stone" -> TerrainType.Stone OR ElementType.Earth
//   "fire"  -> ElementType.Fire
//   "ice"   -> TerrainType.Ice OR ElementType.Frost
public sealed class CasterOnTerrain : IPredicate
{
    public string TileType;
    public CasterOnTerrain(string tileType) { TileType = tileType; }

    public bool Evaluate(PredicateContext ctx)
    {
        // Find the caster's unit
        Unit casterUnit = null;
        if (ctx.Game != null)
        {
            if (ctx.Caster == ctx.Game.PlayerA) casterUnit = ctx.Game.PlayerUnit;
            else if (ctx.Caster == ctx.Game.PlayerB) casterUnit = ctx.Game.EnemyUnit;
            else
            {
                foreach (var u in ctx.Game.UnitsInPlay)
                    if (u != null && u.Name == ctx.Caster?.Name) { casterUnit = u; break; }
            }
        }

        if (casterUnit?.CurrentTile == null) return false;

        var tile = casterUnit.CurrentTile;
        string check = TileType.ToLowerInvariant();

        // Check TerrainType
        if (check == tile.TerrainType.ToString().ToLowerInvariant())
            return true;

        // Check ElementType (map design names to enum values)
        string elementName = tile.ElementType.ToString().ToLowerInvariant();
        if (check == elementName) return true;

        // Handle common aliases: "stone" matches Earth, "ice" matches Frost
        if (check == "stone" && tile.TerrainType == TileTerrainType.Stone) return true;
        if (check == "stone" && tile.ElementType == TileElementType.Earth) return true;
        if (check == "ice" && tile.TerrainType == TileTerrainType.Ice) return true;
        if (check == "ice" && tile.ElementType == TileElementType.Frost) return true;
        if (check == "fire" && tile.ElementType == TileElementType.Fire) return true;
        if (check == "storm" && tile.ElementType == TileElementType.Lightning) return true;
        if (check == "arcane" && tile.TerrainType == TileTerrainType.Arcane) return true;
        if (check == "arcane" && tile.ElementType == TileElementType.Arcane) return true;

        return false;
    }
}

// "Is the caster adjacent to at least one tile with any of these elements?"
public sealed class HasElementsNearCaster : IPredicate
{
    public string[] RequiredElements;
    public int Range;

    public HasElementsNearCaster(string[] elements, int range = 2)
    {
        RequiredElements = elements;
        Range = range;
    }

    public bool Evaluate(PredicateContext ctx)
    {
        if (ctx.Game?.Grid == null) return false;

        Unit casterUnit = null;
        if (ctx.Caster == ctx.Game.PlayerA) casterUnit = ctx.Game.PlayerUnit;
        else if (ctx.Caster == ctx.Game.PlayerB) casterUnit = ctx.Game.EnemyUnit;
        if (casterUnit?.CurrentTile == null) return false;

        var center = casterUnit.CurrentTile.Axial;
        var foundElements = new HashSet<TileElementType>();

        foreach (var kvp in ctx.Game.Grid.Tiles)
        {
            if (ctx.Game.Grid.Distance(center, kvp.Key) > Range) continue;
            var tile = kvp.Value;
            if (tile?.ElementType != TileElementType.None)
                foundElements.Add(tile.ElementType);
        }

        foreach (var req in RequiredElements)
        {
            TileElementType needed = req.ToLowerInvariant() switch
            {
                "fire" => TileElementType.Fire,
                "ice" => TileElementType.Frost,
                "frost" => TileElementType.Frost,
                "storm" => TileElementType.Lightning,
                "stone" => TileElementType.Earth,
                _ => TileElementType.None
            };
            if (!foundElements.Contains(needed)) return false;
        }

        return true;
    }
}