using System;
using System.Collections.Generic;

// ============================================================
// Statecraft.cs
//
// Purpose:        The Embassy's doctrines and Broker the Compact
//                 (campus_building_upgrades_design_v1 §17).
//
//                 Broker the Compact (Embassy III mission, automated for now;
//                 the interactive table is later): a Trusted court signs a
//                 standing treaty with the guild (CourtState.CompactBrokered).
//                 At an archmage's court whose kingdom is corrupted no further
//                 than the archmage's MaxCorruptionForUnite, the Seat is
//                 United by it (the court path of the council spec §10).
//
//                 Embassy T3 doctrines:
//                   Patronage   depth at a few courts: a court may hold two
//                               sworn Patrons, and the guild one more seat
//                               overall. Every Patron brings their token card
//                               to each parley in their kingdom.
//                   Concordat   breadth through allies: every court the guild
//                               has a Compact with sends its soldiers to the
//                               guild's Hold the Line postings, moving the
//                               front further each moon (not against itself).
// Layer:          System (campaign)
// Collaborators:  Charters (the gate), CouncilQueries (slots), CouncilTick
//                 (mission resolution), FieldPostings (Hold the Line),
//                 NegotiationManager (patron tokens), CampaignState (Unite).
// ============================================================

/// <summary>Embassy doctrine rules and the Compact's numbers. Stateless.</summary>
public static class Statecraft
{
    public const string EmbassyId = "embassy";
    public const string Patronage = "patronage";
    public const string Concordat = "concordat";

    /// <summary>Patron seats per court with and without Patronage.</summary>
    public const int PatronSeatsBase = 1;
    public const int PatronSeatsPatronage = 2;

    /// <summary>Guild-wide Patron seats Patronage adds.</summary>
    public const int PatronageExtraSlots = 1;

    // Broker the Compact (starting values).
    public const int CompactLunations = 3;
    public const int CompactGold = 300;

    // Concordat: what each allied court's soldiers add to a held line.
    public const int ConcordatAdvancePerCompact = 3;
    public const int ConcordatAdvanceCap = 6;

    public static bool PatronageActive(GuildSaveData save) => Charters.IsActive(save, EmbassyId, Patronage);

    public static bool ConcordatActive(GuildSaveData save) => Charters.IsActive(save, EmbassyId, Concordat);

    /// <summary>The courts whose soldiers answer a Hold the Line posting on this
    /// front: every court with a Compact, except the kingdom the guild is
    /// fighting there. Empty without the Concordat.</summary>
    public static List<string> ConcordatCourts(CycleState cycle, GuildSaveData save, Warfront wf, bool defend)
    {
        var list = new List<string>();
        var courts = cycle?.Council?.Courts;
        if (courts == null || wf == null || !ConcordatActive(save))
        {
            return list;
        }
        string enemy = defend ? wf.AggressorKingdomId : wf.DefenderKingdomId;
        foreach (var kv in courts)
        {
            if (kv.Value != null && kv.Value.CompactBrokered && kv.Key != enemy)
            {
                list.Add(kv.Key);
            }
        }
        list.Sort(string.CompareOrdinal);
        return list;
    }

    /// <summary>Extra front movement the allied courts bring this moon.</summary>
    public static int ConcordatAdvance(int courts)
        => Math.Min(ConcordatAdvanceCap, Math.Max(0, courts) * ConcordatAdvancePerCompact);

    /// <summary>The archmage seated over a court's kingdom, or "" for a regent
    /// court or an unmapped kingdom.</summary>
    public static string SeatArchmageId(CycleState cycle, CourtState court)
    {
        if (court == null || court.IsRegentCourt || cycle?.Kingdoms == null
            || !cycle.Kingdoms.TryGetValue(court.KingdomId, out var ks)
            || string.IsNullOrEmpty(ks.TemplateRegionId))
        {
            return "";
        }
        return cycle.Campaign?.GetArchmageForRegion(ks.TemplateRegionId) ?? "";
    }

    /// <summary>Why the Compact cannot be brokered at this court now, or null.
    /// The band, Embassy tier and contact gates live on the mission def; this
    /// adds the checks a def cannot express.</summary>
    public static string CannotBrokerReason(CycleState cycle, CourtState court)
    {
        if (court == null)
        {
            return "No court.";
        }
        if (court.CompactBrokered)
        {
            return "The guild already has a Compact with this court.";
        }
        return null;
    }
}
