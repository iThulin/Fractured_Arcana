using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// Ascension.cs: the pact a timeline is played under (shardfall H6d)
//
// Purpose:        docs/shardfall_v1.md, reward G4. Each shard the guild holds
//                 opens one world rule, taken from that shard's own nature.
//                 A timeline is played under a pact: any set of the opened
//                 rules. Each rule carries a weight for how hard it is, and
//                 the pact's weights must add up to at least the floor, which
//                 rises with the shards held but more slowly (ruled
//                 2026-11-22: weighted switches, not a ladder).
//
//                 The six rules:
//                   axiom      Rivals answer in kind. For each overworld spell
//                              the guild casts, a realm carries out its plan
//                              once more at the next new moon (three at most).
//                   binding    Courts are bound together. An offence against
//                              one court is also heard, as a lesser one, in
//                              every court that borders it.
//                   deathless  Nothing stays beaten. A guard the guild breaks
//                              stands again two moons later.
//                   moment     The realms move faster. Each realm carries out
//                              its plan twice a moon.
//                   schema     The world rebuilds. Each new moon, every
//                              province with room posts another guard.
//                   primal     The seeps spread. A live vault seeps into the
//                              provinces beside its own as well.
//
//                 The pact is chosen as a timeline begins and held for all of
//                 it, through every year. There is no screen for choosing it
//                 yet: a new timeline takes the lightest pact that meets the
//                 floor (DefaultPact), and the debug tools can switch any rule.
//                 The weights are first guesses, to be set from play.
// Layer:          Data / Feature builder
// Collaborators:  CycleState (AscensionRules, AscensionAnswers), SaveManager
//                 (BeginNewCycle), KingdomTickSimulation.Plans (the new moon,
//                 moment, axiom), CouncilEcho (binding), Shardfall (primal),
//                 ExpeditionManager and WorldPoi.BrokenOnLunation (deathless),
//                 OverworldSpellManager and CastleManeuvers (NoteCast)
// See:            docs/shardfall_v1.md section 3, docs/shard_descents_v1.md
// ============================================================

/// <summary>The world rules a timeline can be played under, one per shard held.</summary>
public static class Ascension
{
    public const string Axiom = "axiom";
    public const string Binding = "binding";
    public const string Deathless = "deathless";
    public const string Moment = "moment";
    public const string Schema = "schema";
    public const string Primal = "primal";

    /// <summary>Realms that may answer the guild's spells at one new moon (axiom).</summary>
    public const int MaxAnswersAMoon = 3;
    /// <summary>Moons before a broken guard stands again (deathless).</summary>
    public const int GuardReturnMoons = 2;

    public static readonly string[] Keys = { Axiom, Binding, Deathless, Moment, Schema, Primal };

    private static readonly Dictionary<string, (int weight, string name, string rule)> Table = new()
    {
        [Axiom] = (1, "Rivals answer in kind",
            "For each overworld spell you cast, a realm carries out its plan once more at the next new moon (three at most)."),
        [Binding] = (1, "Courts are bound together",
            "An offence against one court is also heard, as a lesser one, in every court that borders it."),
        [Deathless] = (2, "Nothing stays beaten",
            "A guard you break stands again two moons later."),
        [Moment] = (3, "The realms move faster",
            "Each realm carries out its plan twice a moon."),
        [Schema] = (2, "The world rebuilds",
            "Each new moon, every province with room posts another guard."),
        [Primal] = (2, "The seeps spread",
            "A live vault seeps into the provinces beside its own as well."),
    };

    public static int WeightOf(string key) => key != null && Table.TryGetValue(key, out var row) ? row.weight : 0;
    public static string NameOf(string key) => key != null && Table.TryGetValue(key, out var row) ? row.name : "";
    public static string RuleOf(string key) => key != null && Table.TryGetValue(key, out var row) ? row.rule : "";

    /// <summary>The rules the guild may put in a pact: those whose shard it holds.</summary>
    public static List<string> Opened(GuildSaveData save)
    {
        var opened = new List<string>();
        foreach (string key in Keys)
        {
            if (save != null && save.HasFlag($"fragment_{key}_collected"))
            {
                opened.Add(key);
            }
        }
        return opened;
    }

    /// <summary>The heaviest pact the guild could take: every opened rule.</summary>
    public static int Ceiling(GuildSaveData save)
    {
        int total = 0;
        foreach (string key in Opened(save))
        {
            total += WeightOf(key);
        }
        return total;
    }

    /// <summary>The least weight a new timeline's pact must carry: one for every two shards
    /// held, so it rises more slowly than the ceiling.</summary>
    public static int Floor(GuildSaveData save) => Opened(save).Count / 2;

    /// <summary>True when this timeline is played under the rule.</summary>
    public static bool Active(CycleState cycle, string key)
        => cycle?.AscensionRules != null && cycle.AscensionRules.Contains(key);

    /// <summary>The weight of the pact this timeline is played under.</summary>
    public static int Weight(CycleState cycle)
    {
        int total = 0;
        if (cycle?.AscensionRules != null)
        {
            foreach (string key in cycle.AscensionRules)
            {
                total += WeightOf(key);
            }
        }
        return total;
    }

    /// <summary>Why a pact cannot be taken, in the player's words, or "" when it can.</summary>
    public static string WhyNot(GuildSaveData save, IEnumerable<string> pact)
    {
        var opened = Opened(save);
        int total = 0;
        foreach (string key in pact)
        {
            if (!opened.Contains(key))
            {
                return $"\"{NameOf(key)}\" is not open to you. It opens with its shard.";
            }
            total += WeightOf(key);
        }
        int floor = Floor(save);
        return total < floor
            ? $"This pact weighs {total}. With {opened.Count} shard(s) held, a pact must weigh at least {floor}."
            : "";
    }

    /// <summary>The lightest pact that meets the floor: the lightest opened rules first.</summary>
    public static List<string> DefaultPact(GuildSaveData save)
    {
        var opened = Opened(save);
        opened.Sort((a, b) => WeightOf(a).CompareTo(WeightOf(b)));
        var pact = new List<string>();
        int floor = Floor(save);
        int total = 0;
        foreach (string key in opened)
        {
            if (total >= floor)
            {
                break;
            }
            pact.Add(key);
            total += WeightOf(key);
        }
        return pact;
    }

    /// <summary>One line naming the pact, for a report.</summary>
    public static string PactLine(CycleState cycle)
    {
        if (cycle?.AscensionRules == null || cycle.AscensionRules.Count == 0)
        {
            return "";
        }
        var names = new List<string>();
        foreach (string key in cycle.AscensionRules)
        {
            names.Add($"{NameOf(key)} ({WeightOf(key)})");
        }
        return $"This timeline's pact, weight {Weight(cycle)}: {string.Join(", ", names)}.";
    }

    /// <summary>Debug: switch one rule for the running timeline, whatever the floor says.</summary>
    public static void DebugSet(CycleState cycle, string key, bool on)
    {
        if (cycle == null || !Table.ContainsKey(key))
        {
            return;
        }
        cycle.AscensionRules ??= new List<string>();
        cycle.AscensionRules.Remove(key);
        if (on)
        {
            cycle.AscensionRules.Add(key);
        }
        GD.Print($"[Ascension] {NameOf(key)}: {(on ? "on" : "off")}. {PactLine(cycle)}");
    }

    // ── axiom ────────────────────────────────────────────────

    /// <summary>The guild cast an overworld spell. Under the axiom rule a realm will answer.</summary>
    public static void NoteCast(CycleState cycle)
    {
        if (Active(cycle, Axiom))
        {
            cycle.AscensionAnswers = Math.Min(MaxAnswersAMoon, cycle.AscensionAnswers + 1);
        }
    }

    // ── deathless, schema: the new moon ──────────────────────

    /// <summary>What the pact does as a moon turns, apart from the realms' plans. Appends a
    /// line to the reports for each thing the player should hear.</summary>
    public static void NewMoon(CycleState cycle)
    {
        var world = cycle?.World;
        if (world == null || cycle.AscensionRules == null || cycle.AscensionRules.Count == 0)
        {
            return;
        }
        cycle.PendingSiegeReports ??= new List<string>();
        int now = cycle.Calendar?.CurrentLunation ?? 0;

        if (Active(cycle, Deathless) && world.Pois != null)
        {
            int stood = 0;
            foreach (var poi in world.Pois)
            {
                // A vault's garrison is not raised this way: its realm has to seek the vault again.
                if (poi == null || !poi.Guard || !poi.Consumed || poi.BrokenOnLunation <= 0
                    || !string.IsNullOrEmpty(poi.VaultKey) || now - poi.BrokenOnLunation < GuardReturnMoons)
                {
                    continue;
                }
                poi.Consumed = false;
                poi.GuardShaken = false;
                poi.BrokenOnLunation = 0;
                stood++;
            }
            if (stood > 0)
            {
                cycle.PendingSiegeReports.Add($"Nothing stays beaten: {stood} guard(s) you broke stand at their posts again.");
            }
        }

        if (Active(cycle, Schema) && world.HasProvinces())
        {
            int posted = 0;
            foreach (var province in world.Provinces)
            {
                if (province != null && ProvinceField.AddGuard(world, province))
                {
                    posted++;
                }
            }
            if (posted > 0)
            {
                cycle.PendingSiegeReports.Add($"The world rebuilds: {posted} new guard post(s) across the provinces.");
            }
        }
    }
}
