using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CastMasteryTracker.cs
//
// Purpose:        Increments cast count on the OwnedCard 
//                 matching the played blueprint. School-specific
//                 mastery. Generics increment under the active
//                 wizard's school tag. Dirty-flags the save for
//                 next campus write; does not save mid-run.
// Layer:          System
// Collaborators:  GameRunner.cs (call site),
//                 ExpeditionManager.cs (the interval's held casts, O3b),
//                 GuildSaveData / OwnedCard (mutation),
//                 SaveManager.cs (marks dirty)
// ============================================================

public static class CastMasteryTracker
{
    /// <summary>Standing Orders O3b (D206): the casts made in a field fight while the interval
    /// walks, held back until the fight ends. A fight lost in the interval unmakes nothing (D147,
    /// D195), so it can be tried again; if its casts counted, the field store could be lost on
    /// purpose, again and again, to raise mastery. ExpeditionManager records them when the fight is
    /// won (CommitInterval) and lets them go when it is lost (DropInterval).</summary>
    private static readonly List<string> _intervalCasts = new();
    private static bool _committing;

    /// <summary>
    /// Call this every time a card half is successfully cast.
    /// blueprintId is the card's id field from JSON.
    /// </summary>
    public static void RecordCast(string blueprintId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return;
        if (!_committing && PlayerSession.IsOnExpedition && Interval.Walking(save.Cycle))
        {
            _intervalCasts.Add(blueprintId);
            return;
        }

        // PERMANENT record first, and unconditionally. This must not depend on a
        // matching OwnedCard existing: the per-copy counter below lives on
        // CycleState.PlayerDeck and is destroyed every cycle, which is exactly the
        // defect this line fixes. Recording here also covers casts of cards that
        // have no owned copy at all, companion-contributed cards for instance.
        // See CardMasteryService for the three-way "mastery" naming warning.
        CardMasteryService.RecordCast(save, blueprintId);
        SaveManager.MarkDirty();

        if (save.PlayerDeck?.Cards == null) return;

        // Find the lowest-tier copy, same logic as the upgrade screen
        OwnedCard target = null;
        foreach (var card in save.PlayerDeck.Cards)
        {
            if (!string.Equals(card.BlueprintId, blueprintId,
                StringComparison.OrdinalIgnoreCase)) continue;
            if (target == null || card.PointsSpent < target.PointsSpent)
                target = card;
        }

        if (target == null) return;

        target.CastCount++;
        SaveManager.MarkDirty(); // don't save mid-run, just flag it

        GD.Print($"[Mastery] {blueprintId} cast count: {target.CastCount}");
    }

    /// <summary>Standing Orders O3b (D206): a fight won in the interval. Records the casts it held
    /// back, each as it would have been recorded when it was made.</summary>
    public static void CommitInterval()
    {
        if (_intervalCasts.Count == 0)
        {
            return;
        }
        var casts = new List<string>(_intervalCasts);
        _intervalCasts.Clear();
        _committing = true;
        try
        {
            foreach (var id in casts)
            {
                RecordCast(id);
            }
        }
        finally
        {
            _committing = false;
        }
        GD.Print($"[Mastery] {casts.Count} cast(s) from a fight won in the interval recorded.");
    }

    /// <summary>Standing Orders O3b (D206): a fight lost in the interval. Its casts are not
    /// recorded.</summary>
    public static void DropInterval()
    {
        if (_intervalCasts.Count == 0)
        {
            return;
        }
        GD.Print($"[Mastery] {_intervalCasts.Count} cast(s) from a fight lost in the interval are not recorded.");
        _intervalCasts.Clear();
    }
}