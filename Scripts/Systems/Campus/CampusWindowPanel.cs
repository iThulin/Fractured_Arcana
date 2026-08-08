using Godot;
using static CampusUi;

// ============================================================
// CampusWindowPanel.cs
//
// Purpose:        The Window test tab — hosts the ExpeditionWindow3D
//                 prototype so the 3D EXPEDITION view (a bounded run
//                 window with fog, a party pawn, and click-to-move)
//                 can be judged before wiring it into deploy. Sibling
//                 of the Atlas tab; where Atlas renders the whole world
//                 read-only, this renders one walkable window from the
//                 same models the live run uses. Standalone by default
//                 (the renderer self-generates); a later step hands it
//                 a live run's WorldData + fog/overlay models.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 ExpeditionWindow3D.cs (the renderer),
//                 CampusAtlasPanel.cs (the sibling world view)
// See:            docs/atlas_expedition_convergence_v1.md (Stage 2)
// ============================================================

/// <summary>Window test tab: a full-height isolated SubViewport hosting a walkable
/// ExpeditionWindow3D. Lazy — nothing builds until the tab is first shown. A lighter
/// post pass than the Atlas tab (vignette + warm grade, no tilt-shift: the window is a
/// place you're IN, not a model you look down on).</summary>
public sealed class CampusWindowPanel : CampusPanel
{
    private SubViewportContainer _viewportContainer;
    private ExpeditionWindow3D _window;
    private Label _readout;
    private Button _filmicButton;
    private ShaderMaterial _postMaterial;
    private bool _filmicOn = true;
    private bool _built;
    private int _reseed = 12345;

    // Lighter post than the Atlas tilt-shift: at window scale the miniature blur
    // fights immersion. Just a warm-graded vignette + a whisper of aberration.
    private const string PostShaderCode = @"
shader_type canvas_item;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.34;
uniform float aberration : hint_range(0.0, 4.0) = 0.9;
uniform vec3  warm_tint = vec3(1.04, 0.99, 0.92);
uniform float grade_mix : hint_range(0.0, 1.0) = 0.35;
void fragment() {
    vec2 uv = UV;
    vec2 fromCenter = uv - vec2(0.5);
    vec2 ca = fromCenter * aberration * TEXTURE_PIXEL_SIZE;
    vec3 col = vec3(texture(TEXTURE, uv + ca).r, texture(TEXTURE, uv).g, texture(TEXTURE, uv - ca).b);
    col = mix(col, col * warm_tint, grade_mix);
    float vig = 1.0 - vignette_strength * smoothstep(0.5, 0.95, length(fromCenter) * 1.4142);
    col *= vig;
    COLOR = vec4(col, 1.0);
}
";

    protected override void OnBuild(ScrollContainer scroll)
    {
        var root = MakeVBox(8);
        root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        root.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.AddChild(root);

        var header = MakeMargins(24, 12);
        root.AddChild(header);
        var headerBox = MakeVBox(6);
        header.AddChild(headerBox);

        AddSectionHeader(headerBox, "Expedition Window — 3D Walk Prototype");

        var hint = new Label
        {
            Text = "One expedition window, rendered from the SAME data a live run uses " +
                   "(world + fog model + overlay model). Fog is law: unseen is void, charted " +
                   "shows shape, explored shows contents. Click an adjacent tile to walk the " +
                   "party — vision reveals as you go, and move costs are the real numbers the " +
                   "run charges. Drag to pan · scroll to zoom.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        hint.Modulate = UITheme.CampusSubtleText;
        headerBox.AddChild(hint);

        var controls = new HBoxContainer();
        controls.AddThemeConstantOverride("separation", 8);
        headerBox.AddChild(controls);

        var reseedBtn = MakeButton("New Window", 120, 32, UITheme.CampusSmallFontSize, isPrimary: false);
        reseedBtn.Pressed += OnReseed;
        controls.AddChild(reseedBtn);

        _filmicButton = MakeButton("Filmic: On", 104, 32, UITheme.CampusSmallFontSize, isPrimary: false);
        _filmicButton.Pressed += OnFilmicPressed;
        controls.AddChild(_filmicButton);

        _readout = new Label { Text = "Click a tile to walk the party." };
        _readout.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _readout.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        headerBox.AddChild(_readout);

        _viewportContainer = new SubViewportContainer
        {
            Stretch = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 480),
        };
        root.AddChild(_viewportContainer);

        _postMaterial = new ShaderMaterial { Shader = new Shader { Code = PostShaderCode } };
        _viewportContainer.Material = _postMaterial;

        // Hover gating wired ONCE (null-guarded — _window may be unbuilt); the
        // reseed path can then rebuild the viewport freely without re-subscribing.
        _viewportContainer.MouseEntered += () => { if (_window != null) _window.AcceptInput = true; };
        _viewportContainer.MouseExited += () => { if (_window != null) _window.AcceptInput = false; };
    }

    /// <summary>Lazy build on first show — same discipline as the Atlas tab.</summary>
    public override void Refresh()
    {
        if (_viewportContainer == null || !_viewportContainer.IsVisibleInTree() || _built)
            return;
        BuildViewport();
        _built = true;
    }

    /// <summary>Create a fresh isolated viewport + window at the current seed. On reseed
    /// the previous viewport is freed first, so two cameras never share one SubViewport.</summary>
    private void BuildViewport()
    {
        foreach (var child in _viewportContainer.GetChildren())
            child.QueueFree();

        var viewport = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X };
        _viewportContainer.AddChild(viewport);

        _window = new ExpeditionWindow3D { Standalone = true, StandaloneSeed = _reseed };
        _window.PartyMoved += _ => UpdateReadout();
        // AddChild runs the window's _Ready synchronously (viewport is in-tree), so
        // its standalone world is populated by the time we read it. (CampusPanel is a
        // plain object, not a Node — no CallDeferred available anyway.)
        viewport.AddChild(_window);
        UpdateReadout();
    }

    private void OnReseed()
    {
        if (!_built) return;
        // Vary the seed so "New Window" shows different ground. Deterministic step
        // (no Date/RNG dependency): just increment.
        _reseed += 101;
        BuildViewport();
    }

    private void OnFilmicPressed()
    {
        _filmicOn = !_filmicOn;
        _viewportContainer.Material = _filmicOn ? _postMaterial : null;
        if (_filmicButton != null) _filmicButton.Text = _filmicOn ? "Filmic: On" : "Filmic: Off";
    }

    private void UpdateReadout()
    {
        if (_readout != null && _window != null)
            _readout.Text = _window.DescribeParty();
    }
}
