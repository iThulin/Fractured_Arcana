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
//                 hall is unchanged.
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

    // ── The robes (figure tints; the archmagi and the Provost take their school's) ──
    public static readonly Color ClassRobe = new(0.70f, 0.66f, 0.58f);      // borrowed robes, undyed
    public static readonly Color ExaminerRobe = new(0.30f, 0.27f, 0.33f);
    private static readonly Color DustTint = new(0.86f, 0.80f, 0.70f);
    private static readonly Color CrackDark = new(0.10f, 0.10f, 0.11f);

    private readonly Dictionary<string, Node3D> _breakables = new();
    private readonly Dictionary<string, Vector3> _breakPivots = new();
    private readonly HashSet<string> _broken = new();
    private Node3D _figures;

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
    /// the west window; and dust bursts where it happened. The doors only take the dust. False
    /// when the piece is unknown or already broken.</summary>
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
}
