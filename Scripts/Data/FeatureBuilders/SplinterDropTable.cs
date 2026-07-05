// ============================================================
// SplinterDropTable.cs
//
// Purpose:        Single source of truth for Arcane Splinter
//                 drop amounts across all event types.
//                 All values are tunable here without touching
//                 the systems that call them.
// Layer:          Data
// Collaborators:  EncounterRouter.cs (combat drops),
//                 OverworldRunManager.cs (rest / narrative drops)
// ============================================================

/// <summary>
/// Returns Arcane Splinter drop amounts for each event type.
/// Combat amounts scale with encounter difficulty tier.
/// All amounts are randomized within a tuned range.
/// </summary>
public static class SplinterDropTable
{
    // ── Combat ───────────────────────────────────────────────────────────

    /// <summary>
    /// Splinters awarded on winning a combat encounter.
    /// Skirmish &lt; Battle &lt; Siege.
    /// </summary>
    public static int Combat(EncounterTier tier) => tier switch
    {
        EncounterTier.Skirmish => RollRange(2, 3),
        EncounterTier.Battle   => RollRange(4, 6),
        EncounterTier.Siege    => RollRange(8, 12),
        _                      => RollRange(3, 5),
    };

    // ── Exploration events ────────────────────────────────────────────────

    /// <summary>Splinters found at a rest site.</summary>
    public static int RestSite() => RollRange(1, 3);

    /// <summary>Splinters earned from completing a narrative encounter.</summary>
    public static int Narrative() => RollRange(2, 4);

    // ── Helper ────────────────────────────────────────────────────────────

    private static int RollRange(int min, int max)
    {
        // Godot's GD.RandRange returns a double; cast to int for whole numbers.
        return (int)Godot.GD.RandRange(min, max + 1);
    }
}
