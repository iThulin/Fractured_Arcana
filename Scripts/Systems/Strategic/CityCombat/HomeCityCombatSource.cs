using System.Collections.Generic;

// ============================================================
// HomeCityCombatSource.cs
//
// Purpose:        ICityCombatSource adapter over the home campus:
//                 EternalLedger.CampusMap (the /3 flower lattice) +
//                 EternalLedger.Buildings (placement source of truth,
//                 BuildingSaveData.Q/R). Pure read-only snapshot taken
//                 at construction; build one per compile, do not cache
//                 across annexes or building placement.
// Layer:          System (strategic -> combat seam)
// Collaborators:  ICityCombatSource (contract), CampusMapSaveData
//                 (lattice + CornerOwners), GuildSaveData/BuildingSaveData
//                 (IsPlaced/Tier gate — mirrors CampusGridManager
//                 .LoadFromSave's "skip !IsPlaced or Tier <= 0" rule)
// See:            docs/city_battlemap_compiler_spec_v1_1.md §2
// ============================================================

public sealed class HomeCityCombatSource : ICityCombatSource
{
    private const string SeatBuildingId = "grand_hall";
    private const string GateBuildingId = "gatehouse_yard";
    private const string PortalBuildingId = "teleport_sigil";

    private readonly Dictionary<(int q, int r), string> _ground = new();
    private readonly Dictionary<(int q, int r), CityBuildingRef> _buildings = new();
    private readonly Dictionary<string, (int q, int r)> _buildingLots = new();

    public HomeCityCombatSource(EternalLedger ledger)
    {
        if (ledger?.CampusMap?.Tiles != null)
        {
            foreach (var t in ledger.CampusMap.Tiles)
            {
                if (t == null)
                    continue;
                _ground[(t.Q, t.R)] = t.Ground ?? "Lawn";
            }
        }

        if (ledger?.Buildings != null)
        {
            foreach (var b in ledger.Buildings)
            {
                // Same activity gate as CampusGridManager.LoadFromSave:
                // owned-but-unplaced grants nothing, on the battlemap too.
                if (b == null || b.Tier <= 0 || !b.IsPlaced)
                    continue;
                if (!_ground.ContainsKey((b.Q, b.R)))
                    continue;   // stranded off-lattice (pre-migration saves)

                var lot = (b.Q, b.R);
                _buildings[lot] = new CityBuildingRef
                {
                    BlueprintId = b.Id,
                    Rotation = 0,   // BuildingSaveData.Rotation: docx §5, not yet landed
                };
                _buildingLots[b.Id] = lot;
            }
        }
    }

    public IEnumerable<(int q, int r)> Cells => _ground.Keys;

    public CityCellKind KindOf(int q, int r)
    {
        if (!_ground.TryGetValue((q, r), out string ground))
            return CityCellKind.Locked;

        if (ground == "Plaza")
            return CityCellKind.Plaza;

        // A laid cell on a 3-way strategic-tile vertex is a bonus corner cell
        // (CampusMapSaveData.RebuildTilesFromDistricts lays them as "Lawn";
        // the geometry, not the dressing, is what distinguishes them).
        if (CampusMapSaveData.CornerOwners(q, r).Count == 3)
            return CityCellKind.Corner;

        return CityCellKind.Lawn;
    }

    public CityBuildingRef BuildingAt(int q, int r) =>
        _buildings.TryGetValue((q, r), out var b) ? b : null;

    /// <summary>grand_hall's lot; falls back to the founding centre (0,0) —
    /// the Grand Hall startsBuiltAt the origin, so this only fires on a save
    /// where it was somehow unplaced, and (0,0) is still its home.</summary>
    public (int q, int r) SeatCell =>
        _buildingLots.TryGetValue(SeatBuildingId, out var lot) ? lot : (0, 0);

    public (int q, int r)? GateCell =>
        _buildingLots.TryGetValue(GateBuildingId, out var lot) ? lot : null;

    /// <summary>Null until EntryDockType is wired (campus_siege_and_defense_v1_1
    /// §6 / spec §2) — DockRaid is simply unavailable at home until then,
    /// which is the correct diegetic default rather than a guess.</summary>
    public (int q, int r)? DockCell => null;

    public (int q, int r)? TeleporterCell =>
        _buildingLots.TryGetValue(PortalBuildingId, out var lot) ? lot : null;
}
