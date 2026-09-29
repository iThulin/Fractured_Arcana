using Godot;

// ============================================================
// ShardPaths.cs: the ways past a guardian that are not a fight (2026-09-27)
//
// Purpose:        The first two rows of shard_acquisition_spec_v1 section 3,
//                 built on the survey the field party can now make. Take is
//                 the guardian fight, always available. COMMUNE: a surveyed
//                 zone whose fragment answers the guild's own school (or one
//                 the guild is Fluent in) opens its gate without a fight.
//                 SANCTIONED: an Allied kingdom lets the guild through its
//                 own zone's gate. Both stamp a provenance flag so the
//                 Convergence can later know how each shard was won.
//
//                 Not built: Forced key, Inheritance, Reclamation, the
//                 Reliquary raid, and the blight clock. Each is a row in the
//                 same matrix and lands in PeacefulPassage when it does.
// Layer:          Systems / Overworld
// Collaborators:  ShardZone (Surveyed), ProgressionSweep (fragment school),
//                 SchoolMasteryService (Fluency), CouncilQueries (stance),
//                 EternalLedger.MetaNarrativeFlags (provenance),
//                 ExpeditionManager.TryHandleShardZone (the gate)
// ============================================================

public static class ShardPaths
{
    public const string ProvenanceCommuned = "communed";
    public const string ProvenanceSanctioned = "sanctioned";
    public const string ProvenanceTaken = "taken";

    /// <summary>Is there a way through this zone's gate that is not a fight?
    /// <paramref name="how"/> names it (a provenance) and <paramref name="why"/>
    /// says it in words for the lens.</summary>
    public static bool PeacefulPassage(GuildSaveData save, ShardZone zone, out string how, out string why)
    {
        how = "";
        why = "";
        var cycle = save?.Cycle;
        if (cycle == null || zone == null || zone.GuardianCleared)
        {
            return false;
        }

        // Commune needs the survey: you do not talk to a warden you have not
        // learned the shape of.
        if (zone.Surveyed)
        {
            string school = ProgressionSweep.SchoolOfFragment(zone.FragmentKey);
            bool own = !string.IsNullOrEmpty(school)
                       && string.Equals(school, cycle.SelectedSchool, System.StringComparison.OrdinalIgnoreCase);
            bool fluent = !string.IsNullOrEmpty(school) && SchoolMasteryService.IsFluent(save, school);
            if (own || fluent)
            {
                how = ProvenanceCommuned;
                why = own
                    ? $"The warden of {zone.Name} answers to your own school. Surveyed and known, it lets you pass."
                    : $"You are fluent enough in the {school} art to speak with the warden of {zone.Name}. It lets you pass.";
                return true;
            }
        }

        // Sanctioned: the kingdom whose ground this is trusts the guild.
        if (!string.IsNullOrEmpty(zone.KingdomId)
            && CouncilQueries.StanceFor(cycle, zone.KingdomId) == KingdomStance.Allied)
        {
            how = ProvenanceSanctioned;
            why = $"{CouncilTick.CourtDisplayName(cycle, zone.KingdomId)} sanctions the guild's passage. The warden stands aside.";
            return true;
        }
        return false;
    }

    /// <summary>Open the gate without a fight: the same flags the guardian's
    /// fall stamps, plus the provenance. Idempotent.</summary>
    public static void OpenGate(GuildSaveData save, ShardZone zone, string how)
    {
        if (save?.Ledger == null || zone == null)
        {
            return;
        }
        zone.GuardianCleared = true;
        string trial = $"{zone.FragmentKey}_trial_passed";
        if (!save.Ledger.MetaNarrativeFlags.Contains(trial))
        {
            save.Ledger.MetaNarrativeFlags.Add(trial);
        }
        StampProvenance(save, zone.FragmentKey, how);
        SaveManager.MarkDirty();
        GD.Print($"[ShardPaths] {zone.Name}: gate opened, {how}.");
    }

    /// <summary>fragment_&lt;key&gt;_provenance_&lt;how&gt;, once. A shard taken by the
    /// sword after a peaceful gate keeps the peaceful provenance: the gate is
    /// where the manner was decided.</summary>
    public static void StampProvenance(GuildSaveData save, string fragmentKey, string how)
    {
        if (save?.Ledger == null || string.IsNullOrEmpty(fragmentKey) || string.IsNullOrEmpty(how))
        {
            return;
        }
        string prefix = $"fragment_{fragmentKey}_provenance_";
        foreach (var f in save.Ledger.MetaNarrativeFlags)
        {
            if (f.StartsWith(prefix))
            {
                return;
            }
        }
        save.Ledger.MetaNarrativeFlags.Add(prefix + how);
    }
}
