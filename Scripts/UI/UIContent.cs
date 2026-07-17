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

    // ── Behavior-tag chips (v2.2: pack/charge/bulwark telegraph) ────────────
    // Letter chips from the proven ASCII range — the roster row telegraphs a
    // unit's tag mechanics at a glance; the tooltip carries the authored clause.
    private static readonly Dictionary<string, string> TagChipLetters = new(System.StringComparer.OrdinalIgnoreCase)
    {
        { "pack",     "P" },
        { "charge",   "C" },
        { "bulwark",  "B" },
        { "scout",    "S" },
        { "immobile", "▪" },
    };

    /// <summary>Chip letter for a behavior tag, or null when the tag has no
    /// authored chip (inert tags like flock/flying stay off the roster row —
    /// a chip is a promise the mechanic is wired).</summary>
    public static string TagChipLetter(string tag)
        => TagChipLetters.TryGetValue(tag ?? "", out var letter) ? letter : null;

    /// <summary>Chip tooltip: "Pack: +1 damage beside a packmate."</summary>
    public static string TagChipTooltip(string tag)
        => TagClauses.TryGetValue(tag ?? "", out var c)
            ? $"{Capitalize(tag)}: {c}."
            : Capitalize(tag ?? "");

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

    /// <summary>V3 (§9): THE ability log grammar — [Source] AbilityName: effect (state).
    /// Every ability handler routes through this so eight rosters don't invent
    /// eight formats. Movement/attack lines keep their terse forms.</summary>
    public static string FormatLogLine(string source, string ability, string effect, string state = null)
        => string.IsNullOrEmpty(state)
            ? $"[{source}] {ability}: {effect}"
            : $"[{source}] {ability}: {effect} ({state})";

    // ── Reaction line grammar (§9, v2.2 completion) ─────────────────────────
    // Dodge = the listed victim vacated the tile before resolution; Redirect =
    // a Reaction replaced the victim. Both route through FormatLogLine so
    // reactions read in the same grammar as abilities and item procs.

    /// <summary>"[Wolf 2] Dodge: vacated the tile — Boar's charge whiffs".</summary>
    public static string ReactionDodgeLine(string dodgerName, string attackerName, string strikeNoun)
        => FormatLogLine(dodgerName, "Dodge", $"vacated the tile — {attackerName}'s {strikeNoun} whiffs");

    /// <summary>"[Bear] Redirect: intercepts Ranger 1's shot (7 damage)".</summary>
    public static string ReactionRedirectLine(string newVictimName, string attackerName, string strikeNoun, int damage)
        => FormatLogLine(newVictimName, "Redirect", $"intercepts {attackerName}'s {strikeNoun}", $"{damage} damage");
}
