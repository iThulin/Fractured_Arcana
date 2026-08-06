using System.Collections.Generic;

// ============================================================
// ConvergenceState.cs
//
// Purpose:        The finale's progress block — which phase the
//                 player is in, which path they committed to, and
//                 what the event has decided so far. Tier-2 state:
//                 it dies with the timeline, exactly like the rest
//                 of CycleState. The Convergence is a thing that
//                 happens TO a timeline, not a permanent record;
//                 what endures lands in EternalLedger.LoopHistory
//                 and MetaNarrativeFlags instead.
// Layer:          Data (save schema)
// Collaborators:  CycleState (owner), StrategicView (writes Phase
//                 and Outcome at the Conjunction gate),
//                 CampusScreen.BeginNextCycle (reads Outcome to
//                 archive the real LoopRecord), and — from I2 —
//                 ConvergenceDirector.
// See:            docs/convergence_finale_spec_v1.md §2 (this
//                 block), §3 (the gate and outcome routing).
//
// Introduced with save schema v102. Older saves are not migrated:
// this project runs in dev mode and starts a new game per test
// (ruling, 2026-08-06), so the version stamp simply invalidates
// anything older. Revisit when saves become durable.
// ============================================================

/// <summary>The finale's live progress. Phase −1 means "not started", which is
/// what every save that has never opened the Anchorhold carries.</summary>
public class ConvergenceState
{
    /// <summary>−1 = not started; 1..5 = the phase the player is IN.</summary>
    public int Phase = -1;

    /// <summary>"", "Restoration", "Harness", "Synthesis". Committed at the end of
    /// Phase 1. NOTE the naming canon: the second path's code id is "Harness" (it
    /// is already baked into LoopRecord.ResolutionPath); its DISPLAY name is
    /// "Dominion". Never write "Dominion" into save data.</summary>
    public string Path = "";

    /// <summary>"", "Victory", "Defeat". Written only when the finale resolves.
    /// CampusScreen.BeginNextCycle reads this to archive a true LoopRecord
    /// instead of the hardcoded "ConvergenceDefeat" it used before v102.</summary>
    public string Outcome = "";

    /// <summary>Companion ids assigned to secondary fronts in Phase 2 (non-party).</summary>
    public List<string> FrontAssignments = new();

    /// <summary>Aggregate morale from the Gathering, bounded ±3; applied as a
    /// combat-multiplier tweak (spec §5).</summary>
    public int Morale = 0;

    /// <summary>One-use shard invocations still unspent (archmage ids, from
    /// Overthrown seats).</summary>
    public List<string> InvocationsRemaining = new();

    /// <summary>Seal Invocations (fragment keys) not yet spent this event.</summary>
    public List<string> FragmentInvocationsRemaining = new();

    /// <summary>Set once the co-conspirator's second-betrayal / redemption beat
    /// has played, so it cannot repeat across a mid-finale reload.</summary>
    public bool MirrorBeatPlayed = false;

    /// <summary>Per-phase combat results for the Aftermath recap
    /// ("won_clean", "won_bloodied", …).</summary>
    public Dictionary<int, string> PhaseResults = new();

    /// <summary>True once the finale has been entered at all — the state a
    /// mid-finale quit leaves behind. The Conjunction gate reads this (with an
    /// empty Outcome) to offer "Return to the Convergence" instead of the
    /// ordinary press-your-luck choice: once entered, the Convergence is the
    /// only thing left in that timeline.</summary>
    public bool InProgress => Phase >= 1 && string.IsNullOrEmpty(Outcome);

    /// <summary>True when the finale has resolved, either way.</summary>
    public bool Resolved => !string.IsNullOrEmpty(Outcome);
}
