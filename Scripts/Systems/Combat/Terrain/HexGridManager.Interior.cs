using Godot;

// ============================================================
// HexGridManager.Interior.cs  (partial of HexGridManager)
//
// Purpose:        Interior recipes (MapRecipe.Interior): fights staged
//                 inside a built set instead of the outdoor world. The
//                 first is the prologue's Ceremony Hall (HallSet.cs).
//                 While an interior is active GenerateMap builds no
//                 vista rings, no far field and no ground scatter;
//                 this partial gives the scene its own copy of the
//                 environment (Combat_Environment.tres is shared with
//                 the campus viewport, so it must never take the hall's
//                 background), swaps the sky for a plain background,
//                 flattens the tile tops, can dress the tiles as a laid
//                 floor on its own copy of the splat template (also a
//                 shared resource), and builds the set around the
//                 finished board, its floor just under the tile tops.
//                 The next outdoor map restores each of those.
// Layer:          System (generation)
// Collaborators:  MapRecipe (InteriorSpec), HallSet.cs, GenerateMap
//                 (HexGridManager.cs) which calls the five steps below
// ============================================================

public partial class HexGridManager : Node3D
{
    /// <summary>The active recipe's interior, or null outdoors.</summary>
    public InteriorSpec ActiveInterior => _activeRecipe?.Interior;

    /// <summary>True while the active recipe stages the fight inside a set.</summary>
    public bool InteriorActive => !string.IsNullOrEmpty(_activeRecipe?.Interior?.Set);

    /// <summary>The scene's own environment resource, held while an interior works on a
    /// copy of it. Null when no copy is in use.</summary>
    private Godot.Environment _outdoorEnvironment;

    /// <summary>The splat template as the scene supplied it, held while an interior dresses
    /// its floor on a copy. Null when no copy is in use.</summary>
    private ShaderMaterial _outdoorTerrainTemplate;

    /// <summary>The per-terrain tile-top noise amplitudes as authored (HexMeshBuilder reads
    /// these, not TerrainNoiseAmplitude), held while an interior flattens the tops. Order:
    /// Grass, Forest, Stone, Water, Ice, Lava, Arcane, Sand. Null when nothing is held.</summary>
    private float[] _outdoorNoiseAmps;

    /// <summary>Step 1, right after ResolveRecipe: hand back the scene's splat template if a
    /// previous interior left its copy in use, before anything in this generation writes to
    /// the template (DigWaterBasins writes its waterline there); and cap every terrain's
    /// tile-top noise at the interior's TileRelief (a laid floor, not a field: stone's own
    /// amplitude is half a meter), or give an outdoor map the authored amplitudes back.</summary>
    private void PrepareInteriorGeneration()
    {
        if (_outdoorTerrainTemplate != null)
        {
            _terrainMaterialTemplate = _outdoorTerrainTemplate;
            _outdoorTerrainTemplate = null;
        }

        if (InteriorActive && ActiveInterior.TileRelief >= 0f)
        {
            _outdoorNoiseAmps ??= ReadNoiseAmps();
            var capped = new float[_outdoorNoiseAmps.Length];
            for (int i = 0; i < capped.Length; i++)
                capped[i] = Mathf.Min(_outdoorNoiseAmps[i], ActiveInterior.TileRelief);
            WriteNoiseAmps(capped);
        }
        else if (_outdoorNoiseAmps != null)
        {
            WriteNoiseAmps(_outdoorNoiseAmps);
            _outdoorNoiseAmps = null;
        }
    }

    private float[] ReadNoiseAmps() => new[]
    {
        GrassNoiseAmp, ForestNoiseAmp, StoneNoiseAmp, WaterNoiseAmp,
        IceNoiseAmp, LavaNoiseAmp, ArcaneNoiseAmp, SandNoiseAmp,
    };

    private void WriteNoiseAmps(float[] amps)
    {
        GrassNoiseAmp = amps[0];
        ForestNoiseAmp = amps[1];
        StoneNoiseAmp = amps[2];
        WaterNoiseAmp = amps[3];
        IceNoiseAmp = amps[4];
        LavaNoiseAmp = amps[5];
        ArcaneNoiseAmp = amps[6];
        SandNoiseAmp = amps[7];
    }

    /// <summary>Step 2, after DigWaterBasins and before the sand style, the horizon and
    /// ApplyTileHeights (where the tiles duplicate the splat template), so each of those
    /// writes to the copy: an interior with floor tones paints its stone tops as dressed
    /// floor stone, near flat, with its own joints, on a copy of the template. The template
    /// is a shared resource (Assets/Materials/terrain_splat.tres), so it is never written to
    /// here; step 1 hands it back on the next generation.</summary>
    private void ApplyInteriorGroundStyle()
    {
        var spec = ActiveInterior;
        if (!InteriorActive || !UseTerrainTextures || spec.FloorCool == null || spec.FloorWarm == null)
            return;
        var template = GetTerrainMaterialTemplate();
        if (template == null)
            return;   // no splat shader: the tiles fall back to vertex colors anyway

        var floor = (ShaderMaterial)template.Duplicate();
        floor.SetShaderParameter("stone_strength", 1.0f);           // all paint, no rock texture
        floor.SetShaderParameter("stone_cool", spec.FloorCool.Value);
        floor.SetShaderParameter("stone_warm", spec.FloorWarm.Value);
        floor.SetShaderParameter("stone_mottle", 0.05f);
        floor.SetShaderParameter("stone_strata_strength", 0.0f);
        floor.SetShaderParameter("stone_elev_lighten", 0.0f);
        floor.SetShaderParameter("normal_strength", 0.12f);         // dressed, not quarried
        if (spec.FloorJoint is Color joint)
        {
            floor.SetShaderParameter("grid_line_color", joint);
            floor.SetShaderParameter("skirt_color", joint);
        }

        _outdoorTerrainTemplate = template;
        _terrainMaterialTemplate = floor;
    }

    /// <summary>Step 3, before the atmosphere is written: an interior works on its own
    /// copy of the environment; an outdoor map gets the scene's resource back.</summary>
    private void PrepareInteriorEnvironment()
    {
        if (ThemeWorldEnvironment == null)
            return;

        if (InteriorActive)
        {
            if (_outdoorEnvironment == null && ThemeWorldEnvironment.Environment != null)
            {
                _outdoorEnvironment = ThemeWorldEnvironment.Environment;
                ThemeWorldEnvironment.Environment = (Godot.Environment)_outdoorEnvironment.Duplicate();
            }
        }
        else if (_outdoorEnvironment != null)
        {
            ThemeWorldEnvironment.Environment = _outdoorEnvironment;
            _outdoorEnvironment = null;
        }
    }

    /// <summary>Step 4, after the atmosphere and the lens: no sky indoors. The background
    /// shows only where the set does not: past a cut-away wall or ceiling.</summary>
    private void ApplyInteriorBackground()
    {
        if (!InteriorActive || ThemeWorldEnvironment?.Environment is not Godot.Environment env)
            return;
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = ActiveInterior.Background;
    }

    /// <summary>Step 5, once the grid bounds are final: build the active interior's set
    /// around the board, after clearing any set a previous generation left. With a
    /// BoardRise the set's floor sits that far under the lowest tiles' tops, so the board is
    /// laid into the floor; without one it sits at the bottom of the tile skirts.</summary>
    private void BuildInteriorSet()
    {
        foreach (Node child in GetChildren())
        {
            if (child.IsInGroup(HallSet.GroupName))
                child.QueueFree();
        }

        if (!InteriorActive)
            return;

        if (ActiveInterior.Set != HallSet.SetId)
        {
            GD.PushWarning($"[Interior] Unknown set '{ActiveInterior.Set}' on recipe '{MapRecipeId}'; the board stands alone in the dark.");
            return;
        }

        float floorY = ActiveInterior.BoardRise >= 0f
            ? _lastWorldFloor + HexTile.HeightStep - ActiveInterior.BoardRise
            : _lastWorldFloor;

        var set = new HallSet { Name = "CeremonyHall" };
        set.AddToGroup(HallSet.GroupName);
        AddChild(set);
        set.Build(GridBoundsMin, GridBoundsMax, floorY);
    }
}
