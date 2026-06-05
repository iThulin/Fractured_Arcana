using Godot;
using System;

// ============================================================
// EncounterRouter.cs
//
// Purpose:        Orchestrates overworld ↔ combat scene swaps.
//                 Saves overworld state (HP, gold, party coord,
//                 RNG seed) before switching to combat; restores
//                 it after. Full scene swap rather than additive
//                 loading to avoid camera/viewport conflicts.
// Layer:          System
// Collaborators:  EncounterContext.cs (data carrier),
//                 OverworldRunManager.cs (state source),
//                 CombatManager.cs (combat-side consumer),
//                 NegotiationManager.cs (alternate target),
//                 NarrativeEncounterPanel.cs (alternate target)
// See:            README §3 — Architecture (scene swap pattern)
// ============================================================

/// <summary>Process-wide router for transitions between overworld, combat, narrative, and negotiation scenes. Owns the saved overworld state across scene swaps and exposes a deterministic seed so the map regenerates identically on return.</summary>
public partial class EncounterRouter : Node
{
    [Export] public string CombatScenePath = "res://Scenes/Combat/Battlefield.tscn";
    [Export] public string OverworldScenePath = "res://Scenes/Overworld/OverworldScene.tscn";

    public static EncounterRouter Instance { get; private set; }

    public bool HasPendingReturn { get; set; } = false;
    public bool CombatWon { get; set; }
    public int GoldReward { get; set; }
    public int DamageTaken { get; set; }
    public int SplinterReward { get; set; }
    public int SavedSplinterEarned { get; set; }
    private EncounterTier _currentTier = EncounterTier.Battle;

    public int SavedStepsRemaining;
    public int SavedCurrentHP;
    public int SavedGoldEarned;
    public int SavedEncountersWon;
    public Vector2I SavedPartyCoord;
    public Vector2I SavedCombatHexCoord;

    // ── Seed for deterministic map regeneration after combat ────────────
    public int SavedRunSeed;
    public bool HasSavedSeed = false;

    public System.Collections.Generic.Dictionary<Vector2I, OverworldHex.FogState> SavedFogStates = new();
    public System.Collections.Generic.Dictionary<Vector2I, bool> SavedPOIConsumed = new();

    // Patrol positions saved before entering combat; restored on return.
    public System.Collections.Generic.List<Vector2I> SavedPatrolPositions = new();
    public string SavedPatrolArchmageId = "";

    public override void _Ready()
    {
        Instance = this;
        // This node persists across scene changes
        ProcessMode = ProcessModeEnum.Always;
    }

    /// <summary>
    /// Called by RunManager to start a combat encounter.
    /// Saves overworld state, then swaps to the combat scene.
    /// </summary>
    public void StartCombat(
        OverworldRunManager runManager,
        Vector2I combatHexCoord,
        EncounterTier tier = EncounterTier.Battle,
        string terrainType = "Grassland")
    {
        // ── Save overworld state ────────────────────────────────────────
        SavedStepsRemaining = runManager.StepsRemaining;
        SavedCurrentHP = runManager.CurrentHP;
        SavedGoldEarned = runManager.GoldEarned;
        SavedSplinterEarned = runManager.SplinterEarned;
        SavedEncountersWon = runManager.EncountersWon;
        SavedPartyCoord = runManager.GetPartyCoord();
        SavedCombatHexCoord = combatHexCoord;

        // Save fog and POI state for every hex
        SavedFogStates.Clear();
        SavedPOIConsumed.Clear();
        var grid = runManager.GetGrid();
        foreach (var kvp in grid.Hexes)
        {
            SavedFogStates[kvp.Key] = kvp.Value.Fog;
            SavedPOIConsumed[kvp.Key] = kvp.Value.POIConsumed;
        }

        HasPendingReturn = false;

        GD.Print($"EncounterRouter: Saved overworld state. Party at {SavedPartyCoord}, " +
                 $"Steps: {SavedStepsRemaining}, HP: {SavedCurrentHP}");

        // ── Build encounter definition from region pool ──────────────────
        string regionId = runManager.GetRegionId();
        float diffMult = runManager.GetRegion()?.EnemyDifficultyMult ?? 1.0f;

        var encounterDef = EncounterPoolLoader.Pick(regionId, tier, terrainType, diffMult);
        EncounterContextCarrier.Set(encounterDef);
        EncounterContextCarrier.SetContext(terrainType, tier);

        GD.Print($"EncounterRouter: Encounter set — {encounterDef.DisplayName} " +
                 $"({encounterDef.Tier}, {encounterDef.Enemies.Count} enemies)");

        _currentTier = tier;
        // ── Swap to combat scene ────────────────────────────────────────
        GetTree().ChangeSceneToFile(CombatScenePath);
    }

    /// <summary>
    /// Called by GameRunner (via signal) when combat ends.
    /// Stores the result and swaps back to the overworld.
    /// </summary>
    public void OnCombatFinished(bool playerWon)
    {
        EncounterContextCarrier.Clear();
        CombatWon = playerWon;
        GoldReward = CalculateGoldRewardForTier(_currentTier);
        SplinterReward = SplinterDropTable.Combat(_currentTier);
        HasPendingReturn = true;

        GD.Print($"EncounterRouter: Combat finished. Won: {playerWon}. " +
                $"Gold: {GoldReward}, Splinters: {SplinterReward}.");

        // Flush any mid-run state changes (cast counts, etc.) to disk
        SaveManager.SaveIfDirty();

        if (playerWon)
        {
            // Show card reward screen — it routes to overworld when done
            GetTree().ChangeSceneToFile("res://Scenes/UI/CardRewardScreen.tscn");
        }
        else
        {
            // Loss: skip reward, return to overworld after brief delay
            GetTree().CreateTimer(2.0f).Timeout += () =>
                GetTree().ChangeSceneToFile(OverworldScenePath);
        }
    }

    /// <summary>
    /// Called by OverworldRunManager.CommitCombat when bypassing StartCombat
    /// with a pre-built EncounterDefinition from the scout panel.
    /// </summary>
    public void SetCurrentTier(EncounterTier tier) => _currentTier = tier;

    private int CalculateGoldRewardForTier(EncounterTier tier) => tier switch
    {
        EncounterTier.Skirmish => (int)GD.RandRange(8, 15),
        EncounterTier.Battle => (int)GD.RandRange(18, 30),
        EncounterTier.Siege => (int)GD.RandRange(40, 60),
        _ => (int)GD.RandRange(15, 25),
    };

    public static HexGridManager.MapDensityPreset DensityForTier(EncounterTier tier) => tier switch
    {
        EncounterTier.Skirmish => HexGridManager.MapDensityPreset.Sparse,
        EncounterTier.Battle => HexGridManager.MapDensityPreset.Standard,
        EncounterTier.Siege => HexGridManager.MapDensityPreset.Dense,
        _ => HexGridManager.MapDensityPreset.Standard,
    };
}