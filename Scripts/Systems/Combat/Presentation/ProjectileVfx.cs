using Godot;
using System.Threading.Tasks;

// ============================================================
// ProjectileVfx.cs
//
// Purpose:        A travelling spell/arrow visual: an emissive core with a
//                 particle trail that flies from the caster to a point, then
//                 lets its trail die out and frees itself. Built from
//                 primitives in _Ready when no authored scene overrides it
//                 (VfxLibrary). Awaitable: the presenter holds the damage
//                 numbers until Launch() completes.
// Layer:          Systems / Combat / Presentation
// Collaborators:  VfxLibrary (factory), CombatPresenter (caller)
// See:            docs/spell_vfx_pipeline_v1.md §5
// ============================================================

public partial class ProjectileVfx : Node3D
{
    /// <summary>School colour. Set before AddChild; read in _Ready.</summary>
    public Color Tint = VfxLibrary.NeutralTint;

    /// <summary>True for Delivery.Arc (lobbed parabola); false for a straight bolt.</summary>
    public bool Lobbed;

    /// <summary>World units per second. Bolts are fast and flat; arcs are slower and
    /// read as "willed". Duration is clamped to [MinDuration, MaxDuration] so a
    /// 1-tile cast still reads and a 6-tile cast does not drag.</summary>
    public float BoltSpeed = 14f;
    public float ArcSpeed = 9f;
    public const float MinDuration = 0.22f;
    public const float MaxDuration = 0.60f;

    /// <summary>Seconds the trail is allowed to fade after arrival before QueueFree.</summary>
    public float TrailLinger = 0.45f;

    private MeshInstance3D _core;
    private GpuParticles3D _trail;
    private Vector3 _from;
    private Vector3 _to;
    private float _arcHeight;
    private bool _built;

    public override void _Ready()
    {
        if (!_built)
            BuildDefault();
    }

    /// <summary>Procedural default look. Authored scenes override this by having
    /// their own children and setting _built in their own script, or simply by
    /// providing a "Core" MeshInstance3D and "Trail" GPUParticles3D child.</summary>
    protected virtual void BuildDefault()
    {
        _built = true;

        _core = GetNodeOrNull<MeshInstance3D>("Core");
        if (_core == null)
        {
            _core = new MeshInstance3D { Name = "Core" };
            var mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 12, Rings = 6 };
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = Tint.Lightened(0.35f),
                EmissionEnabled = true,
                Emission = Tint,
                EmissionEnergyMultiplier = 3.0f
            };
            mesh.Material = mat;
            _core.Mesh = mesh;
            _core.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(_core);
        }

        _trail = GetNodeOrNull<GpuParticles3D>("Trail");
        if (_trail == null)
        {
            _trail = new GpuParticles3D
            {
                Name = "Trail",
                Amount = 36,
                Lifetime = 0.40,
                Explosiveness = 0f,
                Randomness = 0.3f,
                LocalCoords = false,     // world space: the trail hangs in the air behind us
                Emitting = true
            };

            var gradient = new Gradient();
            gradient.Offsets = new float[] { 0f, 1f };
            gradient.Colors = new Color[]
            {
                new Color(Tint.R, Tint.G, Tint.B, 0.9f),
                new Color(Tint.R, Tint.G, Tint.B, 0f)
            };
            var ramp = new GradientTexture1D { Gradient = gradient };

            var pm = new ParticleProcessMaterial
            {
                Direction = Vector3.Zero,
                Spread = 180f,
                InitialVelocityMin = 0.1f,
                InitialVelocityMax = 0.4f,
                Gravity = Vector3.Zero,
                DampingMin = 1.0f,
                DampingMax = 1.5f,
                ScaleMin = 0.35f,
                ScaleMax = 0.7f,
                ColorRamp = ramp
            };
            _trail.ProcessMaterial = pm;

            var quad = new QuadMesh { Size = new Vector2(0.18f, 0.18f) };
            quad.Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                VertexColorUseAsAlbedo = true,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                AlbedoColor = Colors.White
            };
            _trail.DrawPass1 = quad;
            _trail.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(_trail);
        }
    }

    /// <summary>Fly from <paramref name="from"/> to <paramref name="to"/>. Resolves
    /// when the core arrives; the node frees itself after the trail lingers.
    /// <paramref name="speedScale"/> > 1 fast-forwards (presenter skip).</summary>
    public async Task Launch(Vector3 from, Vector3 to, float speedScale = 1f)
    {
        _from = from;
        _to = to;
        GlobalPosition = from;

        float dist = from.DistanceTo(to);
        float speed = Lobbed ? ArcSpeed : BoltSpeed;
        float dur = Mathf.Clamp(dist / Mathf.Max(0.01f, speed), MinDuration, MaxDuration);
        dur /= Mathf.Max(0.01f, speedScale);
        _arcHeight = Lobbed ? Mathf.Clamp(dist * 0.25f, 0.35f, 1.6f) : 0f;

        var tw = CreateTween();
        tw.SetEase(Lobbed ? Tween.EaseType.InOut : Tween.EaseType.Out)
          .SetTrans(Lobbed ? Tween.TransitionType.Sine : Tween.TransitionType.Quad);
        tw.TweenMethod(Callable.From<float>(Step), 0f, 1f, dur);
        await ToSignal(tw, Tween.SignalName.Finished);

        if (!GodotObject.IsInstanceValid(this))
            return;

        if (_core != null)
            _core.Visible = false;
        if (_trail != null)
            _trail.Emitting = false;

        var linger = GetTree().CreateTimer(TrailLinger / Mathf.Max(0.01f, speedScale));
        await ToSignal(linger, SceneTreeTimer.SignalName.Timeout);
        if (GodotObject.IsInstanceValid(this))
            QueueFree();
    }

    private void Step(float t)
    {
        Vector3 p = _from.Lerp(_to, t);
        if (_arcHeight > 0f)
            p.Y += _arcHeight * 4f * t * (1f - t);   // parabola, peak at t = 0.5
        GlobalPosition = p;
    }
}
