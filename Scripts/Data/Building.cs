using System.Collections.Generic;

/// <summary>
/// Defines a campus building — its identity, tier, requirements, and effects.
/// Loaded from Data/Buildings/*.json. Runtime state lives in GuildSaveData.
/// </summary>
public class Building
{
    // ── Identity ────────────────────────────────────────────────────────
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public string Category = "";        // Core, Magic, Economy, Reputation, School
    public string SchoolAffinity = "";  // empty = any school

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
    public List<string> UnlocksFeatures = new();  // string flags for future features
}