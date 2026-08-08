using Godot;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusAtlasPanel.cs
//
// Purpose:        The Atlas tab — hosts the WorldAtlas3D prototype
//                 so the 3D terrain read of the world can be judged
//                 side by side with the 2D strategic map. Renders
//                 the SAME WorldData the strategic scene renders:
//                 the active cycle's world when one exists, else a
//                 standalone world from StrategicView's default
//                 seed (12345) so the two views show identical
//                 geography. Read-only by design — deploying stays
//                 on the strategic scene while the comparison runs.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 WorldAtlas3D.cs (the renderer), WorldGenerator.cs
//                 (standalone fallback), StrategicView.cs (the 2D
//                 counterpart being compared against)
// See:            single_world_refactor_v2.docx §4.2
// ============================================================

/// <summary>Atlas tab: a full-height SubViewport (own World3D — the campus grid lives in
/// the shell's world and must not co-render here) with lens buttons, a reveal toggle, and a
/// click-to-inspect readout. World generation is LAZY: nothing is built until the tab is
/// first shown, so campus boot pays no cost for a comparison prototype.</summary>
public sealed class CampusAtlasPanel : CampusPanel
{
    /// <summary>Matches StrategicView.StandaloneSeed's default so the standalone fallback
    /// world here is byte-identical to the one the strategic scene generates standalone.</summary>
    private const int StandaloneSeed = 12345;

    private SubViewportContainer _viewportContainer;
    private WorldAtlas3D _atlas;
    private Label _status;
    private Label _tileInfo;
    private readonly Dictionary<StrategicLens, Button> _lensButtons = new();
    private Button _revealButton;
    private Button _filmicButton;
    private ShaderMaterial _postMaterial;
    private bool _filmicOn = true;

    // ── Pass 2: screen-space post on the container ───────────────────────────
    // The Compatibility renderer has NO CameraAttributes depth-of-field, so the
    // miniature look is faked where it's actually cheaper and more controllable:
    // a canvas_item shader on the SubViewportContainer, whose TEXTURE is the
    // rendered 3D scene. One pass does tilt-shift blur bands (the "eight inches
    // wide" cue), a vignette, a GENTLE warm grade (grade_mix is deliberately low —
    // terrain hues must stay distinguishable; this harmonizes, it does not mask),
    // and a whisper of chromatic aberration. Every uniform is a knob; the Filmic
    // button A/Bs the whole layer.
    private const string PostShaderCode = @"
shader_type canvas_item;

uniform float blur_strength : hint_range(0.0, 8.0) = 3.0;    // max blur radius, px
uniform float focus_center  : hint_range(0.0, 1.0) = 0.55;   // screen y of the sharp band
uniform float focus_width   : hint_range(0.0, 0.6) = 0.16;   // half-height of the sharp band
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.32;
uniform float aberration    : hint_range(0.0, 4.0) = 1.1;    // px of RGB split at edges
uniform vec3  warm_tint     = vec3(1.05, 0.98, 0.90);
uniform float grade_mix     : hint_range(0.0, 1.0) = 0.45;   // keep LOW: hues stay readable

void fragment() {
    vec2 uv = UV;
    vec2 fromCenter = uv - vec2(0.5);

    // Tilt-shift: blur grows with distance from a horizontal focus band.
    float band = abs(uv.y - focus_center);
    float blur01 = smoothstep(focus_width, focus_width + 0.25, band);
    float radius = blur01 * blur_strength;

    vec2 ca = fromCenter * aberration * TEXTURE_PIXEL_SIZE;

    vec3 col;
    if (radius < 0.01) {
        col = vec3(texture(TEXTURE, uv + ca).r,
                   texture(TEXTURE, uv).g,
                   texture(TEXTURE, uv - ca).b);
    } else {
        vec2 px = TEXTURE_PIXEL_SIZE * radius;
        vec3 acc = texture(TEXTURE, uv).rgb * 2.0;
        acc += texture(TEXTURE, uv + vec2( px.x, 0.0)).rgb;
        acc += texture(TEXTURE, uv + vec2(-px.x, 0.0)).rgb;
        acc += texture(TEXTURE, uv + vec2(0.0,  px.y)).rgb;
        acc += texture(TEXTURE, uv + vec2(0.0, -px.y)).rgb;
        acc += texture(TEXTURE, uv + vec2( px.x,  px.y) * 0.7).rgb;
        acc += texture(TEXTURE, uv + vec2(-px.x,  px.y) * 0.7).rgb;
        acc += texture(TEXTURE, uv + vec2( px.x, -px.y) * 0.7).rgb;
        acc += texture(TEXTURE, uv + vec2(-px.x, -px.y) * 0.7).rgb;
        col = acc / 10.0;
        col.r = mix(col.r, texture(TEXTURE, uv + ca).r, 0.5);
        col.b = mix(col.b, texture(TEXTURE, uv - ca).b, 0.5);
    }

    // Gentle warm grade — harmonizes toward lamplight without collapsing hue.
    col = mix(col, col * warm_tint, grade_mix);

    // Vignette — the model sits in a pool of light; the room around it is dark.
    float vig = 1.0 - vignette_strength * smoothstep(0.45, 0.95, length(fromCenter) * 1.4142);
    col *= vig;

    COLOR = vec4(col, 1.0);
}
";

    /// <summary>Standalone world cache — generating 96×96 takes real time; do it once
    /// per app run, not per tab visit. Static: survives campus screen rebuilds.</summary>
    private static GeneratedWorldData _standaloneCache;

    private bool _worldLoaded;

    protected override void OnBuild(ScrollContainer scroll)
    {
        // Fill the scroll viewport instead of hugging content — the map IS the content.
        var root = MakeVBox(8);
        root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        root.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.AddChild(root);

        var header = MakeMargins(24, 12);
        root.AddChild(header);
        var headerBox = MakeVBox(6);
        header.AddChild(headerBox);

        AddSectionHeader(headerBox, "World Atlas — 3D Terrain Prototype");

        var hint = new Label
        {
            Text = "The same world the strategic map paints, rendered as terrain: elevation " +
                   "becomes height, discovery stays law (unseen land is a void slab). " +
                   "Drag to pan · scroll to zoom · click a tile to inspect it. " +
                   "Deploying still happens on the strategic map while the two views are compared.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        hint.Modulate = UITheme.CampusSubtleText;
        headerBox.AddChild(hint);

        // Controls: lens buttons mirror the strategic view's lens bar, plus the
        // display-only reveal toggle (StrategicView's debug reveal, as a button).
        var controls = new HBoxContainer();
        controls.AddThemeConstantOverride("separation", 8);
        headerBox.AddChild(controls);

        foreach (StrategicLens lens in System.Enum.GetValues(typeof(StrategicLens)))
        {
            var btn = MakeButton(lens.ToString(), 96, 32, UITheme.CampusSmallFontSize,
                isPrimary: false);
            var captured = lens;
            btn.Pressed += () => OnLensPressed(captured);
            _lensButtons[lens] = btn;
            controls.AddChild(btn);
        }

        controls.AddChild(new VSeparator());

        _revealButton = MakeButton("Reveal: On", 110, 32, UITheme.CampusSmallFontSize,
            isPrimary: false);
        _revealButton.Pressed += OnRevealPressed;
        controls.AddChild(_revealButton);

        _filmicButton = MakeButton("Filmic: On", 104, 32, UITheme.CampusSmallFontSize,
            isPrimary: false);
        _filmicButton.Pressed += OnFilmicPressed;
        controls.AddChild(_filmicButton);

        _status = new Label();
        _status.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _status.Modulate = UITheme.CampusSubtleText;
        controls.AddChild(_status);

        _tileInfo = new Label { Text = "Click a tile to inspect it." };
        _tileInfo.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _tileInfo.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        headerBox.AddChild(_tileInfo);

        // The viewport. OwnWorld3D is the load-bearing property: the campus SubViewport
        // leaves it false (its 3D nodes are the only scene in the shared world), but a
        // SECOND 3D view must isolate its world or both cameras render both scenes.
        _viewportContainer = new SubViewportContainer
        {
            Stretch = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 480),
        };
        root.AddChild(_viewportContainer);

        // Pass 2 post layer — see PostShaderCode's comment block.
        _postMaterial = new ShaderMaterial { Shader = new Shader { Code = PostShaderCode } };
        _viewportContainer.Material = _postMaterial;

        var viewport = new SubViewport
        {
            OwnWorld3D = true,
            // Same reason as the campus viewport: a code-built SubViewport does NOT
            // inherit project MSAA; without this the prism edges stair-step.
            Msaa3D = Viewport.Msaa.Msaa4X,
        };
        _viewportContainer.AddChild(viewport);

        _atlas = new WorldAtlas3D();
        _atlas.TilePicked += OnTilePicked;
        // Stage 1: the miniature blur RELAXES as the camera comes down — at ground
        // level the world should read as a place, not a model.
        _atlas.ZoomChanged += OnZoomChanged;
        viewport.AddChild(_atlas);

        // Hover-gated input, the campus map's own discipline: wheel and drags must not
        // reach the atlas while the pointer is over the header or another panel.
        _viewportContainer.MouseEntered += () => _atlas.AcceptInput = true;
        _viewportContainer.MouseExited += () => _atlas.AcceptInput = false;

        UpdateControlStates();
    }

    /// <summary>Load lazily on first show; on later refreshes, re-point at the cycle
    /// world in case a new cycle replaced it. Skips entirely while the tab is hidden —
    /// RefreshAll runs on every campus refresh and must not pay for this tab.</summary>
    public override void Refresh()
    {
        if (_viewportContainer == null || !_viewportContainer.IsVisibleInTree())
            return;
        LoadWorld();
    }

    private void LoadWorld()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        WorldData world;
        Dictionary<string, KingdomState> kingdoms;
        string source;

        if (cycle?.World != null && cycle.World.Tiles != null
            && cycle.World.Tiles.Length == cycle.World.Width * cycle.World.Height
            && cycle.World.Tiles.Length > 0)
        {
            world = cycle.World;
            kingdoms = cycle.Kingdoms;
            source = "cycle world";
        }
        else
        {
            // No live cycle (fresh save / no slot): show the standalone world on
            // StrategicView's default seed so both renderers agree on geography.
            if (_standaloneCache == null)
            {
                string school = SaveManager.ActiveSave?.SelectedSchool;
                if (string.IsNullOrEmpty(school))
                    school = "Elementalist";
                _standaloneCache = WorldGenerator.Generate(StandaloneSeed, school);
            }
            world = _standaloneCache.World;
            kingdoms = _standaloneCache.Kingdoms;
            source = $"standalone (seed {StandaloneSeed})";
        }

        _atlas.SetWorld(world, kingdoms);
        _worldLoaded = true;
        if (_status != null)
            _status.Text = $"  {world.Width}×{world.Height} · {source}";
    }

    private void OnLensPressed(StrategicLens lens)
    {
        if (!_worldLoaded) return;
        _atlas.SetLens(lens);
        UpdateControlStates();
    }

    private void OnRevealPressed()
    {
        if (!_worldLoaded) return;
        _atlas.SetRevealAll(!_atlas.RevealAll);
        UpdateControlStates();
    }

    /// <summary>A/B the whole filmic layer (tilt-shift, vignette, grade, aberration)
    /// by attaching/detaching the material — no per-uniform bookkeeping.</summary>
    private void OnFilmicPressed()
    {
        _filmicOn = !_filmicOn;
        _viewportContainer.Material = _filmicOn ? _postMaterial : null;
        UpdateControlStates();
    }

    private void UpdateControlStates()
    {
        foreach (var (lens, btn) in _lensButtons)
            btn.Modulate = lens == _atlas.Lens
                ? new Color(1f, 1f, 1f)
                : new Color(0.65f, 0.65f, 0.72f);
        if (_revealButton != null)
            _revealButton.Text = _atlas.RevealAll ? "Reveal: On" : "Reveal: Off";
        if (_filmicButton != null)
            _filmicButton.Text = _filmicOn ? "Filmic: On" : "Filmic: Off";
    }

    private void OnZoomChanged(float zoom01)
    {
        if (_postMaterial == null)
            return;
        _postMaterial.SetShaderParameter("blur_strength", Mathf.Lerp(0.5f, 3.2f, zoom01));
        _postMaterial.SetShaderParameter("focus_width", Mathf.Lerp(0.42f, 0.16f, zoom01));
    }

    private void OnTilePicked(int col, int row)
    {
        if (_tileInfo == null)
            return;
        _tileInfo.Text = _atlas.DescribeTile(col, row)
            + (_atlas.PreviewActive
                ? "    ⚑ window preview — click inside the ring to walk the ghost party · click outside to dismiss"
                : "");
    }
}
