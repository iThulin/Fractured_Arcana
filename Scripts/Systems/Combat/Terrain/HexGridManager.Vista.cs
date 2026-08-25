using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Vista.cs  (partial of HexGridManager)
//
// NON-PLAYABLE VISTA RING: the world beyond the battlefield.
//
// Generates VistaRingDepth extra rings of hexes past the playable
// boundary so the combat scenario reads as a place in a landscape,
// not a floating board. Vista tiles:
//   - Sample the SAME MapField noise as the playable grid, so terrain
//     and height simply continue outward (heights are clamped ring by
//     ring to +/-1 step of already-known neighbours so the world rolls
//     away gently instead of ending on random cliffs).
//   - Live in VistaTiles, NEVER in Tiles. Pathfinding, spawns, zones,
//     labels, features, and AI cannot see them by construction.
//   - Have no collision (MarkAsVista zeroes the body + area layers),
//     so they can't be hovered, clicked, or card-targeted.
//   - Render de-emphasized via the terrain_splat `vista_fade` INSTANCE
//     uniform (Forward+ feature): grid lines suppressed, slightly
//     desaturated/darkened, so the playable boundary stays readable.
//   - Receive reduced-density painterly scatter (grass/flowers/rocks/
//     canopy) through ScatterTiles(); vista canopy is always PERMANENT
//     (never occlusion-fades), because it is the framing treeline.
//
// WORLD-ADJACENCY SEAM (future): VistaTerrainBias lets a caller push
// the vista classification toward a terrain per hex-direction before
// generation. That is the hook for combat_environments §5 spatial storytelling
// (forest vista on the side of the world map that borders forest, etc.).
// Unset = pure field continuation.
//
// INTEGRATION (two lines in GenerateMap()):
//     GenerateVistaRing(field);   // after EnsureConnectivityBetweenSpawns()
//     BuildVistaMeshes();         // after ApplyTileVisuals()
// The split matters: vista DATA must exist before playable edge tiles
// build their meshes (so they blend outward instead of skirting), but
// vista MESHES need _lastWorldFloor, which ApplyTileHeights computes.
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Vista Ring")]

    /** Master toggle. When off, no vista is generated and any existing ring is cleared. */
    [Export] public bool EnableVistaRing = true;

    /** How many rings of non-playable hexes to generate beyond the playable boundary. 2 reads as "the world continues"; 3+ pushes the horizon further at proportional cost (each ring is a full row of tile meshes + scatter). */
    [Export(PropertyHint.Range, "1,6,1")] public int VistaRingDepth = 2;

    /** Painterly scatter density on vista tiles, as a fraction of playable density. Lower keeps the perimeter cheap and softly thinner than the arena. */
    [Export(PropertyHint.Range, "0,1,0.05")] public float VistaScatterDensity = 0.5f;

    /** Draw a persistent thin line along the playable boundary (every arena edge facing vista or void), so players can read where the battlefield ends without selecting a unit. */
    [Export] public bool ShowArenaBoundary = true;

    /** Colour (with alpha) of the arena boundary ribbon. Kept warm + translucent so it reads as a marker, not a wall. */
    [Export] public Color ArenaBoundaryColor = new Color(0.95f, 0.88f, 0.65f, 0.4f);

    /** Width (world units) of the arena boundary ribbon. */
    [Export(PropertyHint.Range, "0.02,0.25,0.01")] public float ArenaBoundaryWidth = 0.07f;

    /// <summary>Vista tiles by axial coord. Deliberately separate from <see cref="Tiles"/>,
    /// so gameplay systems iterate Tiles and can never reach these.</summary>
    public readonly Dictionary<Vector2I, TileData> VistaTiles = new();

    /// <summary>Optional per-direction terrain bias for the vista (future world-adjacency
    /// hook, combat_environments §5). Key = hex direction index 0..5 (HexDirs order);
    /// value = terrain the vista on that side should lean toward. Empty = pure field
    /// continuation. Set BEFORE GenerateMap().</summary>
    public readonly Dictionary<int, TileTerrainType> VistaTerrainBias = new();

    private const string VistaTileGroup = "vista_tiles";

    /// <summary>Tile lookup that also sees vista tiles. Used by the MESH BUILDER (so
    /// playable edge tiles blend outward into the vista) and by scatter neighbour
    /// checks. Gameplay code must keep using GetTile / Tiles.</summary>
    public TileData GetTileOrVista(Vector2I axial)
    {
        if (Tiles.TryGetValue(axial, out var t))
            return t;
        return VistaTiles.TryGetValue(axial, out var v) ? v : null;
    }

    /// <summary>Every tile the painterly scatters should cover: playable tiles at full
    /// density, vista tiles at <see cref="VistaScatterDensity"/>.</summary>
    private IEnumerable<(TileData tile, float scatterDensity)> ScatterTiles()
    {
        foreach (var t in Tiles.Values)
            yield return (t, 1f);
        foreach (var t in VistaTiles.Values)
            yield return (t, VistaScatterDensity);
    }

    /// <summary>Generates vista TileData + nodes ring by ring. Terrain/height come from
    /// the same field the playable grid used; heights are clamped to +/-1 step of
    /// already-known neighbours so the surround rolls away gently.</summary>
    private void GenerateVistaRing(MapField field)
    {
        ClearVistaTiles();

        if (!EnableVistaRing || VistaRingDepth <= 0 || field == null)
            return;

        var palette = _activeRecipe?.BaseTerrain?.Palette;
        var known = new HashSet<Vector2I>(Tiles.Keys);
        var frontier = new List<Vector2I>(Tiles.Keys);

        for (int r = 0; r < VistaRingDepth; r++)
        {
            var next = new List<Vector2I>();

            foreach (var c in frontier)
            {
                for (int k = 0; k < 6; k++)
                {
                    var nc = c + HexDirs[k];
                    if (known.Contains(nc))
                        continue;
                    known.Add(nc);
                    next.Add(nc);
                }
            }

            foreach (var coord in next)
            {
                float elevation = field.SampleElevation01(coord);
                float moisture = field.SampleMoisture01(coord);

                TileTerrainType terrain = palette != null
                    ? field.ClassifyByPalette(palette, elevation, moisture)
                    : field.ClassifyTerrain(Theme, elevation, moisture);

                // World-adjacency bias (fed by CombatManager.ApplyVistaBias from the
                // overworld neighbour terrains): the vista on each side leans toward
                // what borders the fight on the world map. Probabilistic and
                // strengthening OUTWARD. The inner ring mostly continues the arena
                // and the outer ring mostly reads as the neighbouring terrain, so the
                // transition is a mottled blend, not a hard seam. Deterministic per
                // coord + MapSeed.
                if (VistaTerrainBias.Count > 0)
                {
                    int side = DominantDirection(coord);
                    if (VistaTerrainBias.TryGetValue(side, out var biased) && biased != terrain)
                    {
                        float ringT = VistaRingDepth <= 1 ? 1f : (float)r / (VistaRingDepth - 1);
                        float chance = Mathf.Lerp(0.45f, 0.9f, ringT);
                        if (VistaHash01(coord) < chance)
                            terrain = biased;
                    }
                }

                int h = field.ElevationToHeightStep(elevation);

                // RING-PROGRESSIVE height relaxation. The seam ring hugs the arena
                // (a height jump right beside playable tiles reads as walls boxing
                // the board in, the iceberg-rim bug), but each ring outward earns
                // +1 step of freedom in both directions, letting the raw field
                // heights re-emerge with distance. Distant rises silhouette against
                // the sky and fill the horizon instead of a flat void.
                int mn = int.MaxValue, mx = int.MinValue;
                for (int k = 0; k < 6; k++)
                {
                    var nb = GetTileOrVista(coord + HexDirs[k]);
                    if (nb == null)
                        continue;
                    mn = Mathf.Min(mn, nb.Height);
                    mx = Mathf.Max(mx, nb.Height);
                }
                if (mn != int.MaxValue)
                    h = Mathf.Clamp(h, mn - 1 - r, mx + r);

                var tileNode = HexTileScene3D.Instantiate<HexTile>();
                tileNode.Position = AxialToWorld(coord);
                tileNode.Axial = coord;
                AddChild(tileNode);
                tileNode.AddToGroup(VistaTileGroup);

                var data = new TileData
                {
                    Axial = coord,
                    TileView = tileNode
                };
                ApplyTerrainType(data, terrain);
                data.Height = h;
                // Belt and braces: vista is unreachable (not in Tiles), but make the
                // data self-describing anyway. IsBlocked stays FALSE so the painterly
                // scatters (which skip blocked tiles) still cover the vista.
                data.IsWalkable = false;
                data.MoveCost = 999;

                tileNode.Data = data;
                // Horizon melt ramps across the rings: innermost 0 (just muted),
                // outermost 1 (dissolves toward the theme fog colour).
                float horizonBlend = VistaRingDepth <= 1
                    ? 1f
                    : (float)r / (VistaRingDepth - 1);
                tileNode.MarkAsVista(horizonBlend);
                VistaTiles[coord] = data;
            }

            frontier = next;
        }
    }

    /// <summary>Wires the horizon to the active atmosphere. Called by BOTH atmosphere
    /// paths (theme + recipe) with the fog colour they just applied: pushes it into the
    /// terrain splat's `horizon_color` (outer vista rings melt toward it) and turns the
    /// world-floor backdrop plane into a matching haze surface instead of a void.</summary>
    private void ApplyHorizon(Color fogColor)
    {
        var template = GetTerrainMaterialTemplate();
        template?.SetShaderParameter("horizon_color", fogColor);

        // The backdrop plane is a scene-level sibling ("GroundMesh"). Sit it just
        // under the world floor and colour it as distant haze; scene fog finishes
        // the blend. Null-guarded: scenes without the plane simply skip this.
        var ground = GetNodeOrNull<MeshInstance3D>("../GroundMesh");
        if (ground == null)
            return;

        var pos = ground.GlobalPosition;
        pos.Y = _lastWorldFloor - 0.05f;
        ground.GlobalPosition = pos;

        if (ground.MaterialOverride is not StandardMaterial3D groundMat)
        {
            groundMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            };
            ground.MaterialOverride = groundMat;
        }
        // Slightly below the fog colour so the plane reads as ground haze, not sky.
        groundMat.AlbedoColor = new Color(fogColor.R * 0.92f, fogColor.G * 0.92f, fogColor.B * 0.9f);
    }

    /// <summary>Builds vista tile meshes. Runs AFTER ApplyTileHeights/ApplyTileVisuals so
    /// _lastWorldFloor and the splat material template exist; vista data existed before
    /// the playable meshes were built, so edge tiles have already blended outward.</summary>
    private void BuildVistaMeshes()
    {
        foreach (var tile in VistaTiles.Values)
        {
            if (tile.TileView == null)
                continue;
            tile.TileView.SetHeight(tile.Height, _lastWorldFloor);
            RebuildTerrainMesh(tile);
        }
    }

    private void ClearVistaTiles()
    {
        foreach (Node child in GetChildren())
        {
            if (child.IsInGroup(VistaTileGroup))
                child.QueueFree();
        }
        VistaTiles.Clear();
    }

    private const string ArenaBoundaryGroup = "arena_boundary";

    /// <summary>Builds the persistent arena-boundary ribbon: for every playable tile
    /// edge whose neighbour is NOT playable (vista or void), a thin terrain-hugging
    /// quad strip just above the surface. Communicates "the battle ends here" now
    /// that the vista makes non-playable ground fill most of the frame. Rebuilt per
    /// map gen; call AFTER heights/meshes are final so the Y sampling matches.</summary>
    private void BuildArenaBoundary()
    {
        foreach (Node child in GetChildren())
        {
            if (child.IsInGroup(ArenaBoundaryGroup))
                child.QueueFree();
        }

        if (!ShowArenaBoundary || Tiles.Count == 0)
            return;

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = ArenaBoundaryColor,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Triangles, mat);
        bool any = false;

        const int Subdiv = 4;              // segments per edge, so the ribbon follows the noised surface
        const float Lift = 0.07f;          // sit just above the blended mesh
        float halfW = ArenaBoundaryWidth * 0.5f;

        Vector2 CornerXZ(int i)
        {
            float a = Mathf.DegToRad(60f * i);
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * HexRadius;
        }

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null)
                continue;

            Vector3 c = tile.TileView.GlobalPosition;

            for (int d = 0; d < 6; d++)
            {
                if (Tiles.ContainsKey(tile.Axial + HexDirs[d]))
                    continue; // internal edge, no marker

                // Neighbour in HexDirs[d] shares corner edge (6 - d) % 6
                // (dirs run CW, corners CCW, the same reflection the zone renderer uses).
                int e = (6 - d) % 6;
                Vector2 cA = CornerXZ(e);
                Vector2 cB = CornerXZ((e + 1) % 6);

                for (int s = 0; s < Subdiv; s++)
                {
                    Vector2 u0 = cA.Lerp(cB, s / (float)Subdiv);
                    Vector2 u1 = cA.Lerp(cB, (s + 1) / (float)Subdiv);

                    float y0 = SampleGrassSurfaceY(tile, c.X + u0.X, c.Z + u0.Y) + Lift;
                    float y1 = SampleGrassSurfaceY(tile, c.X + u1.X, c.Z + u1.Y) + Lift;

                    Vector2 dir = (u1 - u0).Normalized();
                    Vector2 perp = new Vector2(dir.Y, -dir.X) * halfW;

                    var a0 = new Vector3(c.X + u0.X + perp.X, y0, c.Z + u0.Y + perp.Y);
                    var a1 = new Vector3(c.X + u0.X - perp.X, y0, c.Z + u0.Y - perp.Y);
                    var b0 = new Vector3(c.X + u1.X + perp.X, y1, c.Z + u1.Y + perp.Y);
                    var b1 = new Vector3(c.X + u1.X - perp.X, y1, c.Z + u1.Y - perp.Y);

                    im.SurfaceAddVertex(a0);
                    im.SurfaceAddVertex(b0);
                    im.SurfaceAddVertex(b1);
                    im.SurfaceAddVertex(a0);
                    im.SurfaceAddVertex(b1);
                    im.SurfaceAddVertex(a1);
                    any = true;
                }
            }
        }

        im.SurfaceEnd();

        if (!any)
            return;

        var mi = new MeshInstance3D
        {
            Name = "ArenaBoundary",
            Mesh = im,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        mi.AddToGroup(ArenaBoundaryGroup);
        AddChild(mi);
    }

    /// <summary>Deterministic 0..1 hash of a coord (salted by MapSeed). It drives the
    /// probabilistic vista terrain bias without touching any RNG stream.</summary>
    private float VistaHash01(Vector2I c)
    {
        unchecked
        {
            uint h = (uint)(c.X * 73856093) ^ (uint)(c.Y * 19349663) ^ (uint)MapSeed;
            h ^= h >> 13;
            h *= 0x5BD1E995u;
            h ^= h >> 15;
            return (h & 0xFFFFu) / 65535f;
        }
    }

    /// <summary>Which of the 6 hex directions a coord most points toward from the grid
    /// centre. Used to pick a per-side terrain bias for the vista.</summary>
    private int DominantDirection(Vector2I coord)
    {
        Vector3 centre = (GridBoundsMin + GridBoundsMax) * 0.5f;
        Vector3 w = AxialToWorld(coord);
        var d = new Vector2(w.X - centre.X, w.Z - centre.Z);
        if (d.LengthSquared() < 0.0001f)
            return 0;

        int best = 0;
        float bestDot = float.MinValue;
        for (int k = 0; k < 6; k++)
        {
            Vector3 dir3 = AxialToWorld(HexDirs[k]);
            var dir = new Vector2(dir3.X, dir3.Z).Normalized();
            float dot = dir.Dot(d.Normalized());
            if (dot > bestDot)
            {
                bestDot = dot;
                best = k;
            }
        }
        return best;
    }
}
