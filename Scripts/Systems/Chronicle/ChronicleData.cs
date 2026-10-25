using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// ChronicleData.cs
//
// Purpose:        The shapes the chronicle scheduler reads and
//                 writes (wire item C1, docs/story_wiring_v1.md).
//                 Story files live in Data/Story; each names the
//                 panel file in Data/Encounters that holds its
//                 panels. ChronicleQueued is the one save struct:
//                 a panel waiting for a quiet moment, persisted on
//                 CycleState.ChronicleQueue.
// Layer:          Data
// Collaborators:  ChronicleScheduler.cs (the only reader),
//                 CycleState.cs (ChronicleQueue), SaveManager.cs
//                 (the save options the queue travels through)
// See:            docs/story_wiring_v1.md, C1 (Data, Beat fields,
//                 Shape)
// ============================================================

/// <summary>One beat: a hook it listens on, the conditions it fires under, what it writes
/// and the panel it shows. Field names match the JSON (camelCase).</summary>
public class ChronicleBeat
{
    public string Id = "";
    public string Hook = "";
    /// <summary>"Eternal" writes <see cref="Once"/> to MetaNarrativeFlags; anything else
    /// (the data says "Timeline") writes it to the cycle's WorldFlags.</summary>
    public string Layer = "";
    public string Once = "";
    public string RememberFlag = "";
    public string Group = "";
    public string Show = "";
    public string Bark = "";
    public string Lore = "";
    public string Delivery = "";

    /// <summary>W4: evaluated only when nothing else on the raise showed or queued a panel.</summary>
    public bool Fallback = false;

    /// <summary>Argument values that must all match, case-insensitively. A value is a string
    /// or a list of alternatives (task 9), so it is read as raw JSON and normalized at load.</summary>
    public Dictionary<string, JsonElement> When = new();

    public List<string> Requires = new();
    public List<string> Excludes = new();
    public List<string> AlsoSetMeta = new();
    public List<string> AlsoSetTimeline = new();
    public List<string> Tokens = new();

    /// <summary>Per-token overrides for the line lookup: token, then argument, then value
    /// (values take {arg} substitution).</summary>
    public Dictionary<string, Dictionary<string, string>> LineArgs = new();

    /// <summary>The finale (C6): line-lookup arguments read from flags. Argument name, then
    /// flag (with {arg} substitution); true when the flag is set in either layer.</summary>
    public Dictionary<string, string> FlagArgs = new();

    /// <summary><see cref="When"/> normalized at load: each argument's allowed values.
    /// Internal, so the serializer never sees it.</summary>
    internal Dictionary<string, string[]> WhenNorm = new();
}

/// <summary>One line: the text a line token resolves to when its conditions match. Within a
/// key, the first line whose conditions all match wins, so each key's fallback (empty
/// conditions) comes last in the data.</summary>
public class ChronicleLine
{
    public string Key = "";
    public string Text = "";
    public Dictionary<string, JsonElement> When = new();

    internal Dictionary<string, string[]> WhenNorm = new();
}

/// <summary>One file in Data/Story. A part file carries <see cref="Part"/> (a number); any
/// other story file carries <see cref="Arc"/>, its id. Lines and groups are file-local.</summary>
public class ChronicleFile
{
    public int Part = 0;
    public string Arc = "";
    public string EncounterFile = "";
    /// <summary>Line token, then the key in <see cref="Lines"/> it reads.</summary>
    public Dictionary<string, string> LineTokens = new();
    public List<ChronicleBeat> Beats = new();
    public List<ChronicleLine> Lines = new();

    /// <summary>The file name without ".json". Set at load; the queue persists it.</summary>
    internal string Name = "";
}

/// <summary>A chronicle panel waiting for a quiet moment (C1 rule 6). Flags are written when
/// the beat fires, so persisting this is what keeps a panel from being lost to a crash or a
/// scene change. Lives on <see cref="CycleState.ChronicleQueue"/>.</summary>
public class ChronicleQueued
{
    /// <summary>The story file's name (Data/Story, without ".json").</summary>
    public string File = "";
    public string BeatId = "";
    /// <summary>The panel id, with {arg} substitution already applied.</summary>
    public string PanelId = "";
    /// <summary>Every token the panel needed, resolved when the beat fired: token, then
    /// value. A token the beat could not resolve is absent, and shows literally.</summary>
    public Dictionary<string, string> Tokens = new();
}
