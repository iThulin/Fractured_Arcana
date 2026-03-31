using Godot;
using System;
using System.Runtime.CompilerServices;

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

public class TileData
{
	public Vector2I Axial;
	public HexTile TileView = null;

	// Tile properties
    public bool IsWalkable = true;
    public bool IsBlocked = false;
	public bool BlocksMovementByHeight = false;
	public bool BlocksLineOfSight = false;
	public bool IsHazardous = false;

	// Layout and pathfinding
	public int MoveCost = 1;
	public int Height = 0;

	// Gameplay properties
	public TileTerrainType TerrainType = TileTerrainType.Grass;
	public TileElementType ElementType = TileElementType.None;
	public float ElementStrength = 0f;

	// Occupancy
	public string ObstacleKind = "";
	public Unit Occupant = null;
    public bool IsOccupied => Occupant != null;

    public bool CanEnter(Unit unit)
    {
        return IsWalkable && !IsBlocked && !IsOccupied;
    }

    public bool TrySetOccupant(Unit unit)
    {
        if (unit == null) return false;
        if (Occupant != null) return false;
        if (!CanEnter(unit)) return false;

        Occupant = unit;
        return true;
    }

    public void ClearOccupant(Unit unit)
    {
        if (unit != null && Occupant == unit)
            Occupant = null;
    }
}
