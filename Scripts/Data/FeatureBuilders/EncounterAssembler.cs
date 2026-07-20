using Godot;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

// ============================================================
// EncounterAssembler.cs
//
// Purpose:        Systemic content assembler (discovery_loop_spec
//                 §content-strategy). Fills {slot} tokens in an
//                 encounter body with terrain/region-tagged
//                 fragments, so ONE authored skeleton reads
//                 differently across the world. Fewer authored
//                 pieces, combinatorial apparent variety — the
//                 only version of "Stellaris density" a solo dev
//                 reaches without hand-writing hundreds of events.
// Layer:          Data / assembler
// Collaborators:  NarrativeEncounterData.cs (skeleton bodies),
//                 ExpeditionManager.cs (assembles at show-time),
//                 Data/Encounters/fragments.json (the library)
// ============================================================

/// <summary>One interchangeable fragment for a skeleton slot, tagged so the map's
/// state can pick a fitting one. Empty tag list = matches anything.</summary>
public class EncounterFragment
{
    public string Text = "";
    public List<string> TerrainTags = new();
    public List<string> RegionTags = new();
}

/// <summary>Fills {slot} tokens in encounter bodies from a tagged fragment
/// library. Stateless apart from the cached library.</summary>
public static class EncounterAssembler
{
    private const string FRAGMENTS_PATH = "res://Data/Encounters/fragments.json";
    private static Dictionary<string, List<EncounterFragment>> _slots;
    private static readonly Regex TokenRx =
        new(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    private static void EnsureLoaded()
    {
        if (_slots != null) return;
        _slots = new Dictionary<string, List<EncounterFragment>>();
        if (!FileAccess.FileExists(FRAGMENTS_PATH))
        {
            GD.Print($"EncounterAssembler: no fragment library at {FRAGMENTS_PATH}");
            return;
        }
        try
        {
            using var f = FileAccess.Open(FRAGMENTS_PATH, FileAccess.ModeFlags.Read);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<EncounterFragment>>>(
                f.GetAsText(), JsonOptions);
            if (parsed != null) _slots = parsed;
            GD.Print($"EncounterAssembler: loaded {_slots.Count} fragment slot(s).");
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"EncounterAssembler: error loading fragments: {e.Message}");
        }
    }

    public static void ClearCache() => _slots = null;

    /// <summary>Replace {slot} tokens in <paramref name="body"/> with fragments
    /// matched to terrain/region. An unknown or unmatched slot keeps its literal
    /// token, so authoring gaps are visible rather than silent.</summary>
    public static string Assemble(string body, OverworldHex.TerrainType terrain, string regionId)
    {
        if (string.IsNullOrEmpty(body) || body.IndexOf('{') < 0) return body;
        EnsureLoaded();
        string terrainName = terrain.ToString();
        return TokenRx.Replace(body, m => Pick(m.Groups[1].Value, terrainName, regionId) ?? m.Value);
    }

    /// <summary>Non-destructive: returns the original encounter when its body has
    /// no tokens, else a display clone with an assembled body. Choices are shared
    /// by reference — resolution still runs against the caller's ORIGINAL
    /// encounter (Id/flags), so the cached pool entry is never mutated.</summary>
    public static NarrativeEncounterData ForDisplay(NarrativeEncounterData enc,
                                                    OverworldHex.TerrainType terrain, string regionId)
    {
        if (enc == null || string.IsNullOrEmpty(enc.Body) || enc.Body.IndexOf('{') < 0)
            return enc;
        return new NarrativeEncounterData
        {
            Id = enc.Id,
            Title = enc.Title,
            Body = Assemble(enc.Body, terrain, regionId),
            TerrainTags = enc.TerrainTags,
            RegionTags = enc.RegionTags,
            Choices = enc.Choices,
        };
    }

    /// <summary>Pick a fragment for a slot, preferring specificity:
    /// both-tag match &gt; terrain-only &gt; region-only &gt; unconstrained.</summary>
    private static string Pick(string slot, string terrainName, string regionId)
    {
        if (_slots == null || !_slots.TryGetValue(slot, out var pool) || pool.Count == 0)
            return null;

        List<string> both = new(), terr = new(), reg = new(), any = new();
        foreach (var fr in pool)
        {
            bool tMatch = fr.TerrainTags.Count == 0 || fr.TerrainTags.Contains(terrainName);
            bool rMatch = fr.RegionTags.Count == 0 ||
                          (regionId != null && fr.RegionTags.Contains(regionId));
            if (!tMatch || !rMatch) continue;
            bool tSpec = fr.TerrainTags.Count > 0;
            bool rSpec = fr.RegionTags.Count > 0;
            if (tSpec && rSpec) both.Add(fr.Text);
            else if (tSpec) terr.Add(fr.Text);
            else if (rSpec) reg.Add(fr.Text);
            else any.Add(fr.Text);
        }
        var chosen = both.Count > 0 ? both : terr.Count > 0 ? terr : reg.Count > 0 ? reg : any;
        if (chosen.Count == 0) return null;
        return chosen[(int)(GD.Randi() % (uint)chosen.Count)];
    }
}
