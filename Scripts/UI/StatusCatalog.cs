using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// StatusCatalog.cs
//
// Purpose:        The one table of every condition a unit can
//                 carry: display label, short chip code, tone
//                 family and the tooltip text. Also collects the
//                 full condition list for a unit, merging the
//                 status dictionary with the unit-level flags
//                 that never enter it (stagger, armed reaction,
//                 bodyguard, veil, chitin, spirit wards, heat).
//
//                 Rendered as TEXT chips on purpose. The shipped
//                 fonts (Carlito, Caladea) contain none of the old
//                 symbol glyphs (fire, snowflake, shield, chain,
//                 star): those only drew through the OS fallback
//                 font, which differs between Windows and macOS and
//                 dropped the shield emoji entirely. Letters always
//                 render.
//
// Layer:          UI
// Collaborators:  UnitNameplate.cs, HealthBarRoot.cs, CombatUI.cs,
//                 UITheme.cs, Unit.cs (read only)
// ============================================================

/// <summary>Condition families. The chip colour carries the family, so a
/// player learns five colours instead of forty symbols.</summary>
public enum StatusTone
{
    Debuff,   // lockouts and weakens: frozen, stunned, rooted, blinded
    Damage,   // hurts every turn: burn, bleed, poison
    Mark,     // pays off when the bearer is hit: marked, arcane mark
    Buff,     // helps the bearer: hasted, empowered, shrouded
    Trait,    // standing facts: wildlife, illusion, veil, heat
}

/// <summary>Static display data for one condition key.</summary>
public readonly struct StatusInfo
{
    public readonly string Label;
    public readonly string Abbrev;
    public readonly StatusTone Tone;
    /// <summary>Tooltip body. "{n}" is replaced with the live count.</summary>
    public readonly string Description;

    public StatusInfo(string label, string abbrev, StatusTone tone, string description)
    {
        Label = label;
        Abbrev = abbrev;
        Tone = tone;
        Description = description;
    }
}

/// <summary>One condition as currently carried by a unit.</summary>
public readonly struct UnitCondition
{
    public readonly string Key;
    public readonly string Label;
    public readonly string Abbrev;
    public readonly StatusTone Tone;
    public readonly string Description;
    /// <summary>Turns left or stacks. 0 or less means "no number" (lasts the combat).</summary>
    public readonly int Count;

    public UnitCondition(string key, StatusInfo info, int count, string description = null)
    {
        Key = key;
        Label = info.Label;
        Abbrev = info.Abbrev;
        Tone = info.Tone;
        Count = count;
        Description = description ?? info.Description.Replace("{n}", count.ToString());
    }

    /// <summary>Chip text. Compact plates use the code, detailed plates the label.</summary>
    public string ChipText(bool full)
    {
        string head = full ? Label : Abbrev;
        return Count > 0 ? $"{head} {Count}" : head;
    }

    /// <summary>Tooltip heading, e.g. "Burn (2 turns)".</summary>
    public string TooltipTitle()
    {
        if (Count <= 0)
            return Label;
        return Key switch
        {
            "poisoned" => $"{Label} ({Count} per turn)",
            "chitin" or "heat" => $"{Label} {Count}",
            _ => Count == 1 ? $"{Label} (1 turn)" : $"{Label} ({Count} turns)",
        };
    }
}

public static class StatusCatalog
{
    /// <summary>Durations at or above this are "until combat ends" markers
    /// (poison is stored as 999, stagger and exile as 99). They show no number.</summary>
    public const int PermanentThreshold = 99;

    private static readonly Dictionary<string, StatusInfo> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Debuffs: lockouts ──────────────────────────────────────────
        { "frozen",        new("Frozen",       "FRZ",  StatusTone.Debuff, "Cannot act or move.") },
        { "stunned",       new("Stunned",      "STN",  StatusTone.Debuff, "Loses its actions and movement.") },
        { "bound",         new("Bound",        "BND",  StatusTone.Debuff, "Cannot act or move, and cannot be freed by cleansing.") },
        { "rooted",        new("Rooted",       "ROOT", StatusTone.Debuff, "Cannot move. Can still act.") },
        { "slowed",        new("Slowed",       "SLOW", StatusTone.Debuff, "Movement is halved.") },
        { "temporal_drag", new("Time Drag",    "DRAG", StatusTone.Debuff, "Movement is halved.") },
        { "delayed",       new("Delayed",      "DLY",  StatusTone.Debuff, "Its turn has been postponed. Cannot be postponed again until it acts.") },
        { "dominated",     new("Dominated",    "DOM",  StatusTone.Debuff, "Fights for the other side, attacking its own allies.") },
        { "stagger",       new("Staggered",    "STG",  StatusTone.Debuff, "Its next action is cancelled.") },
        // ── Debuffs: weakens ───────────────────────────────────────────
        { "blinded",       new("Blinded",      "BLND", StatusTone.Debuff, "Its attacks miss.") },
        { "weakened",      new("Weakened",     "WEAK", StatusTone.Debuff, "Its attacks deal 2 less damage.") },
        { "named",         new("Named",        "NAME", StatusTone.Debuff, "Its attacks deal half damage.") },
        { "suppressed",    new("Suppressed",   "SUP",  StatusTone.Debuff, "Loses 1 move point next turn.") },
        { "taunted",       new("Taunted",      "TNT",  StatusTone.Debuff, "Must target the unit that taunted it.") },
        { "mana_taxed",    new("Mana Taxed",   "TAX",  StatusTone.Debuff, "Its spells cost more.") },
        { "silenced",      new("Silenced",     "SIL",  StatusTone.Debuff, "Silenced.") },
        { "cursed",        new("Cursed",       "CRS",  StatusTone.Debuff, "Cursed.") },
        { "hexed",         new("Hexed",        "HEX",  StatusTone.Debuff, "Hexed.") },
        { "geas",          new("Geas",         "GEAS", StatusTone.Debuff, "Bound by a geas. Breaking it draws blood.") },
        { "exiled",        new("Exiled",       "EXL",  StatusTone.Debuff, "Cast out. It cannot be raised again.") },
        // ── Damage over time ───────────────────────────────────────────
        { "burn",          new("Burn",         "BRN",  StatusTone.Damage, "Takes 3 damage each turn.") },
        { "burning",       new("Burning",      "BRN",  StatusTone.Damage, "On fire.") },
        { "bleed",         new("Bleed",        "BLD",  StatusTone.Damage, "Takes 2 damage each turn.") },
        { "poisoned",      new("Poisoned",     "PSN",  StatusTone.Damage, "Loses {n} max HP each turn until combat ends. Lost max HP shows violet at the end of the bar.") },
        { "ball_lightning",new("Ball Lightning","BALL",StatusTone.Damage, "Each turn: 6 damage to this unit and to up to 3 of its allies within 3 tiles.") },
        // ── Marks ──────────────────────────────────────────────────────
        { "marked",        new("Marked",       "MRK",  StatusTone.Mark,   "The next martial hit on it deals +3 damage and spends the mark.") },
        { "vulnerable",    new("Vulnerable",   "VULN", StatusTone.Mark,   "Exposed. Some martial techniques deal bonus damage to it.") },
        { "arcane_mark",   new("Arcane Mark",  "ARC",  StatusTone.Mark,   "The next damage spell to hit it deals +3 damage and spends the mark.") },
        { "haunted",       new("Haunted",      "HNT",  StatusTone.Mark,   "If it dies, it leaves a Strong memorial.") },
        { "wizard_charging", new("Channeling", "CHAN", StatusTone.Mark,   "Channeling. Its intent shows what the release will hit.") },
        // ── Buffs ──────────────────────────────────────────────────────
        { "hasted",        new("Hasted",       "HST",  StatusTone.Buff,   "Gained an extra action.") },
        { "empowered",     new("Empowered",    "EMP",  StatusTone.Buff,   "Its damage spells deal +3 damage.") },
        { "chaining",      new("Chaining",     "CHN",  StatusTone.Buff,   "Its damage spells chain to extra targets.") },
        { "shrouded",      new("Shrouded",     "SHRD", StatusTone.Buff,   "Each hit against it deals at most 5 damage.") },
        { "immortal",      new("Immortal",     "IMM",  StatusTone.Buff,   "Lethal damage leaves it at 1 HP.") },
        { "untargetable",  new("Untargetable", "HIDE", StatusTone.Buff,   "Enemies cannot target it.") },
        { "dancing",       new("The Dance",    "DNC",  StatusTone.Buff,   "Its allies may swap places freely.") },
        { "invulnerable",  new("Invulnerable", "INV",  StatusTone.Buff,   "Takes no damage.") },
        { "vigil",         new("Vigil",        "VIG",  StatusTone.Buff,   "Keeping vigil.") },
        { "undying_turn",  new("Undying",      "UND",  StatusTone.Buff,   "Returns after being destroyed.") },
        { "undying_full_restore", new("Undying", "UND", StatusTone.Buff,  "Returns at full HP after being destroyed.") },
        // ── Traits ─────────────────────────────────────────────────────
        { "wildlife",      new("Wildlife",     "WILD", StatusTone.Trait,  "A creature of the land. Its death enriches the ground.") },
        { "illusion",      new("Illusion",     "ILL",  StatusTone.Trait,  "Not real. It fades when its time runs out.") },
        { "living_spell",  new("Living Spell", "SPL",  StatusTone.Trait,  "A spell given form. It fades when its time runs out.") },
        { "decoy",         new("Decoy",        "DCY",  StatusTone.Trait,  "A decoy.") },
        { "colossus_absorb", new("Absorbing",  "ABS",  StatusTone.Trait,  "Draws power from elemental tiles it stands on.") },

        // ── Unit-level conditions (not status keys; see Collect) ───────
        { "reaction",      new("Ready",        "RDY",  StatusTone.Buff,   "A reaction is armed.") },
        { "guarded",       new("Guarded",      "GRD",  StatusTone.Buff,   "An ally intercepts damage aimed at this unit.") },
        { "veil",          new("Veil",         "VEIL", StatusTone.Trait,  "Damage from anything more than 1 tile away is negated. Damage over time and terrain still land.") },
        { "chitin",        new("Chitin",       "CHT",  StatusTone.Trait,  "Every hit against it is reduced by {n}.") },
        { "taunting",      new("Taunting",     "TAUNT",StatusTone.Trait,  "Enemies prefer to attack it.") },
        { "heat",          new("Heat",         "HEAT", StatusTone.Trait,  "+{n} attack damage.") },
    };

    /// <summary>Display data for a key. Unknown keys still show: a readable
    /// label from the key, a four-letter code, neutral tone. A new status must
    /// never be invisible again.</summary>
    public static StatusInfo Get(string key)
    {
        if (!string.IsNullOrEmpty(key) && Map.TryGetValue(key, out var info))
            return info;

        string label = Humanize(key);
        string code = label.Replace(" ", "");
        code = code.Length > 4 ? code.Substring(0, 4) : code;
        return new StatusInfo(label, code.ToUpperInvariant(), StatusTone.Trait, label + ".");
    }

    public static bool IsKnown(string key) => !string.IsNullOrEmpty(key) && Map.ContainsKey(key);

    public static Color ToneColor(StatusTone tone) => tone switch
    {
        StatusTone.Debuff => UITheme.ConditionDebuff,
        StatusTone.Damage => UITheme.ConditionDamage,
        StatusTone.Mark   => UITheme.ConditionMark,
        StatusTone.Buff   => UITheme.ConditionBuff,
        _                 => UITheme.ConditionTrait,
    };

    /// <summary>Every condition the unit carries, sorted by family (threats
    /// first) and then by label, so chips hold still between refreshes.</summary>
    public static List<UnitCondition> Collect(Unit u)
    {
        var list = new List<UnitCondition>();
        if (u == null || u.Stats == null)
            return list;

        bool staggerShown = false;
        var statuses = u.Stats.StatusEffects;
        if (statuses != null)
        {
            foreach (var kvp in statuses)
            {
                if (kvp.Value <= 0)
                    continue;

                string key = kvp.Key;
                var info = Get(key);

                if (key == "poisoned")
                {
                    int drain = Math.Max(0, u.Stats.PoisonDrainPerTurn);
                    list.Add(new UnitCondition(key, info, drain));
                    continue;
                }
                if (key == "stagger")
                {
                    if (staggerShown)
                        continue;
                    staggerShown = true;
                }

                int count = kvp.Value >= PermanentThreshold ? 0 : kvp.Value;
                list.Add(new UnitCondition(key, info, count));
            }
        }

        // Stagger lives on a bool (it must survive the enemy status tick).
        if (u.IsStaggered && !staggerShown)
            list.Add(new UnitCondition("stagger", Get("stagger"), 0));

        if (u.ArmedReaction != null)
        {
            string name = string.IsNullOrEmpty(u.ArmedReaction.DisplayName) ? "Reaction" : u.ArmedReaction.DisplayName;
            list.Add(new UnitCondition("reaction", Get("reaction"), 0,
                $"{name} is armed and fires on its trigger. Refunded at this unit's next turn if it does not."));
        }

        if (u.BodyguardedBy != null && GodotObject.IsInstanceValid(u.BodyguardedBy))
        {
            string guard = string.IsNullOrEmpty(u.BodyguardedBy.DisplayName) ? u.BodyguardedBy.Name.ToString() : u.BodyguardedBy.DisplayName;
            list.Add(new UnitCondition("guarded", Get("guarded"), 0,
                $"{guard} intercepts damage aimed at this unit."));
        }

        if (u.HasVeil)
            list.Add(new UnitCondition("veil", Get("veil"), 0));
        if (u.ChitinAmount > 0)
            list.Add(new UnitCondition("chitin", Get("chitin"), u.ChitinAmount));
        if (u.IsTaunting)
            list.Add(new UnitCondition("taunting", Get("taunting"), 0));
        if (u.Heat > 0)
            list.Add(new UnitCondition("heat", Get("heat"), u.Heat));

        // Spirit wards are flags plus a turn counter, never status keys.
        if (u.IsUndying)
            list.Add(new UnitCondition(u.UndyingFullRestore ? "undying_full_restore" : "undying_turn",
                Get(u.UndyingFullRestore ? "undying_full_restore" : "undying_turn"), u.UndyingTurns));
        if (u.IsInvulnerable)
            list.Add(new UnitCondition("invulnerable", Get("invulnerable"), u.InvulnerableTurns));
        if (u.IsVigil)
            list.Add(new UnitCondition("vigil", Get("vigil"), u.VigilTurns));

        list.Sort((a, b) =>
        {
            int t = ((int)a.Tone).CompareTo((int)b.Tone);
            return t != 0 ? t : string.CompareOrdinal(a.Label, b.Label);
        });
        return list;
    }

    private static string Humanize(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "Unknown";
        var parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
        return string.Join(" ", parts);
    }
}
