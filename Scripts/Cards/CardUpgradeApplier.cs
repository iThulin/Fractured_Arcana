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
//                 CardUpgradeScreen.cs (calls GetUpgradeDescription)
// ============================================================

public static class CardUpgradeApplier
{
    private const string CARD_DIR = "res://Data/Cards/";

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="Card"/> with halves upgraded to
    /// <paramref name="tier"/>. At tier 0 returns the blueprint's
    /// base card. At tiers 1-3 re-parses and recompiles the JSON
    /// with patches applied.
    /// </summary>
    public static Card Apply(string blueprintId, int tier)
    {
        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, blueprintId, StringComparison.OrdinalIgnoreCase));
        if (bp == null) return null;

        if (tier <= 0) return CardDatabase.Instantiate(bp);

        return BuildUpgradedCard(bp, tier);
    }

    /// <summary>
    /// Returns the human-readable description for an upgrade tier
    /// without applying the patch. Used by the upgrade screen UI.
    /// </summary>
    public static string GetUpgradeDescription(string blueprintId, int tier)
    {
        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, blueprintId, StringComparison.OrdinalIgnoreCase));
        if (bp == null) return "";

        string json = FindAndReadCardJson(bp);
        if (json == null) return "";

        try
        {
            var root = JsonNode.Parse(json);
            var upgradeNode = FindUpgradeNode(root, tier);
            return upgradeNode?["description"]?.GetValue<string>() ?? "";
        }
        catch { return ""; }
    }

    // ── Build upgraded card ─────────────────────────────────────────

    private static Card BuildUpgradedCard(CardBlueprint bp, int tier)
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

        var upgradeNode = FindUpgradeNode(root, tier);
        if (upgradeNode == null)
        {
            GD.PrintErr($"[CardUpgradeApplier] No tier {tier} upgrade for '{bp.Id}'. Using base.");
            return CardDatabase.Instantiate(bp);
        }

        // Apply each change patch
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
                                $"for '{bp.Id}': missing half/field/value");
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

        // Rebuild card from patched JSON
        var rootElement = JsonDocument.Parse(root.ToJsonString()).RootElement;
        var upgraded = JsonCardLoader.BuildCardPublic(rootElement);

        if (upgraded == null)
        {
            GD.PrintErr($"[CardUpgradeApplier] BuildCard failed for '{bp.Id}' tier {tier}. " +
                        $"Falling back to base.");
            return CardDatabase.Instantiate(bp);
        }

        // Apply tier suffix to names
        string suffix = tier switch { 1 => "+", 2 => "++", 3 => "+++", _ => "" };
        if (upgraded.TopHalf != null) upgraded.TopHalf.Name += suffix;
        if (upgraded.BottomHalf != null) upgraded.BottomHalf.Name += suffix;
        upgraded.Rarity = bp.Rarity;

        GD.Print($"[CardUpgradeApplier] Applied tier {tier} to '{bp.Id}'.");
        return upgraded;
    }

    // ── JSON file finder ────────────────────────────────────────────

    private static string FindAndReadCardJson(CardBlueprint bp)
    {
        using var dir = DirAccess.Open(CARD_DIR);
        if (dir == null) return null;

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
                var root = doc.RootElement;

                if (!root.TryGetProperty("school", out var schoolEl)) continue;
                if (!root.TryGetProperty("top", out var topEl)) continue;
                if (!Enum.TryParse<CardSchool>(schoolEl.GetString(), true, out var school)) continue;

                string topName = topEl.TryGetProperty("name", out var tn) ? tn.GetString() : "";
                string botName = "";
                if (root.TryGetProperty("bottom", out var botEl) &&
                    botEl.TryGetProperty("name", out var bn))
                    botName = bn.GetString();

                string candidateId = $"{school}:{topName}|{botName}";
                if (string.Equals(candidateId, bp.Id, StringComparison.OrdinalIgnoreCase))
                {
                    dir.ListDirEnd();
                    return json;
                }
            }
            catch { continue; }
        }
        dir.ListDirEnd();
        return null;
    }

    // ── Upgrade node finder ─────────────────────────────────────────

    private static JsonNode FindUpgradeNode(JsonNode root, int tier)
    {
        if (root["upgrades"] is not JsonArray upgrades) return null;
        foreach (var entry in upgrades)
            if (entry["tier"]?.GetValue<int>() == tier) return entry;
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
