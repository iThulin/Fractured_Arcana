using System.Collections.Generic;

// ============================================================
// UIContent.cs  (V2)
//
// Purpose:        Plain-language strings for the combat UI, keyed
//                 by BehaviorKey / tag / AbilityKey (combat_ui_v2
//                 §7b: content, not code — authoring a unit means
//                 authoring its strings). Also the ability-icon
//                 glyph table (§6) from the proven Label3D/font
//                 glyph range.
// Layer:          UI (static content)
// Collaborators:  CombatUI.cs (roster rows + inspect blocks),
//                 UnitDefinition.cs (keys these describe).
// See:            combat_ui_v2 §6–7b
// ============================================================

/// <summary>Static lookup tables turning data keys into player-facing language.
/// Unknown keys return honest fallbacks rather than empty strings — a missing
/// entry should read as a TODO in playtests, not vanish.</summary>
public static class UIContent
{
    // ── Behavior lines (keyed by BehaviorKey) — "rules and reach" language ──
    private static readonly Dictionary<string, string> BehaviorLines = new(System.StringComparer.OrdinalIgnoreCase)
    {
        { "melee_advance",           "Advances on the nearest unit and strikes." },
        { "melee_target_highest_hp", "Marks your healthiest unit and grinds toward it." },
        { "hold_until_near",         "Holds position and guards; strikes whatever comes adjacent." },
        { "ranged_kite",             "Keeps its distance and shoots; retreats when pressed." },
        { "ranged_charge",           "Channels one turn, then releases a heavy blast; the target tile is locked when the channel begins." },
        { "melee_hunt_wounded",      "Hunts whichever of your units is closest to death, ignoring distance." },
    };

    // ── Tag clauses (appended to the behavior line) ─────────────────────────
    private static readonly Dictionary<string, string> TagClauses = new(System.StringComparer.OrdinalIgnoreCase)
    {
        { "pack",     "+1 damage beside a packmate" },
        { "bulwark",  "plants itself in front of wounded allies" },
        { "charge",   "sprints its full speed and hits harder on arrival" },
        { "scout",    "circles for the flank; breaks off when crowded" },
        { "immobile", "never moves" },
    };

    // ── Ability icons (keyed by AbilityKey) — proven glyph range only ───────
    private static readonly Dictionary<string, string> AbilityIcons = new(System.StringComparer.OrdinalIgnoreCase)
    {
        { "requiem",    "✦" },
        { "deathburst", "✸" },
    };

    /// <summary>Behavior line + tag clauses in one sentence, e.g.
    /// "Advances on the nearest unit and strikes. Pack: +1 damage beside a packmate."</summary>
    public static string DescribeBehavior(string behaviorKey, IReadOnlyList<string> tags)
    {
        string line = BehaviorLines.TryGetValue(behaviorKey ?? "", out var b)
            ? b : $"(behavior '{behaviorKey}' — no description authored)";

        if (tags != null)
        {
            foreach (var tag in tags)
            {
                string clause = TagClauses.TryGetValue(tag, out var c)
                    ? c : $"(tag '{tag}' — no description authored)";
                line += $" {Capitalize(tag)}: {clause}.";
            }
        }
        return line;
    }

    public static string AbilityIcon(string abilityKey)
        => AbilityIcons.TryGetValue(abilityKey ?? "", out var icon) ? icon : "●";

    /// <summary>Role marker glyph (§6): Line = dot, Elite = chevron, Boss = crest.</summary>
    public static string RoleMarker(string role) => role?.ToLowerInvariant() switch
    {
        "elite" => "»",
        "boss"  => "◆",
        _       => "·",
    };

    public static string RoleDisplay(string role) => role?.ToLowerInvariant() switch
    {
        "elite"  => "Elite",
        "boss"   => "Boss",
        "summon" => "Summon",
        _        => "Line",
    };

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
