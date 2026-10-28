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
//
//                 More of it breaking (S2b): four breaches blown through
//                 the walls, two beside the east doors and two under the
//                 south windows. Each takes its wall's slab out of the
//                 batch and lays the wall again round a panel with a
//                 ragged hole in it, blackened at the edge, with the
//                 daylight showing through it and falling into the hall,
//                 and the wall's pieces thrown across the walk or the
//                 floor. Every piece can also break at once, as it lies
//                 afterward (instant), for the hall seen again (S3), and
//                 each breach's shape comes from its id, so it is the same
//                 hole every time. And the points the stray spells burst
//                 on: the walls, the dais, the floor in front of the
//                 doors, never the board.
//
//                 The hall seen again (S3): the Sundering's canon figures
//                 are staged here for the fight and for the hall stage
//                 alike (StageSundering), and the sphere, the folio, the
//                 spent figures and the spell can each be set down as the
//                 Long Second left them, with no motion: the stage
//                 (HallStage) builds the frozen fight that way, with the
//                 shards of the fragments brought home warm.
// Layer:          Combat / presentation
// Collaborators:  HallSet.cs (the build and its helpers; Slab records each
//                 wall's slab), CombatManager.Sundering.cs (the only
//                 caller), HallStage (the hall seen again), CompanionFigure
//                 (the shared rig), BurstVfx (dust)
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
    // The breaches (S2b): holes blown through the walls, beside the east doors and under the two
    // south windows that lose their shutters.
    public const string PieceBreachEastSouth = "breach_east_south";
    public const string PieceBreachSouthA = "breach_south_a";
    public const string PieceBreachEastNorth = "breach_east_north";
    public const string PieceBreachSouthB = "breach_south_b";

    public static readonly string[] Pieces =
    {
        PieceDoors, PieceBench, PieceWindowA, PieceLamp, PieceWindowB, PieceWestWindow,
        PieceBreachEastSouth, PieceBreachSouthA, PieceBreachEastNorth, PieceBreachSouthB,
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

    // ── The cast (the Sundering's canon figures) ─────────────────────────
    /// <summary>A standing figure's chest above its feet (the shared rig's stand-in), and how far
    /// short of the chest of whoever stands on the mark the traitor's spell stops.</summary>
    public const float FigureChest = 0.95f;
    public const float SpellArmLength = 0.7f;
    /// <summary>A spent archmagus sinks this far, onto one knee; a spent member of the class this far,
    /// down behind the benches.</summary>
    public const float KneelDrop = 0.42f;
    public const float ClassDrop = 0.6f;
    /// <summary>The six chairs in Commencement's order (the Astrologer's is the seventh, at the north
    /// end). The traitor's is the honored chair, and the others fill the ring from the south end in
    /// this order. The Witness left before the wards broke, so hers stays empty.</summary>
    private static readonly string[] ChairOrder = { "aurel", "hess", "joren", "namer", "conductor", "engineer" };
    private const string WitnessId = "hess";
    private const string ProvostId = "wenna";
    /// <summary>The graduating class: twenty-six, and you (canon sheet).</summary>
    private const int ClassSize = 26;

    // ── The breaches (S2b) ───────────────────────────────────────────────
    /// <summary>Points round a breach's ragged edge.</summary>
    private const int BreachSides = 30;
    /// <summary>How square a breach is (a superellipse's power: 2 is an ellipse).</summary>
    private const float BreachSquareness = 2.6f;
    /// <summary>A breach under a south window reaches this far each way from the window's middle (its
    /// piers stand at half a bay).</summary>
    private const float BreachSouthHalfWidth = 2.0f;
    /// <summary>A breach beside the east doors: its middle this far along the wall from the doorway's,
    /// between the door frame and the window on that side, and this high, and its size.</summary>
    private const float BreachEastOffset = 5.2f;
    private const float BreachEastMiddle = 3.5f;
    private const float BreachEastHalfWidth = 1.45f;
    private const float BreachEastHalfHeight = 2.35f;
    /// <summary>The wall each breach throws into the hall: this many pieces, the first of them dropped
    /// in a heap at the wall's foot, the rest thrown up to this far.</summary>
    private const int BreachDebris = 22;
    private const int BreachHeap = 9;
    private const float BreachThrow = 4.6f;
    private const int BreachRimChunks = 7;

    // ── The robes (figure tints; the archmagi and the Provost take their school's) ──
    public static readonly Color ClassRobe = new(0.70f, 0.66f, 0.58f);      // borrowed robes, undyed
    public static readonly Color ExaminerRobe = new(0.30f, 0.27f, 0.33f);
    private static readonly Color DustTint = new(0.86f, 0.80f, 0.70f);
    private static readonly Color CrackDark = new(0.10f, 0.10f, 0.11f);
    private static readonly Color MoteLight = new(1.0f, 0.95f, 0.82f);      // dust catching the daylight
    private static readonly Color SeamLight = new(0.92f, 0.96f, 1.0f);      // the light in the sphere's cracks
    private static readonly Color FolioCover = new(0.24f, 0.27f, 0.33f);
    private static readonly Color Soot = new(0.15f, 0.13f, 0.12f);             // the blast's mark round a breach
    private static readonly Color RubbleStone = new(0.47f, 0.44f, 0.39f);      // the wall's core, broken open
    private static readonly Color BreachDaylight = new(1.0f, 0.95f, 0.84f);    // the light through a breach
    private static readonly Color BlastLight = new(1.0f, 0.90f, 0.70f);
    private static readonly Color WarmGlass = new(1.0f, 0.80f, 0.58f, 0.45f);    // a shard brought home
    private static readonly Color WarmLight = new(1.0f, 0.62f, 0.30f);

    private readonly Dictionary<string, Node3D> _breakables = new();
    private readonly Dictionary<string, Vector3> _breakPivots = new();
    private readonly HashSet<string> _broken = new();
    private Node3D _figures;

    private bool _stilled;
    private Node3D _shards;
    private Node3D _folio;
    private Node3D _registerBook;
    private StandardMaterial3D _shardGlass;
    private readonly List<GpuParticles3D> _shaftDust = new();
    private readonly Dictionary<Node3D, WallSlab> _slabs = new();

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

    /// <summary>Where a piece's break shows (its dust): the doorway, the west window, a breach's
    /// mouth, or the piece's own pivot.</summary>
    public Vector3 PiecePoint(string id)
    {
        if (id == PieceDoors)
            return DoorwayPoint;
        if (id == PieceWestWindow)
            return new Vector3(_xWallW + 0.4f, _floorY + 7.5f, _cz);
        if (IsBreach(id))
        {
            var breach = BreachFor(id);
            return breach != null ? breach.Center + breach.Inward * 0.4f : DoorwayPoint;
        }
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

    /// <summary>The Sundering's canon figures, as StageSundering stands them.</summary>
    public sealed class SunderingCast
    {
        /// <summary>The archmagi on the dais, by id: never the traitor, and the Witness's chair is
        /// empty.</summary>
        public readonly Dictionary<string, Node3D> Archmagi = new();
        public Node3D Traitor;
        public Node3D Provost;
        public Node3D Examiner;
        public readonly List<Node3D> Class = new();
    }

    /// <summary>Stands the Sundering's canon figures (D39 to D41), for the fight and for the hall seen
    /// again alike: the loyal archmagi in front of their chairs, facing the board; the traitor, when
    /// <paramref name="traitorId"/> is one of the five, risen at the sphere; the Provost at the
    /// lectern facing the dais; the Examiner at the foot of the dais; and the class along both walks.
    /// Figures belong to the hall.</summary>
    public SunderingCast StageSundering(string traitorId, Vector3 boardCenter)
    {
        var cast = new SunderingCast();
        bool hasTraitor = System.Array.IndexOf(CampaignGenerator.TraitorPool, traitorId ?? "") >= 0;
        var chairs = new List<int>();
        for (int i = 0; i < ChairCount; i++)
            if (i != AstrologerChair && !(hasTraitor && i == HonoredChair))
                chairs.Add(i);
        int next = 0;
        foreach (var id in ChairOrder)
        {
            if (id == traitorId || next >= chairs.Count)
                continue;
            int chair = chairs[next++];
            if (id == WitnessId)
                continue;   // the Witness left before the wards broke: her chair stands empty
            Vector3 feet = ChairStandPoint(chair);
            cast.Archmagi[id] = PlaceFigure(id, feet, boardCenter - feet, SunderingRobe(id));
        }
        if (hasTraitor)
        {
            Vector3 stand = TraitorStandPoint;
            cast.Traitor = PlaceFigure("traitor", stand, SphereCenter - stand, SunderingRobe(traitorId));
        }
        cast.Provost = PlaceFigure("provost", ReaderPoint, DaisCenter - ReaderPoint, SunderingRobe(ProvostId));
        cast.Examiner = PlaceFigure("examiner", ExaminerPoint, boardCenter - ExaminerPoint, ExaminerRobe);
        var points = ClassStandPoints(ClassSize);
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 p = points[i];
            cast.Class.Add(PlaceFigure("class_" + i, p, new Vector3(0f, 0f, boardCenter.Z - p.Z), ClassRobe));
        }
        return cast;
    }

    /// <summary>A figure's robe: the archmagus's school, gold when unknown.</summary>
    public static Color SunderingRobe(string archmageId)
    {
        var def = ArchmageRegistry.Get(archmageId);
        return def != null && System.Enum.TryParse<CardSchool>(def.School, true, out var school)
            ? SchoolColors.GetBorderColor(school)
            : UITheme.Gold;
    }

    /// <summary>A spent figure as it lies once down (Kneel's end, at once): the hall seen again.</summary>
    public static void KneelNow(Node3D figure, float drop = KneelDrop)
    {
        if (figure == null || !IsInstanceValid(figure))
            return;
        figure.Position += new Vector3(0f, -drop, 0f);
        Vector3 turn = figure.Rotation;
        figure.Rotation = new Vector3(turn.X + Mathf.DegToRad(10f), turn.Y, turn.Z);
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
    /// the west window, a breach blows a hole through its wall (S2b); and dust bursts where it
    /// happened. The first window's shaft of light fills with dust and stays full of it. The doors
    /// only take the dust. <paramref name="instant"/> (the hall seen again, S3) leaves each piece as
    /// it lies once broken, with no motion and no dust. False when the piece is unknown or already
    /// broken.</summary>
    public bool BreakPiece(string id, bool instant = false)
    {
        if (string.IsNullOrEmpty(id) || System.Array.IndexOf(Pieces, id) < 0 || !_broken.Add(id))
            return false;

        if (IsBreach(id))
        {
            OpenBreach(id, instant);
        }
        else if (_breakables.TryGetValue(id, out var g) && g != null && IsInstanceValid(g))
        {
            Repivot(g, _breakPivots.TryGetValue(id, out var pivot) ? pivot : Vector3.Zero);
            switch (id)
            {
                case PieceBench:
                {
                    if (instant)
                    {
                        g.Position += new Vector3(0f, -0.3f, 0f);
                        g.Rotation = new Vector3(Mathf.DegToRad(-9f), 0f, Mathf.DegToRad(3f));
                        break;
                    }
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
                    if (instant)
                    {
                        g.Position += new Vector3(0f, -drop, 1.4f);
                        g.Rotation = new Vector3(Mathf.DegToRad(70f), 0f, 0f);
                        break;
                    }
                    var tw = CreateTween();
                    tw.TweenProperty(g, "position", g.Position + new Vector3(0f, -drop, 1.4f), 0.55)
                      .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
                    tw.Parallel().TweenProperty(g, "rotation", new Vector3(Mathf.DegToRad(70f), 0f, 0f), 0.55);
                    break;
                }
                case PieceLamp:
                {
                    // Over onto the walk, toward the hall; the lamplight goes down with it.
                    if (instant)
                    {
                        g.Rotation = new Vector3(Mathf.DegToRad(82f), 0f, 0f);
                        foreach (Node child in g.GetChildren())
                            if (child is OmniLight3D lamp)
                                lamp.LightEnergy *= 0.35f;
                        break;
                    }
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

        if (!instant && !IsBreach(id))
            Dust(PiecePoint(id), id == PieceDoors ? 2.4f : 1.4f);
        if (id == PieceWindowA)
            ShaftDust(id, instant);
        GD.Print(instant ? $"[Hall] {id} lies broken." : $"[Hall] {id} breaks.");
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
    /// south wall's group so the cutaway hides them with it. <paramref name="settled"/>: already
    /// full of them.</summary>
    private void ShaftDust(string id, bool settled = false)
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
        Motes(parent, new Transform3D(basis, center), new Vector3(WindowWidth * 0.4f, ShaftLength * 0.45f, 0.45f), 64, settled);
    }

    /// <summary>Dust drifting slowly down a shaft of daylight: <paramref name="amount"/> motes in a box
    /// of half-size <paramref name="extents"/> about <paramref name="at"/> (its Y axis down the shaft),
    /// under <paramref name="parent"/>, where Still stops them. <paramref name="settled"/>: the shaft
    /// is already full of them.</summary>
    private void Motes(Node3D parent, Transform3D at, Vector3 extents, int amount, bool settled)
    {
        var fade = new Gradient();
        fade.Offsets = new float[] { 0f, 0.2f, 0.8f, 1f };
        fade.Colors = new Color[]
        {
            new Color(MoteLight, 0f), new Color(MoteLight, 0.6f), new Color(MoteLight, 0.6f), new Color(MoteLight, 0f),
        };
        var room = extents + new Vector3(1.1f, 1.1f, 1.1f);
        var motes = new GpuParticles3D
        {
            Name = "ShaftDust",
            Amount = amount,
            Lifetime = 8.0,
            Preprocess = settled ? 8.0 : 0.0,
            Explosiveness = 0f,
            Randomness = 0.5f,
            LocalCoords = false,
            Emitting = true,
            VisibilityAabb = new Aabb(-room, room * 2f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = extents,
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
        motes.GlobalTransform = at;
        _shaftDust.Add(motes);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The breaches (S2b)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>A wall's limewashed slab as BuildLongWall and BuildEastWall lay it, recorded with where
    /// it went in its batch, so a breach can take it out and lay the wall again round its holes.</summary>
    private sealed class WallSlab
    {
        public Node3D Group;
        public int Index;
        public Vector3 Center;
        public Vector3 Size;
        public bool Opened;
        /// <summary>The wall laid again round the panels (null until the first breach).</summary>
        public Node3D Rebuilt;
        /// <summary>The panels breaches have built into it: X along the wall, Y up (world).</summary>
        public readonly List<Rect2> Panels = new();
    }

    /// <summary>One breach: the hole's middle on the wall's inner face, the wall's frame there, the
    /// hole's half-size, the panel of wall it is built into (X along the wall, Y up, world), how far
    /// its pieces spread along the wall from the middle, and how steeply its light falls.</summary>
    private sealed class Breach
    {
        public WallSlab Slab;
        public Vector3 Center;
        public Vector3 Along;
        public Vector3 Inward;
        public float HalfWidth;
        public float HalfHeight;
        public Rect2 Panel;
        public Vector2 Spread;
        public float Pitch;
    }

    /// <summary>True for the pieces that are holes blown through a wall.</summary>
    public static bool IsBreach(string id) =>
        id == PieceBreachEastSouth || id == PieceBreachSouthA || id == PieceBreachEastNorth || id == PieceBreachSouthB;

    /// <summary>The breaches beside the east doors come in from outside; the south ones are found by a
    /// spell from the doors first (CombatManager.Sundering.cs).</summary>
    public static bool IsEastBreach(string id) => id == PieceBreachEastSouth || id == PieceBreachEastNorth;

    /// <summary>Lays a wall's slab as Box would, and records where it went in its batch.</summary>
    private void Slab(Node3D g, Vector3 center, Vector3 size)
    {
        int index = _batches.TryGetValue((g, Prim.Box, Paint.Wash, false), out var list) ? list.Count : 0;
        Box(g, Paint.Wash, false, center, size, Limewash);
        _slabs[g] = new WallSlab { Group = g, Index = index, Center = center, Size = size };
    }

    /// <summary>A breach's place and size from the hall's layout. South: under its window, between the
    /// piers, from below the walk's top (which hides the hole's foot) to under the window's sill stone,
    /// which stays. East: between the door frame and the window on that side, from behind the plinth
    /// up. Null when its wall (or its window) is not there.</summary>
    private Breach BreachFor(string id)
    {
        if (id == PieceBreachSouthA || id == PieceBreachSouthB)
        {
            var wall = GetNodeOrNull<Node3D>("WallSouth");
            string window = id == PieceBreachSouthA ? PieceWindowA : PieceWindowB;
            if (wall == null || !_slabs.TryGetValue(wall, out var slab) || !_breakPivots.TryGetValue(window, out var pivot))
                return null;
            float bottom = _floorY + TierRise * (Tiers + 1) - 0.3f;
            float top = _floorY + WindowSill - 0.38f;
            return new Breach
            {
                Slab = slab,
                Center = new Vector3(pivot.X, (bottom + top) * 0.5f, _zWallS),
                Along = Vector3.Right,
                Inward = Vector3.Back,
                HalfWidth = BreachSouthHalfWidth,
                HalfHeight = (top - bottom) * 0.5f,
                Panel = new Rect2(pivot.X - WindowSpacing * 0.5f, _floorY, WindowSpacing, WindowSill - 0.3f),
                Spread = new Vector2(-BreachSouthHalfWidth - 0.8f, BreachSouthHalfWidth + 0.8f),
                Pitch = 34f,
            };
        }
        if (id == PieceBreachEastSouth || id == PieceBreachEastNorth)
        {
            var wall = GetNodeOrNull<Node3D>("WallEast");
            if (wall == null || !_slabs.TryGetValue(wall, out var slab))
                return null;
            float k = id == PieceBreachEastNorth ? 1f : -1f;
            float z = _cz + k * BreachEastOffset;
            return new Breach
            {
                Slab = slab,
                Center = new Vector3(_xWallE, _floorY + BreachEastMiddle, z),
                Along = Vector3.Back,
                Inward = Vector3.Left,
                HalfWidth = BreachEastHalfWidth,
                HalfHeight = BreachEastHalfHeight,
                Panel = new Rect2(z - 1.75f, _floorY, 3.5f, 8.0f),
                // The pieces spread away from the doors, so none lands against an open leaf.
                Spread = k > 0f ? new Vector2(-1.6f, 2.6f) : new Vector2(-2.6f, 1.6f),
                Pitch = 34f,   // steep enough to reach the floor before the board
            };
        }
        return null;
    }

    /// <summary>A breach: the wall's slab comes out of its batch (the first time) and the wall is laid
    /// again round the panel, the panel with its hole goes in, the daylight shows through and falls
    /// into the hall in a shaft with dust in it, and the wall's pieces lie in front of it. Live, they
    /// fly there, with a blast, dust and the light flaring; <paramref name="instant"/>, they are
    /// already there. The shape and the pieces come from the id, the same every time.</summary>
    private void OpenBreach(string id, bool instant)
    {
        var b = BreachFor(id);
        if (b == null || (!b.Slab.Opened && !TakeOutSlab(b.Slab)))
        {
            GD.PushWarning($"[Hall] {id}: its wall is not built as the hall expects, so it does not open.");
            return;
        }
        var rng = new System.Random(StableSeed(id));
        var rough = BreachRoughness(rng, BreachSides);
        b.Slab.Panels.Add(b.Panel);
        RebuildSlab(b.Slab);

        // In the wall's group, so the cutaway hides it all with the wall.
        var g = new Node3D { Name = "Breach_" + id };
        b.Slab.Group.AddChild(g);
        g.AddChild(new MeshInstance3D
        {
            Name = "Panel",
            Mesh = BreachPanelMesh(b, rough, rng),
            MaterialOverride = MaterialFor(Paint.Wash),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // The daylight outside, seen through the hole from anywhere in the hall.
        float cardWidth = 2f * b.HalfWidth + 5f;
        float cardHeight = 2f * b.HalfHeight + 4f;
        Solo(g, Prim.Box, Paint.Glow,
             new Transform3D(new Basis(b.Along * cardWidth, Vector3.Up * cardHeight, b.Inward * 0.06f),
                             b.Center - b.Inward * (WallThick + 0.35f)),
             WindowLight);

        BreachRim(b, rough, rng, g);

        // The light falls in, down to the floor (stone in its way ends it sooner), with dust in it.
        float pitch = Mathf.DegToRad(b.Pitch);
        Vector3 down = (b.Inward * Mathf.Cos(pitch) + Vector3.Down * Mathf.Sin(pitch)).Normalized();
        Vector3 start = b.Center + b.Inward * 0.15f + Vector3.Up * (b.HalfHeight * 0.25f);
        float shaftLength = Mathf.Clamp((start.Y - _floorY) / Mathf.Sin(pitch), 3f, 7.5f);
        float shaftWidth = 2f * b.HalfWidth * 0.8f;
        Vector3 middle = start + down * (shaftLength * 0.5f);
        foreach (var across in new[] { b.Along, b.Along.Cross(down).Normalized() })
            Solo(g, Prim.Quad, Paint.Shaft,
                 new Transform3D(new Basis(across * shaftWidth, down * shaftLength, across.Cross(down).Normalized()), middle),
                 ShaftLight);
        Motes(g, new Transform3D(new Basis(b.Along, down, b.Along.Cross(down).Normalized()), middle),
              new Vector3(shaftWidth * 0.4f, shaftLength * 0.45f, 0.5f), 48, instant);

        // The light it lets in stays when the cutaway hides the wall, as the tiers it falls on do.
        var light = new OmniLight3D
        {
            Name = "BreachLight",
            Position = b.Center + b.Inward * 1.6f + Vector3.Up * 0.2f,
            LightColor = BreachDaylight,
            LightEnergy = 1.4f,
            OmniRange = 8f,
            ShadowEnabled = false,
        };
        _hall.AddChild(light);

        ThrowBreachDebris(b, rough, rng, instant);
        if (instant)
            return;

        light.LightEnergy = 4f;
        light.CreateTween().TweenProperty(light, "light_energy", 1.4f, 0.7)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        Vector3 mouth = b.Center + b.Inward * 0.6f;
        var blast = new BurstVfx { Tint = BlastLight, Radius = 1.9f, GroundRing = false };
        AddChild(blast);
        blast.GlobalPosition = mouth;
        _ = blast.Play(1f);
        Dust(mouth, 2.8f);
        Dust(b.Center + b.Inward * 2.2f + Vector3.Down * (b.HalfHeight * 0.6f), 2.2f);
        Vector3 field = b.Center + b.Inward * (BreachThrow * 0.55f);
        field.Y = GroundAt(field) + 0.3f;
        GetTree().CreateTimer(0.4).Timeout += () =>
        {
            if (IsInstanceValid(this) && !_stilled)
                Dust(field, 2.4f);
        };
    }

    /// <summary>Takes a wall's slab out of its batch: its instance there shrinks to nothing. False when
    /// the batch is not where the slab was laid.</summary>
    private bool TakeOutSlab(WallSlab slab)
    {
        var expected = Scaled(slab.Center, Basis.Identity, slab.Size);
        foreach (Node child in slab.Group.GetChildren())
        {
            if (child is not MultiMeshInstance3D mmi || mmi.Multimesh == null || mmi.Multimesh.InstanceCount <= slab.Index)
                continue;
            if (!mmi.Multimesh.GetInstanceTransform(slab.Index).IsEqualApprox(expected))
                continue;
            mmi.Multimesh.SetInstanceTransform(slab.Index, new Transform3D(Basis.FromScale(Vector3.Zero), slab.Center));
            slab.Opened = true;
            return true;
        }
        return false;
    }

    /// <summary>Lays the wall again round every panel built into it: whole-height pieces between the
    /// panels, and the wall above and below each one (each panel is its breach's own).</summary>
    private void RebuildSlab(WallSlab slab)
    {
        if (slab.Rebuilt != null && IsInstanceValid(slab.Rebuilt))
        {
            slab.Group.RemoveChild(slab.Rebuilt);
            slab.Rebuilt.QueueFree();
        }
        slab.Rebuilt = new Node3D { Name = "SlabRebuilt" };
        slab.Group.AddChild(slab.Rebuilt);

        bool alongX = slab.Size.X >= slab.Size.Z;
        float mid = alongX ? slab.Center.X : slab.Center.Z;
        float half = (alongX ? slab.Size.X : slab.Size.Z) * 0.5f;
        float bottom = slab.Center.Y - slab.Size.Y * 0.5f;
        float top = slab.Center.Y + slab.Size.Y * 0.5f;
        var panels = new List<Rect2>(slab.Panels);
        panels.Sort((p, q) => p.Position.X.CompareTo(q.Position.X));
        float at = mid - half;
        foreach (var p in panels)
        {
            SlabPiece(slab, alongX, at, p.Position.X, bottom, top);
            SlabPiece(slab, alongX, p.Position.X, p.End.X, bottom, p.Position.Y);
            SlabPiece(slab, alongX, p.Position.X, p.End.X, p.End.Y, top);
            at = p.End.X;
        }
        SlabPiece(slab, alongX, at, mid + half, bottom, top);
    }

    private void SlabPiece(WallSlab slab, bool alongX, float from, float to, float bottom, float top)
    {
        if (to - from < 0.001f || top - bottom < 0.001f)
            return;
        float middle = (from + to) * 0.5f;
        var center = alongX
            ? new Vector3(middle, (bottom + top) * 0.5f, slab.Center.Z)
            : new Vector3(slab.Center.X, (bottom + top) * 0.5f, middle);
        var size = alongX
            ? new Vector3(to - from, top - bottom, slab.Size.Z)
            : new Vector3(slab.Size.X, top - bottom, to - from);
        Solo(slab.Rebuilt, Prim.Box, Paint.Wash, Scaled(center, Basis.Identity, size), Limewash);
    }

    /// <summary>The panel a breach is built into, with its hole: the inner face, blackened toward the
    /// ragged edge, and the hole's sides through the wall. Both are cut along rays from the hole's
    /// middle (the outline's own, and one to each corner of the panel), so every wedge is convex and
    /// the panel meets the wall round it edge to edge. Its paint rides on the vertex colors, as the
    /// batched pieces' rides on theirs.</summary>
    private static ArrayMesh BreachPanelMesh(Breach b, float[] rough, System.Random rng)
    {
        float cu = b.Center.Dot(b.Along);
        float u0 = b.Panel.Position.X - cu;
        float u1 = b.Panel.End.X - cu;
        float v0 = b.Panel.Position.Y - b.Center.Y;
        float v1 = b.Panel.End.Y - b.Center.Y;
        var soot = new float[BreachSides];
        for (int i = 0; i < BreachSides; i++)
            soot[i] = 0.2f + 0.45f * (float)rng.NextDouble();

        var rays = new List<float>();
        for (int i = 0; i < BreachSides; i++)
            rays.Add(Mathf.Tau * i / BreachSides);
        foreach (var corner in new[] { new Vector2(u1, v1), new Vector2(u0, v1), new Vector2(u0, v0), new Vector2(u1, v0) })
            rays.Add(Mathf.PosMod(Mathf.Atan2(corner.Y, corner.X), Mathf.Tau));
        rays.Sort();
        for (int i = rays.Count - 1; i > 0; i--)
            if (rays[i] - rays[i - 1] < 1e-3f)
                rays.RemoveAt(i);
        if (rays.Count > 1 && rays[0] + Mathf.Tau - rays[rays.Count - 1] < 1e-3f)
            rays.RemoveAt(rays.Count - 1);

        int n = rays.Count;
        var hole = new Vector2[n];
        var scorch = new Vector2[n];
        var edge = new Vector2[n];
        for (int j = 0; j < n; j++)
        {
            var dir = new Vector2(Mathf.Cos(rays[j]), Mathf.Sin(rays[j]));
            float reach = RayToRect(dir, u0, u1, v0, v1);
            float r = Mathf.Min(Superellipse(dir, b.HalfWidth, b.HalfHeight) * Around(rough, rays[j]), reach - 0.05f);
            float w = Mathf.Min(Around(soot, rays[j]), (reach - r) * 0.85f);
            hole[j] = dir * r;
            scorch[j] = dir * (r + w);
            edge[j] = dir * reach;
        }

        Vector3 At(Vector2 p, float depth) => b.Center + b.Along * p.X + Vector3.Up * p.Y - b.Inward * depth;
        Color sootEdge = Limewash.Darkened(0.4f);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int j = 0; j < n; j++)
        {
            int k = (j + 1) % n;
            PaintedQuad(st, b.Inward, At(hole[j], 0f), Soot, At(hole[k], 0f), Soot,
                        At(scorch[k], 0f), sootEdge, At(scorch[j], 0f), sootEdge);
            PaintedQuad(st, b.Inward, At(scorch[j], 0f), sootEdge, At(scorch[k], 0f), sootEdge,
                        At(edge[k], 0f), Limewash, At(edge[j], 0f), Limewash);

            // The hole's side, through the wall, facing the hole's middle.
            Vector2 run = hole[k] - hole[j];
            var side = new Vector2(-run.Y, run.X);
            if (side.LengthSquared() < 1e-8f)
                continue;
            if (side.Dot(-(hole[j] + hole[k])) < 0f)
                side = -side;
            side = side.Normalized();
            Vector3 normal = (b.Along * side.X + Vector3.Up * side.Y).Normalized();
            PaintedQuad(st, normal, At(hole[j], 0f), Soot, At(hole[k], 0f), Soot,
                        At(hole[k], WallThick), RubbleStone, At(hole[j], WallThick), RubbleStone);
        }
        return st.Commit();
    }

    /// <summary>Broken masonry round the hole's edge: a few blocks half in its mouth, standing proud of
    /// the face, so the edge is ragged in depth too.</summary>
    private void BreachRim(Breach b, float[] rough, System.Random rng, Node3D g)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = MeshFor(Prim.Box),
        };
        mm.InstanceCount = BreachRimChunks;
        for (int i = 0; i < BreachRimChunks; i++)
        {
            float a = Mathf.Tau * (float)rng.NextDouble();
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            float r = Superellipse(dir, b.HalfWidth, b.HalfHeight) * Around(rough, a) * 1.02f;
            Vector3 at = b.Center + b.Along * (dir.X * r) + Vector3.Up * (dir.Y * r) + b.Inward * Lerp(rng, -0.5f, 0.1f);
            var size = new Vector3(Lerp(rng, 0.2f, 0.45f), Lerp(rng, 0.15f, 0.35f), Lerp(rng, 0.2f, 0.5f));
            var axis = new Vector3(Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f));
            var turn = axis.LengthSquared() > 1e-4f ? new Basis(axis.Normalized(), Lerp(rng, 0.2f, 1.2f)) : Basis.Identity;
            mm.SetInstanceTransform(i, new Transform3D(turn * Basis.FromScale(size), at));
            mm.SetInstanceColor(i, rng.NextDouble() < 0.5 ? Limewash : RubbleStone);
        }
        g.AddChild(new MultiMeshInstance3D
        {
            Name = "Rim",
            Multimesh = mm,
            MaterialOverride = MaterialFor(Paint.Stone),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>The wall the breach throws into the hall: a heap at its foot and pieces thrown across the
    /// walk and the tiers (or the floor before the doors), each on its own arc, turning. In the hall's
    /// group, so they stay when the cutaway hides the wall. Instant, they are already lying there.</summary>
    private void ThrowBreachDebris(Breach b, float[] rough, System.Random rng, bool instant)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = MeshFor(Prim.Box),
        };
        mm.InstanceCount = BreachDebris;
        var from = new Vector3[BreachDebris];
        var to = new Vector3[BreachDebris];
        var size = new Vector3[BreachDebris];
        var axis = new Vector3[BreachDebris];
        var spin = new float[BreachDebris];
        var arc = new float[BreachDebris];
        var flight = new float[BreachDebris];
        float longest = 0.1f;
        for (int i = 0; i < BreachDebris; i++)
        {
            bool heap = i < BreachHeap;
            size[i] = new Vector3(Lerp(rng, 0.14f, 0.5f), Lerp(rng, 0.1f, 0.36f), Lerp(rng, 0.14f, 0.45f)) * (heap ? 1.3f : 1f);
            float a = Mathf.Tau * (float)rng.NextDouble();
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            float r = Superellipse(dir, b.HalfWidth, b.HalfHeight) * Around(rough, a) * Lerp(rng, 0.1f, 0.8f);
            from[i] = b.Center + b.Along * (dir.X * r) + Vector3.Up * (dir.Y * r) - b.Inward * (WallThick * Lerp(rng, 0.2f, 0.8f));
            float reach = heap ? Lerp(rng, 0.15f, 1.3f) : Lerp(rng, 1.0f, BreachThrow);
            float lateral = Lerp(rng, b.Spread.X, b.Spread.Y) * (heap ? 0.7f : 1f);
            Vector3 land = b.Center + b.Along * lateral + b.Inward * reach;
            land.Y = GroundAt(land) + size[i].Y * 0.5f;
            to[i] = land;
            var turn = new Vector3(Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f));
            axis[i] = turn.LengthSquared() > 1e-4f ? turn.Normalized() : Vector3.Up;
            spin[i] = Lerp(rng, 1.5f, 5.5f);
            arc[i] = heap ? Lerp(rng, 0.2f, 0.6f) : Lerp(rng, 0.6f, 1.8f);
            flight[i] = heap ? Lerp(rng, 0.35f, 0.6f) : Lerp(rng, 0.6f, 1.05f);
            longest = Mathf.Max(longest, flight[i]);
            double tone = rng.NextDouble();
            Color color = tone < 0.55 ? Limewash : tone < 0.85 ? RubbleStone : TrimStone;
            mm.SetInstanceColor(i, color.Darkened(Lerp(rng, 0f, 0.15f)));
        }

        Transform3D Pose(int i, float t)
        {
            Vector3 p = from[i].Lerp(to[i], t) + Vector3.Up * (arc[i] * 4f * t * (1f - t));
            return new Transform3D(new Basis(axis[i], spin[i] * t) * Basis.FromScale(size[i]), p);
        }
        for (int i = 0; i < BreachDebris; i++)
            mm.SetInstanceTransform(i, Pose(i, instant ? 1f : 0f));
        var debris = new MultiMeshInstance3D
        {
            Name = "BreachDebris",
            Multimesh = mm,
            MaterialOverride = MaterialFor(Paint.Stone),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
        _hall.AddChild(debris);
        if (instant)
            return;
        debris.CreateTween().TweenMethod(Callable.From<float>(s =>
        {
            if (!IsInstanceValid(debris))
                return;
            float now = s * longest;
            for (int i = 0; i < BreachDebris; i++)
                mm.SetInstanceTransform(i, Pose(i, Mathf.Clamp(now / flight[i], 0f, 1f)));
        }), 0f, 1f, longest);
    }

    /// <summary>The top of whatever stands under <paramref name="p"/>: a tier, the walk along a long
    /// wall, or the floor.</summary>
    private float GroundAt(Vector3 p)
    {
        if (p.X >= _xTierW && p.X <= _xTierE)
        {
            float fromMiddle = Mathf.Abs(p.Z - _cz);
            for (int k = Tiers; k >= 0; k--)
                if (fromMiddle >= _tierFront + k * TierDepth)
                    return _floorY + TierRise * (k + 1);
        }
        return _floorY;
    }

    /// <summary>The hole's roughness round its outline, as a fraction of the shape: lumps (a few,
    /// smoothed), bites (in and out by turns, so the edge zigzags like broken courses) and three crack
    /// tips where the hole runs on into the wall.</summary>
    private static float[] BreachRoughness(System.Random rng, int n)
    {
        var lumps = new float[6];
        for (int i = 0; i < lumps.Length; i++)
            lumps[i] = (float)rng.NextDouble();
        var ring = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = 6f * i / n;
            int i0 = (int)t % 6;
            float s = t - Mathf.Floor(t);
            s = s * s * (3f - 2f * s);
            float lump = Mathf.Lerp(lumps[i0], lumps[(i0 + 1) % 6], s);
            float bite = (float)rng.NextDouble() * (i % 2 == 0 ? 0.12f : -0.16f);
            ring[i] = 0.80f + 0.16f * lump + bite;
        }
        for (int c = 0; c < 3; c++)
        {
            int i = rng.Next(n);
            ring[i] = Mathf.Min(1.12f, ring[i] + 0.18f);
        }
        return ring;
    }

    /// <summary>A ring of values round the outline, read at <paramref name="angle"/> between its points.</summary>
    private static float Around(float[] ring, float angle)
    {
        float t = Mathf.PosMod(angle, Mathf.Tau) / Mathf.Tau * ring.Length;
        int i0 = (int)t % ring.Length;
        return Mathf.Lerp(ring[i0], ring[(i0 + 1) % ring.Length], t - Mathf.Floor(t));
    }

    /// <summary>How far a rounded square of half-size (<paramref name="a"/>, <paramref name="b"/>) reaches
    /// from its middle along <paramref name="dir"/>.</summary>
    private static float Superellipse(Vector2 dir, float a, float b)
    {
        float s = Mathf.Pow(Mathf.Abs(dir.X) / a, BreachSquareness) + Mathf.Pow(Mathf.Abs(dir.Y) / b, BreachSquareness);
        return s > 1e-9f ? Mathf.Pow(s, -1f / BreachSquareness) : Mathf.Max(a, b);
    }

    /// <summary>How far a ray from inside a rectangle (u0 &lt; 0 &lt; u1, v0 &lt; 0 &lt; v1) runs before it
    /// leaves it.</summary>
    private static float RayToRect(Vector2 dir, float u0, float u1, float v0, float v1)
    {
        float t = float.MaxValue;
        if (dir.X > 1e-6f)
            t = Mathf.Min(t, u1 / dir.X);
        else if (dir.X < -1e-6f)
            t = Mathf.Min(t, u0 / dir.X);
        if (dir.Y > 1e-6f)
            t = Mathf.Min(t, v1 / dir.Y);
        else if (dir.Y < -1e-6f)
            t = Mathf.Min(t, v0 / dir.Y);
        return t;
    }

    /// <summary>Two painted triangles, a to c the diagonal (the quad must be convex).</summary>
    private static void PaintedQuad(SurfaceTool st, Vector3 n, Vector3 a, Color ca, Vector3 b, Color cb,
                                    Vector3 c, Color cc, Vector3 d, Color cd)
    {
        PaintedTri(st, n, a, ca, b, cb, c, cc);
        PaintedTri(st, n, a, ca, c, cc, d, cd);
    }

    /// <summary>One flat triangle facing <paramref name="n"/>, each corner with its color, wound as Tri
    /// winds it. A triangle with no area is skipped.</summary>
    private static void PaintedTri(SurfaceTool st, Vector3 n, Vector3 a, Color ca, Vector3 b, Color cb, Vector3 c, Color cc)
    {
        Vector3 face = (b - a).Cross(c - a);
        if (face.LengthSquared() < 1e-12f)
            return;
        if (face.Dot(n) > 0f)
        {
            (b, c) = (c, b);
            (cb, cc) = (cc, cb);
        }
        st.SetColor(ca);
        st.SetNormal(n);
        st.AddVertex(a);
        st.SetColor(cb);
        st.SetNormal(n);
        st.AddVertex(b);
        st.SetColor(cc);
        st.SetNormal(n);
        st.AddVertex(c);
    }

    /// <summary>A seed from an id that is the same in every run (string.GetHashCode is not).</summary>
    internal static int StableSeed(string id)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in id ?? "")
            {
                h ^= c;
                h *= 16777619;
            }
            return (int)(h & 0x7fffffff);
        }
    }

    private static float Lerp(System.Random rng, float a, float b) => a + (b - a) * (float)rng.NextDouble();

    // ════════════════════════════════════════════════════════════════════════
    //  The stray spells (S2b)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Somewhere off the board for a stray spell to burst. The Keepers' (<paramref name="west"/>)
    /// land at the dais end: the west wall round the great window, the dais between the chairs and its
    /// edge, or a long wall's west half. The faculty's land at the doors' end: the east wall (or out
    /// through the open doors), the floor before the doors, or a long wall's east half. Never on the
    /// board, and never on the gallery's deck.</summary>
    public Vector3 StrayTarget(System.Random rng, bool west)
    {
        float xMid = (_xWallW + _xWallE) * 0.5f;
        double roll = rng.NextDouble();
        if (west)
        {
            if (roll < 0.3)
                return new Vector3(_xWallW + 0.3f, _floorY + Lerp(rng, 2.5f, 12f), _cz + Lerp(rng, -9f, 9f));
            if (roll < 0.5)
            {
                float a = Mathf.Tau * (float)rng.NextDouble();
                float r = Lerp(rng, 4.2f, DaisTopRadius * Sqrt3Half - 0.2f);
                return new Vector3(_xDais + Mathf.Cos(a) * r, _daisTop + 0.05f, _cz + Mathf.Sin(a) * r);
            }
            return LongWallPoint(rng, _xWallW + 1.5f, xMid);
        }
        if (roll < 0.35)
        {
            float z = _cz + Lerp(rng, -11f, 11f);
            float y = _floorY + Lerp(rng, 1f, 12f);
            bool outside = DoorsOpen && Mathf.Abs(z - _cz) < 2.3f && y < _floorY + 7.6f;
            return new Vector3(outside ? _xWallE + 3f : _xWallE - 0.3f, y, z);
        }
        if (roll < 0.55)
            return new Vector3(Lerp(rng, _xStripE + 1.4f, _xWallE - 1.5f), _floorY + 0.05f, _cz + Lerp(rng, -6f, 6f));
        return LongWallPoint(rng, xMid, _xWallE - 1.5f);
    }

    /// <summary>A point just off a long wall's inner face, between <paramref name="x0"/> and
    /// <paramref name="x1"/>, above the walk, and over or under the north gallery's deck.</summary>
    private Vector3 LongWallPoint(System.Random rng, float x0, float x1)
    {
        bool north = rng.NextDouble() < 0.5;
        float y = _floorY + Lerp(rng, 2.6f, 13f);
        if (north && y > _floorY + GalleryDeck - 1.4f && y < _floorY + GalleryDeck + 1.4f)
            y += 2.8f;
        return new Vector3(Lerp(rng, x0, x1), y, north ? _zWallN - 0.3f : _zWallS + 0.3f);
    }

    /// <summary>A puff of grit where a stray spell bursts on stone. Nothing once the hall is still.</summary>
    public void ImpactDust(Vector3 at, float radius)
    {
        if (!_stilled)
            Dust(at, radius);
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
    /// model, only the lights pull apart (and when they are part of it too, nothing does).
    /// <paramref name="settled"/> (the hall seen again): already apart, with no motion.</summary>
    public void CatchSphere(float seconds, bool settled = false)
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
        var tw = settled ? null : _shards.CreateTween().SetParallel(true);

        if (sphere != null)
        {
            float r = sphere.Radius;
            var shardGlass = (StandardMaterial3D)GlassMaterial().Duplicate();
            shardGlass.CullMode = BaseMaterial3D.CullModeEnum.Disabled;   // a shard shows its inside face
            _shardGlass = shardGlass;
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
                if (tw != null)
                    tw.TweenProperty(shard, "position", glass.Position + outward * ShardLift, span)
                      .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
                else
                    shard.Position = glass.Position + outward * ShardLift;
                _shards.AddChild(new MeshInstance3D
                {
                    Name = "Seam" + k,
                    Mesh = SeamMesh(r, from),
                    MaterialOverride = seamGlow,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = glass.Position,
                });
            }
            if (tw != null)
                tw.TweenProperty(seamGlow, "albedo_color:a", 0.85f, span);
            else
                seamGlow.AlbedoColor = new Color(SeamLight, 0.85f);
            glass.Visible = false;
        }
        if (lights)
        {
            if (tw != null)
                tw.TweenProperty(_orbs, "scale", Vector3.One * OrbSpread, span)
                  .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
            else
                _orbs.Scale = Vector3.One * OrbSpread;
        }
        GD.Print(settled ? "[Hall] The sphere as it broke: six shards, a finger's width apart."
                         : "[Hall] The sphere breaks: six shards, a finger's width apart.");
    }

    /// <summary>A shard brought home (its fragment placed): the only warm part of the glass (canon).
    /// <paramref name="index"/> is the fragment's place in ShardZones.FragmentKeys; each fragment
    /// has its own shard of the six.</summary>
    public void WarmShard(int index)
    {
        if (_shards == null || !IsInstanceValid(_shards) || _shardGlass == null)
            return;
        var shard = _shards.GetNodeOrNull<MeshInstance3D>("Shard" + index);
        if (shard == null)
            return;
        var warm = (StandardMaterial3D)_shardGlass.Duplicate();
        warm.AlbedoColor = WarmGlass;
        warm.Emission = WarmLight;
        warm.EmissionEnergyMultiplier = 1.4f;
        shard.MaterialOverride = warm;
    }

    /// <summary>The folio slips from under the candidate's arm: it falls toward knee height,
    /// turning, its pages fanning open, over <paramref name="seconds"/> and slowing into the stop,
    /// so the freeze catches it there. The tween belongs to the folio, which Still stops.
    /// <paramref name="settled"/> (the hall seen again): already there, fanned, with no motion.</summary>
    public void DropFolio(float seconds, bool settled = false)
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
        var turned = new Vector3(Mathf.DegToRad(-14f), Mathf.DegToRad(-32f), Mathf.DegToRad(9f));
        var tw = settled ? null : _folio.CreateTween().SetParallel(true);
        if (tw != null)
        {
            tw.TweenProperty(_folio, "position", rest, span)
              .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(_folio, "rotation", turned, span)
              .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        }
        else
        {
            _folio.Position = rest;
            _folio.Rotation = turned;
        }

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
            if (tw != null)
                tw.TweenProperty(leaf, "rotation:z", Mathf.DegToRad(open), span)
                  .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            else
                leaf.Rotation = new Vector3(0f, 0f, Mathf.DegToRad(open));
        }
    }

    /// <summary>Where the traitor's spell stopped: down its line from the hand toward the Provost, an
    /// arm's length short of the chest of whoever stood on the mark.</summary>
    public Vector3 SpellStopPoint()
    {
        Vector3 hand = TraitorHandPoint;
        Vector3 line = ReaderPoint + new Vector3(0f, FigureChest, 0f) - hand;
        float length = line.Length();
        Vector3 dir = length > 0.01f ? line / length : Vector3.Right;
        Vector3 chest = MarkPoint + new Vector3(0f, FigureChest, 0f);
        float along = Mathf.Clamp((chest - hand).Dot(dir) - SpellArmLength, 0f, length);
        return hand + dir * along;
    }

    /// <summary>The traitor's spell as the Long Second holds it (the hall seen again; the fight flies
    /// its own): a small white light an arm's length from the empty mark, the flash still half open
    /// at the hand, and its line of white fire from the hand to the Provost.</summary>
    public void HangSpell()
    {
        if (_hall == null)
            return;
        var spell = new Node3D { Name = "TheSpell" };
        _hall.AddChild(spell);
        Color white = UITheme.TextPrimary;
        Vector3 at = SpellStopPoint();
        spell.AddChild(new MeshInstance3D
        {
            Name = "Light",
            Mesh = new SphereMesh { Radius = 0.16f, Height = 0.32f, RadialSegments = 16, Rings = 8 },
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = white },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = at,
        });
        spell.AddChild(new OmniLight3D
        {
            LightColor = white,
            LightEnergy = 1.6f,
            OmniRange = 3.2f,
            ShadowEnabled = false,
            Position = at,
        });
        spell.AddChild(new MeshInstance3D
        {
            Name = "Flash",
            Mesh = new SphereMesh { Radius = 0.3f, Height = 0.6f, RadialSegments = 16, Rings = 8 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(white, 0.45f),
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = TraitorHandPoint,
        });
        spell.AddChild(new MeshInstance3D
        {
            Name = "Line",
            Mesh = RibbonMesh(TraitorHandPoint, ReaderPoint + new Vector3(0f, FigureChest, 0f), 0.07f),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(white, 0.9f),
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>A thin straight band from <paramref name="a"/> to <paramref name="b"/>: two crossed
    /// strips <paramref name="width"/> wide, so it reads from any side.</summary>
    private static ArrayMesh RibbonMesh(Vector3 a, Vector3 b, float width)
    {
        Vector3 along = b - a;
        Vector3 dir = along.LengthSquared() > 1e-6f ? along.Normalized() : Vector3.Right;
        Vector3 first = dir.Cross(Vector3.Up);
        if (first.LengthSquared() < 1e-6f)
            first = dir.Cross(Vector3.Right);
        first = first.Normalized() * (width * 0.5f);
        Vector3 second = dir.Cross(first).Normalized() * (width * 0.5f);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var side in new[] { first, second })
        {
            Vector3 n = dir.Cross(side).Normalized();
            Tri(st, n, a - side, a + side, b + side);
            Tri(st, n, a - side, b + side, b - side);
        }
        return st.Commit();
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
