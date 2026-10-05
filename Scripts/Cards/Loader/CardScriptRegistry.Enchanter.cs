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
            int weave = n.TryGetProperty("on_trigger_weave", out var w) ? w.GetInt32() : 0;
            bool persist = n.TryGetProperty("persist", out var p) && p.ValueKind == JsonValueKind.True;
            return new LinkGlyphsEffect(count, bonus, weave, persist).WithTag("Glyph");
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
            int repeat = n.TryGetProperty("repeat", out var r) ? r.GetInt32() : 1;
            int weave = n.TryGetProperty("on_trigger_weave", out var w) ? w.GetInt32() : 0;
            return new TriggerAllGlyphsEffect(bonus, consume, repeat, weave).WithTag("Glyph");
        });

        // Swap two glyph tiles
        // { "type": "swap_glyphs" }
        RegisterEffect("swap_glyphs", n =>
        {
            bool after = n.TryGetProperty("trigger_after", out var ta) && ta.ValueKind == JsonValueKind.True;
            int bonus = n.TryGetProperty("bonus", out var b) ? b.GetInt32() : 0;
            bool all = n.TryGetProperty("trigger_all_after", out var tl) && tl.ValueKind == JsonValueKind.True;
            int weave = n.TryGetProperty("on_trigger_weave", out var w) ? w.GetInt32() : 0;
            return new SwapGlyphsEffect(after, bonus, all, weave).WithTag("Glyph");
        });

        // Teleport caster onto nearest friendly glyph
        // { "type": "teleport_to_glyph", "trigger_on_arrive": bool }
        RegisterEffect("teleport_to_glyph", n =>
        {
            bool trigger = n.TryGetProperty("trigger_on_arrive", out var t) && t.GetBoolean();
            bool refresh = n.TryGetProperty("refresh", out var rf) && rf.ValueKind == JsonValueKind.True;
            return new TeleportToGlyphEffect(trigger, refresh).WithTag("Movement");
        });

        // Permanent reusable ally-buff pillars
        // { "type": "enchant_pillar", "count": n, "ally_all_stats": n, ... }
        RegisterEffect("enchant_pillar", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 3;
            int allyAll = n.TryGetProperty("ally_all_stats", out var a) ? a.GetInt32() : 2;
            int enemyDr = n.TryGetProperty("enemy_damage_reduction", out var e) ? e.GetInt32() : 0;
            string aura = n.TryGetProperty("aura_status", out var au) ? au.GetString() : null;
            int auraDmg = n.TryGetProperty("aura_damage", out var ad) ? ad.GetInt32() : 0;
            int wpt = n.TryGetProperty("weave_per_turn", out var wp) ? wp.GetInt32() : 0;
            return new EnchantPillarEffect(count, allyAll, enemyDr, aura, auraDmg, wpt).WithTag("Glyph");
        });

        // Reflect-ward glyph (placement only; reflection needs the cast pipeline)
        // { "type": "reflect_ward", "triggers": n, "radius": n }
        RegisterEffect("reflect_ward", n =>
        {
            int triggers = n.TryGetProperty("triggers", out var t) ? t.GetInt32() : 1;
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 0;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 3;
            float bonus = n.TryGetProperty("reflect_bonus", out var rb) ? (float)rb.GetDouble() : 0f;
            return new ReflectWardEffect(triggers, radius, dur, bonus).WithTag("Glyph");
        });

        // Spell-anchor glyph (placement only; cast-twice needs the cast pipeline)
        // { "type": "spell_anchor", "casts": n }
        RegisterEffect("spell_anchor", n =>
        {
            int casts = n.TryGetProperty("casts", out var c) ? c.GetInt32() : 2;
            int cr = n.TryGetProperty("cost_reduction", out var cc) ? cc.GetInt32() : 0;
            int weave = n.TryGetProperty("on_use_weave", out var w) ? w.GetInt32() : 0;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 3;
            bool reuse = n.TryGetProperty("reusable", out var ru) && ru.ValueKind == JsonValueKind.True;
            return new SpellAnchorEffect(casts, cr, weave, dur, reuse).WithTag("Glyph");
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
            int dmg = n.TryGetProperty("damage_enemies", out var d) ? d.GetInt32() : 0;
            bool name = n.TryGetProperty("name_enemy", out var ne) && ne.ValueKind == JsonValueKind.True;
            return new SwapUnitsEffect(withCaster, dmg, name).WithTag("Movement");
        });

        // Dispel Walk: { "type": "dispel_walk", "count": n, "steal": bool }
        RegisterEffect("dispel_walk", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            bool steal = n.TryGetProperty("steal", out var st) && st.ValueKind == JsonValueKind.True;
            return new DispelWalkEffect(count, steal).WithTag("Control");
        });

        // First Layer: { "type": "swap_locks" } with unit_then_unit targeting.
        RegisterEffect("swap_locks", _ => new SwapLocksEffect().WithTag("Control"));

        // Puppeteer: { "type": "puppeteer", "turns": n, "move_tiles": n, "name_controlled": bool }
        RegisterEffect("puppeteer", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2;
            int tiles = n.TryGetProperty("move_tiles", out var mt) ? mt.GetInt32() : 2;
            bool name = n.TryGetProperty("name_controlled", out var nc) && nc.ValueKind == JsonValueKind.True;
            return new PuppeteerEffect(turns, tiles, name).WithTag("Movement");
        });

        // Geas (2026-10-04, slice 11): a real Name now, not a status with no hook.
        // { "type": "geas", "damage": n, "duration": n, "punish_cast": bool }
        RegisterEffect("geas", n =>
        {
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 2;
            int dmg = n.TryGetProperty("damage", out var dm) ? dm.GetInt32() : 6;
            var trig = NameTrigger.Move;
            if (n.TryGetProperty("punish_cast", out var pc) && pc.ValueKind == System.Text.Json.JsonValueKind.True)
                trig |= NameTrigger.Cast;
            return new NameConditionEffect(trig, dmg, dur).WithTag("Control");
        });

        // Names (class_identity_enchanter_v1 §2b): a condition that punishes.
        // { "type": "name_condition", "trigger": "move"|"attack"|"cast"|"act" or [..], "damage": n, "duration": n }
        RegisterEffect("name_condition", n =>
        {
            var trig = NameTrigger.None;
            void Add(string w)
            {
                trig |= (w ?? "").ToLowerInvariant() switch
                {
                    "move" or "moves" => NameTrigger.Move,
                    "attack" or "attacks" => NameTrigger.Attack,
                    "cast" or "casts" => NameTrigger.Cast,
                    _ => NameTrigger.Any,
                };
            }
            if (n.TryGetProperty("trigger", out var t))
            {
                if (t.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var e in t.EnumerateArray()) Add(e.GetString());
                else
                    Add(t.GetString());
            }
            int dmg = n.TryGetProperty("damage", out var dm) ? dm.GetInt32() : 4;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 2;
            bool B(string k) => n.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            var eff = new NameConditionEffect(trig, dmg, dur)
            {
                HalveAttack = B("halve_attack"),
                Backfire = B("backfire"),
                Shun = B("shun"),
                PerTrigger = B("per_trigger"),
                Grouped = B("grouped"),
            };
            // "trigger": "none" is a Name that never breaks (Not Me only forbids a target).
            if (n.TryGetProperty("trigger", out var tn) && tn.ValueKind == JsonValueKind.String && tn.GetString() == "none")
                eff.Triggers = NameTrigger.None;
            return eff.WithTag("Control");
        });

        // Compel (§2a): the target WALKS (walked entry rules), toward you or a chosen way.
        // { "type": "compel_walk", "tiles": n, "mode": "toward_caster" | "direction" }
        RegisterEffect("compel_walk", n =>
        {
            int tiles = n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 2;
            string mode = n.TryGetProperty("mode", out var m) ? m.GetString() : "toward_caster";
            return new CompelWalkEffect(tiles, mode == "direction") { Chosen = mode == "chosen_path" }.WithTag("Movement");
        });

        // ── Slice 13a: Geas-Binder and Puppeteer ─────────────────────────
        // { "type": "misdirect_attack", "damage": n, "duration": n }
        RegisterEffect("misdirect_attack", n =>
        {
            int dmg = n.TryGetProperty("damage", out var dm) ? dm.GetInt32() : 0;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
            return new MisdirectAttackEffect(dmg, dur).WithTag("Control");
        });
        // { "type": "borrow_will" } with unit_then_tile + any_tile
        RegisterEffect("borrow_will", _ => new BorrowWillEffect().WithTag("Control"));
        // { "type": "draw_on_break", "cap": n }
        RegisterEffect("draw_on_break", n =>
            new DrawOnBreakEffect(n.TryGetProperty("cap", out var c) ? c.GetInt32() : 3).WithTag("CardDraw"));
        // { "type": "compound_names", "growth": n }
        RegisterEffect("compound_names", n =>
            new CompoundNamesEffect(n.TryGetProperty("growth", out var g) ? g.GetInt32() : 2).WithTag("Control"));
        // { "type": "target_named" } / { "type": "name_broken_recently" }
        RegisterPredicate("target_named", _ => new TargetNamedPredicate());
        RegisterPredicate("name_broken_recently", _ => new NameBrokenRecentlyPredicate());

        // Mana Tithe (slice 12a, ruled 2026-10-04): a Name on casting. Enemies have no
        // mana, so the "tax" is pain: if it casts, it takes damage (3 at amount 1, 5 at
        // amount 2, or an explicit "damage"), and its Namer gains the refund in mana and
        // weave_on_cast in Weave. Names punish, never forbid.
        // { "type": "mana_tithe", "amount": n, "refund": n, "weave_on_cast": n, "duration": n, "damage": n }
        RegisterEffect("mana_tithe", n =>
        {
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 3;
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 1;
            int dmg = n.TryGetProperty("damage", out var dm) ? dm.GetInt32() : 1 + 2 * Math.Max(1, amount);
            int refund = n.TryGetProperty("refund", out var r) ? r.GetInt32() : 1;
            int weave = n.TryGetProperty("weave_on_cast", out var w) ? w.GetInt32() : 0;
            return new NameConditionEffect(NameTrigger.Cast, dmg, dur)
                { OwnerMana = refund, OwnerWeave = weave }.WithTag("Control");
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
            int Geti(string k, int d) => n.TryGetProperty(k, out var v) ? v.GetInt32() : d;
            bool Getb(string k) => n.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            return new SummonIllusionEffect(Geti("count", 1), Geti("hp", 4), Geti("duration", 3),
                Geti("copies_attack", 0), Getb("reflect_on_hit"), Getb("trigger_glyph_on_death")).WithTag("Summon");
        });

        // Glyphs deal double effects while active (add check to GlyphData.Fire)
        // { "type": "grand_design_passive", "turns": n }
        RegisterEffect("grand_design_passive", n =>
        {
            int Geti(string k, int d) => n.TryGetProperty(k, out var v) ? v.GetInt32() : d;
            return new GrandDesignPassiveLeafEffect(Geti("spread_radius", 0), Geti("trigger_count", 2),
                Geti("glyph_cost_reduction", 0), Geti("weave_per_prepare", 0)).WithTag("Glyph");
        });

        // Persistent zone that damages enemies in range each turn
        // { "type": "absolute_territory", "radius": n, "damage_per_turn": n, "turns": n }
        RegisterEffect("absolute_territory", n =>
        {
            int Geti(string k, int d) => n.TryGetProperty(k, out var v) ? v.GetInt32() : d;
            bool name = n.TryGetProperty("name_enemies", out var ne) && ne.ValueKind == JsonValueKind.True;
            return new AbsoluteTerritoryLeafEffect(Geti("radius", 3), Geti("damage_per_tile", 2),
                Geti("duration", 2), name).WithTag("Control");
        });
    }
}
