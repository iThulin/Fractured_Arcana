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

/// <summary>Per-process active-run scratchpad. Holds school selection plus a set of debug-mode flags that bypass standard rules (no fog, unlimited steps, god-mode HP, etc.). Distinct from save data — these toggles do not persist.</summary>
public static class PlayerSession
{
    /// <summary>Currently selected wizard school. Drives starting deck composition and school-specific systems.</summary>
    public static CardSchool SelectedSchool = CardSchool.Elementalist;
    public static bool DebugMode = false;
    public static int DeckSize = 10;

    // ── Debug flags (only active when DebugMode = true) ─────────────────
    public static bool NoFog = false;              // reveal all hexes
    public static bool UnlimitedSteps = false;     // step budget never decreases
    public static bool GodModeHP = false;          // HP never drops below 1
    public static bool StartWithGold = false;      // begin run with 5000 gold
    public static bool StartWithSplinters = false; // begin run with 5000 splinters
    public static bool SkipDeployment = false;     // auto-place units in combat

    // Force a specific POI type for the next encounter (-1 = no override)
    public static int ForceNextEncounterType = -1; // maps to OverworldHex.POIType int value

    public static HashSet<string> UnlockedFeatures { get; private set; } = new();

    /// <summary>
    /// Called by OverworldRunManager at run start after BuildingEffectApplier runs.
    /// Also called on campus load so the upgrade screen can read flags without starting a run.
    /// </summary>
    public static void ApplyUnlockedFeatures(HashSet<string> features)
    {
        UnlockedFeatures = features ?? new HashSet<string>();
    }

    public static bool HasFeature(string flag) => UnlockedFeatures.Contains(flag);
}