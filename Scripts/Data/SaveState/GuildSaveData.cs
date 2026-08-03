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
    [JsonIgnore]
    public int SaveVersion
    { get => Ledger.SaveVersion; set { Ledger.SaveVersion = value; Cycle.SaveVersion = value; } }

    [JsonIgnore]
    public string GuildName
    { get => Ledger.GuildName; set => Ledger.GuildName = value; }

    [JsonIgnore]
    public string CreatedAt
    { get => Ledger.CreatedAt; set => Ledger.CreatedAt = value; }

    [JsonIgnore]
    public string LastPlayedAt
    { get => Ledger.LastPlayedAt; set => Ledger.LastPlayedAt = value; }

    // ── Campaign state ───────────────────────────────────────────────────
    [JsonIgnore]
    public CampaignState Campaign
    { get => Cycle.Campaign; set => Cycle.Campaign = value; }

    // ── Wizard ──────────────────────────────────────────────────────────
    [JsonIgnore]
    public string SelectedSchool
    { get => Cycle.SelectedSchool; set => Cycle.SelectedSchool = value; }

    [JsonIgnore]
    public string WizardName
    { get => Cycle.WizardName; set => Cycle.WizardName = value; }

    // ── Region ──────────────────────────────────────────────────────────
    [JsonIgnore]
    public string CurrentRegionId
    { get => Cycle.CurrentRegionId; set => Cycle.CurrentRegionId = value; }

    // ── Economy ─────────────────────────────────────────────────────────
    [JsonIgnore]
    public int Gold
    { get => Cycle.Gold; set => Cycle.Gold = value; }

    /// <summary>Building construction/upgrade cost's second resource, alongside Gold —
    /// standard ratio is 3 Materials : 1 Gold (BuildingTier.EffectiveMaterialsCost).
    /// Same tier as Gold (CycleState, resets each cycle) for consistency. NOTE: this
    /// shim needs a matching `public int BuildMaterials = 0;` field added to
    /// CycleState itself — not yet done, CycleState.cs wasn't available to edit.</summary>
    public int BuildMaterials
    { get => Cycle.BuildMaterials; set => Cycle.BuildMaterials = value; }

    [JsonIgnore]
    public int ArcaneSplinters
    { get => Cycle.ArcaneSplinters; set => Cycle.ArcaneSplinters = value; }

    // ── Run stats ───────────────────────────────────────────────────────
    [JsonIgnore]
    public int TotalRuns
    { get => Cycle.TotalRuns; set => Cycle.TotalRuns = value; }

    [JsonIgnore]
    public int RunsWon
    { get => Cycle.RunsWon; set => Cycle.RunsWon = value; }

    [JsonIgnore]
    public int RunsLost
    { get => Cycle.RunsLost; set => Cycle.RunsLost = value; }

    [JsonIgnore]
    public int TotalGoldEarned
    { get => Cycle.TotalGoldEarned; set => Cycle.TotalGoldEarned = value; }

    [JsonIgnore]
    public int TotalEncountersWon
    { get => Cycle.TotalEncountersWon; set => Cycle.TotalEncountersWon = value; }

    // ── Companions ──────────────────────────────────────────────────────
    [JsonIgnore]
    public List<Companion> Companions
    { get => Cycle.Companions; set => Cycle.Companions = value; }

    [JsonIgnore]
    public List<string> ActivePartyCompanionIds
    { get => Cycle.ActivePartyCompanionIds; set => Cycle.ActivePartyCompanionIds = value; }

    [JsonIgnore]
    public int MaxPartySize
    { get => Cycle.MaxPartySize; set => Cycle.MaxPartySize = value; }

    // ── Training Grounds helpers (read the eternal campus) ──────────────
    [JsonIgnore] public int TrainingGroundsTier => GetBuildingTier("training_grounds");

    [JsonIgnore] public int MartialStanceSlots => TrainingGroundsTier;

    [JsonIgnore]
    public int FighterBaseAP => TrainingGroundsTier switch
    {
        0 => 3,
        1 => 4,
        2 => 4,
        3 => 5,
        _ => 3,
    };

    [JsonIgnore]
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
        foreach (var b in Ledger.Buildings)
            if (b.Id == buildingId)
                return b.Tier;
        return 0;
    }

    // ── Equipment armory ─────────────────────────────────────────────────
    [JsonIgnore]
    public ArmoryData Armory
    { get => Cycle.Armory; set => Cycle.Armory = value; }

    // ── Buildings (the eternal campus) ───────────────────────────────────
    [JsonIgnore]
    public List<BuildingSaveData> Buildings
    { get => Ledger.Buildings; set => Ledger.Buildings = value; }

    // ── Persistent deck ──────────────────────────────────────────────────
    [JsonIgnore]
    public PlayerDeckSave PlayerDeck
    { get => Cycle.PlayerDeck; set => Cycle.PlayerDeck = value; }

    [JsonIgnore]
    public int MinDeckSize
    { get => Cycle.MinDeckSize; set => Cycle.MinDeckSize = value; }

    /// <summary>Discovered blueprints — knowledge, so it lives in the loom.</summary>
    [JsonIgnore]
    public List<string> UnlockedCardBlueprintIds
    { get => Ledger.UnlockedCardBlueprintIds; set => Ledger.UnlockedCardBlueprintIds = value; }

    // ── Faction reputation ──────────────────────────────────────────────
    [JsonIgnore]
    public Dictionary<string, int> FactionReputation
    { get => Cycle.FactionReputation; set => Cycle.FactionReputation = value; }

    // ── Honored Dead (the loom remembers the dead) ──────────────────────
    [JsonIgnore]
    public List<HonoredDeadRecord> HonoredDead
    { get => Ledger.HonoredDead; set => Ledger.HonoredDead = value; }

    // ── Lore / progression flags ────────────────────────────────────────
    [JsonIgnore]
    public List<string> UnlockedLoreEntries
    { get => Ledger.UnlockedLoreEntries; set => Ledger.UnlockedLoreEntries = value; }

    [JsonIgnore]
    public List<string> CompletedQuestIds
    { get => Ledger.CompletedQuestIds; set => Ledger.CompletedQuestIds = value; }

    [JsonIgnore]
    public List<string> CompletedEvents
    { get => Cycle.CompletedEvents; set => Cycle.CompletedEvents = value; }

    // ── Timeline story/chain flags (namespaced away from CompletedEvents) ─
    [JsonIgnore]
    public HashSet<string> WorldFlags
    { get => Cycle.WorldFlags; set => Cycle.WorldFlags = value; }

    /// <summary>True if the timeline flag is set.</summary>
    public bool HasFlag(string flag) =>
        Cycle.HasFlag(flag) || (Ledger?.MetaNarrativeFlags?.Contains(flag) ?? false);

    /// <summary>Set a timeline flag (idempotent). Returns true if newly added.</summary>
    public bool SetFlag(string flag) => Cycle.SetFlag(flag);

    // ── Phase 3+ stubs ───────────────────────────────────────────────────
    [JsonIgnore]
    public string CharterAlignment
    { get => Cycle.CharterAlignment; set => Cycle.CharterAlignment = value; }

    [JsonIgnore]
    public int SeasonalThreatLevel
    { get => Cycle.SeasonalThreatLevel; set => Cycle.SeasonalThreatLevel = value; }

    [JsonIgnore]
    public Dictionary<string, int> FragmentProgress
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
    /// <summary>Every card in the REAL owned collection this cycle. Serialized under
    /// the original "cards" key for save compatibility. Accessed via the
    /// <see cref="Cards"/> routing property, never directly.</summary>
    [JsonPropertyName("cards")]
    public List<OwnedCard> RealCards = new();

    /// <summary>The debug/scratch owned collection — the cards that back the debug deck
    /// only. Separate so seeding or resetting the scratch deck never duplicates into the
    /// real collection. New key; old saves default it empty.</summary>
    public List<OwnedCard> DebugCards = new();

    /// <summary>The live owned collection every call site uses (hydration, deck editor,
    /// crafting). Routes to the debug collection when <see cref="UseDebugDeck"/> is set,
    /// else the real one. Not serialized — the two backing lists are.</summary>
    [JsonIgnore]
    public List<OwnedCard> Cards
    {
        get => UseDebugDeck ? DebugCards : RealCards;
        set
        {
            if (UseDebugDeck) DebugCards = value ?? new List<OwnedCard>();
            else RealCards = value ?? new List<OwnedCard>();
        }
    }

    /// <summary>InstanceIds of cards slotted into the REAL active run deck.
    /// Serialized under the original "activeDeckInstanceIds" key so existing saves
    /// keep their deck. Min 10 / Max 20. Read/written through the
    /// <see cref="ActiveDeckInstanceIds"/> routing property, never directly.</summary>
    [JsonPropertyName("activeDeckInstanceIds")]
    public List<string> RealActiveDeckInstanceIds = new();

    /// <summary>InstanceIds of the debug/scratch deck used only by the combat debug
    /// launcher. Kept separate so testing a deck never edits the real one. New saves
    /// gain this key; old saves default it empty.</summary>
    public List<string> DebugDeckInstanceIds = new();

    /// <summary>When set, <see cref="ActiveDeckInstanceIds"/> routes to the debug deck
    /// instead of the real one — so the existing deck editor and combat both target the
    /// scratch deck with zero call-site changes. Static and NOT serialized: defaults
    /// false, resets on quit and whenever the campus loads. Set by CombatDebugLauncher.</summary>
    [JsonIgnore]
    public static bool UseDebugDeck = false;

    /// <summary>The live deck list every existing call site reads and writes. Routes to
    /// the debug deck when <see cref="UseDebugDeck"/> is set, else the real deck. Not
    /// serialized — the two backing lists above are.</summary>
    [JsonIgnore]
    public List<string> ActiveDeckInstanceIds
    {
        get => UseDebugDeck ? DebugDeckInstanceIds : RealActiveDeckInstanceIds;
        set
        {
            if (UseDebugDeck) DebugDeckInstanceIds = value ?? new List<string>();
            else RealActiveDeckInstanceIds = value ?? new List<string>();
        }
    }

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

    // ── Campus map placement ─────────────────────────────────────────────
    // Single source of truth for where this building sits on the campus
    // hex map (CampusMapSaveData.cs). A building can be Tier > 0 (owned,
    // e.g. auto-unlocked when a companion of that school joins per
    // guild_campus_v2.docx §5) without yet being sited — IsPlaced gates
    // that. Old saves default to Q=0, R=0, IsPlaced=false; CampusGridManager
    // .LoadFromSave skips any building that is !IsPlaced or Tier <= 0, so an
    // owned-but-unplaced building needs player siting and is never auto-placed
    // at the origin.
    public int Q = 0;
    public int R = 0;
    public bool IsPlaced = false;

    /// <summary>True only when this building both exists (Tier > 0 — paid for /
    /// unlocked) AND is sited on the campus map (IsPlaced). Owning a building without
    /// siting it grants nothing — this is the single flag anything gating building
    /// EFFECTS (BuildingEffectApplier, etc.) should check, rather than Tier alone.
    /// Tier > 0 && !IsPlaced means "owned, not yet functional."</summary>
    public bool IsFunctional => Tier > 0 && IsPlaced;

    /// <summary>0-5, one of the six hex rotation steps applied to the building
    /// template's Footprint before anchoring at Q/R. Not yet exposed in the placement
    /// UI (BeginPlacingBuilding always places at rotation 0) — the field exists so the
    /// footprint math and save schema are ready before that UI lands.</summary>
    public int Rotation = 0;

    // ── Integrity (combat damage state) ──────────────────────────────────
    // Flat baseline for now — 20 HP regardless of tier or building type.
    // Per-tier/per-building scaling is an open question (campus_siege_and_
    // defense_v1 §4), not decided yet; don't assume it here.
    public int MaxIntegrity = 20;
    public int CurrentIntegrity = 20;

    /// <summary>Applies combat damage. Clamps at 0 and, on reaching 0, destroys the
    /// building: Tier resets to 0, IsPlaced to false (campus_siege_and_defense_v1 §4b —
    /// "destroyed" and "not built" are the same state on the building record; the
    /// difference lives on the tile, which the caller must separately mark Rubble via
    /// CampusMapSaveData/CampusGridManager — this method only knows about the building).
    /// Returns true the turn it's destroyed (false on every other call, including
    /// once already destroyed), so the caller knows to react exactly once.</summary>
    public bool ApplyDamage(int amount)
    {
        if (CurrentIntegrity <= 0 || amount <= 0)
            return false;

        CurrentIntegrity = System.Math.Max(0, CurrentIntegrity - amount);
        if (CurrentIntegrity > 0)
            return false;

        Tier = 0;
        IsPlaced = false;
        return true;
    }
}
