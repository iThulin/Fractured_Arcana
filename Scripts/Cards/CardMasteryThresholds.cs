using System;
using System.Collections.Generic;
using Godot;

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
    // Cast count required before spending the Nth point
    private static readonly int[] _thresholds = { 0, 10, 20, 35, 55, 80, 110 };

    /// <summary>
    /// Returns true if the player has cast the card enough times
    /// to spend their next upgrade point.
    /// </summary>
    public static bool CanSpendNextPoint(int castCount, int pointsSpent)
    {
        int index = Mathf.Min(pointsSpent, _thresholds.Length - 1);
        return castCount >= _thresholds[index];
    }

    /// <summary>
    /// How many more casts are needed before the next point can be spent.
    /// </summary>
    public static int CastsUntilNextPoint(int castCount, int pointsSpent)
    {
        int index = Mathf.Min(pointsSpent, _thresholds.Length - 1);
        return Mathf.Max(0, _thresholds[index] - castCount);
    }
}