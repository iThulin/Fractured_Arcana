using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CastleAnimator.cs
//
// Purpose:        The walking castle's idle life and its beats
//                 (castle_defense_v2, part 2: motion). Placeholder
//                 geometry only; everything here is transforms, materials,
//                 and particles driven from code, so the castle breathes
//                 before any mesh is sculpted:
//                   - hull plates and legs breathe (slow bob, phase per
//                     part) and sway a hair;
//                   - chimneys smoke (CpuParticles3D);
//                   - the Heart glows and pulses, faster as it weakens,
//                     with a light that reaches the deck; a flare on the
//                     Heart pulse event;
//                   - the ward lantern flickers; the ballista tracks the
//                     nearest enemy while manned;
//                   - beats: a leg lifts and slams on a stomp, the hull
//                     jolts on a lurch, the stacks belch on a vent.
//                 Bound by CombatManager.CastleDefense after the map is
//                 generated; freed with the grid.
// Layer:          Systems / Combat / Terrain
// Collaborators:  HexGridManager.CityStamps (stamp nodes, grouped),
//                 CombatManager.CastleDefense (Bind, beats, station props),
//                 Unit (the Heart's mesh)
// See:            docs/castle_defense_v1.md §6
// ============================================================

public partial class CastleAnimator : Node3D
{
    public const string HullGroup = "castle_hull";
    public const string LegGroup = "castle_leg";
    public const string StackGroup = "castle_stack";
    public const string StationGroup = "castle_station";

    private sealed class Part
    {
        public Node3D Node;
        public Vector3 Rest;
        public float Phase;
        public float Amp;
    }

    private readonly List<Part> _hull = new();
    private readonly List<Part> _legs = new();
    private readonly List<Node3D> _stacks = new();
    private readonly List<CpuParticles3D> _smoke = new();
    private readonly List<(Node3D root, string kind, Vector2I at)> _stations = new();

    private HexGridManager _grid;
    private Unit _heart;
    private StandardMaterial3D _heartMat;
    private OmniLight3D _heartLight;
    private float _heartFlare;          // extra energy decaying after a pulse
    private float _t;
    private float _trackTimer;
    private Func<Vector2I, Unit> _occupantAt;
    private Func<Vector2I, Unit> _nearestEnemyTo;

    private static readonly Color HeartBase = new(0.85f, 0.30f, 0.12f);
    private static readonly Color HeartGlow = new(1.0f, 0.45f, 0.15f);

    /// <summary>Gather the parts the backdrop and the station pass tagged, and
    /// dress the Heart. Call once the grid has generated and the ward exists.</summary>
    public void Bind(HexGridManager grid, Unit heart,
                     Func<Vector2I, Unit> occupantAt, Func<Vector2I, Unit> nearestEnemyTo)
    {
        _grid = grid;
        _heart = heart;
        _occupantAt = occupantAt;
        _nearestEnemyTo = nearestEnemyTo;

        var tree = GetTree();
        if (tree == null)
            return;

        int i = 0;
        foreach (var n in tree.GetNodesInGroup(HullGroup))
            if (n is Node3D h)
                _hull.Add(new Part { Node = h, Rest = h.Position, Phase = i++ * 0.9f, Amp = 0.035f });
        foreach (var n in tree.GetNodesInGroup(LegGroup))
            if (n is Node3D l)
                _legs.Add(new Part { Node = l, Rest = l.Position, Phase = i++ * 1.7f, Amp = 0.02f });
        foreach (var n in tree.GetNodesInGroup(StackGroup))
            if (n is Node3D s)
            {
                _stacks.Add(s);
                _smoke.Add(SpawnSmoke(s));
            }
        foreach (var n in tree.GetNodesInGroup(StationGroup))
            if (n is Node3D root && root.GetParent() is HexTile view)
                _stations.Add((root, root.GetMeta("station_kind", "").AsString(), view.Axial));

        DressHeart();
        GD.Print($"[CastleAnimator] bound: hull={_hull.Count} legs={_legs.Count} stacks={_stacks.Count} stations={_stations.Count} heart={(_heart != null)}");
    }

    // ── Idle ────────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;

        // Breathing: a slow bob, each plate a little out of phase with the next,
        // and a faint yaw sway so the silhouette never sits still.
        foreach (var p in _hull)
        {
            if (!IsInstanceValid(p.Node)) continue;
            float s = Mathf.Sin(_t * 1.1f + p.Phase);
            p.Node.Position = p.Rest + new Vector3(0f, s * p.Amp, 0f);
            p.Node.RotationDegrees = new Vector3(0f, 30f + Mathf.Sin(_t * 0.5f + p.Phase) * 0.6f, 0f);
        }
        foreach (var p in _legs)
        {
            if (!IsInstanceValid(p.Node)) continue;
            float s = Mathf.Sin(_t * 0.7f + p.Phase);
            p.Node.Position = p.Rest + new Vector3(0f, s * p.Amp, 0f);
        }

        PulseHeart(dt);

        _trackTimer -= dt;
        if (_trackTimer <= 0f)
        {
            _trackTimer = 0.25f;
            TrackStations();
        }
        FlickerLanterns();
    }

    // ── Heart ───────────────────────────────────────────────────────────────

    private void DressHeart()
    {
        if (_heart == null || !IsInstanceValid(_heart))
            return;
        var mesh = _heart.GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (mesh == null)
            return;
        _heartMat = new StandardMaterial3D
        {
            AlbedoColor = HeartBase,
            EmissionEnabled = true,
            Emission = HeartGlow,
            EmissionEnergyMultiplier = 1.0f,
            Roughness = 0.35f,
            Metallic = 0.3f,
        };
        mesh.SetSurfaceOverrideMaterial(0, _heartMat);
        _heartLight = new OmniLight3D
        {
            Name = "HeartLight",
            LightColor = HeartGlow,
            LightEnergy = 1.2f,
            OmniRange = 5.5f,
            OmniAttenuation = 1.4f,
            Position = new Vector3(0f, 1.2f, 0f),
            ShadowEnabled = false,
        };
        _heart.AddChild(_heartLight);
    }

    /// <summary>Glow rate follows the Heart's wounds: a slow swell at full health, a
    /// hard hammer near death. The light on the deck follows the same curve.</summary>
    private void PulseHeart(float dt)
    {
        if (_heartMat == null || _heart == null || !IsInstanceValid(_heart))
            return;
        float frac = _heart.Stats.MaxHealth > 0 ? Mathf.Clamp(_heart.Stats.Health / (float)_heart.Stats.MaxHealth, 0f, 1f) : 1f;
        float period = Mathf.Lerp(0.7f, 2.6f, frac);
        float wave = 0.5f + 0.5f * Mathf.Sin(_t * Mathf.Tau / period);
        _heartFlare = Mathf.Max(0f, _heartFlare - dt * 2.5f);
        float energy = 0.6f + wave * (1.0f + (1f - frac) * 1.5f) + _heartFlare;
        _heartMat.EmissionEnergyMultiplier = energy;
        if (_heartLight != null && IsInstanceValid(_heartLight))
            _heartLight.LightEnergy = 0.8f + energy * 0.9f;
        if (!_heart.Stats.IsAlive)
        {
            _heartMat.EmissionEnabled = false;
            if (_heartLight != null && IsInstanceValid(_heartLight))
                _heartLight.Visible = false;
        }
    }

    /// <summary>The Heart pulse event: a flare that decays over about a second.</summary>
    public void FlareHeart() => _heartFlare = 3.0f;

    // ── Stations ────────────────────────────────────────────────────────────

    /// <summary>A manned ballista turns its bar toward the nearest enemy; unmanned
    /// it rests. Cheap: four times a second, not per frame.</summary>
    private void TrackStations()
    {
        if (_grid == null || _occupantAt == null || _nearestEnemyTo == null)
            return;
        foreach (var (root, kind, at) in _stations)
        {
            if (!IsInstanceValid(root))
                continue;
            var keeper = _occupantAt(at);
            // The floating station name turns green while a player unit mans it.
            if (root.GetParent() is Node parentView)
            {
                var marker = parentView.GetNodeOrNull<Label3D>("StationMarker");
                if (marker != null)
                    marker.Modulate = keeper != null && keeper.IsPlayerControlled
                        ? new Color(0.45f, 1f, 0.55f, 0.95f)
                        : new Color(1f, 0.78f, 0.30f, 0.9f);
            }
            if (kind != "ballista")
                continue;
            Unit target = keeper != null && keeper.IsPlayerControlled ? _nearestEnemyTo(at) : null;
            float yaw = 0f;
            if (target?.CurrentTile != null)
            {
                var d = _grid.AxialToWorld(target.CurrentTile.Axial) - _grid.AxialToWorld(at);
                yaw = Mathf.Atan2(-d.Z, d.X) - Mathf.Pi * 0.5f;   // the bar runs along local Z
            }
            var rot = root.Rotation;
            rot.Y = Mathf.LerpAngle(rot.Y, yaw, 0.35f);
            root.Rotation = rot;
        }
    }

    private void FlickerLanterns()
    {
        foreach (var (root, kind, _) in _stations)
        {
            if (kind != "ward_lantern" || !IsInstanceValid(root))
                continue;
            var light = root.GetNodeOrNull<OmniLight3D>("LanternLight");
            if (light == null)
            {
                light = new OmniLight3D
                {
                    Name = "LanternLight",
                    LightColor = new Color(1f, 0.78f, 0.35f),
                    OmniRange = 3.5f,
                    Position = new Vector3(0.42f, 1.4f, 0f),
                    ShadowEnabled = false,
                };
                root.AddChild(light);
            }
            light.LightEnergy = 0.9f + 0.25f * Mathf.Sin(_t * 9.3f) * Mathf.Sin(_t * 2.1f + 0.7f);
        }
    }

    // ── Beats ───────────────────────────────────────────────────────────────

    /// <summary>The station prop on <paramref name="at"/> kicks back when fired.</summary>
    public void Recoil(Vector2I at)
    {
        foreach (var (root, _, tile) in _stations)
        {
            if (tile != at || !IsInstanceValid(root))
                continue;
            var rest = root.Position;
            var tw = CreateTween();
            tw.TweenProperty(root, "position:y", rest.Y + 0.12f, 0.05f);
            tw.TweenProperty(root, "position:y", rest.Y, 0.25f).SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
        }
    }

    /// <summary>A leg lifts and slams. <paramref name="left"/> picks the flank
    /// (legs sorted by world Z: first is the left one).</summary>
    public void StompLeg(bool left)
    {
        if (_legs.Count == 0)
            return;
        _legs.Sort((a, b) => a.Node.Position.Z.CompareTo(b.Node.Position.Z));
        var leg = left ? _legs[0] : _legs[_legs.Count - 1];
        if (!IsInstanceValid(leg.Node))
            return;
        var tw = CreateTween();
        tw.TweenProperty(leg.Node, "position:y", leg.Rest.Y + 1.4f, 0.45f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(leg.Node, "position:y", leg.Rest.Y - 0.12f, 0.12f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tw.TweenProperty(leg.Node, "position:y", leg.Rest.Y, 0.3f).SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
        tw.TweenCallback(Callable.From(() => Jolt(0.18f, 0.35f)));
    }

    /// <summary>The whole body jerks toward the field and settles.</summary>
    public void Lurch() => Jolt(0.35f, 0.6f);

    /// <summary>Every stack belches: a burst of smoke on top of the idle plume.</summary>
    public void Vent()
    {
        foreach (var p in _smoke)
        {
            if (!IsInstanceValid(p)) continue;
            var burst = (CpuParticles3D)p.Duplicate();
            burst.Amount = 40;
            burst.Lifetime = 1.6f;
            burst.OneShot = true;
            burst.Explosiveness = 0.9f;
            burst.InitialVelocityMin = 2.0f;
            burst.InitialVelocityMax = 3.5f;
            burst.Emitting = true;
            p.GetParent().AddChild(burst);
            burst.Position = p.Position;
            var tw = CreateTween();
            tw.TweenInterval(2.2f);
            tw.TweenCallback(Callable.From(() => { if (IsInstanceValid(burst)) burst.QueueFree(); }));
        }
    }

    private void Jolt(float amount, float settle)
    {
        foreach (var p in _hull)
        {
            if (!IsInstanceValid(p.Node)) continue;
            var tw = CreateTween();
            tw.TweenProperty(p.Node, "position:x", p.Rest.X + amount, 0.08f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(p.Node, "position:x", p.Rest.X, settle).SetTrans(Tween.TransitionType.Elastic).SetEase(Tween.EaseType.Out);
        }
    }

    // ── Smoke ───────────────────────────────────────────────────────────────

    private static CpuParticles3D SpawnSmoke(Node3D stack)
    {
        float top = 0f;
        if (stack is MeshInstance3D mi && mi.Mesh is CylinderMesh cyl)
            top = cyl.Height * 0.5f;
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.53f, 0.52f, 0.55f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        var puff = new SphereMesh { Radius = 0.22f, Height = 0.44f, RadialSegments = 8, Rings = 4 };
        puff.Material = mat;
        var p = new CpuParticles3D
        {
            Name = "Smoke",
            Mesh = puff,
            Amount = 22,
            Lifetime = 3.2f,
            Preprocess = 2.0f,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.15f,
            Direction = Vector3.Up,
            Spread = 12f,
            Gravity = new Vector3(0.25f, 0.45f, 0f),
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 0.9f,
            ScaleAmountMin = 0.6f,
            ScaleAmountMax = 1.0f,
            Position = new Vector3(0f, top, 0f),
            Emitting = true,
        };
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 1f, 1f, 0.7f));
        ramp.SetColor(1, new Color(1f, 1f, 1f, 0f));
        p.ColorRamp = ramp;
        var grow = new Curve();
        grow.AddPoint(new Vector2(0f, 0.4f));
        grow.AddPoint(new Vector2(1f, 1.6f));
        p.ScaleAmountCurve = grow;
        stack.AddChild(p);
        return p;
    }
}
