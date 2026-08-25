using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Arcanist.cs
//
// Purpose:        Arcanist school effect registrations. Maps the
//                 school's JSON `type` keys to effect factories.
//                 Called from CardScriptRegistry.RegisterBuiltins().
// Layer:          Loader
// Collaborators:  ArcanistEffects.cs (the effect classes),
//                 JsonCardLoader.cs (registry infrastructure)
// ============================================================

public static partial class CardScriptRegistry
{
    /// <summary>Registers all Arcanist-school effect factories.</summary>
    private static void RegisterArcanistEffects()
    {
        // ═══════════════════════════════════════════════════════════
        // ARCANIST EFFECTS
        // ═══════════════════════════════════════════════════════════

        // Gain Arcane Charges
        // { "type": "gain_charge", "amount": n }

        RegisterEffect("gain_charge", n =>
            new GainChargeEffect(n.GetProperty("amount").GetInt32()).WithTag("Charge"));

        // Spend Arcane Charges to deal damage
        // { "type": "spend_charge_damage", "damage_per_charge": n, "min_spend": n, "max_spend": n, "self_damage_per_charge": n }
        RegisterEffect("spend_charge_damage", n =>
        {
            int per = n.TryGetProperty("damage_per_charge", out var d) ? d.GetInt32() : 5;
            int min = n.TryGetProperty("min_spend", out var mn) ? mn.GetInt32() : 1;
            int max = n.TryGetProperty("max_spend", out var mx) ? mx.GetInt32() : 0;
            int self = n.TryGetProperty("self_damage_per_charge", out var sd) ? sd.GetInt32() : 0;
            return new SpendChargeDamageEffect(per, min, max, self).WithTag("Damage");
        });

        // Spend Arcane Charges to buff damage
        // { "type": "spend_charge_buff", "damage_per_charge": n, "min_spend": n, "max_spend": n }
        RegisterEffect("damage_per_spell_cast", n =>
        {
            int amt = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 4;
            int min = n.TryGetProperty("min", out var m) ? m.GetInt32() : 0;
            return new DamagePerSpellCastEffect(amt, min).WithTag("Damage");
        });

        // Steal mana from target
        // { "type": "steal_mana", "amount": n }
        RegisterEffect("steal_mana", n =>
            new StealManaEffect(n.TryGetProperty("amount", out var a) ? a.GetInt32() : 1).WithTag("Mana"));

        // Replace with:
        RegisterEffect("scry", n =>
        {
            // Three param vocabularies exist in the authored cards and, until
            // 2026-07-28, this factory understood exactly one of them:
            //   look/keep/discount  Chronomancer. Read correctly.
            //   look/draw           Arcanist. `draw` was NEVER READ, so
            //                         {"look":4,"draw":2} silently became keep=1.
            //   count               Worldshaper. NEITHER key was read, so
            //                         {"count":2} silently became look=3, keep=1.
            // All three are honoured now. `draw` is a straight alias for `keep`.
            // `count` is the REORDER form: look at N, put 1 back on TOP, bottom the
            // rest. Nothing goes to hand.
            bool hasCount = n.TryGetProperty("count", out var cnt);
            int look = n.TryGetProperty("look", out var l) ? l.GetInt32()
                     : hasCount ? cnt.GetInt32() : 3;
            int keep = n.TryGetProperty("keep", out var k) ? k.GetInt32()
                     : n.TryGetProperty("draw", out var dr) ? dr.GetInt32()
                     : 1;
            int discount = n.TryGetProperty("discount", out var d) ? d.GetInt32() : 0;
            bool toHand = n.TryGetProperty("to_hand", out var th) ? th.GetBoolean() : !hasCount;
            return new ScryEffect(look, keep, discount, toHand).WithTag("CardDraw");
        });

        // Return cards from discard to hand, then optionally draw
        // { "type": "return_from_discard", "count": n, "draw": m }
        RegisterEffect("return_from_discard", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            int draw = n.TryGetProperty("draw", out var d) ? d.GetInt32() : 0;
            return new ReturnFromDiscardEffect(count, draw).WithTag("CardDraw");
        });

        // Gain Charge equal to buffs on the target (min floor)
        // { "type": "gain_charge_per_buff", "min": n }
        RegisterEffect("gain_charge_per_buff", n =>
        {
            int min = n.TryGetProperty("min", out var m) ? m.GetInt32() : 1;
            return new GainChargePerBuffEffect(min).WithTag("Charge");
        });

        // Gain Charge scaled by keyword count (flat stand-in for now)
        // { "type": "gain_charge_per_keyword", "multiplier": n }
        RegisterEffect("gain_charge_per_keyword", n =>
        {
            int mult = n.TryGetProperty("multiplier", out var m) ? m.GetInt32() : 1;
            return new GainChargePerKeywordEffect(mult).WithTag("Charge");
        });

        // Movement + armor/shield/charge per spell cast this turn (Arcane Drift)
        // { "type": "move_per_spell_cast", "max": n, "armor_per": n, "shield_per": n, "charge_per": n }
        RegisterEffect("move_per_spell_cast", n =>
        {
            int max = n.TryGetProperty("max", out var mx) ? mx.GetInt32() : 4;
            int armorPer = n.TryGetProperty("armor_per", out var a) ? a.GetInt32() : 0;
            int shieldPer = n.TryGetProperty("shield_per", out var sh) ? sh.GetInt32() : 0;
            int chargePer = n.TryGetProperty("charge_per", out var cp) ? cp.GetInt32() : 0;
            return new MovePerSpellCastEffect(max, armorPer, shieldPer, chargePer).WithTag("Movement");
        });

        // Spend charge, deal flat damage, exile on lethal
        // { "type": "disintegrate", "damage": n, "charge_cost": n, "exile_on_lethal": bool }
        RegisterEffect("disintegrate", n =>
        {
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 14;
            int cost = n.TryGetProperty("charge_cost", out var c) ? c.GetInt32() : 3;
            bool exile = !n.TryGetProperty("exile_on_lethal", out var e) || e.GetBoolean();
            return new DisintegrateEffect(damage, cost, exile).WithTag("Damage");
        });

        // Queue bonus damage/draw/status onto the next N spells
        // { "type": "queue_next_spell_modifier", "bonus_damage": n, "extra_draw": n, "applies_to": 1 }
        RegisterEffect("queue_next_spell_modifier", n =>
        {
            int bd = n.TryGetProperty("bonus_damage", out var b) ? b.GetInt32() : 3;
            int ed = n.TryGetProperty("extra_draw", out var e) ? e.GetInt32() : 0;
            int at = n.TryGetProperty("applies_to", out var a) ? a.GetInt32() : 1;
            string gs = n.TryGetProperty("grant_status", out var g) ? g.GetString() : null;
            int sd = n.TryGetProperty("grant_status_duration", out var gsd) ? gsd.GetInt32() : 1;
            return new QueueNextSpellModifierLeafEffect(bd, ed, at, gs, sd).WithTag("Charge");
        });

        // Spells cost charge instead of mana for N turns
        // { "type": "charge_cost_modifier", "charge_per_mana": 1, "turns": n }
        RegisterEffect("charge_cost_modifier", n =>
        {
            int cpm = n.TryGetProperty("charge_per_mana", out var c) ? c.GetInt32() : 1;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2;
            return new ChargeCostModifierLeafEffect(cpm, turns).WithTag("Charge");
        });

        // Perfect chosen card(s): cost 0, +bonus on resolve, returns to hand on cast
        // (2026-07-29: real selection; previously a flat caster-wide damage buff that
        // read `bonus_damage`, a key no card ever authored: Magnum Opus writes
        // `bonus` and `count`, both of which were silently dropped.)
        // { "type": "perfect_card", "count": n, "bonus": n }
        RegisterEffect("perfect_card", n =>
        {
            int bonus = n.TryGetProperty("bonus", out var b) ? b.GetInt32()
                      : n.TryGetProperty("bonus_damage", out var bd) ? bd.GetInt32() : 3;
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            return new PerfectCardEffect(bonus, count).WithTag("Charge");
        });

        // All spells free for N turns; exile cards on expire
        // { "type": "omniscience", "turns": 1, "exile_on_expire": 3 }
        RegisterEffect("omniscience", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 1;
            int exile = n.TryGetProperty("exile_on_expire", out var e) ? e.GetInt32() : 3;
            return new OmniscienceLeafEffect(turns, exile).WithTag("Charge");
        });

        // Permanent: every spell cast generates charge
        // { "type": "arcane_apotheosis", "charge_per_spell": 1 }
        RegisterEffect("arcane_apotheosis", n =>
        {
            int cps = n.TryGetProperty("charge_per_spell", out var c) ? c.GetInt32() : 1;
            return new ArcaneApotheosisLeafEffect(cps).WithTag("Charge");
        });

        // Exile a card from hand; it auto-casts at start of each turn
        // { "type": "bind_card", "turns": n }
        RegisterEffect("bind_card", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new BindCardLeafEffect(turns).WithTag("Charge");
        });

        // Echo the next spell once after it resolves
        // { "type": "replicate_last_spell" }
        RegisterEffect("replicate_last_spell", _ =>
            new ReplicateLastSpellLeafEffect().WithTag("Charge"));

        // Cast the top N cards' top halves, in a player-chosen resolution order
        // (2026-07-29: `count` is finally read; Spell Storm authored count:3 and got 1)
        // { "type": "cast_deck_top", "count": n }
        RegisterEffect("cast_deck_top", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            return new CastDeckTopEffect(count).WithTag("CardDraw");
        });

        // Each spell pulses damage to nearest enemy for N turns
        // { "type": "convergence", "damage": n, "range": n, "turns": n }
        RegisterEffect("convergence", n =>
        {
            int dmg = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 3;
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 6;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new ConvergenceLeafEffect(dmg, range, turns).WithTag("Damage");
        });

        // ═══════════════════════════════════════════════════════════
        // ARCANIST: CONSTRUCTS
        // ═══════════════════════════════════════════════════════════

        // Summon an autonomous arcane construct
        // { "type": "create_arcane_construct", "unit": "ArcaneConstruct", "hp": n, "damage": n, "speed": n, "duration": n }
        RegisterEffect("create_arcane_construct", n =>
        {
            string kind = n.TryGetProperty("unit", out var u) ? u.GetString() : "ArcaneConstruct";
            int hp = n.TryGetProperty("hp", out var h) ? h.GetInt32() : 12;
            int dmg = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 4;
            int spd = n.TryGetProperty("speed", out var sp) ? sp.GetInt32() : 2;
            int dur = n.TryGetProperty("duration", out var du) ? du.GetInt32() : 0;
            return new CreateArcaneConstructEffect(kind, hp, dmg, spd, dur).WithTag("Summon");
        });

        // Summon a unit that embodies an exiled spell (auto-cast AI needs unit integration)
        // (2026-07-29: hp_per_mana / damage_per_mana are finally read; Living Spell
        // authored them and got flat 8HP/5DMG with no exile at all)
        // { "type": "summon_living_spell", "unit": "LivingSpell",
        //   "hp_per_mana": n, "damage_per_mana": n, "hp": n, "damage": n, "duration": n }
        RegisterEffect("summon_living_spell", n =>
        {
            string kind = n.TryGetProperty("unit", out var u) ? u.GetString() : "LivingSpell";
            int hp = n.TryGetProperty("hp", out var h) ? h.GetInt32() : 8;
            int dmg = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 5;
            int dur = n.TryGetProperty("duration", out var du) ? du.GetInt32() : 3;
            int hpm = n.TryGetProperty("hp_per_mana", out var hm) ? hm.GetInt32() : 0;
            int dpm = n.TryGetProperty("damage_per_mana", out var dp) ? dp.GetInt32() : 0;
            return new SummonLivingSpellEffect(kind, hp, dmg, dur, hpm, dpm).WithTag("Summon");
        });
    }
}
