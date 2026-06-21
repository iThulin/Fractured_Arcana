using Godot;
using System;
using System.Collections.Generic;

// HexGridManager.Themes.cs — per-theme accent terrain, theme set-piece features, landmark, atmosphere
// Partial of HexGridManager. Split out for navigability; behaviour-neutral.
public partial class HexGridManager
{
    private void ApplyThemeToLayout()
    {
        switch (Theme)
        {
            case MapTheme.ArcaneMeadow:
                ApplyArcaneMeadowTheme();
                break;

            case MapTheme.FrozenBasin:
                ApplyFrozenBasinTheme();
                break;

            case MapTheme.VolcanicScar:
                ApplyVolcanicScarTheme();
                break;

            case MapTheme.OvergrownRuins:
                ApplyOvergrownRuinsTheme();
                break;

            case MapTheme.VerdantWoods:
                ApplyVerdantWoodsTheme();
                break;

            case MapTheme.Wetlands:
                ApplyWetlandsTheme();
                break;

            case MapTheme.HighlandCrags:
                ApplyHighlandCragsTheme();
                break;

            case MapTheme.RiverValley:
                ApplyRiverValleyTheme();
                break;

            case MapTheme.Heathland:
                ApplyHeathlandTheme();
                break;

            case MapTheme.CoastalShallows:
                ApplyCoastalShallowsTheme();
                break;
        }
    }

    /// <summary>
    /// Places one deliberate theme set-piece near the contest centre by invoking the
    /// (previously unused) Generate*Feature builders, then instantiates the optional
    /// LandmarkScene at the midpoint of the spawn anchors. All of this is null-safe and
    /// runs before connectivity, so the carve pass repairs any path it happens to block.
    /// </summary>
    private void PlaceThemeLandmark()
    {
        switch (Theme)
        {
            case MapTheme.ArcaneMeadow:
                GenerateArcaneMeadowFeature();
                break;

            case MapTheme.FrozenBasin:
                GenerateFrozenBasinFeature();
                break;

            case MapTheme.VolcanicScar:
                GenerateVolcanicScarFeature();
                break;

            case MapTheme.OvergrownRuins:
                GenerateOvergrownRuinsFeature();
                break;
        }

        if (LandmarkScene == null)
            return;

        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);
        if (!Tiles.TryGetValue(center, out var centerTile) || centerTile.TileView == null)
            return;

        var landmark = LandmarkScene.Instantiate<Node3D>();
        Node parent = PropParent ?? this;
        parent.AddChild(landmark);
        landmark.GlobalPosition = centerTile.TileView.GlobalPosition;
        landmark.AddToGroup("generated_prop");
    }

    /// <summary>Optional per-theme lighting + fog. No-op unless a sun / WorldEnvironment is assigned.</summary>
    private void ApplyThemeAtmosphere()
    {
        if (ThemeSun == null && ThemeWorldEnvironment == null)
            return;

        Color sunColor;
        float sunEnergy;
        Color ambient;
        float ambientEnergy;
        Color fogColor;
        float fogDensity;

        switch (Theme)
        {
            case MapTheme.FrozenBasin:
                sunColor = new Color(0.85f, 0.92f, 1.0f);
                sunEnergy = 1.1f;
                ambient = new Color(0.70f, 0.80f, 1.0f);
                ambientEnergy = 0.6f;
                fogColor = new Color(0.85f, 0.90f, 1.0f);
                fogDensity = 0.02f;
                break;

            case MapTheme.VolcanicScar:
                sunColor = new Color(1.0f, 0.70f, 0.45f);
                sunEnergy = 1.0f;
                ambient = new Color(0.50f, 0.35f, 0.30f);
                ambientEnergy = 0.5f;
                fogColor = new Color(0.60f, 0.30f, 0.20f);
                fogDensity = 0.03f;
                break;

            case MapTheme.OvergrownRuins:
                sunColor = new Color(0.80f, 0.95f, 0.75f);
                sunEnergy = 0.9f;
                ambient = new Color(0.50f, 0.60f, 0.45f);
                ambientEnergy = 0.5f;
                fogColor = new Color(0.60f, 0.70f, 0.55f);
                fogDensity = 0.015f;
                break;

            case MapTheme.ArcaneMeadow:
            default:
                sunColor = new Color(0.95f, 0.95f, 1.0f);
                sunEnergy = 1.0f;
                ambient = new Color(0.60f, 0.60f, 0.80f);
                ambientEnergy = 0.4f;
                fogColor = new Color(0.70f, 0.70f, 0.90f);
                fogDensity = 0.01f;
                break;
        }

        if (ThemeSun != null)
        {
            ThemeSun.LightColor = sunColor;
            ThemeSun.LightEnergy = sunEnergy;
        }

        if (ThemeWorldEnvironment?.Environment is Godot.Environment env)
        {
            env.AmbientLightColor = ambient;
            env.AmbientLightEnergy = ambientEnergy;
            env.FogEnabled = true;
            env.FogLightColor = fogColor;
            env.FogDensity = fogDensity;
        }
    }

    // Themes — these now layer ACCENTS on top of the field-derived base terrain.

    private void ApplyArcaneMeadowTheme()
    {
        int forestPatches = GetTerrainPatchCount(1, 4);
        int waterPatches = GetTerrainPatchCount(0, 2);

        for (int i = 0; i < forestPatches; i++)
            PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, GetPatchRadius(1, 3), GetEdgeChance());

        for (int i = 0; i < waterPatches; i++)
            PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Water, GetPatchRadius(1, 2), GetEdgeChance());

        PaintElementPatch(GetRandomCentralCoord(), TileElementType.Arcane, GetPatchRadius(1, 2), 1.0f, GetEdgeChance());

        if (_rng.Randf() < Mathf.Lerp(0.2f, 0.8f, ObstacleDensity))
            PaintObstacleCluster(GetRandomCentralCoord(), "crystal", GetObstacleClusterSize(2, 4));
    }

    private void ApplyFrozenBasinTheme()
    {
        PaintTerrainPatch(GetRandomCentralCoord(), TileTerrainType.Ice, 3, 0.95f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Ice, 2, 0.85f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Water, 2, 0.75f);

        PaintElementPatch(GetRandomCentralCoord(), TileElementType.Frost, 2, 1.0f, 0.9f);
        PaintElementPatch(GetRandomCoord(), TileElementType.Frost, 1, 0.7f, 0.75f);
    }

    private void ApplyVolcanicScarTheme()
    {
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 3, 0.9f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 2, 0.85f);

        Vector2I start = GetRandomCentralCoord();
        Vector2I dir = HexDirs[_rng.RandiRange(0, HexDirs.Length - 1)];

        PaintLinearFeature(start, dir, 5, tile =>
        {
            MakeLava(tile);
            tile.Height -= 1;
        }, 0.2f);

        PaintElementPatch(start, TileElementType.Fire, 2, 1.0f, 0.8f);
    }

    private void ApplyOvergrownRuinsTheme()
    {
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 3, 0.9f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 2, 0.85f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 2, 0.75f);

        if (_rng.Randf() < 0.5f)
            PaintElementPatch(GetRandomCentralCoord(), TileElementType.Arcane, 1, 0.8f, 0.7f);
    }

    private void GenerateArcaneMeadowFeature()
    {
        Vector2I center = GetRandomCentralCoord();

        PaintHeightHill(center, 2, 2);

        PaintFilledRadius(center, 2, tile =>
        {
            MakeArcaneGround(tile);
        }, 0.85f);

        PaintRingFeature(center, 2, tile =>
        {
            if (_rng.Randf() < 0.4f)
                MakeCrystalObstacle(tile);
        }, 0.7f);
    }

    private void GenerateFrozenBasinFeature()
    {
        Vector2I center = GetRandomCentralCoord();

        PaintHeightBasin(center, 2, 2);

        PaintFilledRadius(center, 2, tile =>
        {
            MakeIce(tile);
        }, 0.9f);

        PaintRingFeature(center, 2, tile =>
        {
            if (_rng.Randf() < 0.35f)
                MakeRockObstacle(tile);
        }, 0.75f);
    }

    private void GenerateVolcanicScarFeature()
    {
        Vector2I start = GetRandomCoord();
        Vector2I dir = HexDirs[_rng.RandiRange(0, HexDirs.Length - 1)];

        PaintHeightRidge(start, dir, 5, 2);

        PaintLinearFeature(start, dir, 5, tile =>
        {
            MakeLava(tile);
            tile.Height -= 1; // cut a lava trench through the ridge
        }, 0.25f);
    }

    private void GenerateOvergrownRuinsFeature()
    {
        Vector2I center = GetRandomCentralCoord();

        PaintRingFeature(center, 2, tile =>
        {
            tile.Height = Math.Max(tile.Height, 2);
            tile.TerrainType = TileTerrainType.Stone;
            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.MoveCost = 1;

            if (_rng.Randf() < 0.7f)
                MakeRockObstacle(tile);
        }, 0.75f);

        PaintFilledRadius(center, 1, tile =>
        {
            tile.TerrainType = TileTerrainType.Grass;
            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.MoveCost = 1;
            tile.Height = Math.Max(tile.Height, 1);

            if (_rng.Randf() < 0.5f)
            {
                tile.ElementType = TileElementType.Arcane;
                tile.ElementStrength = 0.8f;
            }
        }, 1.0f);
    }
}
