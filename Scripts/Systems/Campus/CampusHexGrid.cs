using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CampusHexGrid.cs
//
// Purpose:        Interactive 2D hex map of the guild campus.
//                 Renders the ground layout (CampusMapSaveData)
//                 plus sited buildings (BuildingSaveData.Q/R)
//                 and authored landmarks (CampusLandmarkData),
//                 handles click-to-select and click-to-place.
//                 Built entirely in code; lives inside the
//                 SubViewport created by CampusScreen's campus
//                 tab (560×560, Camera2D at origin, zoom 0.75).
// Layer:          UI
// Collaborators:  CampusScreen.cs (owner — wires TileClicked /
//                 BuildingClicked / LandmarkClicked, drives
//                 build mode + vignette hosting),
//                 CampusMapSaveData.cs (ground layout),
//                 GuildSaveData.cs / BuildingSaveData (siting),
//                 BuildingDatabase.cs (display names),
//                 CampusLandmarkData.cs (landmark registry)
// See:            campus_siege_and_defense_v1_1.docx §4/§5 —
//                 multi-hex footprints + rotation arrive later;
//                 today every building occupies its anchor hex
//                 only (TryPlaceBuilding validates one hex).
// ============================================================

/// <summary>
/// The campus hex map. Flat-top axial layout matching the overworld
/// and combat grids (x = 1.5·s·q, y = √3·s·(r + q/2)), centred on
/// (0,0) so CampusScreen's origin camera frames it. Rebuild from the
/// active save with <see cref="LoadFromSave"/> — safe to call
/// repeatedly, it clears and recreates all tiles.
/// </summary>
public partial class CampusHexGrid : Node2D
{
    // ── Events (plain C# — consumed by CampusScreen) ─────────────────────
    /// <summary>A tile was clicked. In build mode, every click lands here;
    /// otherwise only clicks on empty tiles.</summary>
    public event Action<Vector2I> TileClicked;

    /// <summary>A sited building was clicked (outside build mode).</summary>
    public event Action<string, Vector2I> BuildingClicked;

    /// <summary>A landmark was clicked (outside build mode). Carries the
    /// landmark id and axial coordinate.</summary>
    public event Action<string, Vector2I> LandmarkClicked;

    // ── Layout ───────────────────────────────────────────────────────────
    /// <summary>Hex circumradius in viewport pixels. Radius-5 disc at 40px
    /// spans ~±346px — fits the 560px viewport at the 0.75 camera zoom.</summary>
    private const float HexSize = 40f;

    // ── State ────────────────────────────────────────────────────────────
    private readonly Dictionary<Vector2I, CampusHex> _hexes = new();
    private bool _buildMode;
    private Vector2I _hoveredAxial = new(int.MinValue, int.MinValue);

    // ═══════════════════════════════════════════════════════════════════════
    // Loading
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Clear and rebuild the map from save data: one <see cref="CampusHex"/>
    /// per ground tile, then stamp every placed building onto its anchor hex.
    /// </summary>
    public void LoadFromSave(CampusMapSaveData map, List<BuildingSaveData> buildings)
    {
        foreach (var hex in _hexes.Values)
            hex.QueueFree();
        _hexes.Clear();
        _hoveredAxial = new Vector2I(int.MinValue, int.MinValue);

        if (map == null)
            return;

        foreach (var tile in map.Tiles)
        {
            var axial = new Vector2I(tile.Q, tile.R);
            if (_hexes.ContainsKey(axial))
                continue; // defensive: ignore duplicate coords in the save

            var hex = new CampusHex
            {
                Axial = axial,
                Terrain = tile.Terrain ?? "grass",
                Buildable = tile.Buildable,
                Radius = HexSize,
                Position = AxialToWorld(axial),
            };
            AddChild(hex);
            _hexes[axial] = hex;
        }

        if (buildings != null)
        {
            foreach (var b in buildings)
            {
                if (b == null || !b.IsPlaced || b.Tier <= 0)
                    continue;

                var axial = new Vector2I(b.Q, b.R);
                if (_hexes.TryGetValue(axial, out var hex))
                {
                    hex.BuildingId = b.Id;
                    hex.LabelText = Abbreviate(DisplayName(b));
                    hex.QueueRedraw();
                }
                else
                {
                    GD.PrintErr($"CampusHexGrid: '{b.Id}' is placed at ({b.Q},{b.R}) " +
                                "but no such tile exists — leaving it unsited visually.");
                }
            }
        }

        SetBuildMode(_buildMode); // reapply current mode to the fresh tiles
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Landmarks
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stamp landmarks from the <see cref="CampusLandmarkRegistry"/> onto the
    /// hex grid. Call after <see cref="LoadFromSave"/> (and after each
    /// narrative-encounter completion that may change flag state). The
    /// <paramref name="hasFlag"/> delegate reads the player's flag state to
    /// derive each landmark's current phase (ruined/active/restored).
    /// </summary>
    public void LoadLandmarks(System.Func<string, bool> hasFlag)
    {
        // Clear any existing landmark stamps so a refresh is clean.
        foreach (var hex in _hexes.Values)
        {
            if (!string.IsNullOrEmpty(hex.LandmarkId))
            {
                hex.LandmarkId = "";
                hex.LandmarkState = CampusLandmarkData.LandmarkState.Ruined;
                // Only clear the label if no building occupies the hex.
                if (string.IsNullOrEmpty(hex.BuildingId))
                    hex.LabelText = "";
                hex.QueueRedraw();
            }
        }

        foreach (var lm in CampusLandmarkRegistry.All)
        {
            var axial = new Vector2I(lm.Q, lm.R);
            if (!_hexes.TryGetValue(axial, out var hex))
            {
                GD.PrintErr($"CampusHexGrid: landmark '{lm.Id}' at ({lm.Q},{lm.R}) " +
                            "has no matching hex tile.");
                continue;
            }

            // Landmarks don't override buildings — if a building sits here,
            // the building wins visually (the building IS the restoration).
            if (!string.IsNullOrEmpty(hex.BuildingId))
                continue;

            var state = lm.State(hasFlag);
            hex.LandmarkId = lm.Id;
            hex.LandmarkState = state;
            hex.LabelText = lm.HexLabel;
            hex.QueueRedraw();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Build mode & placement
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Toggle placement mode: buildable empty tiles highlight,
    /// everything else dims. CampusScreen drives this from its
    /// Place-on-Map / Cancel-Placement flow.</summary>
    public void SetBuildMode(bool on)
    {
        _buildMode = on;
        foreach (var hex in _hexes.Values)
        {
            hex.BuildModeActive = on;
            hex.QueueRedraw();
        }
    }

    /// <summary>
    /// Attempt to site a built building on <paramref name="axial"/>.
    /// Validates the tile (exists, buildable, empty) and the building
    /// (known, Tier &gt; 0), writes Q/R/IsPlaced onto its
    /// <see cref="BuildingSaveData"/>, and updates the visuals.
    /// The CALLER saves (CampusScreen calls SaveManager.Save on success).
    /// Single-hex anchors only for now — multi-hex footprints and rotation
    /// arrive with the siege work (design doc §4).
    /// </summary>
    public bool TryPlaceBuilding(string buildingId, Vector2I axial, List<BuildingSaveData> buildings)
    {
        if (string.IsNullOrEmpty(buildingId) || buildings == null)
            return false;

        if (!_hexes.TryGetValue(axial, out var hex))
            return false;
        if (!hex.Buildable || !string.IsNullOrEmpty(hex.BuildingId))
            return false;

        var save = buildings.Find(b => b != null && b.Id == buildingId);
        if (save == null || save.Tier <= 0)
            return false;

        // Re-siting: vacate the previous anchor hex if this building had one.
        if (save.IsPlaced &&
            _hexes.TryGetValue(new Vector2I(save.Q, save.R), out var oldHex) &&
            oldHex.BuildingId == buildingId)
        {
            oldHex.BuildingId = "";
            oldHex.LabelText = "";
            oldHex.QueueRedraw();
        }

        save.Q = axial.X;
        save.R = axial.Y;
        save.IsPlaced = true;

        hex.BuildingId = buildingId;
        hex.LabelText = Abbreviate(DisplayName(save));
        hex.QueueRedraw();
        return true;
    }

    /// <summary>The hex at an axial coordinate, or null.</summary>
    public CampusHex GetHex(Vector2I axial)
        => _hexes.TryGetValue(axial, out var hex) ? hex : null;

    // ═══════════════════════════════════════════════════════════════════════
    // Input (events arrive via the SubViewportContainer forwarding)
    // ═══════════════════════════════════════════════════════════════════════

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion)
        {
            var axial = WorldToAxial(GetGlobalMousePosition());
            if (axial == _hoveredAxial)
                return;

            if (_hexes.TryGetValue(_hoveredAxial, out var prev))
            {
                prev.Hovered = false;
                prev.QueueRedraw();
            }
            _hoveredAxial = axial;
            if (_hexes.TryGetValue(_hoveredAxial, out var next))
            {
                next.Hovered = true;
                next.QueueRedraw();
            }
        }
        else if (@event is InputEventMouseButton mb &&
                 mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            var axial = WorldToAxial(GetGlobalMousePosition());
            if (!_hexes.TryGetValue(axial, out var hex))
                return;

            GetViewport().SetInputAsHandled();

            // In build mode every click is a placement attempt.
            if (_buildMode)
            {
                TileClicked?.Invoke(axial);
                return;
            }

            // Outside build mode: landmarks > buildings > empty tile.
            if (!string.IsNullOrEmpty(hex.LandmarkId))
                LandmarkClicked?.Invoke(hex.LandmarkId, axial);
            else if (!string.IsNullOrEmpty(hex.BuildingId))
                BuildingClicked?.Invoke(hex.BuildingId, axial);
            else
                TileClicked?.Invoke(axial);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hex math — flat-top axial, mirroring OverworldHexGrid at map scale
    // ═══════════════════════════════════════════════════════════════════════

    public static Vector2 AxialToWorld(Vector2I axial)
    {
        float x = HexSize * 1.5f * axial.X;
        float y = HexSize * Mathf.Sqrt(3f) * (axial.Y + axial.X / 2f);
        return new Vector2(x, y);
    }

    public static Vector2I WorldToAxial(Vector2 world)
    {
        float q = (2f / 3f * world.X) / HexSize;
        float r = (-1f / 3f * world.X + Mathf.Sqrt(3f) / 3f * world.Y) / HexSize;
        return AxialRound(q, r);
    }

    private static Vector2I AxialRound(float q, float r)
    {
        float s = -q - r;
        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int rs = Mathf.RoundToInt(s);

        float qDiff = Mathf.Abs(rq - q);
        float rDiff = Mathf.Abs(rr - r);
        float sDiff = Mathf.Abs(rs - s);

        if (qDiff > rDiff && qDiff > sDiff)
            rq = -rr - rs;
        else if (rDiff > sDiff)
            rr = -rq - rs;

        return new Vector2I(rq, rr);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Naming helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static string DisplayName(BuildingSaveData b)
    {
        var template = BuildingDatabase.GetTemplate(b.Id);
        if (template != null && !string.IsNullOrEmpty(template.Name))
            return template.Name;
        return string.IsNullOrEmpty(b.Name) ? b.Id : b.Name;
    }

    /// <summary>"The Grand Hall" → "GH". Placeholder marker text until
    /// buildings get real sprites/models.</summary>
    private static string Abbreviate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "?";

        var initials = new System.Text.StringBuilder();
        foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("the", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("of", StringComparison.OrdinalIgnoreCase))
                continue;
            initials.Append(char.ToUpperInvariant(word[0]));
            if (initials.Length >= 3)
                break;
        }
        return initials.Length > 0 ? initials.ToString() : name[..1].ToUpperInvariant();
    }
}

/// <summary>
/// One hex of the campus map: ground tile visual + (optionally) the
/// building sited on it or a landmark marker. Pure view node —
/// authoritative state lives in CampusMapSaveData (ground),
/// BuildingSaveData (siting), and CampusLandmarkData (landmarks).
/// Later the siege work extends this to "one hex of a multi-hex
/// footprint" where clicking any footprint hex selects the whole
/// building (design doc §5).
/// </summary>
public partial class CampusHex : Node2D
{
    public Vector2I Axial;
    public string Terrain = "grass";
    public bool Buildable = true;

    /// <summary>Empty string = no building sited here.</summary>
    public string BuildingId = "";

    /// <summary>Empty string = no landmark here.</summary>
    public string LandmarkId = "";

    /// <summary>Current landmark state (only meaningful when LandmarkId is set).</summary>
    public CampusLandmarkData.LandmarkState LandmarkState = CampusLandmarkData.LandmarkState.Ruined;

    /// <summary>Short placeholder label drawn on sited buildings ("GH") or
    /// landmarks ("BL", "RF").</summary>
    public string LabelText = "";

    public bool Hovered;
    public bool BuildModeActive;

    public float Radius = 40f;

    // ── Palette (local — deliberately not UITheme-coupled so this file
    //    compiles standalone; migrate to UITheme fields when the campus
    //    gets its art pass) ─────────────────────────────────────────────
    private static readonly Color GrassA        = new(0.28f, 0.42f, 0.24f);
    private static readonly Color GrassB        = new(0.25f, 0.38f, 0.22f);
    private static readonly Color NonBuildable  = new(0.32f, 0.32f, 0.30f);
    private static readonly Color BuildingFill  = new(0.45f, 0.38f, 0.28f);
    private static readonly Color BuildTarget   = new(0.32f, 0.52f, 0.30f);
    private static readonly Color DimmedTint    = new(0.65f, 0.65f, 0.65f);
    private static readonly Color BorderColor   = new(0.10f, 0.12f, 0.10f);
    private static readonly Color HoverBorder   = new(0.92f, 0.88f, 0.70f);
    private static readonly Color LabelColor    = new(0.95f, 0.93f, 0.85f);

    // Landmark-specific palette
    private static readonly Color LandmarkRuined    = new(0.38f, 0.30f, 0.35f); // muted plum-grey — frozen ruin
    private static readonly Color LandmarkActive    = new(0.42f, 0.36f, 0.52f); // soft violet — story waiting
    private static readonly Color LandmarkRestored  = new(0.35f, 0.48f, 0.55f); // calm teal — restored
    private static readonly Color LandmarkBorder    = new(0.60f, 0.55f, 0.70f); // lilac border
    private static readonly Color LandmarkRestoredBorder = new(0.50f, 0.65f, 0.68f); // teal border

    public override void _Draw()
    {
        // Flat-top hexagon corners.
        var points = new Vector2[6];
        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.Pi / 3f * i;
            points[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (Radius - 1.5f);
        }

        bool occupied = !string.IsNullOrEmpty(BuildingId);
        bool isLandmark = !string.IsNullOrEmpty(LandmarkId);

        Color fill;
        Color border = BorderColor;

        if (occupied)
        {
            fill = BuildingFill;
        }
        else if (isLandmark)
        {
            fill = LandmarkState switch
            {
                CampusLandmarkData.LandmarkState.Active => LandmarkActive,
                CampusLandmarkData.LandmarkState.Restored => LandmarkRestored,
                _ => LandmarkRuined,
            };
            border = LandmarkState == CampusLandmarkData.LandmarkState.Restored
                ? LandmarkRestoredBorder
                : LandmarkBorder;
        }
        else if (!Buildable)
        {
            fill = NonBuildable;
        }
        else
        {
            fill = ((Axial.X + Axial.Y) & 1) == 0 ? GrassA : GrassB; // subtle checker
        }

        if (BuildModeActive)
        {
            // Valid placement targets pop; everything else recedes.
            if (Buildable && !occupied && !isLandmark)
                fill = fill.Lerp(BuildTarget, 0.55f);
            else
                fill *= DimmedTint;
        }

        if (Hovered)
            fill = fill.Lightened(0.12f);

        DrawColoredPolygon(points, fill);

        // Border (closed loop).
        var outline = new Vector2[7];
        points.CopyTo(outline, 0);
        outline[6] = points[0];
        float borderWidth = Hovered ? 2.5f : (isLandmark ? 2f : 1.5f);
        DrawPolyline(outline, Hovered ? HoverBorder : border, borderWidth);

        // Landmark marker: small diamond glyph above the label.
        if (isLandmark && LandmarkState == CampusLandmarkData.LandmarkState.Active)
        {
            float ds = 4f;
            var diamond = new Vector2[]
            {
                new(0, -ds - 10f),
                new(ds, -10f),
                new(0, ds - 10f),
                new(-ds, -10f),
            };
            DrawColoredPolygon(diamond, LandmarkBorder);
        }

        // Placeholder label (building abbreviation or landmark hex label).
        if (!string.IsNullOrEmpty(LabelText))
        {
            var font = ThemeDB.FallbackFont;
            DrawString(font, new Vector2(-Radius, 7f), LabelText,
                       HorizontalAlignment.Center, Radius * 2f, 20, LabelColor);
        }
    }
}
