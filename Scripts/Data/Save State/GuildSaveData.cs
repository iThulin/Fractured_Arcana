using System.Collections.Generic;

// ============================================================
// GuildSaveData.cs
//
// Purpose:        The full save schema for one guild — everything
//                 that persists between runs (wizard choice, gold,
//                 companions, armory, buildings, faction rep,
//                 region memory, lore unlocks, persistent deck).
// Layer:          Data
// Collaborators:  SaveManager.cs (serializes this),
//                 StarterDeckLoader.cs (seeds PlayerDeck),
//                 PlayerDeckService.cs (hydrates cards from save),
//                 BuildingDatabase.cs (Buildings list),
//                 ItemDatabase.cs (Armory field), CampusScreen.cs
// See:            README §6 — Save System
// Schema version history:
//   v1 — initial schema
//   v2 — added PlayerDeck, ArcaneDust, UnlockedCardBlueprintIds
// ============================================================

/// <summary>
/// Top-level save model. One instance per guild slot.
/// Serialized to JSON by <see cref="SaveManager"/>.
/// Bump <see cref="SaveVersion"/> and add a migration in
/// <c>SaveManager.MigrateIfNeeded</c> whenever the schema changes.
/// </summary>
public class GuildSaveData
{
    // ── Meta ────────────────────────────────────────────────────────────
    public int SaveVersion = 2;
    public string GuildName = "New Guild";
    public string CreatedAt = "";
    public string LastPlayedAt = "";

    // ── Wizard ──────────────────────────────────────────────────────────
    public string SelectedSchool = "Elementalist";

    // ── Region ──────────────────────────────────────────────────────────
    public string CurrentRegionId = "frontier_wilds";

    // ── Economy ─────────────────────────────────────────────────────────
    public int Gold = 0;

    /// <summary>
    /// Upgrade currency. Earned from combat at a low rate; spent at
    /// Training Grounds to upgrade OwnedCard.UpgradeTier.
    /// </summary>
    public int ArcaneDust = 0;

    // ── Run stats ───────────────────────────────────────────────────────
    public int TotalRuns = 0;
    public int RunsWon = 0;
    public int RunsLost = 0;
    public int TotalGoldEarned = 0;
    public int TotalEncountersWon = 0;

    // ── Companions ──────────────────────────────────────────────────────
    public List<Companion> Companions = new();
    public List<string> ActivePartyCompanionIds = new();
    public int MaxPartySize = 2;

    // ── Training Grounds helpers ─────────────────────────────────────────
    public int TrainingGroundsTier => GetBuildingTier("training_grounds");

    /// <summary>
    /// How many stance slots a companion has based on Training Grounds tier.
    /// </summary>
    public int MartialStanceSlots => TrainingGroundsTier;

    /// <summary>Base AP for a Fighter at current Training Grounds tier.</summary>
    public int FighterBaseAP => TrainingGroundsTier switch
    {
        0 => 3,
        1 => 4,
        2 => 4,
        3 => 5,
        _ => 3,
    };

    /// <summary>Base AP for a Ranger at current Training Grounds tier.</summary>
    public int RangerBaseAP => TrainingGroundsTier switch
    {
        0 => 3,
        1 => 5,
        2 => 5,
        3 => 6,
        _ => 3,
    };

    private int GetBuildingTier(string buildingId)
    {
        foreach (var b in Buildings)
            if (b.Id == buildingId) return b.Tier;
        return 0;
    }

    // ── Equipment armory ─────────────────────────────────────────────────
    public ArmoryData Armory = new();

    // ── Buildings ────────────────────────────────────────────────────────
    public List<BuildingSaveData> Buildings = new();

    // ── Persistent deck ──────────────────────────────────────────────────
    /// <summary>
    /// The player's owned card collection and active-deck loadout.
    /// Seeded by <see cref="StarterDeckLoader.SeedStarterDeck"/> on
    /// new game. Hydrated into live Card instances at run start by
    /// <see cref="PlayerDeckService"/>.
    /// </summary>
    public PlayerDeckSave PlayerDeck = new();

    /// <summary>
    /// Set of CardBlueprint.Id values the player has "discovered" —
    /// only discovered cards appear in draft pools. Starter cards are
    /// added here automatically during SeedStarterDeck. New cards enter
    /// the pool when first drafted or found.
    /// </summary>
    public List<string> UnlockedCardBlueprintIds = new();

    // ── Faction reputation ──────────────────────────────────────────────
    public System.Collections.Generic.Dictionary<string, int> FactionReputation = new();

    // ── Region memory (which hexes revealed per region) ─────────────────
    public System.Collections.Generic.Dictionary<string, RegionMemorySaveData> RegionMemory = new();

    // ── Lore / progression flags ────────────────────────────────────────
    public List<string> UnlockedLoreEntries = new();
    public List<string> CompletedEvents = new();

    // ── Phase 3+ stubs ───────────────────────────────────────────────────
    public string CharterAlignment = "";
    public int SeasonalThreatLevel = 0;
    public System.Collections.Generic.Dictionary<string, int> FragmentProgress = new();
}

// ────────────────────────────────────────────────────────────────────────────
// Persistent deck types
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The player's full card collection and active-deck configuration,
/// persisted between runs as part of <see cref="GuildSaveData"/>.
/// </summary>
public class PlayerDeckSave
{
    /// <summary>Every card the player owns, across all copies.</summary>
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
    /// Matches <see cref="CardBlueprint.Id"/>: "school:TopName|BotName".
    /// Used to look up the blueprint in CardDatabase at run start.
    /// </summary>
    public string BlueprintId = "";

    /// <summary>
    /// Unique per owned copy. Generated once as Guid.NewGuid().ToString("N").
    /// Used as the key in ActiveDeckInstanceIds.
    /// </summary>
    public string InstanceId = "";

    /// <summary>
    /// 0 = base, 1 = Refined (+), 2 = Mastered (++).
    /// Applied by PlayerDeckService when instantiating the card for a run.
    /// </summary>
    public int UpgradeTier = 0;

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

// ────────────────────────────────────────────────────────────────────────────
// Region memory types
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Save data for explored region state.</summary>
public class RegionMemorySaveData
{
    public string RegionId = "";
    public float ExplorationPercent = 0f;
    public List<RevealedHexData> RevealedHexes = new();
    public List<string> CompletedLandmarks = new();
    public System.Collections.Generic.Dictionary<string, string> FactionControl = new();
}

/// <summary>Minimal data for a single revealed hex.</summary>
public class RevealedHexData
{
    public int Q;
    public int R;
    public string FogState = "Revealed";
}
