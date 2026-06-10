using System.Collections.Generic;

// ============================================================
// CorePredicates.cs
//
// Purpose:        Library of IPredicate implementations consumed
//                 by ConditionalEffect. Each predicate takes a
//                 PredicateContext, returns bool, and never
//                 mutates state.
// Layer:          Predicates
// Collaborators:  ScriptingInterfaces.cs (IPredicate),
//                 CompositeEffects.cs (ConditionalEffect calls
//                 these), JsonCardLoader.cs (RegisterBuiltins
//                 maps JSON type strings to these classes),
//                 GameState.cs, ElementalAttunement.cs
// See:            README §5.5 (Predicate Types)
// ============================================================
//
// NOTE: Several predicates below (TargetOnTile, TargetAdjacentToTile,
// CountOfTileAtLeast, IsChanneled) are STUBBED — they reference
// game systems that don't yet exist (per-tile TileType keys,
// corpse counting, channel-flag in PredicateContext). They return
// `false` safely so cards that use them never take the THEN
// branch, but they need to be wired up before those cards work
// as designed. Each stub has a TODO showing exactly what to wire.

/// <summary>
/// Always returns true. Useful default for the predicate slot and
/// as a sentinel during card authoring.
/// </summary>
public sealed class AlwaysTrue : IPredicate
{
    public bool Evaluate(PredicateContext ctx) => true;
}

// ── Logical combinators ─────────────────────────────────────────────────

/// <summary>
/// Logical AND across multiple predicates. Empty array is vacuously
///  true. Short-circuits on first false.
/// </summary>
public sealed class AndPredicate : IPredicate
{
    public IPredicate[] Parts;
    public AndPredicate(params IPredicate[] parts) { Parts = parts; }
    public bool Evaluate(PredicateContext ctx)
    {
        foreach (var p in Parts)
            if (!p.Evaluate(ctx))
                return false;
        return true;
    }
}

/// <summary>
/// Logical OR across multiple predicates. Empty array is vacuously
///  false. Short-circuits on first true.
/// </summary>
public sealed class OrPredicate : IPredicate
{
    public IPredicate[] Parts;
    public OrPredicate(params IPredicate[] parts) { Parts = parts; }
    public bool Evaluate(PredicateContext ctx)
    {
        foreach (var p in Parts)
            if (p.Evaluate(ctx))
                return true;
        return false;
    }
}

/// <summary>
/// Logical NOT — inverts the wrapped predicate's result.
/// </summary>
public sealed class NotPredicate : IPredicate
{
    public IPredicate Inner;
    public NotPredicate(IPredicate inner) { Inner = inner; }
    public bool Evaluate(PredicateContext ctx) => !Inner.Evaluate(ctx);
}

// ── Result-inspection predicates ────────────────────────────────────────

/// <summary>
/// True when the previous sibling effect in a SequenceEffect reported 
/// a lethal hit via its <see cref="EffectResult"/>.
/// </summary>
public sealed class LastEffectWasLethal : IPredicate
{
    public bool Evaluate(PredicateContext ctx) => ctx.LastResult?.WasLethal ?? false;
}

// ── Tile / position predicates ──────────────────────────────────────────

/// <summary>
/// Intended: true when the first target is adjacent to a tile
///  of the given type. Currently always returns false — wire up grid lookup 
/// before relying on it.
/// </summary>
public sealed class TargetAdjacentToTile : IPredicate
{
    public string TileType;
    public TargetAdjacentToTile(string tileType) { TileType = tileType; }

    public bool Evaluate(PredicateContext ctx)
    {
        if (ctx.Targets == null || ctx.Targets.Items.Count == 0)
            return false;
        // TODO: replace with real grid lookup
        // var firstTarget = ctx.Targets.Items[0];
        // var pos = GetPositionOf(firstTarget);
        // foreach (var t in ctx.Game.Grid.GetAdjacentTiles(pos))
        //     if (t.TileType == TileType) return true;
        return false;
    }
}

/// <summary>
/// Intended: true when the first target is standing on a tile
/// of the given type. Currently always returns false — wire up grid lookup 
/// before relying on it.
/// </summary>
public sealed class TargetOnTile : IPredicate
{
    public string TileType;
    public TargetOnTile(string tileType) { TileType = tileType; }

    public bool Evaluate(PredicateContext ctx)
    {
        if (ctx.Targets == null || ctx.Targets.Items.Count == 0)
            return false;
        // TODO: replace with real grid lookup.
        // var pos = GetPositionOf(ctx.Targets.Items[0]);
        // var tile = ctx.Game.Grid.GetTile(pos);
        // return tile != null && tile.TileType == TileType;
        return false;
    }
}

/// <summary>
/// True when the first target is within hex distance 1 of the
/// caster's current tile.
/// </summary>
public sealed class TargetAdjacentToCaster : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
    {
        if (ctx.Game?.Grid == null || ctx.Targets == null || ctx.Targets.Items.Count == 0)
            return false;

        var casterUnit = ctx.Game.ActiveCasterUnit;
        if (casterUnit?.CurrentTile == null)
            return false;

        var firstTarget = ctx.Targets.Items[0];
        TileData targetTile = null;

        if (firstTarget is Unit u)
            targetTile = u.CurrentTile;
        else if (firstTarget is TileData td)
            targetTile = td;

        if (targetTile == null)
            return false;

        return ctx.Game.Grid.Distance(casterUnit.CurrentTile.Axial, targetTile.Axial) <= 1;
    }
}

/// <summary> 
/// Intended: true when at least <see cref="AtLeast"/> tiles of 
/// the given type exist on the board. Targets cards like Marrow Shield 
/// ("gain armor equal to corpses"). Currently always returns false.
/// </summary>
public sealed class CountOfTileAtLeast : IPredicate
{
    public string TileType;
    public int AtLeast;
    public CountOfTileAtLeast(string tileType, int atLeast)
    {
        TileType = tileType;
        AtLeast = atLeast;
    }

    public bool Evaluate(PredicateContext ctx)
    {
        // TODO: ctx.Game.Grid.CountTilesOfType(TileType) >= AtLeast;
        return false;
    }
}

/// <summary>
/// Intended: true when the current cast is the channel variant of its parent half. 
/// Currently always returns false — wire up a channel flag in 
/// <see cref="PredicateContext"/> before relying on this. Could replace the 
/// standalone ChannelVariant system, or coexist with it.
/// </summary>
public sealed class IsChanneled : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
    {
        // TODO: set a flag in PredicateContext when the channel variant is used.
        return false;
    }
}

/// <summary>
/// True when the caster's current tile matches the named terrain 
/// or element type. Checks both <c>TerrainType</c> and <c>ElementType</c>
///  with aliases — "stone" matches Stone terrain or Earth imbuement, 
/// "ice" matches Ice terrain or Frost imbuement, "fire"/"storm"/"arcane" 
/// match the corresponding imbuements.
/// </summary>
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
            if (ctx.Caster == ctx.Game.PlayerA)
                casterUnit = ctx.Game.PlayerUnit;
            else if (ctx.Caster == ctx.Game.PlayerB)
                casterUnit = ctx.Game.EnemyUnit;
            else
            {
                foreach (var u in ctx.Game.UnitsInPlay)
                    if (u != null && u.Name == ctx.Caster?.Name)
                    { casterUnit = u; break; }
            }
        }

        if (casterUnit?.CurrentTile == null)
            return false;

        var tile = casterUnit.CurrentTile;
        string check = TileType.ToLowerInvariant();

        // Check TerrainType
        if (check == tile.TerrainType.ToString().ToLowerInvariant())
            return true;

        // Check ElementType (map design names to enum values)
        string elementName = tile.ElementType.ToString().ToLowerInvariant();
        if (check == elementName)
            return true;

        // Handle common aliases: "stone" matches Earth, "ice" matches Frost
        if (check == "stone" && tile.TerrainType == TileTerrainType.Stone)
            return true;
        if (check == "stone" && tile.ElementType == TileElementType.Earth)
            return true;
        if (check == "ice" && tile.TerrainType == TileTerrainType.Ice)
            return true;
        if (check == "ice" && tile.ElementType == TileElementType.Frost)
            return true;
        if (check == "fire" && tile.ElementType == TileElementType.Fire)
            return true;
        if (check == "storm" && tile.ElementType == TileElementType.Lightning)
            return true;
        if (check == "arcane" && tile.TerrainType == TileTerrainType.Arcane)
            return true;
        if (check == "arcane" && tile.ElementType == TileElementType.Arcane)
            return true;

        return false;
    }
}

/// <summary>
/// True when every element in <see cref="RequiredElements"/> is 
/// present on at least one tile within <see cref="Range"/> hexes of the 
/// caster. ALL must be present — partial matches return false. Element name 
/// aliases match those of CasterOnTerrain.</summary>
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
        if (ctx.Game?.Grid == null)
            return false;

        Unit casterUnit = null;
        if (ctx.Caster == ctx.Game.PlayerA)
            casterUnit = ctx.Game.PlayerUnit;
        else if (ctx.Caster == ctx.Game.PlayerB)
            casterUnit = ctx.Game.EnemyUnit;
        if (casterUnit?.CurrentTile == null)
            return false;

        var center = casterUnit.CurrentTile.Axial;
        var foundElements = new HashSet<TileElementType>();

        foreach (var kvp in ctx.Game.Grid.Tiles)
        {
            if (ctx.Game.Grid.Distance(center, kvp.Key) > Range)
                continue;
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
            if (!foundElements.Contains(needed))
                return false;
        }

        return true;
    }
}

/// <summary>
/// True when the caster's banked Charge is at least <see cref="Value"/>. Used by
/// threshold cards ("if 3+ charges this chains…").
/// </summary>
public sealed class ChargeAtLeastPredicate : IPredicate
{
    public int Value;
    public ChargeAtLeastPredicate(int value) { Value = value; }

    public bool Evaluate(PredicateContext ctx)
    {
        var unit = ctx.Game?.ActiveCasterUnit;
        return unit?.Attunement is ArcaneAttunement arc && arc.Charge >= Value;
    }
}

/// <summary>
/// True when the caster has already cast at least one spell earlier this turn
/// (i.e. this is not the turn's first spell). Reads the Grimoire's spell count;
/// because "AbilityCast" fires at push, the current card is already counted, so
/// the test is &gt;= 2.
/// </summary>
public sealed class HasCastSpellThisTurnPredicate : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
    {
        var unit = ctx.Game?.ActiveCasterUnit;
        return unit?.Attunement is ArcaneAttunement arc && arc.SpellsCastThisTurn >= 2;
    }
}

/// <summary>
/// True when the caster's Weave is at least <see cref="Value"/>.
/// </summary>
public sealed class WeaveAtLeastPredicate : IPredicate
{
    public int Value;
    public WeaveAtLeastPredicate(int value) { Value = value; }

    public bool Evaluate(PredicateContext ctx)
    {
        var unit = ctx.Game?.ActiveCasterUnit;
        return unit?.Attunement is WeaveAttunement w && w.Weave >= Value;
    }
}

/// <summary>
/// True when the caster's team has at least <see cref="Value"/> prepared glyphs on the board.
/// Used by "if 2+ prepared tiles…" cards (Glyph Bolt).
/// JSON: { "type": "glyph_count_at_least", "value": n }
/// </summary>
public sealed class GlyphCountAtLeastPredicate : IPredicate
{
    public int Value;
    public GlyphCountAtLeastPredicate(int value) { Value = value; }

    public bool Evaluate(PredicateContext ctx)
    {
        var unit = ctx.Game?.ActiveCasterUnit;
        return DamagePerGlyphEffect.CountFriendlyGlyphs(ctx.Game, unit) >= Value;
    }
}

/// <summary>
/// Returns true when the caster has cast at least <see cref="Threshold"/>
/// spells this turn. Reads <c>GameState.SpellsCastThisTurn</c> which is
/// incremented in <c>Rules.TryCast</c>. See wiring doc §2.
/// JSON predicate: { "type": "spells_cast_this_turn", "threshold": n }
/// </summary>
public sealed class SpellsCastThisTurnPredicate : IPredicate
{
    public int Threshold;
    public SpellsCastThisTurnPredicate(int threshold) { Threshold = threshold; }

    public bool Evaluate(PredicateContext ctx) =>
        ctx?.Game?.SpellsCastThisTurn >= Threshold;

    public bool IsSatisfied(GameState s, Entity caster) =>
        s?.SpellsCastThisTurn >= Threshold;
}
