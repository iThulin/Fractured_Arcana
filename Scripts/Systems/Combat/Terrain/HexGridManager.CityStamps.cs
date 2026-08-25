using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.CityStamps.cs  (partial of HexGridManager)
//
// Purpose:        The `building_stamp` recipe op. Paints a building
//                 shell footprint for city siege battlemaps
//                 (CityBattlemapCompiler output). Implements the tile
//                 semantics LOCKED in campus_siege_and_defense_v1_1
//                 §4a exactly:
//                   IsBlocked = true          (gates entry: CanEnter)
//                   BlocksLineOfSight = true  (solid to targeting)
//                   ObstacleKind = "building:" + id  (identity carrier
//                     for destruction / interiors / bespoke visuals)
//                   IsWalkable = TRUE, explicitly. It is restored even
//                     if a wall band crossed this footprint first. This is
//                     the interiors-forward-compat rule: an enterable
//                     building later just stops setting IsBlocked;
//                     nothing else in the terrain model moves.
//                 Do NOT reroute this through PaintObstacleBand /
//                 RecipeTileApplier. Both set IsWalkable = false.
// Layer:          System (generation)
// Collaborators:  HexGridManager.Recipes (dispatch), CityBattlemapCompiler
//                 (emits the ops), docs/city_battlemap_compiler_spec_v1_1.md §4.3
// ============================================================

public partial class HexGridManager : Node3D
{
    /// <summary>The active recipe's siege spec, or null on a non-siege map.
    /// (CombatManager.SiegeDoors reads Defending + GateGap.)</summary>
    public SiegeSpec ActiveSiege => _activeRecipe?.Siege;

    /// <summary>Public face of the private cliff rule (StepAllowed) for
    /// CombatManager-side spawn floods. Zones and wave arrivals must not
    /// leap onto ramparts a unit could never walk to.</summary>
    public bool StepLegal(Vector2I from, Vector2I to) => StepAllowed(from, to);

    /// <summary>The active siege recipe's gate-gap tiles, or empty when this
    /// map is not a compiled city siege. Consumed by the hold_zone "gate"
    /// zone anchor (CombatManager.Objectives).</summary>
    public IReadOnlyList<Vector2I> SiegeGateGap =>
        _activeRecipe?.Siege?.GateGap ?? (IReadOnlyList<Vector2I>)System.Array.Empty<Vector2I>();

    /// <summary>Compiler-computed hold_zone tiles (door + inside pocket), or
    /// empty on a non-siege map. Preferred over the runtime BFS by
    /// CombatManager.Objectives, because the compiler knows inside from outside.</summary>
    public IReadOnlyList<Vector2I> SiegeObjectiveZone =>
        _activeRecipe?.Siege?.ObjectiveZone ?? (IReadOnlyList<Vector2I>)System.Array.Empty<Vector2I>();

    /// <summary>Paints one building shell: a filled hex disk of
    /// <paramref name="radius"/> around <paramref name="center"/>. Reserved
    /// (spawn-zone) tiles are skipped like every other paint. The compiler
    /// places anchors outside stamps, so a hole here means a compiler bug
    /// upstream, and a playable hole beats an unusable spawn.</summary>
    private void PaintBuildingStamp(Vector2I center, int radius, string buildingId, int stampHeight)
    {
        for (int q = -radius; q <= radius; q++)
        {
            int rMin = Math.Max(-radius, -q - radius);
            int rMax = Math.Min(radius, -q + radius);
            for (int r = rMin; r <= rMax; r++)
            {
                var coord = new Vector2I(center.X + q, center.Y + r);
                if (!Tiles.TryGetValue(coord, out var tile))
                    continue;
                if (IsReserved(coord))
                    continue;

                tile.IsBlocked = true;
                tile.BlocksLineOfSight = true;
                tile.ObstacleKind = "building:" + buildingId;
                tile.IsWalkable = true;   // docx §4a (see header); deliberate

                if (stampHeight > 0)
                    tile.Height = Math.Max(tile.Height, stampHeight);
            }
        }
    }

    /// <summary>The city continuing past the arena edge: decorative prisms for
    /// the backdrop wall + off-map building masses, at AxialToWorld positions
    /// beyond the playable tiles. No TileData, no collision, no gameplay:
    /// pure vista dressing. Tagged generated_obstacle so the standard cleanup
    /// frees it with everything else.</summary>
    private void SpawnSiegeBackdrop()
    {
        var siege = _activeRecipe?.Siege;
        if (siege == null)
            return;

        Node parent = ObstacleParent ?? (Node)this;

        foreach (var t in siege.BackdropWall)
        {
            var mesh = new CylinderMesh
            {
                TopRadius = HexRadius * 0.98f,
                BottomRadius = HexRadius * 0.98f,
                Height = 6.5f,   // deep base: intersects undulating vista ground
                RadialSegments = 6,
            };
            mesh.Material = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.42f, 0.41f, 0.39f),
                Roughness = 1f,
            };
            var prism = new MeshInstance3D
            {
                Mesh = mesh,
                RotationDegrees = new Vector3(0f, 30f, 0f),
            };
            parent.AddChild(prism);
            prism.GlobalPosition = AxialToWorld(t) + new Vector3(0f, 1.1f, 0f);
            prism.AddToGroup("generated_obstacle");
        }

        foreach (var st in siege.BackdropStamps)
        {
            int acc = 0;
            foreach (char c in st.Id)
                acc = (acc * 31 + c) & 0xFFFF;
            var color = Color.FromHsv((acc % 360) / 360f, 0.30f, 0.55f);

            float footprint = HexRadius * (1.0f + st.Radius * 1.45f);
            var mesh = new CylinderMesh
            {
                TopRadius = footprint * 0.85f,
                BottomRadius = footprint,
                Height = 4.6f + st.Radius * 0.5f,   // masses rise above the wall line
                RadialSegments = 6,
            };
            mesh.Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 1f };
            var mass = new MeshInstance3D
            {
                Mesh = mesh,
                RotationDegrees = new Vector3(0f, 30f, 0f),
            };
            parent.AddChild(mass);
            mass.GlobalPosition = AxialToWorld(st.At) + new Vector3(0f, 0.9f, 0f);
            mass.AddToGroup("generated_obstacle");
        }

        if (siege.BackdropWall.Count > 0 || siege.BackdropStamps.Count > 0)
            GD.Print($"[SiegeBackdrop] {siege.BackdropWall.Count} wall tile(s), " +
                     $"{siege.BackdropStamps.Count} building mass(es) beyond the rim.");
    }

    /// <summary>Placeholder visuals for city obstacle kinds until real models
    /// land (art-pass swap point). Hex prisms: grey for "wall", a deterministic
    /// per-building tint for "building:&lt;id&gt;", made deterministic by summing
    /// chars, NOT string.GetHashCode (randomized per process in .NET, would
    /// repaint the city a new colour every launch).</summary>
    private void SpawnCityObstaclePlaceholder(TileData tile)
    {
        bool isWall = tile.ObstacleKind == "wall";
        // 5 ft/hex scale: a curtain wall is 20-30 ft (2026-08-11 ruling, after 1.7
        // read as garden fencing once the full city was visible). Shells top the wall.
        float height = isWall ? 3.2f : 3.8f;

        Color color;
        if (isWall)
        {
            color = new Color(0.45f, 0.44f, 0.42f);
        }
        else
        {
            string id = tile.ObstacleKind.Substring("building:".Length);
            int acc = 0;
            foreach (char c in id)
                acc = (acc * 31 + c) & 0xFFFF;
            color = Color.FromHsv((acc % 360) / 360f, 0.35f, 0.62f);
        }

        var mesh = new CylinderMesh
        {
            TopRadius = HexRadius * (isWall ? 0.98f : 0.88f),
            BottomRadius = HexRadius * (isWall ? 0.98f : 0.92f),
            Height = height,
            RadialSegments = 6,
        };
        mesh.Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 1f };

        var prism = new MeshInstance3D
        {
            Mesh = mesh,
            RotationDegrees = new Vector3(0f, 30f, 0f),   // align to flat-top layout
        };

        if (ObstacleParent != null)
        {
            ObstacleParent.AddChild(prism);
            prism.GlobalPosition = tile.TileView.GlobalPosition + new Vector3(0f, height * 0.5f, 0f);
        }
        else
        {
            AddChild(prism);
            prism.Position = tile.TileView.Position + new Vector3(0f, height * 0.5f, 0f);
        }

        prism.AddToGroup("generated_obstacle");
    }
}
