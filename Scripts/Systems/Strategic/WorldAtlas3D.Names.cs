using Godot;
using System.Collections.Generic;
// ============================================================
// WorldAtlas3D.Names.cs: names that keep to the map (2026-11-05)
//
// Purpose:        Section 7h of the provinces spec
//                 (docs/provinces_v1_7h_map_fixes.md), the map fixes
//                 shelved at the end of 7f. Three of them live here.
//                 Fix 3: a seat's name is kept inside the frame's inner
//                 edge on screen, whatever the camera's yaw, so no name
//                 sits on a bar of the frame. Fix 5: a seat with no name
//                 of its own is labelled with its realm's name alone, not
//                 "<realm> Seat" on every capital. Fix 6: the pieces'
//                 names step aside at the whole-world view, where they sat
//                 on the seat's name; the bodies stay, and the names come
//                 back two notches in.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (the settlement pass calls SeatMapName
//                 and FrameLabel; ApplyLabelScale calls KeepInsideFrame;
//                 PlaceCamera and RebuildMarkers call ApplyPieceNameZoom;
//                 RebuildMarkers calls ForgetFramedLabels),
//                 WorldAtlas3D.Frame.cs (FrameInnerRect, FrameTop)
// See:            docs/provinces_v1_7h_map_fixes.md
// ============================================================
public partial class WorldAtlas3D
{
    // ── Tuning (section 7h) ──────────────────────────────────────────────
    /// <summary>A seat's name: 2 percent of the screen's height, the pieces' size (fix 4).
    /// It was 2.8 percent, and one name spanned a seventh of the screen's width.</summary>
    private const float SeatLabelFrac = 0.02f;
    /// <summary>The font size a seat's name is drawn at before it is scaled to the screen.
    /// 64 is sharp at 2 percent of a 2160 pixel screen.</summary>
    private const int SeatLabelFont = 64;
    /// <summary>The gap kept between a framed name and the frame's inner edge, as a share
    /// of the screen's height.</summary>
    private const float FrameNameMarginFrac = 0.006f;
    /// <summary>The pieces' names hide while the camera stands at least this share of the
    /// whole-world distance away: the whole-world view and one notch of the wheel in.</summary>
    private const float PieceNamesHideAt = 0.8f;

    // ── State ────────────────────────────────────────────────────────────
    /// <summary>The names kept inside the frame (the seats').</summary>
    private readonly HashSet<Label3D> _framedLabels = new();
    /// <summary>Each framed name's text size in font pixels, kept while its text and its font
    /// size are unchanged (provinces 7j: a name is drawn at the size it shows at).</summary>
    private readonly Dictionary<Label3D, (string text, int font, Vector2 size)> _framedTextSize = new();
    /// <summary>The pieces' names this pass hid, and only those: the march hides names of its
    /// own, and those are not this pass's to show again.</summary>
    private readonly HashSet<Label3D> _zoomHiddenNames = new();

    // ── Fix 5: a seat's map name ─────────────────────────────────────────

    /// <summary>The label a seat carries on the map. World generation names no settlements
    /// yet, and the fallback "<realm> Seat" put the same word on every capital; on the map
    /// the realm's name alone says it. Elsewhere SettlementDisplayName still adds the kind.</summary>
    private string SeatMapName(WorldSettlement s)
    {
        if (!string.IsNullOrEmpty(s.Name))
            return s.Name;
        if (_kingdoms != null && !string.IsNullOrEmpty(s.KingdomId)
            && _kingdoms.TryGetValue(s.KingdomId, out var k) && k != null && !string.IsNullOrEmpty(k.DisplayName))
            return k.DisplayName;
        return SettlementDisplayName(s);
    }

    // ── Fix 3: names inside the frame ────────────────────────────────────

    /// <summary>Keep this name inside the frame's inner edge from now on, starting now.</summary>
    private void FrameLabel(Label3D lbl)
    {
        if (lbl == null)
            return;
        _framedLabels.Add(lbl);
        for (int i = _scaledLabels.Count - 1; i >= 0; i--)
        {
            var (l, fs, frac, anchor, stackFrac) = _scaledLabels[i];
            if (l == lbl)
            {
                ApplyLabelScale(l, fs, frac, anchor, stackFrac);
                return;
            }
        }
    }

    /// <summary>The marker pass freed every label: forget the framed ones and the hidden ones.</summary>
    private void ForgetFramedLabels()
    {
        _framedLabels.Clear();
        _framedTextSize.Clear();
        _zoomHiddenNames.Clear();
    }

    /// <summary>Place a framed name: at its natural place (its anchor, plus its climb when it
    /// is stacked), then moved on screen by the least amount that puts its box, halo included,
    /// inside the frame's inner edge. The edge is projected as it stands, so a turned camera
    /// gives a turned quadrilateral and the test still holds. A name wider than the whole
    /// frame cannot fit and is left where the passes leave it.</summary>
    private void KeepInsideFrame(Label3D lbl, Vector3 anchor, float stackFrac, float orthoSize)
    {
        Vector3 natural = anchor;
        if (stackFrac > 0f && _camera != null)
            natural = anchor + _camera.GlobalTransform.Basis.Y * (orthoSize * stackFrac);
        lbl.Position = natural;
        if (_camera == null || !_camera.IsInsideTree() || _world == null)
            return;
        Vector2 vp = _camera.GetViewport().GetVisibleRect().Size;
        if (vp.Y < 2f)
            return;
        Rect2 inner = FrameInnerRect();
        if (inner.Size.X <= 0f || inner.Size.Y <= 0f)
            return;

        var edge = new Vector3[]
        {
            ToGlobal(new Vector3(inner.Position.X, FrameTop, inner.Position.Y)),
            ToGlobal(new Vector3(inner.End.X, FrameTop, inner.Position.Y)),
            ToGlobal(new Vector3(inner.End.X, FrameTop, inner.End.Y)),
            ToGlobal(new Vector3(inner.Position.X, FrameTop, inner.End.Y)),
        };
        Vector3 world = ToGlobal(natural);
        if (_camera.IsPositionBehind(world))
            return;
        var quad = new Vector2[4];
        Vector2 centre = Vector2.Zero;
        for (int i = 0; i < 4; i++)
        {
            if (_camera.IsPositionBehind(edge[i]))
                return;
            quad[i] = _camera.UnprojectPosition(edge[i]);
            centre += quad[i] * 0.25f;
        }

        // The name's box on screen: its text and halo at its pixel size, set by its alignment.
        Vector2 at = _camera.UnprojectPosition(world);
        Vector2 text = FramedTextSize(lbl);
        float halo = 2f * lbl.OutlineSize;
        Basis cam = _camera.GlobalTransform.Basis;
        float w = (_camera.UnprojectPosition(world + cam.X * ((text.X + halo) * lbl.PixelSize)) - at).Length();
        float h = (_camera.UnprojectPosition(world + cam.Y * ((text.Y + halo) * lbl.PixelSize)) - at).Length();
        float left = lbl.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => 0f,
            HorizontalAlignment.Right => -w,
            _ => -0.5f * w,
        };
        float top = lbl.VerticalAlignment switch
        {
            VerticalAlignment.Top => 0f,
            VerticalAlignment.Bottom => -h,
            _ => -0.5f * h,
        };
        var corners = new Vector2[]
        {
            new Vector2(left, top), new Vector2(left + w, top),
            new Vector2(left + w, top + h), new Vector2(left, top + h),
        };

        // Push in from each edge in turn; three passes settle a corner, where two edges push.
        float margin = vp.Y * FrameNameMarginFrac;
        Vector2 shift = Vector2.Zero;
        for (int pass = 0; pass < 3; pass++)
        {
            bool pushed = false;
            for (int e = 0; e < 4; e++)
            {
                Vector2 a = quad[e];
                Vector2 b = quad[(e + 1) % 4];
                var n = new Vector2(a.Y - b.Y, b.X - a.X);
                if (n.LengthSquared() < 1e-6f)
                    continue;
                n = n.Normalized();
                if (n.Dot(centre - a) < 0f)
                    n = -n;
                float worst = 0f;
                foreach (var c in corners)
                {
                    float d = n.Dot(at + shift + c - a) - margin;
                    if (d < worst)
                        worst = d;
                }
                if (worst < 0f)
                {
                    shift -= n * worst;
                    pushed = true;
                }
            }
            if (!pushed)
                break;
        }
        if (shift.LengthSquared() < 0.25f)
            return;
        float depth = (world - _camera.GlobalPosition).Dot(-cam.Z);
        lbl.Position = ToLocal(_camera.ProjectPosition(at + shift, depth));
    }

    /// <summary>A framed name's text size in font pixels, measured once per text.</summary>
    private Vector2 FramedTextSize(Label3D lbl)
    {
        string text = lbl.Text ?? "";
        if (_framedTextSize.TryGetValue(lbl, out var cached) && cached.text == text && cached.font == lbl.FontSize)
            return cached.size;
        Font font = lbl.Font ?? ThemeDB.FallbackFont;
        Vector2 size = font != null
            ? font.GetMultilineStringSize(text, HorizontalAlignment.Center, -1f, lbl.FontSize)
            : new Vector2(0.6f * lbl.FontSize * text.Length, lbl.FontSize);
        _framedTextSize[lbl] = (text, lbl.FontSize, size);
        return size;
    }

    // ── Fix 6: the pieces' names at the whole-world view ─────────────────

    /// <summary>Hide the pieces' names while the camera stands near the whole-world
    /// distance, and show again the ones this pass hid once it comes in. A name hidden by
    /// a march stays hidden until the march's own rebuild.</summary>
    private void ApplyPieceNameZoom()
    {
        if (_world == null || _camera == null)
            return;
        bool far = !_cityMode && _camDist >= OverviewDist() * PieceNamesHideAt;
        if (far)
        {
            HidePieceName(_castleLabelNode);
            foreach (var node in _partyLabelNodes)
                HidePieceName(node);
            return;
        }
        if (_zoomHiddenNames.Count == 0 || _marchPlayback != null)
            return;
        foreach (var lbl in _zoomHiddenNames)
        {
            if (IsInstanceValid(lbl))
                lbl.Visible = true;
        }
        _zoomHiddenNames.Clear();
    }

    private void HidePieceName(Node3D node)
    {
        if (node is not Label3D lbl || !IsInstanceValid(lbl) || !lbl.Visible)
            return;
        lbl.Visible = false;
        _zoomHiddenNames.Add(lbl);
    }
}
