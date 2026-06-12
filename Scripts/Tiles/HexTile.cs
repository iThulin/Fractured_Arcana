using Godot;
using System;

// ============================================================
// HexTile.cs
//
// Purpose:        Visual Node3D for one hex tile on the combat
//                 grid. Renders the mesh (legacy cylinder OR the
//                 generated blended terrain mesh), handles hover/
//                 highlight tinting, manages the imbuement overlay
//                 and glyph indicator, and shows the debug coord
//                 label. Pure visual layer — game state lives on
//                 the paired TileData.
// Layer:          Tiles
// Collaborators:  TileData.cs (1:1 data sibling, via TileView),
//                 ImbuementOverlay.cs (child scene),
//                 UITheme.cs (highlight colours),
//                 HexMeshBuilder.cs (generated blended mesh),
//                 terrain_splat.gdshader (textured terrain material),
//                 HexGridManager.cs (instantiates and positions tiles)
// See:            README §8 — CallDeferred rules apply to glyph
//                 child addition (see ShowGlyph)
//
// Highlight architecture:
//   ALL highlight/hover/overlay tinting flows through SetTint,
//   which writes to whichever material is active:
//   - StandardMaterial3D (legacy cylinder, or vertex-colour
//     blended mesh): emission channel. Albedo is never tinted —
//     it would multiply against vertex colours / textures.
//   - ShaderMaterial (terrain_splat.gdshader): the per-tile
//     duplicate's highlight_color / highlight_strength uniforms,
//     which feed EMISSION in the shader.
//   The flag state machine and the public API are unchanged from
//   the original albedo-based implementation.
// ============================================================

/// <summary>
/// Visual Node3D for one hex tile. Handles per-tile material duplication,
/// emission-based hover/highlight tinting (StandardMaterial3D or splat
/// ShaderMaterial), the layered highlight state machine
/// (deployment / movement / range / target / drag), and ownership of the
/// <see cref="ImbuementOverlay"/> child plus the optional glyph label. All highlight
/// colours come from <see cref="UITheme"/>.
/// </summary>
public partial class HexTile : Node3D
{
    /// <summary>Colour blended onto the tile's tint when the mouse is over it. Defaults to the central UITheme value but is overridable in the inspector for special tiles.</summary>
    [Export] public Color HoverColor = UITheme.TileHover;

    /// <summary>When true, the coord/terrain label is shown in 3D space above the tile (debug only).</summary>
    [Export] public bool ShowDebugInfo = true;

    /// <summary>Optional override for the imbuement overlay scene. If unset, the default at <see cref="DefaultOverlayScenePath"/> is loaded.</summary>
    [Export] public PackedScene ImbuementOverlayScene;

    /// <summary>Emission energy applied to highlight tints. UITheme colours were tuned as albedo overlays; lower this if highlights glow too hot.</summary>
    [Export(PropertyHint.Range, "0.1,3,0.05")] public float HighlightEmissionEnergy = 1.0f;

    private const string DefaultOverlayScenePath = "res://Scenes/Combat/ImbuementOverlay.tscn";

    // Cached nodes and materials
    private MeshInstance3D meshInstance;
    private Transform3D _meshOriginalTransform;
    private float _meshOriginalDepth;
    public const float HeightStep = 0.6f;
    private StandardMaterial3D material;
    private ShaderMaterial _shaderMaterial;
    private Label3D coordLabel;
    private Label3D _glyphLabel;
    private Label3D _memorialLabel;
    private Color baseColor;

    /// <summary>The current tint (transparent black = none). Tracked here so the state machine never needs to read material state back, regardless of which material type is active.</summary>
    private Color _currentTint = new Color(0f, 0f, 0f, 0f);

    /// <summary>True once SetGeneratedMesh has installed a blended terrain mesh: SetHeight stops stretching the cylinder and SetBaseColor stops driving albedo.</summary>
    private bool _generatedMode = false;

    private ImbuementOverlay imbuementOverlay;
    private MemorialState? _memorialState = null;

    // Memorial overlay color constants
    private static readonly Color MemorialFreshColor = new Color(0.85f, 0.82f, 0.6f, 0.55f);
    private static readonly Color MemorialEstablishedColor = new Color(0.75f, 0.75f, 0.55f, 0.38f);
    private static readonly Color MemorialHallowedColor = new Color(0.95f, 0.92f, 0.7f, 0.65f);
    private static readonly Color MemorialNoneColor = new Color(0f, 0f, 0f, 0f);

    // Growth mechaniscs for duid
    private int _growthStage = 0;
    private Label3D _growthLabel;

    /// <summary>Axial (q, r) coordinate identifying this tile's grid position.</summary>
    public Vector2I Axial { get; set; }

    // Highlighting states
    private bool _isHighlighted = false;
    private Color _preHighlightColor;
    private bool deploymentHighlighted = false;
    private bool moveHighlighted = false;
    private Color _moveHighlightColor = UITheme.TileMoveHighlight; // default
    private bool targetHighlighted = false;
    private bool rangeHighlighted = false;
    private bool rangeBorderHighlighted = false;

    /// <summary>Colour used when a draggable card is hovered over this tile during targeting.</summary>
    [Export] public Color DragHoverColor = UITheme.TileDragHover;

    /// <summary>Back-pointer to this tile's TileData. Set by HexGridManager during grid generation.</summary>
    public TileData Data { get; set; }

    public override void _Ready()
    {
        meshInstance = GetNode<MeshInstance3D>("HexMesh");
        _meshOriginalTransform = meshInstance.Transform;
        // Origin.Y = -0.5 in the scene; depth = distance from top (Y=0) to bottom = -2 × origin.Y = 1.0
        _meshOriginalDepth = Mathf.Max(-2f * _meshOriginalTransform.Origin.Y, HeightStep);

        coordLabel = GetNode<Label3D>("CoordLabel");

        // Get the material and cache base color
        var sharedMaterial = meshInstance.GetActiveMaterial(0) as StandardMaterial3D;
        if (sharedMaterial != null)
        {
            material = (StandardMaterial3D)sharedMaterial.Duplicate();
            meshInstance.SetSurfaceOverrideMaterial(0, material);
            baseColor = material.AlbedoColor;
        }

        var area = GetNode<StaticBody3D>("StaticBody3D");
        area.MouseEntered += OnMouseEntered;
        area.MouseExited += OnMouseExited;

        EnsureImbuementOverlay();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Tint plumbing — the single channel all highlights flow through.
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>True when some material is available to receive highlight tints.</summary>
    private bool HasTintTarget => material != null || _shaderMaterial != null;

    /// <summary>Current tint, or transparent black when none is applied.</summary>
    private Color GetTint() => _currentTint;

    /// <summary>Applies a highlight tint via whichever material is active. Near-black clears the highlight (resting state).</summary>
    private void SetTint(Color c)
    {
        _currentTint = c;
        bool off = c.R + c.G + c.B <= 0.004f;

        if (_shaderMaterial != null)
        {
            _shaderMaterial.SetShaderParameter("highlight_color", new Vector3(c.R, c.G, c.B));
            _shaderMaterial.SetShaderParameter("highlight_strength", off ? 0f : HighlightEmissionEnergy);
            return;
        }

        if (material == null)
            return;

        if (off)
        {
            material.EmissionEnabled = false;
            return;
        }

        material.EmissionEnabled = true;
        material.Emission = c;
        material.EmissionEnergyMultiplier = HighlightEmissionEnergy;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Generated blended mesh
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Installs a HexMeshBuilder-generated blended terrain mesh. The mesh is
    /// authored in tile-local space (top surface at Y = 0), so the MeshInstance
    /// transform resets to identity — the legacy cylinder's 30° rotation,
    /// Y-stretch, and -0.5 origin no longer apply.
    ///
    /// Pass a ShaderMaterial template (the grid's terrain_splat material) to
    /// pair with a splat-mode mesh — it is duplicated per tile so highlight
    /// uniforms stay independent. Pass null to pair with a vertex-colour mesh
    /// via a white-albedo StandardMaterial3D.
    /// </summary>
    public void SetGeneratedMesh(ArrayMesh mesh, Material template = null)
    {
        if (meshInstance == null || mesh == null)
            return;

        _generatedMode = true;
        meshInstance.Transform = Transform3D.Identity;
        meshInstance.Mesh = mesh;

        if (template is ShaderMaterial sm)
        {
            _shaderMaterial = (ShaderMaterial)sm.Duplicate();
            material = null;
            meshInstance.SetSurfaceOverrideMaterial(0, _shaderMaterial);
        }
        else
        {
            _shaderMaterial = null;
            material = new StandardMaterial3D
            {
                AlbedoColor = Colors.White,
                VertexColorUseAsAlbedo = true,
                VertexColorIsSrgb = true,
                Roughness = 0.85f
            };
            meshInstance.SetSurfaceOverrideMaterial(0, material);
        }

        baseColor = Colors.White;
        RefreshVisualState();
    }

    private void EnsureImbuementOverlay()
    {
        // Already a child? Use it.
        imbuementOverlay = GetNodeOrNull<ImbuementOverlay>("ImbuementOverlay");
        if (imbuementOverlay != null)
            return;

        var scene = ImbuementOverlayScene
                    ?? GD.Load<PackedScene>(DefaultOverlayScenePath);
        if (scene == null)
        {
            GD.PushWarning($"HexTile {Axial}: ImbuementOverlay scene not found.");
            return;
        }

        imbuementOverlay = scene.Instantiate<ImbuementOverlay>();
        imbuementOverlay.Name = "ImbuementOverlay";
        AddChild(imbuementOverlay);
    }

    private void OnMouseEntered()
    {
        if (!HasTintTarget)
            return;
        // Blend hover on top of the current tint (highlight override or resting)
        SetTint(GetTint().Lerp(HoverColor, 0.5f));

        // ── Tooltip ──────────────────────────────────────────────
        if (Data != null)
            TooltipManager.Instance?.ShowTileTooltip(Data);
    }

    private void OnMouseExited()
    {
        if (_isHighlighted)
        {
            if (rangeBorderHighlighted)
                SetTint(UITheme.TileRangeBorder);
            else if (rangeHighlighted)
                SetTint(UITheme.TileRangeInterior);
            else if (targetHighlighted)
                SetTint(UITheme.TileTargetHighlight);
        }
        else
        {
            RefreshVisualState();
        }

        // ── Tooltip ──────────────────────────────────────────────
        TooltipManager.Instance?.HideTileTooltip();
    }

    /// <summary>Replaces the tile's material with a per-tile duplicate (legacy path; not used while a generated mesh is installed). Pass a StandardMaterial3D for the standard hover/highlight path; other material types disable the tinting features.</summary>
    public void SetMaterial(Material newMaterial)
    {
        if (meshInstance == null || newMaterial == null)
            return;

        _shaderMaterial = null;

        if (newMaterial is StandardMaterial3D stdMat)
        {
            material = (StandardMaterial3D)stdMat.Duplicate();
            meshInstance.SetSurfaceOverrideMaterial(0, material);
            baseColor = material.AlbedoColor;
        }
        else
        {
            meshInstance.SetSurfaceOverrideMaterial(0, newMaterial);
            material = null;
        }
    }

    public void SetHeight(int height, float worldFloor = -1.0f)
    {
        float tileTop = height * HeightStep;

        // Move the tile's origin to its top surface — units, props, raycasts unaffected.
        Position = new Vector3(Position.X, tileTop, Position.Z);

        // Generated mode: the blended mesh handles its own depth (skirts on
        // map edges, watertight surface elsewhere) — no cylinder stretch.
        if (_generatedMode)
            return;

        // How far down the cylinder must reach in local HexTile space.
        // (tileTop - worldFloor) is in world units = local units since HexTile scale = 1.)
        float requiredDepth = Mathf.Max(tileTop - worldFloor, _meshOriginalDepth);
        float yScaleRatio = requiredDepth / _meshOriginalDepth;

        // Scale the MeshInstance3D transform — never the shared CylinderMesh resource.
        // Basis.Y is the (0, 3, 0) column from the scene; length = 3 = original Y scale.
        float newYScale = _meshOriginalTransform.Basis.Y.Length() * yScaleRatio;
        float newYOrigin = -requiredDepth * 0.5f; // top stays at local Y=0

        var origBasis = _meshOriginalTransform.Basis;
        meshInstance.Transform = new Transform3D(
            new Basis(
                origBasis.X,
                origBasis.Y.Normalized() * newYScale, // stretch Y, preserve rotation
                origBasis.Z
            ),
            new Vector3(
                _meshOriginalTransform.Origin.X,
                newYOrigin,
                _meshOriginalTransform.Origin.Z
            )
        );
    }

    /// <summary>Sets both the <see cref="Axial"/> coordinate and the visible debug label.</summary>
    public void SetCoordinatesLabel(int q, int r)
    {
        Axial = new Vector2I(q, r);
        coordLabel.Text = $"({q}, {r})";
    }

    /// <summary>Sets the tile's resting albedo (legacy flat-colour path). In generated-mesh mode this is a no-op — terrain colour lives in the mesh's vertex data and textures.</summary>
    public void SetBaseColor(Color color)
    {
        baseColor = color;

        if (!_generatedMode && material != null)
            material.AlbedoColor = color;

        RefreshVisualState();
    }

    /// <summary>
    /// Sets the imbuement element shown above this tile. Pass
    /// <see cref="TileElementType.None"/> to hide the overlay.
    /// </summary>
    public void SetElement(TileElementType element)
    {
        if (imbuementOverlay == null)
            EnsureImbuementOverlay();

        imbuementOverlay?.SetElement(element);
    }

    /// <summary>Lazily creates a billboarded glyph label above the tile and makes it visible. Uses <c>CallDeferred("add_child", ...)</c> to comply with the Godot 4.6 cross-platform safety rule (see README §8).</summary>
    public void ShowGlyph()
    {
        if (_glyphLabel == null)
        {
            _glyphLabel = new Label3D();
            _glyphLabel.Text = "✦";
            _glyphLabel.FontSize = UITheme.Label3DGlyph;
            _glyphLabel.Modulate = UITheme.TileGlyph;
            _glyphLabel.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            _glyphLabel.Position = new Vector3(0, 0.6f, 0);
            _glyphLabel.Name = "GlyphIndicator";
            CallDeferred("add_child", _glyphLabel);
        }
        else
        {
            _glyphLabel.Visible = true;
        }
    }

    /// <summary>Hides the glyph indicator without destroying it. Cheap to re-show via <see cref="ShowGlyph"/>.</summary>
    public void ClearGlyph()
    {
        if (_glyphLabel != null)
            _glyphLabel.Visible = false;
    }

    /// <summary>Current elemental imbuement displayed by the overlay child, or <see cref="TileElementType.None"/> if no overlay is present.</summary>
    public TileElementType CurrentElement =>
        imbuementOverlay?.CurrentElement ?? TileElementType.None;

    /// <summary>Rebuilds the debug coord label text from the paired <see cref="TileData"/>. No-op when <see cref="ShowDebugInfo"/> is false.</summary>
    public void RefreshLabel(TileData tileData)
    {
        if (coordLabel == null || tileData == null)
            return;

        if (!ShowDebugInfo)
        {
            coordLabel.Text = "";
            return;
        }

        string terrain = tileData.TerrainType.ToString();
        string element = tileData.ElementType.ToString();

        if (tileData.ElementType == TileElementType.None)
            element = "-";

        string blocked = tileData.IsBlocked ? "Yes" : "No";

        coordLabel.Text =
            $"({tileData.Axial.X}, {tileData.Axial.Y})\n" +
            $"Type: {terrain}\n" +
            $"Imbue: {element}\n" +
            $"Block: {blocked}\n" +
            $"H: {tileData.Height}";
    }

    /// <summary>Toggles the soft deployment-zone tint blended into the highlight overlay.</summary>
    public void SetDeploymentHighlight(bool enabled)
    {
        deploymentHighlighted = enabled;
        RefreshVisualState();
    }

    /// <summary>Toggles the movement highlight. When enabled, the movement highlight colour is blended into the highlight overlay. Use <see cref="SetMoveHighlightColored"/> to set a custom colour for this highlight (e.g. to distinguish player vs ally vs dash reachability).</summary>
    public void SetMoveHighlight(bool enabled)
    {
        if (!enabled)
            _moveHighlightColor = UITheme.TileMoveHighlight; // reset to default on clear
        moveHighlighted = enabled;
        RefreshVisualState();
    }

    /// <summary>Sets a custom colour for the movement highlight overlay, then enables it. Used to distinguish player vs ally vs reachable-via-dash highlights at the gameplay level.</summary>
    public void SetMoveHighlightColored(Color color)
    {
        if (!HasTintTarget)
            return;
        _moveHighlightColor = color;
        moveHighlighted = true;
        RefreshVisualState();
    }

    /// <summary>Toggles the targeting highlight (used when a card is being aimed at this tile). Saves and restores the prior tint so the highlight is non-destructive.</summary>
    public void SetTargetHighlight(bool enabled)
    {
        targetHighlighted = enabled;

        if (enabled && !_isHighlighted)
        {
            _preHighlightColor = GetTint();
            _isHighlighted = true;
        }
        else if (!enabled && _isHighlighted)
        {
            _isHighlighted = false;
            SetTint(_preHighlightColor);
            return;
        }

        if (enabled)
            SetTint(UITheme.TileTargetHighlight);
    }

    /// <summary>Toggles the range-preview highlight. Pass <paramref name="border"/> true for the edge of the area, <paramref name="interior"/> true for tiles inside the area. Both false clears the highlight.</summary>
    public void SetRangeHighlight(bool interior, bool border)
    {
        rangeHighlighted = interior;
        rangeBorderHighlighted = border;

        if ((interior || border) && !_isHighlighted)
        {
            _preHighlightColor = GetTint();
            _isHighlighted = true;
        }
        else if (!interior && !border && _isHighlighted)
        {
            _isHighlighted = false;
            SetTint(_preHighlightColor);
            return;
        }

        if (!HasTintTarget)
            return;

        if (border)
            SetTint(UITheme.TileRangeBorder);
        else if (interior)
            SetTint(UITheme.TileRangeInterior);
    }

    /// <summary>Applies the drag-hover tint when a card is being dragged over this tile. Restores the prior state when <paramref name="on"/> is false.</summary>
    public void SetDragHoverHighlight(bool on)
    {
        if (!HasTintTarget)
            return;
        if (on)
            SetTint(DragHoverColor);
        else
            RefreshVisualState(); // restore base/range/target state
    }

    /// <summary>Recomputes the current highlight tint from the layered flags (deployment → move → memorial → growth). No tint active = highlight off; the terrain (vertex colours, textures, or legacy albedo) shows untouched. No-op while a target/range highlight is active — those override.</summary>
    public void RefreshVisualState()
    {
        if (!HasTintTarget)
            return;
        if (_isHighlighted)
            return;

        Color tint = new Color(0f, 0f, 0f, 0f);

        if (deploymentHighlighted)
            tint = tint.Lerp(UITheme.TileDeployHighlight, 0.55f);
        if (moveHighlighted)
            tint = tint.Lerp(_moveHighlightColor, 0.55f);

        // ── Memorial overlay ──────────────────────────────────────────
        if (_memorialState.HasValue)
        {
            Color memColor = _memorialState.Value switch
            {
                MemorialState.Fresh => MemorialFreshColor,
                MemorialState.Established => MemorialEstablishedColor,
                MemorialState.Hallowed => MemorialHallowedColor,
                _ => MemorialNoneColor
            };
            // Lerp into the tint rather than overriding it —
            // the ground still reads as grass/stone/etc underneath
            tint = tint.Lerp(memColor, memColor.A);
        }

        // ── Growth overlay (Druid living terrain) ─────────────────────
        if (_growthStage > 0)
        {
            Color growthColor = _growthStage switch
            {
                1 => UITheme.GrowthSapling,
                2 => UITheme.GrowthThicket,
                _ => UITheme.GrowthOldGrowth
            };
            tint = tint.Lerp(growthColor, growthColor.A);
        }

        SetTint(tint);
    }

    /// <summary>
    /// Updates the tile's living-growth visual — ground tint plus a floating green
    /// pip that grows brighter and larger by stage. Pass 0 to clear. The pip is a
    /// separate node, so it stays visible even while the tile is highlighted (when
    /// the ground tint is suppressed by RefreshVisualState).
    /// 1 = sapling, 2 = thicket, 3 = old growth.
    /// </summary>
    public void SetGrowth(int stage)
    {
        _growthStage = Mathf.Clamp(stage, 0, 3);
        RefreshVisualState();

        if (_growthStage <= 0)
        {
            if (_growthLabel != null)
                _growthLabel.Visible = false;
            return;
        }

        UpdateGrowthLabel(_growthStage);
    }

    private void UpdateGrowthLabel(int stage)
    {
        // Filled dot, larger/brighter as growth matures. Round shape + green colour
        // read distinctly from the memorial star.
        int fontSize = stage switch { 1 => 28, 2 => 38, _ => 52 };
        float alpha = stage switch { 1 => 0.55f, 2 => 0.80f, _ => 1.0f };
        string symbol = stage == 1 ? "•" : "●";

        Color tint = UITheme.GrowthPip;
        tint.A = alpha;

        if (_growthLabel == null)
        {
            _growthLabel = new Label3D
            {
                Name = "GrowthIndicator",
                Text = symbol,
                FontSize = fontSize,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Position = new Vector3(0f, 0.7f, 0f),
                Modulate = tint
            };
            CallDeferred("add_child", _growthLabel);
        }
        else
        {
            _growthLabel.Visible = true;
            _growthLabel.Text = symbol;
            _growthLabel.FontSize = fontSize;
            _growthLabel.Modulate = tint;
        }
    }

    /// <summary>
    /// Updates the tile's memorial visual state — both the ground tint and the
    /// floating symbol. Pass null to clear all memorial visuals.
    /// </summary>
    public void SetMemorial(MemorialData memorial)
    {
        if (memorial == null)
        {
            _memorialState = null;
            ClearMemorialLabel();
            RefreshVisualState();
            return;
        }

        _memorialState = memorial.State;
        RefreshVisualState();
        UpdateMemorialLabel(memorial.State);
    }

    private void UpdateMemorialLabel(MemorialState state)
    {
        // Symbol and opacity keyed to memorial strength.
        // ✦ = solid four-pointed star (strongest signal).
        // ✧ = outline star (medium).
        // · = faint dot (weakest).
        string symbol = state switch
        {
            MemorialState.Hallowed => "✦",
            MemorialState.Fresh => "✧",
            MemorialState.Established => "·",
            _ => ""
        };

        float alpha = state switch
        {
            MemorialState.Hallowed => 0.95f,
            MemorialState.Fresh => 0.70f,
            MemorialState.Established => 0.40f,
            _ => 0f
        };

        if (string.IsNullOrEmpty(symbol) || alpha <= 0f)
        {
            ClearMemorialLabel();
            return;
        }

        if (_memorialLabel == null)
        {
            _memorialLabel = new Label3D
            {
                Name = "MemorialIndicator",
                Text = symbol,
                FontSize = 48,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                // Sit just above the imbuement overlay height
                Position = new Vector3(0f, 0.85f, 0f),
                Modulate = new Color(0.92f, 0.88f, 0.72f, alpha),
            };
            CallDeferred("add_child", _memorialLabel);
        }
        else
        {
            _memorialLabel.Visible = true;
            _memorialLabel.Text = symbol;
            _memorialLabel.Modulate = new Color(0.92f, 0.88f, 0.72f, alpha);
        }
    }

    private void ClearMemorialLabel()
    {
        if (_memorialLabel != null)
            _memorialLabel.Visible = false;
    }

}
