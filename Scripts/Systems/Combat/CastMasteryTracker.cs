using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CastMasteryTracker.cs
//
// Purpose:        Increments cast count on the OwnedCard 
//                 matching the played blueprint. School-specific
//                 mastery — generics increment under the active
//                 wizard's school tag. Dirty-flags the save for
//                 next campus write; does not save mid-run.
// Layer:          System
// Collaborators:  GameRunner.cs (call site),
//                 GuildSaveData / OwnedCard (mutation),
//                 SaveManager.cs (marks dirty)
// ============================================================

public static class CastMasteryTracker
{
    /// <summary>
    /// Call this every time a card half is successfully cast.
    /// blueprintId is the card's id field from JSON.
    /// </summary>
    public static void RecordCast(string blueprintId)
    {
        var save = SaveManager.ActiveSave;
        if (save?.PlayerDeck?.Cards == null) return;

        // Find the lowest-tier copy — same logic as upgrade screen
        OwnedCard target = null;
        foreach (var card in save.PlayerDeck.Cards)
        {
            if (!string.Equals(card.BlueprintId, blueprintId, 
                StringComparison.OrdinalIgnoreCase)) continue;
            if (target == null || card.UpgradeTier < target.UpgradeTier)
                target = card;
        }

        if (target == null) return;

        target.CastCount++;
        SaveManager.MarkDirty(); // don't save mid-run, just flag it

        GD.Print($"[Mastery] {blueprintId} cast count: {target.CastCount}");
    }
}