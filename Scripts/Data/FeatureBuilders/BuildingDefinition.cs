using System.Collections.Generic;

// ============================================================
// BuildingDefinition.cs
//
// Purpose:        Campus building model — identity, tier ladder,
//                 per-tier costs and effect bonuses. Loaded from
//                 Data/Buildings/*.json; runtime upgrade state
//                 lives in GuildSaveData.
// Layer:          Data
// Collaborators:  BuildingDatabase.cs (registry),
//                 BuildingEffectApplier.cs (consumes Tiers
//                 at run-start), CampusScreen.cs (UI),
//                 GuildSaveData.cs (CurrentTier persisted here)
// See:            README §4.4 (Adding a Building)
// ============================================================

/// <summary>One relative hex offset (q, r) in a building's authored footprint, before
/// rotation/anchor are applied. Godot-free, matching this file's plain-data convention —
/// CampusHexGrid does the axial math (rotation, anchoring) at the boundary.</summary>
public class HexOffset
{
    public int Q = 0;
    public int R = 0;
}

/// <summary>Defines a campus building — its identity, tier ladder, and current upgrade state. Loaded from Data/Buildings/*.json. Runtime state (CurrentTier, IsUnlocked) lives in GuildSaveData.</summary>
public class Building
{
    // ── Identity ────────────────────────────────────────────────────────
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public string Category = "";        // Core, Magic, Economy, Reputation, School
    public string SchoolAffinity = "";  // empty = any school

    // ── Footprint (campus hex map) ───────────────────────────────────────
    /// <summary>Hex offsets (relative to the placement anchor, before rotation) this
    /// building occupies on the campus map. Always includes (0,0) explicitly — there's
    /// no implicit anchor hex, so a building with no "footprint" key in its JSON gets
    /// this single-hex default automatically and behaves exactly as before. See
    /// campus_siege_and_defense_v1.docx §4-5.</summary>
    public List<HexOffset> Footprint = new() { new HexOffset { Q = 0, R = 0 } };

    /// <summary>When true, BuildingDatabase.EnsureBuildings seeds this building already
    /// built (Tier 1) and sited at the campus center (Q=0, R=0, Rotation=0, IsPlaced =
    /// true) instead of the normal Tier-0/unplaced backfill — for buildings the guild
    /// starts with rather than constructs, e.g. the Teleport Sigil. Only applies the
    /// first time a save is missing this building's entry; never re-applied afterward,
    /// so the player is free to move or lose it like any other placed building.</summary>
    public bool StartsBuiltAtCampusCenter = false;

    // ── Tiers ───────────────────────────────────────────────────────────
    public int MaxTier = 3;
    public List<BuildingTier> Tiers = new();

    // ── State (runtime, stored in GuildSaveData) ─────────────────────────
    public int CurrentTier = 0;         // 0 = not built
    public bool IsUnlocked = true;      // false = gated behind other buildings/events
    public string UnlockRequirement = "";
}

/// <summary>
/// Data for a single tier of a building.
/// </summary>
public class BuildingTier
{
    public int Tier = 1;
    public string Description = "";     // what this tier adds
    public int GoldCost = 100;

    /// <summary>Materials cost for this tier. -1 (default/unset) means "derive from
    /// GoldCost at the standard 3:1 ratio" (see EffectiveMaterialsCost) — so none of
    /// the existing Data/Buildings/*.json files need editing. Set explicitly only when
    /// a building should deviate from the standard ratio.</summary>
    public int MaterialsCost = -1;

    /// <summary>The materials cost actually charged: MaterialsCost if explicitly set
    /// on the tier, otherwise GoldCost * 3 (the standard ratio).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveMaterialsCost => MaterialsCost >= 0 ? MaterialsCost : GoldCost * 3;

    public List<string> RequiredBuildings = new();  // other building ids required

    // ── Effects ──────────────────────────────────────────────────────────
    // These are read by BuildingEffectApplier at run start / campus load.
    public int BonusStartingHP = 0;
    public int BonusStartingSteps = 0;
    public int BonusStartingGold = 0;
    public int BonusNegotiationTokens = 0;  // added to token pool
    public string BonusTokenType = "";       // which token type
    public int PreRevealHexCount = 0;        // hexes revealed at run start
    public bool UnlocksCardLibrary = false;  // Phase 2 stub
    public int DisenchantSplinterBonus = 0;

    /// <summary>Reduces the gold cost to slot a card into the active deck.
    /// Stacks across tiers. Applied by BuildingEffectApplier.</summary>
    public int SlotCostReduction = 0;
    public List<string> UnlocksFeatures = new();  // string flags for future features
}