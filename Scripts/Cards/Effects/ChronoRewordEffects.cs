using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ChronoRewordEffects.cs
//
// Purpose:        Effects added for the Chronomancer rewording pass
//                 (class_identity_chronomancer_v1 §4, slice 8):
//                 • ScheduleLastEffect: schedule a copy of your last
//                   resolved spell (Afterimage). The copy is captured
//                   now, so it replays THAT spell with its targets, not
//                   whatever happens to be last when it fires.
//                 • GainForesightPerScheduledEffect: +1 Foresight per
//                   spell you have scheduled, capped (Recurrence).
//                 • ForesightAtLeastPredicate: "if you have N+
//                   Foresight" (Split Second, Paradox Bolt). Replaces
//                   the spells-cast-this-turn conditions the doc removes.
// Layer:          Effects
// Collaborators:  GameState.Almanac / AlmanacEntry (Fire, maturity),
//                 DelayedDamageEffect (scheduled strikes count too),
//                 FateAttunement
// See:            docs/class_identity_chronomancer_v1.md §4
// ============================================================

/// <summary>
/// Schedules a copy of the last resolved spell to resolve again in <see cref="Turns"/>
/// turns (Almanac; fires at the start of that turn and matures like any schedule).
/// JSON: { "type": "schedule_last", "turns": n, "value_mult": f }
/// </summary>
public sealed class ScheduleLastEffect : EffectBase
{
    public int Turns;
    public float ValueMult;

    public ScheduleLastEffect(int turns, float valueMult)
    {
        Turns = Math.Max(1, turns);
        ValueMult = valueMult <= 0f ? 1f : valueMult;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var last = s?.LastResolvedItem;
        if (last?.Ability?.Effects == null)
        {
            s?.Log("[ScheduleLast] No spell has resolved yet. Nothing to copy.");
            return;
        }
        // "Your last spell": in an enemy priority window the last resolved item can be
        // an enemy's, which a copy must not replay as yours.
        if (last.CasterUnit != null && !last.CasterUnit.IsPlayerControlled)
        {
            s.Log("[ScheduleLast] The last spell was not one of yours. Nothing to copy.");
            return;
        }
        // A copy of a copy would chain forever (Afterimage scheduling Afterimage).
        if (last.Ability.Effects.Any(e => e is ScheduleLastEffect))
        {
            s.Log("[ScheduleLast] The last spell schedules copies itself. Not copied.");
            return;
        }

        string name = string.IsNullOrEmpty(last.Ability.Name) ? "a spell" : last.Ability.Name;
        s.Almanac ??= new List<AlmanacEntry>();
        s.Almanac.Add(new AlmanacEntry
        {
            TurnsRemaining = Turns,
            Child = new ReplayItemEffect(last, ValueMult),
            Caster = last.Caster ?? caster,
            Targets = last.Targets,
            Snapshot = snap,
            CasterUnit = s.ActiveCasterUnit,
            Label = $"Afterimage: {name}",
        });
        s.Log($"[ScheduleLast] A copy of {name} resolves in {Turns} turn(s).");
    }
}

/// <summary>Replays a captured stack item's effects on its own targets. Internal
/// (ScheduleLastEffect's child); not in the JSON registry.</summary>
public sealed class ReplayItemEffect : EffectBase
{
    private readonly StackItem _item;
    private readonly float _mult;

    public ReplayItemEffect(StackItem item, float mult)
    {
        _item = item;
        _mult = mult;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (_item?.Ability?.Effects == null)
            return;
        var copySnap = new EffectSnapshot { DamageMultiplier = _mult };
        s.Log($"[Afterimage] {_item.Ability.Name} resolves again" + (_mult < 1f ? $" at {_mult * 100f:0}% value." : "."));
        foreach (var eff in _item.Ability.Effects)
            eff.Resolve(s, _item.Caster ?? caster, _item.Targets ?? targets, copySnap);
    }
}

/// <summary>
/// Gain 1 Foresight for each spell the caster has scheduled: visible Almanac entries it
/// cast, plus its foreseen strikes on tiles. Capped at <see cref="Max"/>.
/// JSON: { "type": "gain_foresight_per_scheduled", "max": n }
/// </summary>
public sealed class GainForesightPerScheduledEffect : EffectBase
{
    public int Max;
    public GainForesightPerScheduledEffect(int max) { Max = Math.Max(1, max); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var unit = s?.ActiveCasterUnit;
        if (unit?.Attunement is not FateAttunement fate)
        {
            s?.Log("[ForesightPerScheduled] No Time Bank on the caster. No-op.");
            return;
        }

        int count = 0;
        if (s.Almanac != null)
            count += s.Almanac.Count(e => e != null && !e.Hidden && (e.CasterUnit == unit || e.Caster == caster));
        if (s.ActiveEffects != null)
            count += s.ActiveEffects.OfType<DelayedDamageEffect>().Count(d => !d.IsExpired && d.Owner == caster);

        int gain = Math.Min(count, Max);
        if (gain > 0)
            fate.GainCharges(gain);
        s.Log($"[ForesightPerScheduled] {count} scheduled spell(s): +{gain} Foresight.");
    }
}

/// <summary>
/// True when the caster's Time Bank holds at least <see cref="Value"/> Foresight.
/// JSON predicate: { "type": "foresight_at_least", "value": n }
/// </summary>
public sealed class ForesightAtLeastPredicate : IPredicate
{
    public int Value;
    public ForesightAtLeastPredicate(int value) { Value = value; }

    public bool Evaluate(PredicateContext ctx)
    {
        var unit = ctx?.Game?.ActiveCasterUnit;
        return unit?.Attunement is FateAttunement fate && fate.Charges >= Value;
    }
}
