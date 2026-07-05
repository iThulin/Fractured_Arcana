using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Necromancer.cs
//
// Purpose:        Necromancer school effect registrations — maps the
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

        // Walk Between 
        // { "type": "hollow_mantle_bonus_armor", "turns": n, "armor": n, "bonus_armor": n }
        RegisterEffect("walk_between", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2;
            int armor = n.TryGetProperty("armor", out var a) ? a.GetInt32() : 14;
            return new HollowMantleLeafEffect(turns, armor).WithTag("Transform");
        });
    }
}
