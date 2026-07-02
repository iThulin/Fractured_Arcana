using Godot;
using System.Collections.Generic;

// ============================================================
// StrategicView.cs
//
// Purpose:        The cheap whole-world renderer (Phase 1b). Paints
//                 one quad per WorldTile via a single MultiMesh —
//                 ~9,216 instances, one draw call, zero Area2D /
//                 Label / per-tile nodes. Per-tile color encodes
//                 discovery first (Unseen = void), then faction
//                 tint over terrain, then corruption wash.
//                 Discovered POIs draw as markers in a second
//                 MultiMesh. Camera frames the world with zoom +
//                 drag-pan. Recolor is live: MarkTileDiscovered /
//                 MarkPoiDiscovered recolor single instances so the
//                 map fills in as expeditions reveal it (Phase 1c).
// Layer:          UI (strategic view)
// Collaborators:  WorldData.cs (the data it paints),
//                 UITheme.cs (all colors, incl. FactionColor),
//                 WorldGenerator.cs (standalone self-generation),
//                 OverworldHex.TerrainType (shared terrain colors)
// See:            single_world_refactor_v2.docx §4.2, §4.3
//
// Standalone: with no world supplied, _Ready generates one from a
// fixed seed so the scene renders in complete isolation (open the
// scene, press F6). SetWorld(world) injects the real cycle world.
// ============================================================

/// <summary>Map lenses — each colors the strategic map to answer a different
/// question. Political = faction control + terrain texture + corruption (the
/// combined overview); Terrain = raw region terrain; Corruption = a spread
/// heat map.</summary>
public enum StrategicLens
{
    Political,
    Terrain,
    Corruption,
}

public partial class StrategicView : Node2D
{
    [Export] public float TilePx = 10f;          // world-space size of one tile quad
    [Export] public int StandaloneSeed = 12345;  // used only when self-generating
    [Export] public string StandaloneSchool = "Elementalist";

    /// <summary>When true, _Ready generates a throwaway world for isolated testing.
    /// When false (the real strategic scene), it reads SaveManager.ActiveSave.Cycle.World
    /// and enables staging-point deploy.</summary>
    [Export] public bool Standalone = true;

    // For standalone testing: reveal the whole world so colors are visible
    // without running expeditions. Leave false to see true discovery (mostly void).
    [Export] public bool RevealAllForTesting = true;

    /// <summary>Operating range / window radius handed to the expedition on deploy.</summary>
    [Export] public int DeployWindowRadius = 12;

    /// <summary>Calendar phases one deploy costs. 8 phases per lunation / 3 per
    /// deploy ≈ 2-3 expeditions per lunation, ~32 per 12-lunation cycle. The
    /// second most important pacing knob after LunationsPerCycle.</summary>
    [Export] public int PhasesPerDeploy = 3;

    private WorldData _world;
    private System.Collections.Generic.Dictionary<string, KingdomState> _kingdoms = new();
    private Node2D _labelLayer;
    private const float ArchmageNameZoomThreshold = 1.4f; // ruler line appears past this zoom
    private bool _debugReveal = false;   // debug full-map view (non-destructive)
    private StrategicLens _lens = StrategicLens.Political;  // active map lens
    private MultiMeshInstance2D _tileLayer;
    private MultiMeshInstance2D _poiLayer;
    private MultiMeshInstance2D _settlementLayer;
    private Node2D _edgeLayer;
    private Node2D _borderLayer;
    private Camera2D _camera;

    // Camera control
    private bool _dragging;
    private Vector2 _dragLast;
    private float _zoom = 1f;
    private const float ZoomMin = 0.25f, ZoomMax = 4f, ZoomStep = 1.15f;

    // Index bookkeeping for live recolor.
    private readonly Dictionary<int, int> _poiInstanceOfPoi = new(); // poiIndex → poi MultiMesh instance

    // ── Standalone continent-style debug selector ────────────────────────
    private ContinentStyle? _standaloneStyle = null;   // null = seed-rolled
    private int _standaloneSeed;
    private CanvasLayer _debugControls;
    private Label _debugInfoLabel;

    public override void _Ready()
    {
        if (_world == null)
        {
            if (Standalone)
            {
                // Isolated testing: generate a throwaway world so the scene renders alone.
                _standaloneSeed = StandaloneSeed;
                GenerateStandaloneWorld();
            }
            else
            {
                // Real strategic scene: read the resident cycle world.
                var cycle = SaveManager.ActiveSave?.Cycle;
                if (cycle == null)
                {
                    GD.PrintErr("StrategicView: no active cycle — cannot show world.");
                    return;
                }
                _world = cycle.World;
                _kingdoms = cycle.Kingdoms;
                _debugReveal = PlayerSession.DebugMode && PlayerSession.DebugRevealStrategicMap;
            }
        }

        BuildCamera();
        CallDeferred(nameof(BuildRender));
    }

    /// <summary>Inject the real cycle world (campus integration path).</summary>
    public void SetWorld(WorldData world, System.Collections.Generic.Dictionary<string, KingdomState> kingdoms = null)
    {
        _world = world;
        _kingdoms = kingdoms ?? new System.Collections.Generic.Dictionary<string, KingdomState>();
        if (IsInsideTree())
            CallDeferred(nameof(BuildRender));
    }

    // ── Render construction ──────────────────────────────────────────────
    private void BuildRender()
    {
        if (_world == null)
            return;

        BuildTileLayer();
        BuildSettlementLayer();
        BuildBorderLayer();
        BuildEdgeLayer();
        BuildPoiLayer();
        if (!Standalone)
        {
            BuildStagingMarkers();
            BuildHud();
        }
        if (Standalone)
            BuildDebugControls();
        FrameCamera();
        BuildLabelLayer();   // last: needs the framed _zoom for correct counter-scale
    }

    /// <summary>Persistent strategic-map HUD: a free exit back to campus. Returning
    /// costs nothing — the world, discoveries, and staging points already live in
    /// the saved cycle, so leaving and reopening the map changes nothing.</summary>
    private void BuildHud()
    {
        _hud?.QueueFree();
        _hud = new CanvasLayer { Name = "StrategicHud" };
        AddChild(_hud);

        var campusBtn = new Button
        {
            Text = "Return to Campus",
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 16,
            OffsetTop = 16,
            OffsetRight = 196,
            OffsetBottom = 56,
        };
        campusBtn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(campusBtn, isPrimary: false);
        
        campusBtn.Pressed += () =>
            GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn");
        _hud.AddChild(campusBtn);

        var councilBtn = new Button
        {
            Text = "Council",
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 204,
            OffsetTop = 16,
            OffsetRight = 324,
            OffsetBottom = 56,
        };
        councilBtn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(councilBtn, isPrimary: false);
        councilBtn.Pressed += () => CouncilPanel.Toggle(this);
        _hud.AddChild(councilBtn);

        // ── Calendar readout: the doomsday clock, top-right ──────────────
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle != null)
        {
            var cal = cycle.Calendar;
            var calPanel = new PanelContainer
            {
                AnchorLeft = 1f,
                AnchorTop = 0f,
                AnchorRight = 1f,
                AnchorBottom = 0f,
                GrowHorizontal = Control.GrowDirection.Begin,
                OffsetLeft = -260,
                OffsetRight = -16,
                OffsetTop = 16,
                OffsetBottom = 84,
            };
            calPanel.AddThemeStyleboxOverride("panel",
                UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Gold));
            _hud.AddChild(calPanel);

            var calMargin = new MarginContainer();
            calMargin.AddThemeConstantOverride("margin_left", 14);
            calMargin.AddThemeConstantOverride("margin_right", 14);
            calMargin.AddThemeConstantOverride("margin_top", 8);
            calMargin.AddThemeConstantOverride("margin_bottom", 8);
            calPanel.AddChild(calMargin);

            var calVbox = new VBoxContainer();
            calVbox.AddThemeConstantOverride("separation", 2);
            calMargin.AddChild(calVbox);

            var phaseLbl = new Label
            {
                Text = $"Lunation {cal.CurrentLunation} / {cal.LunationsPerCycle}  ·  {cal.CurrentPhaseName}",
            };
            phaseLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
            phaseLbl.AddThemeColorOverride("font_color", UITheme.Gold);
            calVbox.AddChild(phaseLbl);

            int lunationsLeft = cal.LunationsRemaining;
            var remainLbl = new Label
            {
                Text = lunationsLeft <= 2
                    ? $"⚠ {lunationsLeft} lunation(s) until the Conjunction"
                    : $"{lunationsLeft} lunations until the Conjunction",
            };
            remainLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
            remainLbl.AddThemeColorOverride("font_color",
                lunationsLeft <= 2 ? UITheme.Danger : new Color(1f, 1f, 1f, 0.6f));
            calVbox.AddChild(remainLbl);
        }

        // A short legend so the player knows what they're looking at.
        var hint = new Label
        {
            Text = "Click a gold beacon to deploy an expedition.",
            AnchorLeft = 0.5f,
            AnchorTop = 1f,
            AnchorRight = 0.5f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Both,
            OffsetTop = -34,
            OffsetBottom = -10,
        };
        hint.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        hint.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.5f));
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hud.AddChild(hint);

        BuildLensButtons();
    }

    // ── Map lens toggles ─────────────────────────────────────────────────
    private readonly System.Collections.Generic.List<Button> _lensButtons = new();

    private void BuildLensButtons()
    {
        _lensButtons.Clear();

        // A horizontal row under the Return-to-Campus button, top-left.
        var row = new HBoxContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 16,
            OffsetTop = 64,
            OffsetRight = 16,
            OffsetBottom = 96,
        };
        row.AddThemeConstantOverride("separation", 6);
        _hud.AddChild(row);

        var lbl = new Label { Text = "View:" };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        lbl.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.6f));
        lbl.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(lbl);

        AddLensButton(row, "Political", StrategicLens.Political);
        AddLensButton(row, "Terrain", StrategicLens.Terrain);
        AddLensButton(row, "Corruption", StrategicLens.Corruption);

        UpdateLensButtons();
    }

    private void AddLensButton(HBoxContainer row, string text, StrategicLens lens)
    {
        var btn = new Button { Text = text, ToggleMode = true };
        btn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        btn.Pressed += () => SetLens(lens);
        btn.SetMeta("lens", (int)lens);
        row.AddChild(btn);
        _lensButtons.Add(btn);
    }

    private void UpdateLensButtons()
    {
        foreach (var btn in _lensButtons)
        {
            if (!IsInstanceValid(btn))
                continue;
            bool active = (int)btn.GetMeta("lens") == (int)_lens;
            btn.ButtonPressed = active;
            btn.Modulate = active ? Colors.White : new Color(1f, 1f, 1f, 0.55f);
        }
    }

    private void BuildTileLayer()
    {
        _tileLayer?.QueueFree();

        var quad = MakeQuadMesh(TilePx);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            // GL Compatibility can collapse per-instance COLOR when only one of
            // UseColors / UseCustomData is set. Enabling both keeps the color
            // buffer live on the Compatibility renderer.
            UseCustomData = true,
            Mesh = quad,
            InstanceCount = _world.Width * _world.Height,
        };

        for (int y = 0; y < _world.Height; y++)
        {
            for (int x = 0; x < _world.Width; x++)
            {
                int i = y * _world.Width + x;
                mm.SetInstanceTransform2D(i,
                    new Transform2D(0f, HexCoord.OffsetRenderPosition(x, y, TilePx)));
                mm.SetInstanceColor(i, TileColor(_world.Tiles[i]));
                mm.SetInstanceCustomData(i, Colors.White); // keep custom-data buffer non-zero
            }
        }

        _tileLayer = new MultiMeshInstance2D { Name = "TileLayer", Multimesh = mm };
        AddChild(_tileLayer);
    }

    private void BuildPoiLayer()
    {
        _poiLayer?.QueueFree();
        _poiInstanceOfPoi.Clear();

        // Count discovered POIs first (MultiMesh needs a fixed instance count).
        // Debug reveal shows every POI regardless of discovery.
        var visible = new List<int>();
        for (int i = 0; i < _world.Pois.Count; i++)
            if (_debugReveal || _world.Pois[i].Discovered)
                visible.Add(i);

        // Markers are diamonds, a bit larger than a tile so they read when zoomed out.
        var marker = MakeDiamondMesh(TilePx * 1.4f);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = true,
            Mesh = marker,
            InstanceCount = Mathf.Max(1, visible.Count),
        };

        if (visible.Count == 0)
        {
            // Keep a valid (invisible) instance so the MultiMesh is well-formed.
            mm.SetInstanceTransform2D(0, new Transform2D(0f, new Vector2(-9999, -9999)));
            mm.SetInstanceColor(0, new Color(0, 0, 0, 0));
            mm.SetInstanceCustomData(0, Colors.White);
        }
        else
        {
            for (int n = 0; n < visible.Count; n++)
            {
                int poiIndex = visible[n];
                var poi = _world.Pois[poiIndex];
                Vector2 pos = HexCoord.OffsetRenderPosition(poi.X, poi.Y, TilePx)
                              + new Vector2(TilePx * 0.5f, TilePx * 0.5f);
                mm.SetInstanceTransform2D(n, new Transform2D(0f, pos));
                mm.SetInstanceColor(n, PoiColor(poi.Kind));
                mm.SetInstanceCustomData(n, Colors.White);
                _poiInstanceOfPoi[poiIndex] = n;
            }
        }

        _poiLayer = new MultiMeshInstance2D { Name = "PoiLayer", Multimesh = mm };
        _poiLayer.ZIndex = 1;
        AddChild(_poiLayer);
    }

    /// <summary>River/road overlay for the strategic zoom. Each tile draws a half-
    /// segment from its centre toward each river/road edge's shared boundary; the two
    /// tiles' halves meet, tracing the network as a route (a center-path simplification
    /// — the window draws true hex edges). Respects fog. Rivers blue, roads tan; a road
    /// over a river draws second, reading as a crossing.</summary>
    private void BuildEdgeLayer()
    {
        _edgeLayer?.QueueFree();
        _edgeLayer = new Node2D { Name = "EdgeLayer" };   // over settlement tint, under POIs (z=1)
        AddChild(_edgeLayer);
        if (_world == null)
            return;

        float half = TilePx * 0.5f;
        var center = new Vector2(half, half);
        float riverW = Mathf.Max(1f, TilePx * 0.20f);
        float roadW = Mathf.Max(1f, TilePx * 0.13f);
        float springW = Mathf.Max(1f, TilePx * 0.11f);

        for (int y = 0; y < _world.Height; y++)
        {
            for (int x = 0; x < _world.Width; x++)
            {
                var t = _world.GetTile(x, y);
                if (t.RiverEdges == 0 && t.RoadEdges == 0 && t.SpringEdges == 0)
                    continue;
                if (t.IsWater)
                    continue;   // never originate a line in water — kills the ocean overshoot

                var disc = _debugReveal ? TileDiscovery.Explored : t.Discovery;
                if (disc == TileDiscovery.Unseen)
                    continue;

                Vector2 c = HexCoord.OffsetRenderPosition(x, y, TilePx) + center;
                var (q, r) = HexCoord.OffsetToAxial(x, y);

                for (int d = 0; d < 6; d++)
                {
                    bool spring = (t.SpringEdges & (1 << d)) != 0;
                    bool river = (t.RiverEdges & (1 << d)) != 0;
                    bool road = (t.RoadEdges & (1 << d)) != 0;
                    if (!spring && !river && !road)
                        continue;

                    var (dq, dr) = HexCoord.AxialDirections[d];
                    var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                    if (!_world.InBounds(nc, nr))
                        continue;

                    Vector2 nCenter = HexCoord.OffsetRenderPosition(nc, nr, TilePx) + center;
                    Vector2 dir = nCenter - c;
                    float dist = dir.Length();
                    // Clamp to half a tile so the segment stays inside this tile's
                    // footprint — two tiles' halves still meet near the shared edge.
                    Vector2 end = c + dir / dist * Mathf.Min(dist * 0.5f, TilePx * 0.5f);

                    if (spring && !river)
                        AddEdgeSegment(c, end, springW, UITheme.TerrainLake);   // thin, lighter blue
                    if (river)
                        AddEdgeSegment(c, end, riverW, UITheme.TerrainWater);
                    if (road)
                        AddEdgeSegment(c, end, roadW, UITheme.TerrainRoad);
                }
            }
        }
    }

    /// <summary>Kingdom boundaries, Political lens only. Instead of stroking edges
    /// (which exposes the hex stairstep and drifts on the square-quad renderer), this
    /// TINTS the boundary tiles — a tile whose neighbour is a different kingdom, ocean,
    /// or off-map — with a dark band, using the SAME quad transform as the tile layer
    /// so it lands exactly on grid. Mirrors BuildSettlementLayer's rim technique.</summary>
    private void BuildBorderLayer()
    {
        _borderLayer?.QueueFree();
        if (_world == null || _lens != StrategicLens.Political)
        {
            _borderLayer = new Node2D { Name = "BorderLayer" }; // empty placeholder so the ref is valid
            AddChild(_borderLayer);
            return;
        }

        var rim = new List<(int x, int y)>();
        for (int y = 0; y < _world.Height; y++)
        {
            for (int x = 0; x < _world.Width; x++)
            {
                var t = _world.GetTile(x, y);
                if (!t.IsLand || string.IsNullOrEmpty(t.KingdomId))
                    continue;

                var disc = _debugReveal ? TileDiscovery.Explored : t.Discovery;
                if (disc == TileDiscovery.Unseen)
                    continue;

                var (q, r) = HexCoord.OffsetToAxial(x, y);
                bool onBorder = false;
                for (int d = 0; d < 6; d++)
                {
                    var (dq, dr) = HexCoord.AxialDirections[d];
                    var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                    if (!_world.InBounds(nc, nr))
                    { onBorder = true; break; }
                    var nt = _world.GetTile(nc, nr);
                    if (nt.IsWater || nt.KingdomId != t.KingdomId)
                    { onBorder = true; break; }
                }
                if (onBorder)
                    rim.Add((x, y));
            }
        }

        if (rim.Count == 0)
        {
            _borderLayer = new Node2D { Name = "BorderLayer" };
            AddChild(_borderLayer);
            return;
        }

        var quad = MakeQuadMesh(TilePx);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = true,
            Mesh = quad,
            InstanceCount = rim.Count,
        };
        for (int n = 0; n < rim.Count; n++)
        {
            var (x, y) = rim[n];
            mm.SetInstanceTransform2D(n,
                new Transform2D(0f, HexCoord.OffsetRenderPosition(x, y, TilePx)));
            mm.SetInstanceColor(n, UITheme.KingdomBorder);
            mm.SetInstanceCustomData(n, Colors.White);
        }

        _borderLayer = new MultiMeshInstance2D
        {
            Name = "BorderLayer",
            Multimesh = mm,
            ZIndex = 0, // above tiles, below POIs — same band as settlement rim
        };
        AddChild(_borderLayer);
    }

    private void AddEdgeSegment(Vector2 a, Vector2 b, float width, Color color)
    {
        _edgeLayer.AddChild(new Line2D
        {
            Points = new[] { a, b },
            Width = width,
            DefaultColor = color,
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
        });
    }

    /// <summary>Tints the boundary tiles of each settlement (a one-tile rim) so a
    /// city/town's extent reads without hiding the terrain underneath. A tile is on
    /// the rim if any hex neighbour belongs to a different settlement (or none), or
    /// if it sits on the map edge. Respects fog: Unseen tiles aren't rimmed. Cities
    /// gold, towns bronze. Aligns to the tile layer exactly (same transform/mesh).</summary>
    private void BuildSettlementLayer()
    {
        _settlementLayer?.QueueFree();
        if (_world == null || _world.Settlements.Count == 0)
            return;

        var fill = new List<(int x, int y, SettlementTier tier)>();
        for (int i = 0; i < _world.Settlements.Count; i++)
        {
            var s = _world.Settlements[i];
            foreach (var (tx, ty) in s.Tiles)
            {
                var disc = _debugReveal ? TileDiscovery.Explored : _world.GetTile(tx, ty).Discovery;
                if (disc == TileDiscovery.Unseen)
                    continue;
                fill.Add((tx, ty, s.Tier));
            }
        }
        if (fill.Count == 0)
            return;

        var quad = MakeQuadMesh(TilePx);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = true,
            Mesh = quad,
            InstanceCount = fill.Count,
        };
        for (int n = 0; n < fill.Count; n++)
        {
            var (x, y, tier) = fill[n];
            mm.SetInstanceTransform2D(n,
                new Transform2D(0f, HexCoord.OffsetRenderPosition(x, y, TilePx)));
            mm.SetInstanceColor(n, tier == SettlementTier.City
                ? UITheme.SettlementCityBorder
                : UITheme.SettlementTownBorder);
            mm.SetInstanceCustomData(n, Colors.White);
        }

        _settlementLayer = new MultiMeshInstance2D
        {
            Name = "SettlementLayer",
            Multimesh = mm,
            ZIndex = 0,   // above tiles (added later in tree), below POIs (z=1)
        };
        AddChild(_settlementLayer);
    }

    /// <summary>Per-kingdom name labels, anchored at each kingdom's seat (capital).
    /// Political lens only; the ruler line is zoom-gated so the far-out view stays
    /// readable. Drawn above POIs with a dark backing pill for legibility over the
    /// faction wash.</summary>
    private void BuildLabelLayer()
    {
        _labelLayer?.QueueFree();
        _labelLayer = new Node2D { Name = "LabelLayer", ZIndex = 3 }; // above POIs (z=1), staging (z=2)
        AddChild(_labelLayer);

        if (_world == null || _lens != StrategicLens.Political)
            return;

        bool showRuler = _zoom >= ArchmageNameZoomThreshold;

        // Anchor each kingdom's label at its Seat POI (the capital/seat city centre).
        foreach (var poi in _world.Pois)
        {
            if (poi.Kind != PoiKind.Seat && poi.Kind != PoiKind.Convergence)
                continue;

            var disc = _debugReveal ? TileDiscovery.Explored : _world.GetTile(poi.X, poi.Y).Discovery;
            if (disc == TileDiscovery.Unseen)
                continue; // don't name kingdoms the player hasn't found

            if (string.IsNullOrEmpty(poi.KingdomId) ||
                !_kingdoms.TryGetValue(poi.KingdomId, out var ks))
                continue;

            string place = string.IsNullOrEmpty(ks.DisplayName) ? poi.KingdomId : ks.DisplayName;
            string ruler = null;
            if (showRuler && !string.IsNullOrEmpty(ks.ArchmageId))
                ruler = ArchmageRegistry.Get(ks.ArchmageId)?.DisplayName;

            Vector2 at = HexCoord.OffsetRenderPosition(poi.X, poi.Y, TilePx)
                         + new Vector2(TilePx * 0.5f, TilePx * 0.5f);
            AddKingdomLabel(at, place, ruler, ominous: poi.Kind == PoiKind.Convergence);
        }
    }

    private void AddKingdomLabel(Vector2 center, string place, string ruler, bool ominous = false)
    {
        float inv = _zoom > 0.001f ? 1f / _zoom : 1f;

        var holder = new Node2D
        {
            Position = center,
            Scale = new Vector2(inv, inv),
        };
        _labelLayer.AddChild(holder);

        var plate = new PanelContainer();
        var plateStyle = UITheme.MakePanelStyle(
            new Color(UITheme.BgBase.R, UITheme.BgBase.G, UITheme.BgBase.B, 0.78f),
            ominous ? UITheme.StrategicCorruption : UITheme.Violet);
        plateStyle.ContentMarginLeft = plateStyle.ContentMarginRight = 8;
        plateStyle.ContentMarginTop = plateStyle.ContentMarginBottom = 3;
        plate.AddThemeStyleboxOverride("panel", plateStyle);
        holder.AddChild(plate);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        plate.AddChild(vbox);

        var nameLbl = new Label
        {
            Text = Spaced(place.ToUpperInvariant()),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.StrategicLabelFontSize);
        nameLbl.AddThemeColorOverride("font_color",
            ominous ? new Color(0.95f, 0.55f, 0.62f) : UITheme.Gold);
        nameLbl.AddThemeColorOverride("font_outline_color", UITheme.WorldDeep);
        nameLbl.AddThemeConstantOverride("outline_size", 4);
        vbox.AddChild(nameLbl);

        if (!string.IsNullOrEmpty(ruler))
        {
            var rulerLbl = new Label
            {
                Text = ruler,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            rulerLbl.AddThemeFontSizeOverride("font_size", UITheme.StrategicLabelFontSize - 5);
            rulerLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            rulerLbl.AddThemeColorOverride("font_outline_color", UITheme.WorldDeep);
            rulerLbl.AddThemeConstantOverride("outline_size", 3);
            vbox.AddChild(rulerLbl);
        }

        CallDeferred(nameof(RecenterLabelPlate), plate);
    }

    /// <summary>Re-position a label plate so it's horizontally centred on, and sitting
    /// just above, its holder's origin (the seat). Deferred because a Control's size
    /// isn't known until after it lays out.</summary>
    private void RecenterLabelPlate(PanelContainer plate)
    {
        if (!IsInstanceValid(plate))
            return;
        Vector2 size = plate.Size;
        // Centre horizontally; lift the plate up so its bottom edge clears the seat
        // diamond (which is ~TilePx*1.4 tall, drawn at the anchor).
        plate.Position = new Vector2(-size.X * 0.5f, -size.Y - TilePx * 1.1f);
    }

    /// <summary>Insert thin spaces between characters for a letter-spaced, map-label
    /// feel (Godot Labels have no native tracking control).</summary>
    private static string Spaced(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        // U+2009 THIN SPACE between each character; a hair of tracking, not a full gap.
        return string.Join("\u2009", s.ToCharArray());
    }

    // ── Color logic ──────────────────────────────────────────────────────
    private Color TileColor(WorldTile t)
    {
        // Debug full-map reveal: treat every tile as Explored for DISPLAY only —
        // the saved discovery state is never touched. Lets corruption + the whole
        // world be inspected during testing.
        var discovery = _debugReveal ? TileDiscovery.Explored : t.Discovery;

        // Discovery first: unexplored is void (all lenses respect fog).
        if (discovery == TileDiscovery.Unseen)
            return UITheme.StrategicUnseen;

        // Charted-but-unexplored: dim hint of the active lens's read.
        if (discovery == TileDiscovery.Charted)
        {
            Color hint = LensBaseColor(t);
            return hint.Lerp(UITheme.StrategicCharted, 0.55f);
        }

        // Explored: the active lens decides how the tile reads.
        return LensColor(t);
    }

    /// <summary>The fully-saturated color for a tile under the active lens
    /// (explored tiles). Each lens answers a different question about the tile.</summary>
    private Color LensColor(WorldTile t)
    {
        switch (_lens)
        {
            case StrategicLens.Terrain:
                return TerrainLensColor(t);
            case StrategicLens.Corruption:
                return CorruptionLensColor(t);
            default:
                return PoliticalLensColor(t);
        }
    }

    /// <summary>The base (un-dimmed) color used for the charted-tile hint, per lens.</summary>
    private Color LensBaseColor(WorldTile t)
    {
        switch (_lens)
        {
            case StrategicLens.Terrain:
                return TerrainColorOf(t);
            case StrategicLens.Corruption:
                return CorruptionLensColor(t);
            default:
                bool ownedLand = t.IsLand && !string.IsNullOrEmpty(t.KingdomId);
                return ownedLand ? KingdomColor(t.KingdomId) : TerrainColorOf(t);
        }
    }

    // ── Political lens (default): faction control + terrain luminance + corruption wash ──
    private Color PoliticalLensColor(WorldTile t)
    {
        bool isLand = t.IsLand;
        Color c;
        if (isLand && !string.IsNullOrEmpty(t.KingdomId))
        {
            Color bloc = KingdomColor(t.KingdomId);
            float lum = TerrainLuminance(t.Terrain);
            c = new Color(
                Mathf.Clamp(bloc.R * lum, 0f, 1f),
                Mathf.Clamp(bloc.G * lum, 0f, 1f),
                Mathf.Clamp(bloc.B * lum, 0f, 1f),
                1f);
        }
        else
        {
            c = TerrainColorOf(t);
        }
        if (t.Corruption > 0)
        {
            // Political lens: corruption is a STAIN over the kingdom color, not a
            // recolor — the territory's identity must survive underneath. Capped low
            // and darkened (vs the loud Corruption-lens red) so a heavily corrupted
            // kingdom reads as "this kingdom, corrupted," not "the red kingdom."
            float k = Mathf.Clamp(t.Corruption / 100f, 0f, 1f) * 0.35f;
            c = c.Lerp(UITheme.StrategicCorruptionWash, k);
        }
        return c;
    }

    // ── Terrain lens: pure region terrain, no faction tint. Shows the
    //    per-region terrain identity (the whole point of terrain-per-region). ──
    private Color TerrainLensColor(WorldTile t) => TerrainColorOf(t);

    /// <summary>Terrain color for a whole tile, with ocean shaded shallow→deep by
    /// distance from shore instead of one flat blue. Use this wherever a tile's
    /// terrain color is wanted; TerrainColor(TerrainType) stays for type-only lookups.</summary>
    private static Color TerrainColorOf(WorldTile t)
        => t.Terrain == OverworldHex.TerrainType.Water
            ? UITheme.OceanColor(t.OceanDepth)
            : TerrainColor(t.Terrain);

    // ── Corruption lens: a heat map. Clean land reads cool/neutral, corruption
    //    ramps through warning to full corruption color. Makes the spread legible. ──
    private Color CorruptionLensColor(WorldTile t)
    {
        if (t.IsWater)
            return UITheme.TerrainWater.Darkened(0.3f);
        float k = Mathf.Clamp(t.Corruption / 100f, 0f, 1f);
        // Cool clean -> hot corrupted, via a two-stop ramp for readability.
        Color clean = new Color(0.18f, 0.26f, 0.22f);          // dim green-grey
        Color mid = new Color(0.65f, 0.45f, 0.15f);            // amber
        Color hot = UITheme.StrategicCorruption;               // full corruption
        return k < 0.5f
            ? clean.Lerp(mid, k / 0.5f)
            : mid.Lerp(hot, (k - 0.5f) / 0.5f);
    }

    private static Color TerrainColor(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Grassland => UITheme.TerrainGrassland,
        OverworldHex.TerrainType.Forest => UITheme.TerrainForest,
        OverworldHex.TerrainType.Road => UITheme.TerrainRoad,
        OverworldHex.TerrainType.Ruins => UITheme.TerrainRuins,
        OverworldHex.TerrainType.Mountain => UITheme.TerrainMountain,
        OverworldHex.TerrainType.Swamp => UITheme.TerrainSwamp,
        OverworldHex.TerrainType.ArcaneGround => UITheme.TerrainArcaneGround,
        OverworldHex.TerrainType.Volcanic => UITheme.TerrainVolcanic,
        OverworldHex.TerrainType.Water => UITheme.TerrainWater,
        OverworldHex.TerrainType.Hills => UITheme.TerrainHills,
        OverworldHex.TerrainType.Coast => UITheme.TerrainCoast,
        OverworldHex.TerrainType.Lake => UITheme.TerrainLake,
        OverworldHex.TerrainType.Desert => UITheme.TerrainDesert,
        OverworldHex.TerrainType.Tundra => UITheme.TerrainTundra,
        OverworldHex.TerrainType.Snow => UITheme.TerrainSnow,
        OverworldHex.TerrainType.Marsh => UITheme.TerrainMarsh,
        _ => UITheme.Neutral,
    };

    /// <summary>Brightness multiplier per terrain so terrain reads as texture
    /// WITHIN a faction-colored territory without overriding the faction hue.</summary>
    private static float TerrainLuminance(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Grassland => 1.10f,
        OverworldHex.TerrainType.Road => 1.15f,
        OverworldHex.TerrainType.ArcaneGround => 1.05f,
        OverworldHex.TerrainType.Ruins => 0.95f,
        OverworldHex.TerrainType.Forest => 0.78f,
        OverworldHex.TerrainType.Swamp => 0.72f,
        OverworldHex.TerrainType.Mountain => 0.88f,
        OverworldHex.TerrainType.Volcanic => 0.85f,
        OverworldHex.TerrainType.Hills => 0.95f,
        OverworldHex.TerrainType.Coast => 1.12f,
        OverworldHex.TerrainType.Desert => 0.80f,
        OverworldHex.TerrainType.Tundra => 0.62f,
        OverworldHex.TerrainType.Snow => 0.95f,
        OverworldHex.TerrainType.Marsh => 0.40f,
        _ => 1.0f,
    };

    /// <summary>Resolve a kingdom id to its controlling faction's color.
    /// Tiles store a KINGDOM id (e.g. "kingdom_3"), not a faction id, so we
    /// look up the kingdom's ControllingFactionId first. Falls back to a
    /// per-kingdom distinct hue if the kingdom or faction is missing, so the
    /// map never collapses to one color even with incomplete data.</summary>
    private Color FactionColorForKingdom(string kingdomId)
    {
        if (_kingdoms != null && _kingdoms.TryGetValue(kingdomId, out var ks)
            && !string.IsNullOrEmpty(ks.ControllingFactionId))
        {
            return UITheme.FactionColor(ks.ControllingFactionId);
        }
        // Fallback: derive a stable distinct hue from the kingdom id so
        // unowned/seat territories still read as separate blocs.
        return FallbackHue(kingdomId);
    }

    private static Color FallbackHue(string id)
    {
        if (string.IsNullOrEmpty(id))
            return UITheme.Neutral;
        // Hash the id to a hue; fixed saturation/value for legibility.
        uint h = 2166136261u;
        foreach (char ch in id)
        { h ^= ch; h *= 16777619u; }
        float hue = (h % 360u) / 360f;
        return Color.FromHsv(hue, 0.45f, 0.70f);
    }

    private static Color PoiColor(PoiKind kind) => kind switch
    {
        PoiKind.Combat => UITheme.POICombat,
        PoiKind.Rest => UITheme.POIRest,
        PoiKind.Narrative => UITheme.POINarrative,
        PoiKind.Negotiation => UITheme.POINegotiation,
        PoiKind.Outpost => UITheme.POIOutpost,
        PoiKind.Seat => UITheme.Gold,          // archmage seats: gold
        PoiKind.Settlement => UITheme.ArcaneBlue,
        PoiKind.Convergence => UITheme.POIConvergence,
        _ => UITheme.TextPrimary,
    };

    /// <summary>A stable, visually distinct fill color per kingdom, keyed off the
    /// kingdom INDEX so the ten territories spread evenly around the hue wheel and
    /// adjacent ids never collide. This is the political-lens unit: one kingdom =
    /// one color = one bordered bloc, regardless of which faction controls it
    /// (faction is a separate layer; coloring by it merges distinct territories).</summary>
    private Color KingdomColor(string kingdomId)
    {
        if (string.IsNullOrEmpty(kingdomId))
            return UITheme.Neutral;

        int idx = -1;
        int us = kingdomId.LastIndexOf('_');
        if (us >= 0 && us + 1 < kingdomId.Length)
            int.TryParse(kingdomId.Substring(us + 1), out idx);
        if (idx < 0)
        {
            uint hsh = 2166136261u;
            foreach (char ch in kingdomId)
            { hsh ^= ch; hsh *= 16777619u; }
            idx = (int)(hsh % (uint)UITheme.KingdomPalette.Length);
        }

        return UITheme.KingdomPalette[idx % UITheme.KingdomPalette.Length];
    }

    // ── Live recolor (Phase 1c hooks) ────────────────────────────────────

    /// <summary>Recolor one tile after its discovery/corruption changed.</summary>
    public void MarkTileDirty(int x, int y)
    {
        if (_tileLayer?.Multimesh == null || !_world.InBounds(x, y))
            return;
        int i = y * _world.Width + x;
        _tileLayer.Multimesh.SetInstanceColor(i, TileColor(_world.Tiles[i]));
    }

    /// <summary>A POI just became discovered — rebuild the POI layer (its
    /// instance count changed). Cheap relative to the tile layer.</summary>
    public void RefreshPois() => BuildPoiLayer();

    /// <summary>Switch the active map lens and recolor every tile. Cheap: only
    /// rewrites instance colors, no rebuild.</summary>
    public void SetLens(StrategicLens lens)
    {
        if (_lens == lens)
            return;
        _lens = lens;
        RecolorAllTiles();
        BuildBorderLayer();
        BuildLabelLayer();
        UpdateLensButtons();
    }

    private void RecolorAllTiles()
    {
        if (_tileLayer?.Multimesh == null || _world == null)
            return;
        var mm = _tileLayer.Multimesh;
        for (int i = 0; i < _world.Tiles.Length; i++)
            mm.SetInstanceColor(i, TileColor(_world.Tiles[i]));
    }

    // ── Camera ───────────────────────────────────────────────────────────
    private void BuildCamera()
    {
        _camera = new Camera2D { Name = "StrategicCamera" };
        AddChild(_camera);
        _camera.CallDeferred("make_current");
    }

    private void FrameCamera()
    {
        if (_camera == null || _world == null)
            return;
        float w = _world.Width * TilePx;
        float h = _world.Height * TilePx;
        _camera.Position = new Vector2(w * 0.5f, h * 0.5f);

        // Fit the world to the viewport with a margin.
        var vp = GetViewportRect().Size;
        float fit = Mathf.Min(vp.X / w, vp.Y / h) * 0.9f;
        _zoom = Mathf.Clamp(fit, ZoomMin, ZoomMax);
        _camera.Zoom = new Vector2(_zoom, _zoom);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_camera == null)
            return;

        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
                ApplyZoom(ZoomStep);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
                ApplyZoom(1f / ZoomStep);
            else if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Middle)
            {
                _dragging = mb.Pressed;
                _dragLast = mb.Position;
            }
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            // Pan opposite the drag, scaled by zoom.
            _camera.Position -= (mm.Position - _dragLast) / _camera.Zoom;
            _dragLast = mm.Position;
        }
    }

    private void ApplyZoom(float factor)
    {
        _zoom = Mathf.Clamp(_zoom * factor, ZoomMin, ZoomMax);
        _camera.Zoom = new Vector2(_zoom, _zoom);
        BuildLabelLayer(); // re-evaluate the ruler-line zoom gate
    }

    // ── Meshes ───────────────────────────────────────────────────────────
    private static QuadMesh MakeQuadMesh(float size)
        => new QuadMesh { Size = new Vector2(size, size) };

    /// <summary>A small diamond (rotated square) for POI markers, built as an
    /// ArrayMesh so it reads distinctly from the square tiles.</summary>
    private static ArrayMesh MakeDiamondMesh(float size)
    {
        float h = size * 0.5f;
        var verts = new Vector3[]
        {
            new(0, -h, 0), new(h, 0, 0), new(-h, 0, 0),
            new(h, 0, 0), new(0, h, 0), new(-h, 0, 0),
        };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ── Standalone helper ────────────────────────────────────────────────
    private void RevealAll()
    {
        for (int i = 0; i < _world.Tiles.Length; i++)
            _world.Tiles[i].Discovery = TileDiscovery.Explored;
        foreach (var poi in _world.Pois)
            poi.Discovered = true;
    }

    // ── Standalone continent-style selector (debug only) ─────────────────

    /// <summary>(Re)generate the disposable standalone world from the current
    /// debug seed + style override. Never touches a save — Standalone only.</summary>
    private void GenerateStandaloneWorld()
    {
        var p = new WorldGenerator.Params { ContinentStyleOverride = _standaloneStyle };
        var g = WorldGenerator.Generate(_standaloneSeed, StandaloneSchool, p);
        _world = g.World;
        _kingdoms = g.Kingdoms;
        if (RevealAllForTesting)
            RevealAll();
    }

    /// <summary>Regenerate + repaint the data layers in place. Leaves the debug
    /// panel and camera node alone so the OptionButton selection is preserved.</summary>
    private void RegenerateStandalone()
    {
        GenerateStandaloneWorld();
        BuildTileLayer();
        BuildSettlementLayer();
        BuildBorderLayer();
        BuildEdgeLayer();
        BuildPoiLayer();
        FrameCamera();
        BuildLabelLayer();   // match BuildRender: framed zoom first, then labels
        UpdateDebugInfo();
    }

    private void BuildDebugControls()
    {
        _debugControls?.QueueFree();
        _debugControls = new CanvasLayer { Name = "StandaloneDebugControls" };
        AddChild(_debugControls);

        var panel = new PanelContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 16,
            OffsetTop = 16,
            OffsetRight = 300,
            OffsetBottom = 224,
        };
        panel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Violet));
        _debugControls.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        margin.AddChild(vbox);

        var title = new Label { Text = "Continent (debug)" };
        title.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(title);

        var opt = new OptionButton();
        opt.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        opt.AddItem("Seed-rolled", 0);
        opt.AddItem("Pangaea", 1);
        opt.AddItem("Continents", 2);
        opt.AddItem("Archipelago", 3);
        opt.Select(_standaloneStyle switch
        {
            ContinentStyle.Pangaea => 1,
            ContinentStyle.Continents => 2,
            ContinentStyle.Archipelago => 3,
            _ => 0,
        });
        opt.ItemSelected += OnStyleSelected;
        vbox.AddChild(opt);

        var rerollBtn = new Button { Text = "Reroll seed" };
        rerollBtn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        UITheme.ApplyButtonStyle(rerollBtn, isPrimary: false);
        rerollBtn.Pressed += () =>
        {
            _standaloneSeed = (int)GD.Randi();
            RegenerateStandalone();
        };
        vbox.AddChild(rerollBtn);

        _debugInfoLabel = new Label();
        _debugInfoLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
        _debugInfoLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.6f));
        vbox.AddChild(_debugInfoLabel);

        // ── Lens toggles (debug parity with the real strategic HUD) ──────
        vbox.AddChild(new HSeparator());

        var viewLabel = new Label { Text = "View" };
        viewLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        viewLabel.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(viewLabel);

        _lensButtons.Clear();
        var lensRow = new HBoxContainer();
        lensRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(lensRow);

        AddLensButton(lensRow, "Political", StrategicLens.Political);
        AddLensButton(lensRow, "Terrain", StrategicLens.Terrain);
        AddLensButton(lensRow, "Corruption", StrategicLens.Corruption);
        UpdateLensButtons();

        UpdateDebugInfo();
    }

    private void OnStyleSelected(long idx)
    {
        _standaloneStyle = idx switch
        {
            1 => ContinentStyle.Pangaea,
            2 => ContinentStyle.Continents,
            3 => ContinentStyle.Archipelago,
            _ => (ContinentStyle?)null,
        };
        RegenerateStandalone();
    }

    private void UpdateDebugInfo()
    {
        if (_debugInfoLabel == null || _world == null)
            return;
        string rolled = string.IsNullOrEmpty(_world.ContinentStyle) ? "?" : _world.ContinentStyle;
        _debugInfoLabel.Text = $"seed {_standaloneSeed} · {rolled}";
    }

    // ════════════════════════════════════════════════════════════════════
    // Staging-point deploy (real mode only)
    // ════════════════════════════════════════════════════════════════════

    private Node2D _stagingLayer;
    private CanvasLayer _deployUi;
    private CanvasLayer _hud;
    private StagingPoint _pendingStaging;

    /// <summary>One clickable marker per available staging point. Staging points
    /// are few, so a handful of Area2D markers is cheap (unlike per-tile nodes).</summary>
    private void BuildStagingMarkers()
    {
        _stagingLayer?.QueueFree();
        _stagingLayer = new Node2D { Name = "StagingMarkers", ZIndex = 2 };
        AddChild(_stagingLayer);

        foreach (var sp in _world.StagingPoints)
        {
            if (!sp.Available)
                continue;

            var center = HexCoord.OffsetRenderPosition(sp.X, sp.Y, TilePx)
                         + new Vector2(TilePx * 0.5f, TilePx * 0.5f);

            // Visual: a ringed beacon so it stands out from POI diamonds.
            var marker = new Node2D { Position = center };

            var ring = new Polygon2D
            {
                Polygon = MakeRing(TilePx * 1.6f),
                Color = UITheme.Gold,
            };
            marker.AddChild(ring);

            var core = new Polygon2D
            {
                Polygon = MakeRing(TilePx * 0.7f),
                Color = UITheme.TextPrimary,
            };
            marker.AddChild(core);

            // Clickable area sized to the ring.
            var area = new Area2D();
            var shape = new CollisionShape2D
            {
                Shape = new CircleShape2D { Radius = TilePx * 1.8f },
            };
            area.AddChild(shape);
            var captured = sp;
            area.InputEvent += (viewport, evt, idx) =>
            {
                if (evt is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    OnStagingClicked(captured);
            };
            marker.AddChild(area);

            _stagingLayer.AddChild(marker);
        }
    }

    private void OnStagingClicked(StagingPoint sp)
    {
        _pendingStaging = sp;
        ShowDeployConfirm(sp);
    }

    private void ShowDeployConfirm(StagingPoint sp)
    {
        _deployUi?.QueueFree();
        _deployUi = new CanvasLayer { Name = "DeployUI" };
        AddChild(_deployUi);

        // Dim backdrop.
        var backdrop = new ColorRect { Color = UITheme.BgOverlay };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _deployUi.AddChild(backdrop);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -200,
            OffsetRight = 200,
            OffsetTop = -130,
            OffsetBottom = 130,
        };
        panel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Violet));
        _deployUi.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        var title = new Label { Text = $"Deploy from {sp.Name}" };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        vbox.AddChild(new HSeparator());

        // Context: kingdom + terrain + range.
        var tile = _world.GetTile(sp.X, sp.Y);
        string kingdomLabel = string.IsNullOrEmpty(tile.KingdomId)
            ? "Wilderness"
            : (_kingdoms.TryGetValue(tile.KingdomId, out var ks) && !string.IsNullOrEmpty(ks.ControllingFactionId)
                ? FactionDisplay(ks.ControllingFactionId)
                : tile.KingdomId);

        AddDeployStat(vbox, "Location", $"({sp.X}, {sp.Y}) · {tile.Terrain}");
        AddDeployStat(vbox, "Territory", kingdomLabel);
        AddDeployStat(vbox, "Operating range", $"~{DeployWindowRadius * 2} tiles across");

        // Corruption warning if the staging tile is corrupted.
        if (tile.Corruption >= 20)
        {
            string sev = tile.Corruption >= 60 ? "Heavy" : "Spreading";
            var warn = new Label { Text = $"⚠ {sev} corruption here ({tile.Corruption}/100)." };
            warn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            warn.AddThemeColorOverride("font_color", UITheme.Danger);
            vbox.AddChild(warn);
        }

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        buttons.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttons);

        var cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(120, 40) };
        UITheme.ApplyButtonStyle(cancelBtn, isPrimary: false);
        cancelBtn.Pressed += () => { _deployUi?.QueueFree(); _deployUi = null; _pendingStaging = null; };
        buttons.AddChild(cancelBtn);

        var deployBtn = new Button { Text = "Deploy", CustomMinimumSize = new Vector2(120, 40) };
        UITheme.ApplyButtonStyle(deployBtn, isPrimary: true);
        deployBtn.Pressed += Deploy;
        buttons.AddChild(deployBtn);
    }

    private void AddDeployStat(VBoxContainer parent, string label, string value)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var l = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        l.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        l.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        row.AddChild(l);
        var v = new Label { Text = value };
        v.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        v.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        row.AddChild(v);
        parent.AddChild(row);
    }

    private void Deploy()
    {
        if (_pendingStaging == null)
            return;

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
            return;

        // ── Time advances on deploy: one expedition costs PhasesPerDeploy ──
        // phases (8 per lunation). The lunation BOUNDARY still drives the
        // world tick; the Conjunction remains a real deadline (~32 deploys).
        bool crossedLunation = false;
        for (int i = 0; i < PhasesPerDeploy; i++)
        {
            crossedLunation |= cycle.Calendar.AdvancePhase();
            // SEAM (Phase 4): eclipses land on a specific (lunation, phase).
            // When eclipse resolution exists, check GetEclipseDueNow() here
            // and interrupt the deploy — phase-stepping makes mid-lunation
            // eclipses reachable for the first time.
        }
        SaveManager.MarkDirty();

    if (crossedLunation)
        {
            GD.Print($"[Calendar] New lunation: {cycle.Calendar.CurrentLunation} " +
                     $"({cycle.Calendar.CurrentPhaseName}).");
            // Council resolves BEFORE corruption spreads (§13 order): envoy
            // residency must be computable from missions still live when
            // the moon turned.
            CouncilTick.Tick(cycle);
            // The living world advances one lunation: corruption spreads.
            CorruptionSpread.Tick(cycle.World, cycle.Campaign, cycle.Kingdoms);
        }

        // ── Did this tip the cycle into the Grand Conjunction? ──────────────
        if (cycle.Calendar.ConjunctionReached)
        {
            GD.Print("[Calendar] The Grand Conjunction has come. The cycle ends.");
            _deployUi?.QueueFree();
            _deployUi = null;
            _pendingStaging = null;
            ShowConjunction();
            return;
        }

        SaveManager.SaveIfDirty();

        PlayerSession.ExpeditionStagingCol = _pendingStaging.X;
        PlayerSession.ExpeditionStagingRow = _pendingStaging.Y;
        PlayerSession.ExpeditionWindowRadius = DeployWindowRadius;

        GD.Print($"[StrategicView] Deploying expedition from " +
                 $"'{_pendingStaging.Name}' ({_pendingStaging.X},{_pendingStaging.Y}). " +
                 $"Phase {cycle.Calendar.TotalPhasesElapsed} " +
                 $"(L{cycle.Calendar.CurrentLunation} · {cycle.Calendar.CurrentPhaseName}).");

        GetTree().ChangeSceneToFile("res://Scenes/Overworld/ExpeditionScene.tscn");
    }

    /// <summary>The Grand Conjunction has arrived. For now the cycle simply ends —
    /// no final encounter yet (miniboss + campus assault are a later phase). Show a
    /// beat, then return the player to campus, where the next cycle is begun on
    /// re-entry to the strategic map (school reselection happens there).</summary>
    private void ShowConjunction()
    {
        var panelLayer = new CanvasLayer { Name = "ConjunctionUI" };
        AddChild(panelLayer);

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.0f, 0.04f, 0.92f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        panelLayer.AddChild(backdrop);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -260,
            OffsetRight = 260,
            OffsetTop = -150,
            OffsetBottom = 150,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        panelLayer.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        margin.AddChild(vbox);

        var title = new Label { Text = "The Grand Conjunction" };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        var body = new Label
        {
            Text = "The moons align and the timeline closes. Kassian's alignment completes — " +
                   "this world is unmade. What you have learned endures; the timeline does not.\n\n" +
                   "Return to the campus to begin the next cycle.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        body.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        body.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        vbox.AddChild(body);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var btn = new Button { Text = "Return to Campus", CustomMinimumSize = new Vector2(220, 48) };
        btn.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        UITheme.ApplyButtonStyle(btn, isPrimary: true);
        btn.Pressed += () =>
        {
            // Mark the cycle as ended-by-conjunction so the campus knows to begin a
            // new cycle on next strategic-map entry. We DON'T call BeginNewCycle here
            // because the next school is chosen at the campus.
            PlayerSession.CycleEndedByConjunction = true;
            SaveManager.SaveIfDirty();
            GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn");
        };
        vbox.AddChild(btn);
    }

    private string FactionDisplay(string factionId)
    {
        var def = FactionRegistry.Get(factionId);
        return def != null ? def.DisplayName : factionId;
    }

    /// <summary>A simple filled ring (octagon approximation) for staging markers.</summary>
    private static Vector2[] MakeRing(float radius)
    {
        const int n = 8;
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.Tau * i / n;
            pts[i] = new Vector2(radius * Mathf.Cos(a), radius * Mathf.Sin(a));
        }
        return pts;
    }
}
