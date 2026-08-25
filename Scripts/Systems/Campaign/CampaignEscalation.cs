using Godot;

// ============================================================
// CampaignEscalation.cs
//
// Purpose:        The per-year "the world hardens" pass applied by
//                 SaveManager.ContinueCampaign when the player holds a
//                 timeline past its Grand Conjunction (the press-your-luck
//                 Continue). Corruption is the forcing clock (near-monotonic):
//                 each continued year raises it, which caps how far a chain
//                 can be pushed and keeps Continue a real gamble.
// Layer:          System (campaign)
// CANONICAL:      claude/progression_persistence_model_v1.md §6, §9.
//                 The constants below are the PRIMARY difficulty dial
//                 (progression doc §9). Tune here.
// ============================================================

public static class CampaignEscalation
{
    // ── Tuning knobs (the primary dial, progression doc §9) ───────────────
    /// <summary>Flat corruption added to every LAND tile per continued year.
    /// The forcing clock: higher = shorter chains. Byte-clamped to 100.</summary>
    public const int CorruptionTidePerYear = 8;

    /// <summary>Threat-level increment per continued year. Drives enemy scaling
    /// via CombatDifficultyMult (below), read in ExpeditionManager.DifficultyMultAt.</summary>
    public const int ThreatLevelPerYear = 1;

    /// <summary>Enemy difficulty added per threat level (i.e. per continued year)
    /// to the REGION encounter multiplier. 0.10 = +10% enemy difficulty per year.
    /// Capstones (shard guardians, archmage groups) keep their authored difficulty
    /// for now. A primary tuning knob (progression doc §9).</summary>
    public const float ThreatDifficultyStep = 0.10f;

    /// <summary>Apply one year's escalation to the (persisting) timeline.
    /// Called by SaveManager.ContinueCampaign AFTER CampaignYear is advanced.</summary>
    public static void Apply(CycleState cycle)
    {
        if (cycle == null)
            return;

        cycle.SeasonalThreatLevel += ThreatLevelPerYear;

        var world = cycle.World;
        int raised = 0;
        if (world != null && world.Tiles != null)
        {
            for (int i = 0; i < world.Tiles.Length; i++)
            {
                var t = world.Tiles[i];              // WorldTile is a struct
                if (!t.IsLand)
                    continue;
                int next = Mathf.Min(100, t.Corruption + CorruptionTidePerYear);
                if (next != t.Corruption)
                {
                    t.Corruption = (byte)next;
                    world.Tiles[i] = t;              // write the struct back
                    raised++;
                }
            }
        }

        GD.Print($"[CampaignEscalation] Year {cycle.CampaignYear}: threat +{ThreatLevelPerYear} " +
                 $"(now {cycle.SeasonalThreatLevel}); corruption tide +{CorruptionTidePerYear} on {raised} land tile(s).");
    }

    /// <summary>Region-encounter difficulty multiplier contributed by the timeline's
    /// accumulated threat. 1.0 at Year 1 (threat 0); each continued year adds
    /// ThreatDifficultyStep. Multiplied into ExpeditionManager.DifficultyMultAt.</summary>
    public static float CombatDifficultyMult(CycleState cycle) =>
        cycle == null ? 1f : 1f + cycle.SeasonalThreatLevel * ThreatDifficultyStep;
}
