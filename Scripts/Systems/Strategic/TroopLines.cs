using System;
using System.Collections.Generic;

// ============================================================
// TroopLines.cs
//
// Purpose:        The six troop lines (docs/provinces_v1_12_troops.md,
//                 slice PV7; P5, P6, T1): where each is raised, what it
//                 costs, its numbers at full strength, its ranks and its
//                 two doctrines, and the rule each line plans its turn by
//                 in battle. Pure data and pure functions: the strategic
//                 side (ArmyMuster) and the battle (CombatManager.Troops)
//                 both read here, and the asserts test the rules without
//                 a battle. Every number is a default (PR86 to PR93), not
//                 yet ruled.
// Layer:          Data and rules (static, no nodes)
// Collaborators:  ArmyState.cs (TroopLineState), ArmyMuster.cs,
//                 CombatManager.Troops.cs, StrategicView.Army.cs,
//                 ProvinceSaveAssert.Army.cs
// See:            docs/provinces_v1_12_troops.md sections 2, 3 and 5
// ============================================================

/// <summary>What a line will do on its turn in battle, shown above it through the player's turn.</summary>
public enum TroopIntentKind
{
    None = 0,
    /// <summary>A blow, or a shot, at an enemy in reach when its turn comes.</summary>
    Strike = 1,
    /// <summary>Step toward the nearest enemy, then strike it if it is in reach.</summary>
    Advance = 2,
    /// <summary>Hold the tile: armour until the guild's next turn.</summary>
    Brace = 3,
    /// <summary>Shield every ally beside it until the guild's next turn ends.</summary>
    Ward = 4,
    /// <summary>Restore the Castle Heart, or a gate door, beside it.</summary>
    Mend = 5,
}

/// <summary>One troop line: where it is raised, what it costs, its numbers at full strength and
/// no rank, what it does with no enemy in reach and no order, and its two doctrines.</summary>
public sealed class TroopLineDef
{
    public string Id = "";
    public string Name = "";
    public string Role = "";
    public ProvinceCharacter[] Grounds = Array.Empty<ProvinceCharacter>();
    /// <summary>Also raised in any province that holds a city (the Sappers).</summary>
    public bool FromCities;
    public int RaiseCost;
    public int Upkeep;
    public int Hp;
    public int Armor;
    public int Damage;
    public int Range;
    public int Move;
    public TroopIntentKind Stance = TroopIntentKind.Brace;
    /// <summary>Its brace's armour, its ward's shield or its mend, by its stance.</summary>
    public int StanceValue;
    public string[] Doctrines = Array.Empty<string>();
    public string[] DoctrineTexts = Array.Empty<string>();

    public bool Ranged => Range > 1;
}

/// <summary>A line's numbers for one battle, from its definition, its rank, its doctrine and its
/// strength.</summary>
public struct TroopStats
{
    public int MaxHp;
    public int Hp;
    public int Armor;
    public int Damage;
    public int Range;
    public int Move;
    /// <summary>The armour its brace gives it.</summary>
    public int BraceArmor;
    /// <summary>The shield its ward gives each ally beside it.</summary>
    public int WardShield;
    /// <summary>What its mend restores.</summary>
    public int MendHp;
    /// <summary>Damage added to its strike on an advance (Lancers).</summary>
    public int AdvanceBonus;
    /// <summary>Armour its brace gives each ally beside it as well (Shieldwall).</summary>
    public int ShieldwallArmor;
}

/// <summary>One line's part in a battle, as the battle hands it back (ArmyMuster.RecordBattle).</summary>
public struct TroopBattleResult
{
    public string LineStateId;
    public int HpAtStart;
    public int HpLeft;
    public int MaxHp;
}

/// <summary>The lines, their numbers and their battle rule (PV7).</summary>
public static class TroopLines
{
    // ── Tuning (section 3, PR88 and PR89; section 5, PR91 and PR92) ──────

    /// <summary>Battles fought and lived through that make each rank: Raw, Blooded, Veteran, Elite.</summary>
    public static readonly int[] RankBattles = { 0, 1, 3, 6 };
    public static readonly string[] RankNames = { "Raw", "Blooded", "Veteran", "Elite" };
    public const int MaxRank = 3;

    /// <summary>What each rank adds.</summary>
    public const int DamagePerRank = 1;
    public const int HpPerRank = 2;

    /// <summary>The brace of a line whose stance is not a brace (a ward or a mend with nothing to
    /// do, or an advance held fast).</summary>
    public const int FallbackBrace = 1;

    /// <summary>The two orders the Orders card gives (PR92).</summary>
    public const string OrderCharge = "charge";
    public const string OrderHold = "hold";

    /// <summary>Charge!: damage added to the strike after the advance.</summary>
    public const int ChargeBonus = 1;

    /// <summary>Hold Fast: armour added to the brace.</summary>
    public const int HoldFastBonus = 2;

    // ── The six lines (section 2, PR86) ──────────────────────────────────

    public static readonly TroopLineDef[] All =
    {
        new TroopLineDef
        {
            Id = "pikes", Name = "Pikes", Role = "Hold the line and break charges.",
            Grounds = new[] { ProvinceCharacter.Lowland },
            RaiseCost = 6, Upkeep = 1, Hp = 18, Armor = 1, Damage = 4, Range = 1, Move = 2,
            Stance = TroopIntentKind.Brace, StanceValue = 2,
            Doctrines = new[] { "Hedgehog", "Push of Pike" },
            DoctrineTexts = new[] { "A brace gives 2 more armour.", "A strike deals 2 more damage." },
        },
        new TroopLineDef
        {
            Id = "longbows", Name = "Longbows", Role = "Volleys at range.",
            Grounds = new[] { ProvinceCharacter.Woodland },
            RaiseCost = 8, Upkeep = 2, Hp = 12, Armor = 0, Damage = 4, Range = 3, Move = 2,
            Stance = TroopIntentKind.Brace, StanceValue = 1,
            Doctrines = new[] { "Volley", "Bodkin" },
            DoctrineTexts = new[] { "Reach 1 farther.", "A shot deals 2 more damage." },
        },
        new TroopLineDef
        {
            Id = "outriders", Name = "Outriders", Role = "Flank and pursue.",
            Grounds = new[] { ProvinceCharacter.Lowland, ProvinceCharacter.Arid },
            RaiseCost = 8, Upkeep = 2, Hp = 14, Armor = 0, Damage = 5, Range = 1, Move = 3,
            Stance = TroopIntentKind.Advance, StanceValue = 0,
            Doctrines = new[] { "Harriers", "Lancers" },
            DoctrineTexts = new[] { "Move 1 farther.", "A strike on an advance deals 2 more damage." },
        },
        new TroopLineDef
        {
            Id = "hillguard", Name = "Hillguard", Role = "A shieldwall, slow and hard to move.",
            Grounds = new[] { ProvinceCharacter.Highland, ProvinceCharacter.Mountain },
            RaiseCost = 8, Upkeep = 2, Hp = 22, Armor = 2, Damage = 3, Range = 1, Move = 1,
            Stance = TroopIntentKind.Brace, StanceValue = 3,
            Doctrines = new[] { "Shieldwall", "Stonewall" },
            DoctrineTexts = new[] { "A brace gives each ally beside it 1 armour as well.", "4 more HP." },
        },
        new TroopLineDef
        {
            Id = "sappers", Name = "Sappers", Role = "Breach walls and shore them up.",
            Grounds = new[] { ProvinceCharacter.Ruined }, FromCities = true,
            RaiseCost = 10, Upkeep = 2, Hp = 14, Armor = 1, Damage = 4, Range = 1, Move = 2,
            Stance = TroopIntentKind.Mend, StanceValue = 4,
            Doctrines = new[] { "Engineers", "Demolishers" },
            DoctrineTexts = new[] { "A mend restores 2 more.", "A strike deals 2 more damage." },
        },
        new TroopLineDef
        {
            Id = "wardadepts", Name = "Ward-adepts", Role = "Wards for the line.",
            Grounds = new[] { ProvinceCharacter.Arcane },
            RaiseCost = 12, Upkeep = 3, Hp = 10, Armor = 0, Damage = 3, Range = 2, Move = 2,
            Stance = TroopIntentKind.Ward, StanceValue = 3,
            Doctrines = new[] { "Wardens", "Battle-mages" },
            DoctrineTexts = new[] { "A ward gives 2 more shield.", "Reach 1 farther, and a strike deals 2 more damage." },
        },
    };

    // ══════════════════════════════════════════════════════════════════════
    // Reading
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The line with this id, or null.</summary>
    public static TroopLineDef Get(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        foreach (var def in All)
        {
            if (def.Id == id)
                return def;
        }
        return null;
    }

    /// <summary>The rank a line has after so many battles: 0 Raw to 3 Elite.</summary>
    public static int RankOf(int battles)
    {
        int rank = 0;
        for (int r = 1; r < RankBattles.Length; r++)
        {
            if (battles >= RankBattles[r])
                rank = r;
        }
        return Math.Min(rank, MaxRank);
    }

    public static string RankName(int battles) => RankNames[RankOf(battles)];

    /// <summary>A doctrine index the line has, clamped to its doctrines.</summary>
    public static int DoctrineIndex(TroopLineDef def, int doctrine)
        => def == null || def.Doctrines.Length == 0 ? 0 : Math.Clamp(doctrine, 0, def.Doctrines.Length - 1);

    public static string DoctrineName(TroopLineDef def, int doctrine)
        => def == null || def.Doctrines.Length == 0 ? "" : def.Doctrines[DoctrineIndex(def, doctrine)];

    public static string DoctrineText(TroopLineDef def, int doctrine)
        => def == null || def.DoctrineTexts.Length == 0 ? "" : def.DoctrineTexts[DoctrineIndex(def, doctrine)];

    /// <summary>A line's HP at a strength: its share of the full HP, rounded up, at least 1.</summary>
    public static int HpAtStrength(int maxHp, int strength)
        => Math.Max(1, (Math.Max(0, maxHp) * Math.Clamp(strength, 0, 100) + 99) / 100);

    /// <summary>A line's numbers for a battle: its definition, plus 1 damage and 2 HP a rank, plus
    /// its doctrine, at its strength's share of the HP.</summary>
    public static TroopStats Stats(TroopLineDef def, TroopLineState line)
    {
        var stats = new TroopStats();
        if (def == null)
            return stats;
        int rank = RankOf(line?.Battles ?? 0);
        int doctrine = DoctrineIndex(def, line?.Doctrine ?? 0);
        stats.MaxHp = def.Hp + HpPerRank * rank;
        stats.Armor = def.Armor;
        stats.Damage = def.Damage + DamagePerRank * rank;
        stats.Range = def.Range;
        stats.Move = def.Move;
        stats.BraceArmor = def.Stance == TroopIntentKind.Brace ? def.StanceValue : FallbackBrace;
        stats.WardShield = def.Stance == TroopIntentKind.Ward ? def.StanceValue : 0;
        stats.MendHp = def.Stance == TroopIntentKind.Mend ? def.StanceValue : 0;
        switch (def.Id)
        {
            case "pikes":
                if (doctrine == 0) stats.BraceArmor += 2; else stats.Damage += 2;
                break;
            case "longbows":
                if (doctrine == 0) stats.Range += 1; else stats.Damage += 2;
                break;
            case "outriders":
                if (doctrine == 0) stats.Move += 1; else stats.AdvanceBonus += 2;
                break;
            case "hillguard":
                if (doctrine == 0) stats.ShieldwallArmor = 1; else stats.MaxHp += 4;
                break;
            case "sappers":
                if (doctrine == 0) stats.MendHp += 2; else stats.Damage += 2;
                break;
            case "wardadepts":
                if (doctrine == 0) stats.WardShield += 2;
                else { stats.Range += 1; stats.Damage += 2; }
                break;
        }
        stats.Hp = HpAtStrength(stats.MaxHp, line?.Strength ?? 100);
        return stats;
    }

    /// <summary>A line's strength after a battle: unchanged when it lost no HP; otherwise its HP
    /// left as a share of its full HP, never more than before and at least 1; 0 when it fell.</summary>
    public static int StrengthAfter(int before, int hpAtStart, int hpLeft, int maxHp)
    {
        if (hpLeft <= 0 || maxHp <= 0)
            return 0;
        int ceiling = Math.Clamp(before, 1, 100);
        if (hpLeft >= hpAtStart)
            return ceiling;
        int strength = (int)Math.Round(100.0 * hpLeft / maxHp);
        return Math.Clamp(strength, 1, ceiling);
    }

    // ══════════════════════════════════════════════════════════════════════
    // The battle rule (section 5, PR91)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>What a line plans for its turn. An order decides for every line: Charge! advances,
    /// Hold Fast braces. Otherwise a line with an enemy in reach strikes it, except that
    /// Ward-adepts ward an ally beside them first and Sappers mend first; with no enemy in reach
    /// a line keeps its stance (Outriders advance), and a line with nothing to ward or mend
    /// braces.</summary>
    public static TroopIntentKind Choose(TroopLineDef def, string order, bool enemyInReach, bool mendTarget, bool allyBeside)
    {
        if (def == null)
            return TroopIntentKind.None;
        if (order == OrderCharge)
            return TroopIntentKind.Advance;
        if (order == OrderHold)
            return TroopIntentKind.Brace;
        switch (def.Stance)
        {
            case TroopIntentKind.Ward:
                if (allyBeside)
                    return TroopIntentKind.Ward;
                return enemyInReach ? TroopIntentKind.Strike : TroopIntentKind.Brace;
            case TroopIntentKind.Mend:
                if (mendTarget)
                    return TroopIntentKind.Mend;
                return enemyInReach ? TroopIntentKind.Strike : TroopIntentKind.Brace;
            case TroopIntentKind.Advance:
                return enemyInReach ? TroopIntentKind.Strike : TroopIntentKind.Advance;
            default:
                return enemyInReach ? TroopIntentKind.Strike : TroopIntentKind.Brace;
        }
    }
}
