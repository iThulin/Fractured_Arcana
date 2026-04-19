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
    public static void Generate(OverworldHexGrid grid, int combatCount = 10, 
                                int restCount = 4, int narrativeCount = 3)
    {
        var candidates = new List<Vector2I>();
        var placed = new List<Vector2I>();

        foreach (var kvp in grid.Hexes)
        {
            var coord = kvp.Key;
            var hex = kvp.Value;

            if (coord == grid.EntryCoord) continue;
            if (coord == grid.ObjectiveCoord) continue;
            if (hex.Terrain == OverworldHex.TerrainType.Water) continue;
            if (grid.Distance(coord, grid.EntryCoord) < 3) continue;
            if (grid.Distance(coord, grid.ObjectiveCoord) < 2) continue;

            candidates.Add(coord);
        }

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

        // Place rest POIs
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

        // Place narrative POIs (prefer ruins and arcane ground)
        int narrativePlaced = 0;
        // First pass: try terrain-appropriate hexes
        foreach (var coord in candidates)
        {
            if (narrativePlaced >= narrativeCount) break;
            if (placed.Contains(coord)) continue;
            if (!IsSpacedEnough(coord, placed, grid, 2)) continue;

            var terrain = grid.Hexes[coord].Terrain;
            if (terrain == OverworldHex.TerrainType.Ruins ||
                terrain == OverworldHex.TerrainType.ArcaneGround ||
                terrain == OverworldHex.TerrainType.Forest)
            {
                grid.Hexes[coord].POI = OverworldHex.POIType.Narrative;
                placed.Add(coord);
                narrativePlaced++;
            }
        }
        // Second pass: fill remaining anywhere
        foreach (var coord in candidates)
        {
            if (narrativePlaced >= narrativeCount) break;
            if (placed.Contains(coord)) continue;
            if (!IsSpacedEnough(coord, placed, grid, 2)) continue;

            grid.Hexes[coord].POI = OverworldHex.POIType.Narrative;
            placed.Add(coord);
            narrativePlaced++;
        }

        GD.Print($"POIs placed: {combatPlaced} combat, {restPlaced} rest, {narrativePlaced} narrative");
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