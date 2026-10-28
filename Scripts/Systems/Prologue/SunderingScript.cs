using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// SunderingScript.cs
//
// Purpose:        The Sundering's volley set (S1,
//                 docs/sundering_long_second_founding_v1.md section 2):
//                 what the faculty and the class cast from the dais and
//                 the tiers each round, the order in which the Keepers
//                 spend them, the hall's breaks, the opening strike, the
//                 floor minimum's arrivals and the climax's lines; and
//                 (S2) the lines of the Long Second and the way out:
//                 the freeze's count, the walk's hint and what the mouse
//                 reads in the frozen hall. Read from
//                 Data/Encounters/sundering_volleys.json and named by a
//                 prologue fight's script ("volleys"). Data only:
//                 CombatManager.Sundering.cs runs it.
// Layer:          System (static, data)
// Collaborators:  PrologueDirector.cs (PrologueFightScript.Volleys, the
//                 round-trip assert), CombatManager.Sundering.cs
// ============================================================

/// <summary>One volley set: everything off the board that the Sundering runs.</summary>
public sealed class SunderingVolleySet
{
    public string Id = "";
    /// <summary>The objective line's label; the sentence so far follows it.</summary>
    public string BannerLabel = "";
    /// <summary>The objective line's hover text: what the sentence is and what it holds.</summary>
    public string BannerTooltip = "";
    /// <summary>The traitor's spell, said once in the banner's hover text.</summary>
    public string TraitorLine = "";
    /// <summary>The log line as the wards break and the opening is theirs.</summary>
    public string OpeningLog = "";
    /// <summary>The Keepers' first spell, at the candidate, before the first turn.</summary>
    public SunderingStrike OpeningStrike = null;
    /// <summary>The log line when the hold keeps the candidate standing (once a round at most).</summary>
    public string HoldLog = "";
    /// <summary>The faculty and the class, in casting order each round.</summary>
    public List<SunderingVolley> Volleys = new();
    /// <summary>Who the Keepers spend, one at each round boundary: "class" or an archmage id.
    /// The traitor and anyone not present are skipped.</summary>
    public List<string> SpendOrder = new();
    /// <summary>The counter-volley's line at the class.</summary>
    public string SpendClassLog = "";
    /// <summary>The counter-volley's line at an archmagus ({archmage}: the full name).</summary>
    public string SpendArchmageLog = "";
    /// <summary>The hall's breaks, by round boundary (0: the opening).</summary>
    public List<SunderingSetBeat> SetBeats = new();
    /// <summary>The unit the floor minimum sends through the doors, and its difficulty.</summary>
    public string FloorUnit = "";
    public float FloorUnitMult = 1f;
    public string FloorLog = "";
    /// <summary>The climax: the candidate drawn onto the mark, then the spell.</summary>
    public string ClimaxDrawLog = "";
    public string ClimaxSpellLog = "";

    // ── The Long Second and the way out (S2) ──
    /// <summary>The log line at the freeze ({count}: how many Keepers still stand, as a word).</summary>
    public string FreezeLog = "";
    /// <summary>The freeze's line when one Keeper still stands.</summary>
    public string FreezeLogOne = "";
    /// <summary>The freeze's line when none does.</summary>
    public string FreezeLogNone = "";
    /// <summary>The hint while you walk out of the frozen hall.</summary>
    public string WalkHint = "";
    /// <summary>The hint when the frozen Keepers wall off the doors: the way out is then as far
    /// toward the doors as you can get.</summary>
    public string WalkBlocked = "";
    /// <summary>What the mouse reads in the frozen hall, one line each: a Keeper ({name}), a unit
    /// of yours ({name}), the way out, the ground ({element}) and a memorial ({name}).</summary>
    public string HoverKeeper = "";
    public string HoverAlly = "";
    public string HoverDoors = "";
    public string HoverGround = "";
    public string HoverMemorial = "";
}

/// <summary>A scripted strike from off the board (the opening's).</summary>
public sealed class SunderingStrike
{
    public int Damage = 0;
    /// <summary>The VFX palette (VfxLibrary's style names: "temporal", "arcane", ...).</summary>
    public string Style = "";
    public string Log = "";
}

/// <summary>One figure's spell in the volley phase. <see cref="Rule"/> picks the targets and
/// the effect; the numbers are the rule's. Log tokens: {caster}, {target}, {targets}, {damage},
/// {count}, and the four globals ({wizard_name}, {guild_name}, {traitor_full}, {traitor_short}).</summary>
public sealed class SunderingVolley
{
    /// <summary>An archmage id from the traitor pool, "examiner" or "class".</summary>
    public string Id = "";
    public string Name = "";
    public string Rule = "";
    public int Damage = 0;
    public int Count = 1;
    public int Radius = 1;
    public int Turns = 2;
    public int Health = 10;
    public int Speed = 2;
    public bool Negate = false;
    public string Style = "";
    /// <summary>"bolt", "arc", "burst" or "impact" (VfxLibrary).</summary>
    public string Archetype = "";
    public string Log = "";
    /// <summary>turret_then_bolt: the line once the turret stands.</summary>
    public string RepeatLog = "";
    /// <summary>raise_fallen: the line when the spirits strike ({count}: how many blows).</summary>
    public string StrikeLog = "";
    /// <summary>raise_fallen: the line when nothing fell and no spirit stands (a plain bolt).</summary>
    public string FallbackLog = "";
}

/// <summary>One of the hall's breaks: the piece (HallSet.Piece*) at a round boundary.</summary>
public sealed class SunderingSetBeat
{
    /// <summary>0 is the opening; 2 to 5 are the boundaries into those rounds.</summary>
    public int Boundary = 0;
    public string Piece = "";
    public string Log = "";
}

/// <summary>Process-wide reader for Data/Encounters/sundering_volleys.json.</summary>
public static class SunderingScript
{
    public const string DataPath = "res://Data/Encounters/sundering_volleys.json";

    // ── The volley rules (CombatManager.Sundering.cs runs each) ──────────
    /// <summary>The Keeper with the most dangerous plan takes Damage; with Negate, its next action is struck.</summary>
    public const string RuleWorstIntent = "worst_intent";
    /// <summary>The thickest cluster of Keepers (Radius) takes Damage each, and the ground there takes Storm.</summary>
    public const string RuleClusterStorm = "cluster_storm";
    /// <summary>The Count Keepers nearest the candidate are Named for Turns: Damage if they move.</summary>
    public const string RuleNameNearest = "name_nearest";
    /// <summary>One Keeper that fell this round rises where it fell as a spirit on your side
    /// (Health, Damage, Speed); then every spirit of yours beside a Keeper strikes it.</summary>
    public const string RuleRaiseFallen = "raise_fallen";
    /// <summary>The first time, a turret lands beside the candidate as your construct; after that,
    /// Damage at the Keeper nearest the candidate.</summary>
    public const string RuleTurretThenBolt = "turret_then_bolt";
    /// <summary>Damage at the Keeper nearest the candidate.</summary>
    public const string RuleUnerringNearest = "unerring_nearest";
    /// <summary>Count bolts of Damage at random Keepers.</summary>
    public const string RuleRandomBolts = "random_bolts";

    public static readonly string[] Rules =
    {
        RuleWorstIntent, RuleClusterStorm, RuleNameNearest, RuleRaiseFallen,
        RuleTurretThenBolt, RuleUnerringNearest, RuleRandomBolts,
    };

    // Mirrors PrologueDirector's reader, so the two files read the same way.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    private static Dictionary<string, SunderingVolleySet> _sets;

    /// <summary>The set named <paramref name="id"/>, or null when it is not in the file.</summary>
    public static SunderingVolleySet Get(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(id) && _sets.TryGetValue(id, out var set) ? set : null;
    }

    /// <summary>Every set in the file (the prologue's round-trip assert reads them).</summary>
    public static IReadOnlyCollection<SunderingVolleySet> All
    {
        get
        {
            EnsureLoaded();
            return _sets.Values;
        }
    }

    /// <summary>Testing hook: drop the loaded data so edited JSON reloads.</summary>
    public static void ClearCache() => _sets = null;

    private static void EnsureLoaded()
    {
        if (_sets != null)
            return;
        _sets = new Dictionary<string, SunderingVolleySet>();
        if (!FileAccess.FileExists(DataPath))
        {
            GD.PushError($"SunderingScript: {DataPath} is missing.");
            return;
        }
        try
        {
            using var f = FileAccess.Open(DataPath, FileAccess.ModeFlags.Read);
            if (f == null)
                return;
            var list = JsonSerializer.Deserialize<List<SunderingVolleySet>>(f.GetAsText(), JsonOptions);
            if (list != null)
                foreach (var set in list)
                    if (set != null && !string.IsNullOrEmpty(set.Id) && !_sets.ContainsKey(set.Id))
                        _sets[set.Id] = set;
        }
        catch (Exception e)
        {
            GD.PrintErr($"SunderingScript: error loading {DataPath}: {e.Message}");
        }
        GD.Print($"SunderingScript: loaded {_sets.Count} volley set(s).");
    }
}
