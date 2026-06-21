using Godot;
using System;
using System.Collections.Generic;

// HexGridManager.Spawns.cs — spawn anchors/zones/slots, reservation system, connectivity carve
// Partial of HexGridManager. Split out for navigability; behaviour-neutral.
public partial class HexGridManager
{
    private void GenerateSpawnPlan()
    {
        SpawnZones.Clear();

        Vector2I playerAnchor = FindSpawnAnchor(SpawnSide.Player);
        Vector2I enemyAnchor = FindSpawnAnchor(SpawnSide.Enemy);

        GD.Print($"[SpawnPlan] Player anchor: {playerAnchor}, Enemy anchor: {enemyAnchor}");

        var playerZone = BuildSpawnZone(playerAnchor, SpawnSide.Player, 0, PlayerSpawnCount);
        var enemyZone = BuildSpawnZone(enemyAnchor, SpawnSide.Enemy, 1, EnemySpawnCount);

        GD.Print($"[SpawnPlan] Player zone tiles: {playerZone.Tiles.Count}, Enemy zone tiles: {enemyZone.Tiles.Count}");

        SpawnZones.Add(playerZone);
        SpawnZones.Add(enemyZone);

        ReserveSpawnZones();
        BuildSpawnSlotsFromZones();

        GD.Print($"[SpawnPlan] Total spawn slots: {SpawnSlots.Count}");
    }

    private void DetermineLayoutAnchors()
    {
        // Derive anchors from the actual tile set so any shape works.
        float minX = float.MaxValue, maxX = float.MinValue, sumZ = 0f;
        int n = 0;

        foreach (var c in Tiles.Keys)
        {
            var w = AxialToWorld(c);
            minX = Mathf.Min(minX, w.X);
            maxX = Mathf.Max(maxX, w.X);
            sumZ += w.Z;
            n++;
        }

        if (n == 0)
        {
            PlayerLayoutAnchor = Vector2I.Zero;
            EnemyLayoutAnchor = Vector2I.Zero;
            return;
        }

        _layoutMinX = minX;
        _layoutMaxX = maxX;

        float centerZ = sumZ / n;
        float span = maxX - minX;

        PlayerLayoutAnchor = NearestTileTo(new Vector3(minX + span * 0.12f, 0f, centerZ));
        EnemyLayoutAnchor = NearestTileTo(new Vector3(minX + span * 0.88f, 0f, centerZ));
        _centerCoord = NearestTileTo(new Vector3((minX + maxX) * 0.5f, 0f, centerZ));
    }

    private List<Vector2I> GetSideCandidates(SpawnSide side)
    {
        var result = new List<Vector2I>();
        Vector2I anchor = side == SpawnSide.Player ? PlayerLayoutAnchor : EnemyLayoutAnchor;
        float centerX = (_layoutMinX + _layoutMaxX) * 0.5f;

        foreach (var coord in Tiles.Keys)
        {
            float x = AxialToWorld(coord).X;

            if (side == SpawnSide.Player && x > centerX)
                continue;

            if (side == SpawnSide.Enemy && x < centerX)
                continue;

            if (Distance(coord, anchor) <= 3)
                result.Add(coord);
        }

        return result;
    }

    // Reservation System

    private void ClearReservedTiles()
    {
        ReservedTiles.Clear();
    }

    private void ReserveTile(Vector2I coord)
    {
        if (Tiles.ContainsKey(coord))
            ReservedTiles.Add(coord);
    }

    private void ReserveRadius(Vector2I center, int radius)
    {
        foreach (var coord in Tiles.Keys)
        {
            if (Distance(center, coord) <= radius)
                ReservedTiles.Add(coord);
        }
    }

    private bool IsReserved(Vector2I coord)
    {
        return ReservedTiles.Contains(coord);
    }

    private void EnsureReservedTilesArePlayable()
    {
        foreach (var coord in ReservedTiles)
        {
            if (!Tiles.TryGetValue(coord, out var tile))
                continue;

            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.BlocksLineOfSight = false;
            tile.IsHazardous = false;
            tile.MoveCost = 1;
            tile.ObstacleKind = "";
        }
    }

    private void EnsureConnectivity(Vector2I start, Vector2I goal)
    {
        // BFS on raw coords — no unit involved, just check walkability + cliffs
        var visited = new HashSet<Vector2I> { start };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);
        bool connected = false;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == goal)
            { connected = true; break; }

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;
                if (!Tiles.TryGetValue(neighbor, out var t))
                    continue;
                if (!t.IsWalkable || t.IsBlocked)
                    continue;
                if (!StepAllowed(current, neighbor))
                    continue;
                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        if (connected)
            return;

        GD.Print("No valid path found between spawn points. Carving path...");

        // Ramp state: seed from the start tile so the first clamp is a no-op.
        // Local on purpose — this method runs twice per map and a field would
        // leak the previous carve's height into this one.
        int prevHeight = Tiles.TryGetValue(start, out var startTile) ? startTile.Height : 0;

        Vector2I current2 = start;
        while (current2 != goal)
        {
            if (Tiles.TryGetValue(current2, out var tile))
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

                // Ramp through any cliff: keep each carved tile within the
                // traversable height band of the previous one.
                tile.Height = Math.Clamp(tile.Height,
                    prevHeight - CliffHeightThreshold,
                    prevHeight + CliffHeightThreshold);
                prevHeight = tile.Height;
            }

            int dq = goal.X - current2.X;
            int dr = goal.Y - current2.Y;
            Vector2I step = current2;

            if (Math.Abs(dq) > Math.Abs(dr))
                step = new Vector2I(current2.X + Math.Sign(dq), current2.Y);
            else if (dr != 0)
                step = new Vector2I(current2.X, current2.Y + Math.Sign(dr));

            if (step == current2)
                break;
            current2 = step;
        }

        if (Tiles.TryGetValue(goal, out var goalTile))
        {
            goalTile.ElementStrength = 0f;
            goalTile.IsWalkable = true;
            goalTile.IsBlocked = false;
            goalTile.BlocksLineOfSight = false;
            goalTile.IsHazardous = false;
            goalTile.MoveCost = 1;
            goalTile.ObstacleKind = "";

            // The carve loop exits before processing the goal — ramp it too,
            // or the final step can still be an illegal cliff.
            goalTile.Height = Math.Clamp(goalTile.Height,
                prevHeight - CliffHeightThreshold,
                prevHeight + CliffHeightThreshold);
        }
    }

    // Player and Enemy Spawns

    private Vector2I FindSpawnAnchor(SpawnSide side)
    {
        if (UseDebugSpawnOverrides)
            return side == SpawnSide.Player ? DebugPlayerAnchor : DebugEnemyAnchor;

        Vector2I targetAnchor = side == SpawnSide.Player ? PlayerLayoutAnchor : EnemyLayoutAnchor;
        int requiredSlots = side == SpawnSide.Player ? PlayerSpawnCount : EnemySpawnCount;

        var candidates = GetSideCandidates(side);

        Vector2I bestCoord = Vector2I.Zero;
        int bestScore = int.MinValue;
        bool foundAny = false;

        foreach (var coord in candidates)
        {
            if (!IsValidSpawnTile(coord))
                continue;

            int localCapacity = CountNearbySpawnableTiles(coord, requiredSlots, 3);
            if (localCapacity <= 0)
                continue;

            int distToAnchor = Distance(coord, targetAnchor);

            // higher is better
            int score = 0;

            // prefer being close to layout anchor
            score -= distToAnchor * 10;

            // strongly prefer enough room for whole team
            score += localCapacity * 25;

            // bonus if it fully supports the team
            if (localCapacity >= requiredSlots)
                score += 100;

            if (!foundAny || score > bestScore)
            {
                bestScore = score;
                bestCoord = coord;
                foundAny = true;
            }
        }

        if (foundAny)
            return bestCoord;

        // fallback
        if (candidates.Count > 0)
            return candidates[0];

        // After the existing fallback:
        if (candidates.Count > 0)
            return candidates[0];

        // Nuclear fallback — scan entire correct half for ANY walkable tile
        GD.PrintErr($"[SpawnPlan] No spawn anchor found for {side} — using emergency fallback.");
        foreach (var coord in Tiles.Keys)
        {
            if (side == SpawnSide.Player && coord.X > GridWidth / 2)
                continue;
            if (side == SpawnSide.Enemy && coord.X < GridWidth / 2)
                continue;
            if (IsValidSpawnTile(coord))
                return coord;
        }

        GD.PrintErr($"[SpawnPlan] CRITICAL: No valid spawn tile found for {side}.");
        return Vector2I.Zero;
    }

    private SpawnZone BuildSpawnZone(Vector2I anchor, SpawnSide side, int teamId, int requiredSlots)
    {
        var zone = new SpawnZone
        {
            Anchor = anchor,
            Side = side,
            TeamId = teamId
        };

        var visited = new HashSet<Vector2I>();
        var queue = new Queue<Vector2I>();

        queue.Enqueue(anchor);
        visited.Add(anchor);

        while (queue.Count > 0 && zone.Tiles.Count < requiredSlots)
        {
            var current = queue.Dequeue();

            if (Tiles.TryGetValue(current, out var tile))
            {
                if (tile.IsWalkable && !tile.IsBlocked)
                    zone.Tiles.Add(current);
            }

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;

                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return zone;
    }

    private void BuildSpawnSlotsFromZones()
    {
        SpawnSlots.Clear();

        foreach (var zone in SpawnZones)
        {
            foreach (var coord in zone.Tiles)
            {
                SpawnSlots.Add(new SpawnSlot
                {
                    Coord = coord,
                    Side = zone.Side,
                    TeamId = zone.TeamId,
                    IsOccupied = false
                });
            }
        }
    }

    private void ReserveSpawnZones()
    {
        ClearReservedTiles();

        foreach (var zone in SpawnZones)
        {
            foreach (var coord in zone.Tiles)
                ReservedTiles.Add(coord);
        }
    }

    private void EnsureConnectivityBetweenSpawns()
    {
        if (SpawnZones.Count < 2)
            return;

        var playerZone = SpawnZones.Find(z => z.Side == SpawnSide.Player);
        var enemyZone = SpawnZones.Find(z => z.Side == SpawnSide.Enemy);

        if (playerZone == null || enemyZone == null)
        {
            GD.PrintErr("Missing spawn zones for connectivity.");
            return;
        }

        // Primary connection (anchor → anchor)
        EnsureConnectivity(playerZone.Anchor, enemyZone.Anchor);

        // Optional: reinforce connectivity with extra paths
        if (playerZone.Tiles.Count > 0 && enemyZone.Tiles.Count > 0)
        {
            var p = playerZone.Tiles[_rng.RandiRange(0, playerZone.Tiles.Count - 1)];
            var e = enemyZone.Tiles[_rng.RandiRange(0, enemyZone.Tiles.Count - 1)];

            EnsureConnectivity(p, e);
        }
    }

    private bool IsTileInSpawnSide(Vector2I coord, SpawnSide side)
    {
        foreach (var zone in SpawnZones)
        {
            if (zone.Side == side && zone.Tiles.Contains(coord))
                return true;
        }

        return false;
    }

    private bool IsValidSpawnTile(Vector2I coord)
    {
        if (!Tiles.TryGetValue(coord, out var tile))
            return false;

        if (!tile.IsWalkable || tile.IsBlocked)
            return false;

        if (tile.TerrainType == TileTerrainType.Water)
            return false;

        return true;
    }

    private int CountNearbySpawnableTiles(Vector2I start, int maxCount, int maxDistance = 3)
    {
        if (!IsValidSpawnTile(start))
            return 0;

        var visited = new HashSet<Vector2I>();
        var queue = new Queue<Vector2I>();
        int count = 0;

        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0 && count < maxCount)
        {
            var current = queue.Dequeue();

            if (IsValidSpawnTile(current))
                count++;

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;

                if (Distance(start, neighbor) > maxDistance)
                    continue;

                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return count;
    }

    public SpawnSlot ClaimNextSpawnSlot(SpawnSide side)
    {
        foreach (var slot in SpawnSlots)
        {
            if (slot.Side == side && !slot.IsOccupied)
            {
                slot.IsOccupied = true;
                return slot;
            }
        }

        return null;
    }

    public TileData GetTileAtSpawnSlot(SpawnSlot slot)
    {
        if (slot == null)
            return null;

        return GetTile(slot.Coord);
    }
}
