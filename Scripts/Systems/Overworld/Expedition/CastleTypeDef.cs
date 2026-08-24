using System.Collections.Generic;

// ============================================================
// CastleTypeDef.cs
//
// Purpose:        The Mobile Fortress castle roster (spec §4). The castle is
//                 the school's expression: selecting the school at founding
//                 selects the chassis. Each type = one MOVEMENT SIGNATURE +
//                 one OPERATING QUIRK, keyed by CardSchool.
//
//                 Code table in v1 (§10: promote to Data/Castles/*.json only
//                 if modding demands it). All magnitudes are starting values.
//
//                 What each field feeds:
//                   CheapTerrains/TerrainDiscount, ExtraRoadDiscount, WaiveFord
//                     -> OverworldMovementCost.StepCost (stateless per-edge, so
//                        preview == charge). Set as static ambient at deploy.
//                   ChronoFlatMoves/ChronoFlatCost -> the CHARGE site
//                     (OnPartyMoved), a per-move counter persisted on
//                     PlayerSession so it survives combat round-trips.
//                   BonusMaxFuel     -> MaxFuel at deploy (Adept).
//                   RestRefuelMult   -> the rest-site Refuel (Druid).
//                   CorruptionMult   -> the corruption Hull drain (Necromancer).
//                   WeatherHullImmune-> the weather Hull drain (Elementalist).
//                   BonusScry        -> VisionModifiers.ScryBonus (Arcanist).
//                   BonusModuleSlots -> F5 module system (Tinker).
//                   AmbushChanceMult -> F6 ambush roll (Enchanter).
//                   FreeRevealReroll -> later (Chronomancer).
// Layer:          Data (pure table, no nodes)
// Collaborators:  ExpeditionManager (reads the active def), OverworldMovementCost.
// ============================================================

/// <summary>One school's castle: its movement signature + operating quirk.</summary>
public sealed class CastleTypeDef
{
    public CardSchool School;
    public string Name = "";

    // ── Movement signature (stateless per-edge; applied inside StepCost) ──
    public HashSet<OverworldHex.TerrainType> CheapTerrains = new();
    public int TerrainDiscount;      // subtracted for a CheapTerrains destination
    public int ExtraRoadDiscount;    // added to the base road discount (Tinker)
    public bool WaiveFord;           // ignore the unbridged-river ford penalty (Enchanter)

    // ── Chronomancer flat-move quirk (stateful; applied at the charge) ──
    public int ChronoFlatMoves;      // first N moves of the sortie burn a flat cost
    public int ChronoFlatCost = 1;

    // ── Operating quirks (wired to already-built systems) ──
    public int BonusMaxFuel;                 // Adept
    public int RestRefuelMultiplier = 1;     // Druid
    public float CorruptionDrainMultiplier = 1f; // Necromancer
    public bool WeatherHullImmune;           // Elementalist
    public int BonusScry;                    // Arcanist

    // ── Deferred quirks (data present; wired by later increments) ──
    public int BonusModuleSlots;             // Tinker → F5
    public float AmbushChanceMultiplier = 1f; // Enchanter → F6
    public bool FreeRevealReroll;            // Chronomancer → later

    public string Quirk = "";                // one-line human description
}

/// <summary>The castle roster, keyed by school (spec §4). Static code table.</summary>
public static class CastleTypes
{
    private static readonly Dictionary<CardSchool, CastleTypeDef> Table = Build();

    public static CastleTypeDef For(CardSchool school)
        => Table.TryGetValue(school, out var d) ? d : Table[CardSchool.Adept];

    private static Dictionary<CardSchool, CastleTypeDef> Build()
    {
        var t = new Dictionary<CardSchool, CastleTypeDef>();

        t[CardSchool.Adept] = new CastleTypeDef
        {
            School = CardSchool.Adept, Name = "The Bastion Errant",
            BonusMaxFuel = 5,
            Quirk = "+5 fuel tank (the generalist's deeper reserve)",
        };

        t[CardSchool.Elementalist] = new CastleTypeDef
        {
            School = CardSchool.Elementalist, Name = "The Cinderhold",
            CheapTerrains = new() { OverworldHex.TerrainType.Volcanic, OverworldHex.TerrainType.Desert },
            TerrainDiscount = 1,
            WeatherHullImmune = true,
            Quirk = "Volcanic/Desert stride −1; immune to weather Hull damage",
        };

        t[CardSchool.Druid] = new CastleTypeDef
        {
            School = CardSchool.Druid, Name = "The Verdant Ark",
            CheapTerrains = new() { OverworldHex.TerrainType.Forest, OverworldHex.TerrainType.Swamp },
            TerrainDiscount = 1,
            RestRefuelMultiplier = 2,
            Quirk = "Forest/Swamp stride −1; rest-site refuel doubled",
        };

        t[CardSchool.Necromancer] = new CastleTypeDef
        {
            School = CardSchool.Necromancer, Name = "The Ossuary Ambulant",
            CheapTerrains = new() { OverworldHex.TerrainType.Ruins },
            TerrainDiscount = 1,
            CorruptionDrainMultiplier = 0.5f,
            Quirk = "Ruins stride −1; corruption Hull drain halved",
        };

        t[CardSchool.Tinker] = new CastleTypeDef
        {
            School = CardSchool.Tinker, Name = "The Gearspire",
            ExtraRoadDiscount = 1,        // road discount doubled (base 1 + 1 = 2)
            BonusModuleSlots = 1,         // F5
            Quirk = "road discount doubled; +1 module slot",
        };

        t[CardSchool.Enchanter] = new CastleTypeDef
        {
            School = CardSchool.Enchanter, Name = "The Lantern Keep",
            WaiveFord = true,
            AmbushChanceMultiplier = 0.8f, // F6
            Quirk = "river fords waived; ambush chance −20%",
        };

        t[CardSchool.Arcanist] = new CastleTypeDef
        {
            School = CardSchool.Arcanist, Name = "The Orrery Bastille",
            CheapTerrains = new() { OverworldHex.TerrainType.Hills, OverworldHex.TerrainType.Mountain },
            TerrainDiscount = 1,
            BonusScry = 1,
            Quirk = "Hills/Mountain stride −1; scry radius +1",
        };

        t[CardSchool.Chronomancer] = new CastleTypeDef
        {
            School = CardSchool.Chronomancer, Name = "The Hourglass Redoubt",
            ChronoFlatMoves = 3, ChronoFlatCost = 1,
            FreeRevealReroll = true,     // later
            Quirk = "first 3 strides each sortie burn 1 flat",
        };

        return t;
    }
}
