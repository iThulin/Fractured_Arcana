using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// RegisterBarks.cs
//
// Purpose:        Data model and loader for the Register's lines
//                 (Data/Register/barks.json). Every line the
//                 Register speaks is data, never code: code fires
//                 a trigger key, this catalog decides what (if
//                 anything) is said.
// Layer:          Data
// Collaborators:  RegisterManager.cs (rate limits, slips, flags),
//                 EternalLedger.RegisterSeen (once flags)
// See:            docs/the_register_v1.md §4
// ============================================================

/// <summary>What kind of line a bark is. Decides priority, toggles and queueing (the_register_v1 §4).</summary>
public enum RegisterCategory
{
    /// <summary>Onboarding: queues, shows one at a time, waits to be dismissed. "Register explanations" toggle.</summary>
    Explain,
    /// <summary>A remark on something that just happened in combat. "Register commentary" toggle.</summary>
    Comment,
    /// <summary>An aside on a screen visit. "Register commentary" toggle.</summary>
    Flavor,
}

/// <summary>One authored bark: a trigger key, its conditions, and the lines it may speak.</summary>
public sealed class RegisterBark
{
    public string Key = "";
    public RegisterCategory Category = RegisterCategory.Flavor;
    /// <summary>Show at most once across every cycle (flag kept on <see cref="EternalLedger.RegisterSeen"/>).</summary>
    public bool Once;
    /// <summary>Seconds before this bark may speak again in the same session. 0 = no cooldown.</summary>
    public float CooldownSeconds;
    public int Priority;
    public string Title = "";
    public List<string> Lines = new();
    /// <summary>Plain rule text under the in-voice line. Explain notes only.</summary>
    public string Note = "";
    /// <summary>Argument matches; every pair must equal the fired argument of the same name.</summary>
    public Dictionary<string, string> When = new();

    /// <summary>The flag id this bark is remembered under. The key alone, unless the bark
    /// has conditions, so two variants of one key keep separate once flags.</summary>
    public string SeenId => When.Count == 0
        ? Key
        : Key + "|" + string.Join(",", When.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    /// <summary>True when every condition is met by <paramref name="args"/>.</summary>
    public bool Matches(IReadOnlyDictionary<string, string> args)
    {
        foreach (var pair in When)
        {
            if (args == null || !args.TryGetValue(pair.Key, out var v) || !string.Equals(v, pair.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}

/// <summary>Loads and serves the Register's barks. Lazy, loaded once per process.</summary>
public static class RegisterBarks
{
    public const string DefaultPath = "res://Data/Register/barks.json";

    private static readonly List<RegisterBark> _all = new();
    private static readonly Dictionary<string, List<RegisterBark>> _byKey = new(StringComparer.Ordinal);
    private static bool _loaded;

    /// <summary>Every bark in file order.</summary>
    public static IReadOnlyList<RegisterBark> All { get { EnsureLoaded(); return _all; } }

    /// <summary>The barks authored for <paramref name="key"/>, in file order. Empty for an unknown key.</summary>
    public static IReadOnlyList<RegisterBark> For(string key)
    {
        EnsureLoaded();
        return key != null && _byKey.TryGetValue(key, out var list) ? list : Array.Empty<RegisterBark>();
    }

    public static void EnsureLoaded(string path = DefaultPath)
    {
        if (_loaded)
            return;
        _loaded = true;
        Load(path);
    }

    /// <summary>Drops and rereads the file. For hot-editing lines.</summary>
    public static void Reload(string path = DefaultPath)
    {
        _loaded = false;
        EnsureLoaded(path);
    }

    private static void Load(string path)
    {
        _all.Clear();
        _byKey.Clear();

        using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (fa == null)
        {
            GD.PushWarning($"[Register] Could not open {path}; the Register will stay silent.");
            return;
        }

        var json = new Json();
        if (json.Parse(fa.GetAsText()) != Error.Ok)
        {
            GD.PushError($"[Register] Parse error in {path}: {json.GetErrorMessage()} (line {json.GetErrorLine()})");
            return;
        }

        var root = json.Data.AsGodotDictionary();
        if (!root.ContainsKey("barks"))
        {
            GD.PushWarning($"[Register] {path} has no \"barks\" array.");
            return;
        }

        foreach (var item in root["barks"].AsGodotArray())
        {
            var d = item.AsGodotDictionary();
            string key = d.ContainsKey("key") ? d["key"].AsString() : "";
            if (string.IsNullOrEmpty(key))
            {
                GD.PushWarning("[Register] A bark has no key; skipped.");
                continue;
            }

            var bark = new RegisterBark
            {
                Key = key,
                Category = ParseCategory(d.ContainsKey("category") ? d["category"].AsString() : ""),
                Once = d.ContainsKey("once") && d["once"].AsBool(),
                CooldownSeconds = d.ContainsKey("cooldown") ? (float)d["cooldown"].AsDouble() : 0f,
                Title = d.ContainsKey("title") ? d["title"].AsString() : "The Register",
                Note = d.ContainsKey("note") ? d["note"].AsString() : "",
            };
            bark.Priority = d.ContainsKey("priority")
                ? d["priority"].AsInt32()
                : bark.Category switch
                {
                    RegisterCategory.Explain => 100,
                    RegisterCategory.Comment => 50,
                    _ => 10,
                };

            if (d.ContainsKey("lines"))
            {
                foreach (var line in d["lines"].AsGodotArray())
                {
                    string s = line.AsString();
                    if (!string.IsNullOrWhiteSpace(s))
                        bark.Lines.Add(s);
                }
            }
            if (d.ContainsKey("when"))
            {
                var when = d["when"].AsGodotDictionary();
                foreach (var k in when.Keys)
                    bark.When[k.AsString()] = when[k].AsString();
            }

            if (bark.Lines.Count == 0 && string.IsNullOrEmpty(bark.Note))
            {
                GD.PushWarning($"[Register] Bark '{key}' has neither lines nor a note; skipped.");
                continue;
            }

            _all.Add(bark);
            if (!_byKey.TryGetValue(key, out var list))
                _byKey[key] = list = new List<RegisterBark>();
            list.Add(bark);
        }

        GD.Print($"[Register] Loaded {_all.Count} bark(s) from {path}.");
    }

    private static RegisterCategory ParseCategory(string s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "explain" => RegisterCategory.Explain,
        "comment" => RegisterCategory.Comment,
        _ => RegisterCategory.Flavor,
    };
}
