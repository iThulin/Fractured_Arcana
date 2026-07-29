using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Druid.cs
//
// Purpose:        Druid school effect registrations — maps the
//                 school's JSON `type` keys to effect factories.
//                 Called from CardScriptRegistry.RegisterBuiltins().
// Layer:          Loader
// Collaborators:  DruidEffects.cs (the effect classes),
//                 JsonCardLoader.cs (registry infrastructure)
// ============================================================

public static partial class CardScriptRegistry
{
    /// <summary>Registers all Druid-school effect factories.</summary>
    private static void RegisterDruidEffects()
    {
        // ═══════════════════════════════════════════════════════════
        // DRUID EFFECTS
        // ═══════════════════════════════════════════════════════════


        // Seed growth: { "type": "seed_growth", "stage": 1, "radius": 0, "wilding": 1 }
        RegisterEffect("seed_growth", n =>
        {
            int stage = n.TryGetProperty("stage", out var st) ? st.GetInt32() : 1;
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 0;
            int wilding = n.TryGetProperty("wilding", out var w) ? w.GetInt32() : 1;
            return new SeedGrowthEffect(stage, radius, wilding).WithTag("Growth");
        });

        // Advance growth: { "type": "advance_growth", "radius": 1 }
        RegisterEffect("advance_growth", n =>
            new AdvanceGrowthEffect(n.TryGetProperty("radius", out var r) ? r.GetInt32() : 1).WithTag("Growth"));

        // Spread growth: { "type": "spread_growth", "radius": 2 }
        RegisterEffect("spread_growth", n =>
            new SpreadGrowthEffect(n.TryGetProperty("radius", out var r) ? r.GetInt32() : 2).WithTag("Growth"));

        // Entangle: { "type": "entangle", "radius": 2, "duration": 1 }
        RegisterEffect("entangle", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 2;
            int duration = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
            return new EntangleEffect(radius, duration).WithTag("Debuff");
        });

        // Harvest growth: { "type": "harvest_growth", "radius": 2, "heal_per": 3, "draw_per": 0,
        //                   "mana_per": 0, "mana_cap": 99 }
        RegisterEffect("harvest_growth", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 2;
            int healPer = n.TryGetProperty("heal_per", out var h) ? h.GetInt32() : 0;
            int drawPer = n.TryGetProperty("draw_per", out var d) ? d.GetInt32() : 0;
            int manaPer = n.TryGetProperty("mana_per", out var mp) ? mp.GetInt32() : 0;
            int manaCap = n.TryGetProperty("mana_cap", out var mc) ? mc.GetInt32() : 99;
            return new HarvestGrowthEffect(radius, healPer, drawPer, manaPer, manaCap).WithTag("Growth");
        });

        // Thornlash: { "type": "thornlash", "damage": 3, "per_stage": 2 }
        RegisterEffect("thornlash", n =>
        {
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 0;
            int perStage = n.TryGetProperty("per_stage", out var p) ? p.GetInt32() : 0;
            return new ThornlashEffect(damage, perStage).WithTag("Damage");
        });

        // Gain wilding: { "type": "gain_wilding", "amount": 1 }
        RegisterEffect("gain_wilding", n =>
            new GainWildingEffect(n.GetProperty("amount").GetInt32()).WithTag("Wilding"));

        // Summon wildlife: { "type": "summon_wildlife", "unit": "auto" }
        RegisterEffect("summon_wildlife", n =>
            new SummonWildlifeEffect(n.TryGetProperty("unit", out var u) ? u.GetString() : "auto").WithTag("Summon"));
    }
}
