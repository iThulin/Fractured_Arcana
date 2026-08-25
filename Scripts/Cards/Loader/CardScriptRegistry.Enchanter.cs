using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Enchanter.cs
//
// Purpose:        Enchanter school effect registrations. Maps the
//                 school's JSON `type` keys to effect factories.
//                 Called from CardScriptRegistry.RegisterBuiltins().
// Layer:          Loader
// Collaborators:  EnchanterEffects.cs (the effect classes),
//                 JsonCardLoader.cs (registry infrastructure)
// ============================================================

public static partial class CardScriptRegistry
{
    /// <summary>Registers all Enchanter-school effect factories.</summary>
    private static void RegisterEnchanterEffects()
    {
        // ═══════════════════════════════════════════════════════════
        // ENCHANTER EFFECTS
        // ═══════════════════════════════════════════════════════════

        // Gain Weave charges
        // { "type": "gain_weave", "amount": n }
        RegisterEffect("gain_weave", n =>
            new GainWeaveEffect(n.GetProperty("amount").GetInt32()).WithTag("Weave"));

        // Spend Weave charges to deal damage
        // { "type": "spend_weave_damage", "damage_per_weave": n, "min_spend": n, "max_spend": n }
        RegisterEffect("damage_per_glyph", n =>
        {
            int amt = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 3;
            int min = n.TryGetProperty("min", out var m) ? m.GetInt32() : 0;
            return new DamagePerGlyphEffect(amt, min).WithTag("Damage");
        });

        // Prepare glyph: one or `count` tiles
        // { "type": "prepare_glyph", "trigger": "enter", "damage": n, "status": s, ... }
        RegisterEffect("prepare_glyph", n => BuildPrepareGlyph(n, area: false, cascade: 0).WithTag("Glyph"));

        // Prepare glyphs across a radius
        // { "type": "prepare_glyph_area", "damage": n, "radius": n, "empty_only": bool }
        RegisterEffect("prepare_glyph_area", n => BuildPrepareGlyph(n, area: true, cascade: 0).WithTag("Glyph"));

        // Cascade glyph: an enter glyph that spreads on trigger
        // { "type": "cascade_glyph", "damage": n, "spread": n }
        RegisterEffect("cascade_glyph", n =>
        {
            int spread = n.TryGetProperty("spread", out var sp) ? sp.GetInt32() : 2;
            return BuildPrepareGlyph(n, area: false, cascade: spread).WithTag("Glyph");
        });

        // Link friendly glyphs so triggering one triggers the group
        // { "type": "link_glyphs", "count": n, "cumulative_bonus": n }
        RegisterEffect("link_glyphs", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 2;
            int bonus = n.TryGetProperty("cumulative_bonus", out var b) ? b.GetInt32() : 0;
            return new LinkGlyphsEffect(count, bonus).WithTag("Glyph");
        });

        // Re-arm consumed friendly glyphs, optional empower
        // { "type": "rearm_glyphs", "empower": n }
        RegisterEffect("rearm_glyphs", n =>
        {
            int empower = n.TryGetProperty("empower", out var e) ? e.GetInt32() : 0;
            return new RearmGlyphsEffect(empower).WithTag("Glyph");
        });

        // Fire all friendly glyphs at once
        // { "type": "trigger_all_glyphs", "bonus_per_other": n, "consume": bool }
        RegisterEffect("trigger_all_glyphs", n =>
        {
            int bonus = n.TryGetProperty("bonus_per_other", out var b) ? b.GetInt32() : 0;
            bool consume = !n.TryGetProperty("consume", out var c) || c.GetBoolean();
            return new TriggerAllGlyphsEffect(bonus, consume).WithTag("Glyph");
        });

        // Swap two glyph tiles
        // { "type": "swap_glyphs" }
        RegisterEffect("swap_glyphs", _ => new SwapGlyphsEffect().WithTag("Glyph"));

        // Teleport caster onto nearest friendly glyph
        // { "type": "teleport_to_glyph", "trigger_on_arrive": bool }
        RegisterEffect("teleport_to_glyph", n =>
        {
            bool trigger = n.TryGetProperty("trigger_on_arrive", out var t) && t.GetBoolean();
            return new TeleportToGlyphEffect(trigger).WithTag("Movement");
        });

        // Permanent reusable ally-buff pillars
        // { "type": "enchant_pillar", "count": n, "ally_all_stats": n, ... }
        RegisterEffect("enchant_pillar", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 3;
            int allyAll = n.TryGetProperty("ally_all_stats", out var a) ? a.GetInt32() : 2;
            int enemyDr = n.TryGetProperty("enemy_damage_reduction", out var e) ? e.GetInt32() : 0;
            string aura = n.TryGetProperty("aura_status", out var au) ? au.GetString() : null;
            return new EnchantPillarEffect(count, allyAll, enemyDr, aura).WithTag("Glyph");
        });

        // Reflect-ward glyph (placement only; reflection needs the cast pipeline)
        // { "type": "reflect_ward", "triggers": n, "radius": n }
        RegisterEffect("reflect_ward", n =>
        {
            int triggers = n.TryGetProperty("triggers", out var t) ? t.GetInt32() : 1;
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 0;
            return new ReflectWardEffect(triggers, radius).WithTag("Glyph");
        });

        // Spell-anchor glyph (placement only; cast-twice needs the cast pipeline)
        // { "type": "spell_anchor", "casts": n }
        RegisterEffect("spell_anchor", n =>
        {
            int casts = n.TryGetProperty("casts", out var c) ? c.GetInt32() : 2;
            return new SpellAnchorEffect(casts).WithTag("Glyph");
        });

        // Push/pull a target onto the nearest friendly glyph
        // { "type": "push_to_glyph" } / { "type": "pull_to_glyph" }
        RegisterEffect("push_to_glyph", _ => new MoveToGlyphEffect("PushToGlyph").WithTag("Movement"));
        RegisterEffect("pull_to_glyph", _ => new MoveToGlyphEffect("PullToGlyph").WithTag("Movement"));

        // Dispel buffs from target, optionally steal
        // { "type": "dispel", "count": n, "steal": bool }
        RegisterEffect("dispel", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            bool steal = n.TryGetProperty("steal", out var st) && st.GetBoolean();
            return new DispelEffect(count, steal).WithTag("Control");
        });

        // Swap positions of two targeted units
        // { "type": "swap_units" }
        RegisterEffect("swap_units", n =>
        {
            bool withCaster = n.TryGetProperty("with_caster", out var w) && w.GetBoolean();
            return new SwapUnitsEffect(withCaster).WithTag("Movement");
        });

        // Geas: status whose on-move punish lives in the status system
        // { "type": "geas", "duration": n }
        RegisterEffect("geas", n =>
        {
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 2;
            return new StatusApplyEffect("geas", dur, "(on-move punish needs status hook)").WithTag("Control");
        });

        // Mana tithe: status whose cost-up/refund lives in the status system
        // { "type": "mana_tithe", "duration": n }
        RegisterEffect("mana_tithe", n =>
        {
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 3;
            return new StatusApplyEffect("mana_taxed", dur, "(cost-up / mana-refund needs status hook)").WithTag("Control");
        });


        // ═══════════════════════════════════════════════════════════
        // ENCHANTER: CONTROL / ZONE
        // ═══════════════════════════════════════════════════════════

        // Dominated enemies attack their own allies each turn
        // { "type": "dominate", "turns": n }
        RegisterEffect("dominate", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2;
            return new DominateEffect(turns).WithTag("Control");
        });

        // Summon a phantom copy of the caster with halved stats
        // { "type": "summon_illusion", "hp_fraction": 0.5, "duration": n }
        RegisterEffect("summon_illusion", n =>
        {
            float frac = n.TryGetProperty("hp_fraction", out var f) ? (float)f.GetDouble() : 0.5f;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 3;
            return new SummonIllusionEffect(frac, dur).WithTag("Summon");
        });

        // Glyphs deal double effects while active (add check to GlyphData.Fire)
        // { "type": "grand_design_passive", "turns": n }
        RegisterEffect("grand_design_passive", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new GrandDesignPassiveLeafEffect(turns).WithTag("Glyph");
        });

        // Persistent zone that damages enemies in range each turn
        // { "type": "absolute_territory", "radius": n, "damage_per_turn": n, "turns": n }
        RegisterEffect("absolute_territory", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 3;
            int dpt = n.TryGetProperty("damage_per_turn", out var d) ? d.GetInt32() : 2;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new AbsoluteTerritoryLeafEffect(radius, dpt, turns).WithTag("Control");
        });
    }
}
