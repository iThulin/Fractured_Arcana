using Godot;
using System.Collections.Generic;

// ============================================================
// WorldAtlas3D.Marks.cs: settlement marks and the names' halo (2026-11-04)
//
// Purpose:        Section 7e of the provinces spec
//                 (docs/provinces_v1_7e_map_polish.md), PR32 and PR33.
//                 At the whole-world view a town's block was the size of
//                 a point-of-interest pip, so every settlement gets a map
//                 mark: a flat square printed on its centre tile, edged in
//                 ink, that keeps its width on screen. A town is a small
//                 paper square, a city a larger blue one, a seat a gold one
//                 with a paper rim, the guild's seat the same in violet.
//                 Squares, so a settlement never reads as a round pip.
//                 While the marks show in full the blocks stand down (they
//                 would sit on the marks); as the camera comes close the
//                 marks fade and the blocks return, and city view hides the
//                 marks. Pieces standing on a settlement draw over its mark.
//                 Every label MakeLabel builds takes a halo scaled to its
//                 glyph, opaque since section 7f (PR38: at 0.9 the halo
//                 read grey once the map was stretched).
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (RebuildMarkers adds a mark beside each
//                 settlement's block and clears the list; PlaceCamera calls
//                 UpdateSettlementMarks; MakeLabel reads the halo),
//                 WorldAtlas3D.Provinces.cs (NameHalo, CurrentZoom01; its
//                 province ink draws before the marks)
// See:            docs/provinces_v1_7e_map_polish.md
// ============================================================

public partial class WorldAtlas3D
{
    // ── Tuning (section 7e) ──────────────────────────────────────────────

    /// <summary>Each mark's width on screen, as a share of the view's height: 6, 9 and 12
    /// pixels on a 1080-pixel view.</summary>
    private const float TownMarkScreenFrac = 6f / 1080f;
    private const float CityMarkScreenFrac = 9f / 1080f;
    private const float SeatMarkScreenFrac = 12f / 1080f;

    /// <summary>The marks show in full from MarkFadeFrom out, where the blocks stand down,
    /// and are gone from MarkGoneAt in. At 0.30 a town's block is about 9 pixels across.</summary>
    private const float MarkFadeFrom = 0.30f;
    private const float MarkGoneAt = 0.20f;

    /// <summary>A mark lies this far above its tile's top, under the pieces.</summary>
    private const float MarkLift = 0.12f;

    /// <summary>Where each mark's ink edge starts, as a share of its half width, and where a
    /// seat's paper rim starts outside the ink.</summary>
    private const float TownInkFrom = 0.45f;
    private const float CityInkFrom = 0.62f;
    private const float SeatInkFrom = 0.50f;
    private const float SeatRimFrom = 0.80f;

    /// <summary>Draw order among the see-through things: the province ink first, then the
    /// marks, then the labels (their outline at -1, their text at 0).</summary>
    private const int MarkPriority = -2;
    private const int ProvinceInkPriority = -3;

    private static readonly Color MarkInk = new Color(0.12f, 0.10f, 0.09f);

    /// <summary>The halo on every map label MakeLabel builds, as a share of its font size:
    /// 26 at 88 points, 10 at 34. It was a flat 10, about a pixel on screen once a seat's
    /// name was drawn at a third of its font size. Opaque since 7f (PR38).</summary>
    private const float LabelHaloShare = 0.30f;
    private static readonly Color LabelHaloInk = new Color(0.06f, 0.05f, 0.05f, 1f);

    // ── State ────────────────────────────────────────────────────────────

    private enum MarkKind { Town, City, Seat, Home }

    private sealed class SettlementMark
    {
        public MeshInstance3D Mark;
        public Node3D Block;
        public float ScreenFrac;
    }

    private readonly List<SettlementMark> _settlementMarks = new();
    private readonly Dictionary<MarkKind, ShaderMaterial> _markMaterials = new();
    private Shader _markShader;
    private PlaneMesh _markPlane;
    private float _marksFadeShown = -1f;

    // A flat square: the fill inside ink_from, the ink out to rim_from, then the rim to the
    // edge (a rim_from of 2 means none). Edges are softened over one pixel. Depth-tested, so
    // the pieces standing on a settlement, and any higher ground, draw over the mark.
    private const string MarkShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, blend_mix;

uniform vec4 mark_fill : source_color = vec4(1.0);
uniform vec4 mark_ink : source_color = vec4(0.12, 0.10, 0.09, 1.0);
uniform vec4 mark_rim : source_color = vec4(0.86, 0.82, 0.72, 1.0);
uniform float ink_from = 0.5;
uniform float rim_from = 2.0;
uniform float fade = 1.0;

void fragment() {
    vec2 p = abs(UV * 2.0 - 1.0);
    float d = max(p.x, p.y);
    float aa = max(fwidth(d), 0.0001);
    vec3 col = mix(mark_fill.rgb, mark_ink.rgb, smoothstep(ink_from - aa, ink_from + aa, d));
    col = mix(col, mark_rim.rgb, smoothstep(rim_from - aa, rim_from + aa, d));
    ALBEDO = col;
    ALPHA = fade * (1.0 - smoothstep(1.0 - aa, 1.0, d));
}
";

    // ── Marks ────────────────────────────────────────────────────────────

    /// <summary>From RebuildMarkers, after each settlement's block and name: its mark,
    /// printed on its centre tile. The node lives in _markers with the rest, so the next
    /// RebuildMarkers frees it.</summary>
    private void AddSettlementMark(int col, int row, bool home, bool seat, bool city, Node3D block)
    {
        MarkKind kind = home ? MarkKind.Home : seat ? MarkKind.Seat : city ? MarkKind.City : MarkKind.Town;
        float frac = kind == MarkKind.Town ? TownMarkScreenFrac
                   : kind == MarkKind.City ? CityMarkScreenFrac
                   : SeatMarkScreenFrac;
        _markPlane ??= new PlaneMesh { Size = Vector2.One };
        var mark = new MeshInstance3D
        {
            Name = "SettlementMark",
            Mesh = _markPlane,
            MaterialOverride = MarkMaterial(kind),
            Position = MarkerPos(col, row, MarkLift),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddMarker(mark);
        var entry = new SettlementMark { Mark = mark, Block = block, ScreenFrac = frac };
        _settlementMarks.Add(entry);
        ShowMark(entry, MarkFade());
    }

    /// <summary>From PlaceCamera as the camera moves: every mark at its width for the zoom,
    /// faded as the camera comes close and hidden in city view, with the blocks standing
    /// down while the marks show in full.</summary>
    private void UpdateSettlementMarks()
    {
        if (_settlementMarks.Count == 0)
            return;
        float fade = MarkFade();
        if (Mathf.Abs(fade - _marksFadeShown) > 0.005f)
        {
            _marksFadeShown = fade;
            foreach (var material in _markMaterials.Values)
                material.SetShaderParameter("fade", fade);
        }
        for (int i = _settlementMarks.Count - 1; i >= 0; i--)
        {
            var entry = _settlementMarks[i];
            if (entry.Mark == null || !IsInstanceValid(entry.Mark))
            {
                _settlementMarks.RemoveAt(i);
                continue;
            }
            ShowMark(entry, fade);
        }
    }

    private void ShowMark(SettlementMark entry, float fade)
    {
        bool show = fade > 0.01f;
        entry.Mark.Visible = show;
        if (show)
        {
            float width = entry.ScreenFrac * _camDist * OrthoSizeFactor;
            entry.Mark.Scale = new Vector3(width, 1f, width);
        }
        // City view keeps its own say over the blocks: the guild's seat hides there.
        if (!_cityMode && entry.Block != null && IsInstanceValid(entry.Block))
            entry.Block.Visible = fade < 0.99f;
    }

    /// <summary>How much of the marks shows now: none in city view, else MarkAlpha.</summary>
    private float MarkFade() => _cityMode ? 0f : MarkAlpha(CurrentZoom01());

    /// <summary>How much of the marks shows at a zoom: all from MarkFadeFrom out, none from
    /// MarkGoneAt in, straight between.</summary>
    internal static float MarkAlpha(float zoom01)
        => Mathf.Clamp((zoom01 - MarkGoneAt) / (MarkFadeFrom - MarkGoneAt), 0f, 1f);

    private ShaderMaterial MarkMaterial(MarkKind kind)
    {
        if (_markMaterials.TryGetValue(kind, out var known))
            return known;
        _markShader ??= new Shader { Code = MarkShaderCode };
        var material = new ShaderMaterial { Shader = _markShader, RenderPriority = MarkPriority };
        Color fill = kind == MarkKind.Home ? UITheme.Violet
                   : kind == MarkKind.Seat ? UITheme.Gold
                   : kind == MarkKind.City ? UITheme.ArcaneBlue
                   : NameHalo;
        float inkFrom = kind == MarkKind.Town ? TownInkFrom
                      : kind == MarkKind.City ? CityInkFrom
                      : SeatInkFrom;
        bool rimmed = kind == MarkKind.Home || kind == MarkKind.Seat;
        material.SetShaderParameter("mark_fill", fill);
        material.SetShaderParameter("mark_ink", MarkInk);
        material.SetShaderParameter("mark_rim", NameHalo);
        material.SetShaderParameter("ink_from", inkFrom);
        material.SetShaderParameter("rim_from", rimmed ? SeatRimFrom : 2f);
        material.SetShaderParameter("fade", MarkFade());
        _markMaterials[kind] = material;
        return material;
    }

    // ── The names' halo ──────────────────────────────────────────────────

    /// <summary>The outline size MakeLabel gives a label of this font size.</summary>
    private static int LabelHaloSize(int fontSize)
        => Mathf.Max(4, Mathf.RoundToInt(fontSize * LabelHaloShare));
}
