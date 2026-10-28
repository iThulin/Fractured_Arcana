using Godot;

// ============================================================
// NarrativeOutcome.cs
//
// Purpose:        A narrative panel's choice, applied to the save away
//                 from an expedition: gold, the event marked done, flags
//                 and meta flags, an item, a companion, reputation, lore,
//                 a companion arc's step and the chronicle's sway, then a
//                 save. The city's panels and the hall seen again
//                 (HallStage) both apply their choices here, so the two
//                 can never drift. Moved out of StrategicView (S3,
//                 docs/sundering_long_second_founding_v1.md section 5).
//                 HP and steps do not apply off an expedition, and
//                 ResolutionKind and LaunchGuardian choices must not come
//                 here (the Council panel keeps its own).
// Layer:          Systems (static)
// Collaborators:  StrategicView.ApplyNarrativeOutcome, HallStage,
//                 CompanionArcTracker, ChronicleScheduler.ApplySway,
//                 SaveManager
// ============================================================

/// <summary>Applies a narrative choice to the save (see the file header).</summary>
public static class NarrativeOutcome
{
    /// <summary>Applies <paramref name="choice"/> of <paramref name="enc"/> to the active save and
    /// saves. Returns the companion arc's step when the choice moved one (the caller shows it), else
    /// null.</summary>
    public static CompanionArcStatus Apply(NarrativeEncounterData enc, EncounterChoice choice)
    {
        var save = SaveManager.ActiveSave;
        if (choice == null || save == null)
            return null;

        if (choice.GoldDelta != 0)
            save.Gold = Mathf.Max(0, save.Gold + choice.GoldDelta);

        if (enc != null && !string.IsNullOrEmpty(enc.Id)
            && !save.CompletedEvents.Contains(enc.Id))
            save.CompletedEvents.Add(enc.Id);

        if (choice.SetFlags != null)
            foreach (var flag in choice.SetFlags)
                if (!string.IsNullOrEmpty(flag))
                    save.SetFlag(flag);

        if (choice.SetMetaFlags != null && save.Ledger != null)
            foreach (var flag in choice.SetMetaFlags)
                if (!string.IsNullOrEmpty(flag) && !save.Ledger.MetaNarrativeFlags.Contains(flag))
                    save.Ledger.MetaNarrativeFlags.Add(flag);

        if (!string.IsNullOrEmpty(choice.ItemReward))
        {
            var item = ItemDatabase.Get(choice.ItemReward);
            if (item != null)
                save.Armory.AddItem(item);
        }

        if (!string.IsNullOrEmpty(choice.CompanionUnlock))
            CompanionRoster.GrantFromEncounter(choice.CompanionUnlock);

        if (!string.IsNullOrEmpty(choice.ReputationFactionId) && choice.ReputationAmount != 0)
        {
            save.FactionReputation.TryGetValue(choice.ReputationFactionId, out int current);
            save.FactionReputation[choice.ReputationFactionId] = current + choice.ReputationAmount;
        }

        if (!string.IsNullOrEmpty(choice.LoreId) && !save.UnlockedLoreEntries.Contains(choice.LoreId))
            save.UnlockedLoreEntries.Add(choice.LoreId);

        var arc = CompanionArcTracker.TryCompleteByEncounter(enc?.Id, save);

        // C5: the choice's sway, after its other effects.
        ChronicleScheduler.ApplySway(choice);

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        return arc;
    }

    /// <summary>The line for a companion arc's step: complete, or how far it has come.</summary>
    public static string ArcLine(CompanionArcStatus arc)
    {
        if (arc == null)
            return "";
        return arc.IsComplete
            ? $"{arc.CompanionName}: \"{arc.ArcName}\" complete."
            : $"{arc.CompanionName}: \"{arc.ArcName}\" advances ({arc.CurrentStage}/{arc.TotalStages}).";
    }
}
