using Godot;

// ============================================================
// EncounterContext.cs
//
// Purpose:        Data passed from overworld → combat scene via
//                 the static carrier pattern (mirrors
//                 NegotiationContext). Holds POI/terrain inputs,
//                 result fields, and a separate
//                 EncounterContextCarrier static singleton for
//                 the full EncounterDefinition.
// Layer:          Data
// Collaborators:  EncounterRouter.cs (writes), CombatManager.cs
//                 (reads inputs, writes results),
//                 EncounterDefinition.cs (carried separately)
// See:            README §3 — scene swap pattern
// ============================================================

/// <summary>Per-scene-swap carrier mirroring <see cref="NegotiationContext"/>. Input fields (terrain, POI) set by the router; result fields set by combat on completion.</summary>
public class EncounterContext
{
    // ── Overworld source context ─────────────────────────────────────────
    public OverworldHex.POIType SourcePOI;
    public OverworldHex.TerrainType SourceTerrain;

    // ── Legacy fields (kept for compatibility) ───────────────────────────
    public int EnemyCount = 3;
    public int PlayerCount = 2;

    // ── Results (filled after combat completes) ───────────────────────────
    public bool PlayerWon;
    public int GoldReward;
    public int DamageTaken;
}

/// <summary>
/// Static carrier — set by EncounterRouter before scene swap,
/// read by CombatManager at spawn time.
/// </summary>
public static class EncounterContextCarrier
{
    public static EncounterDefinition Current { get; private set; } = null;
    public static bool HasEncounter => Current != null;

    // Overworld source context, routed through to combat-map generation.
    public static string SourceTerrain { get; private set; } = "";
    public static EncounterTier Tier { get; private set; } = EncounterTier.Battle;

    public static void Set(EncounterDefinition def) => Current = def;

    public static void SetContext(string sourceTerrain, EncounterTier tier)
    {
        SourceTerrain = sourceTerrain ?? "";
        Tier = tier;
    }

    public static void Clear()
    {
        Current = null;
        SourceTerrain = "";
        Tier = EncounterTier.Battle;
    }
}