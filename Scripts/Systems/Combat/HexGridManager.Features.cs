using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Features.cs  (partial of HexGridManager)
//
// Purpose:        A library of composable, NOISE-DRIVEN terrain feature
//                 builders, plus the new theme recipes and layout
//                 skeletons that compose them. Features are the primitive;
//                 themes/layouts are thin recipes over them. Boundaries are
//                 perturbed by seeded noise so nothing reads as a perfect
//                 circle or straight line — this is where map irregularity
//                 comes from.
// Layer:          System (generation)
// Collaborators:  HexGridManager.cs (core grid, _rng, Tiles, Paint*/Make*),
//                 MapField.cs (base terrain — runs before these)
// Notes:          Everything here is seeded via _rng, so a given MapSeed
//                 reproduces the same features. Reserved (spawn) tiles are
//                 skipped by every builder.
// ============================================================

public partial class HexGridManager : Node3D
{
    // ── Shared helpers ───────────────────────────────────────────────────────

    /// <summary>A fresh seeded FastNoiseLite for perturbing a feature's boundary. Advancing _rng keeps every feature seed-deterministic.</summary>
    private FastNoiseLite FeatureNoise(float frequency)
    {
        return new FastNoiseLite
        {
            Seed = (int)_rng.Randi(),
            Frequency = frequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex
        };
    }

    private static float Norm01(float n) => (n + 1f) * 0.5f;

    /// <summary>Turns a tile into water: impassable, low, no obstacle/LOS block.</summary>
    private void MakeWater(TileData tile)
    {
        ClearTileObstacleState(tile);
        tile.TerrainType = TileTerrainType.Water;
        tile.IsWalkable = false;
        tile.IsBlocked = false;
        tile.BlocksLineOfSight = false;
        tile.IsHazardous = false;
        tile.MoveCost = 999;
        tile.ElementType = TileElementType.None;
        tile.ElementStrength = 0f;
        tile.Height = Math.Min(tile.Height, -1);
    }

    /// <summary>
    /// Core irregular-region primitive. Fills a blob around `center` whose boundary
    /// is perturbed by noise (so it's organic, not a circle). `apply` receives each
    /// tile and a 0..1 depth value (0 = at the ragged edge, 1 = core).
    /// </summary>
    private void OrganicBlob(Vector2I center, int maxRadius, float perturb, float threshold, Action<TileData, float> apply)
    {
        if (maxRadius < 1)
            maxRadius = 1;

        var noise = FeatureNoise(0.55f);

        foreach (var kvp in Tiles)
        {
            if (IsReserved(kvp.Key))
                continue;

            int d = Distance(center, kvp.Key);
            if (d > maxRadius)
                continue;

            float edgeT = 1f - (d / (float)maxRadius);             // 0 edge .. 1 centre
            float n = Norm01(noise.GetNoise2D(kvp.Key.X, kvp.Key.Y));
            float field = edgeT + (n - 0.5f) * perturb;            // perturb the boundary

            if (field < threshold)
                continue;

            float t = Mathf.Clamp((field - threshold) / Mathf.Max(0.001f, 1f - threshold), 0f, 1f);
            apply(kvp.Value, t);
        }
    }

    private Vector2I PickHighTile()
    {
        Vector2I best = Vector2I.Zero;
        int bestH = int.MinValue;
        bool found = false;

        foreach (var kvp in Tiles)
        {
            if (IsReserved(kvp.Key))
                continue;
            if (!found || kvp.Value.Height > bestH)
            {
                bestH = kvp.Value.Height;
                best = kvp.Key;
                found = true;
            }
        }

        return found ? best : GetRandomCoord();
    }

    // ── Feature builders ───────────────────────────────────────────────────────

    /// <summary>Irregular lake with a shallow shore ring; settles into a basin.</summary>
    private void CarveLake(Vector2I center, int maxRadius, int depth)
    {
        OrganicBlob(center, maxRadius, 0.6f, 0.42f, (tile, t) =>
        {
            if (t > 0.18f)
            {
                MakeWater(tile);
                tile.Height = Math.Min(tile.Height, -depth);
            }
            else
            {
                // shore: walkable shallow ground
                ClearTileObstacleState(tile);
                tile.TerrainType = TileTerrainType.Grass;
                tile.IsWalkable = true;
                tile.MoveCost = 1;
            }
        });
    }

    /// <summary>
    /// A river that follows the heightfield downhill from `start`, meandering via
    /// noise and exiting at a map edge. width 0 = single channel, 1 = wider.
    /// </summary>
    private void CarveRiver(Vector2I start, int maxLength, int width)
    {
        var noise = FeatureNoise(0.5f);
        var visited = new HashSet<Vector2I>();
        Vector2I current = start;

        for (int i = 0; i < maxLength; i++)
        {
            if (!Tiles.ContainsKey(current))
                break;

            visited.Add(current);

            if (!IsReserved(current))
                MakeWater(Tiles[current]);

            if (width >= 1)
            {
                foreach (var nb in GetNeighbors(current))
                    if (!IsReserved(nb))
                        MakeWater(Tiles[nb]);
            }

            // exit the map → river mouth
            if (GetNeighbors(current).Count < 6)
                break;

            // pick the lowest unvisited neighbour, with a noise wobble for meander
            Vector2I best = current;
            float bestScore = float.MaxValue;
            bool found = false;

            foreach (var nb in GetNeighbors(current))
            {
                if (visited.Contains(nb))
                    continue;

                float wobble = (Norm01(noise.GetNoise2D(nb.X, nb.Y)) - 0.5f) * 1.5f;
                float score = Tiles[nb].Height + wobble;

                if (!found || score < bestScore)
                {
                    bestScore = score;
                    best = nb;
                    found = true;
                }
            }

            if (!found)
                break;

            current = best;
        }
    }

    /// <summary>A narrow, short river.</summary>
    private void CarveStream(Vector2I start, int maxLength) => CarveRiver(start, maxLength, 0);

    /// <summary>
    /// A deep, meandering impassable gash — a chasm. Tiles become unwalkable gaps
    /// (movement-blocking but NOT line-of-sight blocking — you can see across).
    /// </summary>
    private void CarveCrevice(Vector2I start, Vector2I dir, int length, int depth)
    {
        var noise = FeatureNoise(0.6f);
        Vector2I current = start;

        for (int i = 0; i < length; i++)
        {
            if (!Tiles.ContainsKey(current))
                break;

            if (!IsReserved(current))
            {
                var tile = Tiles[current];
                ClearTileObstacleState(tile);
                tile.Height -= depth;
                tile.IsWalkable = false;
                tile.IsBlocked = false;
                tile.BlocksLineOfSight = false;
            }

            current += dir;

            // meander sideways occasionally
            if (Norm01(noise.GetNoise2D(current.X * 1.3f, current.Y * 1.3f)) < 0.35f)
            {
                var perp = HexDirs[_rng.RandiRange(0, HexDirs.Length - 1)];
                if (Tiles.ContainsKey(current + perp))
                    current += perp;
            }
        }
    }

    /// <summary>
    /// Ramps the whole map upward toward the high-X edge into stone cliffs.
    /// Uses a squared falloff so the rise is gentle near the low side and steep
    /// (cliff-like) near the top — pairs well with a larger HeightStep.
    /// </summary>
    private void RaiseMountainside(int peakHeight)
    {
        float span = Mathf.Max(0.001f, _layoutMaxX - _layoutMinX);

        foreach (var kvp in Tiles)
        {
            if (IsReserved(kvp.Key))
                continue;

            float tNorm = (AxialToWorld(kvp.Key).X - _layoutMinX) / span; // 0..1 across map
            int add = Mathf.RoundToInt(tNorm * tNorm * peakHeight);
            if (add <= 0)
                continue;

            var tile = kvp.Value;
            tile.Height += add;
            tile.TerrainType = TileTerrainType.Stone;
            tile.IsWalkable = true;
            tile.MoveCost = 1;

            if (tNorm > 0.8f && _rng.Randf() < 0.3f)
                MakeRockObstacle(tile);
        }
    }

    /// <summary>Soft, organic grass clearing — clears obstacles, keeps gentle undulation.</summary>
    private void PlantMeadow(Vector2I center, int radius)
    {
        OrganicBlob(center, radius, 0.5f, 0.4f, (tile, t) =>
        {
            ClearTileObstacleState(tile);
            tile.TerrainType = TileTerrainType.Grass;
            tile.IsWalkable = true;
            tile.MoveCost = 1;
        });
    }

    /// <summary>Flattened open ground for tactical breathing room — removes cover and levels height.</summary>
    private void CarveClearing(Vector2I center, int radius)
    {
        int baseH = Tiles.TryGetValue(center, out var c) ? c.Height : 0;

        OrganicBlob(center, radius, 0.45f, 0.4f, (tile, t) =>
        {
            ClearTileObstacleState(tile);
            tile.TerrainType = TileTerrainType.Grass;
            tile.IsWalkable = true;
            tile.MoveCost = 1;
            tile.Height = baseH;
        });
    }

    /// <summary>Scatters several irregular forest clusters across the map.</summary>
    private void ScatterCopses(int count, int radius)
    {
        for (int i = 0; i < count; i++)
        {
            OrganicBlob(GetRandomCoord(), radius, 0.7f, 0.5f, (tile, t) =>
            {
                if (tile.TerrainType == TileTerrainType.Water)
                    return;
                ClearTileObstacleState(tile);
                tile.TerrainType = TileTerrainType.Forest;
                tile.IsWalkable = true;
                tile.MoveCost = 2;
            });
        }
    }

    /// <summary>A raised, irregular rocky knoll with scattered blocking boulders near the crown.</summary>
    private void RockyOutcrop(Vector2I center, int radius)
    {
        OrganicBlob(center, radius, 0.6f, 0.5f, (tile, t) =>
        {
            tile.TerrainType = TileTerrainType.Stone;
            tile.IsWalkable = true;
            tile.MoveCost = 1;
            tile.Height = Math.Max(tile.Height, 1 + Mathf.RoundToInt(t * 2));

            if (t > 0.6f && _rng.Randf() < 0.5f)
                MakeRockObstacle(tile);
        });
    }

    // ── New theme recipes (run after field base terrain, layered as accents) ────

    private void ApplyVerdantWoodsTheme()
    {
        ScatterCopses(3 + _rng.RandiRange(0, 2), GetPatchRadius(2, 3));
        CarveStream(PickHighTile(), 8);
        CarveClearing(GetRandomCentralCoord(), 2);
        if (_rng.Randf() < 0.5f)
            RockyOutcrop(GetRandomCoord(), 1);
    }

    private void ApplyWetlandsTheme()
    {
        CarveLake(GetRandomCentralCoord(), GetPatchRadius(2, 3), 2);
        CarveLake(GetRandomCoord(), 2, 1);
        CarveStream(PickHighTile(), 10);
        ScatterCopses(2, 2);
    }

    private void ApplyHighlandCragsTheme()
    {
        RaiseMountainside(4);
        RockyOutcrop(GetRandomCoord(), 2);

        Vector2I start = GetRandomCoord();
        Vector2I dir = HexDirs[_rng.RandiRange(0, HexDirs.Length - 1)];
        CarveCrevice(start, dir, 5, 3);
    }

    private void ApplyRiverValleyTheme()
    {
        CarveRiver(PickHighTile(), 14, 1);
        PlantMeadow(GetRandomCentralCoord(), 2);
        PlantMeadow(GetRandomCoord(), 2);
        ScatterCopses(1, 2);
    }

    private void ApplyHeathlandTheme()
    {
        PlantMeadow(GetRandomCentralCoord(), GetPatchRadius(3, 4));
        ScatterCopses(2, 1);
        if (_rng.Randf() < 0.4f)
            RockyOutcrop(GetRandomCoord(), 1);
    }

    private void ApplyCoastalShallowsTheme()
    {
        float span = Mathf.Max(0.001f, _layoutMaxX - _layoutMinX);
        var noise = FeatureNoise(0.5f);

        foreach (var kvp in Tiles)
        {
            if (IsReserved(kvp.Key))
                continue;

            float tNorm = (AxialToWorld(kvp.Key).X - _layoutMinX) / span;
            float n = Norm01(noise.GetNoise2D(kvp.Key.X, kvp.Key.Y));
            float waterEdge = 0.30f + (n - 0.5f) * 0.25f; // irregular coastline

            if (tNorm < waterEdge)
            {
                MakeWater(kvp.Value);
            }
            else if (tNorm < waterEdge + 0.12f)
            {
                // beach
                ClearTileObstacleState(kvp.Value);
                kvp.Value.TerrainType = TileTerrainType.Grass;
                kvp.Value.IsWalkable = true;
                kvp.Value.MoveCost = 1;
                kvp.Value.Height = Math.Max(0, kvp.Value.Height - 1);
            }
        }

        ScatterCopses(1, 2);
    }

    // ── New layout skeletons (run before spawn plan; anchors known) ────────────

    private void GenerateOpenFieldLayout()
    {
        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);
        PaintObstacleCluster(GetRandomNearbyCoord(center, 2), "rock", 2);
        // deliberately sparse — flank room is the point
    }

    private void GenerateChokepointLayout()
    {
        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);

        // A crevice wall across the player→enemy axis (dir HexDirs[5] runs "vertically").
        Vector2I wallStart = center + HexDirs[2] * 2; // shift up so it spans the middle
        CarveCrevice(wallStart, HexDirs[5], 6, 4);

        // Carve one or two crossings over it — these become the chokepoints.
        CarveLane(PlayerLayoutAnchor, EnemyLayoutAnchor, 0);
        if (_rng.Randf() < 0.6f)
            CarveLane(PlayerLayoutAnchor, center + HexDirs[5] * 2, 0);
    }

    private void GenerateHighGroundLayout()
    {
        // Enemy side ramps up into defensible high ground.
        RaiseMountainside(3);
        CarveLane(PlayerLayoutAnchor, EnemyLayoutAnchor, 1);
        PaintObstacleCluster(GetRandomNearbyCoord(EnemyLayoutAnchor, 2), "rock", 2);
    }

    private void GenerateScatteredCoverLayout()
    {
        int clusters = GetObstacleClusterCount(3, 6);
        for (int i = 0; i < clusters; i++)
        {
            string kind = _rng.Randf() < 0.5f ? "rock" : "crystal";
            PaintObstacleCluster(GetRandomCoord(), kind, GetObstacleClusterSize(1, 3));
        }
        ScatterCopses(2, 1);
    }
}
