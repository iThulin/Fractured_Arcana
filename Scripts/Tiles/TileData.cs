using Godot;
using System;
using System.Runtime.CompilerServices;

// ============================================================
// TileData.cs
//
// Purpose:        Pure-data model for a single hex tile — terrain
//                 type, imbued element, occupancy, height, glyph,
//                 and pathfinding cost. Paired 1:1 with HexTile,
//                 the visual Node3D representation.
// Layer:          Data
// Collaborators:  HexTile.cs (TileView back-pointer + visual),
//                 HexGridManager.cs (owns the TileData grid),
//                 Unit.cs (Occupant), GlyphData.cs (Glyph)
// See:            README §3 (Architecture — hex grid is the spatial
//                 substrate for combat)
// ============================================================

/// <summary>Terrain types a tile can have. Affects movement cost, line-of-sight, and which terrain-tagged predicates apply.</summary>
public enum TileTerrainType
{
    Grass,
    Water,
    Lava,
    Forest,
    Stone,
    Arcane,
    Ice
}

/// <summary>Elemental imbuement currently applied to a tile. <see cref="None"/> means the tile has no active imbuement.</summary>
public enum TileElementType
{
    None,
    Fire,
    Water,
    Earth,
    Air,
    Arcane,
    Frost,
    Shadow,
    Lightning
}

/// <summary>
/// Pure-data representation of one tile on the combat hex grid. Holds terrain, occupancy,
/// imbuement, glyph, height, and pathfinding metadata. The companion <see cref="HexTile"/>
/// Node3D handles all rendering — <see cref="TileView"/> is the back-pointer.
/// </summary>
public class TileData
{
    /// <summary>Axial hex coordinate (q, r) identifying this tile's grid position.</summary>
    public Vector2I Axial;

    /// <summary>Back-pointer to the visual Node3D representing this tile. May be null in headless contexts (tests, save loading).</summary>
    public HexTile TileView = null;

    // ── Tile properties ─────────────────────────────────────────────────────

    /// <summary>True when units may traverse this tile under normal conditions.</summary>
    public bool IsWalkable = true;

    /// <summary>True when an obstacle (rubble, wall, etc.) physically blocks entry. Overrides <see cref="IsWalkable"/>.</summary>
    public bool IsBlocked = false;

    /// <summary>True when the tile's height delta from neighbours blocks movement (e.g. raised by raise_terrain).</summary>
    public bool BlocksMovementByHeight = false;

    /// <summary>True when this tile blocks line-of-sight checks for targeting and ranged effects.</summary>
    public bool BlocksLineOfSight = false;

    /// <summary>True when standing on this tile damages the unit each turn (lava, scorched ground, etc.).</summary>
    public bool IsHazardous = false;

    /// <summary>Optional persistent modifier string ("rubble", "scorched", "frozen", ...). Applied via <see cref="ApplyTerrainModifier"/>.</summary>
    public string TerrainModifier = "";

    /// <summary>Glyph currently placed on this tile, if any. Null when no glyph is active.</summary>
    public GlyphData Glyph;

    // ── Layout and pathfinding ──────────────────────────────────────────────

    /// <summary>Current movement cost in points. Modified by <see cref="ApplyTerrainModifier"/>.</summary>
    public int MoveCost = 1;

    /// <summary>Saved original movement cost so terrain modifier changes can be reverted.</summary>
    public int BaseMoveCost = 1;

    /// <summary>Tile height in integer steps. 0 is ground; positive values are raised terrain.</summary>
    public int Height = 0;

    // ── Gameplay properties ─────────────────────────────────────────────────

    /// <summary>Underlying terrain biome. Distinct from <see cref="ElementType"/>, which is a per-cast imbuement layered on top.</summary>
    public TileTerrainType TerrainType = TileTerrainType.Grass;

    /// <summary>Currently imbued element. <see cref="TileElementType.None"/> means no imbuement.</summary>
    public TileElementType ElementType = TileElementType.None;

    /// <summary>Magnitude of the current imbuement (0..1 typical). Used by element-aware effects to scale.</summary>
    public float ElementStrength = 0f;

    // ── Occupancy ───────────────────────────────────────────────────────────

    /// <summary>Free-text key identifying any obstacle on the tile ("boulder", "wall", etc.). Empty when no obstacle.</summary>
    public string ObstacleKind = "";

    /// <summary>The unit currently standing on this tile, if any. Null means unoccupied.</summary>
    public Unit Occupant = null;

    /// <summary>Convenience accessor — true when <see cref="Occupant"/> is non-null.</summary>
    public bool IsOccupied => Occupant != null;

    /// <summary>Returns true when the given unit may step onto this tile (walkable, unblocked, unoccupied). Does not actually move the unit.</summary>
    public bool CanEnter(Unit unit)
    {
        return IsWalkable && !IsBlocked && !IsOccupied;
    }

    /// <summary>Attempts to claim the tile for the given unit. Returns true on success, false if the tile is already occupied or impassable.</summary>
    public bool TrySetOccupant(Unit unit)
    {
        if (unit == null) return false;
        if (Occupant != null) return false;
        if (!CanEnter(unit)) return false;

        Occupant = unit;
        return true;
    }

    /// <summary>Clears the occupant only if it matches <paramref name="unit"/>. Defensive against double-release bugs.</summary>
    public void ClearOccupant(Unit unit)
    {
        if (unit != null && Occupant == unit)
            Occupant = null;
    }

    // ── Memorial data for necromancy and flavor ─────────────────────────────
    /// <summary>
    /// When a unit dies on this tile, a MemorialData instance is created to track the source, strength, and age of the death.
    /// </summary>
    public MemorialData Memorial = null;

    /// <summary>
    /// Convenience accessor — true when a memorial is present and has not been consumed by a card effect this turn. Note that a memorial may be present but "consumed" (flagged for removal at end of turn) if a card effect has interacted with it, so this accessor checks both conditions.
    /// </summary>
    public bool HasMemorial => Memorial != null && !Memorial.ConsumedThisTurn;
    public bool IsHallowed => HasMemorial && Memorial.State == MemorialState.Hallowed;

    /// <summary>
    /// Applies a named persistent terrain modifier ("rubble", "scorched", "frozen") that
    /// changes move cost and hazard state. Passing "none" reverts to the saved base cost.
    /// Modifiers do NOT stack — applying a new one replaces the prior modifier.
    /// </summary>
    public void ApplyTerrainModifier(string modifier)
    {
        TerrainModifier = modifier;
        BaseMoveCost = MoveCost; // save original

        switch (modifier)
        {
            case "rubble":
                MoveCost = 2;
                TerrainType = TileTerrainType.Stone;
                break;
            case "scorched":
                MoveCost = 2;
                IsHazardous = true;
                break;
            case "frozen":
                MoveCost = 1;
                TerrainType = TileTerrainType.Ice;
                break;
            case "none":
                MoveCost = BaseMoveCost;
                IsHazardous = false;
                TerrainModifier = "";
                break;
        }
    }
}
