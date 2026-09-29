using Godot;
using System.Collections.Generic;

// ============================================================
// WorldAtlas3D.Dispatch.cs: pieces you can click, beacons you can hit,
// and where the selected force can go (2026-09-28)
//
// Purpose:        The map half of the dispatch rework. Three things:
//
//                 1. SCREEN-SPACE PICKING. A click is tested against every
//                    piece (its body and its name label) and every beacon
//                    (its orb, its standard and its letters) in screen space
//                    BEFORE the ground raycast. The raycast lands on the
//                    ground under the cursor, and a beacon's orb floats 3.6
//                    units above its tile, so at the map's pitch aiming at
//                    the orb used to pick a tile well behind it. Pieces had
//                    no pick at all. Pieces win over beacons, because a party
//                    standing on a waystone is the more likely target.
//
//                 2. DISPATCH MARKS. A ring on every place the selected force
//                    can go, blue for a step-through, pale for a walk or a
//                    march, with the cost as a label (always for a
//                    step-through, on hover for the rest, so a map with
//                    twenty cities does not wear twenty captions).
//
//                 3. HIGHLIGHT. The rail's hover lights one ring and shows
//                    its cost, without a rebuild.
//
//                 Marks are drawn inside RebuildMarkers, so they follow the
//                 same lifetime, label scaling and city hiding as everything
//                 else on the board.
// Layer:          Systems / Strategic (presentation)
// Collaborators:  WorldAtlas3D (PickTile, RebuildMarkers, BuildPieceMarkers,
//                 the staging loop), StrategicView.Dispatch (the rail, which
//                 sets DispatchMarks and listens to PiecePicked)
// ============================================================

public partial class WorldAtlas3D
{
    /// <summary>The castle's id in <see cref="PiecePicked"/>. Parties use
    /// their FieldParty.Id.</summary>
    public const string CastlePieceId = "castle";

    /// <summary>A piece was clicked: its id and the tile it stands on. The host
    /// decides whether that means "select it" or "go there".</summary>
    public event System.Action<string, int, int> PiecePicked;

    /// <summary>One destination ring. Set by the host before SetPieces, which
    /// rebuilds; see <see cref="DispatchMarks"/>.</summary>
    public sealed class DispatchMark
    {
        public Vector2I Tile;
        public string Label = "";
        public bool StepThrough;
    }

    /// <summary>The selected force's destinations. Read at the next rebuild;
    /// set it BEFORE SetPieces so one rebuild draws both.</summary>
    public List<DispatchMark> DispatchMarks = new();

    public float CurrentCamDist => _camDist;

    /// <summary>A left double click on the world map (not the city view). The
    /// host drops the selection and pulls the camera back (2026-09-29).</summary>
    public event System.Action MapDoubleClicked;

    /// <summary>Set by the double click's press so its release does not also
    /// pick a tile.</summary>
    private bool _swallowNextRelease;

    /// <summary>The camera flight under way, killed when another starts.</summary>
    private Tween _flyTween;

    private const float PiecePickRadiusPx = 30f;
    private const float BeaconPickRadiusPx = 24f;

    private sealed class PickTarget
    {
        public Node3D Node;          // read live when set (labels move with the zoom)
        public Vector3 Pos;          // used when Node is null
        public string PieceId;       // null for a beacon
        public Vector2I Tile;
        public float RadiusPx;       // circle test when Chars == 0
        public int Chars;            // text test when > 0
        public float ScreenFrac;
    }

    private readonly List<PickTarget> _pickTargets = new();
    private readonly Dictionary<Vector2I, (MeshInstance3D ring, Label3D label, bool always)> _dispatchNodes = new();
    private Vector2I? _dispatchHighlight;

    // ── Registration (called from the builders in WorldAtlas3D.cs) ─────────

    private void ResetPickTargets() => _pickTargets.Clear();

    private string PartyIdAt(int i)
        => PartyIds != null && i >= 0 && i < PartyIds.Count ? PartyIds[i] : null;

    private void RegisterPieceTarget(string pieceId, Vector2I tile, Node3D node)
    {
        if (string.IsNullOrEmpty(pieceId) || node == null)
        {
            return;
        }
        _pickTargets.Add(new PickTarget { Node = node, PieceId = pieceId, Tile = tile, RadiusPx = PiecePickRadiusPx });
    }

    private void RegisterPieceAt(string pieceId, Vector2I tile, Vector3 pos)
    {
        if (string.IsNullOrEmpty(pieceId))
        {
            return;
        }
        _pickTargets.Add(new PickTarget { Pos = pos, PieceId = pieceId, Tile = tile, RadiusPx = PiecePickRadiusPx });
    }

    private void RegisterPieceLabel(string pieceId, Vector2I tile, Label3D label, float screenFrac)
    {
        if (string.IsNullOrEmpty(pieceId) || label == null)
        {
            return;
        }
        _pickTargets.Add(new PickTarget
        {
            Node = label, PieceId = pieceId, Tile = tile,
            Chars = Mathf.Max(1, label.Text?.Length ?? 1), ScreenFrac = screenFrac,
        });
    }

    private void RegisterBeaconAt(Vector2I tile, Vector3 pos)
        => _pickTargets.Add(new PickTarget { Pos = pos, Tile = tile, RadiusPx = BeaconPickRadiusPx });

    private void RegisterBeaconLabel(Vector2I tile, Label3D label, float screenFrac)
    {
        if (label == null)
        {
            return;
        }
        _pickTargets.Add(new PickTarget
        {
            Node = label, Tile = tile,
            Chars = Mathf.Max(1, label.Text?.Length ?? 1), ScreenFrac = screenFrac,
        });
    }

    // ── The pick ─────────────────────────────────────────────────────────

    /// <summary>Test the click against pieces and beacons in screen space.
    /// True when something was hit and reported; the ground pick is skipped.</summary>
    private bool TryDispatchScreenPick(Vector2 screenPos)
    {
        if (_camera == null || _pickTargets.Count == 0)
        {
            return false;
        }
        float vpH = GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
        if (vpH < 1f)
        {
            vpH = 1080f;
        }

        PickTarget best = null;
        float bestD = float.MaxValue;
        bool bestPiece = false;
        foreach (var t in _pickTargets)
        {
            Vector3 world;
            if (t.Node != null)
            {
                if (!IsInstanceValid(t.Node) || !t.Node.IsVisibleInTree())
                {
                    continue;
                }
                world = t.Node.GlobalPosition;
            }
            else
            {
                world = t.Pos;
            }
            if (_camera.IsPositionBehind(world))
            {
                continue;
            }
            Vector2 at = _camera.UnprojectPosition(world);
            Vector2 d = screenPos - at;

            bool hit;
            if (t.Chars > 0)
            {
                // A label: roughly its own box. Glyphs run about half an em
                // wide; the em is the label's share of screen height.
                float em = t.ScreenFrac * vpH;
                float halfW = Mathf.Max(18f, t.Chars * em * 0.3f);
                float halfH = Mathf.Max(12f, em * 0.75f);
                hit = Mathf.Abs(d.X) <= halfW && Mathf.Abs(d.Y) <= halfH;
            }
            else
            {
                hit = d.Length() <= t.RadiusPx;
            }
            if (!hit)
            {
                continue;
            }

            bool piece = t.PieceId != null;
            float len = d.Length();
            if ((piece && !bestPiece) || (piece == bestPiece && len < bestD))
            {
                best = t;
                bestD = len;
                bestPiece = piece;
            }
        }

        if (best == null)
        {
            return false;
        }
        if (best.PieceId != null)
        {
            PiecePicked?.Invoke(best.PieceId, best.Tile.X, best.Tile.Y);
        }
        else
        {
            TilePicked?.Invoke(best.Tile.X, best.Tile.Y);
        }
        return true;
    }

    // ── Marks ────────────────────────────────────────────────────────────

    /// <summary>Draw the rings. Called at the very end of RebuildMarkers, after
    /// the pieces, so a ring's label stacks above a piece's name.</summary>
    private void BuildDispatchMarks()
    {
        _dispatchNodes.Clear();
        if (DispatchMarks == null || _world == null || _cityMode)
        {
            return;
        }
        foreach (var m in DispatchMarks)
        {
            if (m == null || !_world.InBounds(m.Tile.X, m.Tile.Y) || _dispatchNodes.ContainsKey(m.Tile))
            {
                continue;
            }
            Color c = m.StepThrough ? UITheme.ArcaneBlue : UITheme.TextSecondary;
            var ring = DispatchRing(c, MarkerPos(m.Tile.X, m.Tile.Y, 0.12f));
            AddMarker(ring, m.Tile.X, m.Tile.Y);
            var lbl = StackedLabel(m.Tile, m.Label, c, MarkerPos(m.Tile.X, m.Tile.Y, 1.6f), 26, 0.016f);
            AddMarker(lbl, m.Tile.X, m.Tile.Y);
            _dispatchNodes[m.Tile] = (ring, lbl, m.StepThrough);
        }
        ApplyDispatchHighlight();
    }

    /// <summary>Light one ring and show its cost, or none. No rebuild.</summary>
    public void HighlightDispatch(Vector2I? tile)
    {
        _dispatchHighlight = tile;
        ApplyDispatchHighlight();
    }

    private void ApplyDispatchHighlight()
    {
        foreach (var kv in _dispatchNodes)
        {
            bool on = _dispatchHighlight.HasValue && _dispatchHighlight.Value == kv.Key;
            var (ring, lbl, always) = kv.Value;
            if (ring != null && IsInstanceValid(ring))
            {
                ring.Scale = on ? new Vector3(1.5f, 1.5f, 1.5f) : Vector3.One;
            }
            if (lbl != null && IsInstanceValid(lbl))
            {
                lbl.Visible = !_cityMode && (always || on);
            }
        }
    }

    // ── Range overlay (2026-09-29) ───────────────────────────────────────
    //    Asked for: clicking a force should swing the map onto it and show how
    //    far it can go. Every tile inside the reach keeps its colour with a
    //    wash of the force's tint; everything else darkens. Written into the
    //    same multimesh colours the preview lift uses, and re-applied at the
    //    end of RecolorTiles and Rebuild so a recolour cannot silently drop it.

    private Vector2I? _rangeCenter;
    private int _rangeRadius;
    private Color _rangeTint = UITheme.Gold;

    public bool RangeOverlayActive => _rangeCenter.HasValue;

    /// <summary>Light the tiles within <paramref name="radius"/> of the
    /// centre that a force could stand on, and dim the rest. Radius 0 lights
    /// only the force's own tile and dims nothing: a force that cannot move is
    /// said in words, not by blacking out the map.</summary>
    public void SetRangeOverlay(Vector2I center, int radius, Color tint)
    {
        _rangeCenter = center;
        _rangeRadius = Mathf.Max(0, radius);
        _rangeTint = tint;
        RecolorTiles();   // ends in ApplyRangeOverlay
    }

    public void ClearRangeOverlay()
    {
        if (!_rangeCenter.HasValue)
        {
            return;
        }
        _rangeCenter = null;
        RecolorTiles();
    }

    private void ApplyRangeOverlay()
    {
        if (!_rangeCenter.HasValue || _world == null || _cityMode || _instanceIndexOf == null
            || _landLayer?.Multimesh == null || _waterLayer?.Multimesh == null || _canvasLayer?.Multimesh == null)
        {
            return;
        }
        if (_rangeRadius <= 0)
        {
            return;
        }
        var c = _rangeCenter.Value;
        for (int i = 0; i < _world.Tiles.Length; i++)
        {
            int col = i % _world.Width, row = i / _world.Width;
            var t = _world.Tiles[i];
            var baseColor = TileColor(t, col, row);
            bool inside = t.Discovery != TileDiscovery.Unseen && !t.IsWater
                          && _world.HexDistance(c.X, c.Y, col, row) <= _rangeRadius;
            var shown = inside
                ? baseColor.Lerp(_rangeTint, 0.22f).Lightened(0.06f)
                : baseColor.Darkened(0.4f);
            LayerMultimesh(i).SetInstanceColor(_instanceIndexOf[i], shown);
        }
    }

    /// <summary>Fly onto a force and frame its reach: the camera distance is
    /// chosen so the whole disc fits the screen's height, with a margin, and
    /// the view is nudged right of the panels on the left.</summary>
    public void FrameRange(Vector2I center, int radius, float screenLeftShiftPx)
    {
        if (_camera == null || _world == null)
        {
            return;
        }
        float extent = (2f * Mathf.Max(1, radius) + 4f) * RowSpacing;
        float dist = Mathf.Clamp(extent / OrthoSizeFactor, 14f, CamDistMax);
        FlyToTile(center.X, center.Y, dist, screenLeftShiftPx);
    }

    private static MeshInstance3D DispatchRing(Color tint, Vector3 at)
        => new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.95f, OuterRadius = 1.2f, Rings = 24, RingSegments = 6 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(tint, 0.85f),
                EmissionEnabled = true,
                Emission = tint,
                EmissionEnergyMultiplier = 1.2f,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
            },
            Position = at,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
}
