using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

// ============================================================
// NegotiationEncounterLoader.cs
//
// Purpose:        Loads NegotiationEncounterData from
//                 Data/Negotiations/*.json. Per-session cache.
//                 v3: migrates any pre-v3 file (a "terms" array)
//                 into clauses at load time and warns, so old
//                 authoring keeps working until it is rewritten.
// Layer:          Loader
// Collaborators:  NpcArchetype.cs (NegotiationEncounterData
//                 schema), NegotiationManager.cs (caller)
// See:            docs/negotiation_ledger_spec_v1.md §8, §10
// ============================================================

/// <summary>Lazy loader + per-session cache for negotiation encounter JSON. Each encounter file is read at most once per process.</summary>
public static class NegotiationEncounterLoader
{
    private const string DIR = "res://Data/Negotiations/";

    private static readonly Dictionary<string, NegotiationEncounterData> _cache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Returns a PER-TABLE CLONE of the cached encounter. The cache
    /// keeps the pristine authored data; every table gets its own copy, so
    /// runtime mutations (agreed clauses, injected tuition, ephemeral purses)
    /// can never leak into the next visit to the same encounter.</summary>
    public static NegotiationEncounterData Load(string id)
    {
        var pristine = LoadPristine(id);
        if (pristine == null) return null;
        var clone = JsonSerializer.Deserialize<NegotiationEncounterData>(
            JsonSerializer.Serialize(pristine, JsonOptions), JsonOptions);
        // Runtime fields are [JsonIgnore]: the clone comes back with every
        // clause Open/unrevealed, which is exactly the fresh-table state.
        return clone;
    }

    private static NegotiationEncounterData LoadPristine(string id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;

        string path = $"{DIR}{id}.json";
        if (!FileAccess.FileExists(path))
        {
            GD.PrintErr($"NegotiationEncounterLoader: No file at {path}");
            return null;
        }

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            var data = JsonSerializer.Deserialize<NegotiationEncounterData>(
                file.GetAsText(), JsonOptions);

            if (data != null)
            {
                MigrateLegacy(data);
                _cache[id] = data;
            }
            GD.Print($"NegotiationEncounterLoader: Loaded '{id}'");
            return data;
        }
        catch (Exception e)
        {
            GD.PrintErr($"NegotiationEncounterLoader: Error loading {id}: {e.Message}");
            return null;
        }
    }

    // ── v2 → v3 shim (spec §10) ──────────────────────────────────────────

    /// <summary>Turn a pre-v3 "terms" array into clauses. favorPlayer → side;
    /// value from the payload magnitude; hidden penalties become riders on
    /// the most valuable theirs clause; hidden bonuses become Warm-only
    /// extras. Positions, weights and NPC pools are dropped. Logs a warning
    /// per shimmed file so it gets rewritten by hand.</summary>
    public static void MigrateLegacy(NegotiationEncounterData data)
    {
        if (data.Terms == null || data.Terms.Count == 0)
            return;
        if (data.Clauses != null && data.Clauses.Count > 0)
        {
            // Both present: clauses win; the terms are stale copy.
            data.Terms.Clear();
            return;
        }

        data.Clauses = new List<Clause>();
        var hiddenPenalties = new List<Clause>();
        foreach (var t in data.Terms)
        {
            var c = new Clause
            {
                Id = t.Id,
                Text = t.Description,
                ShortName = t.ShortName,
                Side = t.FavorPlayer ? ClauseSide.Theirs : ClauseSide.Yours,
                Kind = GuessKind(t),
                Value = DeriveValue(t),
                GoldDelta = t.GoldDelta,
                ReputationDelta = t.ReputationDelta,
                FactionId = t.FactionId,
                LoreUnlock = t.LoreUnlock,
                FuelDelta = t.StepsDelta,
                SuppliesDelta = t.SuppliesDelta,
                RevealsSupplyCaches = t.RevealsSupplyCaches,
                SpellId = t.SpellId,
            };
            if (t.IsHidden && !t.FavorPlayer)
            {
                // A buried penalty: becomes a rider (a yours-side clause the
                // headline item drags in), surfaced the moment you Ask.
                hiddenPenalties.Add(c);
            }
            else if (t.IsHidden && t.FavorPlayer)
            {
                c.RequiresWarm = true;   // "they were holding more than they let on"
            }
            data.Clauses.Add(c);
        }

        // Attach each hidden penalty to the most valuable un-ridered theirs
        // clause, in descending value order.
        var carriers = data.Clauses
            .Where(c => c.Side == ClauseSide.Theirs && !c.RequiresWarm)
            .OrderByDescending(c => c.Value)
            .ToList();
        int ci = 0;
        foreach (var penalty in hiddenPenalties)
        {
            while (ci < carriers.Count && carriers[ci].HasRider) ci++;
            if (ci >= carriers.Count) break;
            carriers[ci].Rider = penalty.Id;
            ci++;
        }

        data.Terms.Clear();
        GD.PushWarning($"NegotiationEncounterLoader: '{data.Id}' used the v2 'terms' schema; " +
                       $"migrated {data.Clauses.Count} clause(s) at load. Rewrite the file in v3 form " +
                       "(docs/negotiation_ledger_spec_v1.md §8).");
    }

    private static ClauseKind GuessKind(LegacyDealTerm t)
    {
        if (!string.IsNullOrEmpty(t.SpellId)) return ClauseKind.Spell;
        if (!string.IsNullOrEmpty(t.LoreUnlock) || t.RevealsSupplyCaches) return ClauseKind.Lore;
        if (t.SuppliesDelta != 0) return ClauseKind.Goods;
        if (t.StepsDelta != 0) return t.FavorPlayer ? ClauseKind.Passage : ClauseKind.Service;
        if (t.GoldDelta != 0) return ClauseKind.Gold;
        if (t.ReputationDelta != 0) return ClauseKind.Honor;
        return t.FavorPlayer ? ClauseKind.Service : ClauseKind.Favor;
    }

    /// <summary>spec §10: clamp(round(|gold|/20) + 2·|rep| + (lore?2) + |fuel|, 1, 6).</summary>
    private static int DeriveValue(LegacyDealTerm t)
    {
        int v = Mathf.RoundToInt(Mathf.Abs(t.GoldDelta) / 20f)
              + 2 * Mathf.Abs(t.ReputationDelta)
              + (string.IsNullOrEmpty(t.LoreUnlock) ? 0 : 2)
              + Mathf.Abs(t.StepsDelta)
              + Mathf.RoundToInt(Mathf.Abs(t.SuppliesDelta) / 10f)
              + (string.IsNullOrEmpty(t.SpellId) ? 0 : 3)
              + (t.RevealsSupplyCaches ? 2 : 0);
        return Mathf.Clamp(v, 1, 6);
    }

    // ── Pooling ──────────────────────────────────────────────────────────

    /// <summary>Quiet existence probe for candidate pooling: no error spam
    /// for the (expected) region-specific files that don't exist. Returns the
    /// PRISTINE cached instance (read-only use only; PickForTerrain re-Loads
    /// by id so the winner is handed out as a proper clone).</summary>
    private static NegotiationEncounterData TryLoad(string id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;
        if (!FileAccess.FileExists($"{DIR}{id}.json")) return null;
        return LoadPristine(id);
    }

    /// <summary>Suffixes tried per region, and the generic pool. The
    /// archetype-generic encounters make all six NPC archetypes reachable.</summary>
    private static readonly string[] RegionSuffixes =
        { "commander", "merchant", "scholar", "opportunist", "idealist", "survivor" };
    private static readonly string[] GenericPool =
    {
        "generic_merchant", "generic_scholar", "generic_opportunist",
        "generic_idealist", "generic_survivor",
    };

    /// <summary>
    /// Pick a random negotiation encounter appropriate for a terrain type.
    /// Region-specific encounters ({regionId}_{archetype}) are weighted
    /// double so authored flavor wins over the generic pool when it exists.
    /// </summary>
    public static NegotiationEncounterData PickForTerrain(string terrain, string regionId)
    {
        var available = new List<NegotiationEncounterData>();

        foreach (var suffix in RegionSuffixes)
        {
            var data = TryLoad($"{regionId}_{suffix}");
            if (data != null) { available.Add(data); available.Add(data); }
        }

        foreach (var id in GenericPool)
        {
            var data = TryLoad(id);
            if (data != null) available.Add(data);
        }

        if (available.Count == 0) return null;
        var picked = available[(int)(GD.Randi() % (uint)available.Count)];
        return Load(picked.Id);
    }

    public static void ClearCache() => _cache.Clear();
}
