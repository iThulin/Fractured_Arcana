using Godot;
using System;
using System.Collections.Generic;

// HexGridManager.Visuals.cs — tile visual/material application, blended-mesh rebuild, obstacle spawning.
// (Legacy per-tile prop/tuft spawning removed 2026-07-15 — superseded by the
// painterly scatter family: PainterlyGrass / Flowers / Rocks / Canopy.)
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

        // Wire the board-wide imbuement lookup so the GROUND responds too, not just
        // the grass standing on it. Safe on an override material: the shader's
        // use_imbuement_field defaults to false, so a template that never reaches
        // this call renders byte-for-byte as before.
        ImbuementField.Attach(_terrainMaterialTemplate, this);
        _terrainMaterialTemplate.SetShaderParameter("terrain_textures", texArray);
        if (nrmArray != null)
            _terrainMaterialTemplate.SetShaderParameter("terrain_normals", nrmArray);
        _terrainMaterialTemplate.SetShaderParameter("texture_scale", TerrainTextureScale);

        _terrainMaterialTemplate.SetShaderParameter("grid_hex_radius", HexRadius);

        return _terrainMaterialTemplate;
    }

    /// <summary>
    /// Pushes the active recipe's sand palette (or the shader defaults) into
    /// the SHARED splat template. Must run BEFORE ApplyTileVisuals — tiles
    /// duplicate the template. The default constants MIRROR terrain_splat's
    /// sand_light/sand_warm — keep them in sync if the shader defaults change.
    /// Reset matters: the template persists across regens.
    /// </summary>
    private void ApplyRecipeSandStyle()
    {
        var template = GetTerrainMaterialTemplate();
        if (template == null)
            return;

        Color light = new Color(0.85f, 0.76f, 0.57f);
        Color warm = new Color(0.70f, 0.56f, 0.38f);

        SandSpec sand = _activeRecipe?.Sand;
        if (sand != null)
        {
            light = sand.Light ?? light;
            warm = sand.Warm ?? warm;
        }

        template.SetShaderParameter("sand_light", light);
        template.SetShaderParameter("sand_warm", warm);
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

        // City sieges: the city continues past the rim (visual only).
        // ClearObstacleVisuals above frees the previous batch via the
        // generated_obstacle group, so this rebuilds alongside obstacles.
        SpawnSiegeBackdrop();

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

            // City siege kinds ("wall", "building:<id>") have no authored scenes
            // yet — spawn placeholder prisms so blocked is never invisible.
            // (HexGridManager.CityStamps; swap for real models in the art pass.)
            if (scene == null && tile.TileView != null &&
                (tile.ObstacleKind == "wall" || tile.ObstacleKind.StartsWith("building:")))
            {
                SpawnCityObstaclePlaceholder(tile);
                continue;
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
            TileTerrainType.Sand => UITheme.CombatTileSand,
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
}
