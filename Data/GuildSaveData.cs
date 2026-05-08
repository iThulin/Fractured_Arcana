using System.Collections.Generic;

/// <summary>
/// Central save data for one guild. Serialized to JSON.
/// Every field that persists between runs lives here.
/// 
/// VERSION HISTORY:
/// 1 - Initial schema (Phase 2)
/// </summary>
public class GuildSaveData
{
    // ── Meta ────────────────────────────────────────────────────────────
    public int SaveVersion = 1;
    public string GuildName = "New Guild";
    public string CreatedAt = "";
    public string LastPlayedAt = "";

    // ── Wizard ──────────────────────────────────────────────────────────
    public string SelectedSchool = "Elementalist";

    // ── Region ──────────────────────────────────────────────────────────
    public string CurrentRegionId = "frontier_wilds"; 

    // ── Economy ─────────────────────────────────────────────────────────
    public int Gold = 0;

    // ── Run stats ───────────────────────────────────────────────────────
    public int TotalRuns = 0;
    public int RunsWon = 0;
    public int RunsLost = 0;
    public int TotalGoldEarned = 0;
    public int TotalEncountersWon = 0;

    // ── Companions ──────────────────────────────────────────────────────
    public List<CompanionSaveData> Companions = new();

    // ── Buildings ────────────────────────────────────────────────────────
    public List<BuildingSaveData> Buildings = new();

    // ── Faction reputation ──────────────────────────────────────────────
    public Dictionary<string, int> FactionReputation = new();

    // ── Region memory (which hexes revealed per region) ─────────────────
    public Dictionary<string, RegionMemorySaveData> RegionMemory = new();

    // ── Lore / progression flags ────────────────────────────────────────
    public List<string> UnlockedLoreEntries = new();
    public List<string> CompletedEvents = new();

    // ── Phase 3+ stubs (present in schema, unused until later) ──────────
    public string CharterAlignment = "";        // Order, Chaos, Balance
    public int SeasonalThreatLevel = 0;
    public Dictionary<string, int> FragmentProgress = new();
}

/// <summary>
/// Save data for a single companion.
/// </summary>
public class CompanionSaveData
{
    public string Id = "";
    public string Name = "";
    public string School = "";
    public string PersonalityTrait = "";
    public int Loyalty = 50;            // 0-100
    public int ArcStage = 0;            // 0 = not started, 1-3 = in progress, 4 = complete
    public bool IsRecruited = false;
    public bool IsPermadead = false;
    public List<string> UnlockedCards = new();
}

/// <summary>
/// Save data for a single campus building.
/// </summary>
public class BuildingSaveData
{
    public string Id = "";
    public string Name = "";
    public int Tier = 0;                // 0 = not built, 1-3 = built tiers
    public string Category = "";        // Core, Magic, Economy, Reputation, School
    public string SchoolAffinity = "";  // empty for non-school buildings
}

/// <summary>
/// Save data for explored region state.
/// </summary>
public class RegionMemorySaveData
{
    public string RegionId = "";
    public float ExplorationPercent = 0f;
    public List<RevealedHexData> RevealedHexes = new();
    public List<string> CompletedLandmarks = new();
    public Dictionary<string, string> FactionControl = new();
}

/// <summary>
/// Minimal data for a single revealed hex.
/// </summary>
public class RevealedHexData
{
    public int Q;
    public int R;
    public string FogState = "Revealed"; // Revealed or Silhouette
}