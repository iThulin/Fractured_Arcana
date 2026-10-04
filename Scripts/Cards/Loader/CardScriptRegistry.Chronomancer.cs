using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Chronomancer.cs
//
// Purpose:        Chronomancer school effect registrations. Maps the
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
            //   look/keep/discount  (Chronomancer). Read correctly.
            //   look/draw           (Arcanist). `draw` was NEVER READ, so
            //                         {"look":4,"draw":2} silently became keep=1.
            //   count               (Worldshaper). NEITHER key was read, so
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
        // { "type": "peek_intent", "range": n, "lookahead": bool, "permanent": bool }
        // range 0 (default) = every enemy; lookahead also shows the NEXT intent;
        // permanent keeps it revealed for the rest of the fight.
        RegisterEffect("peek_intent", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 0;
            bool look = n.TryGetProperty("lookahead", out var lk) && lk.GetBoolean();
            bool perm = n.TryGetProperty("permanent", out var pm) && pm.GetBoolean();
            var peek = new PeekIntentEffect(range, look, perm);
            peek.TargetOnly = n.TryGetProperty("target_only", out var to) && to.GetBoolean();
            return peek.WithTag("Foresight");
        });

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

        // Defer target enemy's attack: it lands at the end of the round on its locked tile.
        // radius widens it to every enemy within n of the target (99 = every enemy).
        // { "type": "defer_intent", "radius": n, "aimed_within": n, "near_decoy": bool }
        RegisterEffect("defer_intent", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 0;
            var eff = new DeferIntentEffect(radius);
            if (n.TryGetProperty("aimed_within", out var aw))
                eff.AimedWithin = aw.GetInt32();
            eff.NearDecoy = n.TryGetProperty("near_decoy", out var nd) && nd.GetBoolean();
            if (n.TryGetProperty("foresight_per", out var fp))
                eff.ForesightPer = fp.GetInt32();
            return eff.WithTag("Control");
        });

        // Afterimage: schedule a copy of the last resolved spell.
        // { "type": "schedule_last", "turns": n, "value_mult": f }
        RegisterEffect("schedule_last", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 1;
            float mult = n.TryGetProperty("value_mult", out var m) ? (float)m.GetDouble() : 1f;
            return new ScheduleLastEffect(turns, mult).WithTag("Foresight");
        });

        // Recurrence: +1 Foresight per scheduled spell, capped.
        // { "type": "gain_foresight_per_scheduled", "max": n }
        RegisterEffect("gain_foresight_per_scheduled", n =>
        {
            int max = n.TryGetProperty("max", out var m) ? m.GetInt32() : 3;
            return new GainForesightPerScheduledEffect(max).WithTag("Foresight");
        });

        // Advance target enemy's attack: it lands now on its locked tile.
        // { "type": "advance_intent", "radius": n }
        RegisterEffect("advance_intent", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 0;
            var adv = new AdvanceIntentEffect(radius)
            {
                DeferredOnly = n.TryGetProperty("deferred_only", out var d) && d.GetBoolean(),
                ReverseSpeed = n.TryGetProperty("reverse_speed", out var rs) && rs.GetBoolean(),
            };
            return adv.WithTag("Control");
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

        // Fire your soonest scheduled spell(s) now.
        // { "type": "fast_forward", "count": n (99 = all), "foresight_cost": n (default 1) }
        RegisterEffect("fast_forward", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            int cost = n.TryGetProperty("foresight_cost", out var fc) ? fc.GetInt32() : 1;
            return new FastForwardEffect(count, cost);
        });

        // Create a temporal anchor at the target location that you can teleport back to, optionally with a duration after which it expires
        // { "type": "set_anchor", "turns": n, "heal": n, "shield": n, "foresight": n, "lethal_save": bool }
        RegisterEffect("set_anchor", n =>
        {
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2;
            int heal = n.TryGetProperty("heal", out var h) ? h.GetInt32() : 0;
            int shield = n.TryGetProperty("shield", out var sh) ? sh.GetInt32() : 0;
            int foresight = n.TryGetProperty("foresight", out var f) ? f.GetInt32() : 0;
            bool lethal = n.TryGetProperty("lethal_save", out var l) && l.GetBoolean();
            return new SetAnchorEffect(turns, heal, shield, foresight, lethal);
        });

        RegisterEffect("teleport_to_anchor", _ => new TeleportToAnchorEffect());

        // Create temporary tiles that trigger effects when stepped on, optionally with a duration after which they expire
        // { "type": "create_phase_tiles", "count": n, "turns": n, "step_foresight": n }
        RegisterEffect("create_phase_tiles", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 2;
            int turns = n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3;
            int stepForesight = n.TryGetProperty("step_foresight", out var sf) ? sf.GetInt32() : 0;
            return new CreatePhaseTilesEffect(count, turns, stepForesight);
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

        // Overdrawn Time Bank (2026-10-03, replaces mana_debt): lose Foresight, may go below 0.
        // { "type": "lose_foresight", "amount": n }
        RegisterEffect("lose_foresight", n =>
        {
            int amount = n.TryGetProperty("amount", out var a) ? a.GetInt32() : 1;
            var lose = new LoseForesightEffect(amount);
            if (n.TryGetProperty("to_overdraft", out var to))
                lose.ToOverdraft = to.GetInt32();
            return lose.WithTag("Foresight");
        });

        // Round-start snapshot (2026-10-03): back to the tile (and HP) held when the round began.
        // { "type": "return_to_round_start", "restore_hp": bool, "allies_only": bool,
        //   "all_allies": bool, "radius": n, "collision_damage": n, "foresight_cost": n }
        RegisterEffect("return_to_round_start", n => new ReturnToRoundStartEffect
        {
            RestoreHp = n.TryGetProperty("restore_hp", out var rh) && rh.GetBoolean(),
            AlliesOnly = n.TryGetProperty("allies_only", out var ao) && ao.GetBoolean(),
            AllAllies = n.TryGetProperty("all_allies", out var aa) && aa.GetBoolean(),
            Radius = n.TryGetProperty("radius", out var ra) ? ra.GetInt32() : 0,
            CollisionDamage = n.TryGetProperty("collision_damage", out var cd) ? cd.GetInt32() : 0,
            ForesightCost = n.TryGetProperty("foresight_cost", out var fc) ? fc.GetInt32() : 0,
            AllUnits = n.TryGetProperty("all_units", out var au) && au.GetBoolean(),
        });

        // ── The 13 new cards (chronomancer §3, slice 9) ─────────────────────
        static int I(System.Text.Json.JsonElement n, string k, int d) => n.TryGetProperty(k, out var v) ? v.GetInt32() : d;
        RegisterEffect("gain_foresight_per_deferred", n => new GainForesightPerDeferredEffect(I(n, "max", 3)).WithTag("Foresight"));
        RegisterEffect("cancel_deferred", n => new CancelDeferredEffect(I(n, "foresight_cost", 2)).WithTag("Control"));
        RegisterEffect("delay_scheduled", n => new DelayScheduledEffect(I(n, "turns", 1)).WithTag("Foresight"));
        RegisterEffect("move_scheduled", n => new MoveScheduledEffect(I(n, "reach", 1)).WithTag("Foresight"));
        RegisterEffect("ephemeris", _ => new EphemerisEffect().WithTag("Foresight"));
        RegisterEffect("damage_per_overdraft", n => new DamagePerOverdraftEffect(I(n, "per", 3), I(n, "min", 3)).WithTag("Damage"));
        RegisterEffect("repay_overdraft", n => new RepayOverdraftEffect(I(n, "amount", 1)).WithTag("Foresight"));
        RegisterEffect("hasten_decay", n => new HastenDecayEffect(I(n, "bonus", 0)).WithTag("Damage"));
        RegisterEffect("extend_buffs", n => new ExtendBuffsEffect(I(n, "turns", 1)).WithTag("Status"));
        RegisterEffect("wither", _ => new WitherEffect().WithTag("Status"));
        RegisterEffect("gain_foresight_per_status", n => new GainForesightPerStatusEffect(I(n, "max", 3)).WithTag("Foresight"));
        RegisterEffect("gain_foresight_per_moved", n => new GainForesightPerMovedEffect(I(n, "max", 3)).WithTag("Foresight"));
        RegisterEffect("fixed_hour", _ => new FixedHourEffect().WithTag("Foresight"));
    }
}
