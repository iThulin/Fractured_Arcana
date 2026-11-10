using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Elementalist.cs
//
// Purpose:        Elementalist school effect registrations. Maps the
//                 school's JSON `type` keys to effect factories.
//                 Called from CardScriptRegistry.RegisterBuiltins().
// Layer:          Loader
// Collaborators:  ElementalistEffects.cs (the effect classes),
//                 JsonCardLoader.cs (registry infrastructure)
// ============================================================

public static partial class CardScriptRegistry
{
    /// <summary>Registers all Elementalist-school effect factories.</summary>
    private static void RegisterElementalistEffects()
    {
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

        // Elemental Sight (Worldshaper tiers 3-4): charge per distinct nearby element.
        // { "type": "attunement_per_nearby_element", "radius": 3 }
        RegisterEffect("attunement_per_nearby_element", n =>
            new AttunementPerNearbyElementEffect(
                n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3).WithTag("Attunement"));

        // Supercooled (Firestorm tier 3): bonus damage against already-frozen targets.
        // { "type": "target_has_status", "status": "frozen" }
        RegisterPredicate("target_has_status", n =>
            new TargetHasStatusPredicate(
                n.TryGetProperty("status", out var st) ? st.GetString() : ""));

        // ── Slice 15a: the Weaver, the Geomancer, Threefold Argument ─────
        int I(System.Text.Json.JsonElement n, string k, int d) => n.TryGetProperty(k, out var v) ? v.GetInt32() : d;
        string S(System.Text.Json.JsonElement n, string k, string d) => n.TryGetProperty(k, out var v) ? v.GetString() : d;
        bool B(System.Text.Json.JsonElement n, string k) => n.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
        // { "type": "gain_attunement", "element": "fire"|"ice"|"storm"|"earth"|"highest"|"all", "amount": n }
        RegisterEffect("gain_attunement", n => new GainAttunementEffect
            { Element = S(n, "element", "highest"), Amount = I(n, "amount", 1) }.WithTag("Attunement"));
        // { "type": "imbue_highest" }
        RegisterEffect("imbue_highest", _ => new ImbueHighestEffect().WithTag("Terrain"));
        // { "type": "adjust_height", "amount": n | "lower": n, "blocks_los": bool, "max": n }
        RegisterEffect("adjust_height", n => new AdjustHeightEffect
            { Amount = n.TryGetProperty("lower", out var lw) ? -lw.GetInt32() : I(n, "amount", 1), BlocksLos = B(n, "blocks_los"), Max = I(n, "max", AdjustHeightEffect.MaxHeightCap) }.WithTag("Terrain"));
        // { "type": "imbue_reacting", "element": "storm", "radius": n, "reaction_damage": n }
        RegisterEffect("imbue_reacting", n => new ImbueReactingEffect
            { Element = S(n, "element", "storm"), Radius = I(n, "radius", 1), ReactionDamage = I(n, "reaction_damage", 3) }.WithTag("Terrain"));
        // { "type": "draw_per_reaction", "max": n, "per": n }
        RegisterEffect("draw_per_reaction", n => new DrawPerReactionEffect
            { Max = I(n, "max", 3), Per = I(n, "per", 1) }.WithTag("CardDraw"));
        // { "type": "alchemy_of_ruin", "radius": n, "damage": n }
        RegisterEffect("alchemy_of_ruin", n => new AlchemyOfRuinEffect
            { Radius = I(n, "radius", 3), Damage = I(n, "damage", 0) }.WithTag("Terrain"));
        // { "type": "clear_reactions", "radius": n, "attunement_per": n }
        RegisterEffect("clear_reactions", n => new ClearReactionsEffect
            { Radius = I(n, "radius", 3), AttunementPer = I(n, "attunement_per", 1) }.WithTag("Attunement"));
        // { "type": "long_argument", "radius": n, "damage": n, "attunement_per": n }
        RegisterEffect("long_argument", n => new LongArgumentEffect
            { Radius = I(n, "radius", 4), Damage = I(n, "damage", 3), AttunementPer = I(n, "attunement_per", 1) }.WithTag("Buff"));
        // { "type": "imbue_opposed_near_enemies" }
        RegisterEffect("imbue_opposed_near_enemies", _ => new ImbueOpposedNearEnemiesEffect().WithTag("Terrain"));
        // { "type": "target_frozen_or_brittle" }
        RegisterPredicate("target_frozen_or_brittle", _ => new TargetFrozenOrBrittlePredicate());
    }
}
