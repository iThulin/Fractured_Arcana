# Tide swell — the flood map event raises the painterly water (2026-09-16)

## Why the current flood reads as a blue tint, not water
`FloodTo` converts drowned tiles to `Water` and rebuilds the plane, but `ComputeBodyWaterlines`
pins every body's surface to (lowest adjacent LAND top − lip). After a flood to level L, every
walkable tile ≤ L is water, but non-walkable obstacle tiles and VISTA-ring tiles at ≤ L are still
"land", so the lowest bank is still at L and the surface solves to L·0.6 − 0.06, then gets floored
to (shallowest bed + 0.05). The plane ends up 5 cm over ground that the terrain noise (±0.18 for
grass) pokes straight through. What you see is `TileScarWater` (the blue emission tint), not the plane.

## The change
1. **A tide floor.** `HexGridManager.SetFloodSurface(level, cover)` sets `FloodSurfaceFloorY =
   level·HeightStep + cover`; `ComputeBodyWaterlines` takes `max(solved, floor)` for every body.
   Cover = 0.40 (HeightStep 0.6 minus the grass noise amplitude 0.18, minus margin): drowned ground
   gets 0.40 of real water; dry tiles one step up keep their tops 0.20 clear, with the surface just
   reaching their deepest noise dips (the at-grade marsh look). The dry-share cap already guarantees
   no walkable tile sits at ≤ level once the tide fires, so nothing playable is visually drowned.
2. **The swell.** The plane bakes each vertex's PREVIOUS surface height and depth into `UV2`;
   `painterly_water.gdshader` gets `uniform float rise_t` (default 1) and lerps `VERTEX.y` and the
   baked depth from previous → new. `SpawnWaterPlane(riseSeconds)` tweens `rise_t` 0→1 (Sine, InOut).
   Newly drowned tiles start 5 cm under their own bed and well up; the old body rises with them.
   Generation (`riseSeconds = 0`) is unchanged: `rise_t` stays 1 and UV2 is ignored.
3. **No more blue scar on water.** `ConvertTile` skips `SetTerrainScar` for `"water"` — the plane is
   the marking. Chasm/rubble scars unchanged. The pre-fire telegraph pulse is unchanged.
4. Grid lines under the raised surface fade (`grid_fade_below_y` pushed BEFORE the drowned tiles
   re-bake their splat copies).

Not touched: the weather-rain patch flood (`FloodAdjacentPatches`, single tiles at bank height)
still uses the old solve; if it shows the same film, route it through `SetFloodSurface` too.

---

## 1. `Assets/Shaders/painterly_water.gdshader`

### 1a — uniform
FIND:
```
// ---- Toon lighting ----
group_uniforms toon;
```
REPLACE:
```
// ---- Tide swell (map event "flood") ----
group_uniforms flood;
/** 0 = every vertex sits at its PREVIOUS surface (UV2.x, depth UV2.y); 1 = at the newly solved surface. HexGridManager.SpawnWaterPlane(riseSeconds) tweens this 0→1 so the water swells up over drowned ground instead of popping. Static maps leave it at 1. */
uniform float rise_t : hint_range(0.0, 1.0, 0.01) = 1.0;

// ---- Toon lighting ----
group_uniforms toon;
```

### 1b — vertex
FIND:
```
    v_shore = COLOR.r;
    v_depth = COLOR.g;
    v_spill = COLOR.a;
    v_calm = mix(shore_calm_floor, 1.0, smoothstep(0.0, shore_calm_range, v_shore));

    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
```
REPLACE:
```
    v_shore = COLOR.r;
    v_depth = mix(UV2.y, COLOR.g, rise_t);   // tide swell: baked depth follows the rising surface
    v_spill = COLOR.a;
    v_calm = mix(shore_calm_floor, 1.0, smoothstep(0.0, shore_calm_range, v_shore));

    // Tide swell: UV2.x is where this vertex's surface WAS. At rise_t = 1 this is the identity.
    VERTEX.y = mix(UV2.x, VERTEX.y, rise_t);

    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
```

---

## 2. `Scripts/Systems/Combat/Terrain/HexGridManager.WaterPlane.cs`

### 2a — state
FIND:
```
    private const string WaterPlaneGroup = "water_plane";
    private ShaderMaterial _waterMaterialCache;
    private WaterProfile _waterProfile;
```
REPLACE:
```
    private const string WaterPlaneGroup = "water_plane";
    private ShaderMaterial _waterMaterialCache;
    private WaterProfile _waterProfile;

    /// <summary>Tide (map event "flood"): every body's surface is at least this world
    /// Y. float.MinValue = no tide. Set by <see cref="SetFloodSurface"/>, reset each
    /// generation in DigWaterBasins. Without it a flood to level L solves to L − lip
    /// (obstacle and vista tiles at L are still "land"), then floors to bed + 0.05: a
    /// film the terrain noise pokes through. Observed 2026-09-16 as "blue tiles".</summary>
    public float FloodSurfaceFloorY = float.MinValue;

    /// <summary>Per-tile surface Y of the LAST plane built, so a rebuild can bake where
    /// each vertex came from (UV2) and the shader can swell from there (rise_t).</summary>
    private Dictionary<Vector2I, float> _lastWaterlines = new();

    /// <summary>Highest waterline pushed to the splat's grid_fade_below_y so far this map.</summary>
    private float _gridFadeBelowY = -1000f;

    /// <summary>Tide: raise the resting waterline so ground at or below <paramref name="level"/>
    /// is under <paramref name="coverDepth"/> world units of water. Call BEFORE the drowned
    /// tiles are re-baked (they duplicate the splat template, so the grid-line fade must
    /// already be in it) and before SpawnWaterPlane. Never lowers an existing tide.</summary>
    public void SetFloodSurface(int level, float coverDepth)
    {
        float y = level * HexTile.HeightStep + coverDepth;
        FloodSurfaceFloorY = Mathf.Max(FloodSurfaceFloorY, y);
        if (FloodSurfaceFloorY > _gridFadeBelowY)
        {
            _gridFadeBelowY = FloodSurfaceFloorY;
            GetTerrainMaterialTemplate()?.SetShaderParameter("grid_fade_below_y", _gridFadeBelowY);
        }
    }
```

### 2b — SpawnWaterPlane signature + previous-surface bake + swell tween
FIND:
```
    public void SpawnWaterPlane()
    {
        ClearWaterPlane();
```
REPLACE:
```
    public void SpawnWaterPlane(float riseSeconds = 0f)
    {
        ClearWaterPlane();
```

FIND:
```
        Dictionary<Vector2I, float> surfaceYByTile = ComputeBodyWaterlines(waterTiles);

        // Shore skirt: with ramped beaches the plane extends one tile under
```
REPLACE:
```
        Dictionary<Vector2I, float> surfaceYByTile = ComputeBodyWaterlines(waterTiles);

        // Tide swell: where each water tile's surface WAS. Tiles the last plane
        // covered start at that level; newly drowned ground wells up from just
        // under its own bed. Skirts take the lowest previous level of the water
        // they hang off (a skirt over a bank must never start ABOVE the bank).
        bool hadPrevious = _lastWaterlines.Count > 0;
        var prevYByTile = new Dictionary<Vector2I, float>();
        foreach (var tile in waterTiles)
        {
            prevYByTile[tile.Axial] = _lastWaterlines.TryGetValue(tile.Axial, out float py)
                ? py
                : tile.Height * HexTile.HeightStep - 0.05f;
        }
        var skirtPrev = new Dictionary<TileData, float>();

        // Shore skirt: with ramped beaches the plane extends one tile under
```

FIND:
```
                    var nbr = GetTileOrVista(tile.Axial + dir);
                    if (nbr != null &&
                        nbr.TerrainType != TileTerrainType.Water &&
                        !skirt.ContainsKey(nbr))
                        skirt[nbr] = y;
                }
            }
        }
```
REPLACE:
```
                    var nbr = GetTileOrVista(tile.Axial + dir);
                    if (nbr == null || nbr.TerrainType == TileTerrainType.Water)
                        continue;
                    if (!skirt.ContainsKey(nbr))
                        skirt[nbr] = y;
                    float pv = prevYByTile[tile.Axial];
                    if (!skirtPrev.TryGetValue(nbr, out float cur) || pv < cur)
                        skirtPrev[nbr] = pv;
                }
            }
        }
```

FIND:
```
        foreach (var tile in waterTiles)
            AppendWaterHex(st, tile, surfaceYByTile[tile.Axial], isSkirt: false);
        foreach (var kv in skirt)
            AppendWaterHex(st, kv.Key, kv.Value, isSkirt: true);
```
REPLACE:
```
        foreach (var tile in waterTiles)
            AppendWaterHex(st, tile, surfaceYByTile[tile.Axial], prevYByTile[tile.Axial], isSkirt: false);
        foreach (var kv in skirt)
            AppendWaterHex(st, kv.Key, kv.Value, skirtPrev[kv.Key], isSkirt: true);
        _lastWaterlines = new Dictionary<Vector2I, float>(surfaceYByTile);
```

FIND:
```
            if (WaterSunLight != null)
                waterSm.SetShaderParameter("sun_direction", -WaterSunLight.GlobalTransform.Basis.Z);
        }
    }
```
REPLACE:
```
            if (WaterSunLight != null)
                waterSm.SetShaderParameter("sun_direction", -WaterSunLight.GlobalTransform.Basis.Z);

            // Tide swell: lerp every vertex from its previous surface (UV2) to the new
            // one. Only when there WAS a previous plane; a first build has nothing to
            // rise from and would otherwise well up out of the ground on map load.
            if (riseSeconds > 0f && hadPrevious)
            {
                waterSm.SetShaderParameter("rise_t", 0f);
                var swell = CreateTween();
                swell.TweenProperty(waterSm, "shader_parameter/rise_t", 1f, riseSeconds)
                     .SetTrans(Tween.TransitionType.Sine)
                     .SetEase(Tween.EaseType.InOut);
            }
            else
                waterSm.SetShaderParameter("rise_t", 1f);
        }
    }
```

### 2c — reset per generation
FIND:
```
        GetTerrainMaterialTemplate()?.SetShaderParameter("grid_fade_below_y", -1000f);

        if (!EnableWaterPlane)
            return;
```
REPLACE:
```
        GetTerrainMaterialTemplate()?.SetShaderParameter("grid_fade_below_y", -1000f);
        _gridFadeBelowY = -1000f;
        FloodSurfaceFloorY = float.MinValue;   // no tide on a fresh map
        _lastWaterlines.Clear();               // nothing to swell from either

        if (!EnableWaterPlane)
            return;
```

FIND:
```
        if (maxSurface > float.MinValue)
            GetTerrainMaterialTemplate()?.SetShaderParameter("grid_fade_below_y", maxSurface);
    }
```
REPLACE:
```
        if (maxSurface > float.MinValue)
        {
            _gridFadeBelowY = maxSurface;
            GetTerrainMaterialTemplate()?.SetShaderParameter("grid_fade_below_y", maxSurface);
        }
    }
```

### 2d — the floor in the waterline solve
FIND:
```
            // Safety floor only; DigWaterBasins guarantees room below the lip.
            surfaceY = Mathf.Max(surfaceY, bedMaxTop + 0.05f);
```
REPLACE:
```
            // Safety floor only; DigWaterBasins guarantees room below the lip.
            surfaceY = Mathf.Max(surfaceY, bedMaxTop + 0.05f);
            // Tide: the flood event's resting level overrides the bank solve, because
            // after a flood the "banks" at the drowned height are obstacles and vista
            // tiles, not shoreline. Bodies already above it are unaffected.
            if (FloodSurfaceFloorY > float.MinValue)
                surfaceY = Mathf.Max(surfaceY, FloodSurfaceFloorY);
```

### 2e — bake the previous surface into UV2
FIND:
```
    private void AppendWaterHex(SurfaceTool st, TileData tile, float waterY, bool isSkirt)
    {
        Vector3 c = AxialToWorld(tile.Axial);
        c.Y = waterY;
```
REPLACE:
```
    private void AppendWaterHex(SurfaceTool st, TileData tile, float waterY, float prevY, bool isSkirt)
    {
        Vector3 c = AxialToWorld(tile.Axial);
        c.Y = waterY;
```

FIND:
```
            AddWaterTri(st, tile, waterCenters, c, mca, mcb);
            AddWaterTri(st, tile, waterCenters, mca, a, mab);
            AddWaterTri(st, tile, waterCenters, mca, mab, mcb);
            AddWaterTri(st, tile, waterCenters, mcb, mab, b);
        }
    }

    private void AddWaterTri(SurfaceTool st, TileData tile, List<Vector2> waterCenters, Vector3 p0, Vector3 p1, Vector3 p2)
    {
```
REPLACE:
```
            AddWaterTri(st, tile, waterCenters, prevY, c, mca, mcb);
            AddWaterTri(st, tile, waterCenters, prevY, mca, a, mab);
            AddWaterTri(st, tile, waterCenters, prevY, mca, mab, mcb);
            AddWaterTri(st, tile, waterCenters, prevY, mcb, mab, b);
        }
    }

    private void AddWaterTri(SurfaceTool st, TileData tile, List<Vector2> waterCenters, float prevY, Vector3 p0, Vector3 p1, Vector3 p2)
    {
```

FIND:
```
        AddWaterVertex(st, tile, waterCenters, p0);
        AddWaterVertex(st, tile, waterCenters, p1);
        AddWaterVertex(st, tile, waterCenters, p2);
    }

    private void AddWaterVertex(SurfaceTool st, TileData tile, List<Vector2> waterCenters, Vector3 pos)
    {
        float shore = BakeShoreDistance(tile, pos);
        float depth = BakeDepth(tile, pos, pos.Y);
```
REPLACE:
```
        AddWaterVertex(st, tile, waterCenters, prevY, p0);
        AddWaterVertex(st, tile, waterCenters, prevY, p1);
        AddWaterVertex(st, tile, waterCenters, prevY, p2);
    }

    private void AddWaterVertex(SurfaceTool st, TileData tile, List<Vector2> waterCenters, float prevY, Vector3 pos)
    {
        float shore = BakeShoreDistance(tile, pos);
        float depth = BakeDepth(tile, pos, pos.Y);
        // Tide swell: the surface this vertex rises FROM, and the depth it had there.
        float prevDepth = BakeDepth(tile, pos, prevY);
```

FIND:
```
        st.SetColor(new Color(shore, depth, 0f, spill));
        st.SetNormal(Vector3.Up);
```
REPLACE:
```
        st.SetColor(new Color(shore, depth, 0f, spill));
        st.SetUV2(new Vector2(prevY, prevDepth));
        st.SetNormal(Vector3.Up);
```

---

## 3. `Scripts/Systems/Combat/Core/CombatManager.MapEvents.cs`

### 3a — tuning
FIND:
```
    private const float FloodMinDryShare = 0.40f;
    private int _floodDryBaseline = -1;
```
REPLACE:
```
    private const float FloodMinDryShare = 0.40f;
    private int _floodDryBaseline = -1;

    /// <summary>Water over drowned ground, world units. HeightStep (0.6) minus the grass
    /// noise amplitude (0.18) minus margin: drowned tiles sit under real water, dry tiles
    /// one step up keep their tops clear, and the surface just reaches their deepest
    /// noise dips (the at-grade marsh look). Raise toward 0.5 for a deeper tide.</summary>
    private const float FloodCoverDepth = 0.40f;

    /// <summary>How long the surface takes to swell up to the new level.</summary>
    private const float FloodRiseSeconds = 1.6f;
```

### 3b — fire
FIND:
```
        foreach (var t in affected)
        {
            if (t.Occupant != null && t.Occupant.Stats.IsAlive)
                EvictToDry(t.Occupant, effective, damage);
            ConvertTile(t, "water");
        }
        if (affected.Count > 0)
            grid.SpawnWaterPlane();   // the surface follows the new shoreline
        return affected.Count;
```
REPLACE:
```
        // Raise the resting waterline FIRST: ConvertTile re-bakes each drowned tile's
        // splat copy, and the grid-line fade has to be in the template by then.
        if (affected.Count > 0)
            grid.SetFloodSurface(effective, FloodCoverDepth);
        foreach (var t in affected)
        {
            if (t.Occupant != null && t.Occupant.Stats.IsAlive)
                EvictToDry(t.Occupant, effective, damage);
            ConvertTile(t, "water");
        }
        if (affected.Count > 0)
            grid.SpawnWaterPlane(FloodRiseSeconds);   // the surface swells up over the drowned ground
        return affected.Count;
```

### 3c — no blue scar on water
FIND:
```
        grid.GetTileView(t.Axial)?.SetTerrainScar(into);
    }
```
REPLACE:
```
        // Water shows itself: the plane rises over the tile (tide swell), so the blue
        // emission tint would only fight it. Chasm and rubble keep their scars.
        if (into != "water")
            grid.GetTileView(t.Axial)?.SetTerrainScar(into);
    }
```
