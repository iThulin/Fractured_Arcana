using Godot;
using System;
using System.Collections.Generic;

// HexGridManager.Generation.cs: field-driven terrain, base grid, layout skeletons, paint/make helpers, density math
// Partial of HexGridManager. Split out for navigability; behaviour-neutral.
public partial class HexGridManager
{
    /// <summary>Builds a seeded terrain/height field, tuned by the current density knobs.</summary>
    private MapField BuildField()
    {
        int fieldSeed = (int)_rng.Randi();

        var field = new MapField(fieldSeed)
        {
            ElevationFrequency = Mathf.Lerp(0.10f, 0.22f, TerrainRoughness),
            MoistureFrequency = Mathf.Lerp(0.08f, 0.16f, TerrainRoughness),
            DetailWeight = Mathf.Lerp(0.08f, 0.30f, TerrainRoughness),
            MaxHeightStep = Mathf.RoundToInt(Mathf.Lerp(2, 5, HeightVariation)),
            MinHeightStep = -Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1, 3, HeightVariation)))
        };

        return field;
    }

    /// <summary>Derives terrain type and integer height for every tile from the field.</summary>
    private void ApplyFieldTerrainAndHeight(MapField field)
    {
        var palette = _activeRecipe?.BaseTerrain?.Palette;

        foreach (var tile in Tiles.Values)
        {
            float elevation = field.SampleElevation01(tile.Axial);
            float moisture = field.SampleMoisture01(tile.Axial);

            TileTerrainType terrain = palette != null
                ? field.ClassifyByPalette(palette, elevation, moisture)
                : field.ClassifyTerrain(Theme, elevation, moisture);

            ApplyTerrainType(tile, terrain);
            tile.Height = field.ElevationToHeightStep(elevation);
        }
    }

    /// <summary>
    /// Sets gameplay flags + element for a terrain type. Does NOT touch Height:
    /// height is owned by the field / additive features, so terrain and height
    /// stay correlated without one clobbering the other.
    /// </summary>
    private void ApplyTerrainType(TileData tile, TileTerrainType terrain)
    {
        tile.TerrainType = terrain;
        tile.IsBlocked = false;
        tile.BlocksLineOfSight = false;
        tile.ObstacleKind = "";
        tile.AuthoredCover = CoverKind.None;
        tile.IsHazardous = false;
        tile.ElementType = TileElementType.None;
        tile.ElementStrength = 0f;

        switch (terrain)
        {
            case TileTerrainType.Grass:
                tile.IsWalkable = true;
                tile.MoveCost = 1;
                break;

            case TileTerrainType.Forest:
                tile.IsWalkable = true;
                tile.MoveCost = 2;
                break;

            case TileTerrainType.Stone:
                tile.IsWalkable = true;
                tile.MoveCost = 1;
                break;

            case TileTerrainType.Ice:
                tile.IsWalkable = true;
                tile.MoveCost = 1;
                break;

            case TileTerrainType.Water:
                tile.IsWalkable = false;
                tile.MoveCost = 999;
                break;

            case TileTerrainType.Lava:
                tile.IsWalkable = true;
                tile.MoveCost = 2;
                tile.IsHazardous = true;
                tile.ElementType = TileElementType.Fire;
                tile.ElementStrength = 1.0f;
                break;

            case TileTerrainType.Arcane:
                tile.IsWalkable = true;
                tile.MoveCost = 1;
                tile.ElementType = TileElementType.Arcane;
                tile.ElementStrength = 1.0f;
                break;

            case TileTerrainType.Sand:
                tile.IsWalkable = true;
                tile.MoveCost = 1;
                break;

            default:
                tile.IsWalkable = true;
                tile.MoveCost = 1;
                break;
        }
    }

    private void GenerateBaseGrid()
    {
        Tiles.Clear();

        List<Vector2I> coords = MapShapeBuilder.Build(Shape, GridWidth, GridHeight, MapRadius, BlobErosion, _rng);

        bool first = true;
        Vector3 min = Vector3.Zero;
        Vector3 max = Vector3.Zero;

        foreach (var coord in coords)
        {
            var worldPos = AxialToWorld(coord);

            var tileNode = HexTileScene3D.Instantiate<HexTile>();
            tileNode.Position = worldPos;
            tileNode.Axial = coord;
            AddChild(tileNode);

            tileNode.SetCoordinatesLabel(coord.X, coord.Y);

            var tileData = new TileData
            {
                Axial = coord,
                TileView = tileNode,
                IsWalkable = true,
                IsBlocked = false
            };

            tileNode.Data = tileData;
            Tiles[coord] = tileData;
            tileNode.RefreshLabel(tileData);

            var p = tileNode.GlobalPosition;
            if (first)
            {
                min = p;
                max = p;
                first = false;
            }
            else
            {
                min = new Vector3(Mathf.Min(min.X, p.X), 0, Mathf.Min(min.Z, p.Z));
                max = new Vector3(Mathf.Max(max.X, p.X), 0, Mathf.Max(max.Z, p.Z));
            }
        }

        GridBoundsMin = min;
        GridBoundsMax = max;
    }

    private void GenerateLayoutSkeleton()
    {
        switch (LayoutType)
        {
            case MapLayoutType.CentralClash:
                GenerateCentralClashLayout();
                break;

            case MapLayoutType.SplitLanes:
                GenerateSplitLanesLayout();
                break;

            case MapLayoutType.RingCourtyard:
                GenerateRingCourtyardLayout();
                break;
            case MapLayoutType.OpenField:
                GenerateOpenFieldLayout();
                break;

            case MapLayoutType.Chokepoint:
                GenerateChokepointLayout();
                break;

            case MapLayoutType.HighGround:
                GenerateHighGroundLayout();
                break;

            case MapLayoutType.ScatteredCover:
                GenerateScatteredCoverLayout();
                break;
        }
    }

    private void ApplyTileHeights()
    {
        if (Tiles.Count == 0)
            return;

        int minHeight = int.MaxValue;
        foreach (var tile in Tiles.Values)
            minHeight = Math.Min(minHeight, tile.Height);

        _lastWorldFloor = (minHeight - 1) * HexTile.HeightStep;

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null)
                continue;
            tile.TileView.SetHeight(tile.Height, _lastWorldFloor);
        }

        if (UseBlendedTerrainMesh)
        {
            foreach (var tile in Tiles.Values)
                RebuildTerrainMesh(tile);
        }
    }

    private void ResetTileHeights()
    {
        foreach (var tile in Tiles.Values)
            tile.Height = 0;
    }

    private void ResetTileStateForGeneration()
    {
        foreach (var tile in Tiles.Values)
        {
            tile.TerrainType = TileTerrainType.Grass;
            tile.ElementType = TileElementType.None;
            tile.ElementStrength = 0f;
            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.BlocksLineOfSight = false;
            tile.IsHazardous = false;
            tile.MoveCost = 1;
            tile.ObstacleKind = "";
            tile.AuthoredCover = CoverKind.None;
            tile.Height = 0;
        }
    }


    // Paint Terrain and Features

    private void PaintTerrainPatch(Vector2I center, TileTerrainType terrain, int radius, float edgeChance = 0.75f)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            bool apply = true;

            if (dist == radius)
                apply = _rng.Randf() < edgeChance;

            if (!apply)
                continue;

            tile.TerrainType = terrain;

            switch (terrain)
            {
                case TileTerrainType.Water:
                    tile.IsWalkable = false;
                    tile.IsBlocked = false;
                    tile.MoveCost = 999;
                    break;

                case TileTerrainType.Forest:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 2;
                    break;

                case TileTerrainType.Stone:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;

                case TileTerrainType.Ice:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;

                case TileTerrainType.Lava:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 2;
                    tile.IsHazardous = true;
                    break;

                case TileTerrainType.Arcane:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;

                case TileTerrainType.Sand:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;

                default:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;
            }
        }
    }

    private void PaintElementPatch(Vector2I center, TileElementType element, int radius, float strength = 1.0f, float edgeChance = 0.75f)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            bool apply = true;

            if (dist == radius)
                apply = _rng.Randf() < edgeChance;

            if (!apply)
                continue;

            tile.ElementType = element;
            tile.ElementStrength = Mathf.Clamp(strength - (dist * 0.2f), 0.2f, 1.0f);

            if (element == TileElementType.Fire)
                tile.IsHazardous = true;
        }
    }

    /// <summary>Paints an Arcane leyline: a branching vein of Arcane imbuement
    /// walking <paramref name="length"/> tiles from <paramref name="start"/> along
    /// <paramref name="direction"/> (tile_interaction addendum §B2). Leylines carry
    /// no movement verb; they feed the Arcanist's element-synergy cards
    /// (mana_per_nearby_element, has_elements_near_caster, consume_element_tile) and
    /// give ordinary (non-elemental-biome) maps something the tile layer reads.
    /// Skips water / blocked / reserved tiles. Generation-time: writes data only;
    /// the visual pass stamps the overlay from ElementType.</summary>
    private void PaintLeyline(Vector2I start, Vector2I direction, int length,
                             float branchChance = 0.35f, float strength = 1.0f)
    {
        PaintLinearFeature(start, direction, length, tile =>
        {
            if (!tile.IsWalkable || tile.IsBlocked)
                return;
            if (tile.TerrainType == TileTerrainType.Water)
                return;
            tile.ElementType = TileElementType.Arcane;
            tile.ElementStrength = strength;
        }, branchChance);
    }

    private void PaintHeightPatch(Vector2I center, int radius, int peakHeight)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            int height = Math.Max(0, peakHeight - dist);

            tile.Height = Math.Max(tile.Height, height);
        }
    }

    private void PaintHeightHill(Vector2I center, int radius, int peakHeight)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            int appliedHeight = Math.Max(0, peakHeight - dist);
            tile.Height = Math.Max(tile.Height, appliedHeight);
        }
    }

    private void PaintHeightBasin(Vector2I center, int radius, int depth)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            int depression = Math.Max(0, depth - dist);
            tile.Height -= depression;
        }
    }

    private void PaintHeightRidge(Vector2I start, Vector2I direction, int length, int ridgeHeight)
    {
        Vector2I current = start;

        for (int i = 0; i < length; i++)
        {
            if (!Tiles.TryGetValue(current, out var tile))
                break;

            if (!IsReserved(current))
                tile.Height = Math.Max(tile.Height, ridgeHeight);

            foreach (var neighbor in GetNeighbors(current))
            {
                if (!Tiles.TryGetValue(neighbor, out var neighborTile))
                    continue;

                if (IsReserved(neighbor))
                    continue;

                neighborTile.Height = Math.Max(neighborTile.Height, ridgeHeight - 1);
            }

            current += direction;
        }
    }

    private void PaintLinearFeature(Vector2I start, Vector2I direction, int length, Action<TileData> applyToTile, float branchChance = 0.0f)
    {
        Vector2I current = start;

        for (int i = 0; i < length; i++)
        {
            if (Tiles.TryGetValue(current, out var tile) && !IsReserved(current))
            {
                applyToTile(tile);
            }

            if (_rng.Randf() < branchChance)
            {
                var neighbors = GetNeighbors(current);
                if (neighbors.Count > 0)
                {
                    var branch = neighbors[_rng.RandiRange(0, neighbors.Count - 1)];
                    if (Tiles.TryGetValue(branch, out var branchTile) && !IsReserved(branch))
                        applyToTile(branchTile);
                }
            }

            current += direction;

            if (!Tiles.ContainsKey(current))
                break;
        }
    }

    private void PaintRingFeature(Vector2I center, int radius, Action<TileData> applyToTile, float edgeChance = 1.0f)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist != radius)
                continue;

            if (_rng.Randf() > edgeChance)
                continue;

            applyToTile(tile);
        }
    }

    private void PaintFilledRadius(Vector2I center, int radius, Action<TileData> applyToTile, float edgeChance = 1.0f)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            if (dist == radius && _rng.Randf() > edgeChance)
                continue;

            applyToTile(tile);
        }
    }

    /// <summary>True when the kind is registered as Low cover in the obstacle
    /// catalog (Data/Obstacles/obstacle_catalog.json). Unknown kinds are High.</summary>
    public static bool IsLowObstacleKind(string kind) => ObstacleCatalog.IsLow(kind);

    /// <summary>Role tokens a recipe op may use in place of a concrete kind. The
    /// active recipe's obstacles palette (or the terrain default) picks the kind.</summary>
    private static readonly HashSet<string> ObstacleRoleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "low", "high", "pillar"
    };

    /// <summary>Resolve what an op wrote in "kind": a role token becomes the palette's
    /// kind for that role; anything else is taken as a concrete catalog kind.</summary>
    public string ResolveObstacleKind(string kindOrRole)
    {
        if (string.IsNullOrEmpty(kindOrRole))
            return ResolveObstacleKind("high");
        if (!ObstacleRoleTokens.Contains(kindOrRole))
            return kindOrRole;

        string role = kindOrRole.ToLowerInvariant();
        var palette = _activeRecipe?.Obstacles;
        string kind = palette?.Get(role);
        if (string.IsNullOrEmpty(kind))
            kind = ObstaclePalette.DefaultFor(DominantBaseTerrain(), role);
        return kind;
    }

    /// <summary>The terrain most of the grid is made of, for palette defaults on a
    /// recipe that declares none (and for the enum-theme path).</summary>
    private TileTerrainType DominantBaseTerrain()
    {
        var counts = new Dictionary<TileTerrainType, int>();
        foreach (var t in Tiles.Values)
        {
            if (t == null) continue;
            counts[t.TerrainType] = counts.TryGetValue(t.TerrainType, out var c) ? c + 1 : 1;
        }
        var best = TileTerrainType.Grass;
        int bestN = -1;
        foreach (var kv in counts)
        {
            if (kv.Key == TileTerrainType.Water) continue;   // water is never the dressing
            if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
        }
        return best;
    }

    /// <summary>Single write site for obstacle flags so cover and sight stay in
    /// agreement. Accepts a role token or a concrete kind. Low kinds: blocked,
    /// walkable false, sight clear, Low cover. High kinds: blocked, walkable false,
    /// sight blocked (which CoverAt reads as High). Unknown kinds warn once and
    /// paint as High (ObstacleCatalog.GetOrFallback).</summary>
    public void ApplyObstacle(TileData tile, string kindOrRole)
    {
        if (tile == null)
            return;
        string kind = ResolveObstacleKind(kindOrRole);
        var spec = ObstacleCatalog.GetOrFallback(kind);

        tile.IsBlocked = true;
        tile.IsWalkable = false;
        tile.ObstacleKind = kind ?? "";
        if (spec.IsLow)
        {
            tile.BlocksLineOfSight = false;
            tile.AuthoredCover = CoverKind.Low;
        }
        else
        {
            tile.BlocksLineOfSight = true;
            tile.AuthoredCover = CoverKind.None;
        }
    }

    /// <summary>cover_line (cover_and_zoc_v1 §7.6): a band of Low cover laid across
    /// the player-enemy axis so the approach has something to crouch behind.
    /// Centred on <paramref name="center"/>, extended <paramref name="length"/> tiles
    /// along <paramref name="direction"/> (half each way, rounded toward the
    /// negative side), with <paramref name="gaps"/> tiles left open at random so the
    /// line is never a wall, and <paramref name="fill"/> as the per-tile density on
    /// top of that. Skips reserved (spawn) tiles, water, and occupied tiles. The
    /// kind defaults to the "low" role (the recipe's obstacles palette picks the
    /// dressing); a High kind or the "high" role makes a proper wall with gaps.</summary>
    private void PaintCoverLine(Vector2I center, Vector2I direction, int length, string kind, int gaps, float fill)
    {
        if (length <= 0)
            return;

        var coords = new List<Vector2I>();
        int back = length / 2;
        for (int i = -back; i < length - back; i++)
        {
            var c = center + direction * i;
            if (Tiles.ContainsKey(c))
                coords.Add(c);
        }
        if (coords.Count == 0)
            return;

        // Guaranteed gaps: never the two end tiles when the line is long enough,
        // so the opening reads as a gate rather than the band just being short.
        var open = new HashSet<int>();
        int interiorLo = coords.Count >= 4 ? 1 : 0;
        int interiorHi = coords.Count >= 4 ? coords.Count - 2 : coords.Count - 1;
        int wanted = Math.Clamp(gaps, 0, Math.Max(0, interiorHi - interiorLo + 1));
        int guard = 0;
        while (open.Count < wanted && guard++ < 64)
            open.Add(_rng.RandiRange(interiorLo, interiorHi));

        for (int i = 0; i < coords.Count; i++)
        {
            if (open.Contains(i))
                continue;
            if (!Tiles.TryGetValue(coords[i], out var tile))
                continue;
            if (IsReserved(coords[i]) || tile.IsOccupied)
                continue;
            if (tile.TerrainType == TileTerrainType.Water)
                continue;
            if (_rng.Randf() > fill)
                continue;
            ApplyObstacle(tile, kind);
        }
    }

    private void PaintObstacleBand(Vector2I start, Vector2I direction, int length, string obstacleKind, float chance = 0.7f)
    {
        Vector2I current = start;

        for (int i = 0; i < length; i++)
        {
            if (!Tiles.TryGetValue(current, out var tile))
                break;

            if (!IsReserved(current) && _rng.Randf() < chance)
                ApplyObstacle(tile, obstacleKind);

            current += direction;
        }
    }

    private void PaintObstacleCluster(Vector2I start, string obstacleKind, int targetSize)
    {
        if (!Tiles.TryGetValue(start, out var startTile))
            return;

        if (startTile.TerrainType == TileTerrainType.Water)
            return;

        if (IsReserved(start))
            return;

        var frontier = new List<Vector2I> { start };
        var visited = new HashSet<Vector2I> { start };

        int placed = 0;

        while (frontier.Count > 0 && placed < targetSize)
        {
            int index = _rng.RandiRange(0, frontier.Count - 1);
            Vector2I current = frontier[index];
            frontier.RemoveAt(index);

            if (!Tiles.TryGetValue(current, out var tile))
                continue;

            if (IsReserved(current))
                continue;

            if (tile.TerrainType == TileTerrainType.Water)
                continue;

            if (tile.IsOccupied)
                continue;

            ApplyObstacle(tile, obstacleKind);
            placed++;

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;

                visited.Add(neighbor);

                if (_rng.Randf() < 0.75f)
                    frontier.Add(neighbor);
            }
        }
    }

    private int GetTerrainPatchCount(int minCount, int maxCount)
    {
        return Mathf.RoundToInt(Mathf.Lerp(minCount, maxCount, TerrainDensity));
    }

    private float GetEdgeChance()
    {
        // Low roughness = smoother edge fill
        // High roughness = more broken edges
        return Mathf.Lerp(0.95f, 0.55f, TerrainRoughness);
    }

    private int GetObstacleClusterCount(int minCount, int maxCount)
    {
        return Mathf.RoundToInt(Mathf.Lerp(minCount, maxCount, ObstacleDensity));
    }

    private int GetObstacleClusterSize(int minSize, int maxSize)
    {
        return Mathf.RoundToInt(Mathf.Lerp(minSize, maxSize, ObstacleDensity));
    }

    private int GetPatchRadius(int minRadius, int maxRadius)
    {
        // Low roughness = larger patches
        // High roughness = smaller patches
        return Mathf.RoundToInt(Mathf.Lerp(maxRadius, minRadius, TerrainRoughness));
    }

    /// <summary>Guarantee pass (battlefield E2.1 #4), hazard-cap half: connectivity and
    /// spawn playability are already guaranteed by EnsureConnectivityBetweenSpawns /
    /// EnsureReservedTilesArePlayable; this caps INITIAL hazard tiles at
    /// <paramref name="maxFraction"/> of walkable so a recipe (or a dense element ring)
    /// can't hand the player a map that is mostly lava. Clears the hazards FURTHEST from
    /// centre first, keeping the contested middle threatening. Recipe maps only.</summary>
    private void EnforceHazardCap(float maxFraction = 0.35f)
    {
        if (_activeRecipe == null)
            return;
        if (PlayerSession.DebugDisableHazardCap)
            return;
        int walkable = 0, hazardCount = 0;
        foreach (var t in Tiles.Values)
        {
            if (t == null || !t.IsWalkable || t.IsBlocked) continue;
            walkable++;
            if (t.IsHazardous) hazardCount++;
        }
        if (walkable == 0) return;
        int cap = (int)(walkable * maxFraction);
        if (hazardCount <= cap) return;

        var hz = new List<TileData>();
        foreach (var kv in Tiles)
            if (kv.Value != null && kv.Value.IsWalkable && !kv.Value.IsBlocked && kv.Value.IsHazardous)
                hz.Add(kv.Value);
        hz.Sort((a, b) => Distance(_centerCoord, b.Axial).CompareTo(Distance(_centerCoord, a.Axial)));
        int toClear = hazardCount - cap;
        for (int i = 0; i < toClear && i < hz.Count; i++)
        {
            hz[i].IsHazardous = false;
            hz[i].ElementType = TileElementType.None;
            hz[i].ElementStrength = 0f;
        }
        GD.Print($"[MapRecipe] '{_activeRecipe?.Id}': hazard cap cleared {toClear} tile(s) (was {hazardCount}/{walkable} walkable).");
    }

    private void CarveLane(Vector2I start, Vector2I goal, int width = 0)
    {
        Vector2I current = start;

        while (current != goal)
        {
            ClearTileForLane(current, width);

            int dq = goal.X - current.X;
            int dr = goal.Y - current.Y;

            Vector2I step = current;

            if (Math.Abs(dq) > Math.Abs(dr))
                step = new Vector2I(current.X + Math.Sign(dq), current.Y);
            else if (dr != 0)
                step = new Vector2I(current.X, current.Y + Math.Sign(dr));

            if (step == current)
                break;

            current = step;
        }

        ClearTileForLane(goal, width);
    }

    private void ClearTileObstacleState(TileData tile)
    {
        tile.IsBlocked = false;
        tile.BlocksLineOfSight = false;
        tile.ObstacleKind = "";
        tile.AuthoredCover = CoverKind.None;
    }

    private void ClearTileForLane(Vector2I center, int width)
    {
        foreach (var coord in Tiles.Keys)
        {
            if (Distance(center, coord) > width)
                continue;

            if (!Tiles.TryGetValue(coord, out var tile))
                continue;

            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.BlocksLineOfSight = false;
            tile.ObstacleKind = "";
            tile.AuthoredCover = CoverKind.None;
            tile.MoveCost = 1;
        }
    }

    private void GenerateBasin()
    {
        Vector2I center = GetRandomCoord();

        foreach (var kvp in Tiles)
        {
            int dist = Distance(center, kvp.Key);
            if (dist <= 2)
            {
                kvp.Value.Height -= (2 - dist);
            }
        }
    }

    private void GenerateHill()
    {
        PaintHeightPatch(GetRandomCoord(), 2, 2);
    }

    private void GenerateRidge()
    {
        Vector2I start = GetRandomCoord();
        Vector2I dir = HexDirs[_rng.RandiRange(0, HexDirs.Length - 1)];

        Vector2I current = start;

        for (int i = 0; i < 5; i++)
        {
            if (Tiles.TryGetValue(current, out var tile))
            {
                tile.Height += 2;

                foreach (var neighbor in GetNeighbors(current))
                {
                    if (Tiles.TryGetValue(neighbor, out var n))
                        n.Height += 1;
                }
            }

            current += dir;
            if (!Tiles.ContainsKey(current))
                break;
        }
    }

    // Map Skeletons

    private void GenerateCentralClashLayout()
    {
        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);

        // Raised central hill / contest point
        PaintHeightHill(center, 2, 2);

        // Main open route
        CarveLane(PlayerLayoutAnchor, center, 1);
        CarveLane(EnemyLayoutAnchor, center, 1);

        // Cover near the center, but not full wall
        PaintObstacleCluster(GetRandomNearbyCoord(center, 2), "rock", 3);
        PaintObstacleCluster(GetRandomNearbyCoord(center, 2), "rock", 2);

        // Flank patches
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 1, 0.8f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 1, 0.8f);
    }

    private void GenerateSplitLanesLayout()
    {
        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);

        // Create a central blocker band to split traffic
        Vector2I dir = HexDirs[2];
        PaintObstacleBand(center, dir, 4, "rock", 0.8f);

        // Carve left and right lanes around it
        CarveLane(PlayerLayoutAnchor, new Vector2I(center.X - 1, center.Y - 1), 1);
        CarveLane(new Vector2I(center.X - 1, center.Y - 1), EnemyLayoutAnchor, 1);

        CarveLane(PlayerLayoutAnchor, new Vector2I(center.X + 1, center.Y + 1), 1);
        CarveLane(new Vector2I(center.X + 1, center.Y + 1), EnemyLayoutAnchor, 1);

        // Add some height on the band
        PaintHeightRidge(center, dir, 4, 2);
    }

    private void GenerateRingCourtyardLayout()
    {
        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);

        // Central raised courtyard
        PaintFilledRadius(center, 1, tile =>
        {
            tile.TerrainType = TileTerrainType.Grass;
            tile.Height = Math.Max(tile.Height, 1);
            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.MoveCost = 1;
        });

        // Outer ring with broken walls
        PaintRingFeature(center, 2, tile =>
        {
            tile.TerrainType = TileTerrainType.Stone;
            tile.Height = Math.Max(tile.Height, 2);

            if (_rng.Randf() < 0.65f)
            {
                tile.IsBlocked = true;
                tile.IsWalkable = false;
                tile.BlocksLineOfSight = true;
                tile.ObstacleKind = "rock";
            }
        }, 0.85f);

        // Ensure entrances
        CarveLane(PlayerLayoutAnchor, center, 1);
        CarveLane(EnemyLayoutAnchor, center, 1);
    }

    private void ApplyDensityPreset()
    {
        if (DensityControlMode != DensityMode.Preset)
            return;

        switch (DensityPreset)
        {
            case MapDensityPreset.Sparse:
                TerrainDensity = 0.25f;
                TerrainRoughness = 0.25f;
                ObstacleDensity = 0.2f;
                break;

            case MapDensityPreset.Standard:
                TerrainDensity = 0.5f;
                TerrainRoughness = 0.5f;
                ObstacleDensity = 0.4f;
                break;

            case MapDensityPreset.Dense:
                TerrainDensity = 0.75f;
                TerrainRoughness = 0.6f;
                ObstacleDensity = 0.65f;
                break;

            case MapDensityPreset.Wild:
                TerrainDensity = 0.9f;
                TerrainRoughness = 0.9f;
                ObstacleDensity = 0.75f;
                break;
        }
    }

    private void MakeLava(TileData tile)
    {
        ClearTileObstacleState(tile);
        tile.TerrainType = TileTerrainType.Lava;
        tile.IsWalkable = true;
        tile.IsBlocked = false;
        tile.MoveCost = 2;
        tile.IsHazardous = true;
        tile.ElementType = TileElementType.Fire;
        tile.ElementStrength = 1.0f;
    }

    private void MakeArcaneGround(TileData tile)
    {
        ClearTileObstacleState(tile);
        tile.TerrainType = TileTerrainType.Arcane;
        tile.IsWalkable = true;
        tile.IsBlocked = false;
        tile.MoveCost = 1;
        tile.ElementType = TileElementType.Arcane;
        tile.ElementStrength = 1.0f;
    }

    private void MakeIce(TileData tile)
    {
        ClearTileObstacleState(tile);
        tile.TerrainType = TileTerrainType.Ice;
        tile.IsWalkable = true;
        tile.IsBlocked = false;
        tile.MoveCost = 1;
        tile.ElementType = TileElementType.Frost;
        tile.ElementStrength = 1.0f;
    }

    private void MakeRockObstacle(TileData tile)
    {
        tile.IsBlocked = true;
        tile.IsWalkable = false;
        tile.BlocksLineOfSight = true;
        tile.ObstacleKind = "rock";
    }

    private void MakeCrystalObstacle(TileData tile)
    {
        tile.IsBlocked = true;
        tile.IsWalkable = false;
        tile.BlocksLineOfSight = true;
        tile.ObstacleKind = "crystal";
    }
}
