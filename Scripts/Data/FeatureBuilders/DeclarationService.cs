using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// DeclarationService.cs
//
// Purpose:        The faculty gate. You begin the game UNDECLARED, as an
//                 Adept, a graduate whose conferral was interrupted
//                 mid-sentence. Every other discipline must be declared,
//                 and a university teaches what it has teachers for.
//
//                 Two legs, both of which already exist in the data:
//                   1. A FACULTY SOURCE for that school: a recruited
//                      companion past arc stage 2, or an Allied archmage.
//                      The roster is a clean 1:1 grid: exactly one arcane
//                      companion and exactly one archmage per school, all
//                      eight covered, no holes.
//                   2. SchoolMastery >= DeclarableThreshold, so a single
//                      lucky recruit cannot unlock a school the player has
//                      never touched.
//
//                 Plus the Grand Hall, which is where it happens: the room
//                 the conferral stopped in. Its authored description already
//                 promises "school of study".
//
//                 NOT gated on shards: that would lock the player into the
//                 one school with no attunement engine for a full cycle or
//                 two. See design doc §7a.
//
// Layer:          Data / Feature builder
// Collaborators:  EternalLedger.MetaNarrativeFlags (declared_<school>),
//                 SchoolMasteryService.cs (the threshold leg),
//                 CampusGuildPanel.cs (the Declare screen),
//                 CampusExpeditionPanel.cs (gates the new-cycle picker),
//                 NewGameScreen.cs (forces the Adept start),
//                 ArchmageRegistry.cs, Companion (faculty sources)
// See:            docs/progression_card_acquisition_v1.md §7
// ============================================================

/// <summary>Why a school can or cannot be declared right now. Purely a report, since computing it has no side effects.</summary>
public readonly struct DeclarationStatus
{
    /// <summary>Already declared, so this school is playable.</summary>
    public bool Declared { get; init; }

    /// <summary>Every requirement is met; Declare() would succeed.</summary>
    public bool Eligible { get; init; }

    /// <summary>Who can teach it, e.g. "Maren Gravesong (arc 3)". Null when nobody can.</summary>
    public string FacultySource { get; init; }

    public int MasteryPoints { get; init; }
    public int MasteryRequired { get; init; }

    /// <summary>One player-facing sentence naming what is missing. Empty when Declared or Eligible.</summary>
    public string Blocker { get; init; }
}

/// <summary>
/// Reads and writes the set of declared disciplines. Declaration is permanent
/// (it lives on the EternalLedger) but ELIGIBILITY is evaluated against the
/// current cycle, because companions and archmage dispositions are timeline
/// state. You need a teacher now; once taught, you know it forever.
/// </summary>
public static class DeclarationService
{
    /// <summary>The discipline every guild begins in, and the only one that needs no declaring.</summary>
    public const string StartingSchool = "Adept";

    /// <summary>Arc stage a companion must reach before they will teach you. 0 = not started, 4 = complete.</summary>
    public const int FacultyArcStage = 2;

    private const string DeclaredPrefix = "declared_";

    private const string GrandHallId = "grand_hall";

    // ── Read ─────────────────────────────────────────────────────────────

    public static string DeclaredFlag(string school) => DeclaredPrefix + Norm(school);

    /// <summary>
    /// True when the school is playable. Adept is always true, because it is where you
    /// start, and a save that predates this system has no declared_ flags at all,
    /// so without this special case an existing guild would have nothing to play.
    /// </summary>
    public static bool IsDeclared(GuildSaveData save, string school)
    {
        if (string.IsNullOrWhiteSpace(school)) return false;
        string key = Norm(school);

        if (key == StartingSchool) return true;

        // Grandfather clause: whatever school an existing save is currently
        // running counts as declared. Otherwise this change would strand every
        // guild created before it, mid-cycle, in a school it cannot select.
        if (string.Equals(save?.Cycle?.SelectedSchool, key, StringComparison.OrdinalIgnoreCase))
            return true;

        return save?.Ledger?.MetaNarrativeFlags?.Contains(DeclaredFlag(key)) ?? false;
    }

    /// <summary>Every playable school, Adept first.</summary>
    public static List<string> DeclaredSchools(GuildSaveData save)
    {
        var result = new List<string>();
        foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
        {
            string name = s.ToString();
            if (IsDeclared(save, name)) result.Add(name);
        }
        return result;
    }

    /// <summary>
    /// Who, in this timeline, could teach <paramref name="school"/>, or null.
    /// A recruited, living companion of that school past arc stage 2, else an
    /// Allied archmage of that school. Companions are checked first because
    /// they are the earlier and more likely source.
    /// </summary>
    public static string FindFacultySource(GuildSaveData save, string school)
    {
        if (save?.Cycle == null || string.IsNullOrWhiteSpace(school)) return null;
        string key = Norm(school);

        var companion = save.Cycle.Companions?
            .FirstOrDefault(c => c != null
                                 && c.IsRecruited
                                 && !c.IsPermadead
                                 && c.ArcStage >= FacultyArcStage
                                 && string.Equals(Norm(c.School), key, StringComparison.Ordinal));

        if (companion != null)
            return $"{companion.Name} (arc {companion.ArcStage})";

        var dispositions = save.Cycle.Campaign?.Dispositions;
        if (dispositions != null)
        {
            foreach (var kvp in dispositions)
            {
                if (kvp.Value != ArchmageDisposition.Allied) continue;

                var def = ArchmageRegistry.Get(kvp.Key);
                if (def == null) continue;
                if (!string.Equals(Norm(def.School), key, StringComparison.Ordinal)) continue;

                string name = string.IsNullOrWhiteSpace(def.DisplayName) ? kvp.Key : def.DisplayName;
                return $"{name} (allied)";
            }
        }

        return null;
    }

    /// <summary>
    /// True when this school's archmage is resolved but NOT Allied, so coerced or
    /// overthrown. They pay SchoolMastery (ProgressionSweep credits any resolution)
    /// but they will not teach, and the player deserves to be told that directly
    /// rather than read "bring them to your side" about someone already in chains.
    /// </summary>
    private static bool HasUnwillingArchmage(GuildSaveData save, string school)
    {
        var dispositions = save?.Cycle?.Campaign?.Dispositions;
        if (dispositions == null) return false;

        foreach (var kvp in dispositions)
        {
            if (kvp.Value != ArchmageDisposition.Coerced &&
                kvp.Value != ArchmageDisposition.Overthrown) continue;

            var def = ArchmageRegistry.Get(kvp.Key);
            if (def != null && string.Equals(Norm(def.School), Norm(school), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Is the Grand Hall standing? Declaration happens nowhere else.</summary>
    /// <remarks>
    /// Checks <c>IsFunctional</c> (Tier &gt; 0 AND IsPlaced), not Tier alone. That is the
    /// codebase's stated rule for anything gating a building's EFFECTS. A hall you
    /// own but have never sited on the campus map is not a room you can stand in.
    /// </remarks>
    public static bool HasGrandHall(GuildSaveData save) =>
        save?.Ledger?.Buildings?.Any(b => b != null
                                          && string.Equals(b.Id, GrandHallId, StringComparison.OrdinalIgnoreCase)
                                          && b.IsFunctional) ?? false;

    /// <summary>
    /// Full report for one school. Blocker names the single most actionable
    /// missing thing rather than listing all of them: "you need a teacher" is
    /// a quest; "you need a teacher and 4 more mastery and a building" is a wall.
    /// </summary>
    public static DeclarationStatus Evaluate(GuildSaveData save, string school)
    {
        string key = Norm(school);
        int points = SchoolMasteryService.Points(save, key);
        int required = SchoolMasteryService.DeclarableThreshold;

        if (IsDeclared(save, key))
        {
            return new DeclarationStatus
            {
                Declared = true,
                Eligible = true,
                FacultySource = FindFacultySource(save, key),
                MasteryPoints = points,
                MasteryRequired = required,
                Blocker = "",
            };
        }

        string faculty = FindFacultySource(save, key);

        string blocker;
        if (!HasGrandHall(save))
            blocker = "The Grand Hall must stand before any name can be conferred.";
        else if (faculty == null && HasUnwillingArchmage(save, key))
            // RULED: only an ALLIED archmage teaches. You can compel someone to
            // fight beside you; you cannot compel them to make you their student.
            // Say so explicitly rather than repeating the generic line. A player
            // who coerced or overthrew that seat has done the work and deserves to
            // know why it bought them nothing here.
            blocker = $"The {key} archmage is yours by force, not by choice, and no one " +
                      $"teaches under duress. Find a {key} companion, or win that seat again, " +
                      $"willingly, in another timeline.";
        else if (faculty == null)
            blocker = $"No one here can teach {key}. Bring a {key} companion to the second stage " +
                      $"of their story, or bring the {key} archmage to your side.";
        else if (points < required)
            blocker = $"You have watched, but not yet learned. {key} mastery {points}/{required}.";
        else
            blocker = "";

        return new DeclarationStatus
        {
            Declared = false,
            Eligible = blocker.Length == 0,
            FacultySource = faculty,
            MasteryPoints = points,
            MasteryRequired = required,
            Blocker = blocker,
        };
    }

    // ── Write ────────────────────────────────────────────────────────────

    /// <summary>
    /// Permanently declare whatever school an existing save is currently running,
    /// bypassing the gate. Called on load.
    ///
    /// The dynamic clause in <see cref="IsDeclared"/> would keep such a guild
    /// playable, but only for as long as it stayed in that school. Switch to
    /// Adept for one cycle and the school they had played for ten hours would
    /// lock behind a gate that did not exist when they earned it. Writing the
    /// flag makes the grandfathering permanent instead of incidental.
    ///
    /// No-op for Adept and for any save that already has the flag.
    /// </summary>
    public static bool GrandfatherCurrentSchool(GuildSaveData save)
    {
        string current = Norm(save?.Cycle?.SelectedSchool);
        if (string.IsNullOrEmpty(current) || current == StartingSchool) return false;
        if (save?.Ledger == null) return false;

        save.Ledger.MetaNarrativeFlags ??= new List<string>();
        string flag = DeclaredFlag(current);
        if (save.Ledger.MetaNarrativeFlags.Contains(flag)) return false;

        save.Ledger.MetaNarrativeFlags.Add(flag);
        GD.Print($"[Declaration] Grandfathered '{current}'. This guild was already " +
                 $"studying it before the faculty gate existed.");
        return true;
    }

    /// <summary>
    /// Declare a discipline. Re-checks eligibility rather than trusting the
    /// caller, so a stale button cannot confer a name that was not earned.
    /// Returns false (and logs why) if the requirements are not met.
    /// Idempotent: declaring an already-declared school is a no-op success.
    /// </summary>
    public static bool Declare(GuildSaveData save, string school)
    {
        if (save?.Ledger == null || string.IsNullOrWhiteSpace(school)) return false;
        string key = Norm(school);

        // Test the PERSISTED flag, not IsDeclared. IsDeclared also returns true
        // for the currently-selected school (the grandfather clause), so checking
        // it here would make Declare a silent no-op for the school you are
        // playing, returning success without ever writing the durable flag.
        // Today GrandfatherCurrentSchool covers that on load, but the two should
        // not be invisibly coupled: this path must always converge on the flag.
        if (key == StartingSchool) return true;
        if (save.Ledger.MetaNarrativeFlags?.Contains(DeclaredFlag(key)) == true) return true;

        var status = Evaluate(save, key);
        if (!status.Eligible)
        {
            GD.PrintErr($"[Declaration] Refused '{key}': {status.Blocker}");
            return false;
        }

        save.Ledger.MetaNarrativeFlags ??= new List<string>();
        save.Ledger.MetaNarrativeFlags.Add(DeclaredFlag(key));

        // Declaring is itself a deed. It is the moment the school becomes yours.
        SchoolMasteryService.AddMilestone(save, key, $"declared_{key}");

        GD.Print($"[Declaration] {key} DECLARED (taught by {status.FacultySource}, " +
                 $"mastery {status.MasteryPoints}/{status.MasteryRequired}).");
        return true;
    }

    // ── Flavor ───────────────────────────────────────────────────────────

    /// <summary>
    /// The Provost's line from Beat 2, finally finishing. The player gets to
    /// spend this moment seven times, once per discipline, in a different voice
    /// each time (see narrative_frame_intro_finale_v1 §3 Beat 2 and §6).
    /// </summary>
    public static string ConferralLine(string school, string facultySource)
    {
        string who = string.IsNullOrWhiteSpace(facultySource) ? "someone who stayed" : facultySource;
        return $"\"…we confer upon you the name you have earned. Step forward and be written.\"\n\n" +
               $"The sentence finishes. {who} speaks the half of it the Provost never reached.\n" +
               $"You are {Norm(school)} now.";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string Norm(string school)
    {
        if (string.IsNullOrWhiteSpace(school)) return "";
        return Enum.TryParse<CardSchool>(school.Trim(), ignoreCase: true, out var s)
            ? s.ToString()
            : school.Trim();
    }
}
