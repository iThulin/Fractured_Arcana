using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ArmyMuster.cs
//
// Purpose:        The guild's army on the strategic map (docs/provinces_
//                 v1_12_troops.md, slice PV7; P5, T1). A line is raised
//                 in a province held for its benefits whose ground
//                 breeds it (PR86), for supplies, while the army is
//                 smaller than the held provinces can keep (PR87). At
//                 each new moon every line is paid its upkeep, oldest
//                 first; a paid line refits, an unpaid one loses
//                 strength and at nothing melts away (PR88). A battle's
//                 losses and its count come back here (PR93). Every
//                 number is a default, not yet ruled.
// Layer:          System (strategic; static, no nodes)
// Collaborators:  ArmyState.cs (the state), TroopLines.cs (the lines and
//                 their numbers), ProvinceBenefits.cs (held for its
//                 benefits), ProvinceControl.cs (names), StrategicView.cs
//                 (NewMoon in the lunation), StrategicView.Army.cs (the
//                 card's rows, the Army button and panel),
//                 CombatManager.Troops.cs (the battle), StrategicDebug.cs
//                 (DebugMuster), ProvinceSaveAssert.Army.cs
// See:            docs/provinces_v1_12_troops.md sections 3 and 4
// ============================================================

/// <summary>Raising, keeping and recording the army (PV7). Writes nothing to disk itself: every
/// caller marks the save dirty after it.</summary>
public static class ArmyMuster
{
    // ── Tuning (section 3, PR87 and PR88) ────────────────────────────────

    /// <summary>The most lines the army can hold, however many provinces are held.</summary>
    public const int MaxLines = 6;

    /// <summary>Strength a paid line regains at the new moon.</summary>
    public const int RefitPerMoon = 20;

    /// <summary>Strength an unpaid line loses at the new moon.</summary>
    public const int HungerLoss = 25;

    /// <summary>Supplies the debug muster adds for the lines' keep.</summary>
    public const int DebugSupplies = 30;

    // ══════════════════════════════════════════════════════════════════════
    // Reading
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>True where an army can be raised: a world with provinces.</summary>
    public static bool Musters(CycleState cycle)
        => cycle?.World != null && cycle.World.HasProvinces() && cycle.Army != null;

    /// <summary>The lines the army has.</summary>
    public static int Size(CycleState cycle) => cycle?.Army?.Lines?.Count ?? 0;

    /// <summary>The lines the held provinces can keep: one for each province held for its
    /// benefits, at most MaxLines (PR87).</summary>
    public static int Capacity(CycleState cycle)
    {
        if (!Musters(cycle))
            return 0;
        int held = 0;
        foreach (var province in cycle.World.Provinces)
        {
            if (ProvinceBenefits.HeldForBenefits(cycle, province))
                held++;
        }
        return Math.Min(MaxLines, held);
    }

    /// <summary>The upkeep of every line, a new moon.</summary>
    public static int UpkeepTotal(CycleState cycle)
    {
        int total = 0;
        if (cycle?.Army?.Lines == null)
            return 0;
        foreach (var line in cycle.Army.Lines)
            total += TroopLines.Get(line?.LineId)?.Upkeep ?? 0;
        return total;
    }

    /// <summary>True when one of the province's settlements is a city.</summary>
    public static bool HoldsCity(WorldData world, Province province)
    {
        if (world?.Settlements == null || province?.Settlements == null)
            return false;
        foreach (int index in province.Settlements)
        {
            if (index >= 0 && index < world.Settlements.Count && world.Settlements[index] != null
                && world.Settlements[index].Tier == SettlementTier.City)
                return true;
        }
        return false;
    }

    /// <summary>The lines a province's ground breeds, and the Sappers where it holds a city, in
    /// the lines' order.</summary>
    public static List<TroopLineDef> RaisedFrom(WorldData world, Province province)
    {
        var defs = new List<TroopLineDef>();
        if (province == null)
            return defs;
        bool city = HoldsCity(world, province);
        foreach (var def in TroopLines.All)
        {
            if (Array.IndexOf(def.Grounds, province.Character) >= 0 || (def.FromCities && city))
                defs.Add(def);
        }
        return defs;
    }

    /// <summary>"Pikes of Northeast Fields", or the line's name alone when its province is gone.</summary>
    public static string LineName(CycleState cycle, TroopLineState line)
    {
        var def = TroopLines.Get(line?.LineId);
        string name = def?.Name ?? line?.LineId ?? "A line";
        var province = cycle?.World != null ? ProvinceControl.ProvinceById(cycle.World, line?.ProvinceId) : null;
        return province != null ? $"{name} of {ProvinceControl.Name(province)}" : name;
    }

    /// <summary>"Pikes, Outriders and Sappers".</summary>
    public static string Names(List<TroopLineDef> defs)
    {
        if (defs == null || defs.Count == 0)
            return "";
        if (defs.Count == 1)
            return defs[0].Name;
        var names = new List<string>();
        for (int i = 0; i < defs.Count - 1; i++)
            names.Add(defs[i].Name);
        return string.Join(", ", names) + " and " + defs[defs.Count - 1].Name;
    }

    /// <summary>The province card's army line: what the province raises, and whether it can now.</summary>
    public static string CardLine(CycleState cycle, Province province, List<TroopLineDef> defs)
    {
        if (!Musters(cycle) || province == null)
            return "";
        string ground = HoldsCity(cycle.World, province)
            ? $"Its {province.Character} ground and its city"
            : $"Its {province.Character} ground";
        if (defs == null || defs.Count == 0)
            return $"No line is raised from {province.Character} ground.";
        if (!ProvinceBenefits.HeldForBenefits(cycle, province))
            return $"{ground} would raise {Names(defs)}, once the province is held.";
        return $"{ground} raises {Names(defs)}. The army has {Size(cycle)} of {Capacity(cycle)} line(s); "
               + $"the treasury holds {cycle.Supplies} supplies.";
    }

    /// <summary>"3 of 4 lines, upkeep 5 supplies a new moon; the treasury holds 12."</summary>
    public static string Summary(CycleState cycle)
    {
        if (!Musters(cycle))
            return "The army musters only where the world has provinces.";
        return $"{Size(cycle)} of {Capacity(cycle)} line(s) the held provinces can keep. Upkeep "
               + $"{UpkeepTotal(cycle)} supplies at each new moon; the treasury holds {cycle.Supplies}. "
               + "A line is raised from a held province's card, and fights when the castle is assaulted.";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Raising
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>True when the line can be raised in the province now; otherwise why not.</summary>
    public static bool CanRaise(CycleState cycle, Province province, TroopLineDef def, out string why)
    {
        why = "";
        if (!Musters(cycle) || province == null || def == null)
        {
            why = "The army musters only where the world has provinces.";
            return false;
        }
        if (!ProvinceBenefits.HeldForBenefits(cycle, province))
        {
            why = $"{ProvinceControl.Name(province)} is not held.";
            return false;
        }
        if (!RaisedFrom(cycle.World, province).Contains(def))
        {
            why = $"{def.Name} are not raised from {province.Character} ground.";
            return false;
        }
        int capacity = Capacity(cycle);
        if (Size(cycle) >= capacity)
        {
            why = $"The army has {Size(cycle)} line(s), all that {capacity} held province(s) can keep.";
            return false;
        }
        if (cycle.Supplies < def.RaiseCost)
        {
            why = $"Raising {def.Name} takes {def.RaiseCost} supplies; the treasury holds {cycle.Supplies}.";
            return false;
        }
        return true;
    }

    /// <summary>Raise the line in the province: the supplies are paid and it joins the army at
    /// full strength, raw, in its first doctrine. Null and why not when it cannot.</summary>
    public static TroopLineState Raise(CycleState cycle, Province province, string lineId, out string line)
    {
        var def = TroopLines.Get(lineId);
        if (!CanRaise(cycle, province, def, out string why))
        {
            line = why;
            return null;
        }
        cycle.Supplies -= def.RaiseCost;
        var raised = NewLine(cycle, def, province.Id);
        line = $"{LineName(cycle, raised)} are raised for {def.RaiseCost} supplies. {def.Role} "
               + $"Upkeep {def.Upkeep} at each new moon; the treasury holds {cycle.Supplies}.";
        return raised;
    }

    private static TroopLineState NewLine(CycleState cycle, TroopLineDef def, string provinceId)
    {
        var army = cycle.Army;
        var raised = new TroopLineState
        {
            Id = $"line:{army.NextNumber}",
            LineId = def.Id,
            ProvinceId = provinceId ?? "",
            Strength = 100,
            Battles = 0,
            Doctrine = 0,
            RaisedLunation = cycle.Calendar?.CurrentLunation ?? 0,
        };
        army.NextNumber++;
        army.Lines.Add(raised);
        return raised;
    }

    /// <summary>Disband a line. True when it was in the army.</summary>
    public static bool Disband(CycleState cycle, string lineStateId, out string line)
    {
        var found = Find(cycle, lineStateId);
        if (found == null)
        {
            line = "That line is not in the army.";
            return false;
        }
        line = $"{LineName(cycle, found)} are paid off and go home.";
        cycle.Army.Lines.Remove(found);
        return true;
    }

    /// <summary>Set a line's doctrine; it holds from its next battle.</summary>
    public static bool SetDoctrine(CycleState cycle, string lineStateId, int doctrine)
    {
        var found = Find(cycle, lineStateId);
        var def = TroopLines.Get(found?.LineId);
        if (found == null || def == null)
            return false;
        found.Doctrine = TroopLines.DoctrineIndex(def, doctrine);
        return true;
    }

    public static TroopLineState Find(CycleState cycle, string lineStateId)
    {
        if (cycle?.Army?.Lines == null || string.IsNullOrEmpty(lineStateId))
            return null;
        foreach (var line in cycle.Army.Lines)
        {
            if (line != null && line.Id == lineStateId)
                return line;
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // The new moon (PR88)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Pay every line its upkeep, oldest first. A paid line refits; an unpaid one loses
    /// strength, and at nothing it melts away. Returns the news.</summary>
    public static List<string> NewMoon(CycleState cycle)
    {
        var news = new List<string>();
        if (!Musters(cycle) || cycle.Army.Lines.Count == 0)
            return news;
        int paid = 0, cost = 0;
        var gone = new List<TroopLineState>();
        foreach (var line in cycle.Army.Lines)
        {
            var def = TroopLines.Get(line?.LineId);
            if (def == null)
                continue;
            if (cycle.Supplies >= def.Upkeep)
            {
                cycle.Supplies -= def.Upkeep;
                cost += def.Upkeep;
                paid++;
                line.Strength = Math.Min(100, line.Strength + RefitPerMoon);
                continue;
            }
            line.Strength = Math.Max(0, line.Strength - HungerLoss);
            if (line.Strength <= 0)
            {
                gone.Add(line);
                news.Add($"{LineName(cycle, line)} go unpaid once too often and melt away.");
            }
            else
            {
                news.Add($"{LineName(cycle, line)} go unpaid: strength {line.Strength}.");
            }
        }
        foreach (var line in gone)
            cycle.Army.Lines.Remove(line);
        if (paid > 0)
            news.Insert(0, $"The army is paid: {cost} supplies for {paid} line(s); the treasury holds {cycle.Supplies}.");
        return news;
    }

    // ══════════════════════════════════════════════════════════════════════
    // After a battle (PR93)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Write a battle back to the army: each line's strength becomes its HP left as a
    /// share of its full HP (unchanged if it lost none); a line at nothing has fallen; every line
    /// that lived counts the battle toward its rank, won or lost. Returns the news.</summary>
    public static List<string> RecordBattle(CycleState cycle, List<TroopBattleResult> results, bool won)
    {
        var news = new List<string>();
        if (cycle?.Army?.Lines == null || results == null)
            return news;
        foreach (var result in results)
        {
            var line = Find(cycle, result.LineStateId);
            if (line == null)
                continue;
            string name = LineName(cycle, line);
            int strength = TroopLines.StrengthAfter(line.Strength, result.HpAtStart, result.HpLeft, result.MaxHp);
            if (strength <= 0)
            {
                cycle.Army.Lines.Remove(line);
                news.Add($"{name} fell at the castle.");
                continue;
            }
            int before = TroopLines.RankOf(line.Battles);
            line.Strength = strength;
            line.Battles++;
            int rank = TroopLines.RankOf(line.Battles);
            string promoted = rank > before ? $" They are {TroopLines.RankNames[rank]} now." : "";
            news.Add(won
                ? $"{name} held the castle: strength {strength}.{promoted}"
                : $"{name} lived through the castle's breaking: strength {strength}.{promoted}");
        }
        return news;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Debug
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The debug muster (Gate PV7's lever): one line of each kind at full strength,
    /// raised in the castle's province (or the first), whatever the ground and the capacity, and
    /// supplies for their keep. Null where the army cannot muster.</summary>
    public static string DebugMuster(CycleState cycle)
    {
        if (!Musters(cycle) || cycle.World.Provinces.Count == 0)
            return null;
        int index = CastleHops.CastleProvince(cycle);
        var province = index >= 0 && index < cycle.World.Provinces.Count
            ? cycle.World.Provinces[index]
            : cycle.World.Provinces[0];
        foreach (var def in TroopLines.All)
            NewLine(cycle, def, province?.Id);
        cycle.Supplies += DebugSupplies;
        GD.Print($"[Army] Debug muster: {TroopLines.All.Length} lines in {ProvinceControl.Name(province)}.");
        return $"The army musters in {ProvinceControl.Name(province)}: one line of each kind at full strength, "
               + $"and {DebugSupplies} supplies for their keep.";
    }
}
