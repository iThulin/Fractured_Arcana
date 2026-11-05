using Godot;
using System.Collections.Generic;
using System.Globalization;

// ============================================================
// WorldAtlas3D.Provinces.cs: provinces on every lens (2026-11-03)
//
// Purpose:        Section 7d of the provinces spec
//                 (docs/provinces_v1_7d_lenses.md). Draws the borders
//                 between provinces of one realm as dashed ink on every
//                 lens; keeps every ink (A7's realm ink, the province
//                 ink, the rim) at a least width on screen, as the labels
//                 keep a constant size; names each painted province when
//                 the camera comes close, with the guild's influence
//                 under the name on the Reach lens; and rims the province
//                 the readout shows. The Reach lens's colours (the quiet
//                 base and the bands) stay in WorldAtlas3D.cs beside the
//                 other lens colours, and read the tuning kept here.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (RebuildEdges, the city view toggles,
//                 PlaceCamera, RebuildMarkers and RecolorTiles call in
//                 here; MakeEdgeLayer, MakeLabel, AddMarker, MarkerPos,
//                 TileOrigin, TileHeightAt), ProvinceInfluence.cs
//                 (BandOf), StrategicView.ProvinceHover.cs (calls
//                 HighlightProvince), ProvinceSaveAssert.cs (asserts the
//                 label tile, the name fade and the ink width)
// See:            docs/provinces_v1_7d_lenses.md
// ============================================================

public partial class WorldAtlas3D
{
    // ── Tuning (section 7d) ──────────────────────────────────────────────

    /// <summary>The inks' least widths, as shares of the view's height (the labels'
    /// measure): 3 px, 2 px and 3 px on a 1080-pixel view.</summary>
    private const float RealmInkScreenFrac = 3f / 1080f;
    private const float ProvinceInkScreenFrac = 2f / 1080f;
    private const float RimScreenFrac = 3f / 1080f;

    /// <summary>The inks' painted widths in world units, kept when the camera is close
    /// enough that the least width would be narrower. The realm ink's is A7's.</summary>
    private const float RealmInkWidth = 0.06f;
    private const float ProvinceInkWidth = 0.04f;
    private const float RimWidth = 0.06f;

    /// <summary>A province stroke's share of its hex edge: the gaps fall at the corners,
    /// so the border reads as dashes.</summary>
    private const float ProvinceInkDash = 0.6f;

    /// <summary>The province ink's opacity.</summary>
    private const float ProvinceInkAlpha = 0.8f;

    /// <summary>How far the inks sit above the tile tops, and the rim above the inks.</summary>
    private const float InkLift = 0.012f;
    private const float RimLift = 0.03f;

    /// <summary>zoom01 below which the names start to show, and at which they are whole:
    /// about camera distance 71 and 55, where the default world's whole-world view sits
    /// near 115 (zoom01 0.76).</summary>
    private const float NameFadeFrom = 0.45f;
    private const float NameFullAt = 0.34f;

    /// <summary>A name's height as a share of the view's: about 14 px on a 1080-pixel view,
    /// under the seat labels' 0.028.</summary>
    private const float NameScreenFrac = 0.013f;
    private const int NameFontSize = 24;
    private const int NameOutlineSize = 6;
    private const int NameGlyphSpacing = 3;
    private const float NameLift = 1.2f;

    /// <summary>The Reach lens (section 7d): the quiet base, then the guild's blue in steps
    /// keyed to the rules, and gold for a hold.</summary>
    private const float ReachBaseSaturation = 0.25f;
    private const float ReachBaseToCanvas = 0.28f;
    private const float ReachGrowFrom = 0.26f;
    private const float ReachGrowTo = 0.46f;
    private const float ReachStrong = 0.62f;
    private const float ReachGold = 0.62f;
    private const float ReachEdgeGold = 0.80f;

    private static readonly Color NameInk = new Color(0.17f, 0.14f, 0.12f);
    private static readonly Color NameHalo = new Color(0.86f, 0.82f, 0.72f);
    private static readonly Color NumberHalo = new Color(0.10f, 0.09f, 0.08f);
    private static readonly Color NumberGrowing = new Color(0.80f, 0.88f, 1.00f);
    private static readonly Color NumberUntouched = new Color(0.62f, 0.60f, 0.56f);

    // ── State ────────────────────────────────────────────────────────────

    private MultiMeshInstance3D _provinceInkLayer;
    private MultiMeshInstance3D _rimLayer;
    private int _rimProvince = -1;
    private ShaderMaterial _realmInkMaterial;
    private ShaderMaterial _provinceInkMaterial;
    private ShaderMaterial _rimMaterial;
    private FontVariation _nameFont;
    private readonly List<ProvinceName> _provinceNames = new();

    /// <summary>Each province's label tile, worked out once per rebuild of the world (the
    /// partition and the painted ground change only then) and reused by every
    /// RebuildMarkers after it. Null until needed.</summary>
    private int[] _provinceLabelTiles;
    private float _nameFadeShown = -1f;
    private bool _nameNumbersShown;

    /// <summary>A province's name on the map, and the number under it.</summary>
    private sealed class ProvinceName
    {
        public int Province;
        public Label3D Name;
        public Label3D Number;
        public int Shown = int.MinValue;
    }

    // ── Ink ──────────────────────────────────────────────────────────────

    // One stroke is a thin box lying along its hex edge (MakeEdgeLayer). The shader
    // widens it along its own width axis (local z) by width_scale, which UpdateInkWidths
    // sets from the zoom: the stroke keeps a least width on screen and never shrinks
    // under its painted width. Unshaded, so the ink is the same dark on every slope.
    private const string InkShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled;

uniform vec4 ink : source_color = vec4(0.16, 0.13, 0.12, 1.0);
uniform float width_scale = 1.0;

void vertex() {
    VERTEX.z *= width_scale;
}

void fragment() {
    ALBEDO = ink.rgb;
}
";

    private const string InkAlphaShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, blend_mix;

uniform vec4 ink : source_color = vec4(0.16, 0.13, 0.12, 1.0);
uniform float width_scale = 1.0;

void vertex() {
    VERTEX.z *= width_scale;
}

void fragment() {
    ALBEDO = ink.rgb;
    ALPHA = ink.a;
}
";

    private static ShaderMaterial MakeInkMaterial(Color ink, bool translucent)
    {
        var material = new ShaderMaterial
        {
            Shader = new Shader { Code = translucent ? InkAlphaShaderCode : InkShaderCode },
        };
        material.SetShaderParameter("ink", ink);
        material.SetShaderParameter("width_scale", 1f);
        return material;
    }

    /// <summary>A7's realm ink, kept at its least width on screen. RebuildEdges puts it on
    /// the border layer.</summary>
    private ShaderMaterial RealmInkMaterial()
    {
        _realmInkMaterial ??= MakeInkMaterial(Hex3DPalette.BorderInk, translucent: false);
        return _realmInkMaterial;
    }

    /// <summary>The province ink, from RebuildEdges after the realm ink: one dashed stroke
    /// on each hex edge between two painted land tiles of the same kingdom that lie in
    /// different provinces (a tile in none counts as different). Edges between kingdoms
    /// are the realm ink's, so no edge is inked twice. Hidden in city view.</summary>
    private void RebuildProvinceInk()
    {
        _provinceLabelTiles = null;   // the painted ground may have changed: names find their tiles again
        if (_provinceInkLayer != null && IsInstanceValid(_provinceInkLayer))
            _provinceInkLayer.QueueFree();
        _provinceInkLayer = null;
        if (_world != null && _world.HasProvinces())
        {
            var strokes = new List<Transform3D>();
            for (int row = 0; row < _world.Height; row++)
            for (int col = 0; col < _world.Width; col++)
            {
                var t = _world.GetTile(col, row);
                if (!t.IsLand || !Painted(col, row))
                    continue;
                int pi = _world.ProvinceIndexAt(col, row);
                var (q, r) = HexCoord.OffsetToAxial(col, row);
                // Each edge once: directions 0 to 2 here, the mirror is the neighbour's 3 to 5.
                for (int i = 0; i < 3; i++)
                {
                    var (dq, dr) = HexCoord.AxialDirections[i];
                    var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                    if (!_world.InBounds(nc, nr))
                        continue;
                    var n = _world.GetTile(nc, nr);
                    if (!n.IsLand || !Painted(nc, nr) || n.KingdomId != t.KingdomId)
                        continue;
                    if (_world.ProvinceIndexAt(nc, nr) == pi)
                        continue;
                    strokes.Add(EdgeStroke(col, row, nc, nr, InkLift));
                }
            }
            if (strokes.Count > 0)
            {
                _provinceInkMaterial ??= MakeInkMaterial(new Color(Hex3DPalette.BorderInk.R, Hex3DPalette.BorderInk.G,
                                                                   Hex3DPalette.BorderInk.B, ProvinceInkAlpha), translucent: true);
                _provinceInkLayer = MakeEdgeLayer("ProvinceInkLayer", strokes,
                    new Vector3(HexR * ProvinceInkDash, 0.02f, ProvinceInkWidth), Hex3DPalette.BorderInk);
                _provinceInkLayer.MaterialOverride = _provinceInkMaterial;
                _provinceInkLayer.Visible = !_cityMode;
            }
        }
        UpdateInkWidths();
    }

    /// <summary>A stroke lying along the edge between a tile and its neighbour, which may
    /// lie off the map (the rim at the map's border): at the edge's middle, on the higher
    /// of the two tiles, plus a lift.</summary>
    private Transform3D EdgeStroke(int col, int row, int nc, int nr, float lift)
    {
        Vector3 a = TileOrigin(col, row);
        Vector3 b = TileOrigin(nc, nr);
        Vector3 mid = (a + b) * 0.5f;
        float height = TileHeightAt(col, row);
        if (_world.InBounds(nc, nr))
            height = Mathf.Max(height, TileHeightAt(nc, nr));
        mid.Y = height + lift;
        Vector3 along = (b - a).Normalized();
        Vector3 perp = new Vector3(-along.Z, 0f, along.X);
        return new Transform3D(new Basis(Vector3.Up, Mathf.Atan2(-perp.Z, perp.X)), mid);
    }

    /// <summary>The world width an ink draws at: its least width on screen at this camera
    /// distance, or its painted width when that is wider. The view's height in world units
    /// is camDist x OrthoSizeFactor, as ApplyLabelScale has it.</summary>
    internal static float InkWorldWidth(float camDist, float screenFrac, float paintedWidth)
        => Mathf.Max(paintedWidth, screenFrac * camDist * OrthoSizeFactor);

    /// <summary>From PlaceCamera, and whenever an ink layer is built: every ink's width for
    /// the current zoom.</summary>
    private void UpdateInkWidths()
    {
        SetInkWidth(_realmInkMaterial, RealmInkScreenFrac, RealmInkWidth);
        SetInkWidth(_provinceInkMaterial, ProvinceInkScreenFrac, ProvinceInkWidth);
        SetInkWidth(_rimMaterial, RimScreenFrac, RimWidth);
    }

    private void SetInkWidth(ShaderMaterial material, float screenFrac, float paintedWidth)
    {
        if (material == null)
            return;
        material.SetShaderParameter("width_scale", InkWorldWidth(_camDist, screenFrac, paintedWidth) / paintedWidth);
    }

    private bool Painted(int col, int row)
        => _revealAll || _world.GetTile(col, row).Discovery != TileDiscovery.Unseen;

    /// <summary>City view hides the province ink and the rim, and the names with them
    /// (UpdateProvinceNameFade reads _cityMode). From EnterCityMode and LeaveCityMode,
    /// beside A7's ink.</summary>
    private void SetProvinceLayersForCity(bool city)
    {
        if (_provinceInkLayer != null && IsInstanceValid(_provinceInkLayer))
            _provinceInkLayer.Visible = !city;
        if (city)
            HighlightProvince(-1);
        UpdateProvinceNameFade(CurrentZoom01(), force: true);
    }

    // ── The rim ──────────────────────────────────────────────────────────

    /// <summary>Rim a province's painted outline over every lens (its coast and the map's
    /// edge included), or clear the rim with -1. The province readout calls this as the
    /// pointer moves; nothing is rebuilt while the province stays the same.</summary>
    public void HighlightProvince(int index)
    {
        if (index == _rimProvince)
            return;
        if (_rimLayer != null && IsInstanceValid(_rimLayer))
            _rimLayer.QueueFree();
        _rimLayer = null;
        _rimProvince = -1;
        if (index < 0 || _cityMode || _world == null || !_world.HasProvinces() || index >= _world.Provinces.Count)
            return;
        var strokes = new List<Transform3D>();
        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            if (_world.ProvinceIndexAt(col, row) != index || !Painted(col, row))
                continue;
            var (q, r) = HexCoord.OffsetToAxial(col, row);
            for (int i = 0; i < 6; i++)
            {
                var (dq, dr) = HexCoord.AxialDirections[i];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                if (_world.InBounds(nc, nr) && _world.ProvinceIndexAt(nc, nr) == index)
                    continue;
                strokes.Add(EdgeStroke(col, row, nc, nr, RimLift));
            }
        }
        if (strokes.Count == 0)
            return;
        _rimMaterial ??= MakeInkMaterial(UITheme.TextPrimary, translucent: false);
        _rimLayer = MakeEdgeLayer("ProvinceRim", strokes,
            new Vector3(HexR * 1.04f, 0.02f, RimWidth), UITheme.TextPrimary);
        _rimLayer.MaterialOverride = _rimMaterial;
        _rimProvince = index;
        UpdateInkWidths();
    }

    // ── Names ────────────────────────────────────────────────────────────

    /// <summary>From RebuildMarkers, before the pieces: a name, and a number for the Reach
    /// lens, for each province with painted ground. Their nodes live in _markers with the
    /// rest, so the next RebuildMarkers frees them.</summary>
    private void AddProvinceNames()
    {
        _provinceNames.Clear();
        if (_world == null || !_world.HasProvinces())
            return;
        _nameFont ??= new FontVariation { SpacingGlyph = NameGlyphSpacing };
        if (_provinceLabelTiles == null || _provinceLabelTiles.Length != _world.Provinces.Count)
        {
            _provinceLabelTiles = new int[_world.Provinces.Count];
            for (int pi = 0; pi < _provinceLabelTiles.Length; pi++)
                _provinceLabelTiles[pi] = ProvinceLabelTile(_world, pi, _revealAll);
        }
        for (int pi = 0; pi < _world.Provinces.Count; pi++)
        {
            int tile = _provinceLabelTiles[pi];
            if (tile < 0)
                continue;
            int col = tile % _world.Width, row = tile / _world.Width;
            Vector3 anchor = MarkerPos(col, row, NameLift);
            var province = _world.Provinces[pi];
            string text = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
            // The number sits on the anchor and the name one line above it, so on the Reach
            // lens the number reads under the name.
            var name = MakeLabel(text.ToUpperInvariant(), NameInk, anchor, NameFontSize, NameScreenFrac,
                                 NameScreenFrac * LabelLineFactor);
            name.Font = _nameFont;
            name.OutlineSize = NameOutlineSize;
            name.OutlineModulate = NameHalo;
            var number = MakeLabel("", NumberUntouched, anchor, NameFontSize, NameScreenFrac);
            number.OutlineSize = NameOutlineSize;
            number.OutlineModulate = NumberHalo;
            AddMarker(name);
            AddMarker(number);
            _provinceNames.Add(new ProvinceName { Province = pi, Name = name, Number = number });
        }
        RefreshProvinceNames();
    }

    /// <summary>From RecolorTiles, so a lens change or a recolour carries the names along:
    /// the numbers brought up to date, then the fade and the lens applied.</summary>
    private void RefreshProvinceNames()
    {
        if (_provinceNames.Count == 0 || _world == null)
            return;
        foreach (var entry in _provinceNames)
        {
            if (!IsInstanceValid(entry.Number) || entry.Province >= _world.Provinces.Count)
                continue;
            int value = _world.Provinces[entry.Province].Influence;
            if (value != entry.Shown)
            {
                entry.Number.Text = value.ToString(CultureInfo.InvariantCulture);
                entry.Shown = value;
            }
        }
        UpdateProvinceNameFade(CurrentZoom01(), force: true);
    }

    /// <summary>From PlaceCamera as the camera moves: names fade in as it comes down and are
    /// hidden in city view; numbers show on the Reach lens only, in their band's colour.
    /// Does nothing while neither the fade nor the lens has changed, unless forced.</summary>
    private void UpdateProvinceNameFade(float zoom01, bool force = false)
    {
        if (_provinceNames.Count == 0 || _world == null)
            return;
        float alpha = _cityMode ? 0f : NameAlpha(zoom01);
        bool show = alpha > 0.01f;
        bool numbers = show && _lens == StrategicLens.Reach;
        if (!force && Mathf.Abs(alpha - _nameFadeShown) < 0.005f && numbers == _nameNumbersShown)
            return;
        _nameFadeShown = alpha;
        _nameNumbersShown = numbers;
        foreach (var entry in _provinceNames)
        {
            if (!IsInstanceValid(entry.Name) || !IsInstanceValid(entry.Number))
                continue;
            entry.Name.Visible = show;
            entry.Number.Visible = numbers;
            if (show)
            {
                entry.Name.Modulate = WithAlpha(NameInk, alpha);
                entry.Name.OutlineModulate = WithAlpha(NameHalo, alpha);
            }
            if (numbers && entry.Province < _world.Provinces.Count)
            {
                entry.Number.Modulate = WithAlpha(NumberTint(_world.Provinces[entry.Province]), alpha);
                entry.Number.OutlineModulate = WithAlpha(NumberHalo, alpha);
            }
        }
    }

    /// <summary>How much of the names shows at a zoom: none from NameFadeFrom out, all from
    /// NameFullAt in, straight between.</summary>
    internal static float NameAlpha(float zoom01)
        => Mathf.Clamp((NameFadeFrom - zoom01) / (NameFadeFrom - NameFullAt), 0f, 1f);

    private float CurrentZoom01() => Mathf.InverseLerp(CamDistMin, CamDistMax, _camDist);

    private static Color WithAlpha(Color c, float alpha) => new Color(c.R, c.G, c.B, alpha);

    /// <summary>A number's colour: gold when held, grey at nothing, light blue otherwise.</summary>
    private static Color NumberTint(Province province)
    {
        switch (ProvinceInfluence.BandOf(province))
        {
            case ProvinceInfluence.Band.Held:
            case ProvinceInfluence.Band.HeldSlipping:
                return UITheme.Gold;
            case ProvinceInfluence.Band.Untouched:
                return NumberUntouched;
            default:
                return NumberGrowing;
        }
    }

    /// <summary>Where a province's name goes (section 7d): the most interior of its painted
    /// tiles. The region is the province's painted tiles (all of them with revealAll). A
    /// region tile with a neighbour outside the region, or off the map, is on its edge; the
    /// tile the most hex steps in from the edge wins, then the one nearest the region's
    /// centroid as drawn, then the lower index. Settlement tiles are passed over while the
    /// region has others, so a name does not sit on a seat's label. Returns row x Width +
    /// col, or -1 when nothing of the province is painted. Static, so the asserts can run
    /// it on a scratch world.</summary>
    internal static int ProvinceLabelTile(WorldData world, int provinceIndex, bool revealAll)
    {
        if (world == null || !world.HasProvinces() || provinceIndex < 0 || provinceIndex >= world.Provinces.Count)
            return -1;
        int w = world.Width, h = world.Height;
        var inRegion = new bool[w * h];
        var region = new List<int>();
        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
        {
            if (world.ProvinceIndexAt(col, row) != provinceIndex)
                continue;
            if (!revealAll && world.GetTile(col, row).Discovery == TileDiscovery.Unseen)
                continue;
            int i = row * w + col;
            inRegion[i] = true;
            region.Add(i);
        }
        if (region.Count == 0)
            return -1;

        // Steps in from the region's edge, by a breadth-first walk inward.
        var depth = new int[w * h];
        var queue = new Queue<int>();
        foreach (int i in region)
        {
            if (OnRegionEdge(world, inRegion, i))
            {
                queue.Enqueue(i);
            }
            else
            {
                depth[i] = -1;
            }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            var (q, r) = HexCoord.OffsetToAxial(i % w, i / w);
            for (int d = 0; d < 6; d++)
            {
                var (dq, dr) = HexCoord.AxialDirections[d];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                if (!world.InBounds(nc, nr))
                    continue;
                int n = nr * w + nc;
                if (inRegion[n] && depth[n] < 0)
                {
                    depth[n] = depth[i] + 1;
                    queue.Enqueue(n);
                }
            }
        }

        float cx = 0f, cz = 0f;
        bool anyOpen = false;
        foreach (int i in region)
        {
            Vector2 p = DrawnXZ(i % w, i / w);
            cx += p.X;
            cz += p.Y;
            if (world.Tiles[i].SettlementIndex < 0)
                anyOpen = true;
        }
        cx /= region.Count;
        cz /= region.Count;

        int best = -1, bestDepth = int.MinValue;
        float bestDistance = float.MaxValue;
        foreach (int i in region)
        {
            if (anyOpen && world.Tiles[i].SettlementIndex >= 0)
                continue;
            Vector2 p = DrawnXZ(i % w, i / w);
            float distance = (p.X - cx) * (p.X - cx) + (p.Y - cz) * (p.Y - cz);
            if (depth[i] > bestDepth || (depth[i] == bestDepth && distance < bestDistance))
            {
                best = i;
                bestDepth = depth[i];
                bestDistance = distance;
            }
        }
        return best;
    }

    private static bool OnRegionEdge(WorldData world, bool[] inRegion, int i)
    {
        int w = world.Width;
        var (q, r) = HexCoord.OffsetToAxial(i % w, i / w);
        for (int d = 0; d < 6; d++)
        {
            var (dq, dr) = HexCoord.AxialDirections[d];
            var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
            if (!world.InBounds(nc, nr) || !inRegion[nr * w + nc])
                return true;
        }
        return false;
    }

    /// <summary>A tile's centre on the ground plane as drawn (TileOrigin's x and z).</summary>
    private static Vector2 DrawnXZ(int col, int row)
        => new Vector2(col * ColSpacing, row * RowSpacing + (((col & 1) == 1) ? RowSpacing * 0.5f : 0f));
}
