using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ElementReactions.cs
//
// Purpose:        Elementalist element reactions (class identity
//                 spec v1 §2, ruled 2026-10-02). Imbuing a tile that
//                 already holds a DIFFERENT one of the four elements
//                 (Fire, Frost, Storm, Earth) triggers a reaction
//                 instead of overwriting it:
//
//                   Fire  + Frost  -> Steam      (blocks sight, 2 rounds)
//                   Storm + Earth  -> Fulgurite  (5 to the occupant,
//                                                 permanent rubble that
//                                                 stops forced movement)
//                   Fire  + Earth  -> Magma      (no walking in; forced
//                                                 entry deals 6 and stops;
//                                                 3 rounds, then Earth)
//                   Fire  + Storm  -> Wildfire   (spreads Fire one tile
//                                                 per round toward the
//                                                 caster's enemies,
//                                                 2 rounds)
//                   Frost + Storm  -> Brittle    (+3 per hit; a Frozen
//                                                 occupant shatters for
//                                                 8, 2 rounds)
//                   Frost + Earth  -> Glacier    (8 HP wall that blocks
//                                                 movement and sight,
//                                                 2 rounds, then Frost;
//                                                 an occupied tile
//                                                 freezes its occupant
//                                                 instead)
//
//                 Opposed pairs (Fire/Frost, Storm/Earth) blow up once;
//                 compatible pairs leave something behind. Every rule is
//                 deterministic.
//
//                 This class is the ONE runtime write path for tile
//                 elements during combat. TileEntryReactions.ImbueTile
//                 and every card imbue route here, so a reaction cannot
//                 be skipped by a site that writes the field directly.
//                 Map GENERATION still writes ElementType directly
//                 (nothing reacts while a board is being built).
//
//                 Preview: inside CombatSim nothing is mutated. Reactions
//                 are recorded to a sim list (read by the cast preview)
//                 and a sim overlay tracks each tile's would-be element,
//                 so a card that imbues the same tile twice predicts the
//                 second write against the first.
// Layer:          Combat core
// Collaborators:  TileData (Reaction fields), TileEntryReactions
//                 (ImbueTile delegates here; Magma/Fulgurite entry),
//                 HexGridManager (obstacles for Glacier, StepAllowed for
//                 Magma, FirstLosBlocker for Steam), Unit.ApplyDamage
//                 (Brittle), CombatManager (round tick, reset, preview),
//                 HexTile.SetReaction, TooltipManager
// See:            docs/class_identity_elementalist_v1.md §2, §2a
// ============================================================

/// <summary>The six element reactions, plus None. Stored on <see cref="TileData.Reaction"/>.</summary>
public enum ElementReaction
{
    None,
    Steam,
    Fulgurite,
    Magma,
    Wildfire,
    Brittle,
    Glacier
}

/// <summary>Element reaction rules, the combat-time imbue write path, and the round tick.</summary>
public static class ElementReactions
{
    // ── Tuning (starting values; tune by playtest) ──────────────────────
    public const int SteamRounds = 2;
    public const int MagmaRounds = 3;
    public const int WildfireRounds = 2;
    public const int BrittleRounds = 2;
    public const int GlacierRounds = 2;

    public const int FulguriteDamage = 5;
    public const int MagmaForcedEntryDamage = 6;
    public const int BrittleBonusDamage = 3;
    public const int ShatterBonusDamage = 8;

    /// <summary>Obstacle catalog kind used for the Glacier wall (8 HP, high, ice).</summary>
    public const string GlacierObstacleKind = "glacier";

    /// <summary>Log sink for this combat. CombatManager points it at GameState.Log
    /// on combat start; null falls back to GD.Print.</summary>
    public static Action<string> Log;

    // Preview bookkeeping (CombatSim only).
    private static readonly Dictionary<TileData, TileElementType> _simElement = new();
    private static readonly List<(TileData tile, ElementReaction reaction)> _simReactions = new();

    /// <summary>Clears all per-combat and preview state. Called at combat start.</summary>
    public static void Reset()
    {
        _simElement.Clear();
        _simReactions.Clear();
    }

    /// <summary>Clears the preview overlay. Called by CombatSim.Begin.</summary>
    public static void ResetSim()
    {
        _simElement.Clear();
        _simReactions.Clear();
    }

    /// <summary>The reactions the last preview run predicted, in order.</summary>
    public static List<(TileData tile, ElementReaction reaction)> SnapshotSimReactions()
        => new List<(TileData, ElementReaction)>(_simReactions);

    // ── Rules ───────────────────────────────────────────────────────────

    /// <summary>True for the four Elementalist elements that react.</summary>
    public static bool IsReactive(TileElementType e)
        => e == TileElementType.Fire || e == TileElementType.Frost
        || e == TileElementType.Lightning || e == TileElementType.Earth;

    /// <summary>The reaction two different reactive elements produce. Order does not matter.</summary>
    public static ElementReaction Resolve(TileElementType a, TileElementType b)
    {
        if (a == b || !IsReactive(a) || !IsReactive(b))
            return ElementReaction.None;

        bool Has(TileElementType x) => a == x || b == x;

        if (Has(TileElementType.Fire) && Has(TileElementType.Frost)) return ElementReaction.Steam;
        if (Has(TileElementType.Lightning) && Has(TileElementType.Earth)) return ElementReaction.Fulgurite;
        if (Has(TileElementType.Fire) && Has(TileElementType.Earth)) return ElementReaction.Magma;
        if (Has(TileElementType.Fire) && Has(TileElementType.Lightning)) return ElementReaction.Wildfire;
        if (Has(TileElementType.Frost) && Has(TileElementType.Lightning)) return ElementReaction.Brittle;
        if (Has(TileElementType.Frost) && Has(TileElementType.Earth)) return ElementReaction.Glacier;
        return ElementReaction.None;
    }

    /// <summary>Player-facing name, plain letters only (the Label3D font has no symbol glyphs).</summary>
    public static string DisplayName(ElementReaction r) => r switch
    {
        ElementReaction.Steam => "Steam",
        ElementReaction.Fulgurite => "Fulgurite",
        ElementReaction.Magma => "Magma",
        ElementReaction.Wildfire => "Wildfire",
        ElementReaction.Brittle => "Brittle",
        ElementReaction.Glacier => "Glacier",
        _ => ""
    };

    /// <summary>One-line rules text for tooltips, the log and the reference chart.</summary>
    public static string Describe(ElementReaction r) => r switch
    {
        ElementReaction.Steam => "Fire met Frost. Blocks line of sight through this tile.",
        ElementReaction.Fulgurite => $"Storm met Earth. Fused rubble: forced movement stops here.",
        ElementReaction.Magma => $"Fire met Earth. Cannot be walked into; forced entry deals {MagmaForcedEntryDamage} and stops. Cools to Earth.",
        ElementReaction.Wildfire => "Fire met Storm. Spreads Fire one tile each round toward the caster's enemies.",
        ElementReaction.Brittle => $"Frost met Storm. Whoever stands here takes +{BrittleBonusDamage} from each hit; a Frozen unit shatters for +{ShatterBonusDamage}.",
        ElementReaction.Glacier => "Frost met Earth. An ice wall that blocks movement and sight. Melts to Frost.",
        _ => ""
    };

    /// <summary>The element a tile carries after the reaction forms (for the preview overlay).</summary>
    private static TileElementType ResultElement(ElementReaction r)
        => r == ElementReaction.Wildfire ? TileElementType.Fire : TileElementType.None;

    // ── The write path ──────────────────────────────────────────────────

    /// <summary>
    /// Imbues <paramref name="element"/> onto <paramref name="tile"/> at runtime,
    /// reacting with whatever reactive element is already there. Pass the unit that
    /// caused the imbue when known: Wildfire uses its team to pick a direction.
    /// Inside CombatSim this records a prediction and mutates nothing.
    /// No-op for <see cref="TileElementType.None"/>.
    /// </summary>
    public static void Imbue(TileData tile, TileElementType element, float strength = 1f, Unit source = null)
    {
        if (tile == null || element == TileElementType.None)
            return;

        if (CombatSim.Active)
        {
            SimImbue(tile, element);
            return;
        }

        // The wall takes no imbue; Frost quenches Magma, nothing else touches it.
        if (tile.Reaction == ElementReaction.Glacier)
            return;
        if (tile.Reaction == ElementReaction.Magma)
        {
            if (element == TileElementType.Frost)
                Quench(tile);
            return;
        }

        var existing = tile.ElementType;
        var reaction = Resolve(existing, element);
        if (reaction != ElementReaction.None)
        {
            Trigger(tile, existing, element, reaction, source);
            return;
        }

        WriteRaw(tile, element, strength);
    }

    /// <summary>Plain element write with no reaction (data, hazard flag, visual).
    /// Also used for the leftovers of a reaction (melted Glacier, cooled Magma).</summary>
    public static void WriteRaw(TileData tile, TileElementType element, float strength = 1f)
    {
        if (tile == null)
            return;

        tile.ElementType = element;
        tile.ElementStrength = element == TileElementType.None ? 0f : strength;
        if (element == TileElementType.Fire)
            tile.IsHazardous = true;
        else if (tile.Reaction != ElementReaction.Magma)
            tile.IsHazardous = IsTerrainHazard(tile);
        tile.TileView?.SetElement(element);
    }

    private static bool IsTerrainHazard(TileData tile)
        => tile.TerrainType == TileTerrainType.Lava || tile.TerrainModifier == "scorched";

    private static void SimImbue(TileData tile, TileElementType element)
    {
        if (tile.Reaction == ElementReaction.Glacier)
            return;
        if (tile.Reaction == ElementReaction.Magma)
        {
            if (element == TileElementType.Frost)
                _simElement[tile] = TileElementType.Earth;
            return;
        }

        var existing = _simElement.TryGetValue(tile, out var e) ? e : tile.ElementType;
        var reaction = Resolve(existing, element);
        if (reaction == ElementReaction.None)
        {
            _simElement[tile] = element;
            return;
        }

        _simReactions.Add((tile, reaction));
        _simElement[tile] = ResultElement(reaction);

        // Immediate damage lands in the preview ledger (ApplyDamage is sim-gated).
        if (reaction == ElementReaction.Fulgurite && tile.Occupant != null && tile.Occupant.Stats.IsAlive)
            tile.Occupant.ApplyDamage(FulguriteDamage);
    }

    private static void Trigger(TileData tile, TileElementType existing, TileElementType incoming,
                                ElementReaction reaction, Unit source)
    {
        var occupant = tile.Occupant;
        int rounds = 0;

        switch (reaction)
        {
            case ElementReaction.Steam:
                WriteRaw(tile, TileElementType.None);
                rounds = SteamRounds;
                break;

            case ElementReaction.Fulgurite:
                WriteRaw(tile, TileElementType.None);
                tile.ApplyTerrainModifier("rubble");
                tile.TileView?.SetTerrainScar("rubble");
                rounds = -1;   // permanent
                break;

            case ElementReaction.Magma:
                WriteRaw(tile, TileElementType.None);
                tile.IsHazardous = true;
                tile.TileView?.SetElement(TileElementType.Fire);   // reads as molten
                rounds = MagmaRounds;
                break;

            case ElementReaction.Wildfire:
                WriteRaw(tile, TileElementType.Fire);
                rounds = WildfireRounds;
                break;

            case ElementReaction.Brittle:
                WriteRaw(tile, TileElementType.None);
                rounds = BrittleRounds;
                break;

            case ElementReaction.Glacier:
                var grid = HexGridManager.Current;
                if (occupant != null || grid == null || !tile.IsWalkable || tile.IsBlocked)
                {
                    // No room for a wall: the occupant freezes instead and the tile keeps Frost.
                    WriteRaw(tile, TileElementType.Frost);
                    if (occupant != null && occupant.Stats.IsAlive)
                        occupant.ApplyStatus("frozen", 1);
                    Say($"[Reaction] {DisplayName(existing, incoming)} at {tile.Axial}: no room for a Glacier, "
                        + (occupant != null ? $"{occupant.Name} is Frozen." : "the tile keeps Frost."));
                    return;
                }
                WriteRaw(tile, TileElementType.None);
                grid.ApplyObstacle(tile, GlacierObstacleKind);
                grid.RefreshObstacleVisuals();
                rounds = GlacierRounds;
                break;
        }

        tile.Reaction = reaction;
        tile.ReactionRounds = rounds;
        tile.ReactionTeam = source?.TeamId ?? -1;
        tile.TileView?.SetReaction(reaction, rounds);

        Say($"[Reaction] {DisplayName(existing, incoming)} at {tile.Axial}: {DisplayName(reaction)}. {Describe(reaction)}");

        if (reaction == ElementReaction.Fulgurite && occupant != null && occupant.Stats.IsAlive)
            occupant.ApplyDamage(FulguriteDamage, source);
    }

    private static string DisplayName(TileElementType existing, TileElementType incoming)
        => $"{ElementName(incoming)} meets {ElementName(existing)}";

    private static string ElementName(TileElementType e) => e == TileElementType.Lightning ? "Storm" : e.ToString();

    /// <summary>Frost poured on Magma cools it to Earth early.</summary>
    private static void Quench(TileData tile)
    {
        ClearReaction(tile);
        WriteRaw(tile, TileElementType.Earth);
        Say($"[Reaction] Frost quenches the Magma at {tile.Axial}; it cools to Earth.");
    }

    /// <summary>Removes the reaction marker (data and visual) without touching the element.</summary>
    public static void ClearReaction(TileData tile)
    {
        if (tile == null)
            return;
        tile.Reaction = ElementReaction.None;
        tile.ReactionRounds = 0;
        tile.ReactionTeam = -1;
        tile.TileView?.SetReaction(ElementReaction.None, 0);
    }

    // ── Hooks read by other systems ─────────────────────────────────────

    /// <summary>Forced movement may enter Magma (walking may not; see
    /// HexGridManager.StepAllowed). Same checks as TileData.CanEnter otherwise.</summary>
    public static bool ForcedMayEnter(TileData tile)
        => tile != null && tile.Reaction == ElementReaction.Magma
        && tile.IsWalkable && !tile.IsBlocked && !tile.IsOccupied;

    /// <summary>
    /// Brittle ground: returns the hit's amount after the Brittle bonus. A Frozen
    /// unit on Brittle shatters (bigger bonus, thaws, the Brittle is spent). Called
    /// from Unit.ApplyDamage before mitigation and before the sim gate, so the
    /// preview prices it. Mutates nothing inside CombatSim.
    /// </summary>
    public static int ApplyBrittle(Unit victim, int amount)
    {
        var tile = victim?.CurrentTile;
        if (tile == null || tile.Reaction != ElementReaction.Brittle || amount <= 0)
            return amount;

        if (victim.HasStatus("frozen"))
        {
            if (!CombatSim.Active)
            {
                victim.RemoveStatus("frozen");
                ClearReaction(tile);
                Say($"[Reaction] {victim.Name} shatters on Brittle ground (+{ShatterBonusDamage}).");
            }
            return amount + ShatterBonusDamage;
        }

        return amount + BrittleBonusDamage;
    }

    /// <summary>A Glacier wall broken by damage melts to Frost instead of leaving
    /// rubble. Called by HexGridManager.DamageObstacle after the obstacle clears.</summary>
    public static void OnGlacierBroken(TileData tile)
    {
        if (tile == null)
            return;
        ClearReaction(tile);
        WriteRaw(tile, TileElementType.Frost);
    }

    // ── Round tick ──────────────────────────────────────────────────────

    /// <summary>
    /// End of round (after the enemy phase): Wildfire spreads, then every timed
    /// reaction counts down and expires. Spread targets are chosen before any write
    /// so a tile set alight this round cannot spread again until next round, and a
    /// newly lit tile is plain Fire (Wildfire does not breed Wildfire).
    /// </summary>
    public static void TickEndOfRound(GameState s, HexGridManager grid)
    {
        if (grid == null)
            return;

        var active = new List<TileData>();
        foreach (var tile in grid.Tiles.Values)
            if (tile != null && tile.Reaction != ElementReaction.None)
                active.Add(tile);
        if (active.Count == 0)
            return;

        // 1. Wildfire picks its targets.
        var spreads = new List<(TileData target, int team)>();
        var claimed = new HashSet<TileData>();
        foreach (var tile in active)
        {
            if (tile.Reaction != ElementReaction.Wildfire)
                continue;
            var target = PickWildfireTarget(s, grid, tile, claimed);
            if (target != null)
            {
                claimed.Add(target);
                spreads.Add((target, tile.ReactionTeam));
            }
        }

        // 2. Countdown and expiry.
        foreach (var tile in active)
        {
            if (tile.ReactionRounds < 0)
                continue;   // permanent (Fulgurite)
            tile.ReactionRounds--;
            if (tile.ReactionRounds > 0)
            {
                tile.TileView?.SetReaction(tile.Reaction, tile.ReactionRounds);
                continue;
            }
            Expire(tile, grid);
        }

        // 3. Wildfire writes (these may react in turn: Fire onto Frost makes Steam).
        foreach (var (target, team) in spreads)
        {
            Unit teamSource = null;
            if (team >= 0 && s?.UnitsInPlay != null)
                foreach (var u in s.UnitsInPlay)
                    if (u != null && u.TeamId == team) { teamSource = u; break; }
            Imbue(target, TileElementType.Fire, 1f, teamSource);
            Say($"[Reaction] Wildfire spreads to {target.Axial}.");
        }
    }

    private static void Expire(TileData tile, HexGridManager grid)
    {
        var reaction = tile.Reaction;
        ClearReaction(tile);

        switch (reaction)
        {
            case ElementReaction.Magma:
                tile.IsHazardous = IsTerrainHazard(tile);
                WriteRaw(tile, TileElementType.Earth);
                Say($"[Reaction] The Magma at {tile.Axial} cools to Earth.");
                break;

            case ElementReaction.Glacier:
                if (tile.ObstacleKind == GlacierObstacleKind)
                {
                    grid.ClearObstacle(tile);
                    grid.RefreshObstacleVisuals();
                }
                WriteRaw(tile, TileElementType.Frost);
                Say($"[Reaction] The Glacier at {tile.Axial} melts to Frost.");
                break;

            case ElementReaction.Wildfire:
                Say($"[Reaction] The Wildfire at {tile.Axial} settles into ordinary Fire.");
                break;

            default:
                Say($"[Reaction] The {DisplayName(reaction)} at {tile.Axial} fades.");
                break;
        }
    }

    /// <summary>
    /// The adjacent tile Wildfire spreads to: open, walkable, not already Fire, not a
    /// wall or Magma, not claimed this round. Nearest to a unit hostile to the team
    /// that lit it (any unit when the team is unknown); ties break by neighbour order.
    /// </summary>
    private static TileData PickWildfireTarget(GameState s, HexGridManager grid, TileData from, HashSet<TileData> claimed)
    {
        TileData best = null;
        int bestDistance = int.MaxValue;

        foreach (var coord in grid.GetNeighbors(from.Axial))
        {
            var n = grid.GetTile(coord);
            if (n == null || claimed.Contains(n))
                continue;
            if (!n.IsWalkable || n.IsBlocked)
                continue;
            if (n.ElementType == TileElementType.Fire)
                continue;
            if (n.Reaction == ElementReaction.Glacier || n.Reaction == ElementReaction.Magma)
                continue;

            int d = NearestUnitDistance(s, grid, n, from.ReactionTeam);
            if (best == null || d < bestDistance)
            {
                best = n;
                bestDistance = d;
            }
        }
        return best;
    }

    private static int NearestUnitDistance(GameState s, HexGridManager grid, TileData tile, int sourceTeam)
    {
        int best = int.MaxValue;
        if (s?.UnitsInPlay == null)
            return best;
        foreach (var u in s.UnitsInPlay)
        {
            if (u == null || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (sourceTeam >= 0 && u.TeamId == sourceTeam)
                continue;
            int d = grid.Distance(tile.Axial, u.CurrentTile.Axial);
            if (d < best)
                best = d;
        }
        return best;
    }

    private static void Say(string line)
    {
        if (Log != null)
            Log(line);
        else
            GD.Print(line);
    }
}
