using Godot;

// ============================================================
// LoyaltyEvents.cs
//
// Purpose:        K4 loyalty delta hooks: the one place loyalty
//                 moves outside authored arc stages (which keep
//                 their own LoyaltyDelta in CompanionArcTracker).
//                 Table + application; callers are the expedition
//                 extraction path and the injury system's wipe.
// Layer:          Data (FeatureBuilders)
// Collaborators:  ExpeditionManager.Extract (homecoming + heroism),
//                 CompanionInjurySystem.ApplyWipe (survivor cost +
//                 death ripple), CompanionDefinition (tiers).
// Notes:          FRESH-AUTHORED K4 STARTING VALUES (2026-08-13).
//                 The v1 delta table could not be located in repo or
//                 project knowledge; these are starting values under
//                 the empirical-tuning pillar, not recovered canon.
//                 Tune HERE only.
// ============================================================

/// <summary>The K4 loyalty delta table and its appliers. Deltas clamp to 0–100
/// and log one line each; loyalty movement should always be legible in the
/// console during tuning.</summary>
public static class LoyaltyEvents
{
    // ── The table (K4 starting values, tune here) ────────────────────────

    /// <summary>Came home from a successful extraction (fielded, alive).</summary>
    public const int ExtractionDelta = +1;

    /// <summary>Went down in a fight the party WON, and still came home:
    /// heroism, rewarded. (v1's "heroism stays free" floor is kept: being
    /// downed in a won fight never costs loyalty; this is the earned upside.)</summary>
    public const int HeroismDelta = +2;

    /// <summary>Survived an expedition wipe or retreat (injured or not).
    /// The run died and they carried it home.</summary>
    public const int WipeSurvivorDelta = -2;

    /// <summary>Roster-wide ripple when a companion permanently dies.</summary>
    public const int DeathRippleDelta = -8;

    /// <summary>Ripple dampening for Sworn companions. Counterargument logged:
    /// arguably the devoted should grieve HARDEST. Mechanically that punishes
    /// the player's best people for the player's worst moment. The death
    /// already costs the roster its ripple; making Sworn brittle on top would
    /// fight the §4a "personal ceiling" investment. Sworn have survived worse
    /// with you: they take -4, everyone else -8.</summary>
    public const int DeathRippleSwornDelta = -4;

    // ═════════════════════════════════════════════════════════════════════
    // Appliers
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Extraction homecoming: +1 to every fielded companion who is
    /// coming home (alive), +2 more to anyone stabilized at 0: downed in a
    /// won fight, still walked out. MUST run BEFORE
    /// CompanionInjurySystem.ApplyExtractionCheck, which resets ExpeditionHP
    /// (the heroism evidence).</summary>
    public static void OnExtraction(GuildSaveData save)
    {
        if (save == null) return;
        foreach (var id in save.ActivePartyCompanionIds)
        {
            var c = save.Companions.Find(x => x.Id == id && x.IsRecruited && !x.IsPermadead);
            if (c == null) continue;

            Apply(c, ExtractionDelta, "came home");
            if (c.ExpeditionHP == 0)
                Apply(c, HeroismDelta, "downed winning, walked out anyway");
        }
        SaveManager.MarkDirty();
    }

    /// <summary>One wipe survivor pays the run's cost. Called from
    /// CompanionInjurySystem.ApplyWipe per surviving fielded companion.</summary>
    public static void OnWipeSurvived(Companion c)
    {
        if (c == null) return;
        Apply(c, WipeSurvivorDelta, "survived the wipe");
    }

    /// <summary>The v1-locked morale ripple, finally landing: a death moves
    /// the whole living roster. Sworn dampened (see the constant's note).
    /// Signature destruction needs no code; signatures are DERIVED
    /// (StanceRegistry.EligibleSignature) and the dead never spawn.</summary>
    public static void OnDeathRipple(GuildSaveData save, Companion dead)
    {
        if (save == null || dead == null) return;
        GD.Print($"[Loyalty] The roster learns {dead.Name} is gone.");
        foreach (var c in save.Companions)
        {
            if (!c.IsRecruited || c.IsPermadead || c.Id == dead.Id) continue;
            int delta = c.GetLoyaltyTier() == LoyaltyTier.Sworn
                ? DeathRippleSwornDelta : DeathRippleDelta;
            Apply(c, delta, $"mourns {dead.Name}");
        }
        SaveManager.MarkDirty();
    }

    // ── Core ─────────────────────────────────────────────────────────────

    private static void Apply(Companion c, int delta, string why)
    {
        if (delta == 0) return;
        int before = c.Loyalty;
        c.Loyalty = System.Math.Clamp(c.Loyalty + delta, 0, 100);
        if (c.Loyalty != before)
            GD.Print($"[Loyalty] {c.Name} {(delta > 0 ? "+" : "")}{delta} → {c.Loyalty} ({why})" +
                     (c.GetLoyaltyTier() != Companion.TierOfValue(before)
                        ? $", now {c.GetLoyaltyTier()}" : ""));
    }
}
