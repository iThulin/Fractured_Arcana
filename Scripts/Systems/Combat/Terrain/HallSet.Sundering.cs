using Godot;
using System.Collections.Generic;

// ============================================================
// HallSet.Sundering.cs  (partial of HallSet)
//
// Purpose:        The Ceremony Hall's part in the Sundering (S1,
//                 docs/sundering_long_second_founding_v1.md section 2):
//                 the points the fight stages from (the sphere, the
//                 chairs, the lectern, the candidate's mark, the
//                 doorway, the walks the class stands along), the
//                 figures it stands there, and the five pieces of the
//                 hall that break as the sentence is spoken.
//
//                 The breaking pieces are built like every other piece
//                 (HallSet.cs batches them), but each into a small group
//                 of its own, so it can fall without the rest: a bench
//                 block on the south tiers, the shutters of two south
//                 windows, a lamp-stand on the south walk, and a crack
//                 across the great west window, which stays hidden
//                 until it happens. They look exactly as before until
//                 BreakPiece is called, so every other fight in the
//                 hall is unchanged. When the first window breaks, its
//                 shaft of light fills with dust.
//
//                 The Long Second (S2, section 3): the sphere caught
//                 breaking (six shards a finger's width apart, light in
//                 the seams, the six lights pulled apart), the folio
//                 caught at knee height with its pages fanned, the
//                 Register on the lectern in a group of its own so the
//                 candidate can take it, and Still, which stops the
//                 figures, the shards, the folio, the dust and any burst
//                 still open, and the six lights' turning. The cutaway
//                 keeps working through it.
// Layer:          Combat / presentation
// Collaborators:  HallSet.cs (the build and its helpers),
//                 CombatManager.Sundering.cs (the only caller),
//                 CompanionFigure (the shared rig), BurstVfx (dust)
// ============================================================

public partial class HallSet
{
    // ── The breaking pieces (sundering_volleys.json names them) ──────────
    public const string PieceDoors = "doors";
    public const string PieceBench = "bench_south";
    public const string PieceWindowA = "window_south_a";
    public const string PieceLamp = "lamp_south";
    public const string PieceWindowB = "window_south_b";
    public const string PieceWestWindow = "west_window";

    public static readonly string[] Pieces =
    {
        PieceDoors, PieceBench, PieceWindowA, PieceLamp, PieceWindowB, PieceWestWindow,
    };

    /// <summary>The chairs by index round the ring (HallSet.BuildDais): 0 is the south end, 3 the
    /// honored chair (nearest the sphere; the traitor's this year), 6 the Astrologer's.</summary>
    public const int ChairCount = 7;
    public const int HonoredChair = 3;
    public const int AstrologerChair = 6;

    /// <summary>The middle bench block of the south tiers breaks; the window nearest it and the
    /// one nearest the block toward the doors lose their shutters.</summary>
    private const int BreakBenchBlock = 1;
    /// <summary>The south walk's lamps, in BuildTiers' order: the two ends, then the aisles.</summary>
    private const int BreakLampIndex = 2;
    /// <summary>The south windows' shafts as RoundWindow lays them: this long, from the middle of
    /// the window.</summary>
    private const float ShaftLength = 9.5f;

    // ── The Long Second (S2) ─────────────────────────────────────────────
    /// <summary>How far each of the sphere's six shards lifts away: canon's finger's width, a
    /// little more so the seams read at the camera's distance.</summary>
    private const float ShardLift = 0.06f;
    /// <summary>The six lights pull apart inside the breaking glass by this much.</summary>
    private const float OrbSpread = 1.3f;
    /// <summary>Half the width of the light in each of the sphere's seams.</summary>
    private const float SeamHalfWidth = 0.014f;
    private const float FolioWidth = 0.24f;    // a leaf, spine to fore-edge
    private const float FolioHeight = 0.34f;   // along the spine
    private const float LeafThick = 0.004f;
    private const float CoverThick = 0.012f;
    private const int FolioLeaves = 9;
    /// <summary>The folio falls this far: from under the candidate's arm to knee height.</summary>
    private const float FolioFall = 0.5f;

    // ── The robes (figure tints; the archmagi and the Provost take their school's) ──
    public static readonly Color ClassRobe = new(0.70f, 0.66f, 0.58f);      // borrowed robes, undyed
    public static readonly Color ExaminerRobe = new(0.30f, 0.27f, 0.33f);
    private static readonly Color DustTint = new(0.86f, 0.80f, 0.70f);
    private static readonly Color CrackDark = new(0.10f, 0.10f, 0.11f);
    private static readonly Color MoteLight = new(1.0f, 0.95f, 0.82f);      // dust catching the daylight
    private static readonly Color SeamLight = new(0.92f, 0.96f, 1.0f);      // the light in the sphere's cracks
    private static readonly Color FolioCover = new(0.24f, 0.27f, 0.33f);

    private readonly Dictionary<string, Node3D> _breakables = new();
    private readonly Dictionary<string, Vector3> _breakPivots = new();
    private readonly HashSet<string> _broken = new();
    private Node3D _figures;

    private bool _stilled;
    private Node3D _shards;
    private Node3D _folio;
    private Node3D _registerBook;
    private readonly List<GpuParticles3D> _shaftDust = new();

    /// <summary>True for the Ceremony Hall once it is built (not the Grand Hall).</summary>
    public bool IsCeremonyHall => Look != GrandHallSetId && _hall != null;

    // ════════════════════════════════════════════════════════════════════════
    //  Points (world space: the set is top level at the identity)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The dais top at the sphere's foot.</summary>
    public Vector3 DaisCenter => new(_xDais, _daisTop, _cz);

    /// <summary>The middle of the magisphere's glass.</summary>
    public Vector3 SphereCenter => new(_xDais, _daisTop + 2.5f, _cz);

    /// <summary>The candidate's mark, between the lectern and the dais.</summary>
    public Vector3 MarkPoint => new(_xStripW - MarkGap, _floorY, _cz);

    /// <summary>Where the Provost stands to read: east of the lectern, facing the dais.</summary>
    public Vector3 ReaderPoint => new(_xStripW - LecternGap + 0.55f, _floorY, _cz);

    /// <summary>The middle of the east doorway, at a Keeper's chest.</summary>
    public Vector3 DoorwayPoint => new(_xWallE - 1.2f, _floorY + 2.4f, _cz);

    /// <summary>The Examiner's place at the foot of the dais, beside the lectern's line.</summary>
    public Vector3 ExaminerPoint => new(_xStripW - DaisGap + 0.6f, _floorY, _cz - 2.2f);

    /// <summary>Where the traitor stands: risen from the honored chair, beside it, at the glass.</summary>
    public Vector3 TraitorStandPoint => new(_xDais + 1.55f, _daisTop, _cz + 0.8f);

    /// <summary>The traitor's palm, flat on the glass.</summary>
    public Vector3 TraitorPalmPoint => new(_xDais + 1.01f, _daisTop + 1.6f, _cz + 0.52f);

    /// <summary>The traitor's other hand, drawn back toward the lectern: where the spell leaves from.</summary>
    public Vector3 TraitorHandPoint => new(_xDais + 1.9f, _daisTop + 1.45f, _cz + 1.05f);

    /// <summary>Where the folio stops (S2): at knee height, beside the candidate's mark.</summary>
    public Vector3 FolioPoint => MarkPoint + new Vector3(0.05f, 0.5f, 0.42f);

    /// <summary>The floor in front of chair <paramref name="index"/>, where its archmagus stands to
    /// cast (the chairs face out from the sphere).</summary>
    public Vector3 ChairStandPoint(int index)
    {
        float ring = index == HonoredChair ? HonoredRing : ChairRing;
        return DaisCenter + ChairOutward(index) * (ring + 0.55f);
    }

    /// <summary>The way chair <paramref name="index"/> faces: out from the sphere.</summary>
    public Vector3 ChairOutward(int index)
    {
        float a = Mathf.DegToRad((index - HonoredChair) * ChairStep);
        return new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
    }

    /// <summary>Where the graduating class stands: in a line along each wall, on the walk behind
    /// the families' rows, half on each side.</summary>
    public List<Vector3> ClassStandPoints(int count)
    {
        var points = new List<Vector3>();
        if (count <= 0)
            return points;
        float walkTop = _floorY + TierRise * (Tiers + 1);
        float back = _tierFront + Tiers * TierDepth + 1.1f;
        int perSide = Mathf.Max(1, (count + 1) / 2);
        float x0 = _xTierW + 1.0f;
        float x1 = _xTierE - 1.0f;
        foreach (float side in new[] { -1f, 1f })
        {
            for (int i = 0; i < perSide && points.Count < count; i++)
            {
                float t = perSide == 1 ? 0.5f : (float)i / (perSide - 1);
                points.Add(new Vector3(Mathf.Lerp(x0, x1, t), walkTop, _cz + side * back));
            }
        }
        return points;
    }

    /// <summary>Where a piece's break shows (its dust): the doorway, the west window, or the
    /// piece's own pivot.</summary>
    public Vector3 PiecePoint(string id)
    {
        if (id == PieceDoors)
            return DoorwayPoint;
        if (id == PieceWestWindow)
            return new Vector3(_xWallW + 0.4f, _floorY + 7.5f, _cz);
        return _breakPivots.TryGetValue(id ?? "", out var p) ? p : DaisCenter;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Figures
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Stands a figure (the shared rig, or its stand-in) with its feet at
    /// <paramref name="feet"/>, turned to face <paramref name="facing"/> (the rig faces +Z, as the
    /// strategic map's figures do). Figures belong to the set, so they go when it is cleared.</summary>
    public Node3D PlaceFigure(string name, Vector3 feet, Vector3 facing, Color robe, float scale = 1f)
    {
        if (_figures == null || !IsInstanceValid(_figures))
        {
            _figures = new Node3D { Name = "Figures" };
            AddChild(_figures);
        }
        var figure = CompanionFigure.Build(robe, false, scale);
        figure.Name = "Figure_" + (name ?? "");
        _figures.AddChild(figure);
        figure.Position = feet;
        facing.Y = 0f;
        if (facing.LengthSquared() > 1e-4f)
            figure.Rotation = new Vector3(0f, Mathf.Atan2(facing.X, facing.Z), 0f);
        return figure;
    }

    /// <summary>A spent figure goes down: an archmagus to one knee, the class behind the benches
    /// (<paramref name="drop"/> meters, with a slight lean), after <paramref name="delay"/> seconds
    /// so the counter-volley lands first.</summary>
    public void Kneel(Node3D figure, float delay, float drop = 0.42f)
    {
        if (figure == null || !IsInstanceValid(figure))
            return;
        var tw = CreateTween();
        if (delay > 0f)
            tw.TweenInterval(delay);
        tw.TweenProperty(figure, "position", figure.Position + new Vector3(0f, -drop, 0f), 0.3)
          .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        tw.Parallel().TweenProperty(figure, "rotation:x", figure.Rotation.X + Mathf.DegToRad(10f), 0.3);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The breaking pieces
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Breaks a piece once: the bench block drops and tilts, a window's shutters tear away
    /// onto the walk, the lamp-stand goes over and its light rolls low, the crack appears across
    /// the west window; and dust bursts where it happened. The first window's shaft of light fills
    /// with dust and stays full of it. The doors only take the dust. False when the piece is unknown
    /// or already broken.</summary>
    public bool BreakPiece(string id)
    {
        if (string.IsNullOrEmpty(id) || System.Array.IndexOf(Pieces, id) < 0 || !_broken.Add(id))
            return false;

        if (_breakables.TryGetValue(id, out var g) && g != null && IsInstanceValid(g))
        {
            Repivot(g, _breakPivots.TryGetValue(id, out var pivot) ? pivot : Vector3.Zero);
            switch (id)
            {
                case PieceBench:
                {
                    var tw = CreateTween();
                    tw.TweenProperty(g, "position", g.Position + new Vector3(0f, -0.3f, 0f), 0.35)
                      .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
                    tw.Parallel().TweenProperty(g, "rotation", new Vector3(Mathf.DegToRad(-9f), 0f, Mathf.DegToRad(3f)), 0.35);
                    break;
                }
                case PieceWindowA:
                case PieceWindowB:
                {
                    // Torn into the hall (the south wall's inside is +Z) and down onto the walk.
                    float drop = WindowSill + WindowHeight * 0.5f - TierRise * (Tiers + 1) - 0.3f;
                    var tw = CreateTween();
                    tw.TweenProperty(g, "position", g.Position + new Vector3(0f, -drop, 1.4f), 0.55)
                      .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
                    tw.Parallel().TweenProperty(g, "rotation", new Vector3(Mathf.DegToRad(70f), 0f, 0f), 0.55);
                    break;
                }
                case PieceLamp:
                {
                    // Over onto the walk, toward the hall; the lamplight goes down with it.
                    var tw = CreateTween();
                    tw.TweenProperty(g, "rotation", new Vector3(Mathf.DegToRad(82f), 0f, 0f), 0.6)
                      .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
                    foreach (Node child in g.GetChildren())
                        if (child is OmniLight3D lamp)
                            tw.Parallel().TweenProperty(lamp, "light_energy", lamp.LightEnergy * 0.35f, 0.6);
                    break;
                }
                case PieceWestWindow:
                    g.Visible = true;
                    break;
            }
        }

        Dust(PiecePoint(id), id == PieceDoors ? 2.4f : 1.4f);
        if (id == PieceWindowA)
            ShaftDust(id);
        GD.Print($"[Hall] {id} breaks.");
        return true;
    }

    /// <summary>The group for a breaking piece, made on first use as a child of
    /// <paramref name="parent"/> (the hall, or a wall group so the cutaway hides it with its wall),
    /// with the point it turns about when it breaks.</summary>
    private Node3D Breakable(string id, Node3D parent, Vector3 pivot)
    {
        if (_breakables.TryGetValue(id, out var existing))
            return existing;
        var g = new Node3D { Name = "Breaks_" + id };
        parent.AddChild(g);
        _breakables[id] = g;
        _breakPivots[id] = pivot;
        return g;
    }

    /// <summary>The pieces were batched in world space under a group at the origin; move the group
    /// to the pivot and its contents back by as much, so it turns about the pivot.</summary>
    private static void Repivot(Node3D g, Vector3 pivot)
    {
        foreach (Node child in g.GetChildren())
            if (child is Node3D n)
                n.Position -= pivot;
        g.Position += pivot;
    }

    /// <summary>The center X of the south tiers' bench block <paramref name="block"/> (BuildTiers'
    /// layout).</summary>
    private float TierBlockCenterX(int block)
    {
        float len = _xTierE - _xTierW;
        float blockLen = (len - (TierBlocks - 1) * TierAisle) / TierBlocks;
        return _xTierW + block * (blockLen + TierAisle) + blockLen * 0.5f;
    }

    /// <summary>The window in a row (<paramref name="x0"/>, <paramref name="count"/>, WindowSpacing)
    /// nearest <paramref name="x"/>, other than <paramref name="skip"/>; -1 for none.</summary>
    private static int NearestWindow(float x0, int count, float x, int skip = -1)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (i == skip)
                continue;
            float d = Mathf.Abs(x0 + i * WindowSpacing - x);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The crack across the great west window: a jagged dark line on the glass, hidden
    /// until it happens. Called from BuildWestWall, in that wall's group.</summary>
    private void WestWindowCrack(Node3D wall)
    {
        var g = Breakable(PieceWestWindow, wall, new Vector3(_xWallW + 0.1f, _floorY + 7.5f, _cz));
        g.Visible = false;
        float x = _xWallW + 0.12f;   // just in front of the glass (EndWallWindow puts it at +0.04)
        var points = new[]
        {
            new Vector3(x, _floorY + 11.6f, _cz - 1.9f),
            new Vector3(x, _floorY + 10.1f, _cz - 0.8f),
            new Vector3(x, _floorY + 9.3f, _cz - 1.2f),
            new Vector3(x, _floorY + 7.6f, _cz + 0.3f),
            new Vector3(x, _floorY + 6.4f, _cz - 0.1f),
            new Vector3(x, _floorY + 4.6f, _cz + 1.6f),
        };
        for (int i = 0; i + 1 < points.Length; i++)
            CrackSegment(g, points[i], points[i + 1], i % 2 == 0 ? 0.09f : 0.06f);
        CrackSegment(g, points[3], new Vector3(x, _floorY + 8.4f, _cz + 1.5f), 0.05f);   // a branch
    }

    /// <summary>One straight stretch of the crack, from <paramref name="a"/> to <paramref name="b"/>
    /// on the window's plane (constant X), <paramref name="width"/> wide.</summary>
    private void CrackSegment(Node3D g, Vector3 a, Vector3 b, float width)
    {
        Vector3 along = b - a;
        float length = along.Length();
        if (length < 0.01f)
            return;
        Vector3 up = along / length;
        Vector3 across = Vector3.Right.Cross(up).Normalized();
        var basis = new Basis(Vector3.Right * 0.04f, up * length, across * width);
        Add(g, Prim.Box, Paint.Wood, false, new Transform3D(basis, (a + b) * 0.5f), CrackDark);
    }

    /// <summary>A puff of stone dust and grit where something broke.</summary>
    private void Dust(Vector3 at, float radius)
    {
        var burst = new BurstVfx { Tint = DustTint, Radius = radius, GroundRing = false };
        AddChild(burst);
        burst.GlobalPosition = at;
        _ = burst.Play(1f);
    }

    /// <summary>The broken window's shaft of light fills with dust (canon: the dust in the window
    /// light, which the Long Second stops falling): motes drifting slowly down the shaft, in the
    /// south wall's group so the cutaway hides them with it.</summary>
    private void ShaftDust(string id)
    {
        if (!_breakPivots.TryGetValue(id, out var pivot))
            return;
        Node3D parent = _breakables.TryGetValue(id, out var g) && g != null && IsInstanceValid(g)
            ? g.GetParent() as Node3D
            : null;
        parent ??= this;

        // The shaft as RoundWindow lays it on the south wall: from the middle of the window, steeply
        // down and into the hall (+Z).
        float pitch = Mathf.DegToRad(ShaftPitch);
        Vector3 along = new Vector3(0f, -Mathf.Sin(pitch), Mathf.Cos(pitch));
        Vector3 start = new Vector3(pivot.X, pivot.Y, pivot.Z + 0.1f);
        Vector3 center = start + along * (ShaftLength * 0.5f);
        Vector3 across = Vector3.Right;
        var basis = new Basis(across, along, across.Cross(along).Normalized());

        var fade = new Gradient();
        fade.Offsets = new float[] { 0f, 0.2f, 0.8f, 1f };
        fade.Colors = new Color[]
        {
            new Color(MoteLight, 0f), new Color(MoteLight, 0.6f), new Color(MoteLight, 0.6f), new Color(MoteLight, 0f),
        };
        var motes = new GpuParticles3D
        {
            Name = "ShaftDust",
            Amount = 64,
            Lifetime = 8.0,
            Explosiveness = 0f,
            Randomness = 0.5f,
            LocalCoords = false,
            Emitting = true,
            VisibilityAabb = new Aabb(new Vector3(-2f, -5.5f, -1.5f), new Vector3(4f, 11f, 3f)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(WindowWidth * 0.4f, ShaftLength * 0.45f, 0.45f),
                Direction = Vector3.Down,
                Spread = 180f,
                InitialVelocityMin = 0.02f,
                InitialVelocityMax = 0.08f,
                Gravity = new Vector3(0f, -0.04f, 0f),
                ScaleMin = 0.6f,
                ScaleMax = 1.3f,
                ColorRamp = new GradientTexture1D { Gradient = fade },
            },
        };
        var mote = new QuadMesh { Size = new Vector2(0.035f, 0.035f) };
        mote.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        };
        motes.DrawPass1 = mote;
        parent.AddChild(motes);
        motes.GlobalTransform = new Transform3D(basis, center);
        _shaftDust.Add(motes);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The Long Second (S2)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Everything on the set stops where it is: the figures, the breaking glass, the
    /// falling folio, the dust in the window light and any burst still open stop processing (their
    /// tweens, animations and particles with them), and the six lights stop turning. The set itself
    /// keeps processing, so the cutaway still answers the camera.</summary>
    public void Still()
    {
        _stilled = true;
        foreach (var node in new[] { _figures, _shards, _folio })
            if (node != null && IsInstanceValid(node))
                node.ProcessMode = ProcessModeEnum.Disabled;
        foreach (var motes in _shaftDust)
            if (motes != null && IsInstanceValid(motes))
                motes.ProcessMode = ProcessModeEnum.Disabled;
        foreach (Node child in GetChildren())
            if (child is BurstVfx)
                child.ProcessMode = ProcessModeEnum.Disabled;
        GD.Print("[Hall] The Long Second: the hall is still.");
    }

    /// <summary>The sphere caught breaking: the glass parts along six meridians into shards that
    /// lift a finger's width apart, light shows in the seams, and the six lights pull apart, all
    /// over <paramref name="seconds"/> (the spell's flight) and slowing into the stop, so the freeze
    /// catches it there. The tween belongs to the shards, which Still stops. When the sphere is a
    /// model, only the lights pull apart (and when they are part of it too, nothing does).</summary>
    public void CatchSphere(float seconds)
    {
        if (_hall == null || (_shards != null && IsInstanceValid(_shards)))
            return;
        var glass = _hall.GetNodeOrNull<MeshInstance3D>("Magisphere");
        var sphere = glass?.Mesh as SphereMesh;
        bool lights = _orbs != null && IsInstanceValid(_orbs);
        if (sphere == null && !lights)
            return;

        _shards = new Node3D { Name = "Shards" };
        _hall.AddChild(_shards);
        float span = Mathf.Max(0.05f, seconds);
        var tw = _shards.CreateTween().SetParallel(true);

        if (sphere != null)
        {
            float r = sphere.Radius;
            var shardGlass = (StandardMaterial3D)GlassMaterial().Duplicate();
            shardGlass.CullMode = BaseMaterial3D.CullModeEnum.Disabled;   // a shard shows its inside face
            var seamGlow = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(SeamLight, 0f),
            };
            for (int k = 0; k < 6; k++)
            {
                float from = Mathf.Tau * k / 6f;
                float to = Mathf.Tau * (k + 1) / 6f;
                float mid = (from + to) * 0.5f;
                var shard = new MeshInstance3D
                {
                    Name = "Shard" + k,
                    Mesh = LuneMesh(r, from, to),
                    MaterialOverride = shardGlass,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = glass.Position,
                };
                _shards.AddChild(shard);
                var outward = new Vector3(Mathf.Cos(mid), 0f, Mathf.Sin(mid));
                tw.TweenProperty(shard, "position", glass.Position + outward * ShardLift, span)
                  .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
                _shards.AddChild(new MeshInstance3D
                {
                    Name = "Seam" + k,
                    Mesh = SeamMesh(r, from),
                    MaterialOverride = seamGlow,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = glass.Position,
                });
            }
            tw.TweenProperty(seamGlow, "albedo_color:a", 0.85f, span);
            glass.Visible = false;
        }
        if (lights)
            tw.TweenProperty(_orbs, "scale", Vector3.One * OrbSpread, span)
              .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        GD.Print("[Hall] The sphere breaks: six shards, a finger's width apart.");
    }

    /// <summary>The folio slips from under the candidate's arm: it falls toward knee height,
    /// turning, its pages fanning open, over <paramref name="seconds"/> and slowing into the stop,
    /// so the freeze catches it there. The tween belongs to the folio, which Still stops.</summary>
    public void DropFolio(float seconds)
    {
        if (_hall == null || (_folio != null && IsInstanceValid(_folio)))
            return;
        Vector3 rest = FolioPoint;
        _folio = new Node3D
        {
            Name = "Folio",
            Position = rest + new Vector3(0f, FolioFall, 0f),
            Rotation = new Vector3(Mathf.DegToRad(4f), Mathf.DegToRad(-20f), 0f),
        };
        _hall.AddChild(_folio);
        float span = Mathf.Max(0.05f, seconds);
        var tw = _folio.CreateTween().SetParallel(true);
        tw.TweenProperty(_folio, "position", rest, span)
          .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(_folio, "rotation", new Vector3(Mathf.DegToRad(-14f), Mathf.DegToRad(-32f), Mathf.DegToRad(9f)), span)
          .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

        // The leaves turn about the spine (the folio's Z axis): 0 degrees lies flat to one side, 180
        // to the other. Shut, they lie stacked; open, the covers lie nearly flat and the pages fan
        // between them.
        int count = FolioLeaves + 2;
        for (int i = 0; i < count; i++)
        {
            bool cover = i == 0 || i == count - 1;
            float open = i == 0 ? 6f : i == count - 1 ? 174f : Mathf.Lerp(24f, 156f, (i - 1f) / (FolioLeaves - 1f));
            var leaf = new Node3D { Name = cover ? "Cover" : "Leaf", Rotation = new Vector3(0f, 0f, Mathf.DegToRad(i * 0.4f)) };
            _folio.AddChild(leaf);
            var size = new Vector3(FolioWidth, cover ? CoverThick : LeafThick, cover ? FolioHeight + 0.02f : FolioHeight);
            Solo(leaf, Prim.Box, Paint.Wood, Scaled(new Vector3(FolioWidth * 0.5f, 0f, 0f), Basis.Identity, size),
                 cover ? FolioCover : Parchment);
            tw.TweenProperty(leaf, "rotation:z", Mathf.DegToRad(open), span)
              .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
    }

    /// <summary>The Margin: the candidate takes the Register from the lectern.</summary>
    public void TakeRegister()
    {
        if (_registerBook != null && IsInstanceValid(_registerBook))
            _registerBook.Visible = false;
    }

    /// <summary>The group the Register on the lectern is built in (BuildLectern), so it can be
    /// taken. Made on first use, under the hall.</summary>
    private Node3D RegisterBook()
    {
        if (_registerBook == null || !IsInstanceValid(_registerBook))
        {
            _registerBook = new Node3D { Name = "Register" };
            _hall.AddChild(_registerBook);
        }
        return _registerBook;
    }

    /// <summary>One painted piece of its own, for the props made after the build (the folio): a
    /// MultiMesh of one, so the painterly shader reads its color as it reads the batched pieces'.</summary>
    private static void Solo(Node3D parent, Prim prim, Paint paint, Transform3D xf, Color color)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = MeshFor(prim),
        };
        mm.InstanceCount = 1;
        mm.SetInstanceTransform(0, xf);
        mm.SetInstanceColor(0, color);
        parent.AddChild(new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = MaterialFor(paint),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>One of the six shards: the glass between longitudes <paramref name="from"/> and
    /// <paramref name="to"/>, pole to pole, with smooth normals.</summary>
    private static ArrayMesh LuneMesh(float radius, float from, float to)
    {
        const int lonSegs = 6;
        const int latSegs = 16;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < lonSegs; i++)
        {
            float p0 = Mathf.Lerp(from, to, (float)i / lonSegs);
            float p1 = Mathf.Lerp(from, to, (float)(i + 1) / lonSegs);
            for (int j = 0; j < latSegs; j++)
            {
                float t0 = Mathf.Lerp(-Mathf.Pi * 0.5f, Mathf.Pi * 0.5f, (float)j / latSegs);
                float t1 = Mathf.Lerp(-Mathf.Pi * 0.5f, Mathf.Pi * 0.5f, (float)(j + 1) / latSegs);
                Vector3 a = SpherePoint(p0, t0), b = SpherePoint(p1, t0), c = SpherePoint(p1, t1), d = SpherePoint(p0, t1);
                SmoothTri(st, radius, a, b, c);
                SmoothTri(st, radius, a, c, d);
            }
        }
        return st.Commit();
    }

    /// <summary>The light in one of the sphere's cracks: a thin strip just outside the glass along
    /// the meridian at <paramref name="lon"/>, pole to pole.</summary>
    private static ArrayMesh SeamMesh(float radius, float lon)
    {
        const int latSegs = 16;
        float half = SeamHalfWidth / Mathf.Max(0.1f, radius);
        float lifted = radius * 1.004f;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int j = 0; j < latSegs; j++)
        {
            float t0 = Mathf.Lerp(-Mathf.Pi * 0.5f, Mathf.Pi * 0.5f, (float)j / latSegs);
            float t1 = Mathf.Lerp(-Mathf.Pi * 0.5f, Mathf.Pi * 0.5f, (float)(j + 1) / latSegs);
            Vector3 a = SpherePoint(lon - half, t0), b = SpherePoint(lon + half, t0);
            Vector3 c = SpherePoint(lon + half, t1), d = SpherePoint(lon - half, t1);
            SmoothTri(st, lifted, a, b, c);
            SmoothTri(st, lifted, a, c, d);
        }
        return st.Commit();
    }

    /// <summary>A point on the unit sphere: <paramref name="lon"/> round from +X toward +Z,
    /// <paramref name="lat"/> up from the equator.</summary>
    private static Vector3 SpherePoint(float lon, float lat) =>
        new(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));

    /// <summary>A triangle of unit-sphere points scaled to <paramref name="radius"/>, each corner with
    /// its own outward normal, wound as Tri winds (Godot's front faces are clockwise seen from the
    /// front). Degenerate triangles at the poles are skipped.</summary>
    private static void SmoothTri(SurfaceTool st, float radius, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 face = (b - a).Cross(c - a);
        if (face.LengthSquared() < 1e-12f)
            return;
        if (face.Dot(a + b + c) > 0f)
            (b, c) = (c, b);
        foreach (var v in new[] { a, b, c })
        {
            st.SetNormal(v);
            st.AddVertex(v * radius);
        }
    }
}
