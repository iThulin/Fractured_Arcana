using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

// ============================================================
// CardUpgradeApplier.cs
//
// Purpose:        Applies upgrade tier patches to a card at
//                 instantiation time. Re-parses the card's JSON
//                 with the upgrade's field-path changes applied,
//                 then rebuilds the CardHalf(s) through the
//                 normal JsonCardLoader pipeline so all effects,
//                 targeters, and predicates are recompiled.
//
//                 Upgrade data lives in the card JSON under an
//                 "upgrades" array. Each entry targets one tier
//                 (1/2/3) and lists field-path patches using dot
//                 notation with array indexing:
//
//                   "field": "effect.steps[0].amount"
//                   "field": "mana"
//                   "field": "rules_text"
//
// Layer:          Loader
// Collaborators:  PlayerDeckService.cs (calls Apply at run start),
//                 JsonCardLoader.cs (BuildCardPublic must be public),
//                 CardDatabase.cs (blueprint lookup),
//                 GuildSaveData / OwnedCard (UpgradeTier source),
// ============================================================

public static class CardUpgradeApplier
{
    private const string CARD_DIR = "res://Data/Cards/";

    // Cache handling for card lookup
    private static readonly Dictionary<string, string> _jsonCache = new();
    public static void ClearCache() => _jsonCache.Clear();

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Applies top-half upgrades up to topTier and bottom-half upgrades
    /// up to botTier independently. Tier 1 is always "both" and applied
    /// as a prerequisite before any half-specific upgrades.
    /// </summary>
    public static Card Apply(string blueprintId, int topTier, int botTier)
    {
        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, blueprintId, StringComparison.OrdinalIgnoreCase));
        if (bp == null) return null;
        if (topTier <= 0 && botTier <= 0) return CardDatabase.Instantiate(bp);

        string json = FindAndReadCardJson(bp);
        if (json == null) return CardDatabase.Instantiate(bp);

        JsonNode root;
        try { root = JsonNode.Parse(json); }
        catch { return CardDatabase.Instantiate(bp); }

        if (root["upgrades"] is not JsonArray upgrades)
            return CardDatabase.Instantiate(bp);

        string topName = null;
        string botName = null;

        // Always apply tier 1 "both" node first if either half is upgraded
        bool anyUpgraded = topTier >= 1 || botTier >= 1;
        if (anyUpgraded)
        {
            foreach (var entry in upgrades)
            {
                if (entry["tier"]?.GetValue<int>() != 1) continue;
                if (entry["half"]?.GetValue<string>() != "both") continue;
                ApplyChanges(root, entry);
                topName = entry["top_name"]?.GetValue<string>() ?? topName;
                botName = entry["bottom_name"]?.GetValue<string>() ?? botName;
                break;
            }
        }

        // Apply top-half nodes from tier 2 up to topTier
        for (int t = 2; t <= topTier; t++)
        {
            foreach (var entry in upgrades)
            {
                if (entry["tier"]?.GetValue<int>() != t) continue;
                if (entry["half"]?.GetValue<string>() != "top") continue;
                ApplyChanges(root, entry);
                topName = entry["top_name"]?.GetValue<string>() ?? topName;
                break;
            }
        }

        // Apply bottom-half nodes from tier 2 up to botTier
        for (int t = 2; t <= botTier; t++)
        {
            foreach (var entry in upgrades)
            {
                if (entry["tier"]?.GetValue<int>() != t) continue;
                if (entry["half"]?.GetValue<string>() != "bottom") continue;
                ApplyChanges(root, entry);
                botName = entry["bottom_name"]?.GetValue<string>() ?? botName;
                break;
            }
        }

        var rootElement = JsonDocument.Parse(root.ToJsonString()).RootElement;
        var upgraded = JsonCardLoader.BuildCardPublic(rootElement);
        if (upgraded == null) return CardDatabase.Instantiate(bp);

        // Apply names
        if (upgraded.TopHalf != null && !string.IsNullOrEmpty(topName))
            upgraded.TopHalf.Name = topName;
        if (upgraded.BottomHalf != null && !string.IsNullOrEmpty(botName))
            upgraded.BottomHalf.Name = botName;

        upgraded.Rarity = bp.Rarity;
        return upgraded;
    }

    private static void ApplyChanges(JsonNode root, JsonNode upgradeNode)
    {
        if (upgradeNode["changes"] is not JsonArray changes) return;
        foreach (var change in changes)
        {
            string half = change["half"]?.GetValue<string>();
            string field = change["field"]?.GetValue<string>();
            var value = change["value"];
            if (string.IsNullOrEmpty(half) || string.IsNullOrEmpty(field) || value == null)
                continue;
            var halfNode = root[half];
            if (halfNode == null) continue;
            ApplyPatch(halfNode, field, value);
        }
    }

    // Add helpers for the new per-half getters
    public static string GetTopUpgradeDescription(string blueprintId, int tier)
        => GetHalfUpgradeDescription(blueprintId, tier, "top");

    public static string GetBotUpgradeDescription(string blueprintId, int tier)
        => GetHalfUpgradeDescription(blueprintId, tier, "bottom");

    private static string GetHalfUpgradeDescription(string blueprintId,
        int tier, string half)
    {
        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, blueprintId, StringComparison.OrdinalIgnoreCase));
        if (bp == null) return "";
        string json = FindAndReadCardJson(bp);
        if (json == null) return "";
        try
        {
            var root = JsonNode.Parse(json);
            if (root["upgrades"] is not JsonArray upgrades) return "";
            foreach (var entry in upgrades)
            {
                if (entry["tier"]?.GetValue<int>() != tier) continue;
                string h = entry["half"]?.GetValue<string>() ?? "";
                if (h != half && h != "both") continue;
                return entry["description"]?.GetValue<string>() ?? "";
            }
        }
        catch { }
        return "";
    }

    public static string GetSharedUpgradeDescription(string blueprintId)
        => GetHalfUpgradeDescription(blueprintId, 1, "both");

    // ── Build upgraded card ─────────────────────────────────────────

    private static Card BuildUpgradedCard(CardBlueprint bp, int tier,
    string chosenBranch = null)
    {
        string json = FindAndReadCardJson(bp);
        if (json == null)
        {
            GD.PrintErr($"[CardUpgradeApplier] JSON not found for '{bp.Id}'. Using base.");
            return CardDatabase.Instantiate(bp);
        }

        JsonNode root;
        try { root = JsonNode.Parse(json); }
        catch (Exception e)
        {
            GD.PrintErr($"[CardUpgradeApplier] Parse error for '{bp.Id}': {e.Message}");
            return CardDatabase.Instantiate(bp);
        }

        var upgradeNode = FindUpgradeNode(root, tier, chosenBranch);
        if (upgradeNode == null)
        {
            GD.PrintErr($"[CardUpgradeApplier] No tier {tier} upgrade for '{bp.Id}'. Using base.");
            return CardDatabase.Instantiate(bp);
        }

        if (upgradeNode["changes"] is JsonArray changes)
        {
            foreach (var change in changes)
            {
                string half = change["half"]?.GetValue<string>();
                string field = change["field"]?.GetValue<string>();
                var value = change["value"];
                if (string.IsNullOrEmpty(half) || string.IsNullOrEmpty(field) || value == null)
                {
                    GD.PrintErr($"[CardUpgradeApplier] Malformed change in tier {tier} " +
                                $"for '{bp.Id}'");
                    continue;
                }
                var halfNode = root[half];
                if (halfNode == null)
                {
                    GD.PrintErr($"[CardUpgradeApplier] Half '{half}' not found in '{bp.Id}'");
                    continue;
                }
                ApplyPatch(halfNode, field, value);
            }
        }

        var rootElement = JsonDocument.Parse(root.ToJsonString()).RootElement;
        var upgraded = JsonCardLoader.BuildCardPublic(rootElement);

        if (upgraded == null)
        {
            GD.PrintErr($"[CardUpgradeApplier] BuildCard failed for '{bp.Id}' tier {tier}.");
            return CardDatabase.Instantiate(bp);
        }

        string topName = upgradeNode["top_name"]?.GetValue<string>();
        string botName = upgradeNode["bottom_name"]?.GetValue<string>();

        if (upgraded.TopHalf != null)
            upgraded.TopHalf.Name = !string.IsNullOrEmpty(topName)
                ? topName
                : upgraded.TopHalf.Name + (tier == 1 ? "+" : "");

        if (upgraded.BottomHalf != null)
            upgraded.BottomHalf.Name = !string.IsNullOrEmpty(botName)
                ? botName
                : upgraded.BottomHalf.Name + (tier == 1 ? "+" : "");
        upgraded.Rarity = bp.Rarity;

        GD.Print($"[CardUpgradeApplier] Applied tier {tier} " +
                 $"(branch: {chosenBranch ?? "none"}) to '{bp.Id}'.");
        return upgraded;
    }

    // ── JSON file finder ────────────────────────────────────────────

    private static string FindAndReadCardJson(CardBlueprint bp)
    {
        // Return cached result if available
        if (_jsonCache.TryGetValue(bp.Id, out var cached)) return cached;

        using var dir = DirAccess.Open(CARD_DIR);
        if (dir == null)
        {
            GD.PrintErr($"[CardUpgradeApplier] Could not open {CARD_DIR}");
            return null;
        }

        dir.ListDirBegin();
        string file;
        while ((file = dir.GetNext()) != "")
        {
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            string path = $"{CARD_DIR}{file}";
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f == null) continue;

            string json = f.GetAsText();
            try
            {
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("id", out var cardId)) continue;

                if (string.Equals(cardId.GetString(), bp.Id, StringComparison.OrdinalIgnoreCase))
                {
                    dir.ListDirEnd();
                    _jsonCache[bp.Id] = json;
                    return json;
                }
            }
            catch { continue; }
        }

        dir.ListDirEnd();
        GD.PrintErr($"[CardUpgradeApplier] No JSON found for blueprint '{bp.Id}'");
        _jsonCache[bp.Id] = null; // cache the miss too so we don't retry on every click
        return null;
    }

    // ── Upgrade node finder ─────────────────────────────────────────

    private static JsonNode FindUpgradeNode(JsonNode root, int tier, string chosenBranch = null)
    {
        if (root["upgrades"] is not JsonArray upgrades) return null;

        // For tier 1, branch is always null — return the first null-branch entry
        if (tier == 1)
        {
            foreach (var entry in upgrades)
                if (entry["tier"]?.GetValue<int>() == tier &&
                    entry["branch"]?.GetValue<string>() == null)
                    return entry;
            // Fallback: any tier 1 entry
            foreach (var entry in upgrades)
                if (entry["tier"]?.GetValue<int>() == tier)
                    return entry;
            return null;
        }

        // For tier 2+, match on both tier and branch
        if (!string.IsNullOrEmpty(chosenBranch))
        {
            foreach (var entry in upgrades)
            {
                if (entry["tier"]?.GetValue<int>() != tier) continue;
                var branch = entry["branch"]?.GetValue<string>();
                if (string.Equals(branch, chosenBranch, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }

        // Fallback: first entry matching tier (handles cards with no branching)
        foreach (var entry in upgrades)
            if (entry["tier"]?.GetValue<int>() == tier)
                return entry;

        return null;
    }

    // ── Field-path patcher ──────────────────────────────────────────

    private static void ApplyPatch(JsonNode node, string fieldPath, JsonNode value)
    {
        var parts = ParsePath(fieldPath);
        if (parts.Count == 0) return;

        JsonNode current = node;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            var (key, index) = parts[i];
            current = Descend(current, key, index);
            if (current == null)
            {
                GD.PrintErr($"[CardUpgradeApplier] Path '{fieldPath}' — segment '{key}' not found.");
                return;
            }
        }

        var (leafKey, leafIndex) = parts[^1];
        SetLeaf(current, leafKey, leafIndex, value);
    }

    private static JsonNode Descend(JsonNode node, string key, int index)
    {
        if (key != null)
        {
            if (node is JsonObject obj && obj.ContainsKey(key)) node = obj[key];
            else return null;
        }
        if (index >= 0)
        {
            if (node is JsonArray arr && index < arr.Count) node = arr[index];
            else return null;
        }
        return node;
    }

    private static void SetLeaf(JsonNode parent, string key, int index, JsonNode value)
    {
        if (key != null && parent is JsonObject parentObj)
        {
            if (index >= 0)
            {
                if (parentObj[key] is JsonArray arr && index < arr.Count)
                    arr[index] = value?.DeepClone();
            }
            else
            {
                parentObj[key] = value?.DeepClone();
            }
        }
        else if (key == null && index >= 0 && parent is JsonArray parentArr)
        {
            if (index < parentArr.Count)
                parentArr[index] = value?.DeepClone();
        }
    }

    private static List<(string key, int index)> ParsePath(string path)
    {
        var result = new List<(string, int)>();
        foreach (var seg in path.Split('.'))
        {
            int brack = seg.IndexOf('[');
            if (brack >= 0)
            {
                string keyPart = brack > 0 ? seg[..brack] : null;
                int idx = int.Parse(seg[(brack + 1)..seg.IndexOf(']')]);
                if (keyPart != null) result.Add((keyPart, -1));
                result.Add((null, idx));
            }
            else result.Add((seg, -1));
        }
        return result;
    }
}
