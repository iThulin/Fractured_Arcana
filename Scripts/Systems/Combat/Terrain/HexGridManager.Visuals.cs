using Godot;
using System;
using System.Collections.Generic;

// HexGridManager.Visuals.cs: tile visual/material application, blended-mesh rebuild, obstacle spawning.
// (Legacy per-tile prop/tuft spawning removed 2026-07-15, superseded by the
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
    /// the SHARED splat template. Must run BEFORE ApplyTileVisuals, because tiles
    /// duplicate the template. The default constants MIRROR terrain_splat's
    /// sand_light/sand_warm, so keep them in sync if the shader defaults change.
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

    /// <summary>Rebuild every obstacle placeholder after a mid-fight change to the
    /// obstacle set (raise_wall / drop_wall / breakables). Cheap at battlefield size.</summary>
    public void RefreshObstacleVisuals() => SpawnObstacleVisuals();

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

            if (string.IsNullOrEmpty(tile.ObstacleKind) || tile.TileView == null)
                continue;
            if (!tile.IsBlocked)
                continue;   // walkable "rubble" is a terrain scar, not a body (SetTerrainScar)

            // Building shells keep the city prism placeholder (CityStamps).
            if (tile.ObstacleKind.StartsWith("building:"))
            {
                SpawnCityObstaclePlaceholder(tile);
                continue;
            }

            // Everything else is a catalog kind: its silhouette and colour come from
            // Data/Obstacles/obstacle_catalog.json, so the Hills map grows rock ledges
            // where the Ruins map grows broken masonry from the same "low" op.
            var spec = ObstacleCatalog.GetOrFallback(tile.ObstacleKind);
            var scene = spec.Silhouette == ObstacleSilhouette.Scene ? ResolveObstacleScene(spec) : null;

            if (scene != null)
            {
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
                continue;
            }

            bool low = spec.IsLow;
            float height = spec.Height > 0f ? spec.Height : DefaultPlaceholderHeight(spec, low);
            switch (spec.Silhouette)
            {
                case ObstacleSilhouette.Slab:
                    SpawnWallPlaceholder(tile, height, spec.Color);
                    break;
                case ObstacleSilhouette.Pillar:
                    SpawnPillarPlaceholder(tile, height, spec.Color);
                    break;
                default:   // Mass, and Scene with nothing to load
                    SpawnMassPlaceholder(tile, height, spec.Color);
                    break;
            }
        }
    }

    private readonly Dictionary<string, PackedScene> _obstacleSceneCache = new();

    /// <summary>Scene for a Silhouette.Scene kind: the grid's exported scene for the
    /// two legacy kinds when set, else the catalog's res:// path (cached), else null
    /// so the caller falls back to a Mass placeholder.</summary>
    private PackedScene ResolveObstacleScene(ObstacleSpec spec)
    {
        if (spec.Kind.Equals("rock", StringComparison.OrdinalIgnoreCase) && RockObstacleScene != null)
            return RockObstacleScene;
        if (spec.Kind.Equals("crystal", StringComparison.OrdinalIgnoreCase) && CrystalObstacleScene != null)
            return CrystalObstacleScene;
        if (string.IsNullOrEmpty(spec.ScenePath))
            return null;
        if (_obstacleSceneCache.TryGetValue(spec.ScenePath, out var cached))
            return cached;
        var loaded = ResourceLoader.Exists(spec.ScenePath) ? GD.Load<PackedScene>(spec.ScenePath) : null;
        if (loaded == null)
            GD.PushWarning($"[ObstacleCatalog] '{spec.Kind}': scene '{spec.ScenePath}' did not load; using a mass placeholder.");
        _obstacleSceneCache[spec.ScenePath] = loaded;
        return loaded;
    }

    private float DefaultPlaceholderHeight(ObstacleSpec spec, bool low) => spec.Silhouette switch
    {
        ObstacleSilhouette.Slab => low ? LowWallHeight
            : (spec.Kind == "wall" && _activeRecipe?.Siege != null ? 3.2f : TallWallHeight),
        ObstacleSilhouette.Pillar => low ? LowWallHeight : PillarHeight,
        _ => low ? LowMassHeight : TallMassHeight
    };

    /// <summary>A hex prism filling the tile: crates, sandbags, boulders, gorse.</summary>
    private void SpawnMassPlaceholder(TileData tile, float height, Color color)
    {
        var mesh = new CylinderMesh
        {
            TopRadius = HexRadius * 0.80f,
            BottomRadius = HexRadius * 0.92f,
            Height = height,
            RadialSegments = 6,
        };
        mesh.Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 1f };
        PlaceObstaclePlaceholder(new MeshInstance3D { Mesh = mesh, RotationDegrees = new Vector3(0f, 30f, 0f) }, tile, height);
    }

    // Placeholder proportions. A unit's capsule is ~1.5 tall (Unit.tscn), so a low
    // wall sits at its waist and a tall wall clears its head with room to spare.
    private const float LowWallHeight = 0.55f;
    private const float TallWallHeight = 2.2f;
    private const float PillarHeight = 2.4f;
    private const float LowMassHeight = 0.6f;
    private const float TallMassHeight = 1.8f;

    /// <summary>The hex axis (0, 1, or 2) along which <paramref name="tile"/>'s
    /// same-kind obstacle neighbours run, or -1 when it has none. Used to turn a
    /// band of per-tile obstacles into one continuous slab.</summary>
    private int ObstacleRunAxis(TileData tile)
    {
        int bestAxis = -1, bestCount = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            int count = 0;
            foreach (var dir in new[] { HexDirs[axis], HexDirs[axis + 3] })
            {
                if (Tiles.TryGetValue(tile.Axial + dir, out var n)
                    && n.IsBlocked && n.ObstacleKind == tile.ObstacleKind)
                    count++;
            }
            if (count > bestCount)
            { bestCount = count; bestAxis = axis; }
        }
        return bestAxis;
    }

    /// <summary>Y rotation (degrees) that points a box's local X along hex axis
    /// <paramref name="axis"/> in world space.</summary>
    private float AxisYawDegrees(int axis)
    {
        var w = AxialToWorld(HexDirs[axis]);
        return Mathf.RadToDeg(Mathf.Atan2(-w.Z, w.X));
    }

    /// <summary>A wall slab spanning the tile, aligned to its run of same-kind
    /// neighbours (or to the flank axis when isolated) so a cover_line reads as one
    /// wall with a gate rather than a row of blocks.</summary>
    private void SpawnWallPlaceholder(TileData tile, float height, Color color)
    {
        int axis = ObstacleRunAxis(tile);
        if (axis < 0)
            axis = 2;   // isolated: HexDirs[2] runs along world Z, across the X-aligned player-enemy axis
        float neighbourGap = HexRadius * Mathf.Sqrt(3f);     // centre-to-centre, flat-top

        var mesh = new BoxMesh
        {
            Size = new Vector3(neighbourGap * 1.04f, height, HexRadius * 0.45f),
        };
        mesh.Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 1f };

        var slab = new MeshInstance3D
        {
            Mesh = mesh,
            RotationDegrees = new Vector3(0f, AxisYawDegrees(axis), 0f),
        };
        PlaceObstaclePlaceholder(slab, tile, height);
    }

    /// <summary>A round pillar for High kinds with no scene: tall enough to block
    /// sight, narrow enough that the tile behind it is visibly reachable by a burst.</summary>
    private void SpawnPillarPlaceholder(TileData tile, float height, Color color)
    {
        var mesh = new CylinderMesh
        {
            TopRadius = HexRadius * 0.32f,
            BottomRadius = HexRadius * 0.40f,
            Height = height,
            RadialSegments = 10,
        };
        mesh.Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 1f };
        PlaceObstaclePlaceholder(new MeshInstance3D { Mesh = mesh }, tile, height);
    }

    private void PlaceObstaclePlaceholder(MeshInstance3D node, TileData tile, float height)
    {
        if (ObstacleParent != null)
        {
            ObstacleParent.AddChild(node);
            node.GlobalPosition = tile.TileView.GlobalPosition + new Vector3(0f, height * 0.5f, 0f);
        }
        else
        {
            AddChild(node);
            node.Position = tile.TileView.Position + new Vector3(0f, height * 0.5f, 0f);
        }
        node.AddToGroup("generated_obstacle");
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
