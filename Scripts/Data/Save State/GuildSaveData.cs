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
//   v2 — added PlayerDeck, ArcaneSplinters, UnlockedCardBlueprintIds
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
    public int SaveVersion = 8;
    public string GuildName = "New Guild";
    public string CreatedAt = "";
    public string LastPlayedAt = "";

    // ── Wizard ──────────────────────────────────────────────────────────
    public string SelectedSchool = "Elementalist";
    public string WizardName = "Wizard";

    // ── Region ──────────────────────────────────────────────────────────
    public string CurrentRegionId = "frontier_wilds";

    // ── Economy ─────────────────────────────────────────────────────────
    public int Gold = 0;

    /// <summary>
    /// Upgrade currency. Earned from combat at a low rate; spent at
    /// Training Grounds to upgrade OwnedCard.UpgradeTier.
    /// </summary>
    public int ArcaneSplinters = 0;

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
            if (b.Id == buildingId)
                return b.Tier;
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
    /// Minimum cards that must remain in the deck. Default 10.
    /// Cannot disenchant below this floor.
    /// </summary>
    public int MinDeckSize = 10;

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

    // ── Honored Dead records ─────────────────────────────────────────────
    /// <summary>
    /// Records of units who died in combat, used to summon spirits in the Ossuary.
    /// Appended to when units die, but never removed or modified (except for the 
    /// HasBeenSummoned flag) — the full history of deaths is preserved.
    /// </summary>
    public List<HonoredDeadRecord> HonoredDead = new();

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
    /// Tracks the number of times a card has been cast in the campaign
    /// Used as a resource for card mastery.
    public int CastCount = 0;

    // ── Migration shims ───────────────────────────────────────────────
    // Read during v4→v5 migration from old saves. Suppressed on write
    // when at default values so they don't appear in new save files.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int UpgradeTier = 0;

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string ChosenBranch = null;

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
