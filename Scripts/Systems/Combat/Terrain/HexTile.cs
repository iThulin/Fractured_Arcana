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
    private MeshInstance3D _glyphDecal;
    private ShaderMaterial _glyphDecalMaterial;
    private Label3D _memorialLabel;

    /// <summary>Generic point-of-interest marker (see <see cref="SetPoiLabel"/>).
    /// Independent of <see cref="_memorialLabel"/> — a tile may carry both, so this
    /// is a separate node rather than a shared one with swapped text.</summary>
    private Label3D _poiLabel;

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
    private bool threatHighlighted = false;
    private bool telegraphHighlighted = false;
    private string _terrainScar = "";
    private float _pulsePhase = 0f;
    private bool threatRevealed = true;   // hot vs dim threat tint (see SetThreatHighlight)

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
        SetProcess(false);   // only runs while a telegraph pulse is active
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

    /// <summary>Configures this tile as a non-playable VISTA tile (see
    /// HexGridManager.Vista.cs): no collision on any layer (can't be hovered,
    /// clicked, or card-targeted), no coordinate label, and the terrain splat's
    /// per-instance `vista_fade` uniform set so grid lines vanish and the tile
    /// renders de-emphasized. <paramref name="horizonBlend"/> (0 = inner ring,
    /// 1 = outermost) drives the per-instance horizon melt toward the theme fog
    /// colour. Call once, right after AddChild.</summary>
    public void MarkAsVista(float horizonBlend = 0f)
    {
        var body = GetNodeOrNull<StaticBody3D>("StaticBody3D");
        if (body != null)
        {
            body.CollisionLayer = 0;
            body.CollisionMask = 0;
        }

        var area = GetNodeOrNull<Area3D>("Area3D");
        if (area != null)
        {
            area.CollisionLayer = 0;
            area.CollisionMask = 0;
            area.Monitoring = false;
            area.Monitorable = false;
        }

        var label = GetNodeOrNull<Label3D>("CoordLabel");
        if (label != null)
            label.Visible = false;

        var mi = GetNodeOrNull<MeshInstance3D>("HexMesh");
        if (mi != null)
        {
            mi.SetInstanceShaderParameter("vista_fade", 1.0f);
            mi.SetInstanceShaderParameter("vista_horizon", Mathf.Clamp(horizonBlend, 0f, 1f));
        }
    }

    /// <summary>The tile's current resting albedo, as last set by <see cref="SetBaseColor"/>
    /// (or read off the material at init). Read-only accessor for existing state — lets a
    /// caller tint RELATIVE to whatever the terrain pass already decided, instead of
    /// duplicating the terrain→colour mapping on its own side. Meaningless in
    /// generated-mesh mode, where colour lives in the mesh rather than the albedo.</summary>
    public Color BaseColor => baseColor;

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

        // Record it board-wide so the TERRAIN can respond — snow settling on the
        // grass, fire burning it away. Separate from the overlay on purpose: the
        // overlay is what stands ON this tile, the field is what this tile does to
        // the world around it, and grass is chunked 3x3 tiles per MultiMesh so it
        // can only be reached through a world-space lookup.
        ImbuementField.SetTile(GetInstanceId(), GlobalPosition, element);
    }

    /// <summary>Edge length in pixels of the baked glyph-cipher decal. 256 rather than 128:
    /// the sigil now lies flat on the ground where the camera can get close to it, and the
    /// shader blurs its alpha for the halo, which magnifies any softness in the source.</summary>
    private const int GlyphDecalPixels = 256;

    /// <summary>Width of the ground decal in world units. The baked sigil occupies the
    /// inner 80% of this (see the shader's sigil_scale); the rest is the enclosing ring.</summary>
    private const float GlyphDecalSize = 1.50f;

    /// <summary>Clearance above the tile's measured top surface. Enough to stay out of the
    /// terrain's z-fighting range and above the grass roots, low enough that the sigil still
    /// reads as lying ON the ground rather than hovering over it.</summary>
    private const float GlyphDecalHeight = 0.055f;

    /// <summary>Seconds for the inscription when a glyph is first prepared. Six arms are
    /// struck in sequence, so this is ~0.16s per stave — quick, but slow enough to read as
    /// deliberate drawing rather than a fade-in.</summary>
    private const float GlyphInscribeSeconds = 0.95f;

    private const string GlyphDecalShaderPath = "res://Assets/Shaders/glyph_sigil.gdshader";

    /// <summary>
    /// Shows the tile's glyph marker. Pass the <see cref="GlyphData"/> when it is known and
    /// the tile draws that spell's generated cipher sigil; call it bare and it falls back to
    /// the plain ✦ label.
    ///
    /// The fallback is not a stub — it is the correct behaviour for every glyph the cipher
    /// cannot name. Legacy PlaceGlyphEffect glyphs, Runic Cascade's self-spread copies and
    /// anything placed outside the cast pipeline carry no source card, and a tile with no
    /// marker at all would be a gameplay bug. Uses <c>CallDeferred("add_child", ...)</c> to
    /// comply with the Godot 4.6 cross-platform safety rule (see README §8).
    /// </summary>
    public void ShowGlyph(GlyphData glyph = null)
    {
        // Put the fallback marker up FIRST, unconditionally. The cipher decal is baked
        // asynchronously and can fail — no texture service, an unknown blueprint, a
        // driver that returns an empty viewport — and a tile with no marker at all is a
        // gameplay bug, not a cosmetic one. The ✦ stays until a real sigil is in hand
        // to replace it.
        ShowFallbackMarker();

        if (glyph == null
            || string.IsNullOrEmpty(glyph.SourceCardId)
            || GlyphCipherTexture.Instance == null)
            return;

        EnsureGlyphDecal();
        if (_glyphDecal == null)
            return;                               // shader missing — keep the ✦

        // The bake takes two frames cold and is cached, so a repeat placement of the
        // same spell resolves immediately. The nodes may be freed before the callback
        // lands if the tile is torn down mid-bake, hence the validity checks.
        var decal = _glyphDecal;
        var mat = _glyphDecalMaterial;
        var label = _glyphLabel;
        GlyphCipherTexture.Instance.RequestForBlueprint(
            glyph.SourceCardId, glyph.SourceHalf, GlyphDecalPixels, CipherLod.Tile, true,
            tex =>
            {
                if (tex == null || !IsInstanceValid(decal) || mat == null)
                    return;                       // bake failed — keep the ✦
                mat.SetShaderParameter("sigil_tex", tex);
                decal.Visible = true;
                if (IsInstanceValid(label))
                    label.Visible = false;

                // Inscribe it. The shader strikes one arm per beat, clockwise from the
                // top — the cipher's own reading order — drawing each stave outward from
                // the hub, closing the ring behind it, and sealing the hub last.
                //
                // LINEAR on purpose: an eased tween front-loads the motion, which makes
                // the first arms flash past and the last ones crawl. A sequence of equal
                // strokes has to advance at an equal rate to read as a sequence.
                mat.SetShaderParameter("progress", 0f);
                CreateTween()
                    .TweenProperty(mat, "shader_parameter/progress", 1.0f, GlyphInscribeSeconds)
                    .SetTrans(Tween.TransitionType.Linear);
            });
    }

    /// <summary>
    /// The local-space Y of this tile's actual top surface, measured from the HexMesh's
    /// AABB rather than assumed. Same technique <see cref="ImbuementOverlay"/> uses, and
    /// for the same reason: it survives Hex_mesh scale changes, blocker variants and
    /// SetHeight adjustments instead of silently sinking into or floating above them.
    /// </summary>
    private float MeasuredTileTopY()
    {
        var hexMesh = GetNodeOrNull<MeshInstance3D>("HexMesh");
        if (hexMesh?.Mesh == null)
            return 0f;

        var aabb = hexMesh.Mesh.GetAabb();
        var t = hexMesh.Transform;
        return (aabb.Position.Y + aabb.Size.Y) * t.Basis.Y.Length() + t.Origin.Y;
    }

    private void ShowFallbackMarker()
    {
        if (_glyphDecal != null)
            _glyphDecal.Visible = false;

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

    private void EnsureGlyphDecal()
    {
        if (_glyphDecal != null)
            return;

        var shader = GD.Load<Shader>(GlyphDecalShaderPath);
        if (shader == null)
        {
            GD.PushWarning($"HexTile {Axial}: {GlyphDecalShaderPath} not found — glyph falls back to the marker.");
            return;
        }

        _glyphDecalMaterial = new ShaderMaterial { Shader = shader };
        _glyphDecalMaterial.SetShaderParameter("glow_color", UITheme.CipherFunction);
        _glyphDecalMaterial.SetShaderParameter("ring_color", UITheme.CipherFunction);
        _glyphDecalMaterial.SetShaderParameter("backing_color", UITheme.CipherTileBacking);
        _glyphDecalMaterial.SetShaderParameter("progress", 0f);

        // Per-tile phase, so a field of prepared glyphs breathes out of step instead of
        // throbbing in unison. Derived from the axial coords, so it is stable across
        // saves rather than reshuffling every load.
        int h = (Axial.X * 73856093) ^ (Axial.Y * 19349663);
        _glyphDecalMaterial.SetShaderParameter("phase", Mathf.Abs(h % 1000) / 1000f * Mathf.Tau);

        _glyphDecal = new MeshInstance3D
        {
            Name = "GlyphSigil",
            Mesh = new QuadMesh { Size = new Vector2(GlyphDecalSize, GlyphDecalSize) },
            // A QuadMesh faces +Z. Tipping it -90 degrees about X lays it face-up on the
            // ground plane, which is the whole point: this is a rune sketched on the
            // earth, not a billboard turning to watch the camera.
            RotationDegrees = new Vector3(-90f, 0f, 0f),
            Position = new Vector3(0f, MeasuredTileTopY() + GlyphDecalHeight, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _glyphDecalMaterial,
            Visible = false,
        };
        CallDeferred("add_child", _glyphDecal);
    }

    /// <summary>Hides the glyph marker without destroying it. Cheap to re-show via <see cref="ShowGlyph"/>.</summary>
    public void ClearGlyph()
    {
        if (_glyphLabel != null)
            _glyphLabel.Visible = false;
        if (_glyphDecal != null)
            _glyphDecal.Visible = false;
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

    /// <summary>Toggles the threat-tile tint (enemy intent footprint). Lowest layer
    /// in the highlight stack. Two tiers: <paramref name="revealed"/> = hot
    /// (UITheme.TileThreat, full details known); unrevealed = dim reticle
    /// (UITheme.TileThreatDim — you see WHERE it aims, not how hard).</summary>
    public void SetThreatHighlight(bool on, bool revealed = true)
    {
        threatHighlighted = on;
        threatRevealed = revealed;
        RefreshVisualState();
    }

    /// <summary>Battlefield E4: toggles the telegraph tint - a coming destructive map
    /// event will hit this tile next round. Its own layer, so it survives threat/move
    /// recomputes.</summary>
    public void SetTelegraphHighlight(bool on)
    {
        telegraphHighlighted = on;
        if (on)
            _pulsePhase = 0f;
        SetProcess(on);
        RefreshVisualState();
    }

    /// <summary>Battlefield E4: persistent terrain-scar overlay after a tile is converted
    /// mid-fight (collapse/flood/rubble). Emission tint, not a mesh re-skin (terrain colour
    /// is baked into the generated mesh). Pass "" to clear.</summary>
    public void SetTerrainScar(string kind)
    {
        _terrainScar = kind ?? "";
        RefreshVisualState();
    }

    public override void _Process(double delta)
    {
        if (!telegraphHighlighted)
        {
            SetProcess(false);
            return;
        }
        _pulsePhase += (float)delta;
        RefreshVisualState();
    }

    /// <summary>Recomputes the current highlight tint from the layered flags (deployment → move → memorial → growth). No tint active = highlight off; the terrain (vertex colours, textures, or legacy albedo) shows untouched. No-op while a target/range highlight is active — those override.</summary>
    public void RefreshVisualState()
    {
        if (!HasTintTarget)
            return;
        if (_isHighlighted)
            return;

        Color tint = new Color(0f, 0f, 0f, 0f);

        if (threatHighlighted)
        {
            var threat = threatRevealed ? UITheme.TileThreat : UITheme.TileThreatDim;
            tint = tint.Lerp(threat, threat.A);
        }

        if (deploymentHighlighted)
            tint = tint.Lerp(UITheme.TileDeployHighlight, 0.55f);
        if (moveHighlighted)
            tint = tint.Lerp(_moveHighlightColor, 0.55f);
        if (telegraphHighlighted)
        {
            float pa = 0.20f + 0.30f * (0.5f + 0.5f * Mathf.Sin(_pulsePhase * 5f));
            tint = tint.Lerp(UITheme.TileTelegraph, pa);
        }

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

        if (!string.IsNullOrEmpty(_terrainScar))
        {
            Color scar = _terrainScar switch
            {
                "water" => UITheme.TileScarWater,
                "chasm" => UITheme.TileScarChasm,
                _ => UITheme.TileScarRubble
            };
            tint = tint.Lerp(scar, scar.A);
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

    // ═══════════════════════════════════════════════════════════════════════
    // Point-of-interest label (generic)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shows a billboarded text marker above this tile. Campus landmarks are the
    /// only caller today (CampusGridManager.LoadLandmarks); nothing combat-side
    /// uses it yet.
    ///
    /// Takes a plain string and Color rather than any campus type ON PURPOSE: this
    /// is a shared combat node, and the campus keeps its own concepts on its own
    /// side of the boundary — the same rule CampusGridManager follows by holding
    /// _buildableMask / _buildingAtHex as parallel dictionaries instead of adding
    /// campus fields to TileData. The caller maps its state to a colour first.
    ///
    /// Independent of the memorial indicator; a tile can display both at once.
    /// Passing null or empty text clears the label.
    /// </summary>
    /// <param name="fontSize">0 (default) uses <see cref="UITheme.Label3DPoi"/>, sized for a
    /// two-character marker. Everything on the campus map — buildings and landmarks alike —
    /// passes <see cref="UITheme.Label3DPlaceName"/> instead, because it labels with a full
    /// name rather than initials.</param>
    public void SetPoiLabel(string text, Color tint, int fontSize = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            ClearPoiLabel();
            return;
        }

        int size = fontSize > 0 ? fontSize : UITheme.Label3DPoi;

        if (_poiLabel == null)
        {
            _poiLabel = new Label3D
            {
                Name = "PoiLabel",
                Text = text,
                FontSize = size,
                // A billboarded world-space label cannot know what is behind it — grass,
                // a violet building tile, or the skybox. An outline is the only thing that
                // makes it legible against all three, so it is not optional styling.
                OutlineSize = UITheme.Label3DOutlineSize,
                OutlineModulate = UITheme.Label3DOutline,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                // The campus grounds render at 1/3 scale (city view), which shrinks a default
                // world-sized label to an unreadable few pixels. Bump PixelSize to counter the
                // scale so building/landmark names read in city view, and use a linear+mipmap
                // filter so they don't render pixelated when minified at map zoom.
                PixelSize = 0.009f,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
                // Sits above the memorial indicator (0.85) so the two never overlap
                // on a tile that carries both. Raised from 1.15 for the campus map: at a
                // shallow camera pitch a low label is read as sitting on the tile BEHIND it.
                Position = new Vector3(0f, UITheme.Label3DPoiHeight, 0f),
                Modulate = tint,
            };
            // Deferred for the same reason the memorial label is: SetPoiLabel can be
            // called during grid construction, before this node is inside the tree.
            CallDeferred("add_child", _poiLabel);
        }
        else
        {
            _poiLabel.Visible = true;
            _poiLabel.Text = text;
            _poiLabel.Modulate = tint;
            _poiLabel.FontSize = size;   // a reused tile may have carried a different size
            _poiLabel.OutlineSize = UITheme.Label3DOutlineSize;
            _poiLabel.OutlineModulate = UITheme.Label3DOutline;
        }
    }

    /// <summary>Hides the point-of-interest label. Safe on a tile that never had one.</summary>
    public void ClearPoiLabel()
    {
        if (_poiLabel != null)
            _poiLabel.Visible = false;
    }

}
