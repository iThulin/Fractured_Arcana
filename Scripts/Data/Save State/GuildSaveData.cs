using System.Collections.Generic;
using System.Text.Json.Serialization;

// ============================================================
// GuildSaveData.cs
//
// Purpose:        In-memory envelope over the three-tier save
//                 schema. Assembles the EternalLedger (tier 3)
//                 and CycleState (tier 2) into one object and
//                 exposes the ENTIRE legacy field surface as
//                 [JsonIgnore] forwarding shims so existing call
//                 sites compile unchanged during the transition.
//                 THIS CLASS IS NEVER SERIALIZED — SaveManager
//                 writes Ledger and Cycle to separate files.
// Layer:          Data
// Collaborators:  EternalLedger.cs (tier 3), CycleState.cs
//                 (tier 2), SaveManager.cs (dual-file IO),
//                 StarterDeckLoader.cs, PlayerDeckService.cs,
//                 BuildingDatabase.cs, ItemDatabase.cs,
//                 CampusScreen.cs (all via shims)
// See:            open_world_refactor_v1.docx §10 — Save Schema
// Shim policy:    Shims exist so Phase 0 lands without touching
//                 forty call sites. New code should address
//                 .Cycle and .Ledger directly; burn shims down
//                 opportunistically as files get touched.
// ============================================================

/// <summary>
/// The active save, assembled in memory from its two on-disk halves.
/// <see cref="Ledger"/> survives every cycle; <see cref="Cycle"/> is
/// replaced wholesale at each cycle reset.
/// </summary>
public class GuildSaveData
{
    // ── The two tiers ────────────────────────────────────────────────────
    /// <summary>Tier 3 — the loom. The only permanent-loss vector.</summary>
    public EternalLedger Ledger = new();

    /// <summary>Tier 2 — the current timeline.</summary>
    public CycleState Cycle = new();

    // ═══════════════════════════════════════════════════════════════════
    // Forwarding shims — legacy surface, [JsonIgnore], never serialized.
    // ═══════════════════════════════════════════════════════════════════

    // ── Meta ────────────────────────────────────────────────────────────
    [JsonIgnore] public int SaveVersion
    { get => Ledger.SaveVersion; set { Ledger.SaveVersion = value; Cycle.SaveVersion = value; } }

    [JsonIgnore] public string GuildName
    { get => Ledger.GuildName; set => Ledger.GuildName = value; }

    [JsonIgnore] public string CreatedAt
    { get => Ledger.CreatedAt; set => Ledger.CreatedAt = value; }

    [JsonIgnore] public string LastPlayedAt
    { get => Ledger.LastPlayedAt; set => Ledger.LastPlayedAt = value; }

    // ── Campaign state ───────────────────────────────────────────────────
    [JsonIgnore] public CampaignState Campaign
    { get => Cycle.Campaign; set => Cycle.Campaign = value; }

    // ── Wizard ──────────────────────────────────────────────────────────
    [JsonIgnore] public string SelectedSchool
    { get => Cycle.SelectedSchool; set => Cycle.SelectedSchool = value; }

    [JsonIgnore] public string WizardName
    { get => Cycle.WizardName; set => Cycle.WizardName = value; }

    // ── Region ──────────────────────────────────────────────────────────
    [JsonIgnore] public string CurrentRegionId
    { get => Cycle.CurrentRegionId; set => Cycle.CurrentRegionId = value; }

    // ── Economy ─────────────────────────────────────────────────────────
    [JsonIgnore] public int Gold
    { get => Cycle.Gold; set => Cycle.Gold = value; }

    [JsonIgnore] public int ArcaneSplinters
    { get => Cycle.ArcaneSplinters; set => Cycle.ArcaneSplinters = value; }

    // ── Run stats ───────────────────────────────────────────────────────
    [JsonIgnore] public int TotalRuns
    { get => Cycle.TotalRuns; set => Cycle.TotalRuns = value; }

    [JsonIgnore] public int RunsWon
    { get => Cycle.RunsWon; set => Cycle.RunsWon = value; }

    [JsonIgnore] public int RunsLost
    { get => Cycle.RunsLost; set => Cycle.RunsLost = value; }

    [JsonIgnore] public int TotalGoldEarned
    { get => Cycle.TotalGoldEarned; set => Cycle.TotalGoldEarned = value; }

    [JsonIgnore] public int TotalEncountersWon
    { get => Cycle.TotalEncountersWon; set => Cycle.TotalEncountersWon = value; }

    // ── Companions ──────────────────────────────────────────────────────
    [JsonIgnore] public List<Companion> Companions
    { get => Cycle.Companions; set => Cycle.Companions = value; }

    [JsonIgnore] public List<string> ActivePartyCompanionIds
    { get => Cycle.ActivePartyCompanionIds; set => Cycle.ActivePartyCompanionIds = value; }

    [JsonIgnore] public int MaxPartySize
    { get => Cycle.MaxPartySize; set => Cycle.MaxPartySize = value; }

    // ── Training Grounds helpers (read the eternal campus) ──────────────
    [JsonIgnore] public int TrainingGroundsTier => GetBuildingTier("training_grounds");

    [JsonIgnore] public int MartialStanceSlots => TrainingGroundsTier;

    [JsonIgnore] public int FighterBaseAP => TrainingGroundsTier switch
    {
        0 => 3,
        1 => 4,
        2 => 4,
        3 => 5,
        _ => 3,
    };

    [JsonIgnore] public int RangerBaseAP => TrainingGroundsTier switch
    {
        0 => 3,
        1 => 5,
        2 => 5,
        3 => 6,
        _ => 3,
    };

    private int GetBuildingTier(string buildingId)
    {
        foreach (var b in Ledger.Buildings)
            if (b.Id == buildingId)
                return b.Tier;
        return 0;
    }

    // ── Equipment armory ─────────────────────────────────────────────────
    [JsonIgnore] public ArmoryData Armory
    { get => Cycle.Armory; set => Cycle.Armory = value; }

    // ── Buildings (the eternal campus) ───────────────────────────────────
    [JsonIgnore] public List<BuildingSaveData> Buildings
    { get => Ledger.Buildings; set => Ledger.Buildings = value; }

    // ── Persistent deck ──────────────────────────────────────────────────
    [JsonIgnore] public PlayerDeckSave PlayerDeck
    { get => Cycle.PlayerDeck; set => Cycle.PlayerDeck = value; }

    [JsonIgnore] public int MinDeckSize
    { get => Cycle.MinDeckSize; set => Cycle.MinDeckSize = value; }

    /// <summary>Discovered blueprints — knowledge, so it lives in the loom.</summary>
    [JsonIgnore] public List<string> UnlockedCardBlueprintIds
    { get => Ledger.UnlockedCardBlueprintIds; set => Ledger.UnlockedCardBlueprintIds = value; }

    // ── Faction reputation ──────────────────────────────────────────────
    [JsonIgnore] public Dictionary<string, int> FactionReputation
    { get => Cycle.FactionReputation; set => Cycle.FactionReputation = value; }

    // ── Region memory ────────────────────────────────────────────────────
    [JsonIgnore] public Dictionary<string, RegionMemorySaveData> RegionMemory
    { get => Cycle.RegionMemory; set => Cycle.RegionMemory = value; }

    // ── Honored Dead (the loom remembers the dead) ──────────────────────
    [JsonIgnore] public List<HonoredDeadRecord> HonoredDead
    { get => Ledger.HonoredDead; set => Ledger.HonoredDead = value; }

    // ── Lore / progression flags ────────────────────────────────────────
    [JsonIgnore] public List<string> UnlockedLoreEntries
    { get => Ledger.UnlockedLoreEntries; set => Ledger.UnlockedLoreEntries = value; }

    [JsonIgnore] public List<string> CompletedEvents
    { get => Cycle.CompletedEvents; set => Cycle.CompletedEvents = value; }

    // ── Phase 3+ stubs ───────────────────────────────────────────────────
    [JsonIgnore] public string CharterAlignment
    { get => Cycle.CharterAlignment; set => Cycle.CharterAlignment = value; }

    [JsonIgnore] public int SeasonalThreatLevel
    { get => Cycle.SeasonalThreatLevel; set => Cycle.SeasonalThreatLevel = value; }

    [JsonIgnore] public Dictionary<string, int> FragmentProgress
    { get => Cycle.FragmentProgress; set => Cycle.FragmentProgress = value; }
}

// ────────────────────────────────────────────────────────────────────────────
// Persistent deck types (unchanged; serialized inside CycleState.PlayerDeck)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The player's full card collection and active-deck configuration
/// for the current cycle. Seeded by StarterDeckLoader at cycle start;
/// hydrated into live Card instances at run start by PlayerDeckService.
/// </summary>
public class PlayerDeckSave
{
    /// <summary>Every card the player owns this cycle, across all copies.</summary>
    public List<OwnedCard> Cards = new();

    /// <summary>
    /// InstanceIds of cards currently slotted into the active run deck.
    /// Min: 10. Max: 20. All other owned cards are in the stash.
    /// </summary>
    public List<string> ActiveDeckInstanceIds = new();

    // Deck size limits enforced by PlayerDeckService.
    public const int MinDeckSize = 10;
    public const int MaxDeckSize = 20;
}

/// <summary>
/// One owned copy of a card, with its upgrade and graft state.
/// Multiple copies of the same blueprint are separate OwnedCard instances
/// with distinct InstanceIds.
/// </summary>
public class OwnedCard
{
    /// <summary>
    /// Matches <see cref="CardBlueprint.Id"/>. Used to look up the
    /// blueprint in CardDatabase at run start.
    /// </summary>
    public string BlueprintId = "";

    /// <summary>
    /// Unique per owned copy. Generated once as Guid.NewGuid().ToString("N").
    /// Used as the key in ActiveDeckInstanceIds.
    /// </summary>
    public string InstanceId = "";

    /// <summary>
    /// 0 = base, 1 = Refined (+), 2 = Mastered (++), 3 = Ascended (+++).
    /// Applied by PlayerDeckService when instantiating the card for a run.
    /// </summary>
    public int TopTier = 0;

    public int BotTier = 0;

    public int PointsSpent = 0; // total upgrade points spent on this card, for display purposes

    /// <summary>
    /// Ids of grafts applied to this copy. Max 2 grafts per card.
    /// Graft application is permanent and irreversible.
    /// </summary>
    public List<string> Grafts = new();

    /// <summary>
    /// True for cards that were in the starting deck.
    /// Starter cards cannot be removed from the collection (only upgraded).
    /// </summary>
    public bool IsStarter = false;

    /// <summary>
    /// Tracks the number of times a card has been cast in the campaign.
    /// Used as a resource for card mastery.
    /// </summary>
    public int CastCount = 0;

    // ── Convenience ──────────────────────────────────────────────────
    public bool IsBaseUpgraded => TopTier >= 1 && BotTier >= 1;
    public int TotalTier => TopTier + BotTier;
    public bool IsMaxed => TopTier >= 4 && BotTier >= 4;

    // Points remaining after mandatory 1/1 step
    public int PointsRemaining => 6 - PointsSpent;

    // Whether a given half can be upgraded further
    public bool CanUpgradeTop => IsBaseUpgraded && TopTier < 4 && PointsRemaining > 0;
    public bool CanUpgradeBot => IsBaseUpgraded && BotTier < 4 && PointsRemaining > 0;
}

// ────────────────────────────────────────────────────────────────────────────
// Building types
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Save data for a single campus building.</summary>
public class BuildingSaveData
{
    public string Id = "";
    public string Name = "";
    public int Tier = 0;                // 0 = not built, 1-3 = built tiers
    public string Category = "";
    public string SchoolAffinity = "";
}
