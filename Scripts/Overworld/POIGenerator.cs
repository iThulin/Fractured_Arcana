using Godot;
using System.Collections.Generic;

/// <summary>
/// Places POIs (points of interest) on the overworld hex grid.
/// Phase 1: random scatter with spacing rules. Phase 2+: terrain affinity, region definitions.
/// </summary>
public static class POIGenerator
{
    /// <summary>
    /// Scatter POIs across the grid. Call after grid generation, before fog init.
    /// </summary>
    public static void Generate(OverworldHexGrid grid, int combatCount = 7, int restCount = 3)
    {
        var candidates = new List<Vector2I>();
        var placed = new List<Vector2I>();

        // Collect eligible hexes (not entry, not objective, not water)
        foreach (var kvp in grid.Hexes)
        {
            var coord = kvp.Key;
            var hex = kvp.Value;

            if (coord == grid.EntryCoord) continue;
            if (coord == grid.ObjectiveCoord) continue;
            if (hex.Terrain == OverworldHex.TerrainType.Water) continue;

            // Keep POIs away from entry and objective (minimum 3 hexes)
            if (grid.Distance(coord, grid.EntryCoord) < 3) continue;
            if (grid.Distance(coord, grid.ObjectiveCoord) < 2) continue;

            candidates.Add(coord);
        }

        // Shuffle candidates
        Shuffle(candidates);

        // Place combat POIs
        int combatPlaced = 0;
        foreach (var coord in candidates)
        {
            if (combatPlaced >= combatCount) break;
            if (!IsSpacedEnough(coord, placed, grid, 2)) continue;

            grid.Hexes[coord].POI = OverworldHex.POIType.Combat;
            placed.Add(coord);
            combatPlaced++;
        }

        // Place rest POIs (from remaining candidates)
        int restPlaced = 0;
        foreach (var coord in candidates)
        {
            if (restPlaced >= restCount) break;
            if (placed.Contains(coord)) continue;
            if (!IsSpacedEnough(coord, placed, grid, 2)) continue;

            grid.Hexes[coord].POI = OverworldHex.POIType.Rest;
            placed.Add(coord);
            restPlaced++;
        }

        GD.Print($"POIs placed: {combatPlaced} combat, {restPlaced} rest");
    }

    private static bool IsSpacedEnough(Vector2I coord, List<Vector2I> existing, 
                                        OverworldHexGrid grid, int minDist)
    {
        foreach (var other in existing)
        {
            if (grid.Distance(coord, other) < minDist)
                return false;
        }
        return true;
    }

    private static void Shuffle(List<Vector2I> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = (int)(GD.Randi() % (uint)(i + 1));
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}