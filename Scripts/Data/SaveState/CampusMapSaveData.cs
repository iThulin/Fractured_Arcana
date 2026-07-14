using System.Collections.Generic;

// ============================================================
// CampusMapSaveData.cs
//
// Purpose:        Tier 3 save model for the campus ground layout —
//                 the hex map the guild is built on. Pure data,
//                 Godot-free, following the plain-data convention
//                 of GuildSaveData / EternalLedger. Lives on
//                 EternalLedger.CampusMap (the campus, like the
//                 guild, exists outside the timelines).
// Layer:          Data
// Collaborators:  EternalLedger.cs (owner),
//                 SaveManager.cs (lazy migration backfills via
//                 GenerateDefault when a v100 ledger predates the
//                 campus map),
//                 CampusHexGrid.cs (renders this + Buildings),
//                 BuildingSaveData (Q/R/IsPlaced anchor here)
// See:            campus_siege_and_defense_v1_1.docx §5 —
//                 Implementation Hooks (footprints/rotation land
//                 on this schema additively in a later phase)
// ============================================================

/// <summary>
/// One ground hex of the campus map. Axial coordinates, flat-top,
/// matching the overworld/combat hex conventions.
/// </summary>
public class CampusTileSaveData
{
    public int Q = 0;
    public int R = 0;

    /// <summary>Ground type: "grass" for now. Later: path, water, rock,
    /// scar, rubble — see the siege doc's destruction notes.</summary>
    public string Terrain = "grass";

    /// <summary>False for tiles that can never hold a building
    /// (water, monuments, reserved ground).</summary>
    public bool Buildable = true;
}

/// <summary>
/// The ground layout of the campus scene: which hexes exist, their
/// terrain, and whether they accept buildings. Building PLACEMENT is
/// not stored here — it lives on each <see cref="BuildingSaveData"/>
/// (Q/R/IsPlaced) so the building list stays the single source of
/// truth for what the guild owns. Serialized inside the ledger by
/// <see cref="SaveManager"/> (System.Text.Json, IncludeFields).
/// </summary>
public class CampusMapSaveData
{
    /// <summary>Disc radius used by <see cref="GenerateDefault"/>.
    /// Kept in the save so a future "campus expansion" can grow it.</summary>
    public int Radius = 5;

    public List<CampusTileSaveData> Tiles = new();

    /// <summary>
    /// The authored default layout: a radius-5 hex disc centred on
    /// (0,0), all grass, all buildable. CampusScreen's Camera2D zoom
    /// is tuned against this size. Called at NewGame and by the lazy
    /// migration in SaveManager.Load for ledgers that predate the map.
    /// </summary>
    public static CampusMapSaveData GenerateDefault()
    {
        var map = new CampusMapSaveData { Radius = 5 };

        for (int q = -map.Radius; q <= map.Radius; q++)
        {
            int r1 = System.Math.Max(-map.Radius, -q - map.Radius);
            int r2 = System.Math.Min(map.Radius, -q + map.Radius);
            for (int r = r1; r <= r2; r++)
            {
                map.Tiles.Add(new CampusTileSaveData
                {
                    Q = q,
                    R = r,
                    Terrain = "grass",
                    Buildable = true,
                });
            }
        }

        return map;
    }

    /// <summary>Find a tile by axial coordinate, or null.</summary>
    public CampusTileSaveData GetTile(int q, int r)
        => Tiles.Find(t => t.Q == q && t.R == r);
}
