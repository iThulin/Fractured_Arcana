using System.Collections.Generic;

// ============================================================
// CastleManeuverState.cs
//
// Purpose:        The castle's maneuvers between lunations (docs/
//                 provinces_v1_9_bastion.md, slice PV4): the font's
//                 Essence, what is armed for the next march or camp,
//                 the last march (for Retrace), the fronts a spell holds
//                 still, and the forward camp. Plain data with safe
//                 defaults: a save from before PV4 reads as a full font
//                 and nothing armed.
// Layer:          Data
// Collaborators:  CycleState.cs (Maneuvers), CastleManeuvers.cs (reads
//                 and writes all of it), CastleHops.cs (Force Path,
//                 Verdant Passage), ProvinceSaveAssert.Bastion.cs
//                 (round trip)
// See:            docs/provinces_v1_9_bastion.md sections 5 and 6
// ============================================================

/// <summary>The castle's maneuvers: one per cycle, on CycleState.Maneuvers.</summary>
public class CastleManeuverState
{
    /// <summary>The font's Essence, spent by maneuvers and filled again at each new moon.
    /// Its own pool, apart from the expedition's grimoire.</summary>
    public int FontEssence = CastleManeuvers.FontMax;

    /// <summary>Force Path: the next march fords rivers and climbs ridges at no cost.</summary>
    public bool ForcePathArmed = false;

    /// <summary>Verdant Passage holds through this lunation; -1 for none.</summary>
    public int VerdantLunation = -1;

    /// <summary>Veil: the next march leaves no trail.</summary>
    public bool VeilArmed = false;

    /// <summary>True when the castle's last move left no trail (Veil, Retrace), for the
    /// Keeper (PV6).</summary>
    public bool LastMoveVeiled = false;

    /// <summary>Campward: the next camp is warded.</summary>
    public bool CampwardArmed = false;

    /// <summary>The warded camp's tile while its crews unpack; -1 for none.</summary>
    public int WardedCampX = -1;
    public int WardedCampY = -1;

    /// <summary>Wayfarer's Beacon: the next march that ends in a held province makes no camp.</summary>
    public bool BeaconArmed = false;

    /// <summary>The lunation Retrace was last cast in; -1 for never.</summary>
    public int RetraceLunation = -1;

    /// <summary>The castle's last march, for Retrace: where it began and ended, the fuel it
    /// burned and the lunation it was made in. LastMarchValid goes false once the castle
    /// has moved since, or the march was retraced.</summary>
    public bool LastMarchValid = false;
    public int LastMarchFromX = -1;
    public int LastMarchFromY = -1;
    public int LastMarchToX = -1;
    public int LastMarchToY = -1;
    public int LastMarchFuel = 0;
    public int LastMarchLunation = -1;

    /// <summary>Fronts a spell holds still (Thornwall, Stasis Snare): front id to the new
    /// moons it still stands still for.</summary>
    public Dictionary<string, int> FrozenFronts = new();

    /// <summary>The forward camp (Deploy Waystation) while it stands, by its tile and its
    /// province's Id; -1 and "" for none. It goes when the castle next moves.</summary>
    public int WaystationX = -1;
    public int WaystationY = -1;
    public string WaystationProvinceId = "";
}
