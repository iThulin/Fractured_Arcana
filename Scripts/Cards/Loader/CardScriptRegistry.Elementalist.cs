using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Elementalist.cs
//
// Purpose:        Elementalist school effect registrations — maps the
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
    }
}
