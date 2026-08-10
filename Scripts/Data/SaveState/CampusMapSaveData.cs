using System;
using System.Collections.Generic;

// ============================================================
// CampusMapSaveData.cs
//
// Purpose:        Persistent, authored layout of the guild campus
//                 hex map — cosmetic ground + buildable-slot data.
//                 Lives on EternalLedger (tier 3), alongside
//                 Ledger.Buildings, so it survives cycle resets:
//                 the campus is a place, not a run. Building
//                 POSITION lives on BuildingSaveData.Q/R directly
//                 (single source of truth, no join table); this
//                 class only describes the ground itself.
// Layer:          Data
// Collaborators:  EternalLedger.cs (owner), CampusGridManager.cs
//                 (reads this + Ledger.Buildings to populate
//                 HexTile children), GuildSaveData.cs (Buildings)
// See:            guild_campus_v2.docx §1-5, §8 (Campus Grounds),
//                 single_world_refactor_v2.docx §2 (the data/view
//                 split this mirrors — this class is the "world
//                 data" layer, CampusGridManager is the "view")
// ============================================================

/// <summary>
/// One campus ground tile: axial coord, cosmetic dressing, and whether
/// a building may ever be sited here. Does NOT reference a building —
/// see <see cref="BuildingSaveData"/>.Q/R for placement.
/// </summary>
public class CampusTileSaveData
{
    public int Q = 0;
    public int R = 0;

    /// <summary>Cosmetic ground dressing (e.g. "Lawn", "Path", "Plaza", "Pond").
    /// Purely visual in v1 — CampusHex reads this for tint, nothing else keys off it.</summary>
    public string Ground = "Lawn";

    /// <summary>False for tiles that can never hold a building — paths, the ley line
    /// plaza, decorative water. True (default) covers most of the campus.</summary>
    public bool IsBuildable = true;
}

/// <summary>One campus district — a large hex (one world/city hex when the campus is
/// placed on the map). Districts are unlocked and built out over the game; each unlocked
/// one contributes a 7-hex flower of build-slots to the campus. The space between
/// districts is decorative. (Phase 2, Stage 3 — the district campus.)</summary>
public class CampusDistrict
{
    /// <summary>District-space axial coords (0,0 = the founding centre).</summary>
    public int Q = 0;
    public int R = 0;

    /// <summary>True once the guild has claimed this district (its flower is buildable).</summary>
    public bool Unlocked = false;
}

/// <summary>
/// The whole campus ground layout. One instance, held on EternalLedger.
/// Building placement is tracked separately on each BuildingSaveData.
/// </summary>
public class CampusMapSaveData
{
    public int GridWidth = 11;
    public int GridHeight = 11;

    /// <summary>"Dock" or "Skydock" — resolved once at guild creation from the campus's
    /// starting-location terrain (near water → Dock, else Skydock) and never
    /// recomputed. Empty until that lookup is wired in (see campus_siege_and_defense_v1
    /// §3, §5) — GenerateDefault leaves it blank rather than guessing.</summary>
    public string EntryDockType = "";

    /// <summary>Seed for one-time cosmetic ground generation on a brand-new guild.
    /// RESERVED, not yet used: GenerateDefault stores it but lays every tile as Lawn with a
    /// Plaza at the centre, with no seeded variation. Irrelevant once Tiles is populated —
    /// nothing re-derives from this after first creation.
    ///
    /// (Previously cited CampusHexGrid.GenerateDefaultLayout, which was doubly wrong: that
    /// class was retired on 2026-08-03, and the method never existed on the side that
    /// replaced it.)</summary>
    public int Seed = 0;

    public List<CampusTileSaveData> Tiles = new();

    /// <summary>Fine-lattice schema version. 0–2 = retired cuts (index-7 flowers; the
    /// coarse /3 where ring children WERE the shared corners). 3 = the flower lattice:
    /// children at 1/3 scale unrotated, a whole 7-flower per district, vertex cells as
    /// 3-way bonus corners; 3-district founding. SaveManager lazily migrates &lt; 3 by
    /// regenerating (additive field, no save-version bump — the version guard would
    /// reject the save outright).</summary>
    public int LatticeVersion = 0;

    /// <summary>The campus's districts — large hexes (one per city-hex when placed on the
    /// world) that the guild unlocks and builds out over the game. Each unlocked district
    /// contributes a 7-hex flower of build-slots to Tiles; the space between districts is
    /// decorative. (Phase 2, Stage 3 — the district campus.)</summary>
    public List<CampusDistrict> Districts = new();

    // Districts ARE strategic map tiles: district (dq,dr) is the strategic-AXIAL offset
    // from the guild's home world tile, and the fine build-lattice is the /3 rep-tile
    // subdivision of those tiles (children at 1/√3 scale, rotated 30°). Each strategic
    // tile then contains exactly one whole child at its centre plus six children sitting
    // ON its corners — the /3 pattern. The child lattice tessellates globally, so a
    // corner child shared between adjacent city tiles is claimed by the first district
    // that lays it (discrete membership). See DistrictCentre.

    /// <summary>
    /// Builds a fresh campus: a hex disc of radius <paramref name="radius"/>, all
    /// Lawn/buildable except a small authored plaza at the centre (Grand Hall's
    /// eventual home — kept buildable, just dressed differently). Called once
    /// when a new guild is created; never called again, so hand-edited layouts
    /// (moved paths, ponds, etc.) are never clobbered on a later load.
    /// </summary>
    /// <summary>Fine-hex (child) coordinate of a district's CENTRE slot. The child lattice
    /// is the strategic lattice at 1/3 scale, UNROTATED — so district (dq,dr) (a strategic-
    /// axial offset from home) has its centre child at (3dq,3dr), a full 7-hex flower fits
    /// WHOLLY inside each strategic tile (ring children touch the tile edge exactly), and
    /// the cells on the tile's six VERTICES are the corner pieces shared three ways
    /// (verified numerically; see session log 2026-08-10 flower_lattice).</summary>
    public static (int q, int r) DistrictCentre(int dq, int dr)
        => (3 * dq, 3 * dr);

    /// <summary>The 7 fine-hex build-slots of a district: its centre + the 6 neighbours
    /// (a radius-1 hex "flower"). Index 0 is the centre.</summary>
    public static List<(int q, int r)> FlowerTiles(int cq, int cr) => new()
    {
        (cq, cr),
        (cq + 1, cr), (cq - 1, cr),
        (cq, cr + 1), (cq, cr - 1),
        (cq + 1, cr - 1), (cq - 1, cr + 1),
    };

    /// <summary>The six fine-hex axial directions — a district's ring tiles sit at these
    /// offsets from its centre child.</summary>
    private static readonly (int dq, int dr)[] ChildDirs =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, -1), (-1, 1) };

    /// <summary>The six "diagonal" child offsets from a district centre to that strategic
    /// tile's VERTEX cells (child-distance 2, on the corners at world distance R).</summary>
    private static readonly (int dq, int dr)[] CornerDirs =
        { (2, -1), (1, 1), (-1, 2), (-2, 1), (-1, -1), (1, -2) };

    /// <summary>The districts that share a CORNER cell (each vertex of a strategic tile
    /// touches exactly three tiles). A corner cell's three owner centres sit at the
    /// diagonal offsets; centre children are exactly the (3dq,3dr) sublattice points.
    /// Returns 3 owners for a true corner cell; fewer means the cell isn't one.</summary>
    public static List<(int dq, int dr)> CornerOwners(int q, int r)
    {
        var owners = new List<(int, int)>();
        foreach (var (a, b) in CornerDirs)
        {
            int nq = q + a, nr = r + b;
            if (nq % 3 != 0 || nr % 3 != 0) continue;   // not a district centre
            owners.Add((nq / 3, nr / 3));
        }
        return owners;
    }

    /// <summary>Rebuild <see cref="Tiles"/> from the unlocked districts. Each district lays
    /// its whole 7-hex FLOWER — centre child (Plaza) plus 6 ring children (Lawn) — which
    /// fits entirely inside its strategic tile, so nothing ever pokes out of the city.
    /// Then the CORNER cells (on the tile vertices, shared three ways): one exists only
    /// when ALL THREE owner districts are unlocked — expanding into both neighbours of a
    /// corner earns that bonus tile. Order-independent, so a building's Q/R stays valid
    /// across rebuilds.</summary>
    public void RebuildTilesFromDistricts()
    {
        Tiles.Clear();
        var unlocked = new HashSet<(int, int)>();
        foreach (var d in Districts)
            if (d != null && d.Unlocked)
                unlocked.Add((d.Q, d.R));

        var seen = new HashSet<(int, int)>();
        foreach (var d in Districts)
        {
            if (d == null || !d.Unlocked) continue;
            var (cq, cr) = DistrictCentre(d.Q, d.R);
            if (seen.Add((cq, cr)))
                Tiles.Add(new CampusTileSaveData { Q = cq, R = cr, Ground = "Plaza", IsBuildable = true });
            foreach (var (a, b) in ChildDirs)
            {
                int q = cq + a, r = cr + b;
                if (seen.Add((q, r)))
                    Tiles.Add(new CampusTileSaveData { Q = q, R = r, Ground = "Lawn", IsBuildable = true });
            }
        }

        // Bonus corner cells — each district checks its six vertices; a vertex cell is laid
        // once all three districts meeting there are unlocked.
        foreach (var d in Districts)
        {
            if (d == null || !d.Unlocked) continue;
            var (cq, cr) = DistrictCentre(d.Q, d.R);
            foreach (var (a, b) in CornerDirs)
            {
                int q = cq + a, r = cr + b;
                if (seen.Contains((q, r))) continue;
                var owners = CornerOwners(q, r);
                bool inside = owners.Count == 3;
                if (inside)
                    foreach (var owner in owners)
                        if (!unlocked.Contains(owner)) { inside = false; break; }
                if (!inside) continue;
                seen.Add((q, r));
                Tiles.Add(new CampusTileSaveData { Q = q, R = r, Ground = "Lawn", IsBuildable = true });
            }
        }
    }

    /// <summary>Unlock a district (adding it to the lattice if not listed) and rebuild the
    /// tiles. Returns false if it was already unlocked. The unlock TRIGGER (cost /
    /// progression gate) is wired separately — this is the state change + relayout.</summary>
    public bool UnlockDistrict(int dq, int dr)
    {
        var d = Districts.Find(x => x != null && x.Q == dq && x.R == dr);
        if (d == null)
        {
            d = new CampusDistrict { Q = dq, R = dr };
            Districts.Add(d);
        }
        if (d.Unlocked) return false;
        d.Unlocked = true;
        RebuildTilesFromDistricts();
        return true;
    }

    /// <summary>Builds a fresh campus: a founding cluster of districts (the centre plus two,
    /// unlocked) with the surrounding ring available to unlock as the guild grows. Each
    /// unlocked district lays a 7-hex flower of build-slots. Called once at guild creation.
    /// (radius/seed kept for signature compatibility; radius is unused in the district model.)</summary>
    public static CampusMapSaveData GenerateDefault(int radius = 5, int seed = 0)
    {
        var map = new CampusMapSaveData { Seed = seed, LatticeVersion = 3 };

        // Founding: the home world tile plus two neighbours (a 3-tile city). Each district
        // carries a whole 7-hex flower, and the vertex the three founding tiles share earns
        // its bonus corner cell → 22 build tiles. Rings 1–2 are listed lockable: growth
        // annexes adjacent strategic tiles, and completing a vertex's third district earns
        // that corner's bonus tile.
        var foundingUnlocked = new HashSet<(int, int)> { (0, 0), (1, 0), (0, 1) };
        for (int dq = -2; dq <= 2; dq++)
        for (int dr = Math.Max(-2, -dq - 2); dr <= Math.Min(2, -dq + 2); dr++)
            map.Districts.Add(new CampusDistrict
            {
                Q = dq, R = dr,
                Unlocked = foundingUnlocked.Contains((dq, dr)),
            });

        map.RebuildTilesFromDistricts();
        return map;
    }
}
