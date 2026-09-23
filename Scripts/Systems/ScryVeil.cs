using Godot;
using System.Threading.Tasks;

// ============================================================
// ScryVeil.cs
//
// Purpose:        The palantir's breath. Ruled 2026-09-23 (Magos): the
//                 player stares into a stone, scries one of their forces,
//                 and gives it orders; aiming the stone at another force
//                 must never look like a scene loading. Fog rolls in over
//                 the projection, the world underneath is swapped, and the
//                 fog thins to show the new ground.
//
//                 An AUTOLOAD, because it has to outlive the thing it
//                 hides. The expedition scene is torn down and rebuilt
//                 during the switch (see ExpeditionManager.SwitchToForce
//                 for why that road was taken over an in-place swap), and
//                 anything parented inside it would vanish with it and
//                 expose the rebuild. HudManager already proves a
//                 persistent layer survives the change; this sits one
//                 layer beneath it so the global top bar stays visible
//                 while the vision clouds. The frame holds, the stone
//                 fogs.
//
//                 Shape: a full-screen ColorRect on its own CanvasLayer,
//                 driven by one shader uniform (`coverage`) that a Tween
//                 walks 0 -> 1 -> 0. The fog front advances from the rim
//                 toward the centre with a noise-broken edge, and the
//                 body drifts, so it reads as weather over a table rather
//                 than a fade to grey.
//
//                 Safety: RollIn arms a watchdog. If the scene that comes
//                 up never calls Dissipate (a crash, a route that was not
//                 the expedition), the veil lifts itself after a few
//                 seconds rather than leaving the player staring at fog
//                 forever with no control to clear it.
// Layer:          System (autoload)
// Collaborators:  ExpeditionManager (RollIn before the switch, Dissipate
//                 once the new run is built), UITheme (colours),
//                 project.godot [autoload]
// See:            docs/session_log_2026-09-21_expedition_v2_step1_data_layer.md
// ============================================================

/// <summary>Fog over the scrying stone, persistent across scene changes.</summary>
public partial class ScryVeil : Node
{
    public static ScryVeil Instance { get; private set; }

    /// <summary>Below HudManager (90) so the world bar stays readable through
    /// the switch, above everything the expedition draws.</summary>
    private const int VeilLayer = 89;

    /// <summary>How long nobody may leave the fog down before it lifts itself.
    /// Long enough for a slow rebuild on a bad frame, short enough that a stuck
    /// veil is an annoyance and not a soft lock.</summary>
    private const float WatchdogSeconds = 6f;

    public const float DefaultRollInSeconds = 0.75f;
    public const float DefaultDissipateSeconds = 1.1f;

    private CanvasLayer _layer;
    private ColorRect _rect;
    private ShaderMaterial _mat;
    private Tween _tween;
    private float _coverage;
    private int _watchdogToken;

    /// <summary>True while any fog is on screen. The expedition reads this on
    /// entry to decide whether it is arriving through the stone.</summary>
    public bool IsCovering => _coverage > 0.01f;

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;   // the veil must move while a scene is loading

        _layer = new CanvasLayer { Layer = VeilLayer, Name = "ScryVeilLayer" };
        AddChild(_layer);

        var noise = new NoiseTexture2D
        {
            Noise = new FastNoiseLite
            {
                NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
                Frequency = 0.012f,
                FractalOctaves = 4,
            },
            Seamless = true,
            Width = 512,
            Height = 512,
        };

        _mat = new ShaderMaterial { Shader = new Shader { Code = VeilShader } };
        _mat.SetShaderParameter("noise_tex", noise);
        _mat.SetShaderParameter("fog_color", UITheme.ScryVeilFog);
        _mat.SetShaderParameter("glow_color", UITheme.ScryVeilGlow);
        _mat.SetShaderParameter("coverage", 0f);

        _rect = new ColorRect
        {
            Name = "ScryVeil",
            Material = _mat,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(_rect);
    }

    // ── The two verbs ────────────────────────────────────────────────────

    /// <summary>Fog rolls in from the rim until the stone is blind. Awaitable:
    /// the caller changes scene only once this completes, so the rebuild is
    /// never visible.</summary>
    public async Task RollIn(float seconds = DefaultRollInSeconds)
    {
        // While the fog is down, nothing under it may take a click. The veil
        // is Ignore while clear so it never costs a click on an ordinary frame.
        _rect.MouseFilter = Control.MouseFilterEnum.Stop;
        _rect.Visible = true;
        await Animate(1f, seconds);
        ArmWatchdog();
    }

    /// <summary>Fog thins from the centre out to reveal whatever is now under
    /// it. Cancels the watchdog: the scene that called this is alive and has
    /// something to show.</summary>
    public async Task Dissipate(float seconds = DefaultDissipateSeconds)
    {
        _watchdogToken++;
        await Animate(0f, seconds);
        _rect.Visible = false;
        _rect.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    /// <summary>Snap to fully fogged with no animation. For a caller that has
    /// already rolled in and wants to be certain nothing peeks through on the
    /// frame the scene swaps.</summary>
    public void HoldOpaque()
    {
        _tween?.Kill();
        SetCoverage(1f);
        _rect.Visible = true;
        _rect.MouseFilter = Control.MouseFilterEnum.Stop;
    }

    // ── Internals ────────────────────────────────────────────────────────

    private async Task Animate(float target, float seconds)
    {
        _tween?.Kill();
        _tween = CreateTween();
        _tween.SetProcessMode(Tween.TweenProcessMode.Idle);
        _tween.SetPauseMode(Tween.TweenPauseMode.Process);   // keeps moving under a loading pause
        _tween.TweenMethod(Callable.From<float>(SetCoverage), _coverage, target, seconds)
              .SetTrans(Tween.TransitionType.Sine)
              .SetEase(target > _coverage ? Tween.EaseType.In : Tween.EaseType.Out);
        await ToSignal(_tween, Tween.SignalName.Finished);
    }

    private void SetCoverage(float c)
    {
        _coverage = Mathf.Clamp(c, 0f, 1f);
        _mat?.SetShaderParameter("coverage", _coverage);
    }

    private void ArmWatchdog()
    {
        int token = ++_watchdogToken;
        GetTree().CreateTimer(WatchdogSeconds, processAlways: true).Timeout += () =>
        {
            if (token != _watchdogToken || !IsCovering)
            {
                return;
            }
            GD.PrintErr("[ScryVeil] Nothing lifted the veil. Lifting it.");
            _ = Dissipate();
        };
    }

    /// <summary>The fog front walks from the rim (d ~ 1) to the centre (d ~ 0)
    /// as coverage rises, its edge broken by noise so it never reads as a clean
    /// iris wipe. A second, slower noise drifts through the body so the fog
    /// moves while it sits. At full coverage the body is forced solid, because
    /// the rebuild underneath must not show through an 8 percent gap.</summary>
    private const string VeilShader = @"
shader_type canvas_item;

uniform sampler2D noise_tex : repeat_enable, filter_linear;
uniform float coverage : hint_range(0.0, 1.0) = 0.0;
uniform vec4 fog_color : source_color = vec4(0.72, 0.74, 0.80, 1.0);
uniform vec4 glow_color : source_color = vec4(0.55, 0.45, 0.85, 1.0);

void fragment() {
    vec2 uv = UV;
    // Aspect-corrected distance from the centre so the front is a ring on a
    // wide screen rather than an ellipse that closes top and bottom first.
    vec2 c = (uv - 0.5) * vec2(1.7, 1.0);
    float d = length(c) / 0.95;

    float n  = texture(noise_tex, uv * 2.5 + vec2(TIME * 0.035, TIME * 0.022)).r;
    float n2 = texture(noise_tex, uv * 1.2 - vec2(TIME * 0.018, TIME * 0.011)).r;

    // Front position in d-space: past the rim at coverage 0, past the centre at 1.
    float front = 1.30 - coverage * 1.75;
    float edge = smoothstep(front - 0.32, front + 0.08, d + (n - 0.5) * 0.5);

    // Drifting body, forced solid once the stone is meant to be blind.
    float body = mix(0.86 + 0.14 * n2, 1.0, smoothstep(0.82, 1.0, coverage));

    // A faint arcane bloom just behind the front: the stone working.
    float bloom = smoothstep(front - 0.05, front + 0.20, d) * (1.0 - smoothstep(front + 0.20, front + 0.55, d));
    vec3 col = mix(fog_color.rgb, glow_color.rgb, bloom * 0.35 * (1.0 - coverage * 0.6));

    COLOR = vec4(col, edge * body * fog_color.a);
}
";
}
