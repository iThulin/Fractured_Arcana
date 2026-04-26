using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// JSON Card Loader — PHASE 2 UPDATE
//
// New effect types registered:
//   mana_gain, self_damage, heal, imbue_tile, apply_status
//
// These make Elementalist cards functional for the test loop.
// ============================================================

public static class CardScriptRegistry
{
    private static readonly Dictionary<string, Func<JsonElement, IEffect>> _effects = new();
    private static readonly Dictionary<string, Func<JsonElement, IPredicate>> _predicates = new();
    private static readonly Dictionary<string, Func<JsonElement, ITargetSelector>> _targeters = new();

    public static void RegisterEffect(string key, Func<JsonElement, IEffect> factory)
        => _effects[key.ToLowerInvariant()] = factory;

    public static void RegisterPredicate(string key, Func<JsonElement, IPredicate> factory)
        => _predicates[key.ToLowerInvariant()] = factory;

    public static void RegisterTargeter(string key, Func<JsonElement, ITargetSelector> factory)
        => _targeters[key.ToLowerInvariant()] = factory;

    public static IEffect BuildEffect(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) return new EmptyEffect();
        var type = node.GetProperty("type").GetString()?.ToLowerInvariant();
        if (type == null || !_effects.TryGetValue(type, out var factory))
        {
            GD.PrintErr($"[CardLoader] Unknown effect type '{type}'. Using EmptyEffect.");
            return new EmptyEffect();
        }
        return factory(node);
    }

    public static IPredicate BuildPredicate(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) return new AlwaysTrue();
        var type = node.GetProperty("type").GetString()?.ToLowerInvariant();
        if (type == null || !_predicates.TryGetValue(type, out var factory))
        {
            GD.PrintErr($"[CardLoader] Unknown predicate type '{type}'. Defaulting to AlwaysTrue.");
            return new AlwaysTrue();
        }
        return factory(node);
    }

    public static ITargetSelector BuildTargeter(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) return null;
        var type = node.GetProperty("type").GetString()?.ToLowerInvariant();
        if (type == null || !_targeters.TryGetValue(type, out var factory))
        {
            GD.PrintErr($"[CardLoader] Unknown targeter type '{type}'. No targeting.");
            return null;
        }
        return factory(node);
    }

    public static void RegisterBuiltins()
    {
        // ═══════════════════════════════════════════════════════════
        // COMPOSITE EFFECTS
        // ═══════════════════════════════════════════════════════════

        RegisterEffect("sequence", n =>
        {
            var steps = new List<IEffect>();
            foreach (var step in n.GetProperty("steps").EnumerateArray())
                steps.Add(BuildEffect(step));
            return new SequenceEffect(steps.ToArray());
        });

        RegisterEffect("conditional", n =>
        {
            var pred = BuildPredicate(n.GetProperty("if"));
            var then = BuildEffect(n.GetProperty("then"));
            IEffect elseE = n.TryGetProperty("else", out var el) ? BuildEffect(el) : null;
            return new ConditionalEffect(pred, then, elseE);
        });

        RegisterEffect("for_each_target", n =>
            new ForEachTargetEffect(BuildEffect(n.GetProperty("do"))));

        RegisterEffect("empty", _ => new EmptyEffect());

        // ═══════════════════════════════════════════════════════════
        // CORE LEAF EFFECTS (all functional)
        // ═══════════════════════════════════════════════════════════

        RegisterEffect("damage", n =>
            new DealDamageEffect(n.GetProperty("amount").GetInt32()).WithTag("Damage"));

        RegisterEffect("move", n =>
            new DashEffect(n.GetProperty("tiles").GetInt32()).WithTag("Movement"));

        RegisterEffect("draw", n =>
            new DrawCardsEffect(n.GetProperty("count").GetInt32()).WithTag("CardDraw"));

        RegisterEffect("shield", n =>
            new GiveShieldEffect(n.GetProperty("amount").GetInt32()).WithTag("Defense"));

        RegisterEffect("summon", n =>
        {
            var kind = n.GetProperty("unit").GetString();
            var count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            return new SummonEffect(kind, count).WithTag("Summon");
        });

        // ═══════════════════════════════════════════════════════════
        // NEW EFFECTS — Phase 2 (Elementalist playability)
        // ═══════════════════════════════════════════════════════════

        // Mana gain: { "type": "mana_gain", "amount": 2 }
        RegisterEffect("mana_gain", n =>
            new ManaGainEffect(n.GetProperty("amount").GetInt32()).WithTag("Mana"));

        // Self damage: { "type": "self_damage", "amount": 2 }
        RegisterEffect("self_damage", n =>
            new SelfDamageEffect(n.GetProperty("amount").GetInt32()).WithTag("SelfDamage"));

        // Heal: { "type": "heal", "amount": 5 }
        RegisterEffect("heal", n =>
            new HealEffect(n.GetProperty("amount").GetInt32()).WithTag("Heal"));

        // Imbue tile with element: { "type": "imbue_tile", "element": "fire", "bonus_damage": 3 }
        RegisterEffect("imbue_tile", n =>
        {
            var element = n.GetProperty("element").GetString();
            var bonus = n.TryGetProperty("bonus_damage", out var bd) ? bd.GetInt32() : 0;
            return new ImbueTileEffect(element, bonus).WithTag("Terrain");
        });

        // Apply status: { "type": "apply_status", "status": "frozen", "duration": 1 }
        RegisterEffect("apply_status", n =>
        {
            var status = n.GetProperty("status").GetString();
            var duration = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
            return new ApplyStatusEffect(status, duration).WithTag("Status");
        });

        // ═══════════════════════════════════════════════════════════
        // PREDICATES
        // ═══════════════════════════════════════════════════════════

        RegisterPredicate("always_true", _ => new AlwaysTrue());
        RegisterPredicate("was_lethal", _ => new WasLethal());
        RegisterPredicate("target_on_tile", n =>
        {
            var tile = n.GetProperty("tile").GetString();
            return new TargetOnTile(tile);
        });

        // ═══════════════════════════════════════════════════════════
        // TARGETERS
        // ═══════════════════════════════════════════════════════════

        RegisterTargeter("self", _ => new SelectSelfTarget());
        RegisterTargeter("none", _ => new SelectGlobalTarget());

        RegisterTargeter("unit", n =>
        {
            bool enemyOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 6;
            bool los = n.TryGetProperty("los", out var l) && l.GetBoolean();
            return new SelectUnitTarget(enemyOnly, range, los);
        });

        RegisterTargeter("tile", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 4;
            return new SelectTileTarget(range);
        });

        RegisterTargeter("aoe", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 1;
            bool enemiesOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            return new SelectAreaTarget(radius, enemiesOnly, false);
        });

        RegisterTargeter("by_tag", n =>
        {
            var tag = n.GetProperty("tag").GetString();
            bool enemyOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            return new SelectByTagTarget(tag, enemyOnly);
        });
    }
}

// ============================================================
// JsonCardLoader — unchanged structure, included for completeness
// ============================================================

public static class JsonCardLoader
{
    public static List<Card> LoadAll(string directory)
    {
        var cards = new List<Card>();

        using var dir = DirAccess.Open(directory);
        if (dir == null)
        {
            GD.PrintErr($"[JsonCardLoader] Could not open directory: {directory}. " +
                         $"Error: {DirAccess.GetOpenError()}");
            return cards;
        }

        dir.ListDirBegin();
        string file;
        while ((file = dir.GetNext()) != "")
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string path = $"{directory}/{file}";
                string json = ReadGodotFile(path);
                if (json == null) continue;

                try
                {
                    var root = JsonDocument.Parse(json).RootElement;
                    var card = BuildCard(root);
                    if (card != null)
                        cards.Add(card);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[JsonCardLoader] Error parsing {file}: {ex.Message}");
                }
            }
        }
        dir.ListDirEnd();

        GD.Print($"[JsonCardLoader] Loaded {cards.Count} cards from {directory}");
        return cards;
    }

    private static string ReadGodotFile(string path)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PrintErr($"[JsonCardLoader] Cannot open file: {path}. " +
                        $"Error: {FileAccess.GetOpenError()}");
            return null;
        }
        return f.GetAsText();
    }

    private static Card BuildCard(JsonElement root)
    {
        var card = new Card
        {
            CardName = root.GetProperty("name").GetString() ?? "Unnamed"
        };

        if (root.TryGetProperty("rarity", out var r)
            && Enum.TryParse<CardRarity>(r.GetString(), true, out var rarity))
            card.Rarity = rarity;

        if (root.TryGetProperty("top", out var top))
            card.TopHalf = BuildHalf(top, card, root);

        if (root.TryGetProperty("bottom", out var bot))
            card.BottomHalf = BuildHalf(bot, card, root);

        return card;
    }

    private static CardHalf BuildHalf(JsonElement halfNode, Card owner, JsonElement root)
    {
        var school = CardSchool.Tinker;
        if (root.TryGetProperty("school", out var s)
            && Enum.TryParse<CardSchool>(s.GetString(), true, out var parsed))
            school = parsed;

        var half = new CardHalf
        {
            OwnerCard = owner,
            Name = halfNode.TryGetProperty("name", out var n) ? n.GetString() : owner.CardName,
            RulesText = halfNode.TryGetProperty("rules_text", out var rt) ? rt.GetString() : "",
            School = school,
            Speed = ParseSpeed(halfNode),
            Costs = new ICost[] { new ManaCost(halfNode.GetProperty("mana").GetInt32()) },
            Targeting = halfNode.TryGetProperty("targeting", out var t)
                ? CardScriptRegistry.BuildTargeter(t) : null,
            Effects = new[] { halfNode.TryGetProperty("effect", out var e)
                ? CardScriptRegistry.BuildEffect(e) : new EmptyEffect() }
        };

        // ── Parse element tags ──────────────────────────────────────
        if (halfNode.TryGetProperty("tags", out var tagsElement)
            && tagsElement.ValueKind == JsonValueKind.Array)
        {
            var tagList = new List<string>();
            foreach (var tagEl in tagsElement.EnumerateArray())
            {
                var tagStr = tagEl.GetString();
                if (!string.IsNullOrEmpty(tagStr))
                    tagList.Add(tagStr);
            }
            half.Tags = tagList.ToArray();
        }

        if (halfNode.TryGetProperty("channel", out var chan))
            half.ChannelVariant = BuildHalf(chan, owner, root);

        return half;
    }

    private static PlaySpeed ParseSpeed(JsonElement node)
    {
        if (node.TryGetProperty("speed", out var s)
            && Enum.TryParse<PlaySpeed>(s.GetString(), true, out var sp))
            return sp;
        return PlaySpeed.Sorcery;
    }
}
