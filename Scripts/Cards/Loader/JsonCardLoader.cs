using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// JsonCardLoader.cs
//
// Purpose:        Parses card JSON from Data/Cards/ into runtime
//                 Card / CardHalf instances, and hosts the registry
//                 that maps JSON "type" strings to IEffect /
//                 IPredicate / ITargetSelector factories.
// Layer:          Loader
// Collaborators:  CardRuntime.cs (Card, CardHalf, PlaySpeed),
//                 ScriptingInterfaces.cs (IEffect, IPredicate, ITargetSelector),
//                 CardDatabase.cs (consumer of LoadAll),
//                 Schemas/card.schema.json (the JSON contract)
// See:            README §5 (Card Schema Reference),
//                 README §7 — "Effect Types Must Be Registered" gotcha,
//                 README §4.1 (Adding a Card)
// ============================================================
//
// Status gate (Phase 3): every card JSON declares a `status` field.
//   "ready" → always loaded into the CardDatabase
//   "wip"   → loaded only when devMode = true (passed to LoadAll)
//   "stub"  → never loaded
// Missing status is treated as "stub" with a printed warning, so old
// placeholder cards cannot sneak into a release build.

/// <summary>
/// Process-wide registry mapping JSON `type` keys to factory delegates that
/// construct the corresponding <see cref="IEffect"/>, <see cref="IPredicate"/>, or
/// <see cref="ITargetSelector"/>. Populate via <see cref="RegisterBuiltins"/> once
/// at startup; cards loaded by <see cref="JsonCardLoader"/> resolve their type strings
/// through these tables. Adding a new effect/predicate/targeter requires a
/// corresponding <c>Register*</c> call here — see README §7.
/// </summary>
public static class CardScriptRegistry
{
    private static readonly Dictionary<string, Func<JsonElement, IEffect>> _effects = new();
    private static readonly Dictionary<string, Func<JsonElement, IPredicate>> _predicates = new();
    private static readonly Dictionary<string, Func<JsonElement, ITargetSelector>> _targeters = new();

    /// <summary>Registers a factory that builds an <see cref="IEffect"/> from a JSON node. Keys are normalised to lowercase so JSON casing does not matter.</summary>
    public static void RegisterEffect(string key, Func<JsonElement, IEffect> factory)
        => _effects[key.ToLowerInvariant()] = factory;

    /// <summary>Registers a factory that builds an <see cref="IPredicate"/> from a JSON node. Keys are normalised to lowercase.</summary>
    public static void RegisterPredicate(string key, Func<JsonElement, IPredicate> factory)
        => _predicates[key.ToLowerInvariant()] = factory;

    /// <summary>Registers a factory that builds an <see cref="ITargetSelector"/> from a JSON node. Keys are normalised to lowercase.</summary>
    public static void RegisterTargeter(string key, Func<JsonElement, ITargetSelector> factory)
        => _targeters[key.ToLowerInvariant()] = factory;

    /// <summary>
    /// Resolves a JSON effect node to a concrete <see cref="IEffect"/>. Unknown or
    /// missing `type` values fall back to <see cref="EmptyEffect"/> with an error
    /// logged to the Godot console — cards never crash the loader, they just no-op.
    /// </summary>
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

    /// <summary>
    /// Resolves a JSON predicate node to a concrete <see cref="IPredicate"/>. Unknown
    /// or missing `type` values fall back to <see cref="AlwaysTrue"/> with an error
    /// logged — a missing predicate is safer than a hard failure.
    /// </summary>
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

    /// <summary>
    /// Resolves a JSON targeting node to a concrete <see cref="ITargetSelector"/>.
    /// Returns null (no targeting) for missing or unknown types — the caller is
    /// expected to handle a null targeter as "global / no target".
    /// </summary>
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

    /// <summary>
    /// Registers every built-in effect, predicate, and targeter factory. Call exactly
    /// once at startup before <see cref="JsonCardLoader.LoadAll"/> runs. When adding a
    /// new effect type, you must (a) implement the <see cref="IEffect"/> class,
    /// (b) add a <c>RegisterEffect</c> call here, and (c) add the type to
    /// <c>Schemas/card.schema.json</c>'s examples list. Skipping (b) is the most common
    /// "card silently no-ops" bug — see README §7.
    /// </summary>
    public static void RegisterBuiltins()
    {
        // ═══════════════════════════════════════════════════════════
        // COMPOSITE EFFECTS
        // ═══════════════════════════════════════════════════════════

        // Sequence:
        // { "type": "sequence", "steps": [ { ...effect... }, { ...effect... }, ... ] }
        RegisterEffect("sequence", n =>
        {
            var steps = new List<IEffect>();
            foreach (var step in n.GetProperty("steps").EnumerateArray())
                steps.Add(BuildEffect(step));
            return new SequenceEffect(steps.ToArray());
        });

        // Conditional:
        // { "type": "conditional", "if": { ...predicate... }, "then": { ...effect... }, "else": { ...effect... } }
        RegisterEffect("conditional", n =>
        {
            var pred = BuildPredicate(n.GetProperty("if"));
            var then = BuildEffect(n.GetProperty("then"));
            IEffect elseE = n.TryGetProperty("else", out var el) ? BuildEffect(el) : null;
            return new ConditionalEffect(pred, then, elseE);
        });

        // For each target in the current TargetSet, run the child effect with that single target
        // { "type": "for_each_target", "do": { ...effect... } }
        RegisterEffect("for_each_target", n =>
            new ForEachTargetEffect(BuildEffect(n.GetProperty("do"))));

        RegisterEffect("empty", _ => new EmptyEffect());

        // Retarget: run a new targeter mid-sequence, execute child effect
        // { "type": "retarget", "targeting": { ... }, "do": { ... } }
        RegisterEffect("retarget", n =>
        {
            var targeter = BuildTargeter(n.GetProperty("targeting"));
            var child = BuildEffect(n.GetProperty("do"));
            return new RetargetEffect(targeter, child);
        });

        // ═══════════════════════════════════════════════════════════
        // CORE LEAF EFFECTS
        // ═══════════════════════════════════════════════════════════

        // Damage: { "type": "damage", "amount": n }
        RegisterEffect("damage", n =>
            new DealDamageEffect(n.GetProperty("amount").GetInt32()).WithTag("Damage"));

        // Distance damage: { "type": "damage_by_distance", "min": n, "max": n, "per_tile": n }
        RegisterEffect("damage_by_distance", n =>
        {
            int min = n.TryGetProperty("min", out var mn) ? mn.GetInt32() : 1;
            int max = n.TryGetProperty("max", out var mx) ? mx.GetInt32() : 99;
            int perTile = n.TryGetProperty("per_tile", out var pt) ? pt.GetInt32() : 1;
            return new DistanceDamageEffect(min, max, perTile).WithTag("Damage");
        });

        // AoE all: { "type": "aoe_all", "radius": n, "damage": n }
        RegisterEffect("aoe_all", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3;
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 4;
            return new AoeAllEffect(radius, damage).WithTag("Damage");
        });

        // Move: { "type": "move", "tiles": n }
        RegisterEffect("move", n =>
            new DashEffect(n.GetProperty("tiles").GetInt32()).WithTag("Movement"));

        // Teleport: { "type": "teleport" }
        RegisterEffect("teleport", _ => new TeleportEffect().WithTag("Movement"));

        // Draw: { "type": "draw", "count": n }
        RegisterEffect("draw", n =>
            new DrawCardsEffect(n.GetProperty("count").GetInt32()).WithTag("CardDraw"));

        // Shield: { "type": "shield", "amount": n }
        RegisterEffect("shield", n =>
            new GiveShieldEffect(n.GetProperty("amount").GetInt32()).WithTag("Defense"));

        // Armor: { "type": "armor", "amount": n }
        RegisterEffect("armor", n =>
            new GiveArmorEffect(n.GetProperty("amount").GetInt32()).WithTag("Defense"));

        // Grant armor to target: { "type": "grant_armor", "amount": n }
        RegisterEffect("grant_armor", n =>
            new GiveTargetArmorEffect(n.GetProperty("amount").GetInt32()).WithTag("Defense"));

        // Summon: { "type": "summon", "unit": "kind", "count": n }
        RegisterEffect("summon", n =>
        {
            var kind = n.GetProperty("unit").GetString();
            var count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            return new SummonEffect(kind, count).WithTag("Summon");
        });

        // Create rubble: { "type": "create_rubble" }
        RegisterEffect("create_rubble", _ => new CreateRubbleEffect().WithTag("Terrain"));

        // Raise terrain: { "type": "raise_terrain", "height": n }
        RegisterEffect("raise_terrain", n =>
        {
            int height = n.TryGetProperty("height", out var h) ? h.GetInt32() : 1;
            return new RaiseTerrainEffect(height).WithTag("Terrain");
        });

        // Mana gain: { "type": "mana_gain", "amount": n }
        RegisterEffect("mana_gain", n =>
            new ManaGainEffect(n.GetProperty("amount").GetInt32()).WithTag("Mana"));

        // Self damage: { "type": "self_damage", "amount": n }
        RegisterEffect("self_damage", n =>
            new SelfDamageEffect(n.GetProperty("amount").GetInt32()).WithTag("SelfDamage"));

        // Heal: { "type": "heal", "amount": n }
        RegisterEffect("heal", n =>
            new HealEffect(n.GetProperty("amount").GetInt32()).WithTag("Heal"));

        // Imbue tile: { "type": "imbue_tile", "element": "fire", "bonus_damage": n }
        RegisterEffect("imbue_tile", n =>
        {
            var element = n.GetProperty("element").GetString();
            var bonus = n.TryGetProperty("bonus_damage", out var bd) ? bd.GetInt32() : 0;
            return new ImbueTileEffect(element, bonus).WithTag("Terrain");
        });

        // Imbue area: { "type": "imbue_area", "element": "fire", "radius": n }
        RegisterEffect("imbue_area", n =>
        {
            var element = n.GetProperty("element").GetString();
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 2;
            return new ImbueAreaEffect(element, radius).WithTag("Terrain");
        });

        // Place glyph: { "type": "place_glyph", "damage": n, "status": "slowed", "duration": n }
        RegisterEffect("place_glyph", n =>
        {
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 3;
            string status = n.TryGetProperty("status", out var sv) ? sv.GetString() : null;
            int duration = n.TryGetProperty("duration", out var dur) ? dur.GetInt32() : 1;
            return new PlaceGlyphEffect(damage, status, duration).WithTag("Terrain");
        });

        // Apply status: { "type": "apply_status", "status": "frozen", "duration": n }
        RegisterEffect("apply_status", n =>
        {
            var status = n.GetProperty("status").GetString();
            var duration = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
            return new ApplyStatusEffect(status, duration).WithTag("Status");
        });

        // Push: { "type": "push", "tiles": n, "collision_damage": m }
        RegisterEffect("push", n =>
        {
            int tiles = n.GetProperty("tiles").GetInt32();
            int collisionDmg = n.TryGetProperty("collision_damage", out var cd) ? cd.GetInt32() : 0;
            return new PushEffect(tiles, collisionDmg).WithTag("Movement");
        });

        // Push + damage: { "type": "push_damage", "tiles": n, "damage_per_tile": m }
        RegisterEffect("push_damage", n =>
        {
            int tiles = n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 1;
            int dmgPerTile = n.TryGetProperty("damage_per_tile", out var d) ? d.GetInt32() : 0;
            return new PushDamageEffect(tiles, dmgPerTile).WithTag("Movement");
        });

        // Imbue + move: { "type": "imbue_path", "element": "ice", "move": n, "armor_per_tile": m }
        RegisterEffect("imbue_path", n =>
        {
            var element = n.GetProperty("element").GetString();
            int moveTiles = n.TryGetProperty("move", out var m) ? m.GetInt32() : 0;
            int armorPerTile = n.TryGetProperty("armor_per_tile", out var a) ? a.GetInt32() : 0;
            return new ImbuePathEffect(element, moveTiles, armorPerTile);
        });

        // Remove armor: { "type": "remove_armor", "amount": n }
        RegisterEffect("remove_armor", n =>
        {
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 0;
            return new RemoveArmorEffect(amount).WithTag("Debuff");
        });

        // Remove status: { "type": "remove_status" } or { "type": "remove_status", "status": "frozen" }
        RegisterEffect("remove_status", n =>
        {
            string status = n.TryGetProperty("status", out var sv) ? sv.GetString() : null;
            return new RemoveStatusEffect(status).WithTag("Utility");
        });

        // Consume element tile: { "type": "consume_element_tile", "element": "fire", "radius": n, "damage": m }
        RegisterEffect("consume_element_tile", n =>
        {
            var element = n.GetProperty("element").GetString();
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 2;
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 7;
            return new ConsumeElementTileEffect(element, radius, damage).WithTag("Terrain");
        });

        // Damage by hand size: { "type": "damage_by_hand_size", "multiplier": n }
        RegisterEffect("damage_by_hand_size", n =>
        {
            int mult = n.TryGetProperty("multiplier", out var m) ? m.GetInt32() : 2;
            return new DamageByHandSizeEffect(mult).WithTag("Damage");
        });

        // ═══════════════════════════════════════════════════════════
        // ELEMENTALIST-SPECIFIC EFFECTS
        // ═══════════════════════════════════════════════════════════

        // Terraform: { "type": "terraform", "radius": n, "damage": m }
        RegisterEffect("terraform", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3;
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 6;
            return new TerraformEffect(radius, damage).WithTag("Terrain");
        });

        // Elemental Convergence: { "type": "elemental_convergence", "radius": n, "attunement_set_to": m }
        RegisterEffect("elemental_convergence", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3;
            int attSet = n.TryGetProperty("attunement_set_to", out var a) ? a.GetInt32() : 3;
            return new ElementalConvergenceEffect(radius, attSet).WithTag("Terrain");
        });

        // Ragnarok: { "type": "ragnarok", "damage_per_element": n, "half_to_allies": bool }
        RegisterEffect("ragnarok", n =>
        {
            int dmgPer = n.TryGetProperty("damage_per_element", out var d) ? d.GetInt32() : 7;
            bool half = n.TryGetProperty("half_to_allies", out var h) && h.GetBoolean();
            return new RagnarokEffect(dmgPer, half).WithTag("Damage");
        });

        // Cataclysm: { "type": "cataclysm", "radius": n, "damage_per_tile": m, "tiles_per_draw": t }
        RegisterEffect("cataclysm", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 4;
            int dmg = n.TryGetProperty("damage_per_tile", out var d) ? d.GetInt32() : 2;
            int draw = n.TryGetProperty("tiles_per_draw", out var td) ? td.GetInt32() : 3;
            return new CataclysmEffect(radius, dmg, draw).WithTag("Terrain");
        });

        // Primordial Surge: { "type": "primordial_surge", "radius": n }
        RegisterEffect("primordial_surge", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 4;
            return new PrimordialSurgeEffect(radius).WithTag("Terrain");
        });

        // Tectonic Shatter: { "type": "tectonic_shatter", "radius": n, "damage_per_tile": m }
        RegisterEffect("tectonic_shatter", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3;
            int dmg = n.TryGetProperty("damage_per_tile", out var d) ? d.GetInt32() : 5;
            return new TectonicShatterEffect(radius, dmg).WithTag("Terrain");
        });

        // Avatar Transform: { "type": "avatar_transform", "turns": n, "bonus_damage": m, "armor": a, "bonus_speed": s }
        RegisterEffect("avatar_transform", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            int bonus = n.TryGetProperty("bonus_damage", out var b) ? b.GetInt32() : 3;
            int armor = n.TryGetProperty("armor", out var a) ? a.GetInt32() : 7;
            int speed = n.TryGetProperty("bonus_speed", out var sp) ? sp.GetInt32() : 0;
            return new AvatarTransformEffect(turns, bonus, armor, speed).WithTag("Transform");
        });

        // Create Maelstrom: { "type": "create_maelstrom", "radius": n, "damage": m, "turns": t, "freezes": bool }
        RegisterEffect("create_maelstrom", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3;
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 2;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            bool freezes = n.TryGetProperty("freezes", out var f) && f.GetBoolean();
            return new CreateMaelstromEffect(radius, damage, turns, freezes).WithTag("Terrain");
        });

        // ═══════════════════════════════════════════════════════════
        // PREDICATES
        // ═══════════════════════════════════════════════════════════

        RegisterPredicate("always_true", _ => new AlwaysTrue());
        RegisterPredicate("was_lethal", _ => new LastEffectWasLethal());

        // Target on tile: { "type": "target_on_tile", "tile": "ice" }
        RegisterPredicate("target_on_tile", n =>
        {
            var tile = n.GetProperty("tile").GetString();
            return new TargetOnTile(tile);
        });

        // Target adjacent to tile: { "type": "target_adjacent_to_tile", "tile": "fire" }
        RegisterPredicate("target_adjacent_to_tile", n =>
        {
            var tile = n.GetProperty("tile").GetString();
            return new TargetAdjacentToTile(tile);
        });

        // Target adjacent to caster: { "type": "target_adjacent_to_caster" }
        RegisterPredicate("target_adjacent_to_caster", _ => new TargetAdjacentToCaster());

        // Caster standing on terrain: { "type": "caster_on_terrain", "terrain": "stone" }
        RegisterPredicate("caster_on_terrain", n =>
        {
            var terrain = n.GetProperty("terrain").GetString();
            return new CasterOnTerrain(terrain);
        });

        // Caster has elements nearby: { "type": "has_elements_near_caster", "elements": ["fire","ice"], "range": n }
        RegisterPredicate("has_elements_near_caster", n =>
        {
            var elements = new List<string>();
            if (n.TryGetProperty("elements", out var arr))
                foreach (var e in arr.EnumerateArray())
                    elements.Add(e.GetString());
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 2;
            return new HasElementsNearCaster(elements.ToArray(), range);
        });

        // ═══════════════════════════════════════════════════════════
        // TARGETERS
        // ═══════════════════════════════════════════════════════════

        RegisterTargeter("self", _ => new SelectSelfTarget());
        RegisterTargeter("none", _ => new SelectGlobalTarget());

        // Unit selector: { "type": "unit", "enemies_only": bool, "range": n, "los": bool }
        RegisterTargeter("unit", n =>
        {
            bool enemyOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 6;
            bool los = n.TryGetProperty("los", out var l) && l.GetBoolean();
            return new SelectUnitTarget(enemyOnly, range, los);
        });

        // Tile selector: { "type": "tile", "range": n }
        RegisterTargeter("tile", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 4;
            return new SelectTileTarget(range);
        });

        // AoE selector: { "type": "aoe", "radius": n, "enemies_only": bool, "include_tiles": bool }
        RegisterTargeter("aoe", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 1;
            bool enemiesOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            bool includeTiles = n.TryGetProperty("include_tiles", out var it) && it.GetBoolean();
            return new SelectAreaTarget(radius, enemiesOnly, includeTiles);
        });

        // Cone selector: { "type": "cone", "range": n, "enemies_only": bool }
        RegisterTargeter("cone", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 3;
            bool enemiesOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            return new SelectConeTarget(range, enemiesOnly);
        });

        // Ring selector: { "type": "ring", "radius": n, "include_tiles": bool }
        RegisterTargeter("ring", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 2;
            bool includeTiles = n.TryGetProperty("include_tiles", out var it) ? it.GetBoolean() : true;
            return new SelectRingTarget(radius, includeTiles);
        });

        // By tag selector: { "type": "by_tag", "tag": "fire", "enemies_only": bool }
        RegisterTargeter("by_tag", n =>
        {
            var tag = n.GetProperty("tag").GetString();
            bool enemyOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            return new SelectByTagTarget(tag, enemyOnly);
        });

        // Nearest to target selector: { "type": "nearest_to_target", "range": n }
        RegisterTargeter("nearest_to_target", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 3;
            return new SelectNearestToTarget(range);
        });

        // Line selector: { "type": "line", "length": n, "enemies_only": bool, "include_tiles": bool }
        RegisterTargeter("line", n =>
        {
            int length = n.TryGetProperty("length", out var l) ? l.GetInt32() : 2;
            bool enemiesOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            bool includeTiles = n.TryGetProperty("include_tiles", out var it) && it.GetBoolean();
            return new SelectLineTarget(length, enemiesOnly, includeTiles);
        });

        // Adjacent to target selector: { "type": "adjacent_to_target", "include_tiles": bool }
        RegisterTargeter("adjacent_to_target", n =>
        {
            bool includeTiles = n.TryGetProperty("include_tiles", out var it) && it.GetBoolean();
            return new SelectAdjacentToTarget(includeTiles);
        });

        // Element tile selector: { "type": "element_tile", "element": "fire", "range": n }
        RegisterTargeter("element_tile", n =>
        {
            var element = n.GetProperty("element").GetString();
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 6;
            return new SelectElementTileTarget(element, range);
        });

        // Empty tile in range: { "type": "empty_tile", "range": n }
        RegisterTargeter("empty_tile", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 3;
            return new SelectEmptyTileTarget(range);
        });
    }
}

// ── JsonCardLoader ───────────────────────────────────────────────────

/// <summary>
/// Scans a directory of card JSON files and returns the runtime
/// <see cref="Card"/> instances that pass the status gate. The loader is
/// crash-tolerant: malformed files log an error and are skipped, so a single
/// bad card never blocks the rest of the database from loading. Always call
/// <see cref="CardScriptRegistry.RegisterBuiltins"/> before invoking this.
/// </summary>
public static class JsonCardLoader
{
    // ── Status constants ────────────────────────────────────────────
    private const string STATUS_READY = "ready";
    private const string STATUS_WIP = "wip";
    private const string STATUS_STUB = "stub";

    // ── LoadAll ─────────────────────────────────────────────────────

    /// <summary>
    /// Loads every <c>*.json</c> card from <paramref name="directory"/> that passes the
    /// status gate. "ready" cards always load; "wip" cards load only when
    /// <paramref name="devMode"/> is true; "stub" (or missing status) cards are skipped.
    /// Counts of skipped stubs and wip cards are written to the Godot console.
    /// </summary>
    /// <param name="directory">Godot resource-path style directory, e.g. "res://Data/Cards".</param>
    /// <param name="devMode">When true, "wip" cards are loaded alongside "ready" cards. Off in shipping builds.</param>
    /// <returns>The list of successfully built cards. Never null; may be empty if the directory is missing.</returns>
    public static List<Card> LoadAll(string directory, bool devMode = false)
    {
        var cards = new List<Card>();
        int skipped = 0;
        int stubs = 0;

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
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            string path = $"{directory}/{file}";
            string json = ReadGodotFile(path);
            if (json == null) continue;

            try
            {
                var root = JsonDocument.Parse(json).RootElement;
                var status = GetStatus(root, file);

                switch (status)
                {
                    case STATUS_STUB:
                        stubs++;
                        GD.Print($"[JsonCardLoader] Skipping stub: {file}");
                        continue;

                    case STATUS_WIP:
                        if (!devMode)
                        {
                            skipped++;
                            GD.Print($"[JsonCardLoader] Skipping wip (DevMode off): {file}");
                            continue;
                        }
                        GD.Print($"[JsonCardLoader] Loading wip card (DevMode on): {file}");
                        break;

                    case STATUS_READY:
                        break;

                    default:
                        stubs++;
                        GD.PrintErr($"[JsonCardLoader] Unknown status '{status}' in {file}. " +
                                    $"Treating as stub. Valid values: ready, wip, stub.");
                        continue;
                }

                var card = BuildCard(root);
                if (card != null)
                    cards.Add(card);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[JsonCardLoader] Error parsing {file}: {ex.Message}");
            }
        }
        dir.ListDirEnd();

        GD.Print($"[JsonCardLoader] Loaded {cards.Count} cards from {directory} " +
                 $"({stubs} stubs skipped, {skipped} wip skipped)");
        return cards;
    }

    // ── Status helper ───────────────────────────────────────────────
    private static string GetStatus(JsonElement root, string filename)
    {
        if (root.TryGetProperty("status", out var s))
            return s.GetString()?.ToLowerInvariant() ?? STATUS_STUB;

        GD.PrintErr($"[JsonCardLoader] '{filename}' has no 'status' field. " +
                    $"Treating as stub. Add \"status\": \"stub\" to silence this warning, " +
                    $"or \"status\": \"ready\" when the card is complete.");
        return STATUS_STUB;
    }

    // ── File reader ─────────────────────────────────────────────────
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

    // ── BuildCard ───────────────────────────────────────────────────
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

    // ── BuildHalf ───────────────────────────────────────────────────
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

        if (halfNode.TryGetProperty("requires", out var reqElement)
            && reqElement.ValueKind == JsonValueKind.Array)
        {
            var reqList = new List<string>();
            foreach (var r2 in reqElement.EnumerateArray())
            {
                var rs = r2.GetString();
                if (!string.IsNullOrEmpty(rs)) reqList.Add(rs);
            }
            half.Requirements = reqList.ToArray();
        }

        if (halfNode.TryGetProperty("channel", out var chan))
            half.ChannelVariant = BuildHalf(chan, owner, root);

        return half;
    }

    // ── ParseSpeed ──────────────────────────────────────────────────
    private static PlaySpeed ParseSpeed(JsonElement node)
    {
        if (node.TryGetProperty("speed", out var s)
            && Enum.TryParse<PlaySpeed>(s.GetString(), true, out var sp))
            return sp;
        return PlaySpeed.Sorcery;
    }

    /// <summary>Public entry point for CardUpgradeApplier to recompile patched JSON.</summary>
    public static Card BuildCardPublic(JsonElement root) => BuildCard(root);
}
