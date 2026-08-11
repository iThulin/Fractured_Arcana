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

    /// <summary>Founding scenario id chosen on the NewGameScreen, carried for the
    /// OnComplete host path (which creates the save itself). EnsureCycleWorld reads
    /// it as a fallback when the ledger's FoundingScenario is still unset. Empty
    /// otherwise; not run state, so ClearRunState leaves it alone.</summary>
    public static string PendingStartScenarioId = "";

    /// <summary>One-shot: set by CampusScreen when leaving the campus for the world,
    /// consumed by StrategicView to open the atlas framed on the home city and swoop
    /// out to the overview (Phase 2, Stage 2 — the "ascend" transition). Not run
    /// state; a UI hand-off flag, so ClearRunState leaves it alone.</summary>
    public static bool ZoomFromHomeOnOpen = false;

    /// <summary>True while the campus is open as an IN-WORLD OVERLAY hosted by the
    /// strategic scene (Phase 2, Stage 3 — single-scene merge): no scene swap, the
    /// atlas sits hidden beneath. The campus draws its own chrome, so the global HUD
    /// hides while this is set. Not run state.</summary>
    public static bool CampusOverlayOpen = false;

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

    /// <summary>U3 "stop" (units doc §5): when set, every enemy trigger opens an
    /// interactive priority window even with no Reaction card in hand. The test
    /// lever for watching the stack pause; normal play leaves this off so
    /// auto-pass costs zero clicks.</summary>
    public static bool DebugStopOnTriggers = false;

    /// <summary>R22 damage-preview self-check: when set, every real player cast
    /// re-runs the CombatSim preview for that same cast and, after it resolves,
    /// logs whether the predicted per-enemy HP loss matched the actual delta.
    /// A "[PreviewSelfCheck] DESYNC …" line means the preview and the live
    /// resolver diverged — the maintenance guard that replaces a second test
    /// suite. Off in normal play (adds a sim run + a 0.5s deferred compare per
    /// cast). Reaction/trigger damage landing within that window can log a
    /// benign mismatch — read DESYNC lines in that light.</summary>
    public static bool DebugPreviewSelfCheck = false;

    // ── Stack stops (combat_ui §7c, V3) ─────────────────────────────────
    // Per-trigger-type "stop" toggles set from the stack strip header — the
    // digital-card-game full-control pattern. A set stop opens an interactive
    // priority window for that category even with no Reflex card in hand.
    // Player-facing (NOT debug-gated); persists for the session.
    public static bool StopOnStrikes = false;
    public static bool StopOnEnemyAbilities = false;
    public static bool StopOnItemProcs = false;

    /// <summary>Set by CombatDebugLauncher: this fight was launched standalone, so
    /// win/lose/forfeit returns to the campus instead of the (absent) overworld
    /// run. Reset on return. Prevents the blank-screen exit from a debug fight.</summary>
    public static bool DebugCombat = false;

    // ── Battlefield debug injectors (set by CombatDebugLauncher) ─────────
    /// <summary>When non-empty, HexGridManager.ActiveMapEvents appends a synthetic
    /// MapEventDef of this kind (imbue_patch / spread_element / advance_hazard_ring)
    /// so E4 map events can be exercised on ANY launched map, not just bf_cauldron.
    /// Cleared on return to campus.</summary>
    public static string DebugMapEventKind = null;
    public static string DebugMapEventElement = "fire";

    /// <summary>When set, HexGridManager.EnforceHazardCap is skipped so the
    /// guarantee-pass hazard trim can be A/B-compared against an uncapped map.</summary>
    public static bool DebugDisableHazardCap = false;

    /// <summary>CombatDebugLauncher: extra map objects to spawn near the arena centre
    /// for isolated E3 testing (each entry is a MapObjectCatalog kind). Cleared on return.</summary>
    public static List<string> DebugMapObjects = null;

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

    // ── Wizard expedition HP carry (K2.5 symmetry, 2026-07-29) ───────────

    /// <summary>The wizard's in-combat HP carried between fights of the same
    /// expedition, mirroring <c>Companion.ExpeditionHP</c> — the playtest
    /// asymmetry was that companions carried battle damage while the wizard
    /// reset to full every fight. −1 = not carrying (next fight fields at
    /// full). Written on combat victory, applied at wizard spawn, reset on a
    /// fresh deploy, quarter-healed by rest sites, cleared (full) by outposts.
    /// Deliberately NOT reset in ClearRunState — that runs on every
    /// ExpeditionManager._Ready including combat returns, which would defeat
    /// the carry.</summary>
    public static int WizardExpeditionHP = -1;

    /// <summary>MaxHealth recorded alongside <see cref="WizardExpeditionHP"/> so
    /// overworld rest sites can heal a fraction without reaching into combat
    /// data.</summary>
    public static int WizardExpeditionMaxHP = 20;

    // ── Expedition deploy handoff (strategic view → expedition scene) ────

    /// <summary>Offset column of the staging point the next expedition launches from.</summary>
    public static int ExpeditionStagingCol = -1;

    /// <summary>Offset row of the staging point the next expedition launches from.</summary>
    public static int ExpeditionStagingRow = -1;

    /// <summary>Window radius for the next expedition (0 = use ExpeditionManager default).</summary>
    public static int ExpeditionWindowRadius = 0;

    /// <summary>Player-facing view preference: when true, an expedition run opens
    /// directly into the 3D expedition-window view and the in-run 2D/3D toggle
    /// starts in 3D. NOT debug-gated — a real setting that persists for the
    /// session. Because this is a static scratchpad it survives the scene change
    /// into the overworld and across combat returns, so the choice made by the
    /// HUD toggle carries to the next deploy. Read at run start in
    /// ExpeditionManager._Ready; flipped by the HUD toggle. Default 2D.
    /// Deliberately NOT reset in ClearRunState — a view preference, not run state.</summary>
    public static bool ExpeditionView3D = false;

    /// <summary>Set true when the Grand Conjunction ends a cycle. The campus reads
    /// this on entry and begins the next cycle (with school reselection) instead of
    /// resuming the dead one. Reset to false once the new cycle is begun.</summary>
    public static bool CycleEndedByConjunction = false;

    /// <summary>Debug: when true, the strategic view charts the ENTIRE map (all tiles
    /// visible, all POIs discovered) so corruption spread and the whole world can be
    /// inspected during testing. Does not write to the save — purely a view override.</summary>
    public static bool DebugRevealStrategicMap = false;

    /// <summary>Debug: suppress enemy-initiated combat on expeditions (patrol ambushes / warfront
    /// interceptions), so the map can be walked freely — e.g. to reach a distant enemy capital
    /// while testing. Does not affect player-initiated combat (walking into a combat POI).</summary>
    public static bool DebugNoAmbush = false;
}