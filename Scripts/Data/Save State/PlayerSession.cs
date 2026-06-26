using System.Collections.Generic;

// ============================================================
// PlayerSession.cs
//
// Purpose:        Process-wide scratchpad holding the active
//                 wizard's school choice and debug-mode flags
//                 for the current run. Lives outside save data
//                 so toggles can be flipped without writing to
//                 disk.
// Layer:          Data
// Collaborators:  ClassSelectUi.cs / CampusScreen.cs (writers),
//                 CombatManager.cs, OverworldRunManager.cs (readers)
// See:            README §6 — Debug flags
// ============================================================

/// <summary>
/// Per-process active-run scratchpad. Holds school selection, run-scoped
/// feature flags unlocked by campus buildings, debug-mode flags, and the
/// disenchant splinter bonus accumulated from building tiers.
/// Distinct from save data — nothing here persists to disk.
/// </summary>
public static class PlayerSession
{
    /// <summary>
    /// When starting a new game from the campus screen, store the selected slot
    /// here so the NewGameScreen can write the new save to the correct slot.
    /// </summary>
    public static int PendingNewGameSlot = -1;

    /// <summary>True while the player is on an active expedition run.
    /// Gates deck editing in DeckEditorUi.</summary>
    public static bool IsOnExpedition = false;
    /// <summary>Currently selected wizard school. Drives starting deck composition and school-specific systems.</summary>
    public static CardSchool SelectedSchool = CardSchool.Elementalist;
    public static bool DebugMode = false;
    public static int DeckSize = 10;

    /// <summary>Gold cost to slot one card into the active deck.
    /// Base 30, reduced by buildings. Never below 0.</summary>
    public static int CardSlotCost = 30;

    /// <summary>
    /// Extra Arcane Splinters added to every disenchant yield.
    /// Accumulated from Dissolution Chamber tiers by BuildingEffectApplier.
    /// Reset to 0 by ClearRunState().
    /// </summary>
    public static int DisenchantSplinterBonus = 0;

    // ── Debug flags (only active when DebugMode = true) ─────────────────
    public static bool NoFog = false;
    public static bool UnlimitedSteps = false;
    public static bool DebugGrantStagingArmed = false;
    public static bool GodModeHP = false;
    public static bool StartWithGold = false;
    public static bool StartWithSplinters = false;
    public static bool SkipDeployment = false;

    // Force a specific POI type for the next encounter (-1 = no override)
    public static int ForceNextEncounterType = -1;

    // ── Feature flags ────────────────────────────────────────────────────
    // Populated by BuildingEffectApplier.CalculateRunBonuses() via SetFeature().
    // Also populated by BuildingEffectApplier.ApplyCampusEffects() so campus
    // screens (upgrade, deck editor) can read flags without starting a run.
    // Cleared by ClearRunState() before each new run.
    private static readonly HashSet<string> _activeFeatures = new();

    /// <summary>
    /// Activate a named feature flag. Called by BuildingEffectApplier when
    /// iterating UnlocksFeatures on each built building tier.
    /// </summary>
    public static void SetFeature(string feature)
    {
        if (!string.IsNullOrEmpty(feature))
            _activeFeatures.Add(feature);
    }

    /// <summary>Returns true if the named feature is currently active.</summary>
    public static bool HasFeature(string feature) => _activeFeatures.Contains(feature);

    /// <summary>
    /// Clear all run-scoped state. Call before BuildingEffectApplier runs
    /// at the start of each run so stale flags don't carry forward.
    /// </summary>
    public static void ClearRunState()
    {
        _activeFeatures.Clear();
        DisenchantSplinterBonus = 0;
        CardSlotCost = 30;
        ForceNextEncounterType = -1;
    }

    // ── Expedition deploy handoff (strategic view → expedition scene) ────

    /// <summary>Offset column of the staging point the next expedition launches from.</summary>
    public static int ExpeditionStagingCol = -1;

    /// <summary>Offset row of the staging point the next expedition launches from.</summary>
    public static int ExpeditionStagingRow = -1;

    /// <summary>Window radius for the next expedition (0 = use ExpeditionManager default).</summary>
    public static int ExpeditionWindowRadius = 0;

    /// <summary>Set true when the Grand Conjunction ends a cycle. The campus reads
    /// this on entry and begins the next cycle (with school reselection) instead of
    /// resuming the dead one. Reset to false once the new cycle is begun.</summary>
    public static bool CycleEndedByConjunction = false;

    /// <summary>Debug: when true, the strategic view charts the ENTIRE map (all tiles
    /// visible, all POIs discovered) so corruption spread and the whole world can be
    /// inspected during testing. Does not write to the save — purely a view override.</summary>
    public static bool DebugRevealStrategicMap = false;
}