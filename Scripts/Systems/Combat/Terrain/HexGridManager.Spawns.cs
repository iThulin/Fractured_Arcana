using Godot;
using System;
using System.Collections.Generic;

// HexGridManager.Spawns.cs: spawn anchors/zones/slots, reservation system, connectivity carve
// Partial of HexGridManager. Split out for navigability; behaviour-neutral.
public partial class HexGridManager
{
    // ── battlefield_variety_spec_v1 §3: deployment variants ─────────────────
    /// <summary>Debug hook: when set and the active recipe lists a deployment with this
    /// id, generation uses it instead of rolling one. Empty = weighted roll.</summary>
    public string ForcedDeploymentId = "";

    /// <summary>Rotation hint: the previous fight's deployment id. Excluded from the
    /// roll when the recipe offers more than one variant. Empty = no exclusion.</summary>
    public string AvoidDeploymentId = "";

    /// <summary>Set by CombatManager when the fight carries a zone objective sited on
    /// the midpoint (hold_zone). Deployments that seed the ENEMY at the midpoint are
    /// then excluded from the roll: enemies starting inside the zone would breach it
    /// every round and lose the fight by round 3 without a card being played.</summary>
    public bool KeepMidpointClearOfEnemies = false;

    /// <summary>The deployment variant this generation rolled. Null on recipes with no
    /// `deployments` block, on siege recipes, and on the enum path: the pre-spec plan.</summary>
    public DeploymentSpec ActiveDeployment { get; private set; }

    /// <summary>Hex direction index (0–5) the layout frame is built on: player→enemy.
    /// Backs the axis:N / flank:N tokens and the relative `dir` tokens. Without a
    /// deployment it is derived from the anchors exactly as AxisShift always did.</summary>
    public int LayoutAxis => _layoutAxis;
    private int _layoutAxis;

    /// <summary>Where the spawn zones are seeded. Default = the layout anchors; a
    /// deployment's player_at / enemy_at tokens move (and multiply) them.</summary>
    private readonly List<Vector2I> _playerSpawnTargets = new();
    private readonly List<Vector2I> _enemySpawnTargets = new();

    /// <summary>Rolls (or forces) the recipe's deployment and the layout axis. Runs before
    /// DetermineLayoutAnchors. Draws from _rng only when a deployments block exists, so a
    /// recipe without one generates byte-for-byte as before.</summary>
    private void ResolveDeployment()
    {
        ActiveDeployment = null;
        _layoutAxis = 0;

        var list = _activeRecipe?.Deployments;
        if (list == null || list.Count == 0)
            return;
        if (_activeRecipe.Siege != null)
        {
            GD.Print("[Deploy] siege recipe: deployments block ignored (compiler-authored anchors win).");
            return;
        }

        DeploymentSpec chosen = null;
        if (!string.IsNullOrEmpty(ForcedDeploymentId))
        {
            chosen = list.Find(d => d.Id == ForcedDeploymentId);
            if (chosen == null)
                GD.PushWarning($"[Deploy] forced deployment '{ForcedDeploymentId}' not on recipe '{_activeRecipe.Id}'; rolling instead.");
        }

        if (chosen == null)
        {
            var pool = list;
            if (KeepMidpointClearOfEnemies)
            {
                var clear = pool.FindAll(d => !d.EnemyAt.Exists(t => t == "midpoint" || t == "center"));
                if (clear.Count > 0)
                    pool = clear;
                else
                    GD.PushWarning($"[Deploy] every deployment on '{_activeRecipe.Id}' seeds enemies at the midpoint; hold_zone will start breached.");
            }
            if (!string.IsNullOrEmpty(AvoidDeploymentId) && pool.Count > 1)
            {
                var fresh = pool.FindAll(d => d.Id != AvoidDeploymentId);
                if (fresh.Count > 0)
                    pool = fresh;
            }
            float total = 0f;
            foreach (var d in pool)
                total += Mathf.Max(0f, d.Weight);
            float roll = _rng.Randf() * total;
            foreach (var d in pool)
            {
                roll -= Mathf.Max(0f, d.Weight);
                if (roll <= 0f)
                {
                    chosen = d;
                    break;
                }
            }
            chosen ??= pool[pool.Count - 1];
        }

        ActiveDeployment = chosen;
        _layoutAxis = chosen.AnyAxis
            ? _rng.RandiRange(0, 5)
            : chosen.Axes[_rng.RandiRange(0, chosen.Axes.Count - 1)];

        GD.Print($"[Deploy] recipe '{_activeRecipe.Id}': deployment '{chosen.Id}', axis {_layoutAxis}, " +
                 $"layout {chosen.PlayerFraction:0.00}/{chosen.EnemyFraction:0.00}, " +
                 $"player_at [{string.Join(",", chosen.PlayerAt)}], enemy_at [{string.Join(",", chosen.EnemyAt)}].");
    }

    private void GenerateSpawnPlan()
    {
        // Spawn targets: the layout anchors unless the deployment moves them.
        // Tokens resolve against the layout frame (midpoint, axis:N, flank:N...),
        // so "enemy_at": "midpoint" seeds the enemy inside whatever the skeleton
        // built at the midpoint, with the skeleton itself unchanged.
        _playerSpawnTargets.Clear();
        _enemySpawnTargets.Clear();
        if (ActiveDeployment != null)
        {
            foreach (var tok in ActiveDeployment.PlayerAt)
                _playerSpawnTargets.Add(ResolveCoord(Variant.From(tok)));
            foreach (var tok in ActiveDeployment.EnemyAt)
                _enemySpawnTargets.Add(ResolveCoord(Variant.From(tok)));
        }
        if (_playerSpawnTargets.Count == 0)
            _playerSpawnTargets.Add(PlayerLayoutAnchor);
        if (_enemySpawnTargets.Count == 0)
            _enemySpawnTargets.Add(EnemyLayoutAnchor);

        bool ok = BuildSpawnPlanFor(_playerSpawnTargets, _enemySpawnTargets);

        // Guarantee (spec §3.4): a moved or split deployment that cannot seat its
        // headcount, or whose targets collapsed onto one anchor, falls back to the
        // plain line on the SAME skeleton. The layout frame (and so every feature)
        // is untouched; only the zones move. The line plan is the shipped path and
        // cannot fail worse than it did before this spec.
        if (!ok && ActiveDeployment != null && ActiveDeployment.MovesTargets)
        {
            GD.PushWarning($"[Deploy] guarantee failed for deployment '{ActiveDeployment.Id}'; falling back to the layout anchors.");
            _playerSpawnTargets.Clear();
            _enemySpawnTargets.Clear();
            _playerSpawnTargets.Add(PlayerLayoutAnchor);
            _enemySpawnTargets.Add(EnemyLayoutAnchor);
            BuildSpawnPlanFor(_playerSpawnTargets, _enemySpawnTargets);
        }

        ReserveSpawnZones();
        BuildSpawnSlotsFromZones();

        GD.Print($"[SpawnPlan] Total spawn slots: {SpawnSlots.Count}");
    }

    /// <summary>Builds one zone per target on each side, splitting the side's slot
    /// count evenly (ceil) across its targets. Returns false when any zone came up
    /// short or two anchors coincided, so the caller can fall back.</summary>
    private bool BuildSpawnPlanFor(List<Vector2I> playerTargets, List<Vector2I> enemyTargets)
    {
        SpawnZones.Clear();
        bool ok = true;
        var anchorsUsed = new HashSet<Vector2I>();

        int playerPer = (PlayerSpawnCount + playerTargets.Count - 1) / playerTargets.Count;
        int enemyPer = (EnemySpawnCount + enemyTargets.Count - 1) / enemyTargets.Count;

        foreach (var target in playerTargets)
        {
            Vector2I anchor = FindSpawnAnchor(SpawnSide.Player, target, enemyTargets, playerPer);
            if (!anchorsUsed.Add(anchor))
                ok = false;
            var zone = BuildSpawnZone(anchor, SpawnSide.Player, 0, playerPer);
            if (zone.Tiles.Count < playerPer)
                ok = false;
            GD.Print($"[SpawnPlan] Player anchor: {anchor} (target {target}), zone tiles: {zone.Tiles.Count}/{playerPer}");
            SpawnZones.Add(zone);
        }

        foreach (var target in enemyTargets)
        {
            Vector2I anchor = FindSpawnAnchor(SpawnSide.Enemy, target, playerTargets, enemyPer);
            if (!anchorsUsed.Add(anchor))
                ok = false;
            var zone = BuildSpawnZone(anchor, SpawnSide.Enemy, 1, enemyPer);
            if (zone.Tiles.Count < enemyPer)
                ok = false;
            GD.Print($"[SpawnPlan] Enemy anchor: {anchor} (target {target}), zone tiles: {zone.Tiles.Count}/{enemyPer}");
            SpawnZones.Add(zone);
        }

        return ok;
    }

    private void DetermineLayoutAnchors()
    {
        if (ActiveDeployment != null)
        {
            DetermineLayoutAnchorsOnAxis(ActiveDeployment);
            return;
        }

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

        // Siege recipes carry AUTHORED anchors (attacker at the approach,
        // defender behind the wall). Those override the X-extent derivation when
        // present and on-map. Absent/off-map coords keep the default.
        if (_activeRecipe?.Siege is SiegeSpec sg)
        {
            if (sg.PlayerAnchor is Vector2I pa && Tiles.ContainsKey(pa))
                PlayerLayoutAnchor = pa;
            if (sg.EnemyAnchor is Vector2I ea && Tiles.ContainsKey(ea))
                EnemyLayoutAnchor = ea;
        }

        // The axis the coord tokens walk: derived from the anchors, which is what
        // AxisShift / FlankDirection always computed inline.
        _layoutAxis = HexDirection.Pick(PlayerLayoutAnchor, EnemyLayoutAnchor, 6);
    }

    /// <summary>Deployment path: project every tile onto the world direction of the
    /// rolled hex axis and place the anchors at the deployment's fractions of that
    /// span, on the line through the centroid. NOTE the anchor line lies on a TRUE
    /// hex direction (flat-top layout: 30° off world X for axis 0), not on the
    /// X-extent line the legacy path uses, so axis:N / flank:N tokens and "dir":
    /// "axis" features walk exactly along and across the anchor line, and rotating
    /// the axis by one step rotates the whole skeleton by exactly 60°. Recipes
    /// without a deployments block keep the legacy derivation untouched.</summary>
    private void DetermineLayoutAnchorsOnAxis(DeploymentSpec dep)
    {
        Vector3 dir = AxialToWorld(HexDirs[_layoutAxis]);
        dir.Y = 0f;
        dir = dir.Normalized();

        Vector3 centroid = Vector3.Zero;
        float minX = float.MaxValue, maxX = float.MinValue;
        int n = 0;
        foreach (var c in Tiles.Keys)
        {
            var w = AxialToWorld(c);
            centroid += w;
            minX = Mathf.Min(minX, w.X);
            maxX = Mathf.Max(maxX, w.X);
            n++;
        }

        if (n == 0)
        {
            PlayerLayoutAnchor = Vector2I.Zero;
            EnemyLayoutAnchor = Vector2I.Zero;
            return;
        }

        centroid /= n;
        centroid.Y = 0f;
        _layoutMinX = minX;
        _layoutMaxX = maxX;

        float minP = float.MaxValue, maxP = float.MinValue;
        foreach (var c in Tiles.Keys)
        {
            var w = AxialToWorld(c);
            w.Y = 0f;
            float p = (w - centroid).Dot(dir);
            minP = Mathf.Min(minP, p);
            maxP = Mathf.Max(maxP, p);
        }
        float span = maxP - minP;

        PlayerLayoutAnchor = NearestTileTo(centroid + dir * (minP + span * dep.PlayerFraction));
        EnemyLayoutAnchor = NearestTileTo(centroid + dir * (minP + span * dep.EnemyFraction));
        _centerCoord = NearestTileTo(centroid + dir * ((minP + maxP) * 0.5f));

        if (PlayerLayoutAnchor == EnemyLayoutAnchor)
            GD.PushWarning($"[Deploy] layout anchors coincide at {PlayerLayoutAnchor} on axis {_layoutAxis}; check the recipe's layout fractions.");
    }

    private List<Vector2I> GetSideCandidates(SpawnSide side, Vector2I target, List<Vector2I> opposing)
    {
        var result = new List<Vector2I>();
        Vector2I anchor = target;
        float centerX = (_layoutMinX + _layoutMaxX) * 0.5f;

        // Siege anchors are authored positions, not X-extent extremes: the
        // halves-of-the-map filter is meaningless there (both anchors can sit
        // in the same half). Candidates come from a depth-3 WALKABLE flood
        // instead of raw distance, for two reasons: (a) zones must not swallow
        // wall/building tiles, or EnsureReservedTilesArePlayable would bulldoze
        // holes in the curtain; (b) raw distance leaks THROUGH the wall onto
        // interior tiles. The flood is bounded by the curtain by construction.
        if (_activeRecipe?.Siege != null)
        {
            // The gate-gap tiles are impassable to the ZONE flood even though
            // they are walkable ground: the door spawns there after placement,
            // and a flood that slips through the doorway puts enemies inside
            // the courtyard at round 1 (observed 2026-08-11: an enemy in the
            // hold_zone at spawn, and its occupancy blocked a door panel).
            var doorway = new HashSet<Vector2I>(_activeRecipe.Siege.GateGap);

            var depth = new Dictionary<Vector2I, int> { [anchor] = 0 };
            var queue = new Queue<Vector2I>();
            queue.Enqueue(anchor);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                if (depth[c] >= 3)
                    continue;
                foreach (var n in GetNeighbors(c))
                {
                    if (depth.ContainsKey(n))
                        continue;
                    if (doorway.Contains(n))
                        continue;
                    if (!Tiles.TryGetValue(n, out var t))
                        continue;
                    if (t.IsBlocked || !t.IsWalkable)
                        continue;
                    if (!StepAllowed(c, n))
                        continue;   // cliff rule: candidates must be walkable-to
                    depth[n] = depth[c] + 1;
                    queue.Enqueue(n);
                }
            }
            result.AddRange(depth.Keys);
            return result;
        }

        // Deployment path: the X-half filter is meaningless once a target can sit
        // at the midpoint or on a flank, so a candidate belongs to a target when it
        // is within 3 of it AND no opposing target is nearer. On the plain line
        // this is the half filter with extra steps; on a pincer it keeps the two
        // enemy pockets from bleeding into the player's ground.
        if (ActiveDeployment != null)
        {
            foreach (var coord in Tiles.Keys)
            {
                int dSelf = Distance(coord, anchor);
                if (dSelf > 3)
                    continue;
                bool contested = false;
                foreach (var opp in opposing)
                {
                    if (Distance(coord, opp) <= dSelf)
                    { contested = true; break; }
                }
                if (!contested)
                    result.Add(coord);
            }
            return result;
        }

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
            tile.AuthoredCover = CoverKind.None;
        }
    }

    private void EnsureConnectivity(Vector2I start, Vector2I goal)
    {
        // BFS on raw coords: no unit involved, just check walkability + cliffs
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
        // Local on purpose, because this method runs twice per map and a field
        // would leak the previous carve's height into this one.
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
                tile.AuthoredCover = CoverKind.None;

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
            goalTile.AuthoredCover = CoverKind.None;

            // The carve loop exits before processing the goal, so ramp it too,
            // or the final step can still be an illegal cliff.
            goalTile.Height = Math.Clamp(goalTile.Height,
                prevHeight - CliffHeightThreshold,
                prevHeight + CliffHeightThreshold);
        }
    }

    // Player and Enemy Spawns

    private Vector2I FindSpawnAnchor(SpawnSide side, Vector2I targetAnchor, List<Vector2I> opposing, int requiredSlots)
    {
        if (UseDebugSpawnOverrides)
            return side == SpawnSide.Player ? DebugPlayerAnchor : DebugEnemyAnchor;

        var candidates = GetSideCandidates(side, targetAnchor, opposing);

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

        // Nuclear fallback: scan entire correct half for ANY walkable tile
        GD.PrintErr($"[SpawnPlan] No spawn anchor found for {side}. Using emergency fallback.");
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

        // Siege doorway: the gate door spawns on these tiles AFTER placement,
        // so the zone BFS must neither claim them nor traverse THROUGH them.
        // This BFS is self-contained (it does NOT use GetSideCandidates), and
        // without the guard it pours through the open gap into the courtyard
        // (observed 2026-08-11: enemy spawned ON a gap tile, blocking a door
        // panel and starting inside the hold_zone).
        var doorway = _activeRecipe?.Siege?.GateGap != null
            ? new HashSet<Vector2I>(_activeRecipe.Siege.GateGap)
            : new HashSet<Vector2I>();

        queue.Enqueue(anchor);
        visited.Add(anchor);

        while (queue.Count > 0 && zone.Tiles.Count < requiredSlots)
        {
            var current = queue.Dequeue();

            if (Tiles.TryGetValue(current, out var tile))
            {
                if (tile.IsWalkable && !tile.IsBlocked && !doorway.Contains(current))
                    zone.Tiles.Add(current);
            }

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;

                visited.Add(neighbor);
                if (doorway.Contains(neighbor))
                    continue;   // don't traverse through the doorway either
                if (!StepAllowed(current, neighbor))
                    continue;   // cliff rule: no zoning onto rampart tops
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

        // Deployment variants can seat more than one zone per side (pincer,
        // surround). Every further zone must reach the first player zone too,
        // or a flank pocket walled off by the skeleton spawns enemies that can
        // never engage. The two-zone case above is untouched (no extra draws).
        if (SpawnZones.Count > 2)
        {
            foreach (var z in SpawnZones)
            {
                if (z == playerZone || z == enemyZone)
                    continue;
                EnsureConnectivity(playerZone.Anchor, z.Anchor);
            }
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
