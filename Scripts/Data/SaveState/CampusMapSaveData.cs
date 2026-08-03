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
// Collaborators:  EternalLedger.cs (owner), CampusHexGrid.cs
//                 (reads this + Ledger.Buildings to populate
//                 CampusHex children), GuildSaveData.cs (Buildings)
// See:            guild_campus_v2.docx §1-5, §8 (Campus Grounds),
//                 single_world_refactor_v2.docx §2 (the data/view
//                 split this mirrors — this class is the "world
//                 data" layer, CampusHexGrid is the "view")
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

    /// <summary>Seed for one-time cosmetic ground generation on a brand-new guild
    /// (see CampusHexGrid.GenerateDefaultLayout). Irrelevant once Tiles is populated —
    /// nothing re-derives from this after first creation.</summary>
    public int Seed = 0;

    public List<CampusTileSaveData> Tiles = new();

    /// <summary>
    /// Builds a fresh campus: a hex disc of radius <paramref name="radius"/>, all
    /// Lawn/buildable except a small authored plaza at the centre (Grand Hall's
    /// eventual home — kept buildable, just dressed differently). Called once
    /// when a new guild is created; never called again, so hand-edited layouts
    /// (moved paths, ponds, etc.) are never clobbered on a later load.
    /// </summary>
    public static CampusMapSaveData GenerateDefault(int radius = 5, int seed = 0)
    {
        var map = new CampusMapSaveData
        {
            GridWidth = radius * 2 + 1,
            GridHeight = radius * 2 + 1,
            Seed = seed
        };

        for (int q = -radius; q <= radius; q++)
        {
            int rMin = System.Math.Max(-radius, -q - radius);
            int rMax = System.Math.Min(radius, -q + radius);
            for (int r = rMin; r <= rMax; r++)
            {
                bool isCentre = q == 0 && r == 0;
                map.Tiles.Add(new CampusTileSaveData
                {
                    Q = q,
                    R = r,
                    Ground = isCentre ? "Plaza" : "Lawn",
                    IsBuildable = true
                });
            }
        }

        return map;
    }
}
