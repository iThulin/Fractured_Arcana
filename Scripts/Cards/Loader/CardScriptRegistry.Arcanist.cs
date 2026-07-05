using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Arcanist.cs
//
// Purpose:        Arcanist school effect registrations — maps the
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
            int look = n.TryGetProperty("look", out var l) ? l.GetInt32() : 3;
            int keep = n.TryGetProperty("keep", out var k) ? k.GetInt32() : 1;
            int discount = n.TryGetProperty("discount", out var d) ? d.GetInt32() : 0;
            return new ScryEffect(look, keep, discount).WithTag("CardDraw");
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

        // Grant armor/shield per spell cast this turn
        // { "type": "move_per_spell_cast", "max": n, "armor_per": n, "shield_per": n }
        RegisterEffect("move_per_spell_cast", n =>
        {
            int max = n.TryGetProperty("max", out var mx) ? mx.GetInt32() : 4;
            int armorPer = n.TryGetProperty("armor_per", out var a) ? a.GetInt32() : 0;
            int shieldPer = n.TryGetProperty("shield_per", out var sh) ? sh.GetInt32() : 0;
            return new MovePerSpellCastEffect(max, armorPer, shieldPer).WithTag("Movement");
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

        // Permanent spell-damage boost (full card-selection UI pending)
        // { "type": "perfect_card", "bonus_damage": n, "draw": n }
        RegisterEffect("perfect_card", n =>
        {
            int bd = n.TryGetProperty("bonus_damage", out var b) ? b.GetInt32() : 3;
            int draw = n.TryGetProperty("draw", out var d) ? d.GetInt32() : 0;
            return new PerfectCardEffect(bd, draw).WithTag("Charge");
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

        // Immediately resolve the top card of the deck
        // { "type": "cast_deck_top" }
        RegisterEffect("cast_deck_top", _ =>
            new CastDeckTopEffect().WithTag("CardDraw"));

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
        // ARCANIST — CONSTRUCTS
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

        // Summon a unit that embodies a spell (auto-cast AI needs unit integration)
        // { "type": "summon_living_spell", "unit": "LivingSpell", "hp": n, "damage": n, "duration": n }
        RegisterEffect("summon_living_spell", n =>
        {
            string kind = n.TryGetProperty("unit", out var u) ? u.GetString() : "LivingSpell";
            int hp = n.TryGetProperty("hp", out var h) ? h.GetInt32() : 8;
            int dmg = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 5;
            int dur = n.TryGetProperty("duration", out var du) ? du.GetInt32() : 3;
            return new SummonLivingSpellEffect(kind, hp, dmg, dur).WithTag("Summon");
        });
    }
}
