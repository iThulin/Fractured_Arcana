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

    /// <summary>Registers a factory that builds an <see cref="IEffect"/> from a JSON node. Keys are normalized to lowercase so JSON casing does not matter.</summary>
    public static void RegisterEffect(string key, Func<JsonElement, IEffect> factory)
        => _effects[key.ToLowerInvariant()] = factory;

    /// <summary>Registers a factory that builds an <see cref="IPredicate"/> from a JSON node. Keys are normalized to lowercase.</summary>
    public static void RegisterPredicate(string key, Func<JsonElement, IPredicate> factory)
        => _predicates[key.ToLowerInvariant()] = factory;

    /// <summary>Registers a factory that builds an <see cref="ITargetSelector"/> from a JSON node. Keys are normalized to lowercase.</summary>
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

        // Armor per target: { "type": "armor_per_target", "amount": n }
        RegisterEffect("armor_per_target", n =>
        {
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 2;
            return new ArmorPerTargetEffect(amount).WithTag("Defense");
        });

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

        // Mana per nearby element: { "type": "mana_per_nearby_element", "radius": n }
        RegisterEffect("mana_per_nearby_element", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3;
            return new ManaPerNearbyElementEffect(radius).WithTag("Mana");
        });

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

        // Imbue all tiles randomly: { "type": "imbue_all_tiles_random" }
        RegisterEffect("imbue_all_tiles_random", _ =>
            new ImbueAllTilesRandomEffect().WithTag("Terrain"));

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

        // Cleanse debuffs: { "type": "cleanse_debuffs" }
        RegisterEffect("cleanse_debuffs", _ =>
            new CleanseDebuffsEffect().WithTag("Utility"));

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

        // Pull: { "type": "pull", "tiles": n }
        RegisterEffect("pull", n =>
        {
            int tiles = n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 2;
            return new PullEffect(tiles).WithTag("Movement");
        });

        // Pull + damage: { "type": "pull_damage", "tiles": n, "damage_per_tile": m }
        RegisterEffect("pull_damage", n =>
        {
            int tiles = n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 2;
            int dmgPerTile = n.TryGetProperty("damage_per_tile", out var d) ? d.GetInt32() : 0;
            return new PullDamageEffect(tiles, dmgPerTile).WithTag("Movement");
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

        // Worldshaper: { "type": "worldshaper", "radius": n, "damage_per_tile": m, "elements": 1 }
        RegisterEffect("worldshaper", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3;
            int dmgPerTile = n.TryGetProperty("damage_per_tile", out var d) ? d.GetInt32() : 3;
            int elements = n.TryGetProperty("elements", out var e) ? e.GetInt32() : 1;
            return new WorldshaperEffect(radius, dmgPerTile, elements).WithTag("Terrain");
        });

        // ═══════════════════════════════════════════════════════════
        // NECROMANCER EFFECTS
        // ═══════════════════════════════════════════════════════════

        // Alias: gain_mana -> ManaGainEffect (cards use "gain_mana", registry has "mana_gain")
        RegisterEffect("gain_mana", n =>
            new GainManaEffect(n.GetProperty("amount").GetInt32()).WithTag("Mana"));

        // Alias: draw_if_memorial_passed -> DrawCardsEffect (conditional draw handled at runtime)
        RegisterEffect("draw_if_memorial_passed", n =>
            new DrawCardsEffect(n.TryGetProperty("count", out var c) ? c.GetInt32() : 1).WithTag("CardDraw"));

        RegisterEffect("draw_if_memorial_end", n =>
            new DrawCardsEffect(n.TryGetProperty("count", out var c) ? c.GetInt32() : 1).WithTag("CardDraw"));

        // Summon spirit on a memorial tile
        // { "type": "summon_spirit", "unit": "Spirit", "hp": 10, "damage": 5, "speed": 1 }
        RegisterEffect("summon_spirit", n =>
        {
            string unit = n.TryGetProperty("unit", out var u) ? u.GetString() : "Spirit";
            int hp      = n.TryGetProperty("hp", out var h) ? h.GetInt32() : 10;
            int damage  = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 5;
            int speed   = n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 1;
            bool onDeath = n.TryGetProperty("on_death_memorial", out var od) && od.GetBoolean();
            return new SummonSpiritEffect(unit, hp, damage, speed, onDeath).WithTag("Summon");
        });

        // Summon spirit from every memorial on the board
        // { "type": "summon_spirit_from_all_memorials", "unit": "Spirit", "hp": 10, ... }
        RegisterEffect("summon_spirit_from_all_memorials", n =>
        {
            string unit       = n.TryGetProperty("unit", out var u) ? u.GetString() : "Spirit";
            int baseHp        = n.TryGetProperty("hp", out var h) ? h.GetInt32() : 10;
            int damage        = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 5;
            int speed         = n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 1;
            bool hpPerSpirit  = n.TryGetProperty("hp_per_spirit", out var hps) && hps.GetBoolean();
            int advance       = n.TryGetProperty("on_arrive_advance", out var oa) ? oa.GetInt32() : 0;
            bool inheritName  = n.TryGetProperty("inherit_memorial_name", out var im) && im.GetBoolean();
            int bonusDmg      = n.TryGetProperty("bonus_damage_per_strength", out var bd) ? bd.GetInt32() : 0;
            return new SummonSpiritFromAllMemorialsEffect(unit, baseHp, damage, speed,
                hpPerSpirit, advance, inheritName, bonusDmg).WithTag("Summon");
        });

        // Create a memorial on target/caster tile
        // { "type": "create_memorial", "strength": "solid" }
        RegisterEffect("create_memorial", n =>
        {
            string strengthStr = n.TryGetProperty("strength", out var sv) ? sv.GetString() : "solid";
            var strength = strengthStr switch
            {
                "faint"  => MemorialStrength.Faint,
                "strong" => MemorialStrength.Strong,
                _        => MemorialStrength.Solid
            };
            return new CreateMemorialEffect(strength).WithTag("Terrain");
        });

        // Consume target memorial
        // { "type": "consume_memorial" }
        RegisterEffect("consume_memorial", _ =>
            new ConsumeMemorialEffect().WithTag("Terrain"));

        // Consume memorial or dismiss spirit on target tile
        // { "type": "consume_memorial_or_dismiss_spirit" }
        RegisterEffect("consume_memorial_or_dismiss_spirit", _ =>
            new ConsumeMemorialOrDismissSpiritEffect().WithTag("Terrain"));

        // Gain Grief charges
        // { "type": "gain_grief", "amount": n }
        RegisterEffect("gain_grief", n =>
            new GainGriefEffect(n.TryGetProperty("amount", out var a) ? a.GetInt32() : 1).WithTag("Grief"));

        // Advance all friendly spirits toward nearest enemy
        // { "type": "advance_all_spirits", "tiles": n, "attack_if_adjacent": true }
        RegisterEffect("advance_all_spirits", n =>
        {
            int tiles  = n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 1;
            bool atk   = !n.TryGetProperty("attack_if_adjacent", out var a) || a.GetBoolean();
            bool grant = n.TryGetProperty("grant_attack_if_reached", out var g) && g.GetBoolean();
            return new AdvanceAllSpiritsEffect(tiles, atk, grant).WithTag("Movement");
        });

        // Buff all friendly spirits with a temporary stat increase
        // { "type": "buff_all_spirits", "stat": "damage", "amount": n, "duration": 1 }
        RegisterEffect("buff_all_spirits", n =>
        {
            string stat = n.TryGetProperty("stat", out var s) ? s.GetString() : "damage";
            int amount  = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 2;
            int dur     = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
            return new BuffAllSpiritsEffect(stat, amount, dur).WithTag("Buff");
        });

        // Mark all spirits to create a memorial when they score a kill
        // { "type": "mark_spirits_memorial_on_kill" }
        RegisterEffect("mark_spirits_memorial_on_kill", _ =>
            new MarkSpiritsMemorialOnKillEffect().WithTag("Spirit"));

        // Gain armor equal to AmountPer × memorial count
        // { "type": "armor_per_memorial", "amount_per": n }
        RegisterEffect("armor_per_memorial", n =>
        {
            int amt = n.TryGetProperty("amount_per", out var a) ? a.GetInt32() : 1;
            return new ArmorPerMemorialEffect(amt).WithTag("Defense");
        });

        // Gain armor equal to AmountPer × Grief charges
        // { "type": "armor_per_grief", "amount_per": n }
        RegisterEffect("armor_per_grief", n =>
        {
            int amt = n.TryGetProperty("amount_per", out var a) ? a.GetInt32() : 1;
            return new ArmorPerGriefEffect(amt).WithTag("Defense");
        });

        // Heal caster for a fraction of damage dealt in the previous step
        // { "type": "heal_fraction_of_damage", "fraction": 0.5 }
        RegisterEffect("heal_fraction_of_damage", n =>
        {
            float frac = n.TryGetProperty("fraction", out var f) ? (float)f.GetDouble() : 0.5f;
            return new HealFractionOfDamageEffect(frac).WithTag("Heal");
        });

        // Deal damage + push all enemies near spirits/memorials
        // { "type": "dirge_pulse", "damage": n, "push": n }
        RegisterEffect("dirge_pulse", n =>
        {
            int dmg  = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 3;
            int push = n.TryGetProperty("push", out var p) ? p.GetInt32() : 1;
            int col  = n.TryGetProperty("collision_damage", out var c) ? c.GetInt32() : 0;
            return new DirgePulseEffect(dmg, push, col).WithTag("Damage");
        });

        // Hallow target tile
        // { "type": "hallow_tile", "duration": n, "auto_rise_range": n }
        RegisterEffect("hallow_tile", n =>
        {
            int dur   = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 99;
            int range = n.TryGetProperty("auto_rise_range", out var r) ? r.GetInt32() : 0;
            return new HallowTileEffect(dur, range).WithTag("Terrain");
        });

        // Hallow all tiles within radius of caster
        // { "type": "hallow_area", "radius": n }
        RegisterEffect("hallow_area", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 2;
            return new HallowAreaEffect(radius).WithTag("Terrain");
        });

        // Each memorial strikes adjacent enemies
        // { "type": "memorial_strike_all", "damage": n }
        RegisterEffect("memorial_strike_all", n =>
        {
            int dmg     = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 4;
            int push    = n.TryGetProperty("push", out var p) ? p.GetInt32() : 0;
            bool leave  = n.TryGetProperty("leave_memorial", out var l) && l.GetBoolean();
            int strikes = n.TryGetProperty("strikes", out var s) ? s.GetInt32() : 1;
            return new MemorialStrikeAllEffect(dmg, push, leave, strikes).WithTag("Damage");
        });

        // Consume memorials for champion summon (handled by SummonSpiritEffect on next step)
        // { "type": "consume_memorials_for_champion", "count": 2, "range": 3 }
        RegisterEffect("consume_memorials_for_champion", n =>
        {
            // For now, consume the nearest N memorials within range
            // The champion summon is a separate summon_spirit step
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 2;
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 3;
            return new ConsumeMemorialEffect().WithTag("Terrain"); // simplified: consumes target memorial
        });

        // Imbue target tile as Memorial Ground
        // { "type": "create_memorial_ground", "duration": n, "summon_discount": n }
        RegisterEffect("create_memorial_ground", n =>
        {
            int dur      = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 3;
            int discount = n.TryGetProperty("summon_discount", out var s) ? s.GetInt32() : 2;
            int regen    = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 0;
            return new CreateMemorialGroundEffect(dur, discount, regen).WithTag("Terrain");
        });

        // Spend Grief, deal damage per charge to all enemies
        // { "type": "grief_discharge_damage", "damage_per_grief": n }
        RegisterEffect("grief_discharge_damage", n =>
        {
            int dmgPer = n.TryGetProperty("damage_per_grief", out var d) ? d.GetInt32() : 3;
            return new GriefDischargeDamageEffect(dmgPer).WithTag("Damage");
        });

        // Apply status effect to all friendly spirits
        // { "type": "apply_status_to_all_spirits", "status": "undying_turn", "duration": 1 }
        RegisterEffect("apply_status_to_all_spirits", n =>
        {
            string status = n.TryGetProperty("status", out var sv) ? sv.GetString() : "undying_turn";
            int duration  = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
            int reviveHp  = n.TryGetProperty("revive_hp", out var r) ? r.GetInt32() : 8;
            return new ApplyStatusToAllSpiritsEffect(status, duration, reviveHp).WithTag("Spirit");
        });

        // Consume all memorials globally, gain mana/draw per memorial
        // { "type": "consume_all_memorials_global", "mana_per": n, "draw_per": n }
        RegisterEffect("consume_all_memorials_global", n =>
        {
            int mana = n.TryGetProperty("mana_per", out var m) ? m.GetInt32() : 0;
            int draw = n.TryGetProperty("draw_per", out var d) ? d.GetInt32() : 0;
            return new ConsumeAllMemorialsGlobalEffect(mana, draw).WithTag("Terrain");
        });

        // Deal damage × memorial count to all enemies
        // { "type": "damage_per_memorial_global", "damage_per": n }
        RegisterEffect("damage_per_memorial_global", n =>
        {
            int dmgPer = n.TryGetProperty("damage_per", out var d) ? d.GetInt32() : 3;
            return new DamagePerMemorialGlobalEffect(dmgPer).WithTag("Damage");
        });

        // Swap positions with a friendly spirit
        // { "type": "swap_with_spirit" }
        RegisterEffect("swap_with_spirit", _ =>
            new EmptyEffect().WithTag("Movement")); // placeholder — implement when movement system supports swap

        // Pull memorials together, merge overlapping pairs into Revenants
        // { "type": "pull_memorials_and_merge", "range": n, "merge_unit": "Revenant", ... }
        RegisterEffect("pull_memorials_and_merge", n =>
            new EmptyEffect().WithTag("Terrain")); // placeholder — complex spatial operation

        // Mark spirits to draw cards on kill
        RegisterEffect("mark_spirits_draw_on_kill", _ =>
            new EmptyEffect().WithTag("Spirit")); // placeholder

        // Shield per memorial
        RegisterEffect("shield_per_memorial", n =>
        {
            int amt = n.TryGetProperty("amount_per", out var a) ? a.GetInt32() : 1;
            return new EmptyEffect().WithTag("Defense"); // placeholder — add ShieldPerMemorialEffect later
        });

        // Consume all memorials in range
        RegisterEffect("consume_all_memorials_in_range", n =>
        {
            int mana = n.TryGetProperty("mana_per", out var m) ? m.GetInt32() : 0;
            int draw = n.TryGetProperty("draw_per", out var d) ? d.GetInt32() : 0;
            return new ConsumeAllMemorialsGlobalEffect(mana, draw).WithTag("Terrain");
        });

        // Trigger the Flood immediately
        RegisterEffect("trigger_flood", _ =>
            new EmptyEffect().WithTag("Grief")); // placeholder — wire to GriefAttunement.TriggerFlood

        // Set Grief to a specific value
        RegisterEffect("set_grief", n =>
        {
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 4;
            return new GainGriefEffect(amount).WithTag("Grief"); // simplified: GainGrief handles clamping
        });

        // Hollow Mantle: gain armor, create a protective barrier that absorbs damage for a few turns
        // { "type": "hollow_mantle", "turns": n, "armor": n }
        RegisterEffect("hollow_mantle", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            int armor = n.TryGetProperty("armor", out var a) ? a.GetInt32() : 11;
            return new HollowMantleLeafEffect(turns, armor).WithTag("Transform");
        });

        // Open Gate: deaths create memorials + summon spirits
        // { "type": "open_gate", "turns": n }
        RegisterEffect("open_gate", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new OpenGateLeafEffect(turns).WithTag("Persistent");
        });

        // Ossuary Aura: spirits within range regen HP per turn
        // { "type": "ossuary_aura", "spirit_regen": n, "spirit_regen_range": n }
        RegisterEffect("ossuary_aura", n =>
        {
            int turns  = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int regen  = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 2;
            int range  = n.TryGetProperty("spirit_regen_range", out var rr) ? rr.GetInt32() : 2;
            int mdr    = n.TryGetProperty("memorial_on_spirit_death_range", out var m) ? m.GetInt32() : 0;
            int arr    = n.TryGetProperty("auto_rise_range", out var ar) ? ar.GetInt32() : 0;
            int grief  = n.TryGetProperty("grief_per_turn", out var g) ? g.GetInt32() : 0;
            return new OssUaryAuraLeafEffect(turns, regen, range, mdr, arr, grief).WithTag("Persistent");
        });

        // Ossuary Shrine (spirit deaths near ossuary leave memorials)
        // { "type": "ossuary_aura_shrine", "spirit_regen": n, "spirit_regen_range": n, "memorial_on_spirit_death_range": n }
        RegisterEffect("ossuary_aura_shrine", n =>
        {
            int turns  = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int regen  = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 3;
            int range  = n.TryGetProperty("spirit_regen_range", out var rr) ? rr.GetInt32() : 2;
            int mdr    = n.TryGetProperty("memorial_on_spirit_death_range", out var m) ? m.GetInt32() : 2;
            return new OssUaryAuraLeafEffect(turns, regen, range, mdr).WithTag("Persistent");
        });

        // Ossuary Garden (auto-rise from adjacent memorials)
        // { "type": "ossuary_aura_garden", "spirit_regen": n, "spirit_regen_range": n, "memorial_on_spirit_death_range": n, "auto_rise_range": n }
        RegisterEffect("ossuary_aura_garden", n =>
        {
            int turns  = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int regen  = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 3;
            int range  = n.TryGetProperty("spirit_regen_range", out var rr) ? rr.GetInt32() : 2;
            int mdr    = n.TryGetProperty("memorial_on_spirit_death_range", out var m) ? m.GetInt32() : 2;
            int arr    = n.TryGetProperty("auto_rise_range", out var ar) ? ar.GetInt32() : 1;
            return new OssUaryAuraLeafEffect(turns, regen, range, mdr, arr).WithTag("Persistent");
        });

        // Soul Well (indestructible ossuary variant with grief per turn)
        // { "type": "soul_well_aura", "spirit_regen": n, "spirit_regen_range": n, "memorial_on_spirit_death_range": n, "auto_rise_range": n, "grief_per_turn": n }
        RegisterEffect("soul_well_aura", n =>
        {
            int regen  = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 3;
            int range  = n.TryGetProperty("spirit_regen_range", out var rr) ? rr.GetInt32() : 4;
            int mdr    = n.TryGetProperty("memorial_on_spirit_death_range", out var m) ? m.GetInt32() : 4;
            int arr    = n.TryGetProperty("auto_rise_range", out var ar) ? ar.GetInt32() : 2;
            int grief  = n.TryGetProperty("grief_per_turn", out var g) ? g.GetInt32() : 1;
            return new OssUaryAuraLeafEffect(99, regen, range, mdr, arr, grief).WithTag("Persistent");
        });

        // Memorial Seat Aura: spirits +2/+2, healing triggers twice
        // { "type": "memorial_seat_aura" }
        RegisterEffect("memorial_seat_aura", n =>
        {
            int turns  = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg    = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 2;
            int armor  = n.TryGetProperty("spirit_buff_armor", out var a) ? a.GetInt32() : 2;
            return new MemorialSeatAuraLeafEffect(turns, dmg, armor).WithTag("Persistent");
        });

        // Memorial Seat Aura (with healing)
        // { "type": "memorial_seat_aura_healing", "turns": n, "spirit_buff_damage": n, "spirit_buff_armor": n, "spirit_regen": n }
        RegisterEffect("memorial_seat_aura_healing", n =>
        {
            int turns  = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg    = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 2;
            int armor  = n.TryGetProperty("spirit_buff_armor", out var a) ? a.GetInt32() : 2;
            int regen  = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 2;
            return new MemorialSeatAuraLeafEffect(turns, dmg, armor, regenRange: 2, regen: regen).WithTag("Persistent");
        });

        // Memorial Seat Aura (with draw per turn)
        // { "type": "memorial_seat_aura_counsel", "turns": n, "spirit_buff_damage": n, "spirit_regen": n, "draw_per_turn": n }
        RegisterEffect("memorial_seat_aura_counsel", n =>
        {
            int turns  = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg    = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 2;
            int regen  = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 2;
            int draw   = n.TryGetProperty("draw_per_turn", out var dr) ? dr.GetInt32() : 1;
            return new MemorialSeatAuraLeafEffect(turns, dmg, 2, regenRange: 2, regen: regen, drawPerTurn: draw).WithTag("Persistent");
        });

        // Hallowed Double Rise: deaths on hallowed ground summon 2 spirits
        // { "type": "hallowed_double_rise" }
        RegisterEffect("hallowed_double_rise", n =>
            new HallowedDoubleRiseLeafEffect(false).WithTag("Persistent"));

        // Hallowed Double Rise (with spirit empowerment on kill)
        RegisterEffect("hallowed_double_rise_empower", n =>
            new HallowedDoubleRiseLeafEffect(true).WithTag("Persistent"));

        // Elder Aura: spirits within range gain bonus damage
        // { "type": "elder_aura", "spirit_buff_damage": n, "spirit_buff_range": n }
        RegisterEffect("elder_aura", n =>
        {
            int turns   = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg     = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 2;
            int range   = n.TryGetProperty("spirit_buff_range", out var r) ? r.GetInt32() : 3;
            bool prot   = n.TryGetProperty("protect_memorials", out var p) && p.GetBoolean();
            return new ElderAuraLeafEffect(turns, dmg, range, prot).WithTag("Persistent");
        });

        // Elder Aura Keeper (with memorial protection)
        // { "type": "elder_aura_keeper", "spirit_buff_damage": n, "spirit_buff_range": n }
        RegisterEffect("elder_aura_keeper", n =>
        {
            int turns   = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg     = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 3;
            int range   = n.TryGetProperty("spirit_buff_range", out var r) ? r.GetInt32() : 3;
            return new ElderAuraLeafEffect(turns, dmg, range, protectMemorials: true).WithTag("Persistent");
        });

        // Open Gate variants
        // Open Gate: deaths create memorials + summon spirits, but with different parameters or tags for specific cards
        RegisterEffect("open_gate_aura", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 5;
            return new OpenGateLeafEffect(turns).WithTag("Persistent");
        });

        // Open Gate (with summon discount instead of free summons)
        // { "type": "open_gate_aura_discount", "turns": n }
        RegisterEffect("open_gate_aura_discount", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 5;
            return new OpenGateLeafEffect(turns).WithTag("Persistent");
        });

        // Hollow Mantle variants
        // Hollow Mantle: gain armor, create a protective barrier that absorbs damage for a few turns, but with different parameters or tags for specific cards
        // { "type": "hollow_mantle_grief", "turns": n, "armor": n }
        RegisterEffect("hollow_mantle_grief", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 4;
            int armor = n.TryGetProperty("armor", out var a) ? a.GetInt32() : 14;
            return new HollowMantleLeafEffect(turns, armor).WithTag("Transform");
        });

        // Hollow Mantle + Draw: gain armor, create a protective barrier, and draw cards on damage taken
        // { "type": "hollow_mantle_grief_draw", "turns": n, "armor": n }
        RegisterEffect("hollow_mantle_grief_draw", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 4;
            int armor = n.TryGetProperty("armor", out var a) ? a.GetInt32() : 14;
            return new HollowMantleLeafEffect(turns, armor).WithTag("Transform");
        });

        // Walk Between 
        // { "type": "hollow_mantle_bonus_armor", "turns": n, "armor": n, "bonus_armor": n }
        RegisterEffect("walk_between", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2;
            int armor = n.TryGetProperty("armor", out var a) ? a.GetInt32() : 14;
            return new HollowMantleLeafEffect(turns, armor).WithTag("Transform");
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
            CardName = root.GetProperty("name").GetString() ?? "Unnamed",
            BlueprintId = root.TryGetProperty("id", out var idEl) 
                ? idEl.GetString() ?? "" 
                : ""  // fallback to empty — RegisterPrebuiltCard will warn
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

        if (halfNode.TryGetProperty("can_channel", out var cc))
            half.CanChannel = cc.GetBoolean();

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
