using Godot;
using System;
using System.Collections.Generic;

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
    [Export] public string OverworldScenePath = "res://Scenes/Overworld/ExpeditionScene.tscn";

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

    /// <summary>True when the pending combat was a patrol ambush (C4 deed).</summary>
    public bool SavedCombatWasPatrolAmbush = false;

    /// <summary>Owner archmage of the ambushing patrol ("wilds" for the
    /// generic pursuer).</summary>
    public string SavedCombatPatrolArchmageId = "";

    /// <summary>Non-empty when the pending combat is a fragment guardian (the
    /// fragment key). On a win, ExpeditionManager sets &lt;key&gt;_trial_passed.</summary>
    public string SavedCombatGuardianKey = "";

    // ── Seed for deterministic map regeneration after combat ────────────
    public int SavedRunSeed;
    public bool HasSavedSeed = false;

    public System.Collections.Generic.Dictionary<Vector2I, OverworldHex.FogState> SavedFogStates = new();
    public System.Collections.Generic.Dictionary<Vector2I, bool> SavedPOIConsumed = new();

    // Patrol positions saved before entering combat; restored on return.
    public System.Collections.Generic.List<Vector2I> SavedPatrolPositions = new();
    public List<int> SavedPatrolCooldowns = new();
    public string SavedPatrolArchmageId = "";

    public override void _Ready()
    {
        Instance = this;
        // This node persists across scene changes
        ProcessMode = ProcessModeEnum.Always;
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
            // Adept ruling (2026-07-10): the generalist never drafts on the road.
            // Instead the splinter reward is DOUBLED — the Academy stipend — and
            // deck building happens back at the campus (upgrades now; a splinter
            // card-acquisition screen is the planned follow-on).
            if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave?.SelectedSchool,
                    ignoreCase: true, out var schoolForReward)
                && schoolForReward == CardSchool.Adept)
            {
                SplinterReward *= 2;
                GD.Print($"EncounterRouter: Adept stipend — no draft, splinters doubled to {SplinterReward}.");
                GetTree().CreateTimer(2.0f).Timeout += () =>
                    GetTree().ChangeSceneToFile(OverworldScenePath);
            }
            else
            {
                // Show card reward screen — it routes to overworld when done
                GetTree().ChangeSceneToFile("res://Scenes/UI/CardRewardScreen.tscn");
            }
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

    /// <summary>K2 (§5b): boss contexts roll death at 40% — the expedition
    /// return path reads the tier of the combat it's returning from.</summary>
    public EncounterTier CurrentTier => _currentTier;

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