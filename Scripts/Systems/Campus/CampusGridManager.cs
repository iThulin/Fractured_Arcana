using Godot;
using System.Collections.Generic;

// ============================================================
// CampusGridManager.cs
//
// Purpose:        The 3D campus hex grid. Inherits HexGridManager
//                 directly — gets Tiles, AxialToWorld, GetNeighbors,
//                 Distance, ApplyVisualToTile, HexTileScene3D,
//                 HexRadius, and the whole painterly decoration
//                 export surface for free. Overrides _Ready() to
//                 skip the base class's procedural-generation
//                 fallback entirely; always populated explicitly
//                 from save data via LoadFromSave.
// Layer:          System
// Collaborators:  HexGridManager.cs / HexGridManager.Visuals.cs
//                 (base class), CampusMapSaveData.cs (ground
//                 layout), GuildSaveData.cs (Buildings), Building
//                 Database.cs / BuildingDefinition.cs (Footprint),
//                 CampusInputController.cs (click/drag)
// See:            conversation note confirming ApplyVisualToTile
//                 and RebuildTileAndNeighbors are public, and that
//                 _Ready() only self-generates when Tiles is empty
//                 — this class relies on both of those facts.
// ============================================================

/// <summary>3D campus hex grid — a HexGridManager subclass, not a sibling. Reuses the
/// base class's tile machinery and painterly export surface wholesale; supplies its
/// own tile SOURCE (CampusMapSaveData/BuildingSaveData) instead of procedural
/// generation, and never calls GenerateMap() or any of its private helpers.
/// IMPORTANT construction requirement (set by the caller, e.g. CampusScreen, since
/// these are inherited [Export] fields with base-class defaults that don't suit a
/// campus): HexTileScene3D must be assigned, HexRadius should match combat's real
/// value (1.025, confirmed from Battlefield.tscn), and UseBlendedTerrainMesh MUST be
/// set false — left at the base default (true), ApplyVisualToTile takes the blended-
/// mesh branch, which depends on a private field (_lastWorldFloor) this class never
/// populates.</summary>
public partial class CampusGridManager : HexGridManager
{
    /// <summary>Renamed from the base class's own GridBoundsMin/Max (which have a
    /// private setter this subclass can't write to) rather than hiding them with
    /// `new` — avoids any ambiguity about which one a given reference sees.</summary>
    public Vector3 CampusGridBoundsMin { get; private set; }
    public Vector3 CampusGridBoundsMax { get; private set; }

    /// <summary>Parallel to Tiles — CampusTileSaveData.IsBuildable per hex. Kept
    /// separate from TileData on purpose: "can a building go here" is a campus-only
    /// concept, and TileData is a shared combat type we don't want to pollute with it.</summary>
    private readonly Dictionary<Vector2I, bool> _buildableMask = new();

    /// <summary>Parallel to Tiles — empty string = no building. Lets a click on ANY
    /// footprint hex resolve back to the owning building's id, and lets placement
    /// validation check occupancy without parsing TileData.ObstacleKind strings.</summary>
    private readonly Dictionary<Vector2I, string> _buildingAtHex = new();

    /// <summary>Parallel to Tiles — empty string = no landmark. Mirrors
    /// <see cref="_buildingAtHex"/> so a click can resolve a hex back to its landmark
    /// id, and for the same reason it is kept off TileData: landmarks are a campus
    /// story concept with no meaning in combat.</summary>
    private readonly Dictionary<Vector2I, string> _landmarkAtHex = new();

    /// <summary>Parallel to <see cref="_landmarkAtHex"/> — the flag-derived phase,
    /// cached at stamp time so a redraw never has to re-run the hasFlag predicate
    /// per tile.</summary>
    private readonly Dictionary<Vector2I, CampusLandmarkData.LandmarkState> _landmarkStateAtHex = new();

    private List<Vector2I> _previewHexes = new();

    /// <summary>Deliberately does NOT call base._Ready() — that schedules the base
    /// class's AutoGenerateIfEmpty() safety net (a procedural GenerateMap() fallback
    /// this class never wants, since it's always populated explicitly via
    /// LoadFromSave). Overriding as a no-op is safer than relying on Tiles.Count > 0
    /// timing to suppress it.</summary>
    public override void _Ready() { }

    // ── Loading ───────────────────────────────────────────────────────

    /// <summary>Clears any existing tiles and rebuilds the grid from saved data.
    /// Never procedural — the campus is authored/persistent, not a fresh region.</summary>
    public void LoadFromSave(CampusMapSaveData map, List<BuildingSaveData> buildings)
    {
        ClearTiles();

        bool first = true;
        Vector3 min = Vector3.Zero, max = Vector3.Zero;

        foreach (var tileSave in map.Tiles)
        {
            var coord = new Vector2I(tileSave.Q, tileSave.R);
            var worldPos = AxialToWorld(coord); // inherited
            var tileNode = HexTileScene3D.Instantiate<HexTile>(); // inherited field
            tileNode.Position = worldPos;
            tileNode.Axial = coord;
            AddChild(tileNode);

            if (first) { min = worldPos; max = worldPos; first = false; }
            else
            {
                min = new Vector3(Mathf.Min(min.X, worldPos.X), 0, Mathf.Min(min.Z, worldPos.Z));
                max = new Vector3(Mathf.Max(max.X, worldPos.X), 0, Mathf.Max(max.Z, worldPos.Z));
            }

            var terrain = GroundToTerrain(tileSave.Ground);
            var tileData = new TileData
            {
                Axial = coord,
                TileView = tileNode,
                TerrainType = terrain,
                IsWalkable = terrain != TileTerrainType.Water,
                IsBlocked = false,
            };
            if (tileSave.Ground == "Rubble")
                tileData.ApplyTerrainModifier("rubble"); // existing TileData mechanic — see campus_siege_and_defense_v1 §4b

            tileNode.Data = tileData;
            Tiles[coord] = tileData; // inherited dict

            ApplyVisualToTile(tileData); // inherited PUBLIC method — combat's real terrain palette

            // Rubble is never buildable regardless of the saved flag — clearing it is
            // a separate action, not a placement-time override (same rule as before).
            _buildableMask[coord] = tileSave.Ground == "Rubble" ? false : tileSave.IsBuildable;
            _buildingAtHex[coord] = "";
        }

        CampusGridBoundsMin = min;
        CampusGridBoundsMax = max;

        foreach (var b in buildings)
        {
            if (!b.IsPlaced || b.Tier <= 0)
                continue;

            var anchor = new Vector2I(b.Q, b.R);
            var template = BuildingDatabase.GetTemplate(b.Id);
            var footprintHexes = GetFootprintHexes(template, anchor, b.Rotation);

            bool fits = true;
            foreach (var coord in footprintHexes)
                if (!Tiles.ContainsKey(coord)) { fits = false; break; }

            if (!fits)
            {
                GD.PrintErr($"CampusGridManager: building '{b.Id}' footprint anchored at {anchor} " +
                            $"(rotation {b.Rotation}) extends off the campus tile grid. Skipping.");
                continue;
            }

            StampBuilding(b.Id, footprintHexes);
        }
    }

    // ── Landmarks ─────────────────────────────────────────────────────

    /// <summary>Landmark id stamped on this hex, or empty. Mirrors
    /// <see cref="GetBuildingIdAt"/>; CampusInputController uses both to decide which
    /// click signal to emit.</summary>
    public string GetLandmarkIdAt(Vector2I coord) => _landmarkAtHex.GetValueOrDefault(coord, "");

    /// <summary>The flag-derived phase of the landmark on this hex. Only meaningful
    /// when <see cref="GetLandmarkIdAt"/> returns a non-empty id.</summary>
    public CampusLandmarkData.LandmarkState GetLandmarkStateAt(Vector2I coord) =>
        _landmarkStateAtHex.GetValueOrDefault(coord, CampusLandmarkData.LandmarkState.Ruined);

    /// <summary>
    /// Stamp landmarks from <see cref="CampusLandmarkRegistry"/> onto the grid: state
    /// tint on the tile plus a billboarded two-letter marker. Ported from the retired
    /// 2D CampusHexGrid.LoadLandmarks (858054d), preserving all three of its rules —
    /// clear before restamping, buildings win, and a missing hex is an error rather
    /// than a crash.
    ///
    /// MUST be called after <see cref="LoadFromSave"/>: it reads _buildingAtHex, which
    /// LoadFromSave populates. Safe to call repeatedly — each stamped tile has its
    /// terrain visual reapplied before the tint, so tints never compound.
    ///
    /// <paramref name="hasFlag"/> reads player flag state to derive each landmark's
    /// current phase (ruined / active / restored).
    /// </summary>
    public void LoadLandmarks(System.Func<string, bool> hasFlag)
    {
        // ── Clear: restore the terrain visual on every previously stamped hex, so a
        //    refresh after a narrative beat is clean rather than additive.
        foreach (var coord in _landmarkAtHex.Keys)
        {
            if (!Tiles.TryGetValue(coord, out var stale))
                continue;
            ApplyVisualToTile(stale);          // inherited — resets albedo to terrain
            stale.TileView?.ClearPoiLabel();
        }
        _landmarkAtHex.Clear();
        _landmarkStateAtHex.Clear();

        foreach (var lm in CampusLandmarkRegistry.All)
        {
            var coord = new Vector2I(lm.Q, lm.R);

            if (!Tiles.TryGetValue(coord, out var tile))
            {
                // Canary: authored landmark coordinates and CampusMapSaveData.GenerateDefault's
                // radius-5 disc currently agree (docs/campus_landmarks_3d_v1.md §0). If the
                // generator's radius or shape ever changes, this is what tells you.
                GD.PrintErr($"CampusGridManager: landmark '{lm.Id}' at ({lm.Q},{lm.R}) " +
                            "has no matching hex tile.");
                continue;
            }

            // Landmarks don't override buildings — if a building sits here, the
            // building wins visually (the building IS the restoration).
            if (!string.IsNullOrEmpty(_buildingAtHex.GetValueOrDefault(coord, "")))
                continue;

            var state = lm.State(hasFlag);
            _landmarkAtHex[coord] = lm.Id;
            _landmarkStateAtHex[coord] = state;

            // Reapply terrain first so the lerp below always starts from the terrain
            // colour, never from a previous landmark tint.
            ApplyVisualToTile(tile);

            Color tint = state switch
            {
                CampusLandmarkData.LandmarkState.Active => UITheme.LandmarkTintActive,
                CampusLandmarkData.LandmarkState.Restored => UITheme.LandmarkTintRestored,
                _ => UITheme.LandmarkTintRuined,
            };

            if (tile.TileView != null)
            {
                // Lerp rather than replace — same technique ApplyVisualToTile uses for
                // the spawn-side tints, so a landmark still reads as its ground type.
                tile.TileView.SetBaseColor(
                    tile.TileView.BaseColor.Lerp(tint, UITheme.LandmarkTintStrength));
                tile.TileView.SetPoiLabel(lm.HexLabel, tint);
            }
        }
    }

    private void ClearTiles()
    {
        foreach (var tile in Tiles.Values)
            tile.TileView?.QueueFree();
        Tiles.Clear();
        _buildableMask.Clear();
        _buildingAtHex.Clear();
        _landmarkAtHex.Clear();
        _landmarkStateAtHex.Clear();
    }

    /// <summary>Maps CampusTileSaveData.Ground to a combat TileTerrainType. Colour now
    /// comes entirely from the inherited ApplyVisualToTile (combat's own palette) —
    /// see the class-level note on the Path/Plaza-both-read-as-Stone trade-off.</summary>
    private static TileTerrainType GroundToTerrain(string ground) => ground switch
    {
        "Path" => TileTerrainType.Stone,
        "Plaza" => TileTerrainType.Stone,
        "Pond" => TileTerrainType.Water,
        "Grove" => TileTerrainType.Forest,
        "Rubble" => TileTerrainType.Stone,
        _ => TileTerrainType.Grass, // Lawn
    };

    // ── Building footprints ───────────────────────────────────────────

    private static Vector2I RotateOffset(HexOffset offset, int steps)
    {
        int x = offset.Q, z = offset.R, y = -x - z;
        steps = ((steps % 6) + 6) % 6;
        for (int i = 0; i < steps; i++)
        {
            int nx = -z, ny = -x, nz = -y;
            x = nx; y = ny; z = nz;
        }
        return new Vector2I(x, z);
    }

    public static List<Vector2I> GetFootprintHexes(Building template, Vector2I anchor, int rotation)
    {
        var footprint = (template?.Footprint != null && template.Footprint.Count > 0)
            ? template.Footprint
            : new List<HexOffset> { new HexOffset { Q = 0, R = 0 } };

        var result = new List<Vector2I>(footprint.Count);
        foreach (var offset in footprint)
            result.Add(anchor + RotateOffset(offset, rotation));
        return result;
    }

    /// <summary>Placeholder occupancy tint on top of the tile's real terrain colour —
    /// a campus-only concept (combat has no notion of "a building is here" baked into
    /// ApplyVisualToTile), so this stays a direct SetBaseColor call rather than trying
    /// to route it through the inherited visual method.</summary>
    private static readonly Color BuildingTint = new Color(0.55f, 0.4f, 0.6f);

    private void StampBuilding(string buildingId, List<Vector2I> footprintHexes)
    {
        foreach (var coord in footprintHexes)
        {
            if (!Tiles.TryGetValue(coord, out var tile))
                continue;

            tile.IsBlocked = true;          // footprint is solid — units path around it
            tile.BlocksLineOfSight = true;  // and ranged/targeting treats it as solid
            tile.ObstacleKind = "building:" + buildingId;

            _buildingAtHex[coord] = buildingId;
            tile.TileView?.SetBaseColor(BuildingTint);
        }
    }

    // ── Placement (drag-and-drop target) ─────────────────────────────

    /// <summary>True if every hex in the given footprint exists, is marked buildable,
    /// and is currently unoccupied. Read-only — no side effects.</summary>
    public bool CanPlaceBuilding(Building template, Vector2I anchor, int rotation)
    {
        foreach (var coord in GetFootprintHexes(template, anchor, rotation))
        {
            if (!Tiles.ContainsKey(coord))
                return false;
            if (!_buildableMask.TryGetValue(coord, out bool buildable) || !buildable)
                return false;
            if (!string.IsNullOrEmpty(_buildingAtHex.GetValueOrDefault(coord, "")))
                return false;
        }
        return true;
    }

    /// <summary>Validates and commits placement, mutating the matching BuildingSaveData
    /// (Q/R/Rotation/IsPlaced) in the list the caller passes. Returns false with no
    /// side effects if CanPlaceBuilding fails or the building isn't found / already
    /// placed.</summary>
    public bool PlaceBuilding(string buildingId, Vector2I anchor, int rotation, List<BuildingSaveData> buildings)
    {
        BuildingSaveData target = null;
        foreach (var b in buildings)
        {
            if (b.Id == buildingId) { target = b; break; }
        }
        if (target == null || target.Tier <= 0 || target.IsPlaced)
            return false;

        var template = BuildingDatabase.GetTemplate(buildingId);
        if (!CanPlaceBuilding(template, anchor, rotation))
            return false;

        target.Q = anchor.X;
        target.R = anchor.Y;
        target.Rotation = rotation;
        target.IsPlaced = true;

        StampBuilding(buildingId, GetFootprintHexes(template, anchor, rotation));
        return true;
    }

    /// <summary>Resolves any footprint hex back to its building id. Empty string if
    /// the hex is unoccupied or unknown.</summary>
    public string GetBuildingIdAt(Vector2I coord) => _buildingAtHex.GetValueOrDefault(coord, "");

    // ── Placement preview (drag ghost) ───────────────────────────────

    public void ShowPlacementPreview(Building template, Vector2I anchor, int rotation)
    {
        ClearPlacementPreview();

        bool valid = CanPlaceBuilding(template, anchor, rotation);
        var previewColor = valid ? new Color(0.4f, 0.9f, 0.5f, 0.8f) : new Color(0.9f, 0.3f, 0.3f, 0.8f);

        _previewHexes = GetFootprintHexes(template, anchor, rotation);
        foreach (var coord in _previewHexes)
        {
            if (Tiles.TryGetValue(coord, out var tile))
                tile.TileView?.SetBaseColor(previewColor);
        }
    }

    /// <summary>Restores each previewed hex to its true appearance by re-deriving it
    /// (ApplyVisualToTile from TerrainType, then re-applying the building tint if
    /// occupied) rather than caching colours — simpler now that the real colour comes
    /// from the inherited method instead of code here.</summary>
    public void ClearPlacementPreview()
    {
        foreach (var coord in _previewHexes)
        {
            if (!Tiles.TryGetValue(coord, out var tile))
                continue;

            ApplyVisualToTile(tile);
            string buildingId = GetBuildingIdAt(coord);
            if (!string.IsNullOrEmpty(buildingId))
                tile.TileView?.SetBaseColor(BuildingTint);
        }
        _previewHexes.Clear();
    }
}
