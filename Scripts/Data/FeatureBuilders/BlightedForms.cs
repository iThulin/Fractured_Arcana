using System;

// ============================================================
// BlightedForms.cs
//
// Purpose:        Every building's blighted form (campus_building_upgrades_
//                 design_v1 §11c, §21). At blight level 2 a building's
//                 doctrine goes dark and it works in the blight's way: a
//                 trade, not a penalty table. Level 3 (overrun) stops the
//                 building instead, so a form is live at level 2 only.
//
//                 The five forms built with the corruption slice live in
//                 their own systems (Scribe's Tower, Enchanter's Workshop,
//                 Crucible, Teleport Sigil, Grand Hall). This file holds the
//                 numbers for the twelve added 2026-09-30, and the one line
//                 per building the campus card shows.
// Layer:          Data / rules
// Collaborators:  CampusBlight (levels), every system that reads a form:
//                 EdgeRules and CombatManager (Training Grounds, Armory,
//                 Sanctum), DisenchantValues (Dissolution), CardCommission-
//                 Service (Library), CardUpgradeScreen (Scriptorum),
//                 ExpeditionManager (Scrying, Gatehouse Yard), ParleyDeck and
//                 CouncilTick (Courier, Embassy), ShadowTick and Networks
//                 (Undercroft), CompanionInjurySystem (Dormitory),
//                 CardHalls (Sanctum's floor), CampusScreen (the label).
// ============================================================

/// <summary>Blighted-form rules and their card text. Stateless.</summary>
public static class BlightedForms
{
    // Building ids (Data/Buildings).
    public const string TrainingGrounds = "training_grounds";
    public const string Dissolution = "dissolution_chamber";
    public const string Library = "arcane_library";
    public const string Scriptorum = "scriptorum";
    public const string Scrying = "scrying_chambers";
    public const string Courier = "courier_station";
    public const string Undercroft = "undercroft";
    public const string Embassy = "embassy";
    public const string Armory = "armory";
    public const string Dormitory = "dormitory";
    public const string Gatehouse = "gatehouse_yard";
    public const string Sanctum = "sanctum";

    // ── Numbers (starting values) ────────────────────────────────────────
    public const int BloodDrillEdge = 1;          // Training Grounds: +1 starting Edge
    public const int BloodDrillCost = 1;          //   and 1 HP per maneuver
    public const int HungryVatsPercent = 150;     // Dissolution: yield x1.5, no upgrade refund
    public const int WhisperingStacksFaster = 1;  // Library: research one moon sooner
    public const int FeveredCopyingPercent = 50;  // Scriptorum: upgrades at half cost, once a moon per card
    public const int CloudedWaterSites = 2;       // Scrying: sites charted x2
    public const int CloudedWaterEssence = 3;     //   and 3 Essence drunk at the start
    public const int LooseLipsCards = 2;          // Courier: Connections cards x2
    public const int RottenCellarInformants = 2;  // Undercroft: +2 informants
    public const int RottenCellarBurn = 10;       //   and +10% burn chance each
    public const int TaintedGiftsPercent = 50;    // Embassy: missions at half gold, +1 Exposure each
    public const int RustDamage = 1;              // Armory: +1 attack with a weapon, -1 armor from gear
    public const int RestlessSleepLoyalty = 3;    // Dormitory: recovery x2, -3 loyalty a moon while resting
    public const int OpenGateFuelPercent = 125;   // Gatehouse: +25% fuel or rations
    public const int OpenGateHullPercent = 85;    //   and -15% Hull or Health
    public const int ProfanedFloor = 2;           // Sanctum: deck floor -2
    public const int ProfanedHand = 1;            //   and the wizard's opening hand one short

    /// <summary>Is this building's blighted form in force? Blighted (level 2)
    /// and standing; an overrun building has stopped working instead.</summary>
    public static bool Active(GuildSaveData save, string buildingId)
    {
        var b = CampusBlight.Find(save, buildingId);
        return b != null && b.Tier > 0 && b.IsPlaced && b.BlightLevel == 2;
    }

    /// <summary>A percentage of <paramref name="value"/>, rounded to the nearest.</summary>
    public static int Percent(int value, int percent)
        => (int)Math.Round(value * percent / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>The blighted form's line for the campus card, or null when the
    /// building has none.</summary>
    public static string Describe(string buildingId) => buildingId switch
    {
        "scribes_tower" => "Cracked Glass: seals any rarity below Legendary at half the gold, but the glass cuts its caster when it shatters.",
        "enchanters_workshop" => "Enchants cost half, but each writes a drawback into the item and seals its slot.",
        "crucible_of_storms" => "The storm slips its leash: the moon picks the front, and it opens every fight with 2 attunement instead of 1.",
        "teleport_sigil" => "The pattern leaks: kingdoms learn it twice as fast, and the rift opens to Friendly cities too.",
        "grand_hall" => "One fewer charter seat: the latest charter past the seats goes dark.",
        TrainingGrounds => $"Blood Drills: martials start every fight with +{BloodDrillEdge} Edge, but each maneuver costs them {BloodDrillCost} HP.",
        Dissolution => "Hungry Vats: disenchanting pays half again, but a card's upgrades are lost, not refunded.",
        Library => "Whispering Stacks: research lands a moon sooner, but brings a random card of the same school and rarity.",
        Scriptorum => "Fevered Copying: upgrades cost half, but each card takes at most one a moon.",
        Scrying => $"Clouded Water: twice the sites charted, but the water drinks {CloudedWaterEssence} Essence at each expedition's start.",
        Courier => "Loose Lips: twice the Connections cards at the table, but each moon the court that trusts you least grows warier.",
        Undercroft => $"Rotten Cellar: {RottenCellarInformants} more informants may run, but every one is easier to burn.",
        Embassy => "Tainted Gifts: missions cost half the gold, but every one that lands raises Exposure at its court.",
        Armory => "Rusted Racks: jagged steel hits 1 harder, but armor from gear holds 1 less.",
        Dormitory => $"Restless Sleep: the injured mend twice as fast, but lose {RestlessSleepLoyalty} loyalty each moon they lie there.",
        Gatehouse => "Open Gate: every sortie leaves with a quarter more fuel or rations, but less Hull or Health.",
        Sanctum => $"Profaned Lectern: the deck floor drops by {ProfanedFloor}, but the wizard's opening hand is {ProfanedHand} card short.",
        _ => null,
    };
}
