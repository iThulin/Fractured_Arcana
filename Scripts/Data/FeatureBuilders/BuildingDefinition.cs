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
/// CampusGridManager.GetFootprintHexes does the axial math (rotation, anchoring) at the
/// boundary.</summary>
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

    // ── Starting placement ───────────────────────────────────────────────
    /// <summary>Authored campus anchor for a building the guild STARTS with rather than
    /// constructs. Null (the default, and what you get when the JSON omits the key) means
    /// the normal Tier-0/unplaced backfill. When set, BuildingDatabase.EnsureBuildings
    /// seeds the entry already built (Tier 1) and sited here at Rotation 0.
    ///
    /// Replaces the old <c>StartsBuiltAtCampusCenter</c> bool, which hardcoded (0,0) and
    /// therefore could only ever describe ONE building — every additional starting
    /// building would have stacked on the same hex.
    ///
    /// For a NON-foundational building this is applied once, on first creation only, so a
    /// player who later moves or loses it is never overridden back. Foundational buildings
    /// are re-asserted on every load — see <see cref="IsFoundational"/>.</summary>
    public HexOffset StartsBuiltAt = null;

    /// <summary>True for the small set of buildings that host a system the player cannot
    /// play the game without (roster, loadout, deck, deployment, guild identity). These are
    /// free, start built, and are REPAIRED on every load rather than seeded once: tier is
    /// floored at 1, an unplaced entry is re-sited at <see cref="StartsBuiltAt"/>, and zero
    /// integrity is restored. Without that, a foundational building lost to a siege or left
    /// at tier 0 by an older save would strand its system permanently unreachable once the
    /// campus map replaces the tab bar.
    ///
    /// Buildings live on EternalLedger.Buildings, which SaveManager.BeginNewCycle does not
    /// touch, so a foundational building seeded once already survives cycle resets; the
    /// repair pass is what makes that guarantee hold under damage and schema drift.
    ///
    /// Requires <see cref="StartsBuiltAt"/> to be set — a foundational building with no
    /// anchor has nowhere to be repaired to, and EnsureBuildings logs an error for it.</summary>
    public bool IsFoundational = false;

    /// <summary>Which campus system clicking this building opens — "guild", "companions",
    /// "expedition", "armory", "deck", or empty for a building that opens nothing.
    ///
    /// Resolved by <see cref="CampusLocationRegistry.ForSystemKey"/>, so adding a door to the
    /// campus map is a JSON edit rather than a code change. An unknown key makes the building
    /// inert rather than throwing. Also drives the map name label's colour: a building with a
    /// key is drawn in <c>UITheme.BuildingLabelDoor</c>, one without in
    /// <c>UITheme.BuildingLabelPlain</c>.</summary>
    public string HostsSystem = "";

    /// <summary>Optional short name drawn on the campus map. Leave unset and
    /// <see cref="EffectiveMapLabel"/> derives one from <see cref="Name"/>; set it only when
    /// the derived name is too long to sit on a hex.</summary>
    public string MapLabel = "";

    /// <summary>The text actually drawn on the map: <see cref="MapLabel"/> when authored,
    /// otherwise <see cref="Name"/> with a leading "The " stripped, so "The Gatehouse Yard"
    /// reads as "Gatehouse Yard" without needing a label authored for every building.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveMapLabel =>
        !string.IsNullOrEmpty(MapLabel) ? MapLabel
        : (Name != null && Name.StartsWith("The ", System.StringComparison.OrdinalIgnoreCase)
            ? Name.Substring(4)
            : Name ?? "");

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

    /// <summary>Flavor name for this tier ("Basic Training Grounds", "The
    /// Unbinding Floor"). The building JSONs have carried "displayName" all
    /// along — this field just lets the CamelCase loader finally parse it
    /// (2026-08-13, for the city-view upgrade strip). Empty = untitled tier.</summary>
    public string DisplayName = "";

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

    /// <summary>(2026-08-13) Campus-persistent party-size growth — the §4a
    /// "party-size growth via campus" lever, finally given a home (the Grand
    /// Hall's tiers). Accumulated across built tiers by
    /// BuildingEffectApplier.ApplyCampusEffects onto CycleState.MaxPartySize.</summary>
    public int PartySizeBonus = 0;

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