using System.Collections.Generic;

// ============================================================
// CalendarState.cs
//
// Purpose:        The astrological calendar for one cycle —
//                 the strategic turn clock. Tracks the current
//                 phase (turn), lunation, eclipse omens, and the
//                 Grand Conjunction countdown. Phase 0 ships the
//                 data model + advancement logic; Phase 1 wires
//                 it to the strategic map and corruption ticks.
// Layer:          Data
// Collaborators:  CycleState.cs (owner),
//                 CalendarManager (Phase 1 — drives advancement),
//                 CampaignState.cs (corruption re-key, Phase 1),
//                 AssaultDirector (Phase 4 — eclipse scheduling)
// See:            open_world_refactor_v1.docx §2 — The Calendar
// ============================================================

/// <summary>
/// One eclipse omen on the calendar: Kassian has found an alignment.
/// Scheduled by the beacon system (Phase 4); resolved as a campus
/// defense battle when the calendar reaches it.
/// </summary>
public class EclipseOmen
{
    /// <summary>Lunation on which the eclipse lands.</summary>
    public int LandsOnLunation = 0;

    /// <summary>Phase index (0–7) within that lunation.</summary>
    public int LandsOnPhase = 0;

    /// <summary>
    /// Why this eclipse was called: "BeaconThreshold", "TraceBack",
    /// or a scripted id. Drives assault force composition in Phase 4.
    /// </summary>
    public string Reason = "";

    /// <summary>True once the assault has been fought (win or lose).</summary>
    public bool Resolved = false;
}

/// <summary>
/// The cycle's clock. Time advances in phases; eight phases make a
/// lunation; the cycle ends when the Grand Conjunction completes at
/// the end of the final lunation (or when the player forces it).
/// </summary>
public class CalendarState
{
    // ── Constants ────────────────────────────────────────────────────────
    public const int PhasesPerLunation = 8;

    /// <summary>
    /// Canonical phase order. Index = phase index. Names are working
    /// titles pending the terminology pass; the phase→school mapping
    /// is locked design.
    /// </summary>
    public static readonly string[] PhaseNames =
    {
        "The Veiled",        // New Moon
        "The Quickening",    // Waxing Crescent
        "The Forgelight",    // First Quarter
        "The Gathering",     // Waxing Gibbous
        "The Naming",        // Full Moon
        "The Reflection",    // Waning Gibbous
        "The Turning",       // Last Quarter
        "The Last Thread",   // Waning Crescent
    };

    /// <summary>School affinity per phase index. Must parallel PhaseNames.</summary>
    public static readonly string[] PhaseSchools =
    {
        "Necromancer",
        "Druid",
        "Tinker",
        "Elementalist",
        "Adept",
        "Arcanist",
        "Chronomancer",
        "Enchanter",
    };

    // ── Serialized state ─────────────────────────────────────────────────
    /// <summary>Current phase index, 0–7. 0 = the new moon (The Veiled).</summary>
    public int CurrentPhase = 0;

    /// <summary>Current lunation, 1-based.</summary>
    public int CurrentLunation = 1;

    /// <summary>
    /// Lunations in this cycle before the Grand Conjunction. Default 12.
    /// The single most important pacing knob in the game.
    /// </summary>
    public int LunationsPerCycle = 12;

    /// <summary>Total phases elapsed this cycle (audit / deed timestamps).</summary>
    public int TotalPhasesElapsed = 0;

    /// <summary>Scheduled and historical eclipses for this cycle.</summary>
    public List<EclipseOmen> Eclipses = new();

    /// <summary>True if the player forced the Conjunction early.</summary>
    public bool ConjunctionForced = false;

    // ── Computed (not serialized) ────────────────────────────────────────
    [System.Text.Json.Serialization.JsonIgnore]
    public string CurrentPhaseName => PhaseNames[CurrentPhase];

    [System.Text.Json.Serialization.JsonIgnore]
    public string CurrentPhaseSchool => PhaseSchools[CurrentPhase];

    /// <summary>True when the calendar has run out — the Conjunction is here.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ConjunctionReached =>
        ConjunctionForced || CurrentLunation > LunationsPerCycle;

    /// <summary>Lunations remaining before the Conjunction (0 when reached).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int LunationsRemaining =>
        System.Math.Max(0, LunationsPerCycle - CurrentLunation + 1);

    // ── Advancement ──────────────────────────────────────────────────────

    /// <summary>
    /// Advance the calendar by one phase. Returns true when this advance
    /// crossed a lunation boundary (the new moon) — the caller (Phase 1's
    /// CalendarManager) runs the kingdom simulation tick on that signal.
    /// Does nothing if the Conjunction has been reached.
    /// </summary>
    public bool AdvancePhase()
    {
        if (ConjunctionReached)
            return false;

        TotalPhasesElapsed++;
        CurrentPhase++;

        if (CurrentPhase >= PhasesPerLunation)
        {
            CurrentPhase = 0;
            CurrentLunation++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Advance the calendar by one whole lunation — the cost of deploying one
    /// expedition. Snaps to the new moon (phase 0) of the next lunation and
    /// keeps TotalPhasesElapsed consistent (counts the phases skipped to reach
    /// the next new moon). Returns true (a lunation boundary is always crossed),
    /// mirroring AdvancePhase's contract so callers can run the per-lunation
    /// world tick on the return value. Does nothing if the Conjunction is reached.
    /// </summary>
    public bool AdvanceLunation()
    {
        if (ConjunctionReached)
            return false;

        // Phases consumed to land on the next new moon: finish out the current
        // lunation (PhasesPerLunation - CurrentPhase). From a fresh new moon that
        // is a full 8; mid-lunation it's whatever remains.
        int phasesToNextNewMoon = PhasesPerLunation - CurrentPhase;
        TotalPhasesElapsed += phasesToNextNewMoon;

        CurrentPhase = 0;
        CurrentLunation++;

        return true;
    }

    /// <summary>
    /// Returns the unresolved eclipse landing on the current phase,
    /// or null. The caller resolves it as a defense battle.
    /// </summary>
    public EclipseOmen GetEclipseDueNow()
    {
        foreach (var e in Eclipses)
        {
            if (!e.Resolved &&
                e.LandsOnLunation == CurrentLunation &&
                e.LandsOnPhase == CurrentPhase)
                return e;
        }
        return null;
    }

    /// <summary>
    /// True if any unresolved eclipse is visible on the calendar ahead
    /// of (or at) the current moment — the omen the UI displays.
    /// </summary>
    public bool HasPendingEclipse()
    {
        foreach (var e in Eclipses)
        {
            if (e.Resolved)
                continue;
            if (e.LandsOnLunation > CurrentLunation)
                return true;
            if (e.LandsOnLunation == CurrentLunation && e.LandsOnPhase >= CurrentPhase)
                return true;
        }
        return false;
    }
}
