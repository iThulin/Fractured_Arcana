using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Obstruction.cs  (partial of HexGridManager)
//
// Purpose:        Combat overhaul stage C8: an obstruction laid over
//                 whatever map was rolled, so the ground itself gets in
//                 the way on any biome and any recipe. One kind so far:
//
//                   barrier   a wall across the whole board, between the
//                             two sides, with a two-tile gate and a Gate
//                             Winch beside it
//
//                 The wall is built from the biome's own obstacle kinds
//                 (a role token resolves through the recipe's palette),
//                 so it is a drystone wall on the heath and an ice ridge
//                 in the snow with no authoring per map.
// Layer:          Systems / Combat / Terrain
// Collaborators:  HexGridManager.cs (GenerateMap calls ApplyObstruction
//                   after the accent features and before connectivity),
//                 HexGridManager.Generation.cs (ApplyObstacle),
//                 CombatManager.Obstructions.cs (sets the knobs, announces),
//                 CombatManager.WorkObjects.cs (the winch reads the gate tiles)
// See:            docs/combat_c8_obstructions_v1.md
// ============================================================

public partial class HexGridManager
{
    /// <summary>"barrier" forces one, "none" forbids one, empty rolls
    /// <see cref="ObstructionChance"/>. Set by CombatManager before GenerateMap.</summary>
    public string ObstructionKind = "none";

    /// <summary>Chance of a barrier when <see cref="ObstructionKind"/> is empty.</summary>
    public float ObstructionChance = 0f;

    /// <summary>Obstacle kind the barrier is built from. "palisade" stands taller
    /// than a unit, so it blocks sight as well as movement, and it can be broken
    /// (10 health a tile). Was the role "low" until 2026-11-25: a waist-high wall
    /// did not read as something that stops you.</summary>
    public string ObstructionWall = "palisade";

    /// <summary>How many tiles past each edge of the board the wall is drawn, so it
    /// does not stop dead at the board's outline. Looks only: no rules.</summary>
    [Export] public int BarrierRunOffTiles = 10;

    private Vector2I _barrierCentre;
    private Vector2I _barrierDir;

    /// <summary>The gate's tiles. Left open by the generator. The winch closes
    /// and opens them (CombatManager.WorkObjects.cs).</summary>
    public readonly List<Vector2I> ObstructionGateTiles = new();

    /// <summary>What was actually built this map: "barrier" or empty.</summary>
    public string ActiveObstruction { get; private set; } = "";

    /// <summary>From GenerateMap, after the recipe's accent features. Before the
    /// connectivity pass, which walks through the open gate and so leaves the wall alone.</summary>
    private void ApplyObstruction()
    {
        ObstructionGateTiles.Clear();
        ActiveObstruction = "";
        if (ObstructionKind == "none" || _activeRecipe == null)
            return;
        if (_activeRecipe.Siege != null || InteriorActive || _activeRecipe.HardGround)
            return;   // authored layouts keep their own walls

        string kind = ObstructionKind;
        if (string.IsNullOrEmpty(kind))
        {
            if (ObstructionChance <= 0f)
                return;
            kind = _rng.Randf() < ObstructionChance ? "barrier" : "none";
        }
        if (kind == "barrier")
            PaintBarrier();
    }

    /// <summary>Draws the wall on past both edges of the board, over the out-of-bounds
    /// ground, so it reads as a wall that crosses the country and not one cut to the
    /// board. Called by CombatManager once the map and its surround exist. The pieces
    /// are in the generated_obstacle group and go when the map is cleared.</summary>
    public void BuildBarrierRunOff()
    {
        if (ActiveObstruction != "barrier" || BarrierRunOffTiles <= 0)
            return;
        var spec = ObstacleCatalog.GetOrFallback(ResolveObstacleKind(ObstructionWall));
        if (spec == null || spec.Silhouette != ObstacleSilhouette.Slab)
            return;   // only a slab continues as a wall
        float height = spec.Height > 0f ? spec.Height : DefaultPlaceholderHeight(spec, spec.IsLow);

        int axis = -1;
        for (int i = 0; i < 6; i++)
            if (HexDirs[i] == _barrierDir)
                axis = i % 3;
        if (axis < 0)
            return;
        float yaw = AxisYawDegrees(axis);
        float neighbourGap = HexRadius * Mathf.Sqrt(3f);
        var material = new StandardMaterial3D { AlbedoColor = spec.Color, Roughness = 1f };
        var mesh = new BoxMesh
        {
            Size = new Vector3(neighbourGap * 1.04f, height, HexRadius * 0.45f),
            Material = material,
        };

        int built = 0;
        for (int sign = 1; sign >= -1; sign -= 2)
        {
            // Walk to the last board tile on this side, then keep going.
            Vector2I c = _barrierCentre;
            for (int i = 0; i < 64 && Tiles.ContainsKey(c + _barrierDir * sign); i++)
                c += _barrierDir * sign;

            for (int i = 1; i <= BarrierRunOffTiles; i++)
            {
                Vector2I at = c + _barrierDir * (i * sign);
                if (!VistaTiles.TryGetValue(at, out var vt) || vt?.TileView == null)
                    break;   // past the out-of-bounds ground
                if (vt.TerrainType == TileTerrainType.Water)
                    continue;   // a wall does not cross open water
                Vector3 world = vt.TileView.GlobalPosition;
                float y = SampleGrassSurfaceY(vt, world.X, world.Z);
                var slab = new MeshInstance3D
                {
                    Mesh = mesh,
                    RotationDegrees = new Vector3(0f, yaw, 0f),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                Node parent = ObstacleParent != null ? ObstacleParent : this;
                parent.AddChild(slab);
                slab.GlobalPosition = new Vector3(world.X, y + height * 0.5f, world.Z);
                slab.AddToGroup("generated_obstacle");
                built++;
            }
        }
        GD.Print($"[Board] barrier run-off: {built} piece(s) past the board's edges.");
    }

    /// <summary>Walking distance from <paramref name="from"/> to every tile that can
    /// be walked to from it. Obstacles and field objects stop it. Units do not: they
    /// move. The start tile is in the result at 0 whatever stands on it, so it can
    /// be an occupied tile or the blocked tile a mover is heading for.</summary>
    public Dictionary<Vector2I, int> WalkField(Vector2I from)
    {
        var field = new Dictionary<Vector2I, int>();
        if (!Tiles.ContainsKey(from))
            return field;
        var queue = new Queue<Vector2I>();
        field[from] = 0;
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int next = field[current] + 1;
            foreach (var n in GetNeighbors(current))
            {
                if (field.ContainsKey(n) || !Tiles.TryGetValue(n, out var tile))
                    continue;
                if (!tile.IsWalkable || tile.IsBlocked)
                    continue;
                if (tile.Occupant != null && tile.Occupant.IsMapObject)
                    continue;
                if (!StepAllowed(current, n))
                    continue;
                field[n] = next;
                queue.Enqueue(n);
            }
        }
        return field;
    }

    private void PaintBarrier()
    {
        Vector2I dir = FlankDirection();
        Vector2I centre = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);
        if (!Tiles.ContainsKey(centre))
            return;
        _barrierCentre = centre;
        _barrierDir = dir;

        var gate = new HashSet<Vector2I> { centre };
        if (Tiles.ContainsKey(centre + dir))
            gate.Add(centre + dir);

        int painted = 0;
        for (int sign = 1; sign >= -1; sign -= 2)
        {
            for (int i = sign > 0 ? 0 : 1; i < 64; i++)
            {
                Vector2I c = centre + dir * (i * sign);
                if (!Tiles.TryGetValue(c, out var tile))
                    break;
                if (gate.Contains(c))
                    continue;
                if (IsReserved(c) || tile.IsOccupied || tile.TerrainType == TileTerrainType.Water)
                    continue;   // a spawn tile or open water stays a way through
                ApplyObstacle(tile, ObstructionWall);
                painted++;
            }
        }
        if (painted == 0)
            return;

        foreach (var g in gate)
            ObstructionGateTiles.Add(g);

        // The winch: against the wall on the party's side, two tiles along from the
        // gateway, so it never stands in the opening or on the way into it.
        Vector2I toParty = Vector2I.Zero;
        int best = int.MaxValue;
        foreach (var h in HexDirs)
        {
            if (h == dir || h == -dir)
                continue;
            int d = Distance(centre + h, PlayerLayoutAnchor);
            if (d < best)
            {
                best = d;
                toParty = h;
            }
        }
        Vector2I winchAt = centre;
        if (toParty != Vector2I.Zero)
        {
            // Two along on one side of the gateway, then the other, then three along.
            var tries = new[]
            {
                centre - dir * 2 + toParty,
                centre + dir * 3 + toParty,
                centre - dir * 3 + toParty,
                centre + dir * 4 + toParty,
            };
            foreach (var t in tries)
            {
                if (!Tiles.TryGetValue(t, out var wt) || !wt.IsWalkable || wt.IsBlocked || wt.IsOccupied || IsReserved(t))
                    continue;
                if (wt.TerrainType == TileTerrainType.Water)
                    continue;
                bool besideGateway = false;
                foreach (var g in gate)
                    if (Distance(t, g) <= 1)
                        besideGateway = true;
                if (besideGateway)
                    continue;
                winchAt = t;
                break;
            }
        }
        if (winchAt != centre)
            PendingMapObjects.Add((winchAt, "gate_winch", 1));

        // C9: a Signal Fire on the enemy's side of the wall, out on a flank, so an
        // enemy has to run for it and the party can see it over the wall.
        Vector2I fireAt = centre;
        int farthest = -1;
        foreach (var td in Tiles.Values)
        {
            if (td == null || !td.IsWalkable || td.IsBlocked || td.IsOccupied || IsReserved(td.Axial))
                continue;
            if (td.TerrainType == TileTerrainType.Water)
                continue;
            int toEnemy = Distance(td.Axial, EnemyLayoutAnchor);
            if (toEnemy >= Distance(td.Axial, PlayerLayoutAnchor))
                continue;   // the party's side
            int toCentre = Distance(td.Axial, centre);
            if (toCentre < 4 || toCentre > 6)
                continue;
            if (toEnemy > farthest)
            {
                farthest = toEnemy;
                fireAt = td.Axial;
            }
        }
        if (fireAt != centre)
            PendingMapObjects.Add((fireAt, "signal_fire", 1));

        ActiveObstruction = "barrier";
        GD.Print($"[Board] barrier: {painted} wall tile(s) of '{ObstructionWall}' through {centre}, " +
                 $"gate of {gate.Count}, winch at {(winchAt != centre ? winchAt.ToString() : "none")}.");
    }
}
