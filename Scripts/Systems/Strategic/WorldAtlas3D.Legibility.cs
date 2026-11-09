using Godot;
using System.Collections.Generic;

// ============================================================
// WorldAtlas3D.Legibility.cs: the map read close in (2026-11-07)
//
// Purpose:        Section 7j of the provinces spec
//                 (docs/provinces_v1_7j_test_fixes.md), the map fixes from
//                 the first play of the provinces build-out.
//                 Fix 1: every name the map draws is rasterized at the size
//                 it shows at and sampled without mipmaps. Drawn at 64 and
//                 shown near 45 pixels, a seat's name sampled the font
//                 atlas's smaller mips, which bled the glyphs packed beside
//                 each letter into it ("The Cagwerk Reach").
//                 Fix 2: the realm ink and the province rim climb the
//                 relief. Where two strokes meet at a corner at different
//                 heights, a short upright joins them, so a border reads as
//                 one line over a cliff instead of breaking at it.
//                 Fix 3: faint seams between land tiles of one height, shown
//                 only as the camera comes close, so neighbours of the same
//                 colour read as separate hexes.
//                 Fix 4: a city's footprint wears no trees or spires, and a
//                 road steps over relief with a riser at each step.
//                 Fix 5: the orthographic camera stands well back, so its
//                 near plane never cuts the tiles at the foot of the screen
//                 when the view comes close.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (ApplyLabelScale calls ShownFontSize and
//                 ResizeLabelFont; RebuildEdges notes the realm ink's
//                 corners, adds their posts, adds the road risers and calls
//                 RebuildSeams; RebuildDecorations asks IsCityGround;
//                 PlaceCamera calls CameraStandOff), WorldAtlas3D.Provinces.cs
//                 (HighlightProvince notes the rim's corners and adds their
//                 posts; UpdateInkWidths calls UpdateSeams;
//                 SetProvinceLayersForCity hides the seams),
//                 WorldAtlas3D.Names.cs (FramedTextSize keys on the size)
// See:            docs/provinces_v1_7j_test_fixes.md
// ============================================================

public partial class WorldAtlas3D
{
    // ── Tuning (section 7j) ──────────────────────────────────────────────

    /// <summary>The font sizes a name may be drawn at, in pixels.</summary>
    private const int ShownFontMin = 8;
    private const int ShownFontMax = 200;

    /// <summary>A height step smaller than this needs no post and no riser.</summary>
    private const float ReliefStepMin = 0.01f;

    /// <summary>The road stroke's box is this thick (RebuildEdges builds it); a riser is
    /// scaled from it to RoadRiserDepth, as deep as the road is wide, so it reads from any
    /// side.</summary>
    private const float RoadStrokeThickness = 0.025f;
    private const float RoadRiserDepth = 0.14f;

    /// <summary>The seams: their opacity when whole, the zoom01 at which they start to show
    /// and the one at which they are whole (about camera distance 49 and 32), their painted
    /// width, their least width on screen (1 px on a 1080-pixel view), their share of the
    /// hex edge, and how far above the tile tops they sit.</summary>
    private const float SeamAlpha = 0.24f;
    private const float SeamFadeFrom = 0.30f;
    private const float SeamFullAt = 0.18f;
    private const float SeamWidth = 0.02f;
    private const float SeamScreenFrac = 1f / 1080f;
    private const float SeamDash = 0.92f;
    private const float SeamLift = 0.008f;

    /// <summary>The seams draw under every ink and mark.</summary>
    private const int SeamPriority = -4;

    /// <summary>How far back the orthographic camera stands at the least. At the closest
    /// zoom and the automatic pitch its near plane then clears the tallest tiles at the foot
    /// of the screen with room to spare; the view is the same from any distance.</summary>
    private const float OrthoStandOff = 30f;

    // ── State ────────────────────────────────────────────────────────────

    private MultiMeshInstance3D _seamLayer;
    private ShaderMaterial _seamMaterial;
    private float _seamAlphaShown = -1f;

    /// <summary>A corner where ink strokes meet, and the lowest and highest of them.</summary>
    private sealed class CornerSpan
    {
        public Vector3 At;
        public float Lo;
        public float Hi;
    }

    // ── Fix 1: names at their own size ───────────────────────────────────

    /// <summary>The font size, in pixels, that a name of this share of the screen's height
    /// shows at on the atlas's viewport; the fallback before the viewport can be measured.</summary>
    private int ShownFontSize(float screenFrac, int fallback)
    {
        float height = _camera != null && _camera.IsInsideTree() ? _camera.GetViewport().GetVisibleRect().Size.Y : 0f;
        if (height < 2f || screenFrac <= 0f)
            return fallback;
        return Mathf.Clamp(Mathf.RoundToInt(screenFrac * height), ShownFontMin, ShownFontMax);
    }

    /// <summary>Draw a label at a new font size, its halo kept at the same share of the glyph.</summary>
    private static void ResizeLabelFont(Label3D lbl, int size)
    {
        int old = Mathf.Max(1, lbl.FontSize);
        if (lbl.OutlineSize > 0)
            lbl.OutlineSize = Mathf.Max(1, Mathf.RoundToInt(lbl.OutlineSize * (float)size / old));
        lbl.FontSize = size;
    }

    // ── Fix 2: ink over relief ───────────────────────────────────────────

    /// <summary>Note the two corners of the hex edge between a tile and its neighbour (which
    /// may lie off the map) for a stroke on the higher of the two tiles plus a lift, where
    /// EdgeStroke and the realm ink lay theirs.</summary>
    private void NoteEdgeCorners(Dictionary<(int, int), CornerSpan> corners, int col, int row, int nc, int nr, float lift)
    {
        float height = TileHeightAt(col, row);
        if (_world.InBounds(nc, nr))
            height = Mathf.Max(height, TileHeightAt(nc, nr));
        height += lift;
        Vector3 a = TileOrigin(col, row);
        Vector3 b = TileOrigin(nc, nr);
        Vector3 mid = (a + b) * 0.5f;
        Vector3 along = new Vector3(b.X - a.X, 0f, b.Z - a.Z).Normalized();
        Vector3 side = new Vector3(-along.Z, 0f, along.X) * (HexR * 0.5f);
        NoteCorner(corners, mid + side, height);
        NoteCorner(corners, mid - side, height);
    }

    private static void NoteCorner(Dictionary<(int, int), CornerSpan> corners, Vector3 at, float height)
    {
        var key = (Mathf.RoundToInt(at.X * 1000f), Mathf.RoundToInt(at.Z * 1000f));
        if (corners.TryGetValue(key, out var span))
        {
            span.Lo = Mathf.Min(span.Lo, height);
            span.Hi = Mathf.Max(span.Hi, height);
            return;
        }
        corners[key] = new CornerSpan { At = new Vector3(at.X, 0f, at.Z), Lo = height, Hi = height };
    }

    /// <summary>A post at every corner where strokes meet at different heights: two crossed
    /// upright strokes of the layer's own box (meshLength long along its local x), so the
    /// post reads from any side and widens on screen with the ink.</summary>
    private static void AddCornerPosts(List<Transform3D> strokes, Dictionary<(int, int), CornerSpan> corners, float meshLength)
    {
        foreach (var span in corners.Values)
        {
            float rise = span.Hi - span.Lo;
            if (rise <= ReliefStepMin)
                continue;
            var at = new Vector3(span.At.X, (span.Lo + span.Hi) * 0.5f, span.At.Z);
            Vector3 up = Vector3.Up * (rise / meshLength);
            foreach (var face in new[] { Vector3.Right, Vector3.Back })
                strokes.Add(new Transform3D(new Basis(up, face.Cross(Vector3.Up).Normalized(), face), at));
        }
    }

    // ── Fix 3: seams ─────────────────────────────────────────────────────

    /// <summary>From RebuildEdges: a faint stroke on each hex edge between two painted land
    /// tiles of one height, where no border is inked and neither tile lies in a city. Shown
    /// only close in, and never in city view.</summary>
    private void RebuildSeams()
    {
        if (_seamLayer != null && IsInstanceValid(_seamLayer))
            _seamLayer.QueueFree();
        _seamLayer = null;
        _seamAlphaShown = -1f;
        if (_world == null)
            return;
        bool provinces = _world.HasProvinces();
        var strokes = new List<Transform3D>();
        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            var t = _world.GetTile(col, row);
            if (!t.IsLand || !Painted(col, row) || IsCityGround(t, col, row))
                continue;
            float height = TileHeightAt(col, row);
            var (q, r) = HexCoord.OffsetToAxial(col, row);
            // Each edge once: directions 0 to 2 here, the mirror is the neighbour's 3 to 5.
            for (int i = 0; i < 3; i++)
            {
                var (dq, dr) = HexCoord.AxialDirections[i];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                if (!_world.InBounds(nc, nr))
                    continue;
                var n = _world.GetTile(nc, nr);
                if (!n.IsLand || !Painted(nc, nr) || IsCityGround(n, nc, nr))
                    continue;
                if (Mathf.Abs(TileHeightAt(nc, nr) - height) > ReliefStepMin)
                    continue;   // a step shows the edge already
                if (RulerKeyAt(n, nc, nr) != RulerKeyAt(t, col, row))
                    continue;   // the realm ink is drawn here
                if (provinces && _world.ProvinceIndexAt(nc, nr) != _world.ProvinceIndexAt(col, row))
                    continue;   // the province ink is drawn here
                strokes.Add(EdgeStroke(col, row, nc, nr, SeamLift));
            }
        }
        if (strokes.Count == 0)
            return;
        var ink = Hex3DPalette.BorderInk;
        _seamMaterial ??= MakeInkMaterial(new Color(ink.R, ink.G, ink.B, 0f), translucent: true);
        _seamMaterial.RenderPriority = SeamPriority;
        _seamLayer = MakeEdgeLayer("SeamLayer", strokes, new Vector3(HexR * SeamDash, 0.01f, SeamWidth), ink);
        _seamLayer.MaterialOverride = _seamMaterial;
        _seamLayer.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        UpdateSeams();
    }

    /// <summary>From UpdateInkWidths, whenever the camera moves or an ink is built: the seams'
    /// opacity for the zoom, their least width on screen, and whether they draw at all.</summary>
    private void UpdateSeams()
    {
        if (_seamLayer == null || !IsInstanceValid(_seamLayer) || _seamMaterial == null)
            return;
        float alpha = _cityMode ? 0f : SeamAlpha * SeamShow(CurrentZoom01());
        _seamLayer.Visible = alpha > 0.005f;
        if (Mathf.Abs(alpha - _seamAlphaShown) > 0.004f)
        {
            _seamAlphaShown = alpha;
            var ink = Hex3DPalette.BorderInk;
            _seamMaterial.SetShaderParameter("ink", new Color(ink.R, ink.G, ink.B, alpha));
        }
        SetInkWidth(_seamMaterial, SeamScreenFrac, SeamWidth);
    }

    /// <summary>How much of the seams shows at a zoom: none from SeamFadeFrom out, all from
    /// SeamFullAt in, straight between.</summary>
    internal static float SeamShow(float zoom01)
        => Mathf.Clamp((SeamFadeFrom - zoom01) / (SeamFadeFrom - SeamFullAt), 0f, 1f);

    // ── Fix 4: a city's ground, and roads over relief ────────────────────

    /// <summary>True on a city's footprint (the grey region TileColor paints) and on the
    /// guild's own city: streets, where no tree or spire stands and no seam is drawn.</summary>
    private bool IsCityGround(in WorldTile t, int col, int row)
        => _cityTiles.Contains(new Vector2I(col, row))
           || (t.SettlementIndex >= 0 && t.SettlementIndex < _world.Settlements.Count
               && _world.Settlements[t.SettlementIndex].Tier == SettlementTier.City);

    /// <summary>A road's riser at a step: an upright post at the shared edge, from the lower
    /// tile's top to the higher tile's, as wide as the road and as deep.</summary>
    private static Transform3D RoadRiser(Vector3 top, float bottomY, Vector3 widthAxis)
    {
        float rise = Mathf.Max(ReliefStepMin, top.Y - bottomY);
        var at = new Vector3(top.X, (top.Y + bottomY) * 0.5f, top.Z);
        Vector3 depth = widthAxis.Cross(Vector3.Up).Normalized() * (RoadRiserDepth / RoadStrokeThickness);
        return new Transform3D(new Basis(Vector3.Up * rise, depth, widthAxis), at);
    }

    // ── Fix 5: the camera stands back ────────────────────────────────────

    /// <summary>How far from its target the camera stands. An orthographic view is the same
    /// from any distance along its line of sight, so it stands at least OrthoStandOff back
    /// and its near plane never cuts the tiles at the foot of the screen; the zoom is still
    /// the ortho size, set from _camDist. The perspective diorama stands at _camDist.</summary>
    private float CameraStandOff() => _orthographic ? Mathf.Max(_camDist, OrthoStandOff) : _camDist;
}
