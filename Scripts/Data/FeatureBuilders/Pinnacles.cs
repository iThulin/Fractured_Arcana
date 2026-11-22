using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// Pinnacles.cs: what a held shard gives every class (shardfall H6c)
//
// Purpose:        docs/shardfall_v1.md, reward G3. Each of the six shards
//                 raises a pinnacle on the campus: a permanent boon,
//                 flavoured by the shard's school, that every class the
//                 guild plays has from then on. This file is the one place
//                 that names them and answers "does the guild have it".
//
//                 A pinnacle is held from the moment its shard is picked up
//                 (ruled 2026-11-22: rewards fire on pickup, and seating the
//                 shard in the hall stays the ceremony). It is a fact on the
//                 permanent ledger (fragment_<key>_collected), not a
//                 building the player constructs, so it cannot be lost,
//                 sold or left unbuilt. It is not yet drawn on the campus.
//
//                 The Pattern Foundry's first boon ("campus buildings begin
//                 each timeline one tier higher") assumed buildings reset
//                 each timeline, and they do not. Ruled 2026-11-22: it
//                 enlarges the founding purse instead.
// Layer:          Data / Feature builder
// Collaborators:  TimelineAnchors (the Ossuary), SaveManager and
//                 CycleInitializer (the Stilled Hour, the Oathhall),
//                 CouncilState (PinnacleStanding), CardRewardScreen (the
//                 Athenaeum), ExpeditionManager (the Hearth), SaveManager
//                 (the Foundry's purse in BeginNewCycle)
// See:            docs/shardfall_v1.md section 3, docs/shardfall_h6_rewards_audit.md
// ============================================================

/// <summary>The six pinnacles, by the fragment key of the shard that raises each.</summary>
public static class Pinnacles
{
    public const string Athenaeum = "axiom";
    public const string Oathhall = "binding";
    public const string Ossuary = "deathless";
    public const string StilledHour = "moment";
    public const string PatternFoundry = "schema";
    public const string Hearth = "primal";

    /// <summary>The Athenaeum Restored: one more card to choose from in every card reward.</summary>
    public const int AthenaeumExtraCards = 1;
    /// <summary>The Oathhall: added to every court's standing. The bands are about six wide,
    /// so this is one band.</summary>
    public const int OathhallStanding = 6;
    /// <summary>The Stilled Hour: one more moon before the Conjunction.</summary>
    public const int StilledHourMoons = 1;
    /// <summary>The Hearth of Storms: a party on foot carries this much more health.</summary>
    public const float HearthHealthMult = 1.25f;
    /// <summary>The Pattern Foundry: every timeline opens with this much more gold and
    /// materials.</summary>
    public const float FoundryPurseMult = 1.5f;

    private static readonly Dictionary<string, (string name, string boon)> Table = new()
    {
        [Athenaeum] = ("The Athenaeum Restored", "Every card reward offers one more card to choose from."),
        [Oathhall] = ("The Oathhall", "Every court begins each timeline one band warmer."),
        [Ossuary] = ("The Ossuary of Names", "One more Anchor in every timeline."),
        [StilledHour] = ("The Stilled Hour", "One more moon in every timeline before the Conjunction."),
        [PatternFoundry] = ("The Pattern Foundry", "Every timeline opens with half again as much gold and materials."),
        [Hearth] = ("The Hearth of Storms", "Every party on foot carries a quarter more health, and terrain and weather do it no harm."),
    };

    /// <summary>True once the guild has picked up the shard that raises this pinnacle.</summary>
    public static bool Has(GuildSaveData save, string fragmentKey)
        => save != null && !string.IsNullOrEmpty(fragmentKey) && save.HasFlag($"fragment_{fragmentKey}_collected");

    public static string NameOf(string fragmentKey)
        => fragmentKey != null && Table.TryGetValue(fragmentKey, out var row) ? row.name : "";

    /// <summary>What the pinnacle does, in the player's words. Empty for one with no boon yet.</summary>
    public static string BoonOf(string fragmentKey)
        => fragmentKey != null && Table.TryGetValue(fragmentKey, out var row) ? row.boon : "";

    /// <summary>The line shown when a shard is taken: the pinnacle and what it gives.</summary>
    public static string RaisedLine(string fragmentKey)
    {
        string name = NameOf(fragmentKey);
        if (name.Length == 0)
        {
            return "";
        }
        string boon = BoonOf(fragmentKey);
        return boon.Length > 0
            ? $"{name} rises on the campus, in this timeline and every one after. {boon}"
            : $"{name} rises on the campus, in this timeline and every one after.";
    }

    /// <summary>The Stilled Hour: lengthen a calendar that has just been made for a new
    /// timeline or a new year. Call once on a fresh calendar, never on a running one.</summary>
    public static void ApplyToFreshCalendar(GuildSaveData save, CalendarState calendar)
    {
        if (calendar != null && Has(save, StilledHour))
        {
            calendar.LunationsPerCycle += StilledHourMoons;
        }
    }

    /// <summary>The Oathhall: warm every court of a world that has just been generated.</summary>
    public static void ApplyToNewWorld(GuildSaveData save, CycleState cycle)
    {
        if (cycle?.Council?.Courts == null || !Has(save, Oathhall))
        {
            return;
        }
        foreach (var court in cycle.Council.Courts.Values)
        {
            if (court != null)
            {
                court.PinnacleStanding = OathhallStanding;
            }
        }
        GD.Print($"[Pinnacles] The Oathhall: {cycle.Council.Courts.Count} court(s) begin {OathhallStanding} standing warmer.");
    }

    /// <summary>The Pattern Foundry: the gold or materials a new timeline opens with.</summary>
    public static int FoundingAmount(GuildSaveData save, int baseAmount)
        => Has(save, PatternFoundry) ? Mathf.RoundToInt(baseAmount * FoundryPurseMult) : baseAmount;

    /// <summary>Health a party on foot sets out with, after the Hearth.</summary>
    public static int FieldHealth(GuildSaveData save, int baseHealth)
        => Has(save, Hearth) ? Mathf.RoundToInt(baseHealth * HearthHealthMult) : baseHealth;
}
