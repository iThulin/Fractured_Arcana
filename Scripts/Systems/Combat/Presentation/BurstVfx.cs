using Godot;
using System.Threading.Tasks;

// ============================================================
// BurstVfx.cs
//
// Purpose:        A one-shot burst at a point: a flash sphere that swells and
//                 fades, a spray of additive sparks, and (for ground bursts)
//                 a flat ring that expands along the tile. Used for AoE
//                 detonations, projectile impacts, melee hits and the
//                 caster's release flash — same node, different Radius and
//                 GroundRing. Frees itself when the sparks die.
// Layer:          Systems / Combat / Presentation
// Collaborators:  VfxLibrary (factory), CombatPresenter (caller)
// See:            docs/spell_vfx_pipeline_v1.md §5
// ============================================================

public partial class BurstVfx : Node3D
{
    public Color Tint = VfxLibrary.NeutralTint;

    /// <summary>World-unit radius of the flash and ring. Impacts ~0.35, caster
    /// release ~0.25, single-tile burst ~0.9, radius-1 AoE ~2.0 (one hex across
    /// flats is 2 m).</summary>
    public float Radius = 0.5f;

    /// <summary>Adds the expanding floor ring. On for burst archetypes, off for
    /// impacts in the air.</summary>
    public bool GroundRing;

    /// <summary>Seconds the flash takes to swell and fade.</summary>
    public float FlashDuration = 0.28f;

    private bool _built;

    public override void _Ready()
    {
        if (!_built)
            BuildDefault();
    }

    protected virtual void BuildDefault()
    {
        _built = true;

        // Flash sphere.
        var flash = new MeshInstance3D { Name = "Flash" };
        var sphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 16, Rings = 8 };
        var flashMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoColor = new Color(Tint.R, Tint.G, Tint.B, 0.85f),
            EmissionEnabled = true,
            Emission = Tint,
            EmissionEnergyMultiplier = 2.5f
        };
        sphere.Material = flashMat;
        flash.Mesh = sphere;
        flash.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        flash.Scale = Vector3.One * (Radius * 0.25f);
        AddChild(flash);

        // Sparks.
        var sparks = new GpuParticles3D
        {
            Name = "Sparks",
            Amount = Mathf.Clamp((int)(24 + Radius * 20f), 24, 72),
            Lifetime = 0.55,
            OneShot = true,
            Explosiveness = 0.95f,
            Randomness = 0.4f,
            LocalCoords = false,
            Emitting = false
        };
        var gradient = new Gradient();
        gradient.Offsets = new float[] { 0f, 0.6f, 1f };
        gradient.Colors = new Color[]
        {
            new Color(1f, 1f, 1f, 1f),
            new Color(Tint.R, Tint.G, Tint.B, 0.8f),
            new Color(Tint.R, Tint.G, Tint.B, 0f)
        };
        var pm = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = GroundRing ? 80f : 180f,
            InitialVelocityMin = 1.5f + Radius,
            InitialVelocityMax = 3.0f + Radius * 1.5f,
            Gravity = new Vector3(0f, -4f, 0f),
            DampingMin = 1.0f,
            DampingMax = 2.0f,
            ScaleMin = 0.4f,
            ScaleMax = 0.9f,
            ColorRamp = new GradientTexture1D { Gradient = gradient }
        };
        sparks.ProcessMaterial = pm;
        var quad = new QuadMesh { Size = new Vector2(0.14f, 0.14f) };
        quad.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            AlbedoColor = Colors.White
        };
        sparks.DrawPass1 = quad;
        sparks.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(sparks);

        // Ground ring.
        MeshInstance3D ring = null;
        StandardMaterial3D ringMat = null;
        if (GroundRing)
        {
            ring = new MeshInstance3D { Name = "Ring" };
            var torus = new TorusMesh { InnerRadius = 0.85f, OuterRadius = 1f, Rings = 24, RingSegments = 8 };
            ringMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(Tint.R, Tint.G, Tint.B, 0.9f),
                EmissionEnabled = true,
                Emission = Tint,
                EmissionEnergyMultiplier = 2.0f
            };
            torus.Material = ringMat;
            ring.Mesh = torus;
            ring.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            ring.Position = new Vector3(0f, 0.06f, 0f);
            ring.Scale = new Vector3(0.15f, 0.15f, 0.15f);
            AddChild(ring);
        }

        _flash = flash;
        _flashMat = flashMat;
        _sparks = sparks;
        _ring = ring;
        _ringMat = ringMat;
    }

    private MeshInstance3D _flash;
    private StandardMaterial3D _flashMat;
    private GpuParticles3D _sparks;
    private MeshInstance3D _ring;
    private StandardMaterial3D _ringMat;

    /// <summary>Fire the burst. Resolves when the flash has faded (the sparks keep
    /// going and the node frees itself afterwards).</summary>
    public async Task Play(float speedScale = 1f)
    {
        if (!_built)
            BuildDefault();

        float dur = FlashDuration / Mathf.Max(0.01f, speedScale);

        if (_sparks != null)
        {
            _sparks.Restart();
            _sparks.Emitting = true;
        }

        var tw = CreateTween().SetParallel(true);
        tw.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        if (_flash != null)
        {
            tw.TweenProperty(_flash, "scale", Vector3.One * Radius, dur);
            tw.TweenProperty(_flashMat, "albedo_color:a", 0f, dur);
        }
        if (_ring != null)
        {
            tw.TweenProperty(_ring, "scale", new Vector3(Radius, Radius, Radius), dur * 1.6f);
            tw.TweenProperty(_ringMat, "albedo_color:a", 0f, dur * 1.6f);
        }
        await ToSignal(tw, Tween.SignalName.Finished);

        if (!GodotObject.IsInstanceValid(this))
            return;

        float linger = (float)(_sparks?.Lifetime ?? 0.6) + 0.1f;
        var timer = GetTree().CreateTimer(linger / Mathf.Max(0.01f, speedScale));
        await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        if (GodotObject.IsInstanceValid(this))
            QueueFree();
    }
}
