using Godot;
using System;
using System.Collections.Generic;

// HexGridManager.Visuals.cs — tile visual/material application, blended-mesh rebuild, obstacle + prop spawning
// Partial of HexGridManager. Split out for navigability; behaviour-neutral.
public partial class HexGridManager
{
    /// <summary>Rebuilds the blended mesh for a tile and its six neighbours after a runtime
    /// Height or TerrainType change (raise_terrain, scorch, freeze, etc.). Corner averages
    /// depend on neighbours, so the ring must rebuild too. No-op in legacy mode.</summary>
    public void RebuildTileAndNeighbors(Vector2I coord)
    {
        if (!UseBlendedTerrainMesh)
            return;

        void RebuildOne(Vector2I c)
        {
            if (!Tiles.TryGetValue(c, out var t) || t.TileView == null)
                return;
            t.TileView.SetHeight(t.Height, _lastWorldFloor);
            var mesh = HexMeshBuilder.Build(this, t, _lastWorldFloor, TerrainSolidFactor, TerrainTerraceSteps);
            if (mesh != null)
                t.TileView.SetGeneratedMesh(mesh);
        }

        RebuildOne(coord);
        foreach (var dir in HexDirection.All)
            RebuildOne(coord + dir);
    }

    /// <summary>Lazily builds the shared splat material (shader + packed texture array).
    /// Tiles duplicate it in SetGeneratedMesh so highlight uniforms stay per-tile,
    /// while the Texture2DArray is shared by reference. Null = shader missing → caller
    /// falls back to vertex-colour mode.</summary>
    private ShaderMaterial GetTerrainMaterialTemplate()
    {
        if (_terrainMaterialTemplate != null)
            return _terrainMaterialTemplate;

        var shader = GD.Load<Shader>("res://Assets/Shaders/terrain_splat.gdshader");
        if (shader == null)
        {
            GD.PushWarning("[HexGridManager] terrain_splat.gdshader not found; using vertex-colour terrain.");
            return null;
        }

        var texArray = TerrainTextureLibrary.GetOrBuild(this, TerrainTextureSize);
        if (texArray == null)
            return null;

        var nrmArray = TerrainTextureLibrary.GetOrBuildNormals(this, TerrainTextureSize);
        _terrainMaterialTemplate = TerrainMaterialOverride ?? new ShaderMaterial { Shader = shader };
        _terrainMaterialTemplate.SetShaderParameter("terrain_textures", texArray);
        if (nrmArray != null)
            _terrainMaterialTemplate.SetShaderParameter("terrain_normals", nrmArray);
        _terrainMaterialTemplate.SetShaderParameter("texture_scale", TerrainTextureScale);

        _terrainMaterialTemplate.SetShaderParameter("grid_hex_radius", HexRadius);

        return _terrainMaterialTemplate;
    }

    private void RebuildTerrainMesh(TileData tile)
    {
        if (tile.TileView == null)
            return;

        var template = UseTerrainTextures ? GetTerrainMaterialTemplate() : null;
        bool splat = template != null;

        var mesh = HexMeshBuilder.Build(this, tile, _lastWorldFloor,
            TerrainSolidFactor, TerrainTerraceSteps, splat);
        if (mesh != null)
            tile.TileView.SetGeneratedMesh(mesh, template);
    }

    private void ClearObstacleVisuals()
    {
        Node parent = ObstacleParent ?? this;

        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup("generated_obstacle"))
                child.QueueFree();
        }
    }

    // Tile Visuals

    private void SpawnObstacleVisuals()
    {
        ClearObstacleVisuals();

        foreach (var kvp in Tiles)
        {
            TileData tile = kvp.Value;

            if (string.IsNullOrEmpty(tile.ObstacleKind))
                continue;

            PackedScene scene = null;

            switch (tile.ObstacleKind)
            {
                case "rock":
                    scene = RockObstacleScene;
                    break;
                case "crystal":
                    scene = CrystalObstacleScene;
                    break;
            }

            if (scene == null || tile.TileView == null)
                continue;

            var obstacle = scene.Instantiate<Node3D>();

            if (ObstacleParent != null)
            {
                ObstacleParent.AddChild(obstacle);
                obstacle.GlobalPosition = tile.TileView.GlobalPosition + new Vector3(0f, 0.5f, 0f);
            }
            else
            {
                AddChild(obstacle);
                obstacle.Position = tile.TileView.Position + new Vector3(0f, 0.5f, 0f);
            }

            obstacle.AddToGroup("generated_obstacle");
        }
    }

    public void ApplyVisualToTile(TileData tile)
    {
        if (tile.TileView == null)
            return;

        if (UseBlendedTerrainMesh)
        {
            tile.TileView.RefreshVisualState();
            return;
        }

        Color color = tile.TerrainType switch
        {
            TileTerrainType.Grass => UITheme.CombatTileGrass,
            TileTerrainType.Forest => UITheme.CombatTileForest,
            TileTerrainType.Stone => UITheme.CombatTileStone,
            TileTerrainType.Water => UITheme.CombatTileWater,
            TileTerrainType.Lava => UITheme.CombatTileLava,
            TileTerrainType.Arcane => UITheme.CombatTileArcane,
            TileTerrainType.Ice => UITheme.CombatTileIce,
            _ => Colors.White
        };

        bool inPlayerSpawn = IsTileInSpawnSide(tile.Axial, SpawnSide.Player);
        bool inEnemySpawn = IsTileInSpawnSide(tile.Axial, SpawnSide.Enemy);

        if (inPlayerSpawn)
            color = color.Lerp(UITheme.SpawnTintPlayer, UITheme.SpawnTintStrength);

        if (inEnemySpawn)
            color = color.Lerp(UITheme.SpawnTintEnemy, UITheme.SpawnTintStrength);

        tile.TileView.SetBaseColor(color);
        tile.TileView.SetElement(tile.ElementType);
    }

    private void ApplyTileVisuals()
    {
        foreach (var kvp in Tiles)
        {
            TileData tile = kvp.Value;
            if (tile.TileView == null)
                continue;

            ApplyVisualToTile(tile);
        }
    }

    private void RefreshAllTileLabels()
    {
        foreach (var tile in Tiles.Values)
        {
            tile.TileView?.RefreshLabel(tile);
        }
    }

    // Tile Props

    private void SpawnTerrainProps()
    {
        ClearTerrainProps();

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null)
                continue;

            if (tile.IsBlocked)
                continue;

            if (tile.TerrainType == TileTerrainType.Grass)
            {
                SpawnGrassOnTile(tile, 0.65f, 1, 3);
            }
            else if (tile.TerrainType == TileTerrainType.Forest)
            {
                SpawnGrassOnTile(tile, 0.9f, 2, 4);
            }
        }
    }

    private void ClearTerrainProps()
    {
        Node parent = PropParent ?? this;

        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup("generated_prop"))
                child.QueueFree();
        }
    }

    private void SpawnGrassOnTile(TileData tile, float spawnChance, int minCount, int maxCount)
    {
        if (_rng.Randf() > spawnChance)
            return;

        int count = _rng.RandiRange(minCount, maxCount);

        for (int i = 0; i < count; i++)
        {
            PackedScene scene = GrassTuftScene;

            if (GrassTuftSceneAlt != null && _rng.Randf() < 0.35f)
                scene = GrassTuftSceneAlt;

            if (scene == null)
                continue;

            var tuft = scene.Instantiate<Node3D>();

            Node parent = PropParent ?? this;
            parent.AddChild(tuft);

            Vector3 basePos = tile.TileView.GlobalPosition;

            float xOffset = _rng.RandfRange(-0.35f, 0.35f);
            float zOffset = _rng.RandfRange(-0.35f, 0.35f);

            tuft.GlobalPosition = basePos + new Vector3(xOffset, 0.05f, zOffset);

            Vector3 rot = tuft.RotationDegrees;
            rot.Y = _rng.RandfRange(0f, 360f);
            tuft.RotationDegrees = rot;

            float scale = _rng.RandfRange(0.85f, 1.2f);
            tuft.Scale = new Vector3(scale, scale, scale);

            tuft.AddToGroup("generated_prop");
        }
    }
}
