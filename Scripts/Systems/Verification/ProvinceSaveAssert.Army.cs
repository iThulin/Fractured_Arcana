using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

// ============================================================
// ProvinceSaveAssert.Army.cs  (partial of ProvinceSaveAssert)
//
// Purpose:        The asserts of slice PV7, the army (docs/provinces_
//                 v1_12_troops.md section 7), on the scratch world with
//                 the castle at the west capital: the state round-trips
//                 and reads empty on legacy JSON; the west capital's
//                 Lowland ground and its city raise Pikes, Outriders and
//                 Sappers, Northeast Fields Pikes and Outriders, the
//                 Weald Longbows; a line is raised only in a held
//                 province, one line a held province, for its supplies;
//                 upkeep is paid oldest first, a paid line refits and an
//                 unpaid one starves; the lines' numbers, ranks,
//                 doctrines and battle rule; a battle written back; the
//                 debug muster; the Orders card built outside the card
//                 database.
// Layer:          Verification
// Collaborators:  ProvinceSaveAssert.cs (AssertAll calls AssertArmy;
//                 Check, RoundTrip), ProvinceSaveAssert.Bastion.cs
//                 (BastionCycle), ArmyMuster.cs, TroopLines.cs,
//                 CombatManager.Troops.cs (BuildOrdersCard,
//                 IssueTroopOrder), TroopEffects.cs
// See:            docs/provinces_v1_12_troops.md section 7
// ============================================================

public static partial class ProvinceSaveAssert
{
    /// <summary>Every army assert (PV7).</summary>
    private static bool AssertArmy(StringBuilder sb)
    {
        bool ok = AssertArmyFields(sb);
        ok &= AssertArmyRaising(sb);
        ok &= AssertArmyUpkeep(sb);
        ok &= AssertArmyLines(sb);
        ok &= AssertArmyBattle(sb);
        return ok;
    }

    /// <summary>ArmyState survives the trip, and legacy JSON reads an army of no lines.</summary>
    private static bool AssertArmyFields(StringBuilder sb)
    {
        var source = new ArmyState { NextNumber = 4 };
        source.Lines.Add(new TroopLineState
        {
            Id = "line:3", LineId = "hillguard", ProvinceId = "province:kingdom_0:1", Strength = 55, Battles = 4,
            Doctrine = 1, RaisedLunation = 7,
        });
        var army = RoundTrip(source);
        var line = army?.Lines?.FirstOrDefault();
        bool ok = Check(sb, "ArmyState survives the trip",
            army != null && army.NextNumber == 4 && army.Lines.Count == 1 && line != null && line.Id == "line:3"
            && line.LineId == "hillguard" && line.ProvinceId == "province:kingdom_0:1" && line.Strength == 55
            && line.Battles == 4 && line.Doctrine == 1 && line.RaisedLunation == 7);
        var legacy = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":2}", SaveManager.JsonOptions);
        ok &= Check(sb, "legacy JSON: an army of no lines",
            legacy?.Army != null && legacy.Army.Lines != null && legacy.Army.Lines.Count == 0 && legacy.Army.NextNumber == 1);

        sb.AppendLine(ok ? "  Army fields: PASS" : "  Army fields: FAIL");
        return ok;
    }

    private static string LineIds(List<TroopLineDef> defs) => string.Join(",", defs.Select(d => d.Id));

    /// <summary>Raising: what each ground breeds, held provinces only, one line a held province,
    /// the supplies paid, the ids in order.</summary>
    private static bool AssertArmyRaising(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int capital = world.ProvinceIndexAt(6, 15), fields = world.ProvinceIndexAt(18, 5), weald = world.ProvinceIndexAt(40, 8);
        if (capital < 0 || fields < 0 || weald < 0)
        {
            sb.AppendLine("  Army raising: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        var provinces = world.Provinces;
        cycle.Supplies = 20;

        string capitalIds = LineIds(ArmyMuster.RaisedFrom(world, provinces[capital]));
        string fieldIds = LineIds(ArmyMuster.RaisedFrom(world, provinces[fields]));
        string wealdIds = LineIds(ArmyMuster.RaisedFrom(world, provinces[weald]));
        bool ok = Check(sb, $"the west capital's Lowland ground and its city raise Pikes, Outriders and Sappers; Northeast Fields Pikes and Outriders; the Weald Longbows ({capitalIds} / {fieldIds} / {wealdIds})",
            capitalIds == "pikes,outriders,sappers" && fieldIds == "pikes,outriders" && wealdIds == "longbows");

        ok &= Check(sb, "with nothing held the army keeps no line, and no Pikes are raised",
            ArmyMuster.Capacity(cycle) == 0
            && !ArmyMuster.CanRaise(cycle, provinces[fields], TroopLines.Get("pikes"), out _)
            && ArmyMuster.Raise(cycle, provinces[fields], "pikes", out _) == null && cycle.Supplies == 20);

        provinces[fields].Held = true;
        var pikes = ArmyMuster.Raise(cycle, provinces[fields], "pikes", out string raised);
        ok &= Check(sb, $"held, Northeast Fields raises Pikes for 6 supplies, as line:1, at full strength, raw, in Hedgehog ({raised})",
            pikes != null && cycle.Supplies == 14 && cycle.Army.Lines.Count == 1 && pikes.Id == "line:1"
            && pikes.LineId == "pikes" && pikes.ProvinceId == provinces[fields].Id && pikes.Strength == 100
            && pikes.Battles == 0 && pikes.Doctrine == 0 && pikes.RaisedLunation == 5);
        ok &= Check(sb, "one held province keeps one line: the Outriders wait for a second",
            ArmyMuster.Capacity(cycle) == 1 && ArmyMuster.Raise(cycle, provinces[fields], "outriders", out _) == null
            && cycle.Supplies == 14 && cycle.Army.Lines.Count == 1);

        provinces[weald].Held = true;
        ok &= Check(sb, "Longbows are not raised from Lowland ground, but are from the Weald's Woodland",
            !ArmyMuster.CanRaise(cycle, provinces[fields], TroopLines.Get("longbows"), out _)
            && ArmyMuster.CanRaise(cycle, provinces[weald], TroopLines.Get("longbows"), out _));
        var bows = ArmyMuster.Raise(cycle, provinces[weald], "longbows", out _);
        ok &= Check(sb, "the Longbows are raised as line:2 for 8 supplies",
            bows != null && bows.Id == "line:2" && cycle.Supplies == 6 && cycle.Army.NextNumber == 3 && cycle.Army.Lines.Count == 2);

        provinces[capital].Held = true;
        ok &= Check(sb, "6 supplies raise Pikes in the capital, but not its Sappers (10)",
            ArmyMuster.Capacity(cycle) == 3
            && ArmyMuster.CanRaise(cycle, provinces[capital], TroopLines.Get("pikes"), out _)
            && !ArmyMuster.CanRaise(cycle, provinces[capital], TroopLines.Get("sappers"), out _));

        sb.AppendLine(ok ? "  Army raising: PASS" : "  Army raising: FAIL");
        return ok;
    }

    /// <summary>The new moon: upkeep oldest first, a paid line refits, an unpaid one starves and at
    /// nothing melts away.</summary>
    private static bool AssertArmyUpkeep(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int fields = world.ProvinceIndexAt(18, 5);
        if (fields < 0)
        {
            sb.AppendLine("  Army upkeep: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        string home = world.Provinces[fields].Id;
        cycle.Army.Lines.Add(new TroopLineState { Id = "line:1", LineId = "pikes", ProvinceId = home, Strength = 70 });
        cycle.Army.Lines.Add(new TroopLineState { Id = "line:2", LineId = "longbows", ProvinceId = home, Strength = 100 });

        cycle.Supplies = 2;
        var news = ArmyMuster.NewMoon(cycle);
        bool ok = Check(sb, $"2 supplies pay the Pikes (1), who refit to 90, and leave the Longbows (2) unpaid at 75 ({string.Join(" ", news)})",
            cycle.Supplies == 1 && cycle.Army.Lines.Count == 2 && cycle.Army.Lines[0].Strength == 90
            && cycle.Army.Lines[1].Strength == 75 && news.Count == 2 && news[0].StartsWith("The army is paid"));

        cycle.Supplies = 0;
        news = ArmyMuster.NewMoon(cycle);
        ok &= Check(sb, "an empty treasury pays neither: 65 and 50",
            cycle.Army.Lines[0].Strength == 65 && cycle.Army.Lines[1].Strength == 50 && news.Count == 2);

        cycle.Army.Lines[1].Strength = 25;
        news = ArmyMuster.NewMoon(cycle);
        ok &= Check(sb, $"unpaid at 25, the Longbows melt away ({string.Join(" ", news)})",
            cycle.Army.Lines.Count == 1 && cycle.Army.Lines[0].LineId == "pikes" && cycle.Army.Lines[0].Strength == 40);

        cycle.Supplies = 5;
        cycle.Army.Lines[0].Strength = 95;
        ArmyMuster.NewMoon(cycle);
        ok &= Check(sb, "a paid line refits to full strength and no further",
            cycle.Army.Lines[0].Strength == 100 && cycle.Supplies == 4);

        sb.AppendLine(ok ? "  Army upkeep: PASS" : "  Army upkeep: FAIL");
        return ok;
    }

    /// <summary>The lines' numbers, ranks and doctrines, and the rule each plans its turn by.</summary>
    private static bool AssertArmyLines(StringBuilder sb)
    {
        var pikes = TroopLines.Get("pikes");
        var bows = TroopLines.Get("longbows");
        var riders = TroopLines.Get("outriders");
        var guard = TroopLines.Get("hillguard");
        var sappers = TroopLines.Get("sappers");
        var adepts = TroopLines.Get("wardadepts");
        if (pikes == null || bows == null || riders == null || guard == null || sappers == null || adepts == null
            || TroopLines.All.Length != 6)
        {
            sb.AppendLine("  Army lines: FAIL (the six lines are not all there)");
            return false;
        }

        var raw = TroopLines.Stats(pikes, new TroopLineState { LineId = "pikes" });
        bool ok = Check(sb, "raw Pikes in Hedgehog: 18 HP, armour 1, damage 4, reach 1, move 2, a brace of 4",
            raw.MaxHp == 18 && raw.Hp == 18 && raw.Armor == 1 && raw.Damage == 4 && raw.Range == 1 && raw.Move == 2
            && raw.BraceArmor == 4);
        var veteran = TroopLines.Stats(pikes, new TroopLineState { LineId = "pikes", Battles = 3, Doctrine = 1, Strength = 50 });
        ok &= Check(sb, $"veteran Pikes in Push of Pike at half strength: 11 of 22 HP, damage 8, a brace of 2 ({veteran.Hp}/{veteran.MaxHp}, {veteran.Damage}, {veteran.BraceArmor})",
            veteran.MaxHp == 22 && veteran.Hp == 11 && veteran.Damage == 8 && veteran.BraceArmor == 2);
        ok &= Check(sb, "the doctrines: Volley reaches 4, Battle-mages reach 3 for 5, Stonewall has 26 HP, Lancers add 2 on an advance, Shieldwall lends 1, Engineers mend 6",
            TroopLines.Stats(bows, new TroopLineState()).Range == 4
            && TroopLines.Stats(adepts, new TroopLineState { Doctrine = 1 }).Range == 3
            && TroopLines.Stats(adepts, new TroopLineState { Doctrine = 1 }).Damage == 5
            && TroopLines.Stats(guard, new TroopLineState { Doctrine = 1 }).MaxHp == 26
            && TroopLines.Stats(riders, new TroopLineState { Doctrine = 1 }).AdvanceBonus == 2
            && TroopLines.Stats(guard, new TroopLineState()).ShieldwallArmor == 1
            && TroopLines.Stats(sappers, new TroopLineState()).MendHp == 6);
        ok &= Check(sb, "ranks: Raw at 0 battles, Blooded from 1, Veteran from 3, Elite from 6",
            TroopLines.RankOf(0) == 0 && TroopLines.RankOf(1) == 1 && TroopLines.RankOf(2) == 1 && TroopLines.RankOf(3) == 2
            && TroopLines.RankOf(5) == 2 && TroopLines.RankOf(6) == 3 && TroopLines.RankOf(40) == 3);

        ok &= Check(sb, "with no order a line strikes an enemy in reach, else keeps its stance: Pikes brace, Outriders advance",
            TroopLines.Choose(pikes, "", true, false, false) == TroopIntentKind.Strike
            && TroopLines.Choose(pikes, "", false, false, false) == TroopIntentKind.Brace
            && TroopLines.Choose(riders, "", false, false, false) == TroopIntentKind.Advance
            && TroopLines.Choose(riders, "", true, false, false) == TroopIntentKind.Strike);
        ok &= Check(sb, "Ward-adepts ward an ally beside them first and Sappers mend first; with nothing to do each braces",
            TroopLines.Choose(adepts, "", true, false, true) == TroopIntentKind.Ward
            && TroopLines.Choose(adepts, "", true, false, false) == TroopIntentKind.Strike
            && TroopLines.Choose(adepts, "", false, false, false) == TroopIntentKind.Brace
            && TroopLines.Choose(sappers, "", true, true, false) == TroopIntentKind.Mend
            && TroopLines.Choose(sappers, "", false, false, false) == TroopIntentKind.Brace);
        ok &= Check(sb, "an order decides for every line: Charge! advances, Hold Fast braces",
            TroopLines.Choose(pikes, TroopLines.OrderCharge, true, false, false) == TroopIntentKind.Advance
            && TroopLines.Choose(adepts, TroopLines.OrderHold, false, false, true) == TroopIntentKind.Brace);

        ok &= Check(sb, "HP at a strength rounds up and keeps 1: 18 at 90 is 17, at 1 is 1",
            TroopLines.HpAtStrength(18, 90) == 17 && TroopLines.HpAtStrength(18, 1) == 1);
        ok &= Check(sb, "strength after a battle: unchanged with no loss, the HP left's share with one, never more than before, 0 when fallen",
            TroopLines.StrengthAfter(90, 17, 17, 18) == 90 && TroopLines.StrengthAfter(90, 17, 9, 18) == 50
            && TroopLines.StrengthAfter(40, 8, 7, 18) == 39 && TroopLines.StrengthAfter(90, 17, 0, 18) == 0);

        sb.AppendLine(ok ? "  Army lines: PASS" : "  Army lines: FAIL");
        return ok;
    }

    /// <summary>A battle written back, the debug muster, and the Orders card.</summary>
    private static bool AssertArmyBattle(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int capital = world.ProvinceIndexAt(6, 15), fields = world.ProvinceIndexAt(18, 5);
        if (capital < 0 || fields < 0)
        {
            sb.AppendLine("  Army battle: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        string home = world.Provinces[fields].Id;
        cycle.Army.Lines.Add(new TroopLineState { Id = "line:1", LineId = "pikes", ProvinceId = home, Strength = 90, Battles = 0 });
        cycle.Army.Lines.Add(new TroopLineState { Id = "line:2", LineId = "longbows", ProvinceId = home, Strength = 100, Battles = 2 });
        cycle.Army.Lines.Add(new TroopLineState { Id = "line:3", LineId = "hillguard", ProvinceId = home, Strength = 60, Battles = 5 });
        var results = new List<TroopBattleResult>
        {
            new TroopBattleResult { LineStateId = "line:1", HpAtStart = 17, HpLeft = 9, MaxHp = 18 },
            new TroopBattleResult { LineStateId = "line:2", HpAtStart = 12, HpLeft = 0, MaxHp = 12 },
            new TroopBattleResult { LineStateId = "line:3", HpAtStart = 16, HpLeft = 16, MaxHp = 26 },
        };
        var news = ArmyMuster.RecordBattle(cycle, results, true);
        var held = ArmyMuster.Find(cycle, "line:1");
        var wall = ArmyMuster.Find(cycle, "line:3");
        bool ok = Check(sb, $"a won battle: the Pikes stand at 50 and are Blooded, the Longbows fell, the Hillguard lost none and are Elite ({string.Join(" ", news)})",
            held != null && held.Strength == 50 && held.Battles == 1 && ArmyMuster.Find(cycle, "line:2") == null
            && wall != null && wall.Strength == 60 && wall.Battles == 6 && news.Count == 3
            && news[0].Contains("Blooded") && news[1].Contains("fell") && news[2].Contains("Elite"));

        var muster = BastionCycle("Adept");
        int supplies = muster.Supplies;
        string said = ArmyMuster.DebugMuster(muster);
        string capitalId = muster.World.Provinces[capital].Id;
        ok &= Check(sb, $"the debug muster raises one line of each kind in the castle's province, with 30 supplies ({said})",
            said != null && muster.Army.Lines.Count == 6 && muster.Supplies == supplies + ArmyMuster.DebugSupplies
            && muster.Army.Lines.All(l => l.ProvinceId == capitalId) && muster.Army.NextNumber == 7);

        var card = CombatManager.BuildOrdersCard(CardSchool.Adept);
        ok &= Check(sb, "the Orders card builds outside the card database: Charge! and Hold Fast, 1 mana each, no blueprint id",
            card != null && card.TopHalf != null && card.BottomHalf != null
            && card.TopHalf.Name == "Charge!" && card.BottomHalf.Name == "Hold Fast"
            && card.TopHalf.ManaCost == 1 && card.BottomHalf.ManaCost == 1 && card.BlueprintId == ""
            && card.TopHalf.Effects.Length == 1 && card.TopHalf.Effects[0] is TroopOrderEffect charge && charge.Order == TroopLines.OrderCharge
            && card.BottomHalf.Effects.Length == 1 && card.BottomHalf.Effects[0] is TroopOrderEffect hold && hold.Order == TroopLines.OrderHold
            && CardDatabase.Blueprints.All(b => b.Id != "troop_orders"));
        ok &= Check(sb, "with no fight on, an order finds no line to hear it",
            CombatManager.IssueTroopOrder(TroopLines.OrderCharge).StartsWith("No line"));

        sb.AppendLine(ok ? "  Army battle: PASS" : "  Army battle: FAIL");
        return ok;
    }
}
