using System;
using System.Collections.Generic;

// ============================================================
// CardChoice.cs
//
// Purpose:        The post-cast player-choice seam. An effect that
//                 cannot finish without asking the player something
//                 publishes a CardChoiceRequest and RETURNS; the
//                 request is serviced after the resolution unwinds,
//                 and the rest of the work happens in a continuation.
// Layer:          Runtime (rules-adjacent; no Godot nodes)
// Collaborators:  GameStateManager.cs (holds the slot + the seam),
//                 RulesManager.cs (Resolver publishes it),
//                 CompositeEffects.cs (SequenceEffect chains onto it),
//                 CombatManager.CardChoice.cs (services it),
//                 ChronomancerEffects.cs (ScryEffect, the first caller)
// See:            claude/u3e_playtest_results_2026-07-28.md — the
//                 "no post-cast player-choice machinery" finding
// ============================================================

/// <summary>One question the game must ask the player before an effect can finish.
///
/// WHY THIS EXISTS, and why it is a continuation rather than a pause.
/// <c>IEffect.Resolve</c> returns <c>void</c> and <c>Resolver.ResolveTop</c> is
/// synchronous, so an effect physically cannot await player input. The two ways out
/// were (a) make the whole resolution pipeline async — every effect class and every
/// call site — or (b) let the effect publish what it needs and hand the remainder to
/// a callback. (b) is also the idiom this codebase already uses for exactly this
/// shape of problem: <c>GameState.OnSummonRequested</c> and
/// <c>GameState.OnDrawCards</c> are both "the rules layer asks, CombatManager
/// answers" seams. This is the third.
///
/// THE ORDERING TRAP, and how it is closed. A continuation by definition runs after
/// its effect returned — so in <c>[scry, draw]</c> the draw would happen before the
/// player had chosen. That is not hypothetical: five of the nine authored scry
/// sequences have steps after the scry. <see cref="SequenceEffect"/> therefore
/// CHAINS: when a step leaves a request in <c>GameState.PendingChoice</c>, the
/// sequence folds its own remaining steps into <see cref="OnChosen"/> and stops.
/// Ordering is preserved by construction, not by authoring discipline.
///
/// A request is only ever a REQUEST. If nothing services the seam — headless tests,
/// an AI cast, a preview — <see cref="Resolve"/> takes the default and the game
/// continues; a choice must never be able to wedge a fight.</summary>
public sealed class CardChoiceRequest
{
    /// <summary>Short heading, e.g. "Precognition".</summary>
    public string Title = "";
    /// <summary>What the player is deciding, in plain language.</summary>
    public string Prompt = "";
    /// <summary>Whose cards these are — the picker shows and mutates this unit's deck.</summary>
    public Unit Owner;
    /// <summary>The revealed cards, in the order they came off the pile.</summary>
    public List<Card> Candidates = new();
    /// <summary>How many the player must pick. Clamped to Candidates.Count.</summary>
    public int PickCount = 1;
    /// <summary>Log/telemetry tag for the requesting effect.</summary>
    public string Source = "Choice";

    /// <summary>Runs once with the chosen cards. Everything the effect could not do
    /// before the player answered belongs here — including, via SequenceEffect
    /// chaining, the later steps of the sequence the effect was part of.</summary>
    public Action<List<Card>> OnChosen;

    /// <summary>True when there is no decision to make — the player would be picking
    /// every candidate. The seam resolves these immediately and shows no UI: a modal
    /// with one legal answer is a click the player cannot act on, which is the same
    /// anti-click-fatigue rule R3 applies to priority windows.</summary>
    public bool IsDegenerate => Candidates == null || Candidates.Count <= PickCount;

    /// <summary>The default answer — the first <see cref="PickCount"/> candidates.
    /// Used for degenerate requests and as the fallback when no UI is listening.</summary>
    public List<Card> DefaultPick()
    {
        var picked = new List<Card>();
        if (Candidates == null)
            return picked;
        for (int i = 0; i < Candidates.Count && picked.Count < PickCount; i++)
            if (Candidates[i] != null)
                picked.Add(Candidates[i]);
        return picked;
    }

    /// <summary>Fires the continuation exactly once. Null-safe and idempotent, because
    /// a continuation that runs twice would draw twice.</summary>
    public void Complete(List<Card> chosen)
    {
        var cb = OnChosen;
        if (cb == null)
            return;
        OnChosen = null;                 // one-shot
        cb(chosen ?? new List<Card>());
    }

    /// <summary>Folds extra work onto the end of the continuation, preserving order.
    /// SequenceEffect uses this to append its remaining steps.</summary>
    public void Then(Action<List<Card>> more)
    {
        if (more == null)
            return;
        var first = OnChosen;
        OnChosen = chosen =>
        {
            first?.Invoke(chosen);
            more(chosen);
        };
    }
}
