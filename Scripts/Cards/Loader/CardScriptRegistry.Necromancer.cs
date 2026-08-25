using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Necromancer.cs
//
// Purpose:        Necromancer school effect registrations. Maps the
//                 school's JSON `type` keys to effect factories.
//                 Called from CardScriptRegistry.RegisterBuiltins().
// Layer:          Loader
// Collaborators:  NecromancerEffects.cs (the effect classes),
//                 JsonCardLoader.cs (registry infrastructure)
// ============================================================

public static partial class CardScriptRegistry
{
    /// <summary>Registers all Necromancer-school effect factories.</summary>
    private static void RegisterNecromancerEffects()
    {
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
            int hp = n.TryGetProperty("hp", out var h) ? h.GetInt32() : 10;
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 5;
            int speed = n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 1;
            bool onDeath = n.TryGetProperty("on_death_memorial", out var od) && od.GetBoolean();
            return new SummonSpiritEffect(unit, hp, damage, speed, onDeath).WithTag("Summon");
        });

        // Summon spirit from every memorial on the board
        // { "type": "summon_spirit_from_all_memorials", "unit": "Spirit", "hp": 10, ... }
        RegisterEffect("summon_spirit_from_all_memorials", n =>
        {
            string unit = n.TryGetProperty("unit", out var u) ? u.GetString() : "Spirit";
            int baseHp = n.TryGetProperty("hp", out var h) ? h.GetInt32() : 10;
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 5;
            int speed = n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 1;
            bool hpPerSpirit = n.TryGetProperty("hp_per_spirit", out var hps) && hps.GetBoolean();
            int advance = n.TryGetProperty("on_arrive_advance", out var oa) ? oa.GetInt32() : 0;
            bool inheritName = n.TryGetProperty("inherit_memorial_name", out var im) && im.GetBoolean();
            int bonusDmg = n.TryGetProperty("bonus_damage_per_strength", out var bd) ? bd.GetInt32() : 0;
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
                "faint" => MemorialStrength.Faint,
                "strong" => MemorialStrength.Strong,
                _ => MemorialStrength.Solid
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
            int tiles = n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 1;
            bool atk = !n.TryGetProperty("attack_if_adjacent", out var a) || a.GetBoolean();
            bool grant = n.TryGetProperty("grant_attack_if_reached", out var g) && g.GetBoolean();
            return new AdvanceAllSpiritsEffect(tiles, atk, grant).WithTag("Movement");
        });

        // Buff all friendly spirits with a temporary stat increase
        // { "type": "buff_all_spirits", "stat": "damage", "amount": n, "duration": 1 }
        RegisterEffect("buff_all_spirits", n =>
        {
            string stat = n.TryGetProperty("stat", out var s) ? s.GetString() : "damage";
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 2;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
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
            int dmg = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 3;
            int push = n.TryGetProperty("push", out var p) ? p.GetInt32() : 1;
            int col = n.TryGetProperty("collision_damage", out var c) ? c.GetInt32() : 0;
            return new DirgePulseEffect(dmg, push, col).WithTag("Damage");
        });

        // Hallow target tile
        // { "type": "hallow_tile", "duration": n, "auto_rise_range": n }
        RegisterEffect("hallow_tile", n =>
        {
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 99;
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
            int dmg = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 4;
            int push = n.TryGetProperty("push", out var p) ? p.GetInt32() : 0;
            bool leave = n.TryGetProperty("leave_memorial", out var l) && l.GetBoolean();
            int strikes = n.TryGetProperty("strikes", out var s) ? s.GetInt32() : 1;
            return new MemorialStrikeAllEffect(dmg, push, leave, strikes).WithTag("Damage");
        });

        // Consume memorials for champion summon (handled by SummonSpiritEffect on next step)
        // { "type": "consume_memorials_for_champion", "count": 2, "range": 3 }
        RegisterEffect("consume_memorials_for_champion", n =>
        {
            // Consumes the nearest N memorials within range of the caster,
            // sparing the cast's target tile (the champion rises there), and
            // records combined strength for summon_spirit_scaled.
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 2;
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 3;
            return new ConsumeMemorialsForChampionEffect(count, range).WithTag("Terrain");
        });

        // Imbue target tile as Memorial Ground
        // { "type": "create_memorial_ground", "duration": n, "summon_discount": n }
        RegisterEffect("create_memorial_ground", n =>
        {
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 3;
            int discount = n.TryGetProperty("summon_discount", out var s) ? s.GetInt32() : 2;
            int regen = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 0;
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
            int duration = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
            int reviveHp = n.TryGetProperty("revive_hp", out var r) ? r.GetInt32() : 8;
            return new ApplyStatusToAllSpiritsEffect(status, duration, reviveHp).WithTag("Spirit");
        });

        // Consume all memorials globally, gain mana/draw per memorial
        // { "type": "consume_all_memorials_global", "mana_per": n, "draw_per": n, "spare_strong": bool }
        RegisterEffect("consume_all_memorials_global", n =>
        {
            int mana = n.TryGetProperty("mana_per", out var m) ? m.GetInt32() : 0;
            int draw = n.TryGetProperty("draw_per", out var d) ? d.GetInt32() : 0;
            bool spare = n.TryGetProperty("spare_strong", out var sp) && sp.GetBoolean();
            return new ConsumeAllMemorialsGlobalEffect(mana, draw, spare).WithTag("Terrain");
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
            new SwapWithSpiritEffect().WithTag("Movement"));

        // Pull memorials 1 tile toward the caster; overlapping pairs merge into a
        // summoned unit. Optional remainder_unit makes lone memorials rise too.
        // { "type": "pull_memorials_and_merge", "range": 3, "merge_unit": "Revenant",
        //   "merge_hp": 12, "merge_damage": 5, "merge_speed": 1,
        //   "scale_with_strength": false, "remainder_unit": "Spirit", ... }
        RegisterEffect("pull_memorials_and_merge", n =>
            new PullMemorialsAndMergeEffect(
                n.TryGetProperty("range", out var r) ? r.GetInt32() : 3,
                n.TryGetProperty("merge_unit", out var mu) ? mu.GetString() : "Revenant",
                n.TryGetProperty("merge_hp", out var mh) ? mh.GetInt32() : 12,
                n.TryGetProperty("merge_damage", out var md) ? md.GetInt32() : 5,
                n.TryGetProperty("merge_speed", out var ms) ? ms.GetInt32() : 1,
                n.TryGetProperty("scale_with_strength", out var sw) && sw.GetBoolean(),
                n.TryGetProperty("remainder_unit", out var ru) ? ru.GetString() : null,
                n.TryGetProperty("remainder_hp", out var rh) ? rh.GetInt32() : 0,
                n.TryGetProperty("remainder_damage", out var rd) ? rd.GetInt32() : 0,
                n.TryGetProperty("remainder_speed", out var rs) ? rs.GetInt32() : 1
            ).WithTag("Terrain"));

        // Mark spirits to draw cards on kill this turn
        // { "type": "mark_spirits_draw_on_kill", "count": 1 }
        RegisterEffect("mark_spirits_draw_on_kill", n =>
            new MarkSpiritsDrawOnKillEffect(
                n.TryGetProperty("count", out var c) ? c.GetInt32() : 1).WithTag("Spirit"));

        // Shield per memorial: { "type": "shield_per_memorial", "amount_per": 1 }
        RegisterEffect("shield_per_memorial", n =>
            new ShieldPerMemorialEffect(
                n.TryGetProperty("amount_per", out var a) ? a.GetInt32() : 1).WithTag("Defense"));

        // Consume all memorials in range
        RegisterEffect("consume_all_memorials_in_range", n =>
        {
            int mana = n.TryGetProperty("mana_per", out var m) ? m.GetInt32() : 0;
            int draw = n.TryGetProperty("draw_per", out var d) ? d.GetInt32() : 0;
            return new ConsumeAllMemorialsGlobalEffect(mana, draw).WithTag("Terrain");
        });

        // Trigger the Flood immediately: { "type": "trigger_flood" }
        RegisterEffect("trigger_flood", _ =>
            new TriggerFloodEffect().WithTag("Grief"));

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
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int regen = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 2;
            int range = n.TryGetProperty("spirit_regen_range", out var rr) ? rr.GetInt32() : 2;
            int mdr = n.TryGetProperty("memorial_on_spirit_death_range", out var m) ? m.GetInt32() : 0;
            int arr = n.TryGetProperty("auto_rise_range", out var ar) ? ar.GetInt32() : 0;
            int grief = n.TryGetProperty("grief_per_turn", out var g) ? g.GetInt32() : 0;
            return new OssUaryAuraLeafEffect(turns, regen, range, mdr, arr, grief).WithTag("Persistent");
        });

        // Ossuary Shrine (spirit deaths near ossuary leave memorials)
        // { "type": "ossuary_aura_shrine", "spirit_regen": n, "spirit_regen_range": n, "memorial_on_spirit_death_range": n }
        RegisterEffect("ossuary_aura_shrine", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int regen = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 3;
            int range = n.TryGetProperty("spirit_regen_range", out var rr) ? rr.GetInt32() : 2;
            int mdr = n.TryGetProperty("memorial_on_spirit_death_range", out var m) ? m.GetInt32() : 2;
            return new OssUaryAuraLeafEffect(turns, regen, range, mdr).WithTag("Persistent");
        });

        // Ossuary Garden (auto-rise from adjacent memorials)
        // { "type": "ossuary_aura_garden", "spirit_regen": n, "spirit_regen_range": n, "memorial_on_spirit_death_range": n, "auto_rise_range": n }
        RegisterEffect("ossuary_aura_garden", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int regen = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 3;
            int range = n.TryGetProperty("spirit_regen_range", out var rr) ? rr.GetInt32() : 2;
            int mdr = n.TryGetProperty("memorial_on_spirit_death_range", out var m) ? m.GetInt32() : 2;
            int arr = n.TryGetProperty("auto_rise_range", out var ar) ? ar.GetInt32() : 1;
            return new OssUaryAuraLeafEffect(turns, regen, range, mdr, arr).WithTag("Persistent");
        });

        // Soul Well (indestructible ossuary variant with grief per turn)
        // { "type": "soul_well_aura", "spirit_regen": n, "spirit_regen_range": n, "memorial_on_spirit_death_range": n, "auto_rise_range": n, "grief_per_turn": n }
        RegisterEffect("soul_well_aura", n =>
        {
            int regen = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 3;
            int range = n.TryGetProperty("spirit_regen_range", out var rr) ? rr.GetInt32() : 4;
            int mdr = n.TryGetProperty("memorial_on_spirit_death_range", out var m) ? m.GetInt32() : 4;
            int arr = n.TryGetProperty("auto_rise_range", out var ar) ? ar.GetInt32() : 2;
            int grief = n.TryGetProperty("grief_per_turn", out var g) ? g.GetInt32() : 1;
            return new OssUaryAuraLeafEffect(99, regen, range, mdr, arr, grief).WithTag("Persistent");
        });

        // Memorial Seat Aura: spirits +2/+2, healing triggers twice
        // { "type": "memorial_seat_aura" }
        RegisterEffect("memorial_seat_aura", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 2;
            int armor = n.TryGetProperty("spirit_buff_armor", out var a) ? a.GetInt32() : 2;
            return new MemorialSeatAuraLeafEffect(turns, dmg, armor).WithTag("Persistent");
        });

        // Memorial Seat Aura (with healing)
        // { "type": "memorial_seat_aura_healing", "turns": n, "spirit_buff_damage": n, "spirit_buff_armor": n, "spirit_regen": n }
        RegisterEffect("memorial_seat_aura_healing", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 2;
            int armor = n.TryGetProperty("spirit_buff_armor", out var a) ? a.GetInt32() : 2;
            int regen = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 2;
            return new MemorialSeatAuraLeafEffect(turns, dmg, armor, regenRange: 2, regen: regen).WithTag("Persistent");
        });

        // Memorial Seat Aura (with draw per turn)
        // { "type": "memorial_seat_aura_counsel", "turns": n, "spirit_buff_damage": n, "spirit_regen": n, "draw_per_turn": n }
        RegisterEffect("memorial_seat_aura_counsel", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 2;
            int regen = n.TryGetProperty("spirit_regen", out var r) ? r.GetInt32() : 2;
            int draw = n.TryGetProperty("draw_per_turn", out var dr) ? dr.GetInt32() : 1;
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
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 2;
            int range = n.TryGetProperty("spirit_buff_range", out var r) ? r.GetInt32() : 3;
            bool prot = n.TryGetProperty("protect_memorials", out var p) && p.GetBoolean();
            return new ElderAuraLeafEffect(turns, dmg, range, prot).WithTag("Persistent");
        });

        // Elder Aura Keeper (with memorial protection)
        // { "type": "elder_aura_keeper", "spirit_buff_damage": n, "spirit_buff_range": n }
        RegisterEffect("elder_aura_keeper", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 99;
            int dmg = n.TryGetProperty("spirit_buff_damage", out var d) ? d.GetInt32() : 3;
            int range = n.TryGetProperty("spirit_buff_range", out var r) ? r.GetInt32() : 3;
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

        // Walk Between (Hollow Mantle tier 4): spells heal all spirits while active.
        // { "type": "walk_between", "turns": 2, "spirit_heal_on_cast": 3 }
        // (Was a miswired hollow_mantle duplicate, fixed 2026-07-06.)
        RegisterEffect("walk_between", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2;
            int heal = n.TryGetProperty("spirit_heal_on_cast", out var h) ? h.GetInt32() : 3;
            return new WalkBetweenLeafEffect(turns, heal).WithTag("Transform");
        });

        // ═══════════════════════════════════════════════════════════
        // Upgrade-tier backlog (2026-07-06). Contracts live in
        // docs/card_effect_backlog.md
        // ═══════════════════════════════════════════════════════════

        // { "type": "pull_to_memorial", "range": 6 }
        RegisterEffect("pull_to_memorial", n =>
            new PullToMemorialEffect(
                n.TryGetProperty("range", out var r) ? r.GetInt32() : 6).WithTag("Movement"));

        // { "type": "pull_all_to_memorial", "range": 3, "tiles": 1 }
        RegisterEffect("pull_all_to_memorial", n =>
            new PullAllToMemorialEffect(
                n.TryGetProperty("range", out var r) ? r.GetInt32() : 3,
                n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 1).WithTag("Movement"));

        // { "type": "push_all_from_memorial", "tiles": 2, "collision_damage": 2 }
        RegisterEffect("push_all_from_memorial", n =>
            new PushAllFromMemorialEffect(
                n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 2,
                n.TryGetProperty("collision_damage", out var c) ? c.GetInt32() : 0).WithTag("Movement"));

        // { "type": "push_all_to_memorial", "damage_before": 5, "damage_on_land": 4 }
        RegisterEffect("push_all_to_memorial", n =>
            new PushAllToMemorialEffect(
                n.TryGetProperty("damage_before", out var db) ? db.GetInt32() : 0,
                n.TryGetProperty("damage_on_land", out var dl) ? dl.GetInt32() : 0).WithTag("Movement"));

        // { "type": "mark_on_death_memorial", "strength": "strong" }
        RegisterEffect("mark_on_death_memorial", n =>
        {
            string strengthStr = n.TryGetProperty("strength", out var sv) ? sv.GetString() : "strong";
            var strength = strengthStr switch
            {
                "faint" => MemorialStrength.Faint,
                "solid" => MemorialStrength.Solid,
                _ => MemorialStrength.Strong
            };
            return new MarkOnDeathMemorialEffect(strength).WithTag("Status");
        });

        // { "type": "commune_all_memorials", "range": 3, "draw_per": 1, "grief_per": 1,
        //   "summon_per": { "unit": "Spirit", "hp": 8, "damage": 4, "speed": 1 }, "consume": false }
        RegisterEffect("commune_all_memorials", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 3;
            int drawPer = n.TryGetProperty("draw_per", out var d) ? d.GetInt32() : 1;
            int griefPer = n.TryGetProperty("grief_per", out var g) ? g.GetInt32() : 1;
            bool consume = !n.TryGetProperty("consume", out var c) || c.GetBoolean();
            string summonUnit = null;
            int sHp = 8, sDmg = 4, sSpd = 1;
            if (n.TryGetProperty("summon_per", out var sp) && sp.ValueKind == JsonValueKind.Object)
            {
                summonUnit = sp.TryGetProperty("unit", out var su) ? su.GetString() : "Spirit";
                sHp = sp.TryGetProperty("hp", out var sh) ? sh.GetInt32() : 8;
                sDmg = sp.TryGetProperty("damage", out var sd) ? sd.GetInt32() : 4;
                sSpd = sp.TryGetProperty("speed", out var ss) ? ss.GetInt32() : 1;
            }
            return new CommuneAllMemorialsEffect(range, drawPer, griefPer, consume,
                summonUnit, sHp, sDmg, sSpd).WithTag("Grief");
        });

        // { "type": "create_memorial_ground_area", "radius": 1, "duration": 5, "summon_discount": 2, "spirit_regen": 2 }
        RegisterEffect("create_memorial_ground_area", n =>
            new CreateMemorialGroundAreaEffect(
                n.TryGetProperty("radius", out var r) ? r.GetInt32() : 1,
                n.TryGetProperty("duration", out var d) ? d.GetInt32() : 5,
                n.TryGetProperty("summon_discount", out var sd) ? sd.GetInt32() : 2,
                n.TryGetProperty("spirit_regen", out var sr) ? sr.GetInt32() : 0).WithTag("Terrain"));

        // { "type": "armor_per_grief_spent", "amount_per": 1 }
        RegisterEffect("armor_per_grief_spent", n =>
            new ArmorPerGriefSpentEffect(
                n.TryGetProperty("amount_per", out var a) ? a.GetInt32() : 1).WithTag("Defense"));

        // { "type": "grief_per_damage", "damage_per_grief": 3 }
        RegisterEffect("grief_per_damage", n =>
            new GriefPerDamageEffect(
                n.TryGetProperty("damage_per_grief", out var d) ? d.GetInt32() : 3).WithTag("Grief"));

        // { "type": "heal_fraction_of_total_damage", "fraction": 1.0 }
        RegisterEffect("heal_fraction_of_total_damage", n =>
            new HealFractionOfTotalDamageEffect(
                n.TryGetProperty("fraction", out var f) ? f.GetSingle() : 1.0f).WithTag("Heal"));

        // { "type": "heal_equal_to_damage_dealt" }, the full-fraction alias
        RegisterEffect("heal_equal_to_damage_dealt", _ =>
            new HealFractionOfTotalDamageEffect(1.0f).WithTag("Heal"));

        // { "type": "heal_most_damaged_spirit", "amount": 4 }
        RegisterEffect("heal_most_damaged_spirit", n =>
            new HealMostDamagedSpiritEffect(
                n.TryGetProperty("amount", out var a) ? a.GetInt32() : 4).WithTag("Heal"));

        // { "type": "grief_overflow_heal_spirits" }
        RegisterEffect("grief_overflow_heal_spirits", _ =>
            new GriefOverflowHealSpiritsEffect().WithTag("Heal"));

        // { "type": "damage_equal_to_missing_hp" }
        RegisterEffect("damage_equal_to_missing_hp", _ =>
            new DamageEqualToMissingHpEffect().WithTag("Damage"));

        // { "type": "dirge_pulse_global", "damage": 4, "push": 2, "collision_damage": 3, "adjacent_spirit_multiplier": 2 }
        RegisterEffect("dirge_pulse_global", n =>
            new DirgePulseGlobalEffect(
                n.TryGetProperty("damage", out var d) ? d.GetInt32() : 4,
                n.TryGetProperty("push", out var p) ? p.GetInt32() : 0,
                n.TryGetProperty("collision_damage", out var c) ? c.GetInt32() : 0,
                n.TryGetProperty("adjacent_spirit_multiplier", out var m) ? m.GetInt32() : 1).WithTag("Damage"));

        // { "type": "teleport_all_spirits_to_nearest_memorial" }
        RegisterEffect("teleport_all_spirits_to_nearest_memorial", _ =>
            new TeleportAllSpiritsToNearestMemorialEffect().WithTag("Movement"));

        // { "type": "damage_per_memorial", "amount_per": 1 }, the targeted variant
        RegisterEffect("damage_per_memorial", n =>
            new TargetedDamagePerMemorialEffect(
                n.TryGetProperty("amount_per", out var a) ? a.GetInt32() : 1).WithTag("Damage"));

        // { "type": "spirit_swap_with_nearest_enemy" }
        RegisterEffect("spirit_swap_with_nearest_enemy", _ =>
            new SpiritSwapWithNearestEnemyEffect().WithTag("Movement"));

        // { "type": "last_rite_aoe", "damage": 7, "spirit_strike": 5, "summon_on_kill": {...} }
        RegisterEffect("last_rite_aoe", n =>
        {
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 7;
            int strike = n.TryGetProperty("spirit_strike", out var st) ? st.GetInt32() : 0;
            string unit = null;
            int hp = 8, dmg = 4, spd = 1;
            if (n.TryGetProperty("summon_on_kill", out var sk) && sk.ValueKind == JsonValueKind.Object)
            {
                unit = sk.TryGetProperty("unit", out var su) ? su.GetString() : "Spirit";
                hp = sk.TryGetProperty("hp", out var sh) ? sh.GetInt32() : 8;
                dmg = sk.TryGetProperty("damage", out var sd2) ? sd2.GetInt32() : 4;
                spd = sk.TryGetProperty("speed", out var ss) ? ss.GetInt32() : 1;
            }
            return new LastRiteAoeEffect(damage, strike, unit, hp, dmg, spd).WithTag("Damage");
        });

        // { "type": "mass_departure", "damage": 7, "push": 2, "collision_damage": 2, "memorial_strength": "strong" }
        RegisterEffect("mass_departure", n =>
        {
            string strengthStr = n.TryGetProperty("memorial_strength", out var ms) ? ms.GetString() : "strong";
            var strength = strengthStr switch
            {
                "faint" => MemorialStrength.Faint,
                "solid" => MemorialStrength.Solid,
                _ => MemorialStrength.Strong
            };
            return new MassDepartureEffect(
                n.TryGetProperty("damage", out var d) ? d.GetInt32() : 7,
                n.TryGetProperty("push", out var p) ? p.GetInt32() : 2,
                n.TryGetProperty("collision_damage", out var c) ? c.GetInt32() : 0,
                strength).WithTag("Damage");
        });

        // { "type": "draw_per_memorial_global", "count_per": 1 }
        RegisterEffect("draw_per_memorial_global", n =>
            new DrawPerMemorialGlobalEffect(
                n.TryGetProperty("count_per", out var c) ? c.GetInt32() : 1).WithTag("CardDraw"));

        // { "type": "strengthen_all_memorials" }
        RegisterEffect("strengthen_all_memorials", _ =>
            new StrengthenAllMemorialsEffect().WithTag("Terrain"));

        // { "type": "summon_spirit_scaled", "unit": "...", "base_hp": 28, "base_damage": 10,
        //   "hp_per_strength": 4, "damage_per_strength": 2, "speed": 1 }
        RegisterEffect("summon_spirit_scaled", n =>
            new SummonSpiritScaledEffect(
                n.TryGetProperty("unit", out var u) ? u.GetString() : "Revenant_Champion",
                n.TryGetProperty("base_hp", out var h) ? h.GetInt32() : 24,
                n.TryGetProperty("base_damage", out var d) ? d.GetInt32() : 8,
                n.TryGetProperty("hp_per_strength", out var hs) ? hs.GetInt32() : 0,
                n.TryGetProperty("damage_per_strength", out var ds) ? ds.GetInt32() : 0,
                n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 1).WithTag("Summon"));

        // { "type": "consume_all_memorials_for_champions", "range": 3, "unit": "...", "base_hp": 24, "base_damage": 8, "speed": 1 }
        RegisterEffect("consume_all_memorials_for_champions", n =>
            new ConsumeAllMemorialsForChampionsEffect(
                n.TryGetProperty("range", out var r) ? r.GetInt32() : 3,
                n.TryGetProperty("unit", out var u) ? u.GetString() : "Revenant_Champion",
                n.TryGetProperty("base_hp", out var h) ? h.GetInt32() : 24,
                n.TryGetProperty("base_damage", out var d) ? d.GetInt32() : 8,
                n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 1).WithTag("Summon"));

        // { "type": "summon_spirit_from_new_memorials", "unit": "Spirit", "hp": 8, "damage": 4, "speed": 1 }
        RegisterEffect("summon_spirit_from_new_memorials", n =>
            new SummonSpiritFromNewMemorialsEffect(
                n.TryGetProperty("unit", out var u) ? u.GetString() : "Spirit",
                n.TryGetProperty("hp", out var h) ? h.GetInt32() : 8,
                n.TryGetProperty("damage", out var d) ? d.GetInt32() : 4,
                n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 1).WithTag("Summon"));

        // { "type": "summon_spirit_from_all_memorials_and_death_sites", ... }
        RegisterEffect("summon_spirit_from_all_memorials_and_death_sites", n =>
            new SummonSpiritFromAllMemorialsAndDeathSitesEffect(
                n.TryGetProperty("unit", out var u) ? u.GetString() : "Spirit",
                n.TryGetProperty("base_hp", out var h) ? h.GetInt32() : 4,
                n.TryGetProperty("damage", out var d) ? d.GetInt32() : 6,
                n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 1,
                n.TryGetProperty("hp_per_spirit", out var hps) && hps.GetBoolean(),
                n.TryGetProperty("on_arrive_advance", out var oa) ? oa.GetInt32() : 0,
                n.TryGetProperty("inherit_memorial_name", out var im) && im.GetBoolean(),
                n.TryGetProperty("bonus_damage_per_strength", out var bd) ? bd.GetInt32() : 0).WithTag("Summon"));

        // { "type": "imbue_path_memorial", "move": 3, "phase": true }
        RegisterEffect("imbue_path_memorial", n =>
            new ImbuePathMemorialEffect(
                n.TryGetProperty("move", out var m) ? m.GetInt32() : 3,
                n.TryGetProperty("phase", out var p) && p.GetBoolean()).WithTag("Movement"));

        // { "type": "draw_per_memorial_passed", "count_per": 1 }
        RegisterEffect("draw_per_memorial_passed", n =>
            new PerMemorialPassedEffect(
                n.TryGetProperty("count_per", out var c) ? c.GetInt32() : 1,
                grantArmor: false).WithTag("CardDraw"));

        // { "type": "armor_per_memorial_passed", "amount_per": 2 }
        RegisterEffect("armor_per_memorial_passed", n =>
            new PerMemorialPassedEffect(
                n.TryGetProperty("amount_per", out var a) ? a.GetInt32() : 2,
                grantArmor: true).WithTag("Defense"));

        // Targeter: nearest memorial tile to the caster.
        // { "type": "nearest_memorial" }
        RegisterTargeter("nearest_memorial", _ => new SelectNearestMemorialTarget());
    }
}
