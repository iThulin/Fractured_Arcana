using Godot;
using System.Collections.Generic;

// ============================================================
// CrewStations.cs
//
// Purpose:        The Mobile Fortress crew system (spec §5). The active party
//                 IS the crew: each sortie its members man five stations, and
//                 each staffed station grants the castle an effect derived from
//                 data that ALREADY exists: the companion's PersonalityTrait
//                 (its station archetype) and K4 loyalty tier. No new companion
//                 fields (§5).
//
//                 Archetype from trait: the K5 TraitArchetypeAffinity table
//                 (CouncilState) gives each trait one archetype, and those map
//                 1:1 onto the stations' best-in-slot archetypes (§5 table):
//                   Stoic  -> Survivor  -> Helm
//                   Loyal  -> Commander -> Furnace
//                   Curious-> Scholar   -> Lens Room
//                   Cunning-> (Merchant)-> Quartermaster
//                   Reckless->Idealist  -> Wardroom
//                 A companion at their trait's station is BEST-IN-SLOT (full
//                 effect); anywhere else is a mismatch (half effect). Loyalty
//                 scales on top: Wary ½, mid full, Sworn full +25% (§5).
//
//                 Effects wired now: Helm (fuel burn), Furnace (MaxFuel), Lens
//                 Room (scry). Wardroom (ambush wizard delay −1) is stored for
//                 F6; Quartermaster (loot rarity shift) for the loot pass.
// Layer:          System (pure computation; no nodes)
// Collaborators:  ExpeditionManager (deploy: assign + apply), Companion,
//                 OverworldMovementCost (Helm fuel multiplier), VisionModifiers.
// ============================================================

public enum CrewStation { Helm, Furnace, LensRoom, Quartermaster, Wardroom }

/// <summary>Aggregate castle effects from a staffed crew.</summary>
public struct CrewEffects
{
    public float FuelBurnMultiplier;    // Helm: 1.0 = none, 0.9 = −10%
    public int BonusMaxFuel;            // Furnace
    public int BonusScry;               // Lens Room
    public int WardroomAmbushReduction; // Wardroom (rounds; consumed by F6)
    public int QuartermasterLootShift;  // Quartermaster (loot rarity; later)

    public static CrewEffects None => new CrewEffects { FuelBurnMultiplier = 1f };
}

public static class CrewStations
{
    // ── Tuning (starting values) ─────────────────────────────────────────
    public const float HelmBurnReduction = 0.10f;   // −10% at a matched, Neutral Helm
    public const int FurnaceBonusFuel = 5;
    public const int LensScryBonus = 1;
    public const int WardroomDelayReduction = 1;
    public const int QuartermasterLootShift = 1;

    /// <summary>The station a companion of this trait is best-in-slot for, or null
    /// for an off-vocabulary/empty trait (which is a mismatch everywhere).</summary>
    public static CrewStation? BestStationFor(string trait) => trait switch
    {
        "Stoic" => CrewStation.Helm,
        "Loyal" => CrewStation.Furnace,
        "Curious" => CrewStation.LensRoom,
        "Cunning" => CrewStation.Quartermaster,
        "Reckless" => CrewStation.Wardroom,
        _ => null,
    };

    /// <summary>Auto-assign the party to stations: each companion to their best
    /// station if it is free, then leftovers fill any remaining stations (as
    /// mismatches). One companion per station; empty stations grant nothing.</summary>
    public static Dictionary<CrewStation, Companion> AutoAssign(IReadOnlyList<Companion> party)
    {
        var assign = new Dictionary<CrewStation, Companion>();
        if (party == null) return assign;
        var used = new HashSet<string>();

        // Pass 1: best-in-slot placements.
        foreach (var c in party)
        {
            if (c == null) continue;
            var st = BestStationFor(c.PersonalityTrait);
            if (st.HasValue && !assign.ContainsKey(st.Value))
            { assign[st.Value] = c; used.Add(c.Id); }
        }
        // Pass 2: fill remaining stations with leftover crew (mismatched).
        foreach (var c in party)
        {
            if (c == null || used.Contains(c.Id)) continue;
            foreach (CrewStation st in System.Enum.GetValues(typeof(CrewStation)))
                if (!assign.ContainsKey(st)) { assign[st] = c; used.Add(c.Id); break; }
        }
        return assign;
    }

    /// <summary>The aggregate effects of a station assignment.</summary>
    public static CrewEffects Compute(Dictionary<CrewStation, Companion> assign)
    {
        var e = CrewEffects.None;
        if (assign == null) return e;

        foreach (var kv in assign)
        {
            var station = kv.Key;
            var c = kv.Value;
            if (c == null) continue;

            bool match = BestStationFor(c.PersonalityTrait) == station;
            float loy = LoyaltyScale(c.GetLoyaltyTier());
            float aScale = match ? 1f : 0.5f;
            // The small +1 perks (scry / wardroom / loot) need a competent, non-Wary
            // matched hand to land, since "a wary crew member works the station badly".
            bool solidMatch = match && c.GetLoyaltyTier() != LoyaltyTier.Wary;

            switch (station)
            {
                case CrewStation.Helm:
                    e.FuelBurnMultiplier *= 1f - HelmBurnReduction * aScale * loy;
                    break;
                case CrewStation.Furnace:
                    e.BonusMaxFuel += Mathf.RoundToInt(FurnaceBonusFuel * aScale * loy);
                    break;
                case CrewStation.LensRoom:
                    if (solidMatch) e.BonusScry += LensScryBonus;
                    break;
                case CrewStation.Wardroom:
                    if (solidMatch) e.WardroomAmbushReduction += WardroomDelayReduction;
                    break;
                case CrewStation.Quartermaster:
                    if (solidMatch) e.QuartermasterLootShift += QuartermasterLootShift;
                    break;
            }
        }
        return e;
    }

    /// <summary>K4 loyalty scaling (§5): Wary ½, mid full, Sworn full +25%.</summary>
    private static float LoyaltyScale(LoyaltyTier tier) => tier switch
    {
        LoyaltyTier.Wary => 0.5f,
        LoyaltyTier.Sworn => 1.25f,
        _ => 1f,
    };

    public static string StationName(CrewStation s) => s switch
    {
        CrewStation.Helm => "Helm",
        CrewStation.Furnace => "Furnace",
        CrewStation.LensRoom => "Lens Room",
        CrewStation.Quartermaster => "Quartermaster",
        CrewStation.Wardroom => "Wardroom",
        _ => s.ToString(),
    };
}
