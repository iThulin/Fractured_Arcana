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
    /// matched to terrain/region, then resolve any remaining tokens against
    /// save-data substitutions from EchoSeeder. An unknown or unmatched slot
    /// keeps its literal token, so authoring gaps are visible rather than
    /// silent.</summary>
    public static string Assemble(string body, OverworldHex.TerrainType terrain, string regionId)
    {
        if (string.IsNullOrEmpty(body) || body.IndexOf('{') < 0) return body;
        EnsureLoaded();
        string terrainName = terrain.ToString();
        return TokenRx.Replace(body, m =>
        {
            string key = m.Groups[1].Value;
            // Try fragment library first
            string fragment = Pick(key, terrainName, regionId);
            if (fragment != null) return fragment;
            // Then try save-data substitutions from the echo seeder
            var subs = EchoSeeder.Substitutions;
            if (subs != null && subs.TryGetValue(key, out string sub))
                return sub;
            // Unknown token — keep literal so authoring gaps are visible
            return m.Value;
        });
    }

    /// <summary>Non-destructive: returns the original encounter untouched when
    /// nothing it displays carries a token, else a display clone with an assembled
    /// title, body and — new — assembled choice text. Choice text was previously
    /// un-tokenizable because the clone shared the Choices list by reference;
    /// writing assembled text into those objects would have poisoned the cached
    /// pool entry for every later draw. The clone now deep-copies the choice list
    /// (and ONLY when a choice actually carries a token, so the common case still
    /// shares by reference and allocates nothing). Resolution reads its effects
    /// off the choice the panel hands back, and Clone() copies every effect field,
    /// so outcomes are identical either way. The pool entry is never mutated.</summary>
    public static NarrativeEncounterData ForDisplay(NarrativeEncounterData enc,
                                                    OverworldHex.TerrainType terrain, string regionId)
    {
        if (enc == null) return enc;
        bool bodyHasTokens = !string.IsNullOrEmpty(enc.Body) && enc.Body.IndexOf('{') >= 0;
        bool titleHasTokens = !string.IsNullOrEmpty(enc.Title) && enc.Title.IndexOf('{') >= 0;
        bool choicesHaveTokens = false;
        if (enc.Choices != null)
        {
            foreach (var ch in enc.Choices)
            {
                if (ch == null) continue;
                if ((!string.IsNullOrEmpty(ch.Label) && ch.Label.IndexOf('{') >= 0) ||
                    (!string.IsNullOrEmpty(ch.ResultText) && ch.ResultText.IndexOf('{') >= 0))
                {
                    choicesHaveTokens = true;
                    break;
                }
            }
        }
        if (!bodyHasTokens && !titleHasTokens && !choicesHaveTokens) return enc;

        var choices = enc.Choices;
        if (choicesHaveTokens)
        {
            choices = new List<EncounterChoice>(enc.Choices.Count);
            foreach (var ch in enc.Choices)
            {
                if (ch == null) { choices.Add(null); continue; }
                var copy = ch.Clone();
                copy.Label = Assemble(copy.Label, terrain, regionId);
                copy.ResultText = Assemble(copy.ResultText, terrain, regionId);
                choices.Add(copy);
            }
        }

        return new NarrativeEncounterData
        {
            Id = enc.Id,
            Title = titleHasTokens ? Assemble(enc.Title, terrain, regionId) : enc.Title,
            Body = bodyHasTokens ? Assemble(enc.Body, terrain, regionId) : enc.Body,
            TerrainTags = enc.TerrainTags,
            RegionTags = enc.RegionTags,
            RequiredFlag = enc.RequiredFlag,
            ArchmageId = enc.ArchmageId,
            Choices = choices,
        };
    }

    // Specificity weights. A fragment tagged to BOTH this region and this
    // terrain is six times as likely to be drawn as an unconstrained one — but
    // it does not silence the broad pool, which is the whole difference between
    // this and the original hard ladder. Under the ladder, authoring even one
    // region-tagged fragment collapsed that slot to the region's own fragments
    // for the entire region, so the in-region repeat rate was pinned to the
    // number of region fragments no matter how large the library grew. Bespoke
    // flavour should tilt the draw, not own it.
    private const int WEIGHT_BOTH = 6;
    private const int WEIGHT_TERRAIN = 3;
    private const int WEIGHT_REGION = 2;
    private const int WEIGHT_ANY = 1;

    /// <summary>Pick a fragment for a slot, weighted toward specificity:
    /// region+terrain &gt; terrain &gt; region &gt; unconstrained, blended rather
    /// than hard-preferred so the broad library always stays in the draw.</summary>
    private static string Pick(string slot, string terrainName, string regionId)
    {
        if (_slots == null || !_slots.TryGetValue(slot, out var pool) || pool.Count == 0)
            return null;

        List<string> texts = new();
        List<int> weights = new();
        int total = 0;
        foreach (var fr in pool)
        {
            bool tMatch = fr.TerrainTags.Count == 0 || fr.TerrainTags.Contains(terrainName);
            bool rMatch = fr.RegionTags.Count == 0 ||
                          (regionId != null && fr.RegionTags.Contains(regionId));
            if (!tMatch || !rMatch) continue;
            bool tSpec = fr.TerrainTags.Count > 0;
            bool rSpec = fr.RegionTags.Count > 0;
            int w = tSpec && rSpec ? WEIGHT_BOTH
                  : tSpec ? WEIGHT_TERRAIN
                  : rSpec ? WEIGHT_REGION
                  : WEIGHT_ANY;
            texts.Add(fr.Text);
            weights.Add(w);
            total += w;
        }
        if (total <= 0) return null;

        int roll = (int)(GD.Randi() % (uint)total);
        for (int i = 0; i < texts.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return texts[i];
        }
        return texts[texts.Count - 1];
    }
}
