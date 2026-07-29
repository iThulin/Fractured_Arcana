using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Chronomancer.cs
//
// Purpose:        Chronomancer school effect registrations — maps the
//                 school's JSON `type` keys to effect factories.
//                 Called from CardScriptRegistry.RegisterBuiltins().
// Layer:          Loader
// Collaborators:  ChronomancerEffects.cs (the effect classes),
//                 JsonCardLoader.cs (registry infrastructure)
// ============================================================

public static partial class CardScriptRegistry
{
    /// <summary>Registers all Chronomancer-school effect factories.</summary>
    private static void RegisterChronomancerEffects()
    {
        // ═══════════════════════════════════════════════════════════
        // CHRONOMANCER EFFECTS
        // ═══════════════════════════════════════════════════════════

        // Gain foresight stacks to manipulate turn order and card timing
        // { "type": "gain_foresight", "amount": n }
        RegisterEffect("gain_foresight", n =>
        {
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 1;
            return new GainForesightEffect(amount).WithTag("Foresight");
        });

        // Scry: look at top N, keep M, bottom the rest; optionally gain mana discount on kept cards
        // { "type": "scry", "look": n, "keep": m, "discount": n }
        RegisterEffect("scry", n =>
        {
            // Three param vocabularies exist in the authored cards and, until
            // 2026-07-28, this factory understood exactly one of them:
            //   look/keep/discount  — Chronomancer. Read correctly.
            //   look/draw           — Arcanist. `draw` was NEVER READ, so
            //                         {"look":4,"draw":2} silently became keep=1.
            //   count               — Worldshaper. NEITHER key was read, so
            //                         {"count":2} silently became look=3, keep=1.
            // All three are honoured now. `draw` is a straight alias for `keep`.
            // `count` is the REORDER form: look at N, put 1 back on TOP, bottom the
            // rest — nothing goes to hand.
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

        // Delay damage from the next enemy attack by N turns, then take it all at once
        // { "type": "delayed_damage", "amount": n, "turns": n }
        RegisterEffect("delayed_damage", n =>
        {
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 4;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new DelayedDamageLeafEffect(amount, turns).WithTag("Damage");
        });

        // Peek at the next N enemy intents, optionally with a mana cost reduction on cards that interact with them
        // { "type": "peek_intent", "amount": n, "discount": n }
        RegisterEffect("peek_intent", _ => new PeekIntentEffect().WithTag("Foresight"));

        // Temporary buff to a specific stat for a number of turns
        // { "type": "temp_buff", "stat": "movement", "amount": n, "turns": n }
        RegisterEffect("temp_buff", n =>
        {
            string stat = n.TryGetProperty("stat", out var s) ? s.GetString() : "movement";
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 2;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 1;
            return new TempBuffEffect(stat, amount, turns).WithTag("Buff");
        });

        // Modify the cost of the next spell(s) in hand, optionally with a scope for which spells it applies to
        // { "type": "cost_modify", "amount": n, "scope": "self_next" }
        RegisterEffect("cost_modify", n =>
        {
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 1;
            string scope = n.TryGetProperty("scope", out var s) ? s.GetString() : "self_next";
            return new CostModifyEffect(amount, scope).WithTag("Foresight");
        });

        // Postpone the next N turns of the target (enemy or self), causing their next N actions to be delayed until after the current turn ends
        // { "type": "postpone", "turns": n }
        RegisterEffect("postpone", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 1;
            return new PostponeEffect(turns);
        });

        // Skip the next N turns of the enemy, causing them to lose their next N actions (can be used on self for a "stasis" effect)
        // { "type": "skip_enemy_turn", "turns": n }
        RegisterEffect("skip_enemy_turn", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 1;
            return new SkipEnemyTurnEffect(turns);
        });

        // Schedule an effect to occur after a delay, allowing for interesting combos and setups
        // { "type": "schedule", "turns": n, "do": {...} }
        RegisterEffect("schedule", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 1;
            IEffect child = n.TryGetProperty("do", out var d) ? BuildEffect(d) : new EmptyEffect();
            return new ScheduleLeafEffect(turns, child);
        });

        RegisterEffect("advance", _ => new AdvanceEffect());

        RegisterEffect("fast_forward", _ => new FastForwardEffect());

        // Create a temporal anchor at the target location that you can teleport back to, optionally with a duration after which it expires
        // { "type": "set_anchor", "turns": n }
        RegisterEffect("set_anchor", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2;
            return new SetAnchorEffect(turns);
        });

        RegisterEffect("teleport_to_anchor", _ => new TeleportToAnchorEffect());

        // Create temporary tiles that trigger effects when stepped on, optionally with a duration after which they expire
        // { "type": "create_phase_tiles", "count": n, "turns": n }
        RegisterEffect("create_phase_tiles", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 2;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new CreatePhaseTilesEffect(count, turns);
        });

        RegisterEffect("teleport_to_phase_tile", _ => new TeleportToPhaseTileEffect());

        // After casting, immediately take another turn with the same cards in hand (some hardcoded interactions to prevent infinite loops, needs more work)
        // { "type": "take_extra_turn", "mana": n, "draw": n }
        RegisterEffect("echo_last", n =>
        {
            float mult = n.TryGetProperty("value_mult", out var v) ? (float)v.GetDouble() : 0.5f;
            return new EchoLastEffect(mult);
        });

        // Rewind time to the start of the current turn, optionally retargeting spells that were cast after the rewind point
        // { "type": "rewind_last", "retarget": bool }
        RegisterEffect("rewind_last", n =>
        {
            bool retarget = n.TryGetProperty("retarget", out var r) && r.GetBoolean();
            return new RewindLastEffect(retarget);
        });

        RegisterEffect("reverse_stack", _ => new ReverseStackEffect());

        // Redirect a spell or enemy attack to a different target, with various targeting options
        // { "type": "redirect", "to": "random_enemy" }
        RegisterEffect("redirect", n =>
        {
            string to = n.TryGetProperty("to", out var t) ? t.GetString() : "random_enemy";
            return new RedirectEffect(to);
        });

        // Redirect a charge to a different target, with various targeting options
        // { "type": "redirect_charge", "to": "chosen" }
        RegisterEffect("redirect_charge", n =>
        {
            string to = n.TryGetProperty("to", out var t) ? t.GetString() : "chosen";
            return new RedirectChargeEffect(to);
        });

        // Redirect all spells and attacks to a different target for a number of turns, with various targeting options
        // { "type": "redirect_all", "to": "random_enemy", "turns": n }
        RegisterEffect("redirect_all", n =>
        {
            string to = n.TryGetProperty("to", out var t) ? t.GetString() : "random_enemy";
            int turns = n.TryGetProperty("turns", out var d) ? d.GetInt32() : 1;
            return new RedirectAllEffect(to, turns);
        });

        // Take an extra turn immediately after this one, optionally with a mana bonus and card draw
        // { "type": "extra_turn", "mana": n, "draw": n }
        RegisterEffect("extra_turn", n =>
        {
            int mana = n.TryGetProperty("mana", out var m) ? m.GetInt32() : 2;
            int draw = n.TryGetProperty("draw", out var d) ? d.GetInt32() : 1;
            return new ExtraTurnLeafEffect(mana, draw);
        });

        // Summon a decoy that draws enemy attacks for a number of turns, optionally with its own HP pool
        // { "type": "summon_decoy", "hp": n, "turns": n }
        RegisterEffect("summon_decoy", n =>
        {
            int hp = n.TryGetProperty("hp", out var h) ? h.GetInt32() : 10;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new SummonDecoyLeafEffect(hp, turns);
        });

        // Redirect all attacks against the target to other valid targets within a radius for a number of turns
        // { "type": "redirect_aura", "radius": n, "turns": n }
        RegisterEffect("redirect_aura", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 2;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            return new RedirectAuraLeafEffect(radius, turns);
        });

        // Temporal decay field: { "type": "temporal_decay_field", "damage": n, "scaling": n }
        RegisterEffect("temporal_decay_field", n =>
        {
            int damage = n.TryGetProperty("damage", out var d) ? d.GetInt32() : 4;
            int scaling = n.TryGetProperty("scaling", out var s) ? s.GetInt32() : 2;
            return new TemporalDecayFieldLeafEffect(damage, scaling);
        });

        RegisterEffect("event_control", _ => new EventControlLeafEffect());
    }
}
