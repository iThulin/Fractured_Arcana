using Godot;
using System.Collections.Generic;

// ============================================================
// HallSet.cs
//
// Purpose:        The Ceremony Hall, built around the combat board for
//                 the prologue's fights (and, later, the finale's,
//                 which canon sets in the same hall).
//
//                 Canon (R24): the hall was the Fast's house of the
//                 sphere before the Academy took it, so it is built
//                 lean: limewashed walls on plain hexagonal piers,
//                 round-headed windows with timber shutters, no
//                 ornament but the sphere and the six schools' colors.
//
//                 The board is the examination floor, laid into the
//                 hall floor in a slate panel with a brass line round
//                 it (the recipe dresses the tiles as floor stone; see
//                 HexGridManager.Interior). A limewashed ceiling on dark
//                 beams closes the hall from inside and lifts away when
//                 the camera rises above it. Down both sides the families'
//                 rows rise in stone tiers with timber benches, up to a
//                 walk along each wall lit by iron lamp-stands. At the
//                 west end the dais is a stepped hexagon: the magisphere
//                 in its cradle at the center, six turned stone bowls
//                 toward its corners (one for each school of the Seal,
//                 ringed in its color), and seven chairs in a ring with
//                 their backs to the sphere, the honored chair nearest
//                 it. The lectern stands at the foot of the dais, with
//                 the candidate's mark between the two. The gallery
//                 (north wall) is where the examining board sits; the
//                 side door (south wall) is the one the Witness leaves
//                 by; the east doors are the Keepers'. Daylight falls in
//                 shafts from the south windows onto the tiers, from the
//                 great west window onto the dais, and from the east
//                 oculus in front of the doors: never across the floor.
//
//                 Built from code (boxes, cylinders, hexagonal prisms,
//                 spheres, domes, light sheets) on the painterly prism
//                 shader, so it needs no art assets. The fixed-size
//                 pieces are swappable: a model at Assets/Hall/<piece>
//                 (.tscn, .glb or .gltf) is placed instead of the code
//                 version, with no code change. PieceGuide lists them.
//
//                 Dollhouse cutaway: each wall hides while the camera
//                 is outside it, so the board is never behind a wall.
//
//                 The same pieces build a second set, the Grand Hall,
//                 for the declaration exams (HallSet.GrandHall.cs): Build
//                 makes it when Look is GrandHallSetId.
// Layer:          Combat / presentation
// Collaborators:  HexGridManager.Interior.cs (builds and clears it),
//                 PainterlyPrism (the material factory), SchoolColors
// ============================================================

/// <summary>The prologue's Ceremony Hall around the board (see the file header).</summary>
public partial class HallSet : Node3D
{
    /// <summary>The interior id a recipe names to get this set.</summary>
    public const string SetId = "ceremony_hall";

    /// <summary>Group the grid uses to find and clear the set.</summary>
    public const string GroupName = "interior_set";

    /// <summary>The east doors stand open (InteriorSpec.DoorsOpen: the Sundering). Set
    /// before Build.</summary>
    public bool DoorsOpen;

    private const string ModelDir = "res://Assets/Hall/";

    /// <summary>The swappable pieces. Each model is authored at real size (meters), with its
    /// origin on the floor at the center of its footprint unless noted.</summary>
    public const string PieceGuide =
        "bench_segment: 3.6 long (X) x 0.36 deep, seat at 0.42; stretched along X to fit. "
        + "chair, chair_honored, chair_astrologer: seat at 0.5, facing +X (back toward -X); set in a ring with their backs to the sphere. "
        + "lectern: reading slope toward +X (the reader stands east of it, facing the dais), top near 1.2. "
        + "magisphere: pedestal, cradle and glass, origin at the pedestal base; cradle lip 1.0 to 1.3, glass center near 2.5. "
        + "turned_bowl: plinth and dome, origin at the plinth base, about 1.2 across and 0.75 tall; the ring in its school's color is added by code. "
        + "lamp_stand: origin at the base, lantern near 2.5; its light is added by code.";

    // ── Layout, meters ───────────────────────────────────────────────────
    private const float FrameWidth = 0.6f;      // the slate frame round the examination floor
    private const float AisleGap = 1.2f;        // the floor's long edges to the first tier
    private const int Tiers = 4;                // the families' rows, each side
    private const float TierDepth = 0.9f;
    private const float TierRise = 0.4f;
    private const float WalkDepth = 1.8f;       // the walk along each wall, one step above the top row
    private const float TierInset = 0.8f;       // the tiers stop this far inside the floor's ends
    private const int TierBlocks = 3;           // the benches stand in three blocks, an aisle between each
    private const float TierAisle = 1.2f;
    private const float BenchDepth = 0.36f;
    private const float BenchSeat = 0.42f;
    private const float SegmentLength = 3.6f;   // bench segments
    private const float WallThick = 1.2f;
    private const float WallHeight = 18f;
    private const float DaisGap = 2.8f;         // the floor's west end to the dais's lowest step
    private const float LecternGap = 1.0f;      // the floor's west end to the lectern
    private const float MarkGap = 2.0f;         // the floor's west end to the candidate's mark
    private const float DaisTopRadius = 6.6f;   // the top hexagon, center to corner
    private const float DaisStepWidth = 0.6f;   // each step ring, corner to corner
    private const int DaisSteps = 2;
    private const float DaisRise = 0.4f;
    private const float DaisBack = 1.0f;        // the lowest step's west face to the west wall
    private const float ChairRing = 3.0f;       // the chairs' distance from the sphere's center
    private const float HonoredRing = 1.9f;     // the honored chair's: the nearest the sphere
    private const float ChairStep = 24f;        // degrees between neighbors on the ring
    private const float BowlRing = 5.6f;        // the six bowls' distance from the center, toward the corners
    private const float EastGap = 6.5f;         // the floor's east end to the east doors
    private const float WindowSpacing = 6.2f;
    private const float WindowWidth = 2.2f;
    private const float WindowHeight = 8.0f;    // the upright part; the round head adds half the width
    private const float WindowSill = 4.5f;
    private const float ShutterOpen = 110f;     // degrees: the south wall's shutters stand open into the hall
    private const float ShutterFolded = 172f;   // the north wall's lie back against it, clear of the gallery
    private const float ShaftPitch = 65f;       // degrees below level: the south windows' light lands on the tiers
    private const float GalleryInset = 0.5f;    // the gallery's end piers stand this far inside the tiers' ends
    private const float GalleryDepth = 2.2f;    // out from the north wall, over the walk
    private const float GalleryDeck = 7.0f;     // the deck's top above the hall floor
    private const float SideDoorWidth = 1.8f;
    private const float SideDoorHeight = 3.2f;  // the lintel stays under the window's sill
    private const float CutawayMargin = 0.5f;   // a wall hides once the camera is this close inside its inner face, or anywhere past it
    private const float CeilingMargin = 1.5f;   // the ceiling hides once the camera is this close under it, or above
    private const float OrbSpin = 0.12f;        // radians a second: the six lights turning in the glass
    private const float Sqrt3Half = 0.8660254f;

    // ── Colors (painted pieces take theirs per instance) ─────────────────
    private static readonly Color FloorStone = new(0.50f, 0.48f, 0.44f);
    private static readonly Color FrameSlate = new(0.19f, 0.20f, 0.22f);
    private static readonly Color Brass = new(0.72f, 0.57f, 0.30f);
    private static readonly Color Limewash = new(0.80f, 0.77f, 0.70f);
    private static readonly Color PierStone = new(0.67f, 0.63f, 0.56f);
    private static readonly Color TrimStone = new(0.60f, 0.56f, 0.50f);
    private static readonly Color PlinthStone = new(0.38f, 0.36f, 0.33f);
    private static readonly Color TierStone = new(0.56f, 0.53f, 0.48f);
    private static readonly Color TierStoneAlt = new(0.52f, 0.49f, 0.45f);
    private static readonly Color WalkStone = new(0.54f, 0.51f, 0.47f);
    private static readonly Color DaisStone = new(0.62f, 0.58f, 0.52f);
    private static readonly Color StepStone = new(0.55f, 0.52f, 0.47f);
    private static readonly Color BowlBronze = new(0.42f, 0.33f, 0.21f);
    private static readonly Color Ceiling = new(0.74f, 0.71f, 0.64f);
    private static readonly Color Timber = new(0.26f, 0.18f, 0.12f);
    private static readonly Color DoorWood = new(0.22f, 0.15f, 0.10f);
    private static readonly Color Iron = new(0.15f, 0.15f, 0.16f);
    private static readonly Color Linen = new(0.84f, 0.80f, 0.70f);
    private static readonly Color WindowLight = new(0.96f, 0.95f, 0.90f);
    private static readonly Color OculusLight = new(0.98f, 0.93f, 0.82f);
    private static readonly Color LampGlow = new(1.0f, 0.80f, 0.52f);
    private static readonly Color LampLight = new(1.0f, 0.80f, 0.56f);
    private static readonly Color ShaftLight = new(1.0f, 0.94f, 0.80f, 0.11f);
    private static readonly Color RegisterCover = new(0.35f, 0.10f, 0.08f);
    private static readonly Color Parchment = new(0.92f, 0.88f, 0.76f);

    /// <summary>The six schools of the Seal: the six lights in the glass and the six bowls.</summary>
    private static readonly CardSchool[] SealSchools =
    {
        CardSchool.Elementalist, CardSchool.Arcanist, CardSchool.Chronomancer,
        CardSchool.Enchanter, CardSchool.Tinker, CardSchool.Necromancer,
    };

    // ── Build state ──────────────────────────────────────────────────────
    private enum Prim { Box, Cylinder, Hex, Sphere, Dome, Quad }
    private enum Paint { Stone, Wash, Wood, Glow, Shaft }

    private readonly Dictionary<(Node3D group, Prim prim, Paint paint, bool shadow), List<(Transform3D xf, Color color)>> _batches = new();
    private readonly List<(Node3D node, Vector3 point, Vector3 inward)> _walls = new();
    private Node3D _hall;
    private Node3D _orbs;
    private Node3D _ceiling;
    private float _ceilingY;

    private float _floorY, _cz, _hw;
    private float _xStripW, _xStripE, _xTierW, _xTierE, _xDais, _xWallW, _xWallE, _zWallN, _zWallS;
    private float _daisTop, _tierFront;

    // ── Shared resources ─────────────────────────────────────────────────
    private static Material _stone, _wash, _wood, _glow, _shaft;
    private static StandardMaterial3D _glass;
    private static Mesh _box, _cylinder, _hex, _sphere, _dome, _quad;
    private static readonly Dictionary<string, PackedScene> _models = new();
    private static readonly HashSet<string> _noModel = new();

    // ════════════════════════════════════════════════════════════════════════
    //  Build
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Builds the hall around a board. <paramref name="boardMin"/> and
    /// <paramref name="boardMax"/> are the grid's padded bounds
    /// (HexGridManager.GridBoundsMin and Max; Y is ignored). The board runs west to east
    /// along X. <paramref name="floorY"/> is the hall floor, where the board's skirts end.</summary>
    public void Build(Vector3 boardMin, Vector3 boardMax, float floorY)
    {
        TopLevel = true;                       // world coordinates, whatever the grid's transform
        Transform = Transform3D.Identity;
        if (Look == GrandHallSetId)
        {
            BuildGrandHall(boardMin, boardMax, floorY);   // HallSet.GrandHall.cs
            return;
        }

        _floorY = floorY;
        _cz = (boardMin.Z + boardMax.Z) * 0.5f;
        _hw = (boardMax.Z - boardMin.Z) * 0.5f;
        _xStripW = boardMin.X;
        _xStripE = boardMax.X;
        _xTierW = _xStripW + TierInset;
        _xTierE = _xStripE - TierInset;

        _tierFront = _hw + AisleGap;
        float wallOff = _tierFront + Tiers * TierDepth + WalkDepth;
        _zWallN = _cz + wallOff;
        _zWallS = _cz - wallOff;

        float lowest = DaisTopRadius + DaisSteps * DaisStepWidth;   // the lowest step, center to corner
        _xDais = _xStripW - DaisGap - lowest * Sqrt3Half;              // its east face stands DaisGap from the floor
        _xWallW = _xDais - lowest * Sqrt3Half - DaisBack;
        _xWallE = _xStripE + EastGap;
        _daisTop = _floorY + DaisRise * (DaisSteps + 1);

        _hall = Group("Hall");
        BuildFloor();
        BuildTiers();
        BuildLongWall(Wall("WallNorth", new Vector3(0f, 0f, _zWallN), new Vector3(0f, 0f, -1f)), _zWallN, 1f);
        BuildLongWall(Wall("WallSouth", new Vector3(0f, 0f, _zWallS), new Vector3(0f, 0f, 1f)), _zWallS, -1f);
        BuildEastWall(Wall("WallEast", new Vector3(_xWallE, 0f, 0f), new Vector3(-1f, 0f, 0f)));
        BuildWestWall(Wall("WallWest", new Vector3(_xWallW, 0f, 0f), new Vector3(1f, 0f, 0f)));
        BuildCeiling();
        BuildDais();
        BuildLectern();
        BuildMagisphere();
        Flush();

        GD.Print($"[Hall] The Ceremony Hall: {_xWallE - _xWallW:0.0} x {_zWallN - _zWallS:0.0} m around a "
                 + $"{_xStripE - _xStripW:0.0} x {2f * _hw:0.0} m examination floor, floor at {_floorY:0.00}.");
    }

    // ── The floor ────────────────────────────────────────────────────────

    /// <summary>The floor slab under the whole hall, the slate panel the examination floor
    /// is laid into (it shows round the board as a frame, and in the notches of its hex
    /// edge) with a brass line outside it, and the candidate's mark between the lectern and
    /// the dais. The set's floor is where HexGridManager.Interior puts it: just under the
    /// tile tops when the recipe lays the board in, or at the bottom of its skirts.</summary>
    private void BuildFloor()
    {
        float len = _xWallE - _xWallW;
        float wid = _zWallN - _zWallS;
        float xMid = (_xWallW + _xWallE) * 0.5f;
        Box(_hall, Paint.Stone, false, new Vector3(xMid, _floorY - 0.25f, _cz),
            new Vector3(len + 2f * WallThick, 0.5f, wid + 2f * WallThick), FloorStone);

        float xc = (_xStripW + _xStripE) * 0.5f;
        float outerLen = _xStripE - _xStripW + 2f * FrameWidth;
        Box(_hall, Paint.Stone, false, new Vector3(xc, _floorY + 0.005f, _cz),
            new Vector3(outerLen, 0.01f, 2f * (_hw + FrameWidth)), FrameSlate);
        foreach (float k in new[] { -1f, 1f })
        {
            Box(_hall, Paint.Wood, false, new Vector3(xc, _floorY + 0.007f, _cz + k * (_hw + FrameWidth + 0.035f)),
                new Vector3(outerLen + 0.14f, 0.014f, 0.07f), Brass);
            float xLine = k < 0f ? _xStripW - FrameWidth - 0.035f : _xStripE + FrameWidth + 0.035f;
            Box(_hall, Paint.Wood, false, new Vector3(xLine, _floorY + 0.007f, _cz), new Vector3(0.07f, 0.014f, 2f * (_hw + FrameWidth)), Brass);
        }

        // The candidate's mark: a ring of brass set in slate, where the named stand.
        var mark = new Vector3(_xStripW - MarkGap, _floorY, _cz);
        Add(_hall, Prim.Cylinder, Paint.Wood, false, Scaled(mark + new Vector3(0f, 0.007f, 0f), Basis.Identity, new Vector3(0.9f, 0.014f, 0.9f)), Brass);
        Add(_hall, Prim.Cylinder, Paint.Stone, false, Scaled(mark + new Vector3(0f, 0.009f, 0f), Basis.Identity, new Vector3(0.76f, 0.018f, 0.76f)), FrameSlate);
    }

    // ── The families' rows ───────────────────────────────────────────────

    /// <summary>The families' rows down both sides: stone tiers stepping up from the aisle to a
    /// walk along each wall, timber benches at the back of each tier in three blocks, and
    /// iron lamp-stands along the walk at its ends and at each aisle.</summary>
    private void BuildTiers()
    {
        float len = _xTierE - _xTierW;
        if (len <= 2f)
            return;   // a floor too short for rows
        float xMid = (_xTierW + _xTierE) * 0.5f;
        float wallOff = _zWallN - _cz;
        float blockLen = (len - (TierBlocks - 1) * TierAisle) / TierBlocks;
        int perBlock = Mathf.Max(1, Mathf.RoundToInt(blockLen / SegmentLength));
        float segment = blockLen / perBlock;
        float walkTop = _floorY + TierRise * (Tiers + 1);

        foreach (float side in new[] { -1f, 1f })
        {
            // Each step runs from its front edge back to the wall; stacked, they read as stairs.
            for (int k = 0; k <= Tiers; k++)
            {
                float front = _tierFront + k * TierDepth;
                float top = _floorY + TierRise * (k + 1);
                float depth = wallOff - front;
                Color tone = k == Tiers ? WalkStone : k % 2 == 0 ? TierStone : TierStoneAlt;
                Box(_hall, Paint.Stone, true, new Vector3(xMid, (_floorY + top) * 0.5f, _cz + side * (front + depth * 0.5f)),
                    new Vector3(len, top - _floorY, depth), tone);
            }

            for (int k = 0; k < Tiers; k++)
            {
                float top = _floorY + TierRise * (k + 1);
                float z = _cz + side * (_tierFront + (k + 1) * TierDepth - 0.04f - BenchDepth * 0.5f);
                for (int b = 0; b < TierBlocks; b++)
                {
                    float x0 = _xTierW + b * (blockLen + TierAisle);
                    // The Sundering breaks one block of the south rows (HallSet.Sundering.cs), so it
                    // is built in a group of its own; it looks the same until it breaks.
                    var rows = side < 0f && b == BreakBenchBlock
                        ? Breakable(PieceBench, _hall, new Vector3(x0 + blockLen * 0.5f, _floorY + TierRise * 2f,
                                                                    _cz + side * (_tierFront + Tiers * TierDepth * 0.5f)))
                        : _hall;
                    for (int i = 0; i < perBlock; i++)
                        BenchSegment(rows, new Vector3(x0 + segment * (i + 0.5f), top, z), segment);
                }
            }

            float zLamp = _cz + side * (_tierFront + Tiers * TierDepth + 0.35f);
            var lampXs = new List<float> { _xTierW + 0.45f, _xTierE - 0.45f };
            for (int b = 1; b < TierBlocks; b++)
                lampXs.Add(_xTierW + b * (blockLen + TierAisle) - TierAisle * 0.5f);
            for (int li = 0; li < lampXs.Count; li++)
            {
                var at = new Vector3(lampXs[li], walkTop, zLamp);
                // One south lamp-stand goes over in the Sundering: its own group, light and all.
                LampStand(side < 0f && li == BreakLampIndex ? Breakable(PieceLamp, _hall, at) : _hall, at, true);
            }
        }
    }

    private void BenchSegment(Node3D g, Vector3 at, float length)
    {
        if (PlaceModel(g, "bench_segment", new Transform3D(Basis.FromScale(new Vector3(length / SegmentLength, 1f, 1f)), at)))
            return;
        var xf = new Transform3D(Basis.Identity, at);
        float legX = length * 0.5f - 0.3f;
        PieceBox(g, Paint.Wood, true, xf, new Vector3(0f, BenchSeat - 0.035f, 0f), new Vector3(length - 0.04f, 0.07f, BenchDepth), Timber);
        foreach (float sx in new[] { -1f, 1f })
            PieceBox(g, Paint.Wood, true, xf, new Vector3(sx * legX, (BenchSeat - 0.07f) * 0.5f, 0f),
                new Vector3(0.1f, BenchSeat - 0.07f, BenchDepth - 0.08f), Timber);
    }

    /// <summary>An iron lamp-stand: a hexagonal foot, a pole, a lantern and a cap. A lit one
    /// carries an omni light at the lantern.</summary>
    private void LampStand(Node3D g, Vector3 basePos, bool lit)
    {
        var at = new Transform3D(Basis.Identity, basePos);
        if (!PlaceModel(g, "lamp_stand", at))
        {
            Add(g, Prim.Hex, Paint.Wood, true, at * new Transform3D(Basis.FromScale(new Vector3(0.5f, 0.12f, 0.5f)), new Vector3(0f, 0.06f, 0f)), Iron);
            Add(g, Prim.Cylinder, Paint.Wood, true, at * new Transform3D(Basis.FromScale(new Vector3(0.09f, 2.2f, 0.09f)), new Vector3(0f, 1.22f, 0f)), Iron);
            PieceBox(g, Paint.Glow, false, at, new Vector3(0f, 2.5f, 0f), new Vector3(0.28f, 0.42f, 0.28f), LampGlow);
            Add(g, Prim.Hex, Paint.Wood, true, at * new Transform3D(Basis.FromScale(new Vector3(0.44f, 0.08f, 0.44f)), new Vector3(0f, 2.75f, 0f)), Iron);
        }
        if (lit)
            Light(basePos + new Vector3(0f, 2.5f, 0f), 1.1f, 9f, g);
    }

    // ── Walls ────────────────────────────────────────────────────────────

    /// <summary>A north or south wall: limewash, a cornice, a row of round-headed windows with
    /// their shutters, and a hexagonal pier between each pair. The north wall carries the
    /// gallery and folds its shutters back; the south wall opens its shutters, lets the light
    /// in onto its tiers, and has the side door under the window nearest the dais.
    /// <paramref name="side"/> is +1 for north (+Z) and -1 for south.</summary>
    private void BuildLongWall(Node3D g, float face, float side)
    {
        float len = _xWallE - _xWallW;
        float xMid = (_xWallW + _xWallE) * 0.5f;

        Box(g, Paint.Wash, false, new Vector3(xMid, _floorY + WallHeight * 0.5f, face + side * WallThick * 0.5f),
            new Vector3(len + 2f * WallThick, WallHeight, WallThick), Limewash);
        Box(g, Paint.Stone, false, new Vector3(xMid, _floorY + WallHeight - 0.35f, face - side * 0.2f),
            new Vector3(len, 0.7f, 0.4f), TrimStone);

        int count = Mathf.Max(1, (int)((len - 8f) / WindowSpacing) + 1);
        float x0 = xMid - (count - 1) * WindowSpacing * 0.5f;

        float doorX = float.NaN;
        if (side < 0f)
        {
            // The side door: under the window nearest the dais, where no tiers stand.
            for (int i = 0; i < count; i++)
            {
                float x = x0 + i * WindowSpacing;
                if (x > _xTierW - (SideDoorWidth * 0.5f + 1.0f))
                    continue;
                if (float.IsNaN(doorX) || Mathf.Abs(x - _xDais) < Mathf.Abs(doorX - _xDais))
                    doorX = x;
            }
            if (!float.IsNaN(doorX))
                SideDoor(g, face, side, doorX);
        }
        else
        {
            Gallery(g, face, side, x0, count);
        }
        Plinth(g, face, side, doorX);

        bool south = side < 0f;
        // The Sundering tears the shutters off two south windows (HallSet.Sundering.cs): the one
        // nearest the breaking bench block, and the one nearest the block toward the doors.
        int breakA = south ? NearestWindow(x0, count, TierBlockCenterX(BreakBenchBlock)) : -1;
        int breakB = south ? NearestWindow(x0, count, TierBlockCenterX(BreakBenchBlock + 1), breakA) : -1;
        for (int i = 0; i < count; i++)
        {
            float x = x0 + i * WindowSpacing;
            var shutterPivot = new Vector3(x, _floorY + WindowSill + WindowHeight * 0.5f, face);
            Node3D shutters = i == breakA ? Breakable(PieceWindowA, g, shutterPivot)
                            : i == breakB ? Breakable(PieceWindowB, g, shutterPivot)
                            : null;
            RoundWindow(g, x, face, side, south ? ShutterOpen : ShutterFolded, south, shutters);
        }

        var flatToHall = new Basis(Vector3.Up, Mathf.Pi / 6f);   // a hexagon's flat face toward the hall
        for (int i = 0; i <= count; i++)
        {
            float x = x0 + (i - 0.5f) * WindowSpacing;
            Add(g, Prim.Hex, Paint.Stone, false, Scaled(new Vector3(x, _floorY + (WallHeight - 0.7f) * 0.5f, face - side * 0.2f),
                flatToHall, new Vector3(1.2f, WallHeight - 0.7f, 1.2f)), PierStone);
        }
    }

    /// <summary>A round-headed window on a long wall: a stone surround, the light, an iron
    /// upright and cross bar, two shutters, and (on the south wall) a shaft of daylight
    /// falling steeply onto the tiers below it.</summary>
    private void RoundWindow(Node3D g, float x, float face, float side, float shutterDeg, bool shaft,
                             Node3D shutterGroup = null)
    {
        float sill = _floorY + WindowSill;
        float r = WindowWidth * 0.5f;
        float head = sill + WindowHeight;                                // the round head's center
        var throughWall = new Basis(Vector3.Right, Mathf.Pi * 0.5f);     // a cylinder's axis turned through the wall (Z)

        Add(g, Prim.Cylinder, Paint.Stone, false, Scaled(new Vector3(x, head, face - side * 0.02f), throughWall,
            new Vector3(WindowWidth + 0.5f, 0.04f, WindowWidth + 0.5f)), TrimStone);
        foreach (float k in new[] { -1f, 1f })
            Box(g, Paint.Stone, false, new Vector3(x + k * (r + 0.125f), sill + WindowHeight * 0.5f, face - side * 0.06f),
                new Vector3(0.25f, WindowHeight, 0.12f), TrimStone);
        Box(g, Paint.Stone, false, new Vector3(x, sill - 0.15f, face - side * 0.2f), new Vector3(WindowWidth + 0.7f, 0.3f, 0.4f), TrimStone);

        float zGlass = face - side * 0.04f;
        Box(g, Paint.Glow, false, new Vector3(x, sill + WindowHeight * 0.5f, zGlass), new Vector3(WindowWidth, WindowHeight, 0.08f), WindowLight);
        Add(g, Prim.Cylinder, Paint.Glow, false, Scaled(new Vector3(x, head, zGlass), throughWall,
            new Vector3(WindowWidth, 0.08f, WindowWidth)), WindowLight);
        Box(g, Paint.Wood, false, new Vector3(x, sill + (WindowHeight + r) * 0.5f, face - side * 0.1f), new Vector3(0.1f, WindowHeight + r, 0.1f), Iron);
        Box(g, Paint.Wood, false, new Vector3(x, sill + WindowHeight * 0.62f, face - side * 0.1f), new Vector3(WindowWidth, 0.1f, 0.1f), Iron);

        foreach (float away in new[] { -1f, 1f })
            Shutter(shutterGroup ?? g, x + away * (r + 0.12f), away, face, side, sill, shutterDeg);

        if (shaft)
        {
            float pitch = Mathf.DegToRad(ShaftPitch);
            var along = new Vector3(0f, -Mathf.Sin(pitch), -side * Mathf.Cos(pitch));
            Shaft(g, new Vector3(x, sill + WindowHeight * 0.5f, face - side * 0.1f), along, 9.5f, WindowWidth * 0.9f, new Vector3(1f, 0f, 0f));
        }
    }

    /// <summary>A timber shutter beside a long-wall window, hinged at <paramref name="hingeX"/>
    /// and strapped twice in iron. <paramref name="away"/> is the direction along the wall away
    /// from the window: shut (0 degrees) the shutter would cover its half of the window, and it
    /// opens through <paramref name="openDeg"/> into the hall.</summary>
    private void Shutter(Node3D g, float hingeX, float away, float face, float side, float sill, float openDeg)
    {
        float w = WindowWidth * 0.5f + 0.1f;
        float a = Mathf.DegToRad(openDeg);
        var along = new Vector3(away, 0f, 0f);
        var into = new Vector3(0f, 0f, -side);
        Vector3 d = -along * Mathf.Cos(a) + into * Mathf.Sin(a);   // from the hinge to the free edge
        Vector3 n = d.Cross(Vector3.Up).Normalized();
        var hinge = new Vector3(hingeX, sill + WindowHeight * 0.5f, face - side * 0.12f);
        Vector3 centre = hinge + d * (w * 0.5f);
        Add(g, Prim.Box, Paint.Wood, false, new Transform3D(new Basis(d * w, Vector3.Up * WindowHeight, n * 0.08f), centre), Timber);
        foreach (float f in new[] { 0.18f, 0.82f })
        {
            Vector3 strap = centre + new Vector3(0f, WindowHeight * (f - 0.5f), 0f);
            Add(g, Prim.Box, Paint.Wood, false, new Transform3D(new Basis(d * (w - 0.08f), Vector3.Up * 0.1f, n * 0.11f), strap), Iron);
        }
    }

    /// <summary>A shaft of daylight: two crossed sheets of soft, additive light from
    /// <paramref name="start"/> along <paramref name="along"/>, each fading to nothing at its
    /// long edges. Where a sheet meets stone it ends there, by the depth test.</summary>
    private void Shaft(Node3D g, Vector3 start, Vector3 along, float length, float width, Vector3 across)
    {
        Vector3 s = along.Normalized();
        Vector3 a1 = across.Normalized();
        Vector3 a2 = a1.Cross(s).Normalized();
        Vector3 centre = start + s * (length * 0.5f);
        foreach (var a in new[] { a1, a2 })
            Add(g, Prim.Quad, Paint.Shaft, false, new Transform3D(new Basis(a * width, s * length, a.Cross(s).Normalized()), centre), ShaftLight);
    }

    /// <summary>The plinth along a long wall, split around the side door when there is one
    /// (<paramref name="doorX"/> is NaN for none). Where tiers stand it is behind them.</summary>
    private void Plinth(Node3D g, float face, float side, float doorX)
    {
        float z = face - side * 0.1f;
        float gap = SideDoorWidth * 0.5f + 0.3f;   // the door's half-width plus its jamb
        var runs = float.IsNaN(doorX)
            ? new[] { (_xWallW, _xWallE) }
            : new[] { (_xWallW, doorX - gap), (doorX + gap, _xWallE) };
        foreach (var (a, b) in runs)
        {
            if (b - a > 0.05f)
                Box(g, Paint.Stone, false, new Vector3((a + b) * 0.5f, _floorY + 0.6f, z), new Vector3(b - a, 1.2f, 0.2f), PlinthStone);
        }
    }

    /// <summary>The side door in the south wall, by the dais: the one the Witness leaves by.
    /// A shut leaf with one iron band, in a stone frame, under a window.</summary>
    private void SideDoor(Node3D g, float face, float side, float x)
    {
        float w = SideDoorWidth;
        float h = SideDoorHeight;
        Box(g, Paint.Wood, false, new Vector3(x, _floorY + h * 0.5f, face - side * 0.12f), new Vector3(w - 0.05f, h, 0.24f), DoorWood);
        Box(g, Paint.Wood, false, new Vector3(x, _floorY + h * 0.62f, face - side * 0.26f), new Vector3(w - 0.15f, 0.14f, 0.04f), Iron);
        foreach (float k in new[] { -1f, 1f })
            Box(g, Paint.Stone, false, new Vector3(x + k * (w * 0.5f + 0.15f), _floorY + (h + 0.4f) * 0.5f, face - side * 0.2f),
                new Vector3(0.3f, h + 0.4f, 0.4f), TrimStone);
        Box(g, Paint.Stone, false, new Vector3(x, _floorY + h + 0.2f, face - side * 0.2f), new Vector3(w + 0.6f, 0.4f, 0.4f), TrimStone);
    }

    /// <summary>The gallery on the north wall, where the examining board sits over the
    /// examination floor: a deck on stone brackets above the walk, a front beam, a parapet
    /// with a rail and posts, and two benches. Empty for now. It runs between the outermost
    /// piers over the tiers (GalleryInset in from their ends), with a bracket on every pier it
    /// passes, so no bracket ever crosses a window (<paramref name="windowX0"/> and
    /// <paramref name="windows"/> are the wall's row).</summary>
    private void Gallery(Node3D g, float face, float side, float windowX0, int windows)
    {
        var brackets = new List<float>();
        for (int i = 0; i <= windows; i++)
        {
            float x = windowX0 + (i - 0.5f) * WindowSpacing;
            if (x >= _xTierW + GalleryInset && x <= _xTierE - GalleryInset)
                brackets.Add(x);
        }
        if (brackets.Count < 2)
            return;   // a floor too short for a bay: no gallery

        float x0 = brackets[0] - 0.6f;                       // out to the piers' far sides
        float len = brackets[brackets.Count - 1] + 0.6f - x0;
        float xc = x0 + len * 0.5f;
        float deck = _floorY + GalleryDeck;
        float zFront = face - side * GalleryDepth;

        Box(g, Paint.Wood, false, new Vector3(xc, deck - 0.15f, face - side * GalleryDepth * 0.5f),
            new Vector3(len, 0.3f, GalleryDepth), Timber);
        Box(g, Paint.Wood, false, new Vector3(xc, deck - 0.45f, zFront + side * 0.15f), new Vector3(len, 0.5f, 0.3f), DoorWood);
        Box(g, Paint.Wood, false, new Vector3(xc, deck + 0.45f, zFront + side * 0.05f), new Vector3(len, 0.9f, 0.08f), Timber);
        Box(g, Paint.Wood, false, new Vector3(xc, deck + 0.95f, zFront + side * 0.05f), new Vector3(len + 0.2f, 0.1f, 0.18f), DoorWood);

        foreach (float x in brackets)
            Box(g, Paint.Stone, false, new Vector3(x, deck - 0.9f, face - side * 0.7f), new Vector3(0.35f, 1.2f, 1.4f), TrimStone);

        int posts = Mathf.Max(2, Mathf.RoundToInt(len / 3f) + 1);
        for (int i = 0; i < posts; i++)
        {
            float x = x0 + 0.3f + (len - 0.6f) * i / (posts - 1);
            Box(g, Paint.Wood, false, new Vector3(x, deck + 0.5f, zFront + side * 0.05f), new Vector3(0.16f, 1.0f, 0.16f), DoorWood);
        }

        int segments = Mathf.Max(1, Mathf.RoundToInt((len - 1f) / SegmentLength));
        float segment = (len - 1f) / segments;
        foreach (float fromWall in new[] { 0.95f, 1.7f })   // clear of the piers
        {
            for (int i = 0; i < segments; i++)
                BenchSegment(g, new Vector3(x0 + 0.5f + segment * (i + 0.5f), deck, face - side * fromWall), segment);
        }
    }

    /// <summary>The east wall: the great doors in their stone frame, a hexagonal oculus above
    /// them with three bars through it corner to corner, a round-headed window to each side,
    /// and the oculus's shaft of light falling steeply in front of the doors.</summary>
    private void BuildEastWall(Node3D g)
    {
        float wid = _zWallN - _zWallS;
        Box(g, Paint.Wash, false, new Vector3(_xWallE + WallThick * 0.5f, _floorY + WallHeight * 0.5f, _cz),
            new Vector3(WallThick, WallHeight, wid + 2f * WallThick), Limewash);
        // The plinth stops at the door frame on either side.
        float plinth = wid * 0.5f - 3.4f;
        foreach (float k in new[] { -1f, 1f })
            Box(g, Paint.Stone, false, new Vector3(_xWallE - 0.1f, _floorY + 0.6f, _cz + k * (3.4f + plinth * 0.5f)),
                new Vector3(0.2f, 1.2f, plinth), PlinthStone);
        Box(g, Paint.Stone, false, new Vector3(_xWallE - 0.2f, _floorY + WallHeight - 0.35f, _cz), new Vector3(0.4f, 0.7f, wid), TrimStone);

        // The east doors: two plain leaves strapped in iron, shut until the Keepers open them.
        // Open (DoorsOpen: the Sundering), each stands swung back into the hall on its hinge at
        // the frame, and the doorway shows the daylight outside.
        foreach (float k in new[] { -1f, 1f })
        {
            if (DoorsOpen)
            {
                float zLeaf = _cz + k * 2.425f;   // hinged 2.6 out, 0.35 thick
                Box(g, Paint.Wood, false, new Vector3(_xWallE - 1.305f, _floorY + 4.0f, zLeaf), new Vector3(2.55f, 8.0f, 0.35f), DoorWood);
                foreach (float y in new[] { 1.5f, 4.0f, 6.5f })
                    Box(g, Paint.Wood, false, new Vector3(_xWallE - 1.305f, _floorY + y, _cz + k * 2.22f), new Vector3(2.4f, 0.18f, 0.06f), Iron);
                continue;
            }
            float z = _cz + k * 1.3f;
            Box(g, Paint.Wood, false, new Vector3(_xWallE - 0.2f, _floorY + 4.0f, z), new Vector3(0.35f, 8.0f, 2.55f), DoorWood);
            foreach (float y in new[] { 1.5f, 4.0f, 6.5f })
                Box(g, Paint.Wood, false, new Vector3(_xWallE - 0.4f, _floorY + y, z), new Vector3(0.06f, 0.18f, 2.4f), Iron);
        }
        if (DoorsOpen)
            Box(g, Paint.Glow, false, new Vector3(_xWallE - 0.04f, _floorY + 4.0f, _cz), new Vector3(0.08f, 8.0f, 5.2f), WindowLight);
        Box(g, Paint.Stone, false, new Vector3(_xWallE - 0.35f, _floorY + 4.6f, _cz - 3.0f), new Vector3(0.7f, 9.2f, 0.8f), TrimStone);
        Box(g, Paint.Stone, false, new Vector3(_xWallE - 0.35f, _floorY + 4.6f, _cz + 3.0f), new Vector3(0.7f, 9.2f, 0.8f), TrimStone);
        Box(g, Paint.Stone, false, new Vector3(_xWallE - 0.35f, _floorY + 9.6f, _cz), new Vector3(0.7f, 0.8f, 6.8f), TrimStone);

        // The oculus: a hexagon of light in a stone frame, barred corner to corner.
        float yOc = _floorY + 13.4f;
        var facingWest = new Basis(new Vector3(0f, 0f, 1f), Mathf.Pi * 0.5f);   // a prism's axis turned onto X
        Add(g, Prim.Hex, Paint.Stone, false, Scaled(new Vector3(_xWallE - 0.06f, yOc, _cz), facingWest, new Vector3(5.8f, 0.12f, 5.8f)), TrimStone);
        Add(g, Prim.Hex, Paint.Glow, false, Scaled(new Vector3(_xWallE - 0.1f, yOc, _cz), facingWest, new Vector3(5.0f, 0.08f, 5.0f)), OculusLight);
        for (int i = 0; i < 3; i++)
        {
            float a = Mathf.Pi / 3f * i;
            var u = new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a));    // along the bar, corner to corner
            var v = new Vector3(0f, Mathf.Cos(a), -Mathf.Sin(a));   // across it, in the wall's plane
            Add(g, Prim.Box, Paint.Stone, false, new Transform3D(new Basis(new Vector3(0.12f, 0f, 0f), v * 0.16f, u * 5.0f),
                new Vector3(_xWallE - 0.16f, yOc, _cz)), TrimStone);
        }
        Add(g, Prim.Hex, Paint.Stone, false, Scaled(new Vector3(_xWallE - 0.18f, yOc, _cz), facingWest, new Vector3(0.7f, 0.14f, 0.7f)), TrimStone);
        float pitch = Mathf.DegToRad(72f);
        Shaft(g, new Vector3(_xWallE - 0.2f, yOc, _cz), new Vector3(-Mathf.Cos(pitch), -Mathf.Sin(pitch), 0f), 15f, 3.0f, new Vector3(0f, 0f, 1f));

        foreach (float k in new[] { -1f, 1f })
            EndWallWindow(g, _xWallE, -1f, _cz + k * 8.5f, 2.2f, 8.0f, _floorY + 4.0f, 1);
    }

    /// <summary>The west wall: the great window behind the dais, the sphere's backdrop, and
    /// its afternoon light falling across the dais onto the honored chair.</summary>
    private void BuildWestWall(Node3D g)
    {
        float wid = _zWallN - _zWallS;
        Box(g, Paint.Wash, false, new Vector3(_xWallW - WallThick * 0.5f, _floorY + WallHeight * 0.5f, _cz),
            new Vector3(WallThick, WallHeight, wid + 2f * WallThick), Limewash);
        Box(g, Paint.Stone, false, new Vector3(_xWallW + 0.1f, _floorY + 0.6f, _cz), new Vector3(0.2f, 1.2f, wid), PlinthStone);
        Box(g, Paint.Stone, false, new Vector3(_xWallW + 0.2f, _floorY + WallHeight - 0.35f, _cz), new Vector3(0.4f, 0.7f, wid), TrimStone);
        EndWallWindow(g, _xWallW, 1f, _cz, 6.0f, 9.0f, _floorY + 3.0f, 2);
        WestWindowCrack(g);   // the Sundering's last break, hidden until it happens (HallSet.Sundering.cs)
        float pitch = Mathf.DegToRad(35f);
        Shaft(g, new Vector3(_xWallW + 0.2f, _floorY + 7.5f, _cz), new Vector3(Mathf.Cos(pitch), -Mathf.Sin(pitch), 0f), 13f, 5.4f, new Vector3(0f, 0f, 1f));
    }

    /// <summary>The ceiling: limewashed boards on the wall tops, carried by a dark tie beam
    /// over each pair of piers and two long beams, so the hall has a lid wherever the camera
    /// looks up from inside it. It hides while the camera is within CeilingMargin of it or
    /// above, so from overhead the hall stays open like a dollhouse. No shadows: it must not
    /// darken the board.</summary>
    private void BuildCeiling()
    {
        _ceiling = Group("Ceiling");
        _ceilingY = _floorY + WallHeight;
        float len = _xWallE - _xWallW;
        float wid = _zWallN - _zWallS;
        float xMid = (_xWallW + _xWallE) * 0.5f;
        Box(_ceiling, Paint.Wash, false, new Vector3(xMid, _ceilingY + 0.1f, _cz),
            new Vector3(len + 2f * WallThick, 0.2f, wid + 2f * WallThick), Ceiling);

        int count = Mathf.Max(1, (int)((len - 8f) / WindowSpacing) + 1);   // as BuildLongWall: a beam over each pier
        float x0 = xMid - (count - 1) * WindowSpacing * 0.5f;
        for (int i = 0; i <= count; i++)
            Box(_ceiling, Paint.Wood, false, new Vector3(x0 + (i - 0.5f) * WindowSpacing, _ceilingY - 0.25f, _cz),
                new Vector3(0.45f, 0.5f, wid), Timber);
        foreach (float k in new[] { -1f, 1f })
            Box(_ceiling, Paint.Wood, false, new Vector3(xMid, _ceilingY - 0.2f, _cz + k * wid * 0.25f),
                new Vector3(len, 0.4f, 0.35f), Timber);
    }

    /// <summary>A round-headed window on an east or west wall (one facing along X).
    /// <paramref name="inward"/> is the X direction into the hall; the window gets
    /// <paramref name="mullions"/> iron uprights, each stopping at the arch, and a cross bar.</summary>
    private void EndWallWindow(Node3D g, float faceX, float inward, float z, float width, float height, float sill, int mullions)
    {
        float r = width * 0.5f;
        float head = sill + height;
        var throughWall = new Basis(new Vector3(0f, 0f, 1f), Mathf.Pi * 0.5f);   // a cylinder's axis turned onto X

        Add(g, Prim.Cylinder, Paint.Stone, false, Scaled(new Vector3(faceX + inward * 0.02f, head, z), throughWall,
            new Vector3(width + 0.6f, 0.04f, width + 0.6f)), TrimStone);
        foreach (float k in new[] { -1f, 1f })
            Box(g, Paint.Stone, false, new Vector3(faceX + inward * 0.06f, sill + height * 0.5f, z + k * (r + 0.15f)),
                new Vector3(0.12f, height, 0.3f), TrimStone);
        Box(g, Paint.Stone, false, new Vector3(faceX + inward * 0.2f, sill - 0.15f, z), new Vector3(0.4f, 0.3f, width + 0.8f), TrimStone);

        float xGlass = faceX + inward * 0.04f;
        Box(g, Paint.Glow, false, new Vector3(xGlass, sill + height * 0.5f, z), new Vector3(0.08f, height, width), WindowLight);
        Add(g, Prim.Cylinder, Paint.Glow, false, Scaled(new Vector3(xGlass, head, z), throughWall, new Vector3(width, 0.08f, width)), WindowLight);
        for (int m = 1; m <= mullions; m++)
        {
            float dz = -r + width * m / (mullions + 1);
            float top = height + Mathf.Sqrt(Mathf.Max(0f, r * r - dz * dz));   // up to the arch
            Box(g, Paint.Wood, false, new Vector3(faceX + inward * 0.1f, sill + top * 0.5f, z + dz), new Vector3(0.1f, top, 0.1f), Iron);
        }
        Box(g, Paint.Wood, false, new Vector3(faceX + inward * 0.1f, sill + height * 0.62f, z), new Vector3(0.1f, 0.1f, width), Iron);
    }

    // ── The dais ─────────────────────────────────────────────────────────

    /// <summary>The dais at the west end: a stepped hexagon, corners north and south and flat
    /// faces east and west, with the magisphere at its center (BuildMagisphere). On it: an
    /// undyed runner, the six turned bowls toward its corners, and seven chairs in a ring with
    /// their backs to the sphere, the six archmagi who sit the dais and the Astrologer's, empty
    /// (the Provost presides from the lectern). The honored chair faces the hall, nearest the
    /// sphere, near enough to rest a hand on the cradle; the Astrologer's is the north end of
    /// the ring. Two unlit lamp-stands stand at its foot.</summary>
    private void BuildDais()
    {
        var centre = new Vector3(_xDais, _daisTop, _cz);
        for (int k = DaisSteps; k >= 0; k--)
        {
            float r = DaisTopRadius + k * DaisStepWidth;
            float top = _daisTop - k * DaisRise;
            Add(_hall, Prim.Hex, Paint.Stone, true, Scaled(new Vector3(_xDais, (_floorY + top) * 0.5f, _cz), Basis.Identity,
                new Vector3(2f * r, top - _floorY, 2f * r)), k == 0 ? DaisStone : StepStone);
        }

        float runnerFrom = HonoredRing + 0.4f;
        float runnerTo = DaisTopRadius * Sqrt3Half;   // the top's east face
        Box(_hall, Paint.Wood, false, new Vector3(_xDais + (runnerFrom + runnerTo) * 0.5f, _daisTop + 0.01f, _cz),
            new Vector3(runnerTo - runnerFrom, 0.02f, 1.2f), Linen);

        for (int j = 0; j < 6; j++)
        {
            float a = Mathf.Tau * j / 6f;
            var toward = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));   // corner j: north first, then round by east
            TurnedBowl(_hall, centre + toward * BowlRing, SealSchools[j]);
        }

        for (int i = 0; i < 7; i++)
        {
            int kind = i == 3 ? 1 : i == 6 ? 2 : 0;
            float a = Mathf.DegToRad((i - 3) * ChairStep);
            var outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));   // 0 degrees faces the hall
            float ring = kind == 1 ? HonoredRing : ChairRing;
            Chair(_hall, new Transform3D(new Basis(Vector3.Up, -a), centre + outward * ring), kind);
        }

        float xFoot = _xStripW - DaisGap + 0.3f;
        foreach (float k in new[] { -1f, 1f })
            LampStand(_hall, new Vector3(xFoot, _floorY, _cz + k * 4.8f), false);

        Light(new Vector3(_xDais + 2.0f, _daisTop + 4.5f, _cz), 1.3f, 13f);
    }

    /// <summary>One of the six turned bowls on the dais: an old bronze bowl, turned down on a
    /// low hexagonal plinth, ringed at its foot in its school's color, the color of its light
    /// in the glass.</summary>
    private void TurnedBowl(Node3D g, Vector3 basePos, CardSchool school)
    {
        var at = new Transform3D(Basis.Identity, basePos);
        if (!PlaceModel(g, "turned_bowl", at))
        {
            Add(g, Prim.Hex, Paint.Stone, true, at * new Transform3D(Basis.FromScale(new Vector3(1.2f, 0.3f, 1.2f)), new Vector3(0f, 0.15f, 0f)), StepStone);
            Add(g, Prim.Dome, Paint.Wood, true, at * new Transform3D(Basis.FromScale(new Vector3(1.0f, 0.9f, 1.0f)), new Vector3(0f, 0.3f, 0f)), BowlBronze);
        }
        Add(g, Prim.Cylinder, Paint.Glow, false, at * new Transform3D(Basis.FromScale(new Vector3(1.14f, 0.05f, 1.14f)), new Vector3(0f, 0.325f, 0f)),
            SchoolColors.GetBorderColor(school));
    }

    /// <summary>A tall-backed chair facing its local +X. Kind 1 is the honored chair (taller,
    /// with a brass finial), kind 2 the Astrologer's: built as a plain chair, with its own
    /// model id so a model can set it apart.</summary>
    private void Chair(Node3D g, Transform3D at, int kind)
    {
        string id = kind == 1 ? "chair_honored" : kind == 2 ? "chair_astrologer" : "chair";
        if (PlaceModel(g, id, at))
            return;
        float back = kind == 1 ? 2.2f : 1.6f;
        float width = kind == 1 ? 0.8f : 0.66f;
        PieceBox(g, Paint.Wood, true, at, new Vector3(0f, 0.23f, 0f), new Vector3(0.5f, 0.46f, width - 0.12f), Timber);
        PieceBox(g, Paint.Wood, true, at, new Vector3(0f, 0.5f, 0f), new Vector3(0.62f, 0.08f, width), Timber);
        PieceBox(g, Paint.Wood, true, at, new Vector3(-0.29f, 0.54f + back * 0.5f, 0f), new Vector3(0.1f, back, width), Timber);
        if (kind == 1)
            PieceBox(g, Paint.Wood, true, at, new Vector3(-0.29f, 0.54f + back + 0.12f, 0f), new Vector3(0.14f, 0.24f, 0.3f), Brass);
    }

    private void Light(Vector3 position, float energy, float range, Node3D parent = null)
    {
        (parent ?? _hall).AddChild(new OmniLight3D
        {
            Name = "Lamplight",
            Position = position,
            LightColor = LampLight,
            LightEnergy = energy,
            OmniRange = range,
            ShadowEnabled = false,
        });
    }

    // ── The lectern and the sphere ───────────────────────────────────────

    /// <summary>The lectern at the foot of the dais, the Register open on it to a clean page.
    /// The Provost reads from its east side, facing the dais; the candidate's mark is in front
    /// of it, between the lectern and the dais.</summary>
    private void BuildLectern()
    {
        var at = new Transform3D(Basis.Identity, new Vector3(_xStripW - LecternGap, _floorY, _cz));
        if (PlaceModel(_hall, "lectern", at))
            return;
        PieceBox(_hall, Paint.Wood, true, at, new Vector3(0f, 0.06f, 0f), new Vector3(0.6f, 0.12f, 0.6f), Timber);
        PieceBox(_hall, Paint.Wood, true, at, new Vector3(0f, 0.6f, 0f), new Vector3(0.24f, 1.0f, 0.3f), Timber);
        var slope = at * new Transform3D(new Basis(new Vector3(0f, 0f, 1f), -Mathf.DegToRad(18f)), new Vector3(0f, 1.16f, 0f));
        PieceBox(_hall, Paint.Wood, true, slope, Vector3.Zero, new Vector3(0.62f, 0.06f, 0.8f), Timber);
        PieceBox(_hall, Paint.Wood, false, slope, new Vector3(0f, 0.045f, 0f), new Vector3(0.5f, 0.03f, 0.66f), RegisterCover);
        PieceBox(_hall, Paint.Wood, false, slope, new Vector3(0f, 0.07f, 0f), new Vector3(0.46f, 0.03f, 0.6f), Parchment);
    }

    /// <summary>The magisphere in its cradle of old stone at the dais's center: six lights in
    /// one glass, turning slowly.</summary>
    private void BuildMagisphere()
    {
        var at = new Transform3D(Basis.Identity, new Vector3(_xDais, _daisTop, _cz));
        if (PlaceModel(_hall, "magisphere", at))
            return;

        // The cradle's lip stands at a seated archmage's reach (1.0 to 1.3 above the dais):
        // the honored chair's occupant rests a hand behind them on it.
        Add(_hall, Prim.Hex, Paint.Stone, true, at * new Transform3D(Basis.FromScale(new Vector3(2.0f, 1.0f, 2.0f)), new Vector3(0f, 0.5f, 0f)), DaisStone);
        Add(_hall, Prim.Cylinder, Paint.Stone, true, at * new Transform3D(Basis.FromScale(new Vector3(2.8f, 0.3f, 2.8f)), new Vector3(0f, 1.15f, 0f)), TrimStone);

        const float radius = 1.45f;
        Vector3 centre = at.Origin + new Vector3(0f, 1.05f + radius, 0f);
        _hall.AddChild(new MeshInstance3D
        {
            Name = "Magisphere",
            Mesh = new SphereMesh { Radius = radius, Height = 2f * radius, RadialSegments = 32, Rings = 16 },
            MaterialOverride = GlassMaterial(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = centre,
        });

        _orbs = new Node3D { Name = "SixLights", Position = centre };
        _hall.AddChild(_orbs);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = MeshFor(Prim.Sphere),
        };
        mm.InstanceCount = SealSchools.Length;
        for (int i = 0; i < SealSchools.Length; i++)
        {
            float a = Mathf.Tau * i / SealSchools.Length;
            var p = new Vector3(Mathf.Cos(a) * 0.72f, (i % 2 == 0 ? 0.2f : -0.2f), Mathf.Sin(a) * 0.72f);
            mm.SetInstanceTransform(i, new Transform3D(Basis.FromScale(new Vector3(0.5f, 0.5f, 0.5f)), p));
            mm.SetInstanceColor(i, SchoolColors.GetBorderColor(SealSchools[i]));
        }
        _orbs.AddChild(new MultiMeshInstance3D
        {
            Name = "Lights",
            Multimesh = mm,
            MaterialOverride = MaterialFor(Paint.Glow),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Cutaway and motion
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Hides each wall while the camera is outside it, and the ceiling while the
    /// camera is above it, and turns the six lights. A wall's test plane sits CutawayMargin
    /// inside its inner face, so a camera inside the wall's own thickness never sees the backs
    /// of its windows and shutters.</summary>
    public override void _Process(double delta)
    {
        var camera = GetViewport()?.GetCamera3D();
        if (camera != null)
        {
            Vector3 eye = camera.GlobalPosition;
            foreach (var (node, point, inward) in _walls)
            {
                bool outside = (eye - point).Dot(inward) < CutawayMargin;
                if (node.Visible == outside)
                    node.Visible = !outside;
            }
            if (_ceiling != null)
            {
                bool above = eye.Y > _ceilingY - CeilingMargin;
                if (_ceiling.Visible == above)
                    _ceiling.Visible = !above;
            }
        }
        _orbs?.RotateY(OrbSpin * (float)delta);
        TickHearth((float)delta);   // the Grand Hall's fire; nothing in the Ceremony Hall
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pieces, batches and resources
    // ════════════════════════════════════════════════════════════════════════

    private Node3D Group(string name)
    {
        var g = new Node3D { Name = name };
        AddChild(g);
        return g;
    }

    /// <summary>A wall's group, registered for the cutaway: <paramref name="point"/> lies on
    /// its inner face, <paramref name="inward"/> points into the hall.</summary>
    private Node3D Wall(string name, Vector3 point, Vector3 inward)
    {
        var g = Group(name);
        _walls.Add((g, point, inward.Normalized()));
        return g;
    }

    private static Transform3D Scaled(Vector3 centre, Basis rotation, Vector3 size) =>
        new Transform3D(rotation * Basis.FromScale(size), centre);

    private void Box(Node3D g, Paint paint, bool shadow, Vector3 centre, Vector3 size, Color color) =>
        Add(g, Prim.Box, paint, shadow, Scaled(centre, Basis.Identity, size), color);

    /// <summary>A box in a piece's own frame: <paramref name="local"/> is its center there.</summary>
    private void PieceBox(Node3D g, Paint paint, bool shadow, Transform3D piece, Vector3 local, Vector3 size, Color color) =>
        Add(g, Prim.Box, paint, shadow, piece * new Transform3D(Basis.FromScale(size), local), color);

    private void Add(Node3D g, Prim prim, Paint paint, bool shadow, Transform3D xf, Color color)
    {
        var key = (g, prim, paint, shadow);
        if (!_batches.TryGetValue(key, out var list))
        {
            list = new List<(Transform3D, Color)>();
            _batches[key] = list;
        }
        list.Add((xf, color));
    }

    /// <summary>One MultiMesh per group, primitive, paint and shadow setting; the color rides
    /// on each instance (the painterly prism shader and the unshaded paints read it as COLOR).</summary>
    private void Flush()
    {
        foreach (var entry in _batches)
        {
            var (group, prim, paint, shadow) = entry.Key;
            var list = entry.Value;
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = MeshFor(prim),
            };
            mm.InstanceCount = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                mm.SetInstanceTransform(i, list[i].xf);
                mm.SetInstanceColor(i, list[i].color);
            }
            group.AddChild(new MultiMeshInstance3D
            {
                Name = $"{prim}{paint}",
                Multimesh = mm,
                MaterialOverride = MaterialFor(paint),
                CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        _batches.Clear();
    }

    /// <summary>Places a kit piece's model when Assets/Hall has one. False means build the
    /// code version.</summary>
    private bool PlaceModel(Node3D g, string id, Transform3D at)
    {
        var scene = Model(id);
        if (scene == null)
            return false;
        if (scene.Instantiate() is not Node3D node)
        {
            GD.PushWarning($"[Hall] {ModelDir}{id}: the root is not a Node3D; using the code piece.");
            return false;
        }
        node.Transform = at;
        g.AddChild(node);
        return true;
    }

    /// <summary>The model for a piece, looked up once per run: .tscn, then .glb, then .gltf.</summary>
    private static PackedScene Model(string id)
    {
        if (_models.TryGetValue(id, out var cached))
            return cached;
        if (_noModel.Contains(id))
            return null;
        foreach (var ext in new[] { ".tscn", ".glb", ".gltf" })
        {
            string path = ModelDir + id + ext;
            if (!ResourceLoader.Exists(path))
                continue;
            var scene = GD.Load<PackedScene>(path);
            if (scene == null)
                continue;
            _models[id] = scene;
            GD.Print($"[Hall] {id}: using the model at {path}.");
            return scene;
        }
        _noModel.Add(id);
        return null;
    }

    private static Mesh MeshFor(Prim prim)
    {
        switch (prim)
        {
            case Prim.Cylinder:
                return _cylinder ??= new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 1f, RadialSegments = 20, Rings = 1 };
            case Prim.Hex:
                return _hex ??= HexPrism();
            case Prim.Sphere:
                return _sphere ??= new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 16, Rings = 8 };
            case Prim.Dome:
                return _dome ??= new SphereMesh { Radius = 0.5f, Height = 0.5f, IsHemisphere = true, RadialSegments = 20, Rings = 8 };
            case Prim.Quad:
                return _quad ??= new QuadMesh { Size = Vector2.One };
            default:
                return _box ??= new BoxMesh { Size = Vector3.One };
        }
    }

    /// <summary>A unit hexagonal prism with flat faces: corners 0.5 from the axis (one at +Z,
    /// so flat faces look east and west), 1 tall, centered on the origin. Built here because a
    /// six-sided CylinderMesh shades its sides as if round.</summary>
    private static Mesh HexPrism()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var top = new Vector3[6];
        var bottom = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float a = Mathf.Tau * i / 6f;
            top[i] = new Vector3(0.5f * Mathf.Sin(a), 0.5f, 0.5f * Mathf.Cos(a));
            bottom[i] = new Vector3(top[i].X, -0.5f, top[i].Z);
        }
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            var n = new Vector3(top[i].X + top[j].X, 0f, top[i].Z + top[j].Z).Normalized();
            Tri(st, n, bottom[i], top[i], top[j]);
            Tri(st, n, bottom[i], top[j], bottom[j]);
            Tri(st, Vector3.Up, new Vector3(0f, 0.5f, 0f), top[i], top[j]);
            Tri(st, Vector3.Down, new Vector3(0f, -0.5f, 0f), bottom[i], bottom[j]);
        }
        return st.Commit();
    }

    /// <summary>One flat triangle facing <paramref name="n"/>. Godot's front faces wind
    /// clockwise seen from the front, so the order is fixed here whatever the caller passed.</summary>
    private static void Tri(SurfaceTool st, Vector3 n, Vector3 a, Vector3 b, Vector3 c)
    {
        if ((b - a).Cross(c - a).Dot(n) > 0f)
            (b, c) = (c, b);
        st.SetNormal(n);
        st.AddVertex(a);
        st.SetNormal(n);
        st.AddVertex(b);
        st.SetNormal(n);
        st.AddVertex(c);
    }

    /// <summary>Stone, limewash and wood are the painterly prism shader (instance color, banded
    /// light, brush grain) with their own grain; glow is unshaded instance color; a shaft is
    /// unshaded additive light that fades out at its long edges.</summary>
    private static Material MaterialFor(Paint paint)
    {
        switch (paint)
        {
            case Paint.Glow:
                return _glow ??= new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                };
            case Paint.Shaft:
                return _shaft ??= ShaftMaterial();
            case Paint.Wood:
                return _wood ??= Painted(0.11f, 2.6f, 0.05f, 0.12f, 0.3f);
            case Paint.Wash:
                return _wash ??= Painted(0.05f, 0.8f, 0.04f, 0.10f, 0.3f);
            default:
                return _stone ??= Painted(0.10f, 1.1f, 0.10f, 0.14f, 0.5f);
        }
    }

    private static Material Painted(float grain, float grainScale, float stripes, float skirtDarken, float jitter)
    {
        var material = PainterlyPrism.TileMaterial(PainterlyPrism.Land, 0.9f);
        if (material is ShaderMaterial sm)
        {
            sm.SetShaderParameter("grain_strength", grain);
            sm.SetShaderParameter("grain_scale", grainScale);
            sm.SetShaderParameter("toon_bands", 3.0f);
            sm.SetShaderParameter("toon_softness", 0.22f);
            sm.SetShaderParameter("toon_wrap", 0.45f);
            sm.SetShaderParameter("toon_jitter", jitter);
            sm.SetShaderParameter("skirt_darken", skirtDarken);
            sm.SetShaderParameter("stripe_strength", stripes);
            sm.SetShaderParameter("stripe_scale", 1.6f);
        }
        return material;
    }

    private static Material ShaftMaterial()
    {
        var gradient = new Gradient();
        gradient.Offsets = new float[] { 0f, 0.5f, 1f };
        gradient.Colors = new Color[] { new Color(1f, 1f, 1f, 0f), new Color(1f, 1f, 1f, 1f), new Color(1f, 1f, 1f, 0f) };
        return new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = new GradientTexture1D { Gradient = gradient, Width = 64 },
        };
    }

    /// <summary>The sphere's glass: clear enough to show the six lights, with a cool glow and
    /// a rim so it reads as a sphere against the pale dais and walls.</summary>
    private static StandardMaterial3D GlassMaterial() =>
        _glass ??= new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.62f, 0.74f, 0.92f, 0.32f),
            Roughness = 0.08f,
            Metallic = 0.1f,
            EmissionEnabled = true,
            Emission = new Color(0.36f, 0.46f, 0.64f),
            EmissionEnergyMultiplier = 0.45f,
            RimEnabled = true,
            Rim = 0.8f,
            RimTint = 0.5f,
        };
}
