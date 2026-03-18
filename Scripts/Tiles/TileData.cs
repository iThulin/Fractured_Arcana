using Godot;
using System;

public class TileData
{
	public Vector2I Axial;
    public bool IsWalkable = true;
    public bool IsBlocked = false;
    public int ElementId = 0;
    public string ObstacleKind = "";
    public Unit Occupant = null;

    public HexTile TileView = null;

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
