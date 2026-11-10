using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ElementalistWeaverEffects.cs
//
// Purpose:        Elementalist new cards, slice 15a
//                 (class_identity_elementalist_v1 §3A, §3C, §4):
//                 • gain_attunement: direct attunement in one element
//                   (Threefold Argument's modes, Call the Front)
//                 • imbue_highest: imbue with your highest-attunement element
//                 • adjust_height: raise or lower a tile without imbuing it;
//                   a drop of 2 or more is a fall (3 per level, ruled)
//                 • imbue_reacting: Thunderhead (reacting tiles hurt their occupant)
//                 • draw_per_reaction: Catalyst Spark's bottom
//                 • alchemy_of_ruin / clear_reactions: Alchemy of Ruin
//                 • long_argument / imbue_opposed_near_enemies: the legendary
//                 • predicate target_frozen_or_brittle: Shatterpoint
// Layer:          Effects
// Collaborators:  ElementReactions (Imbue, FormedCount, OnFormed, Dispel),
//                 ElementalAttunement (GainCharge), ElementPick
// See:            docs/class_identity_elementalist_v1.md §2, §3, §4
// ============================================================

/// <summary>Element tag helpers shared by the slice 15 effects.</summary>
internal static class ElementTags
{
    public static readonly ElementTag[] All = { ElementTag.Fire, ElementTag.Ice, ElementTag.Storm, ElementTag.Earth };

    public static bool TryFromTile(TileElementType t, out ElementTag tag)
    {
        switch (t)
        {
            case TileElementType.Fire: tag = ElementTag.Fire; return true;
            case TileElementType.Frost: tag = ElementTag.Ice; return true;
            case TileElementType.Lightning: tag = ElementTag.Storm; return true;
            case TileElementType.Earth: tag = ElementTag.Earth; return true;
            default: tag = ElementTag.Fire; return false;
        }
    }

    /// <summary>Grants attunement outside the cast pipeline (no opposition, no burst).</summary>
    public static void Gain(Unit u, ElementTag tag, int amount)
    {
        if (amount <= 0 || CombatSim.Active || u?.Attunement is not ElementalAttunement a)
            return;
        a.GainCharge(tag, amount);
    }

    /// <summary>The tiles in a target set, once each: tiles, tile views, units' tiles, and
    /// the caster's own tile for a self cast.</summary>
    public static List<TileData> Tiles(GameState s, Entity caster, TargetSet targets, Func<GameState, Entity, Unit> findCaster)
    {
        var list = new List<TileData>();
        if (targets?.Items == null || s?.Grid == null)
            return list;
        foreach (var o in targets.Items)
        {
            TileData t = o switch
            {
                TileData td => td,
                HexTile hv => s.Grid.GetTile(hv.Axial),
                Unit u => u.CurrentTile,
                Entity e => findCaster(s, e)?.CurrentTile,
                _ => null,
            };
            if (t != null && !list.Contains(t))
                list.Add(t);
        }
        return list;
    }
}

/// <summary>Gain attunement directly. JSON: { "type": "gain_attunement", "element":
/// "fire"|"ice"|"storm"|"earth"|"highest"|"all", "amount": n }</summary>
public sealed class GainAttunementEffect : EffectBase
{
    public string Element = "highest";
    public int Amount = 1;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var u = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (u?.Attunement is not ElementalAttunement a)
            return;
        string e = (Element ?? "highest").ToLowerInvariant();
        if (e == "all")
        {
            foreach (var t in ElementTags.All)
                ElementTags.Gain(u, t, Amount);
        }
        else if (e == "highest")
            ElementTags.Gain(u, a.HighestElement(), Amount);
        else if (ElementalAttunement.TryParseTag(e, out var tag))
            ElementTags.Gain(u, tag, Amount);
    }
}

/// <summary>Imbue each target tile with the caster's highest-attunement element.
/// JSON: { "type": "imbue_highest" }</summary>
public sealed class ImbueHighestEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var u = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        var element = ElementPick.Highest(u);
        foreach (var t in ElementTags.Tiles(s, caster, targets, FindCasterUnit))
            ElementPick.Imbue(t, element, u);
    }
}

/// <summary>Raise (positive) or lower (negative) each target tile without imbuing it.
/// A unit on a tile lowered by 2 or more falls: 3 damage per level (ruled §3C).
/// JSON: { "type": "adjust_height", "amount": n, "blocks_los": bool, "max": n }</summary>
public sealed class AdjustHeightEffect : EffectBase
{
    public const int MinHeight = -3, MaxHeightCap = 6, FallPerLevel = 3;
    public int Amount;
    public bool BlocksLos;
    public int Max = MaxHeightCap;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (Amount == 0 || s?.Grid == null)
            return;
        var src = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
        foreach (var tile in ElementTags.Tiles(s, caster, targets, FindCasterUnit))
        {
            int before = tile.Height;
            int after = Math.Clamp(before + Amount, MinHeight, Math.Min(Max, MaxHeightCap));
            int delta = after - before;
            if (delta == 0)
                continue;
            var occ = tile.Occupant;

            if (CombatSim.Active)
            {
                // Preview: the fall lands in the HP ledger (ApplyDamage is sim-gated).
                if (delta <= -2 && occ != null && occ.Stats.IsAlive)
                    occ.ApplyDamage(FallPerLevel * -delta, src);
                continue;
            }

            tile.Height = after;
            if (delta > 0 && BlocksLos)
                tile.BlocksLineOfSight = true;
            tile.TileView?.SetHeight(tile.Height);
            s.Grid.RebuildTileAndNeighbors(tile.Axial);
            if (occ != null && GodotObject.IsInstanceValid(occ) && tile.TileView != null)
                occ.GlobalPosition = tile.TileView.GlobalPosition;

            s.Log($"[Height] {tile.Axial} {(delta > 0 ? "rises" : "sinks")} {Math.Abs(delta)} (now {tile.Height}).");
            if (delta <= -2 && occ != null && occ.Stats.IsAlive)
            {
                int dmg = FallPerLevel * -delta;
                occ.ApplyDamage(dmg, src);
                s.Log($"[Height] {occ.Name} falls {-delta} levels: {dmg} damage.");
            }
        }
    }
}

/// <summary>Thunderhead: imbue the target tile (and every tile within <see cref="Radius"/>)
/// with an element; each tile that reacts deals <see cref="ReactionDamage"/> to its occupant.
/// JSON: { "type": "imbue_reacting", "element": "storm", "radius": n, "reaction_damage": n }</summary>
public sealed class ImbueReactingEffect : EffectBase
{
    public string Element = "storm";
    public int Radius = 1, ReactionDamage = 3;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null || !ElementalAttunement.TryParseTag(Element, out var tag))
            return;
        var element = ElementPick.ToTile(tag);
        var src = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
        var area = new List<TileData>();
        foreach (var c in ElementTags.Tiles(s, caster, targets, FindCasterUnit))
            foreach (var t in s.Grid.Tiles.Values)
                if (t != null && s.Grid.Distance(c.Axial, t.Axial) <= Radius && !area.Contains(t))
                    area.Add(t);

        int reacted = 0;
        foreach (var t in area)
        {
            var occ = t.Occupant;
            int before = ElementReactions.FormedCount;
            ElementPick.Imbue(t, element, src);
            if (ElementReactions.FormedCount == before)
                continue;
            reacted++;
            if (ReactionDamage > 0 && occ != null && GodotObject.IsInstanceValid(occ) && occ.Stats.IsAlive)
                occ.ApplyDamage(ReactionDamage, src);
        }
        if (!CombatSim.Active)
            s.Log($"[Thunderhead] {area.Count} tile(s) imbued; {reacted} reacted.");
    }
}

/// <summary>Draw 1 card per reaction on the board, up to <see cref="Max"/>.
/// JSON: { "type": "draw_per_reaction", "max": n, "per": n }</summary>
public sealed class DrawPerReactionEffect : EffectBase
{
    public int Max = 3, Per = 1;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null || CombatSim.Active)
            return;
        int reactions = s.Grid.Tiles.Values.Count(t => t != null && t.Reaction != ElementReaction.None);
        int n = Math.Min(Max, reactions * Per);
        s.Log($"[Catalyst] {reactions} reaction(s) on the board: draw {n}.");
        if (n > 0)
            new DrawCardsEffect(n).Resolve(s, caster, targets, snap);
    }
}

/// <summary>Alchemy of Ruin: every tile within <see cref="Radius"/> of the caster holding a
/// reactive element is imbued with the caster's highest-attunement element (it reacts
/// unless it already holds that element). JSON: { "type": "alchemy_of_ruin", "radius": n, "damage": n }</summary>
public sealed class AlchemyOfRuinEffect : EffectBase
{
    public int Radius = 3, Damage;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var u = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (u?.CurrentTile == null || s?.Grid == null)
            return;
        var element = ElementPick.Highest(u);
        var tiles = s.Grid.Tiles.Values
            .Where(t => t != null && t.Reaction == ElementReaction.None && ElementReactions.IsReactive(t.ElementType)
                        && s.Grid.Distance(u.CurrentTile.Axial, t.Axial) <= Radius)
            .OrderBy(t => s.Grid.Distance(u.CurrentTile.Axial, t.Axial)).ThenBy(t => t.Axial.X).ThenBy(t => t.Axial.Y)
            .ToList();
        int reacted = 0;
        foreach (var t in tiles)
        {
            var occ = t.Occupant;
            int before = ElementReactions.FormedCount;
            ElementPick.Imbue(t, element, u);
            if (ElementReactions.FormedCount == before)
                continue;
            reacted++;
            if (Damage > 0 && occ != null && GodotObject.IsInstanceValid(occ) && occ.Stats.IsAlive && occ.TeamId != u.TeamId)
                occ.ApplyDamage(Damage, u);
        }
        if (!CombatSim.Active)
            s.Log($"[AlchemyOfRuin] {tiles.Count} imbued tile(s) meet {element}: {reacted} reaction(s).");
    }
}

/// <summary>Clear every reaction within <see cref="Radius"/> of the caster; gain
/// <see cref="AttunementPer"/> attunement in each element of each reaction cleared.
/// JSON: { "type": "clear_reactions", "radius": n, "attunement_per": n }</summary>
public sealed class ClearReactionsEffect : EffectBase
{
    public int Radius = 3, AttunementPer = 1;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var u = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (u?.CurrentTile == null || s?.Grid == null || CombatSim.Active)
            return;
        var tiles = s.Grid.Tiles.Values
            .Where(t => t != null && t.Reaction != ElementReaction.None
                        && s.Grid.Distance(u.CurrentTile.Axial, t.Axial) <= Radius)
            .ToList();
        foreach (var t in tiles)
        {
            var (a, b) = ElementReactions.ElementsOf(t.Reaction);
            ElementReactions.Dispel(t, s.Grid);
            if (ElementTags.TryFromTile(a, out var ta)) ElementTags.Gain(u, ta, AttunementPer);
            if (ElementTags.TryFromTile(b, out var tb)) ElementTags.Gain(u, tb, AttunementPer);
        }
        s.Log($"[Dispel] {tiles.Count} reaction(s) cleared.");
    }
}

/// <summary>The Long Argument: for the rest of the fight, a reaction forming within
/// <see cref="Radius"/> of the holder gives 1 attunement in each of its elements and deals
/// <see cref="Damage"/> to the enemy nearest the reaction.
/// JSON: { "type": "long_argument", "radius": n, "damage": n, "attunement_per": n }</summary>
public sealed class LongArgumentEffect : EffectBase
{
    public int Radius = 4, Damage = 3, AttunementPer = 1;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var u = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (u == null || CombatSim.Active)
            return;
        LongArgument.Add(s, u, Radius, Damage, AttunementPer);
        s.Log($"[LongArgument] For the rest of the fight, reactions within {Radius} of {u.Name} feed the argument.");
    }
}

/// <summary>The Long Argument's holders and its listener on <see cref="ElementReactions.OnFormed"/>.</summary>
public static class LongArgument
{
    private sealed class Holder { public GameState State; public Unit Unit; public int Radius, Damage, AttunementPer; }
    private static readonly List<Holder> _holders = new();
    private static bool _busy;

    public static void Reset() => _holders.Clear();

    public static void Add(GameState s, Unit u, int radius, int damage, int attunementPer)
    {
        var h = _holders.FirstOrDefault(x => x.Unit == u);
        if (h == null)
            _holders.Add(new Holder { State = s, Unit = u, Radius = radius, Damage = damage, AttunementPer = attunementPer });
        else
        {
            h.State = s;
            h.Radius = Math.Max(h.Radius, radius);
            h.Damage = Math.Max(h.Damage, damage);
            h.AttunementPer = Math.Max(h.AttunementPer, attunementPer);
        }
        ElementReactions.OnFormed -= Heard;
        ElementReactions.OnFormed += Heard;
    }

    private static void Heard(TileData tile, ElementReaction reaction, Unit source)
    {
        if (_busy || tile == null || CombatSim.Active)
            return;
        _busy = true;
        try
        {
            foreach (var h in _holders.ToList())
            {
                var u = h.Unit;
                if (u == null || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                    continue;
                var grid = h.State?.Grid;
                if (grid == null || grid.Distance(u.CurrentTile.Axial, tile.Axial) > h.Radius)
                    continue;
                var (a, b) = ElementReactions.ElementsOf(reaction);
                if (ElementTags.TryFromTile(a, out var ta)) ElementTags.Gain(u, ta, h.AttunementPer);
                if (ElementTags.TryFromTile(b, out var tb)) ElementTags.Gain(u, tb, h.AttunementPer);

                var mark = h.State.UnitsInPlay?
                    .Where(e => e != null && GodotObject.IsInstanceValid(e) && e.Stats.IsAlive && e.CurrentTile != null
                                && e.TeamId != u.TeamId && !e.IsStructure)
                    .OrderBy(e => grid.Distance(tile.Axial, e.CurrentTile.Axial))
                    .FirstOrDefault();
                if (mark != null && h.Damage > 0)
                {
                    mark.ApplyDamage(h.Damage, u);
                    h.State.Log($"[LongArgument] {ElementReactions.DisplayName(reaction)} argues on: {h.Damage} damage to {mark.Name}.");
                }
            }
        }
        finally { _busy = false; }
    }
}

/// <summary>The Long Argument's bottom: every unoccupied tile adjacent to an enemy that holds
/// a reactive element is imbued with the element opposed to it (Steam or Fulgurite forms).
/// JSON: { "type": "imbue_opposed_near_enemies" }</summary>
public sealed class ImbueOpposedNearEnemiesEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var u = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (u == null || s?.Grid == null || s.UnitsInPlay == null)
            return;
        var tiles = new List<TileData>();
        foreach (var e in s.UnitsInPlay)
        {
            if (e == null || !GodotObject.IsInstanceValid(e) || !e.Stats.IsAlive || e.CurrentTile == null || e.TeamId == u.TeamId)
                continue;
            foreach (var nb in s.Grid.GetNeighbors(e.CurrentTile.Axial))
            {
                var t = s.Grid.GetTile(nb);
                if (t != null && t.Occupant == null && t.Reaction == ElementReaction.None
                    && ElementPick.Opposed(t.ElementType) != TileElementType.None && !tiles.Contains(t))
                    tiles.Add(t);
            }
        }
        foreach (var t in tiles)
            ElementPick.Imbue(t, ElementPick.Opposed(t.ElementType), u);
        if (!CombatSim.Active)
            s.Log($"[Rebuttal] {tiles.Count} tile(s) beside enemies turned to their opposites.");
    }
}

/// <summary>True when the first target is Frozen or stands on Brittle ground.
/// JSON predicate: { "type": "target_frozen_or_brittle" }</summary>
public sealed class TargetFrozenOrBrittlePredicate : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
    {
        var items = ctx?.Targets?.Items;
        if (items == null)
            return false;
        foreach (var o in items)
        {
            var u = o switch
            {
                Unit uu => uu,
                TileData td => td.Occupant,
                _ => null,
            };
            if (u == null)
                continue;
            return u.HasStatus("frozen") || u.CurrentTile?.Reaction == ElementReaction.Brittle;
        }
        return false;
    }
}
