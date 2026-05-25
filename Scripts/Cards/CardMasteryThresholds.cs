using System;

// ============================================================
// CardMasteryThresholds.cs
//
// Purpose:        Single source of truth for cast counts
//                 required to unlock each upgrade stage.
//                 Tunable here without touching cast logic.
// Layer:          Data
// Collaborators:  CastMasteryTracker.cs, CardUpgradeScreen.cs
// ============================================================

public static class CardMasteryThresholds
{
    // Casts required to make each stage available for purchase
    // These unlock the option — the player still spends splinters
    // to actually apply the upgrade at the Scriptorum
    public static readonly int[] CastsRequired = { 0, 15, 35, 70, 120 };

    public static bool IsStageUnlocked(int castCount, int stage)
        => castCount >= CastsRequired[stage];

    public static int CastsToNextStage(int castCount, int currentStage)
    {
        int next = currentStage + 1;
        if (next >= CastsRequired.Length) return 0;
        return Math.Max(0, CastsRequired[next] - castCount);
    }
}