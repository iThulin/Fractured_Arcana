using Godot;

// ============================================================
// WorldAtlas3D.Frame.cs: the map's frame (2026-11-04)
//
// Purpose:        Section 7e of the provinces spec
//                 (docs/provinces_v1_7e_map_polish.md), PR31. The
//                 whole-world view fits the world into the part of the
//                 screen the host's panels leave free, with the camera's
//                 pitch counted, and the camera's centre of view sits in
//                 the middle of that free screen at every zoom. Under the
//                 world lies one sheet of the canvas material, so past the
//                 map's edge the view shows the paper the unexplored
//                 ground is painted on, not a dark void.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (OverviewDist calls FitWorldDist;
//                 PlaceCamera calls PitchDegFor and ApplyViewCentre;
//                 RebuildTiles calls RebuildPaperSheet; the environment
//                 takes PaperVoid), StrategicView.Dispatch.cs (calls
//                 SetViewInsets), PainterlyPrism (the canvas material)
// See:            docs/provinces_v1_7e_map_polish.md
// ============================================================

public partial class WorldAtlas3D
{
    // ── Tuning (section 7e) ──────────────────────────────────────────────

    /// <summary>Margin around the world in the whole-world view, as a share of the fitted
    /// size: the 4 percent the fit always had.</summary>
    private const float OverviewPad = 1.04f;

    /// <summary>The paper sheet's half width in world units: far past anything the camera
    /// shows before its far plane cuts in.</summary>
    private const float PaperSheetHalf = 3000f;

    /// <summary>The sheet lies just under the tiles' feet (every prism stands on y 0), well
    /// clear of the lowest tile top (the canvas slab, 0.06), so the two never fight for
    /// depth.</summary>
    private const float PaperSheetY = -0.01f;

    /// <summary>The background past the far plane: an estimate of the sheet as lit, so a view
    /// that reaches that far ends in paper, not a dark band. Tune by eye.</summary>
    private static readonly Color PaperVoid = new Color(0.70f, 0.64f, 0.52f);

    // ── State ────────────────────────────────────────────────────────────

    /// <summary>The screen the host's panels cover, as shares of the view: left, top, right
    /// and bottom. Zero until the host says (SetViewInsets).</summary>
    private float _insetLeft, _insetTop, _insetRight, _insetBottom;

    private MultiMeshInstance3D _paperSheet;

    // ── The free screen ──────────────────────────────────────────────────

    /// <summary>The host's panels, as shares of the screen (each held to 0.45 at most). The
    /// camera's centre of view moves to the middle of what they leave free, and the
    /// whole-world view fits the world inside it. Re-places the camera at once; the zoom
    /// is left alone.</summary>
    public void SetViewInsets(float left, float top, float right, float bottom)
    {
        _insetLeft = Mathf.Clamp(left, 0f, 0.45f);
        _insetTop = Mathf.Clamp(top, 0f, 0.45f);
        _insetRight = Mathf.Clamp(right, 0f, 0.45f);
        _insetBottom = Mathf.Clamp(bottom, 0f, 0.45f);
        if (_camera != null && _world != null)
            PlaceCamera();
    }

    /// <summary>The pitch the camera takes at a distance: the player's own pitch if they set
    /// one, else steep far out and shallower close in. PlaceCamera and the whole-world fit
    /// both read it, so the fit always counts the pitch the camera will have.</summary>
    private float PitchDegFor(float camDist)
        => _pitchOverrideDeg >= 0f
            ? _pitchOverrideDeg
            : Mathf.Lerp(40f, 68f, Mathf.InverseLerp(CamDistMin, CamDistMax, camDist));

    /// <summary>The camera distance at which the whole world fits the free screen. The
    /// world's north to south depth shows on screen shortened by the sine of the pitch, and
    /// the pitch follows the distance, so the fit settles over a few rounds (it moves by a
    /// fraction of a unit after the third).</summary>
    private float FitWorldDist(float aspect)
    {
        float w = _world.Width * ColSpacing;
        float h = _world.Height * RowSpacing;
        float freeW = Mathf.Max(0.1f, 1f - _insetLeft - _insetRight);
        float freeH = Mathf.Max(0.1f, 1f - _insetTop - _insetBottom);
        float dist = CamDistMax;
        for (int i = 0; i < 6; i++)
        {
            float sinPitch = Mathf.Sin(Mathf.DegToRad(PitchDegFor(dist)));
            float viewH = Mathf.Max(h * sinPitch / freeH, w / (Mathf.Max(0.1f, aspect) * freeW)) * OverviewPad;
            dist = Mathf.Clamp(viewH / OrthoSizeFactor, CamDistMin, CamDistMax);
        }
        return dist;
    }

    /// <summary>From PlaceCamera: shift the view so the camera's target shows in the middle
    /// of the free screen. Camera3D's own offsets move the picture, the picking rays and
    /// UnprojectPosition together, so pointing and clicking stay exact.</summary>
    private void ApplyViewCentre()
    {
        if (_camera == null)
            return;
        float viewH = _orthographic
            ? _camera.Size
            : 2f * _camDist * Mathf.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f);
        var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        float aspect = vp.Y > 1f ? vp.X / vp.Y : 1.7778f;
        // The free centre lies right of the screen's by half the left inset less half the
        // right one, and below it by half the top inset less half the bottom one.
        _camera.HOffset = -(_insetLeft - _insetRight) * 0.5f * viewH * aspect;
        _camera.VOffset = (_insetTop - _insetBottom) * 0.5f * viewH;
    }

    // ── The paper ────────────────────────────────────────────────────────

    /// <summary>From RebuildTiles, beside the tile layers: one flat sheet of the canvas
    /// material under the whole world and far past it, in the unexplored ground's colour,
    /// so the world sits on its paper and the map's edge under fog dissolves into it. It
    /// casts no shadow, and the canvas mode takes none.</summary>
    private void RebuildPaperSheet()
    {
        if (_paperSheet != null && IsInstanceValid(_paperSheet))
            _paperSheet.QueueFree();
        _paperSheet = null;
        if (_world == null)
            return;
        var mesh = new PlaneMesh { Size = new Vector2(2f, 2f) };
        mesh.Material = PainterlyPrism.TileMaterial(PainterlyPrism.Canvas, 1.0f);
        var sheet = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = 1,
        };
        Vector3 centre = WorldCenter();
        sheet.SetInstanceTransform(0, new Transform3D(
            Basis.FromScale(new Vector3(PaperSheetHalf, 1f, PaperSheetHalf)),
            new Vector3(centre.X, PaperSheetY, centre.Z)));
        sheet.SetInstanceColor(0, UITheme.CanvasUnseen);
        _paperSheet = new MultiMeshInstance3D
        {
            Name = "PaperSheet",
            Multimesh = sheet,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_paperSheet);
    }
}
