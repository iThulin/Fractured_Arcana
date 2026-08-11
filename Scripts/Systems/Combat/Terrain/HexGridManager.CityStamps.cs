using Godot;
using System;

// ============================================================
// HexGridManager.CityStamps.cs  (partial of HexGridManager)
//
// Purpose:        The `building_stamp` recipe op — paints a building
//                 shell footprint for city siege battlemaps
//                 (CityBattlemapCompiler output). Implements the tile
//                 semantics LOCKED in campus_siege_and_defense_v1_1
//                 §4a exactly:
//                   IsBlocked = true          (gates entry: CanEnter)
//                   BlocksLineOfSight = true  (solid to targeting)
//                   ObstacleKind = "building:" + id  (identity carrier
//                     for destruction / interiors / bespoke visuals)
//                   IsWalkable = TRUE, explicitly — restored even if a
//                     wall band crossed this footprint first. This is
//                     the interiors-forward-compat rule: an enterable
//                     building later just stops setting IsBlocked;
//                     nothing else in the terrain model moves.
//                 Do NOT reroute this through PaintObstacleBand /
//                 RecipeTileApplier — both set IsWalkable = false.
// Layer:          System (generation)
// Collaborators:  HexGridManager.Recipes (dispatch), CityBattlemapCompiler
//                 (emits the ops), docs/city_battlemap_compiler_spec_v1_1.md §4.3
// ============================================================

public partial class HexGridManager : Node3D
{
    /// <summary>Paints one building shell: a filled hex disk of
    /// <paramref name="radius"/> around <paramref name="center"/>. Reserved
    /// (spawn-zone) tiles are skipped like every other paint — the compiler
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
                tile.IsWalkable = true;   // docx §4a — see header; deliberate

                if (stampHeight > 0)
                    tile.Height = Math.Max(tile.Height, stampHeight);
            }
        }
    }

    /// <summary>Placeholder visuals for city obstacle kinds until real models
    /// land (art-pass swap point). Hex prisms: grey for "wall", a deterministic
    /// per-building tint for "building:&lt;id&gt;" — deterministic by summing
    /// chars, NOT string.GetHashCode (randomized per process in .NET, would
    /// repaint the city a new colour every launch).</summary>
    private void SpawnCityObstaclePlaceholder(TileData tile)
    {
        bool isWall = tile.ObstacleKind == "wall";
        float height = isWall ? 1.7f : 2.6f;

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
