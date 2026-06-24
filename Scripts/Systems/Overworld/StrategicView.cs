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

    private WorldData _world;
    private System.Collections.Generic.Dictionary<string, KingdomState> _kingdoms = new();
    private MultiMeshInstance2D _tileLayer;
    private MultiMeshInstance2D _poiLayer;
    private Camera2D _camera;

    // Camera control
    private bool _dragging;
    private Vector2 _dragLast;
    private float _zoom = 1f;
    private const float ZoomMin = 0.25f, ZoomMax = 4f, ZoomStep = 1.15f;

    // Index bookkeeping for live recolor.
    private readonly Dictionary<int, int> _poiInstanceOfPoi = new(); // poiIndex → poi MultiMesh instance

    public override void _Ready()
    {
        if (_world == null)
        {
            if (Standalone)
            {
                // Isolated testing: generate a throwaway world so the scene renders alone.
                var g = WorldGenerator.Generate(StandaloneSeed, StandaloneSchool);
                _world = g.World;
                _kingdoms = g.Kingdoms;
                if (RevealAllForTesting)
                    RevealAll();
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
        BuildPoiLayer();
        if (!Standalone)
        {
            BuildStagingMarkers();
            BuildHud();
        }
        FrameCamera();
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
        var visible = new List<int>();
        for (int i = 0; i < _world.Pois.Count; i++)
            if (_world.Pois[i].Discovered)
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

    // ── Color logic ──────────────────────────────────────────────────────
    private Color TileColor(WorldTile t)
    {
        // Discovery first: unexplored is void.
        if (t.Discovery == TileDiscovery.Unseen)
            return UITheme.StrategicUnseen;

        if (t.Discovery == TileDiscovery.Charted)
        {
            // Known shape, not yet explored: faction hue at low strength over
            // a dim base, so you can see WHOSE land it is before exploring it.
            bool ownedLand = t.Terrain != OverworldHex.TerrainType.Water &&
                             !string.IsNullOrEmpty(t.KingdomId);
            Color baseC = ownedLand ? FactionColorForKingdom(t.KingdomId) : TerrainColor(t.Terrain);
            return baseC.Lerp(UITheme.StrategicCharted, 0.55f);
        }

        // Explored: faction control is the PRIMARY read at strategic scale.
        // Terrain only modulates brightness slightly; corruption washes on top.
        bool isLand = t.Terrain != OverworldHex.TerrainType.Water;

        Color c;
        if (isLand && !string.IsNullOrEmpty(t.KingdomId))
        {
            // Faction color is the base. Terrain shifts its luminance a little
            // so forests read darker than grassland WITHIN a territory, without
            // washing out which faction owns the tile.
            Color faction = FactionColorForKingdom(t.KingdomId);
            float lum = TerrainLuminance(t.Terrain);          // ~0.7..1.15
            c = new Color(
                Mathf.Clamp(faction.R * lum, 0f, 1f),
                Mathf.Clamp(faction.G * lum, 0f, 1f),
                Mathf.Clamp(faction.B * lum, 0f, 1f),
                1f);
        }
        else
        {
            // Wilderness / water: terrain color, no faction.
            c = TerrainColor(t.Terrain);
        }

        if (t.Corruption > 0)
        {
            float k = Mathf.Clamp(t.Corruption / 3f, 0f, 1f) * 0.55f;
            c = c.Lerp(UITheme.StrategicCorruption, k);
        }

        return c;
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
        _ => UITheme.TextPrimary,
    };

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
        if (tile.Corruption > 0)
        {
            var warn = new Label { Text = $"⚠ Corruption level {tile.Corruption} in this region." };
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

        PlayerSession.ExpeditionStagingCol = _pendingStaging.X;
        PlayerSession.ExpeditionStagingRow = _pendingStaging.Y;
        PlayerSession.ExpeditionWindowRadius = DeployWindowRadius;

        GD.Print($"[StrategicView] Deploying expedition from " +
                 $"'{_pendingStaging.Name}' ({_pendingStaging.X},{_pendingStaging.Y}).");

        GetTree().ChangeSceneToFile("res://Scenes/Overworld/ExpeditionScene.tscn");
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
