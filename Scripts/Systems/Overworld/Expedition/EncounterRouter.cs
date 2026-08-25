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
// See:            README §3, Architecture (scene swap pattern)
// ============================================================

/// <summary>Process-wide router for transitions between overworld, combat, narrative, and negotiation scenes. Owns the saved overworld state across scene swaps and exposes a deterministic seed so the map regenerates identically on return.</summary>
public partial class EncounterRouter : Node
{
    [Export] public string CombatScenePath = "res://Scenes/Combat/Battlefield.tscn";
    [Export] public string OverworldScenePath = "res://Scenes/Overworld/ExpeditionScene.tscn";

    /// <summary>Step 9 (campus → combat round trip): when non-empty, combat
    /// returns to this scene instead of OverworldScenePath. Set by campus-side
    /// launchers before the scene swap; consumed (and cleared) by the return
    /// host when it processes HasPendingReturn. CardRewardScreen also routes
    /// through <see cref="ReturnScenePath"/> so drafting doesn't strand the
    /// player on the expedition map after a campus-launched fight.</summary>
    public string ReturnSceneOverride = "";

    /// <summary>The scene combat actually returns to: the override when set,
    /// else the overworld.</summary>
    public string ReturnScenePath =>
        string.IsNullOrEmpty(ReturnSceneOverride) ? OverworldScenePath : ReturnSceneOverride;

    public static EncounterRouter Instance { get; private set; }

    public bool HasPendingReturn { get; set; } = false;
    public bool CombatWon { get; set; }
    public int GoldReward { get; set; }
    public int DamageTaken { get; set; }
    public int SplinterReward { get; set; }
    public int SavedSplinterEarned { get; set; }
    public int SavedMaterialEarned { get; set; }
    public int SavedSuppliesEarned { get; set; }
    private EncounterTier _currentTier = EncounterTier.Battle;

    public int SavedStepsRemaining;
    public int SavedCurrentHP;
    public int SavedGoldEarned;
    public int SavedEncountersWon;
    public Vector2I SavedPartyCoord;
    public Vector2I SavedCombatHexCoord;

    /// <summary>Weather over the combat tile at launch (Mobile Fortress W3).
    /// The battlefield injects a matching weather_tick hazard from this. Set by
    /// ExpeditionManager.CommitCombat; cleared on combat finish so a later
    /// non-overworld fight (debug, city gate) never inherits stale weather.</summary>
    public WeatherType SavedWeather = WeatherType.Clear;

    /// <summary>True when the pending combat was a patrol ambush (C4 deed).</summary>
    public bool SavedCombatWasPatrolAmbush = false;

    /// <summary>True when the ambush caught the castle MID-STRIDE (Mobile Fortress
    /// §3.4). The F6 "Defend the Castle" combat reads this to add +1 round to the
    /// wizard's teleport delay (the castle was unbraced). Inert until F6 ships.</summary>
    public bool SavedStrideAmbush = false;

    /// <summary>Owner archmage of the ambushing patrol ("wilds" for the
    /// generic pursuer).</summary>
    public string SavedCombatPatrolArchmageId = "";

    /// <summary>Non-empty when the pending combat is a fragment guardian (the
    /// fragment key). On a win, ExpeditionManager sets &lt;key&gt;_trial_passed.</summary>
    public string SavedCombatGuardianKey = "";

    /// <summary>Owner archmage of a POI combat whose composition was drawn from
    /// that archmage's own pool ("" for region-pool fights). On a win,
    /// ExpeditionManager advances the archmage's dossier (DossierService).
    /// Patrol ambushes carry attribution via SavedCombatPatrolArchmageId
    /// instead. Reset by CommitCombat; set AFTER it (the patrol pattern).</summary>
    public string SavedCombatArchmageId = "";

    /// <summary>Non-empty when the pending combat is an archmage RESOLUTION
    /// boss fight (the Overthrow verb, Step 9). On a win, the return host sets
    /// the archmage's disposition to Overthrown. Reset alongside the other
    /// attribution fields; set by the launcher just before the scene swap.</summary>
    public string SavedResolutionArchmageId = "";

    /// <summary>Marginalia (marginalia_spec_v1 R2): the won fight's kill tally by
    /// enemy FactionId. Written by CombatManager at VICTORY only (cleared at
    /// defeat), consumed-and-cleared by ExpeditionManager.EmitCombatDeed, which
    /// commits it to EternalLedger.DeedCounts as marginalia_kill_&lt;family&gt;.</summary>
    public System.Collections.Generic.Dictionary<string, int> SavedCombatFamilyKills = new();

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
        SavedWeather = WeatherType.Clear; // W3: don't leak weather into the next fight
        SavedStrideAmbush = false;        // §3.4: clear the stride-ambush flag too
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
            // Instead the splinter reward is DOUBLED (the Academy stipend) and
            // deck building happens back at the campus (upgrades now; a splinter
            // card-acquisition screen is the planned follow-on).
            if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave?.SelectedSchool,
                    ignoreCase: true, out var schoolForReward)
                && schoolForReward == CardSchool.Adept)
            {
                SplinterReward *= 2;
                GD.Print($"EncounterRouter: Adept stipend. No draft, splinters doubled to {SplinterReward}.");
                GetTree().CreateTimer(2.0f).Timeout += () =>
                    GetTree().ChangeSceneToFile(ReturnScenePath);
            }
            else
            {
                // Show card reward screen. It routes to ReturnScenePath when done.
                GetTree().ChangeSceneToFile("res://Scenes/UI/CardRewardScreen.tscn");
            }
        }
        else
        {
            // Loss: skip reward, return to the launch host after brief delay
            GetTree().CreateTimer(2.0f).Timeout += () =>
                GetTree().ChangeSceneToFile(ReturnScenePath);
        }
    }

    /// <summary>
    /// Called by OverworldRunManager.CommitCombat when bypassing StartCombat
    /// with a pre-built EncounterDefinition from the scout panel.
    /// </summary>
    public void SetCurrentTier(EncounterTier tier) => _currentTier = tier;

    /// <summary>K2 (§5b): boss contexts roll death at 40%. The expedition
    /// return path reads the tier of the combat it's returning from.</summary>
    public EncounterTier CurrentTier => _currentTier;

    private int CalculateGoldRewardForTier(EncounterTier tier) => tier switch
    {
        EncounterTier.Skirmish => (int)GD.RandRange(8, 15),
        EncounterTier.Battle => (int)GD.RandRange(18, 30),
        EncounterTier.Siege => (int)GD.RandRange(40, 60),
        // Ambush sits between Skirmish and Battle. This was already the
        // fallthrough value; made explicit now that the tier is actually routed.
        EncounterTier.Ambush => (int)GD.RandRange(15, 25),
        _ => (int)GD.RandRange(15, 25),
    };

    public static HexGridManager.MapDensityPreset DensityForTier(EncounterTier tier) => tier switch
    {
        EncounterTier.Skirmish => HexGridManager.MapDensityPreset.Sparse,
        EncounterTier.Battle => HexGridManager.MapDensityPreset.Standard,
        EncounterTier.Siege => HexGridManager.MapDensityPreset.Dense,
        EncounterTier.Ambush => HexGridManager.MapDensityPreset.Standard,
        _ => HexGridManager.MapDensityPreset.Standard,
    };
}