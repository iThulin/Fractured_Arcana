using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ChronoNewCardEffects.cs
//
// Purpose:        Effects for the 13 new Chronomancer cards
//                 (class_identity_chronomancer_v1 §3, slice 9):
//                 • Fatekeeper: per-Deferred Foresight, cancel a
//                   Deferred attack.
//                 • Long Game: delay or move one of your scheduled
//                   spells; Ephemeris (the first spell each turn is
//                   copied into the Almanac, see ChronoHooks).
//                 • Debtor: damage that scales with the Overdraft,
//                   repaying it, the "overdrawn" predicate.
//                 • Clockwright: damage-over-time resolved now,
//                   buffs lengthened, shields and buffs stripped,
//                   Foresight per status, Foresight per unit moved.
//                 • The Fixed Hour: permanent lookahead for every
//                   enemy plus one free Defer a round.
// Layer:          Effects
// Collaborators:  IntentTime (Defer/Cancel hooks), GameState.Almanac,
//                 FateAttunement, Unit (Ephemeris / Fixed Hour fields),
//                 RulesManager (ChronoHooks.OnSpellResolved),
//                 CombatManager.Actions.cs (free Defer button)
// See:            docs/class_identity_chronomancer_v1.md §3
// ============================================================

/// <summary>Statuses that help the unit carrying them (Hasten Decay's bottom half,
/// Wither the Hour). Kept explicit: InterfaceHelpers.Debuffs treats every unknown key as
/// a buff, which would count internal markers (wizard_charging, delayed).</summary>
internal static class ChronoStatuses
{
    public static readonly HashSet<string> Buffs = new()
    {
        "empowered", "hasted", "untargetable", "dancing", "immortal", "shrouded",
        "chaining", "fortified", "regenerating", "warded", "undying_turn",
    };
}

/// <summary>
/// Gain 1 Foresight for each Deferred attack on the board, capped.
/// JSON: { "type": "gain_foresight_per_deferred", "max": n }
/// </summary>
public sealed class GainForesightPerDeferredEffect : EffectBase
{
    public int Max;
    public GainForesightPerDeferredEffect(int max) { Max = Math.Max(1, max); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.ActiveCasterUnit?.Attunement is not FateAttunement fate || s.UnitsInPlay == null)
        {
            s?.Log("[ForesightPerDeferred] No Time Bank on the caster. No-op.");
            return;
        }
        int count = s.UnitsInPlay.Count(u => u != null && u.Stats.IsAlive && !u.IsPlayerControlled
                                             && u.AttackTiming == IntentTiming.Deferred);
        int gain = Math.Min(count, Max);
        if (gain > 0)
            fate.GainCharges(gain);
        s.Log($"[ForesightPerDeferred] {count} Deferred attack(s): +{gain} Foresight.");
    }
}

/// <summary>
/// Cancel target enemy's Deferred attack: it never lands. Spends Foresight, and needs it.
/// JSON: { "type": "cancel_deferred", "foresight_cost": n }
/// </summary>
public sealed class CancelDeferredEffect : EffectBase
{
    public int ForesightCost;
    public CancelDeferredEffect(int cost) { ForesightCost = Math.Max(0, cost); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var fate = s?.ActiveCasterUnit?.Attunement as FateAttunement;
        if (ForesightCost > 0 && (fate == null || fate.Charges < ForesightCost))
        {
            s?.Log($"[CancelDeferred] Needs {ForesightCost} Foresight. Nothing happens.");
            return;
        }

        bool any = false;
        if (targets?.Items != null)
        {
            foreach (var obj in targets.Items)
            {
                var enemy = ResolveTargetUnit(s, obj);
                if (enemy == null || enemy.IsPlayerControlled)
                    continue;
                string why = IntentTime.Cancel(enemy);
                if (why != null)
                    s.Log($"[CancelDeferred] {enemy.Name}: {why}");
                else
                    any = true;
            }
        }
        if (any && ForesightCost > 0)
            fate.SpendCharges(ForesightCost);
    }
}

/// <summary>Shared Almanac reads for the Long Game cards.</summary>
internal static class Schedules
{
    /// <summary>The caster's visible scheduled spells, soonest first.</summary>
    public static List<AlmanacEntry> Mine(GameState s, Entity caster) =>
        s?.Almanac == null ? new List<AlmanacEntry>()
        : s.Almanac.Where(e => e != null && !e.Hidden
                               && (e.Caster == caster || (e.CasterUnit != null && e.CasterUnit == s.ActiveCasterUnit)))
                   .OrderBy(e => e.TurnsRemaining).ToList();

    /// <summary>Where an entry lands: its targeted tile, or its targeted unit's tile.</summary>
    public static Vector2I? AimOf(AlmanacEntry e)
    {
        if (e?.Targets?.Items == null || e.Targets.Items.Count == 0)
            return null;
        return e.Targets.Items[0] switch
        {
            TileData td => td.Axial,
            Unit u when GodotObject.IsInstanceValid(u) && u.CurrentTile != null => u.CurrentTile.Axial,
            HexTile ht => ht.Axial,
            _ => null,
        };
    }
}

/// <summary>
/// Delay your soonest scheduled spell by N turns. It keeps maturing while it waits.
/// JSON: { "type": "delay_scheduled", "turns": n }
/// </summary>
public sealed class DelayScheduledEffect : EffectBase
{
    public int Turns;
    public DelayScheduledEffect(int turns) { Turns = Math.Max(1, turns); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var entry = Schedules.Mine(s, caster).FirstOrDefault();
        if (entry == null)
        {
            s?.Log("[DelayScheduled] You have nothing scheduled.");
            return;
        }
        entry.TurnsRemaining += Turns;
        s.Log($"[DelayScheduled] {entry.Label} now resolves in {entry.TurnsRemaining} turn(s).");
    }
}

/// <summary>
/// Re-aim one of your scheduled spells (or a foreseen strike) onto the target tile. It
/// must currently be aimed within <see cref="Reach"/> of that tile; the nearest wins.
/// JSON: { "type": "move_scheduled", "reach": n }
/// </summary>
public sealed class MoveScheduledEffect : EffectBase
{
    public int Reach;
    public MoveScheduledEffect(int reach) { Reach = Math.Max(1, reach); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        TileData dest = null;
        if (targets?.Items != null)
            foreach (var obj in targets.Items)
            {
                dest = InterfaceHelpers.ResolveTile(s, obj);
                if (dest != null)
                    break;
            }
        if (dest == null || s?.Grid == null)
        {
            s?.Log("[MoveScheduled] No tile chosen.");
            return;
        }

        AlmanacEntry bestEntry = null;
        DelayedDamageEffect bestStrike = null;
        int best = int.MaxValue;
        foreach (var e in Schedules.Mine(s, caster))
        {
            var aim = Schedules.AimOf(e);
            if (aim == null)
                continue;
            int d = s.Grid.Distance(aim.Value, dest.Axial);
            if (d > 0 && d <= Reach && d < best)
            {
                best = d;
                bestEntry = e;
            }
        }
        if (s.ActiveEffects != null)
        {
            foreach (var dd in s.ActiveEffects.OfType<DelayedDamageEffect>())
            {
                if (dd.IsExpired || dd.Owner != caster)
                    continue;
                int d = s.Grid.Distance(dd.TargetCoord, dest.Axial);
                if (d > 0 && d <= Reach && d < best)
                {
                    best = d;
                    bestEntry = null;
                    bestStrike = dd;
                }
            }
        }

        if (bestEntry != null)
        {
            bestEntry.Targets = new TargetSet { Items = new List<object> { dest } };
            s.Log($"[MoveScheduled] {bestEntry.Label} now lands on {dest.Axial}.");
        }
        else if (bestStrike != null)
        {
            bestStrike.TargetCoord = dest.Axial;
            s.Log($"[MoveScheduled] {bestStrike.Label ?? "The foreseen strike"} now lands on {dest.Axial}.");
        }
        else
        {
            s.Log($"[MoveScheduled] None of your scheduled spells is aimed within {Reach} of {dest.Axial}.");
        }
    }
}

/// <summary>
/// Ephemeris: for the rest of the fight, the first spell this unit casts each turn is
/// copied into the Almanac to resolve again at the start of its next turn.
/// JSON: { "type": "ephemeris" }
/// </summary>
public sealed class EphemerisEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var u = s?.ActiveCasterUnit;
        if (u == null)
            return;
        u.EphemerisActive = true;
        u.EphemerisFiredThisTurn = true;   // the Ephemeris cast itself is this turn's first
        s.Log($"[Ephemeris] {u.Name}'s first spell each turn will echo at the start of the next.");
    }
}

/// <summary>Runtime seams the resolver calls (RulesManager.ResolveTop).</summary>
internal static class ChronoHooks
{
    /// <summary>After a spell resolves: Ephemeris copies the turn's first player spell.</summary>
    public static void OnSpellResolved(GameState s, StackItem item)
    {
        var u = item?.CasterUnit;
        if (s == null || u == null || !GodotObject.IsInstanceValid(u) || !u.IsPlayerControlled)
            return;
        if (!u.EphemerisActive || u.EphemerisFiredThisTurn || s.EnemyPhaseContext)
            return;
        if (item.SourceCard == null || item.Ability?.Effects == null)
            return;
        if (item.Ability.Effects.Any(e => e is EphemerisEffect || e is ScheduleLastEffect))
            return;

        u.EphemerisFiredThisTurn = true;
        string name = string.IsNullOrEmpty(item.Ability.Name) ? "a spell" : item.Ability.Name;
        s.Almanac ??= new List<AlmanacEntry>();
        s.Almanac.Add(new AlmanacEntry
        {
            TurnsRemaining = 1,
            Child = new ReplayItemEffect(item, 1f),
            Caster = item.Caster,
            Targets = item.Targets,
            Snapshot = item.Snapshot,
            CasterUnit = u,
            Label = $"Ephemeris: {name}",
        });
        s.Log($"[Ephemeris] {name} will resolve again at the start of {u.Name}'s next turn.");
    }
}

/// <summary>
/// Damage that scales with the caster's Overdraft: <see cref="Per"/> per point below zero,
/// at least <see cref="Min"/>. Goes through DealDamageEffect, so every bonus applies.
/// JSON: { "type": "damage_per_overdraft", "per": n, "min": n }
/// </summary>
public sealed class DamagePerOverdraftEffect : EffectBase
{
    public int Per;
    public int Min;
    public DamagePerOverdraftEffect(int per, int min) { Per = Math.Max(1, per); Min = Math.Max(0, min); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        int debt = s?.ActiveCasterUnit?.Attunement is FateAttunement f ? Math.Max(0, -f.Charges) : 0;
        int amount = Math.Max(Min, Per * debt);
        s?.Log($"[CompoundInterest] Overdrawn {debt}: {amount} damage.");
        new DealDamageEffect(amount).Resolve(s, caster, targets, snap);
    }
}

/// <summary>
/// Repay up to N points of Overdraft (never takes the bank above 0).
/// JSON: { "type": "repay_overdraft", "amount": n }
/// </summary>
public sealed class RepayOverdraftEffect : EffectBase
{
    public int Amount;
    public RepayOverdraftEffect(int amount) { Amount = Math.Max(1, amount); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.ActiveCasterUnit?.Attunement is not FateAttunement fate || fate.Charges >= 0)
        {
            s?.Log("[Repay] You are not Overdrawn.");
            return;
        }
        int pay = Math.Min(Amount, -fate.Charges);
        fate.GainCharges(pay);
        s.Log($"[Repay] Repaid {pay}. The bank is at {fate.Charges}.");
    }
}

/// <summary>True when the caster's Time Bank is below zero.
/// JSON predicate: { "type": "overdrawn" }</summary>
public sealed class OverdrawnPredicate : IPredicate
{
    public bool Evaluate(PredicateContext ctx) =>
        ctx?.Game?.ActiveCasterUnit?.Attunement is FateAttunement f && f.IsOverdrawn;
}

/// <summary>
/// Every Burn and Bleed on the target lands all its remaining ticks now (Burn 3, Bleed 2
/// per turn left), plus <see cref="Bonus"/>, and ends.
/// JSON: { "type": "hasten_decay", "bonus": n }
/// </summary>
public sealed class HastenDecayEffect : EffectBase
{
    public int Bonus;
    public HastenDecayEffect(int bonus) { Bonus = Math.Max(0, bonus); }

    private static readonly (string status, int perTurn)[] Dots = { ("burn", 3), ("bleed", 2) };

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets?.Items == null)
            return;
        foreach (var obj in targets.Items)
        {
            var u = ResolveTargetUnit(s, obj);
            if (u == null || !u.Stats.IsAlive)
                continue;
            int total = 0;
            foreach (var (status, perTurn) in Dots)
            {
                if (!u.Stats.StatusEffects.TryGetValue(status, out int turns) || turns <= 0)
                    continue;
                total += perTurn * turns;
                u.RemoveStatus(status);
            }
            if (total == 0)
            {
                s.Log($"[HastenDecay] {u.Name} has no Burn or Bleed.");
                continue;
            }
            total += Bonus;
            s.Log($"[HastenDecay] {u.Name}'s decay comes due: {total} damage.");
            u.ApplyDamage(total);
        }
    }
}

/// <summary>
/// Each buff on the target ally lasts N more turns.
/// JSON: { "type": "extend_buffs", "turns": n }
/// </summary>
public sealed class ExtendBuffsEffect : EffectBase
{
    public int Turns;
    public ExtendBuffsEffect(int turns) { Turns = Math.Max(1, turns); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        int team = s?.ActiveCasterUnit?.TeamId ?? 0;
        if (targets?.Items == null)
            return;
        foreach (var obj in targets.Items)
        {
            var u = ResolveTargetUnit(s, obj);
            if (u == null || u.TeamId != team)
                continue;
            var keys = u.Stats.StatusEffects.Keys.Where(k => ChronoStatuses.Buffs.Contains(k)).ToList();
            foreach (var k in keys)
                u.Stats.StatusEffects[k] += Turns;
            u.RefreshHealthBar();
            s.Log(keys.Count == 0 ? $"[ExtendBuffs] {u.Name} has no buffs."
                                  : $"[ExtendBuffs] {u.Name}: {string.Join(", ", keys)} last {Turns} more turn(s).");
        }
    }
}

/// <summary>
/// The target loses all its shield and every buff.
/// JSON: { "type": "wither" }
/// </summary>
public sealed class WitherEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets?.Items == null)
            return;
        foreach (var obj in targets.Items)
        {
            var u = ResolveTargetUnit(s, obj);
            if (u == null || !u.Stats.IsAlive)
                continue;
            int shield = u.Stats.Shield;
            u.Stats.Shield = 0;
            var keys = u.Stats.StatusEffects.Keys.Where(k => ChronoStatuses.Buffs.Contains(k)).ToList();
            foreach (var k in keys)
                u.RemoveStatus(k);
            u.RefreshHealthBar();
            s.Log($"[Wither] {u.Name} loses {shield} shield" + (keys.Count > 0 ? $" and {string.Join(", ", keys)}." : "."));
        }
    }
}

/// <summary>
/// Gain 1 Foresight for each status on the target, capped.
/// JSON: { "type": "gain_foresight_per_status", "max": n }
/// </summary>
public sealed class GainForesightPerStatusEffect : EffectBase
{
    public int Max;
    public GainForesightPerStatusEffect(int max) { Max = Math.Max(1, max); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.ActiveCasterUnit?.Attunement is not FateAttunement fate)
        {
            s?.Log("[ForesightPerStatus] No Time Bank on the caster. No-op.");
            return;
        }
        Unit u = null;
        if (targets?.Items != null)
            foreach (var obj in targets.Items)
                if ((u = ResolveTargetUnit(s, obj)) != null)
                    break;
        int count = u?.Stats.StatusEffects.Count(kv => kv.Value > 0) ?? 0;
        int gain = Math.Min(count, Max);
        if (gain > 0)
            fate.GainCharges(gain);
        s.Log($"[ForesightPerStatus] {count} status(es): +{gain} Foresight.");
    }
}

/// <summary>
/// Gain 1 Foresight for each unit standing somewhere other than where it began the round.
/// JSON: { "type": "gain_foresight_per_moved", "max": n }
/// </summary>
public sealed class GainForesightPerMovedEffect : EffectBase
{
    public int Max;
    public GainForesightPerMovedEffect(int max) { Max = Math.Max(1, max); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.ActiveCasterUnit?.Attunement is not FateAttunement fate || s.UnitsInPlay == null)
        {
            s?.Log("[ForesightPerMoved] No Time Bank on the caster. No-op.");
            return;
        }
        int count = s.UnitsInPlay.Count(u => u != null && u.Stats.IsAlive && u.CurrentTile != null
                                             && u.RoundStartTile.HasValue && u.CurrentTile.Axial != u.RoundStartTile.Value);
        int gain = Math.Min(count, Max);
        if (gain > 0)
            fate.GainCharges(gain);
        s.Log($"[ForesightPerMoved] {count} unit(s) moved this round: +{gain} Foresight.");
    }
}

/// <summary>
/// The Fixed Hour: for the rest of the fight every enemy's intent and next intent stay
/// revealed (new plans included), and the caster gets a free Defer once per round
/// (action bar).
/// JSON: { "type": "fixed_hour" }
/// </summary>
public sealed class FixedHourEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s == null)
            return;
        s.FixedHourActive = true;
        if (s.ActiveCasterUnit != null)
            s.ActiveCasterUnit.HasFixedHour = true;
        if (s.UnitsInPlay != null)
            foreach (var u in s.UnitsInPlay)
                if (u != null && u.Stats.IsAlive && !u.IsPlayerControlled)
                    IntentTime.Reveal(u, lookahead: true, permanent: true);
        s.Log("[FixedHour] The hour is fixed: every enemy's next two intents stay visible for the rest of the fight.");
    }
}
