using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HallSet.GrandHall.cs  (partial of HallSet)
//
// Purpose:        The Grand Hall, built around the combat board for the
//                 declaration exams (E3, ruled 2026-10-26: each school's
//                 practical is sat in a Grand Hall set). The second look
//                 of HallSet; a recipe names it with interior set
//                 "grand_hall" (Data/Maps/bf_grand_hall.json, and
//                 bf_grand_hall_frost.json for the Frost Plate).
//
//                 Canon (Data/Buildings/grand_hall.json): "the one room
//                 the Stillness never reached all the way into. The
//                 guild's name is cut over the door; the roll of every
//                 cycle you have spent is kept inside." Tier 2 "doubles
//                 its long tables"; tier 3 brings "banners, a dais, and
//                 room for every voice". Each charter the hall holds
//                 hangs framed on the west wall.
//
//                 So it is a guildhall, warm where the Ceremony Hall is
//                 lean: an oak floor with the examination floor laid into
//                 it in a timber frame, a timber wainscot under ochre
//                 plaster, a hammerbeam roof, long tables down both sides
//                 on trestles, a hearth in the north wall that still
//                 burns, and tall mullioned windows in the south wall
//                 whose light falls on the tables with the dust in it
//                 stilled. The west wall, which the camera faces in an
//                 exam, holds the Roll of Cycles (a line for every cycle
//                 spent), the charters above it, and the lectern with the
//                 Register in front of it. The east doors carry the
//                 guild's name.
//
//                 Built from the same pieces, batches, paints and cutaway
//                 as the Ceremony Hall (HallSet.cs). The long table is a
//                 swappable piece (GrandHallPieceGuide); the bench, chair,
//                 lectern and lamp-stand are the Ceremony Hall's.
// Layer:          Combat / presentation
// Collaborators:  HallSet.cs (Build dispatches here when Look is
//                 GrandHallSetId; _Process ticks the hearth),
//                 HexGridManager.Interior.cs (sets Look), SaveManager
//                 (the hall's tier, the guild's name, the Roll),
//                 Charters (slots and charters held), DeclarationService
//                 (which banners are dyed), SchoolColors
// ============================================================

public partial class HallSet
{
    /// <summary>The interior id a recipe names to get the Grand Hall.</summary>
    public const string GrandHallSetId = "grand_hall";

    /// <summary>Which set Build makes: SetId (the Ceremony Hall) or GrandHallSetId. Set
    /// before Build.</summary>
    public string Look = SetId;

    /// <summary>The Grand Hall's swappable piece, as PieceGuide lists the Ceremony Hall's.</summary>
    public const string GrandHallPieceGuide =
        "long_table: 4.0 long (X) x 1.0 wide, top at 0.76, origin on the floor at its center; stretched along X to fit.";

    // ── Layout, meters ───────────────────────────────────────────────────
    private const float GhFrame = 0.45f;            // the timber frame round the examination floor
    private const float GhAisle = 1.3f;             // the floor's long edges to the first bench
    private const float GhBenchGap = 0.3f;          // a bench to its table's edge
    private const float GhTableWidth = 1.0f;
    private const float GhTableTop = 0.76f;         // the table top's surface
    private const float GhTableSlot = 4.7f;         // a table and the gap after it, about
    private const float GhTableGap = 0.6f;
    private const float GhTableModelLength = 4.0f;  // long_table's authored length
    private const float GhTableOverhang = 1.0f;     // the tables run this far past the floor's ends
    private const float GhRowPitch = 3.0f;          // the first row's table to the second's, from tier 2
    private const float GhRowDepth = 2f * BenchDepth + 2f * GhBenchGap + GhTableWidth;
    private const float GhWalk = 1.6f;              // the walk along each long wall
    private const float GhEndWest = 5.0f;           // the floor's west end to the west wall
    private const float GhEndEast = 5.0f;           // the floor's east end to the east doors
    private const float GhWallPlate = 11.0f;        // the walls' height: the roof stands on it
    private const float GhWainscot = 3.2f;
    private const float GhBay = 6.0f;               // the trusses' spacing, about
    private const float GhWindowSill = 3.8f;
    private const float GhWindowWidth = 2.0f;
    private const float GhWindowHeight = 6.0f;
    private const float GhTransom = 4.0f;           // above the sill
    private const float GhEndWindowOff = 8.5f;      // the end walls' windows, either side of the middle
    private const float GhShaftPitch = 65f;         // degrees below level: the south light lands on the tables
    private const float GhShaftLength = 9.5f;
    private const int GhMotesPerShaft = 12;
    private const float GhMoteSize = 0.05f;
    private const float GhHearthWidth = 3.6f;
    private const float GhHearthDepth = 0.9f;       // the chimney breast, out from the north wall
    private const float GhHearthOpening = 2.0f;
    private const float GhHearthOpeningHigh = 1.5f;
    private const float GhHearthRecess = 0.5f;
    private const float GhHearthClear = 2.65f;      // the north tables stop this far either side of it
    private const float GhLecternGap = 1.4f;        // the floor's west end to the lectern
    private const float GhDaisRise = 0.45f;         // tier 3
    private const float GhDaisDepth = 3.0f;
    private const float GhDaisGap = 0.9f;           // the floor's west end to the dais's front
    private const float GhDaisHalfWidth = 6.0f;
    private const float GhDoorLeaf = 2.1f;
    private const float GhDoorHeight = 5.0f;
    private const float GhBannerWidth = 0.9f;
    private const float GhBannerLength = 3.6f;
    private const float GhBannerTop = 8.0f;         // under the trusses' braces
    private const float GhRoofPitch = 28f;
    private const float GhHammer = 1.6f;            // the hammer beams' reach from the wall
    private const int GhGableSteps = 12;
    private const float GhRollBottom = 2.0f;
    private const float GhRollHeight = 3.4f;
    private const float GhRollWidth = 2.2f;
    private const float GhRollPitch = 0.09f;        // line to line on the Roll
    private const int GhRollCap = 30;               // lines the Roll has room for
    private const float GhCharterY = 6.4f;          // the charters' centers, over the Roll
    private const float GhCharterPitch = 1.5f;
    private const int GhNameCap = 22;               // letters the door's lintel has room for
    private const float GhPlank = 0.3f;

    // ── Colors ───────────────────────────────────────────────────────────
    private static readonly Color GhOak = new(0.46f, 0.33f, 0.21f);
    private static readonly Color GhOakAlt = new(0.42f, 0.30f, 0.19f);
    private static readonly Color GhFrameWood = new(0.22f, 0.15f, 0.10f);
    private static readonly Color GhWainscotWood = new(0.34f, 0.23f, 0.14f);
    private static readonly Color GhOchre = new(0.80f, 0.64f, 0.42f);
    private static readonly Color GhTableWood = new(0.38f, 0.26f, 0.16f);
    private static readonly Color GhDaisWood = new(0.30f, 0.21f, 0.13f);
    private static readonly Color GhRoofBoard = new(0.20f, 0.14f, 0.09f);
    private static readonly Color GhSoot = new(0.07f, 0.06f, 0.05f);
    private static readonly Color GhEmber = new(1.0f, 0.42f, 0.12f);
    private static readonly Color GhFlame = new(1.0f, 0.72f, 0.30f);
    private static readonly Color GhHearthLight = new(1.0f, 0.62f, 0.32f);
    private static readonly Color GhInk = new(0.16f, 0.12f, 0.10f);
    private static readonly Color GhMote = new(1.0f, 0.96f, 0.84f);
    private static readonly Color GhWax = new(0.92f, 0.88f, 0.78f);

    // ── State ────────────────────────────────────────────────────────────
    private OmniLight3D _hearthLight;
    private float _hearthBase;
    private float _hearthTime;

    // ════════════════════════════════════════════════════════════════════════
    //  Build
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The Grand Hall around a board, as Build is for the Ceremony Hall (same arguments).
    /// The hall dresses itself from the loaded save: its tier, its charters, the Roll and the
    /// guild's name. With no save it stands at tier 1, bare.</summary>
    private void BuildGrandHall(Vector3 boardMin, Vector3 boardMax, float floorY)
    {
        var save = SaveManager.ActiveSave;
        int tier = GrandHallTier(save);
        int rows = tier >= 2 ? 2 : 1;

        _floorY = floorY;
        _cz = (boardMin.Z + boardMax.Z) * 0.5f;
        _hw = (boardMax.Z - boardMin.Z) * 0.5f;
        _xStripW = boardMin.X;
        _xStripE = boardMax.X;
        float wallOff = _hw + GhAisle + GhRowPitch + GhRowDepth + GhWalk;   // room for two rows at every tier
        _zWallN = _cz + wallOff;
        _zWallS = _cz - wallOff;
        _xWallW = _xStripW - GhEndWest;
        _xWallE = _xStripE + GhEndEast;
        _ceilingY = _floorY + GhWallPlate;

        float len = _xWallE - _xWallW;
        int bays = Mathf.Max(3, Mathf.RoundToInt(len / GhBay));
        if (bays % 2 == 0)
            bays++;   // an odd count, so a bay stands in the middle for the hearth
        float bay = len / bays;

        _hall = Group("Hall");
        GhFloor();
        GhTables(rows);
        GhLongWall(Wall("WallNorth", new Vector3(0f, 0f, _zWallN), new Vector3(0f, 0f, -1f)), _zWallN, 1f, bays, bay, tier, save);
        GhLongWall(Wall("WallSouth", new Vector3(0f, 0f, _zWallS), new Vector3(0f, 0f, 1f)), _zWallS, -1f, bays, bay, tier, save);
        GhEastWall(Wall("WallEast", new Vector3(_xWallE, 0f, 0f), new Vector3(-1f, 0f, 0f)), save);
        int rollLines = GhWestWall(Wall("WallWest", new Vector3(_xWallW, 0f, 0f), new Vector3(1f, 0f, 0f)), save, out int charters, out int held);
        GhRoof(bays, bay);
        GhLecternAndDais(tier);
        GhLamps(bays, bay);
        Flush();

        GD.Print($"[Hall] The Grand Hall, tier {tier}: {len:0.0} x {_zWallN - _zWallS:0.0} m around a "
                 + $"{_xStripE - _xStripW:0.0} x {2f * _hw:0.0} m examination floor, floor at {_floorY:0.00}; "
                 + $"{rows} row(s) of tables a side, {held} of {charters} charter(s) held, {rollLines} line(s) on the Roll.");
    }

    /// <summary>The Grand Hall's tier in this save (1 to 3), or 1 when the save has none.</summary>
    private static int GrandHallTier(GuildSaveData save)
    {
        var list = save?.Ledger?.Buildings;
        if (list != null)
            foreach (var b in list)
                if (b != null && b.Id == Charters.GrandHallId && b.Tier > 0)
                    return Mathf.Clamp(b.Tier, 1, 3);
        return 1;
    }

    // ── The floor ────────────────────────────────────────────────────────

    /// <summary>Oak boards the length of the hall, every other one a shade darker, and the
    /// examination floor laid into them in a dark timber frame with a brass line outside it. All
    /// of it lies under the tile tops (HexGridManager.Interior lays the board 4 cm proud).</summary>
    private void GhFloor()
    {
        float len = _xWallE - _xWallW;
        float wid = _zWallN - _zWallS;
        float xMid = (_xWallW + _xWallE) * 0.5f;
        Box(_hall, Paint.Wood, false, new Vector3(xMid, _floorY - 0.25f, _cz),
            new Vector3(len + 2f * WallThick, 0.5f, wid + 2f * WallThick), GhOak);
        int boards = Mathf.CeilToInt(wid / GhPlank);
        for (int i = 0; i < boards; i += 2)
            Box(_hall, Paint.Wood, false, new Vector3(xMid, _floorY + 0.003f, _zWallS + GhPlank * (i + 0.5f)),
                new Vector3(len, 0.006f, GhPlank), GhOakAlt);

        float xc = (_xStripW + _xStripE) * 0.5f;
        float outerLen = _xStripE - _xStripW + 2f * GhFrame;
        Box(_hall, Paint.Wood, false, new Vector3(xc, _floorY + 0.009f, _cz),
            new Vector3(outerLen, 0.006f, 2f * (_hw + GhFrame)), GhFrameWood);
        foreach (float k in new[] { -1f, 1f })
        {
            Box(_hall, Paint.Wood, false, new Vector3(xc, _floorY + 0.012f, _cz + k * (_hw + GhFrame + 0.035f)),
                new Vector3(outerLen + 0.14f, 0.012f, 0.07f), Brass);
            float xLine = k < 0f ? _xStripW - GhFrame - 0.035f : _xStripE + GhFrame + 0.035f;
            Box(_hall, Paint.Wood, false, new Vector3(xLine, _floorY + 0.012f, _cz),
                new Vector3(0.07f, 0.012f, 2f * (_hw + GhFrame)), Brass);
        }
    }

    // ── The long tables ──────────────────────────────────────────────────

    /// <summary>The long tables down both sides of the floor, each on two trestles with a bench
    /// either side: one row a side, two from tier 2 ("the hall doubles its long tables"). The
    /// north rows leave the hearth clear.</summary>
    private void GhTables(int rows)
    {
        float x0 = _xStripW - GhTableOverhang;
        float span = _xStripE + GhTableOverhang - x0;
        int slots = Mathf.Max(1, Mathf.RoundToInt(span / GhTableSlot));
        float slot = span / slots;
        float table = slot - GhTableGap;
        float xMid = (_xWallW + _xWallE) * 0.5f;
        float benchOff = GhTableWidth * 0.5f + GhBenchGap + BenchDepth * 0.5f;

        foreach (float side in new[] { -1f, 1f })
        {
            for (int r = 0; r < rows; r++)
            {
                float zTable = _cz + side * (_hw + GhAisle + BenchDepth + GhBenchGap + GhTableWidth * 0.5f + r * GhRowPitch);
                for (int i = 0; i < slots; i++)
                {
                    float xc = x0 + slot * (i + 0.5f);
                    if (side > 0f && xc + table * 0.5f > xMid - GhHearthClear + 0.01f
                        && xc - table * 0.5f < xMid + GhHearthClear - 0.01f)
                        continue;
                    LongTable(new Vector3(xc, _floorY, zTable), table);
                    foreach (float b in new[] { -1f, 1f })
                        BenchSegment(_hall, new Vector3(xc, _floorY, zTable + b * benchOff), table - 0.3f);
                }
            }
        }
    }

    /// <summary>A long table on two trestles and a stretcher, its top GhTableWidth across.</summary>
    private void LongTable(Vector3 at, float length)
    {
        if (PlaceModel(_hall, "long_table", new Transform3D(Basis.FromScale(new Vector3(length / GhTableModelLength, 1f, 1f)), at)))
            return;
        var xf = new Transform3D(Basis.Identity, at);
        PieceBox(_hall, Paint.Wood, true, xf, new Vector3(0f, GhTableTop - 0.035f, 0f), new Vector3(length, 0.07f, GhTableWidth), GhTableWood);
        float legX = length * 0.5f - 0.45f;
        float legH = GhTableTop - 0.07f;
        foreach (float sx in new[] { -1f, 1f })
        {
            PieceBox(_hall, Paint.Wood, true, xf, new Vector3(sx * legX, legH * 0.5f, 0f), new Vector3(0.09f, legH, GhTableWidth - 0.2f), Timber);
            PieceBox(_hall, Paint.Wood, true, xf, new Vector3(sx * legX, 0.04f, 0f), new Vector3(0.16f, 0.08f, GhTableWidth - 0.1f), Timber);
        }
        PieceBox(_hall, Paint.Wood, true, xf, new Vector3(0f, 0.32f, 0f), new Vector3(2f * legX, 0.08f, 0.08f), Timber);
    }

    // ── The long walls ───────────────────────────────────────────────────

    /// <summary>A north or south wall: ochre plaster over a timber wainscot, the wall plate, a post
    /// under each truss, and a tall mullioned window in each bay. The north wall has the hearth
    /// in its middle bay; the south wall's windows let the light in onto the tables. Tier 3 hangs
    /// four of the eight banners on each. <paramref name="side"/> is +1 for north (+Z).</summary>
    private void GhLongWall(Node3D g, float face, float side, int bays, float bay, int tier, GuildSaveData save)
    {
        float len = _xWallE - _xWallW;
        float xMid = (_xWallW + _xWallE) * 0.5f;
        Box(g, Paint.Wash, false, new Vector3(xMid, _floorY + GhWallPlate * 0.5f, face + side * WallThick * 0.5f),
            new Vector3(len + 2f * WallThick, GhWallPlate, WallThick), GhOchre);
        Box(g, Paint.Wood, false, new Vector3(xMid, _floorY + GhWainscot * 0.5f, face - side * 0.03f),
            new Vector3(len, GhWainscot, 0.06f), GhWainscotWood);
        Box(g, Paint.Wood, false, new Vector3(xMid, _floorY + GhWainscot + 0.05f, face - side * 0.06f),
            new Vector3(len, 0.1f, 0.12f), Timber);
        Box(g, Paint.Wood, false, new Vector3(xMid, _ceilingY - 0.15f, face - side * 0.12f),
            new Vector3(len, 0.3f, 0.24f), Timber);
        for (int k = 1; k < bays; k++)
            Box(g, Paint.Wood, false, new Vector3(_xWallW + bay * k, _floorY + GhWallPlate * 0.5f, face - side * 0.11f),
                new Vector3(0.36f, GhWallPlate, 0.22f), Timber);

        bool north = side > 0f;
        int middle = bays / 2;
        for (int i = 0; i < bays; i++)
        {
            float x = _xWallW + bay * (i + 0.5f);
            if (north && i == middle)
                Hearth(g, x, face, side);
            else
                GhWindow(g, x, face, side, !north, i);
        }
        if (tier >= 3)
            Banners(g, face, side, bays, bay, save);
    }

    /// <summary>A tall window on a long wall: the light, a timber surround, a stone sill, an iron
    /// mullion and transom, and (on the south wall) a shaft of daylight falling onto the tables,
    /// with the dust in it stilled.</summary>
    private void GhWindow(Node3D g, float x, float face, float side, bool shaft, int index)
    {
        float sill = _floorY + GhWindowSill;
        float w = GhWindowWidth;
        float h = GhWindowHeight;
        float zIn = face - side * 0.05f;
        Box(g, Paint.Glow, false, new Vector3(x, sill + h * 0.5f, face - side * 0.02f), new Vector3(w, h, 0.04f), WindowLight);
        foreach (float k in new[] { -1f, 1f })
            Box(g, Paint.Wood, false, new Vector3(x + k * (w * 0.5f + 0.09f), sill + h * 0.5f, zIn), new Vector3(0.18f, h + 0.36f, 0.1f), Timber);
        Box(g, Paint.Wood, false, new Vector3(x, sill + h + 0.09f, zIn), new Vector3(w + 0.36f, 0.18f, 0.1f), Timber);
        Box(g, Paint.Stone, false, new Vector3(x, sill - 0.08f, face - side * 0.14f), new Vector3(w + 0.5f, 0.16f, 0.28f), TrimStone);
        Box(g, Paint.Wood, false, new Vector3(x, sill + h * 0.5f, zIn - side * 0.02f), new Vector3(0.08f, h, 0.08f), Iron);
        Box(g, Paint.Wood, false, new Vector3(x, sill + GhTransom, zIn - side * 0.02f), new Vector3(w, 0.08f, 0.08f), Iron);
        if (!shaft)
            return;
        float pitch = Mathf.DegToRad(GhShaftPitch);
        var along = new Vector3(0f, -Mathf.Sin(pitch), -side * Mathf.Cos(pitch));
        var start = new Vector3(x, sill + h * 0.5f, face - side * 0.1f);
        Shaft(g, start, along, GhShaftLength, w * 0.9f, new Vector3(1f, 0f, 0f));
        StilledMotes(g, start, along, w * 0.9f, index);
    }

    /// <summary>Dust motes in a shaft of window light, hanging where they are: the Stillness
    /// reached this far into the hall and no further. Placed by a fixed sequence per window, so
    /// the hall is the same every time it is built.</summary>
    private void StilledMotes(Node3D g, Vector3 start, Vector3 along, float width, int seed)
    {
        Vector3 s = along.Normalized();
        Vector3 a1 = Vector3.Right;
        Vector3 a2 = a1.Cross(s).Normalized();
        float reach = (start.Y - (_floorY + 1.0f)) / Mathf.Max(0.1f, -s.Y);   // a meter clear of the floor
        uint state = 2654435761u * (uint)(seed + 1);
        float Next()
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f;
        }
        var size = new Vector3(GhMoteSize, GhMoteSize, GhMoteSize);
        for (int i = 0; i < GhMotesPerShaft; i++)
        {
            float t = Mathf.Lerp(0.6f, reach, Next());
            float u = (Next() - 0.5f) * width * 0.7f;
            float v = (Next() - 0.5f) * width * 0.5f;
            Add(g, Prim.Sphere, Paint.Glow, false, Scaled(start + s * t + a1 * u + a2 * v, Basis.Identity, size), GhMote);
        }
    }

    /// <summary>The hearth in the north wall's middle bay, still burning: a stone chimney breast
    /// with the fire's opening in it, a sooted back, a stone lintel, a timber mantel, the
    /// hearthstone, and the fire itself (an ember bed, two logs, three flames) with a warm light
    /// that flickers (TickHearth).</summary>
    private void Hearth(Node3D g, float x, float face, float side)
    {
        float zFront = face - side * GhHearthDepth;
        float halfOpen = GhHearthOpening * 0.5f;
        float jamb = (GhHearthWidth - GhHearthOpening) * 0.5f;
        float zBreast = face - side * GhHearthDepth * 0.5f;
        foreach (float k in new[] { -1f, 1f })
            Box(g, Paint.Stone, false, new Vector3(x + k * (halfOpen + jamb * 0.5f), _floorY + GhWallPlate * 0.5f, zBreast),
                new Vector3(jamb, GhWallPlate, GhHearthDepth), PierStone);
        Box(g, Paint.Stone, false, new Vector3(x, _floorY + (GhWallPlate + GhHearthOpeningHigh) * 0.5f, zBreast),
            new Vector3(GhHearthOpening, GhWallPlate - GhHearthOpeningHigh, GhHearthDepth), PierStone);
        float backDepth = GhHearthDepth - GhHearthRecess;
        Box(g, Paint.Stone, false, new Vector3(x, _floorY + GhHearthOpeningHigh * 0.5f, face - side * backDepth * 0.5f),
            new Vector3(GhHearthOpening, GhHearthOpeningHigh, backDepth), GhSoot);
        Box(g, Paint.Stone, false, new Vector3(x, _floorY + GhHearthOpeningHigh + 0.1f, zFront - side * 0.04f),
            new Vector3(GhHearthOpening + 0.5f, 0.2f, 0.08f), TrimStone);
        Box(g, Paint.Wood, false, new Vector3(x, _floorY + 1.95f, zFront - side * 0.18f), new Vector3(GhHearthWidth + 0.3f, 0.12f, 0.36f), Timber);
        Box(g, Paint.Stone, false, new Vector3(x, _floorY + 0.02f, zFront - side * 0.6f), new Vector3(GhHearthWidth - 0.6f, 0.04f, 1.2f), PlinthStone);

        float zFire = face - side * (backDepth + GhHearthRecess * 0.5f);
        Box(g, Paint.Glow, false, new Vector3(x, _floorY + 0.06f, zFire), new Vector3(1.4f, 0.1f, 0.42f), GhEmber);
        var logAxis = new Basis(new Vector3(0f, 0f, 1f), Mathf.Pi * 0.5f);   // a cylinder laid along X
        foreach (float k in new[] { -1f, 1f })
            Add(g, Prim.Cylinder, Paint.Wood, false, Scaled(new Vector3(x, _floorY + 0.17f, zFire + k * 0.09f),
                new Basis(Vector3.Up, k * 0.18f) * logAxis, new Vector3(0.16f, 1.2f, 0.16f)), GhFrameWood);
        float[] heights = { 0.42f, 0.6f, 0.36f };
        for (int i = 0; i < heights.Length; i++)
            Box(g, Paint.Glow, false, new Vector3(x + (i - 1) * 0.32f, _floorY + 0.22f + heights[i] * 0.5f, zFire),
                new Vector3(0.22f, heights[i], 0.2f), GhFlame);

        _hearthBase = 1.7f;
        _hearthLight = GhLight(new Vector3(x, _floorY + 0.9f, zFront - side * 1.0f), _hearthBase, 11f, GhHearthLight);
    }

    /// <summary>The hearth's light flickers; nothing else in the hall moves. From _Process.</summary>
    private void TickHearth(float delta)
    {
        if (_hearthLight == null)
            return;
        _hearthTime += delta;
        float t = _hearthTime;
        float flicker = 0.08f * Mathf.Sin(7.1f * t) + 0.05f * Mathf.Sin(13.3f * t + 1.7f) + 0.03f * Mathf.Sin(23.9f * t + 0.4f);
        _hearthLight.LightEnergy = _hearthBase * (1f + flicker);
    }

    /// <summary>Tier 3's banners: the eight schools, four on each long wall, hung on the posts away
    /// from the middle. A declared school's banner is dyed its color; the rest are undyed, with a
    /// stripe of their color, waiting.</summary>
    private void Banners(Node3D g, float face, float side, int bays, float bay, GuildSaveData save)
    {
        var posts = new SortedSet<int>();
        foreach (int k in new[] { 1, 2, bays - 2, bays - 1 })
            if (k >= 1 && k <= bays - 1)
                posts.Add(k);
        var schools = (CardSchool[])Enum.GetValues(typeof(CardSchool));
        int first = side > 0f ? 0 : 4;   // the north wall hangs the first four schools, the south the rest
        int n = 0;
        foreach (int k in posts)
        {
            if (n >= 4 || first + n >= schools.Length)
                break;
            var school = schools[first + n];
            n++;
            Banner(g, _xWallW + bay * k, face, side, school, DeclarationService.IsDeclared(save, school.ToString()));
        }
    }

    private void Banner(Node3D g, float x, float face, float side, CardSchool school, bool declared)
    {
        float top = _floorY + GhBannerTop;
        float z = face - side * 0.32f;
        Color hem = SchoolColors.GetDarkColor(school);
        Box(g, Paint.Wood, false, new Vector3(x, top + 0.05f, z), new Vector3(GhBannerWidth + 0.3f, 0.06f, 0.06f), Iron);
        Box(g, Paint.Wash, false, new Vector3(x, top - GhBannerLength * 0.5f, z), new Vector3(GhBannerWidth, GhBannerLength, 0.03f),
            declared ? SchoolColors.GetBorderColor(school) : Linen);
        Box(g, Paint.Wash, false, new Vector3(x, top - GhBannerLength + 0.15f, z - side * 0.02f), new Vector3(GhBannerWidth, 0.3f, 0.02f), hem);
        if (!declared)
            Box(g, Paint.Wash, false, new Vector3(x, top - GhBannerLength * 0.5f, z - side * 0.02f),
                new Vector3(0.12f, GhBannerLength - 0.6f, 0.02f), hem);
    }

    // ── The end walls ────────────────────────────────────────────────────

    /// <summary>The east wall: the doors (two leaves strapped in iron, shut, in a stone frame), the
    /// guild's name cut over them, and a tall window either side.</summary>
    private void GhEastWall(Node3D g, GuildSaveData save)
    {
        float wid = _zWallN - _zWallS;
        Box(g, Paint.Wash, false, new Vector3(_xWallE + WallThick * 0.5f, _floorY + GhWallPlate * 0.5f, _cz),
            new Vector3(WallThick, GhWallPlate, wid + 2f * WallThick), GhOchre);
        float doorHalf = GhDoorLeaf + 0.4f;   // both leaves' half, and a jamb
        float run = wid * 0.5f - doorHalf;
        foreach (float k in new[] { -1f, 1f })
        {
            float zRun = _cz + k * (doorHalf + run * 0.5f);
            Box(g, Paint.Wood, false, new Vector3(_xWallE - 0.03f, _floorY + GhWainscot * 0.5f, zRun), new Vector3(0.06f, GhWainscot, run), GhWainscotWood);
            Box(g, Paint.Wood, false, new Vector3(_xWallE - 0.06f, _floorY + GhWainscot + 0.05f, zRun), new Vector3(0.12f, 0.1f, run), Timber);
        }
        Box(g, Paint.Wood, false, new Vector3(_xWallE - 0.12f, _ceilingY - 0.15f, _cz), new Vector3(0.24f, 0.3f, wid), Timber);

        foreach (float k in new[] { -1f, 1f })
        {
            float z = _cz + k * GhDoorLeaf * 0.5f;
            Box(g, Paint.Wood, false, new Vector3(_xWallE - 0.15f, _floorY + GhDoorHeight * 0.5f, z),
                new Vector3(0.25f, GhDoorHeight, GhDoorLeaf - 0.04f), DoorWood);
            foreach (float y in new[] { 1.0f, 2.5f, 4.0f })
                Box(g, Paint.Wood, false, new Vector3(_xWallE - 0.3f, _floorY + y, z), new Vector3(0.05f, 0.16f, GhDoorLeaf - 0.2f), Iron);
            Box(g, Paint.Stone, false, new Vector3(_xWallE - 0.2f, _floorY + (GhDoorHeight + 0.3f) * 0.5f, _cz + k * (GhDoorLeaf + 0.2f)),
                new Vector3(0.4f, GhDoorHeight + 0.3f, 0.4f), TrimStone);
        }
        float lintelY = _floorY + GhDoorHeight + 0.75f;
        Box(g, Paint.Stone, false, new Vector3(_xWallE - 0.2f, lintelY, _cz), new Vector3(0.4f, 0.9f, 2f * GhDoorLeaf + 1.2f), TrimStone);
        GuildName(g, lintelY, save);

        foreach (float k in new[] { -1f, 1f })
            GhEndWindow(g, _xWallE, -1f, _cz + k * GhEndWindowOff);
    }

    /// <summary>"The guild's name is cut over the door": a raised gilt block for each letter on the
    /// lintel's face, read from inside the hall (south to north, the reader facing east).</summary>
    private void GuildName(Node3D g, float y, GuildSaveData save)
    {
        string name = (save?.Ledger?.GuildName ?? "").Trim();
        if (name.Length == 0)
            return;
        if (name.Length > GhNameCap)
            name = name.Substring(0, GhNameCap).TrimEnd();
        const float pitch = 0.22f;
        float z0 = _cz - (name.Length - 1) * pitch * 0.5f;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsWhiteSpace(c))
                continue;
            float w = 0.1f + 0.03f * (c % 3);
            float h = char.IsUpper(c) ? 0.42f : 0.32f;
            Box(g, Paint.Wood, false, new Vector3(_xWallE - 0.42f, y - 0.2f + h * 0.5f, z0 + i * pitch), new Vector3(0.04f, h, w), Brass);
        }
    }

    /// <summary>A tall window on an east or west wall (one facing along X): the light, a timber
    /// surround, a stone sill, an iron mullion and transom. <paramref name="inward"/> is the X
    /// direction into the hall. No shaft: the sun is on the south side.</summary>
    private void GhEndWindow(Node3D g, float faceX, float inward, float z)
    {
        float sill = _floorY + GhWindowSill;
        float w = GhWindowWidth;
        float h = GhWindowHeight;
        float xIn = faceX + inward * 0.05f;
        Box(g, Paint.Glow, false, new Vector3(faceX + inward * 0.02f, sill + h * 0.5f, z), new Vector3(0.04f, h, w), WindowLight);
        foreach (float k in new[] { -1f, 1f })
            Box(g, Paint.Wood, false, new Vector3(xIn, sill + h * 0.5f, z + k * (w * 0.5f + 0.09f)), new Vector3(0.1f, h + 0.36f, 0.18f), Timber);
        Box(g, Paint.Wood, false, new Vector3(xIn, sill + h + 0.09f, z), new Vector3(0.1f, 0.18f, w + 0.36f), Timber);
        Box(g, Paint.Stone, false, new Vector3(faceX + inward * 0.14f, sill - 0.08f, z), new Vector3(0.28f, 0.16f, w + 0.5f), TrimStone);
        Box(g, Paint.Wood, false, new Vector3(xIn + inward * 0.02f, sill + h * 0.5f, z), new Vector3(0.08f, h, 0.08f), Iron);
        Box(g, Paint.Wood, false, new Vector3(xIn + inward * 0.02f, sill + GhTransom, z), new Vector3(0.08f, 0.08f, w), Iron);
    }

    /// <summary>The west wall, which the camera faces in an exam: the Roll of Cycles in the middle,
    /// the charters the hall holds framed over it, and a tall window either side. Returns the
    /// lines written on the Roll; <paramref name="charters"/> is the frames hung and
    /// <paramref name="held"/> the ones with a charter in them.</summary>
    private int GhWestWall(Node3D g, GuildSaveData save, out int charters, out int held)
    {
        float wid = _zWallN - _zWallS;
        Box(g, Paint.Wash, false, new Vector3(_xWallW - WallThick * 0.5f, _floorY + GhWallPlate * 0.5f, _cz),
            new Vector3(WallThick, GhWallPlate, wid + 2f * WallThick), GhOchre);
        Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.03f, _floorY + GhWainscot * 0.5f, _cz), new Vector3(0.06f, GhWainscot, wid), GhWainscotWood);
        Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.06f, _floorY + GhWainscot + 0.05f, _cz), new Vector3(0.12f, 0.1f, wid), Timber);
        Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.12f, _ceilingY - 0.15f, _cz), new Vector3(0.24f, 0.3f, wid), Timber);
        foreach (float k in new[] { -1f, 1f })
            GhEndWindow(g, _xWallW, 1f, _cz + k * GhEndWindowOff);
        int lines = RollOfCycles(g, save);
        CharterFrames(g, save, out charters, out held);
        return lines;
    }

    /// <summary>"The roll of every cycle you have spent is kept inside": a parchment roll in a
    /// timber frame, headed, with a line of ink for every cycle in the ledger's history (up to
    /// GhRollCap), each opening with a mark in the color of the school it was spent in.
    /// Read from inside the hall (north to south, the reader facing west).</summary>
    private int RollOfCycles(Node3D g, GuildSaveData save)
    {
        float yMid = _floorY + GhRollBottom + GhRollHeight * 0.5f;
        float top = _floorY + GhRollBottom + GhRollHeight;
        Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.1f, yMid, _cz), new Vector3(0.08f, GhRollHeight, GhRollWidth), Timber);
        Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.15f, yMid, _cz), new Vector3(0.02f, GhRollHeight - 0.3f, GhRollWidth - 0.3f), Parchment);
        float xInk = _xWallW + 0.165f;
        Box(g, Paint.Wood, false, new Vector3(xInk, top - 0.3f, _cz), new Vector3(0.01f, 0.06f, 1.2f), GhInk);

        var history = save?.Ledger?.LoopHistory;
        int lines = Mathf.Min(history?.Count ?? 0, GhRollCap);
        float zLeft = _cz + (GhRollWidth - 0.3f) * 0.5f - 0.12f;   // the reader's left, facing west
        for (int i = 0; i < lines; i++)
        {
            var record = history[i];
            float y = top - 0.48f - i * GhRollPitch;
            int hash = Math.Abs((record?.CycleNumber ?? i) * 31 + (record?.School?.Length ?? 0) * 7 + i * 13);
            float length = 0.9f + (hash % 7) * 0.1f;
            Color mark = Enum.TryParse(record?.School ?? "", true, out CardSchool school)
                ? SchoolColors.GetBorderColor(school)
                : GhInk;
            Box(g, Paint.Wood, false, new Vector3(xInk, y, zLeft - 0.04f), new Vector3(0.012f, 0.06f, 0.06f), mark);
            Box(g, Paint.Wood, false, new Vector3(xInk, y, zLeft - 0.12f - length * 0.5f), new Vector3(0.01f, 0.03f, length), GhInk);
        }
        return lines;
    }

    /// <summary>The charters: a frame for each seat the hall holds (Charters.Slots: one a tier,
    /// one fewer when the hall is blighted), centered over the Roll. A seat with a charter in it
    /// shows the doctrine's page under a wax seal; an empty one shows the bare backing.</summary>
    private void CharterFrames(Node3D g, GuildSaveData save, out int charters, out int held)
    {
        charters = save != null ? Mathf.Clamp(Charters.Slots(save), 0, 3) : 0;
        held = save != null ? Mathf.Clamp(Charters.Used(save), 0, charters) : 0;
        float y = _floorY + GhCharterY;
        var sealAxis = new Basis(new Vector3(0f, 0f, 1f), Mathf.Pi * 0.5f);   // a disc facing along X
        for (int i = 0; i < charters; i++)
        {
            float z = _cz + (i - (charters - 1) * 0.5f) * GhCharterPitch;
            Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.1f, y, z), new Vector3(0.06f, 1.3f, 1.0f), Timber);
            if (i >= held)
            {
                Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.135f, y, z), new Vector3(0.01f, 1.1f, 0.8f), GhWainscotWood);
                continue;
            }
            Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.14f, y, z), new Vector3(0.02f, 1.1f, 0.8f), Parchment);
            for (int l = 0; l < 4; l++)
                Box(g, Paint.Wood, false, new Vector3(_xWallW + 0.155f, y + 0.38f - l * 0.12f, z), new Vector3(0.01f, 0.025f, l == 0 ? 0.4f : 0.6f), GhInk);
            Add(g, Prim.Cylinder, Paint.Wood, false, Scaled(new Vector3(_xWallW + 0.16f, y - 0.3f, z), sealAxis,
                new Vector3(0.16f, 0.02f, 0.16f)), RegisterCover);
        }
    }

    // ── The roof ─────────────────────────────────────────────────────────

    /// <summary>The hammerbeam roof on the walls' plates, in the ceiling group: a truss over each
    /// post (hammer beams, braces, hammer posts, principal rafters, a collar and its arch braces), the
    /// ridge and two purlins a side, the boards, and the end walls' gables, stepped. It hides
    /// while the camera is within CeilingMargin of the wall plates or above, as the Ceremony
    /// Hall's ceiling does. No shadows.</summary>
    private void GhRoof(int bays, float bay)
    {
        _ceiling = Group("Ceiling");
        float len = _xWallE - _xWallW;
        float half = (_zWallN - _zWallS) * 0.5f;
        float xMid = (_xWallW + _xWallE) * 0.5f;
        float pitch = Mathf.DegToRad(GhRoofPitch);
        float rise = half * Mathf.Tan(pitch);
        float ridgeY = _ceilingY + rise;
        float collarY = _ceilingY + rise * 0.6f;
        float collarHalf = half * 0.4f;
        float postTop = _ceilingY + GhHammer * Mathf.Tan(pitch);

        for (int k = 1; k < bays; k++)
        {
            float x = _xWallW + bay * k;
            foreach (float side in new[] { -1f, 1f })
            {
                float zWall = _cz + side * half;
                float zIn = zWall - side * GhHammer;
                Beam(_ceiling, Paint.Wood, new Vector3(x, _ceilingY - 0.18f, zWall), new Vector3(x, _ceilingY - 0.18f, zIn), 0.3f, 0.36f, Timber);
                Beam(_ceiling, Paint.Wood, new Vector3(x, _ceilingY - 2.6f, zWall - side * 0.11f), new Vector3(x, _ceilingY - 0.36f, zIn), 0.24f, 0.24f, Timber);
                Beam(_ceiling, Paint.Wood, new Vector3(x, _ceilingY, zIn), new Vector3(x, postTop, zIn), 0.26f, 0.26f, Timber);
                Beam(_ceiling, Paint.Wood, new Vector3(x, _ceilingY, zWall), new Vector3(x, ridgeY, _cz), 0.28f, 0.38f, Timber);
                // The arch brace: from the hammer post's foot up under the rafter to the collar.
                Beam(_ceiling, Paint.Wood, new Vector3(x, _ceilingY, zIn), new Vector3(x, collarY, _cz + side * collarHalf * 0.5f), 0.22f, 0.22f, Timber);
            }
            Beam(_ceiling, Paint.Wood, new Vector3(x, collarY, _cz - collarHalf), new Vector3(x, collarY, _cz + collarHalf), 0.26f, 0.32f, Timber);
        }

        Box(_ceiling, Paint.Wood, false, new Vector3(xMid, ridgeY - 0.1f, _cz), new Vector3(len, 0.3f, 0.3f), Timber);
        foreach (float side in new[] { -1f, 1f })
        {
            foreach (float f in new[] { 1f / 3f, 2f / 3f })
                Box(_ceiling, Paint.Wood, false, new Vector3(xMid, _ceilingY + rise * f, _cz + side * half * (1f - f)),
                    new Vector3(len, 0.24f, 0.24f), Timber);
            Beam(_ceiling, Paint.Wood, new Vector3(xMid, _ceilingY + 0.3f, _cz + side * (half + 0.3f)), new Vector3(xMid, ridgeY + 0.3f, _cz),
                len + 2f * WallThick, 0.12f, GhRoofBoard);
        }

        foreach (float xEnd in new[] { _xWallW - WallThick * 0.5f, _xWallE + WallThick * 0.5f })
            for (int s = 0; s < GhGableSteps; s++)
            {
                float y0 = _ceilingY + rise * s / GhGableSteps;
                float y1 = _ceilingY + rise * (s + 1) / GhGableSteps;
                float w = 2f * half * (1f - (s + 0.5f) / GhGableSteps);
                Box(_ceiling, Paint.Wash, false, new Vector3(xEnd, (y0 + y1) * 0.5f, _cz), new Vector3(WallThick, y1 - y0, w), GhOchre);
            }
    }

    /// <summary>A timber from <paramref name="a"/> to <paramref name="b"/>: <paramref name="width"/>
    /// across it level, <paramref name="depth"/> across it the other way.</summary>
    private void Beam(Node3D g, Paint paint, Vector3 a, Vector3 b, float width, float depth, Color color)
    {
        Vector3 u = b - a;
        float len = u.Length();
        if (len < 0.01f)
            return;
        Vector3 dir = u / len;
        Vector3 side = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up.Cross(dir).Normalized();
        Vector3 up = dir.Cross(side).Normalized();
        Add(g, Prim.Box, paint, false, new Transform3D(new Basis(side * width, up * depth, dir * len), (a + b) * 0.5f), color);
    }

    // ── The lectern, the dais and the lights ─────────────────────────────

    /// <summary>The lectern at the floor's west end with the Register open on it, its reader
    /// standing west of it facing the hall, and a candle on an iron stand beside it. At tier 3 it
    /// stands on the dais, with eight chairs along the dais's back, four either side: room for
    /// every voice.</summary>
    private void GhLecternAndDais(int tier)
    {
        var lectern = new Vector3(_xStripW - GhLecternGap, _floorY, _cz);
        if (tier >= 3)
        {
            float xFront = _xStripW - GhDaisGap;
            float xBack = xFront - GhDaisDepth;
            float top = _floorY + GhDaisRise;
            Box(_hall, Paint.Wood, true, new Vector3((xFront + xBack) * 0.5f, (_floorY + top) * 0.5f, _cz),
                new Vector3(GhDaisDepth, GhDaisRise, 2f * GhDaisHalfWidth), GhDaisWood);
            Box(_hall, Paint.Wood, true, new Vector3(xFront + 0.2f, _floorY + GhDaisRise * 0.25f, _cz),
                new Vector3(0.4f, GhDaisRise * 0.5f, 2f * GhDaisHalfWidth - 1.0f), GhDaisWood);
            foreach (float k in new[] { -1f, 1f })
                foreach (float off in new[] { 1.6f, 2.8f, 4.0f, 5.2f })
                    Chair(_hall, new Transform3D(Basis.Identity, new Vector3(xBack + 0.5f, top, _cz + k * off)), 0);
            lectern = new Vector3(xFront - 1.0f, top, _cz);
        }
        GhLectern(lectern);
    }

    private void GhLectern(Vector3 basePos)
    {
        var at = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), basePos);   // its reading slope toward -X
        if (!PlaceModel(_hall, "lectern", at))
        {
            PieceBox(_hall, Paint.Wood, true, at, new Vector3(0f, 0.06f, 0f), new Vector3(0.6f, 0.12f, 0.6f), Timber);
            PieceBox(_hall, Paint.Wood, true, at, new Vector3(0f, 0.6f, 0f), new Vector3(0.24f, 1.0f, 0.3f), Timber);
            var slope = at * new Transform3D(new Basis(new Vector3(0f, 0f, 1f), -Mathf.DegToRad(18f)), new Vector3(0f, 1.16f, 0f));
            PieceBox(_hall, Paint.Wood, true, slope, Vector3.Zero, new Vector3(0.62f, 0.06f, 0.8f), Timber);
            PieceBox(_hall, Paint.Wood, false, slope, new Vector3(0f, 0.045f, 0f), new Vector3(0.5f, 0.03f, 0.66f), RegisterCover);
            PieceBox(_hall, Paint.Wood, false, slope, new Vector3(0f, 0.07f, 0f), new Vector3(0.46f, 0.03f, 0.6f), Parchment);
        }

        var stand = basePos + new Vector3(0.1f, 0f, 0.75f);
        Add(_hall, Prim.Hex, Paint.Wood, true, Scaled(stand + new Vector3(0f, 0.04f, 0f), Basis.Identity, new Vector3(0.3f, 0.08f, 0.3f)), Iron);
        Add(_hall, Prim.Cylinder, Paint.Wood, true, Scaled(stand + new Vector3(0f, 0.65f, 0f), Basis.Identity, new Vector3(0.05f, 1.2f, 0.05f)), Iron);
        Add(_hall, Prim.Cylinder, Paint.Wood, false, Scaled(stand + new Vector3(0f, 1.36f, 0f), Basis.Identity, new Vector3(0.07f, 0.22f, 0.07f)), GhWax);
        Add(_hall, Prim.Sphere, Paint.Glow, false, Scaled(stand + new Vector3(0f, 1.52f, 0f), Basis.Identity, new Vector3(0.05f, 0.09f, 0.05f)), LampGlow);
        GhLight(stand + new Vector3(0f, 1.55f, 0f), 0.6f, 5.5f, LampLight);
    }

    /// <summary>Lamp-stands on the walks: bays 1 and n-2 on both sides, and the south walk's
    /// middle bay (the hearth lights the north's).</summary>
    private void GhLamps(int bays, float bay)
    {
        foreach (float side in new[] { -1f, 1f })
        {
            float z = _cz + side * (_zWallN - _cz - 0.55f);
            var at = new SortedSet<int> { 1, bays - 2 };
            if (side < 0f)
                at.Add(bays / 2);
            foreach (int i in at)
                LampStand(_hall, new Vector3(_xWallW + bay * (i + 0.5f), _floorY, z), true);
        }
    }

    private OmniLight3D GhLight(Vector3 position, float energy, float range, Color color)
    {
        var light = new OmniLight3D
        {
            Name = "Lamplight",
            Position = position,
            LightColor = color,
            LightEnergy = energy,
            OmniRange = range,
            ShadowEnabled = false,
        };
        _hall.AddChild(light);
        return light;
    }
}
