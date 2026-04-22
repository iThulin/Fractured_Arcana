using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// JSON Card Loader.
//
// Uses Godot's DirAccess + FileAccess so res:// paths work both
// in-editor and in exported builds. Raw System.IO calls DO NOT
// understand res:// and will fail in builds even if they work
// in-editor on some platforms.
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
        // --- Composite effects ---
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

        // --- Leaf effects ---
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

        // --- Predicates ---
        RegisterPredicate("always", _ => new AlwaysTrue());
        RegisterPredicate("and", n =>
        {
            var parts = new List<IPredicate>();
            foreach (var p in n.GetProperty("parts").EnumerateArray())
                parts.Add(BuildPredicate(p));
            return new AndPredicate(parts.ToArray());
        });
        RegisterPredicate("or", n =>
        {
            var parts = new List<IPredicate>();
            foreach (var p in n.GetProperty("parts").EnumerateArray())
                parts.Add(BuildPredicate(p));
            return new OrPredicate(parts.ToArray());
        });
        RegisterPredicate("not", n => new NotPredicate(BuildPredicate(n.GetProperty("inner"))));
        RegisterPredicate("was_lethal", _ => new LastEffectWasLethal());
        RegisterPredicate("target_adjacent_to_tile", n =>
            new TargetAdjacentToTile(n.GetProperty("tile").GetString()));
        RegisterPredicate("target_on_tile", n =>
            new TargetOnTile(n.GetProperty("tile").GetString()));
        RegisterPredicate("count_of_tile_at_least", n =>
            new CountOfTileAtLeast(
                n.GetProperty("tile").GetString(),
                n.GetProperty("at_least").GetInt32()));
        RegisterPredicate("is_channeled", _ => new IsChanneled());

        // --- Targeters ---
        RegisterTargeter("unit", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 6;
            bool los = n.TryGetProperty("los", out var l) && l.GetBoolean();
            bool enemiesOnly = !n.TryGetProperty("enemies_only", out var e) || e.GetBoolean();
            return new SelectUnitTarget(enemiesOnly, range, los, false);
        });

        RegisterTargeter("tile", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 4;
            return new SelectTileTarget(range);
        });

        RegisterTargeter("self", _ => new SelectSelfTarget());

        RegisterTargeter("aoe", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 1;
            bool enemiesOnly = n.TryGetProperty("enemies_only", out var e) && e.GetBoolean();
            bool tiles = n.TryGetProperty("tiles", out var t) && t.GetBoolean();
            return new SelectAreaTarget(radius, enemiesOnly, tiles);
        });

        RegisterTargeter("none", _ => null);
    }
}

public static class JsonCardLoader
{
    public static List<Card> LoadAll(string directory)
    {
        var cards = new List<Card>();

        // Godot's DirAccess understands res:// — System.IO does not.
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
