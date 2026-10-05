using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// EnchanterNameCardEffects.cs
//
// Purpose:        Effects and predicates for the slice 13a Enchanter
//                 cards (class_identity_enchanter_v1 §3 B and C): the
//                 Geas-Binder (Names that punish choices) and the
//                 Puppeteer (who and where).
//                 • target_named / name_broken_recently predicates
//                 • misdirect_attack: Speak It Wrongly
//                 • borrow_will: Borrowed Will (choose its strike tile)
//                 • draw_on_break: Fine Print (Terms and Conditions)
//                 • compound_names: The Price Compounds
// Layer:          Effects
// Collaborators:  EnchanterEngine.cs (Names, NameCondition),
//                 IntentTime (CombatManager hooks for intents),
//                 CombatManager.Enchanter.cs (draws on break)
// See:            docs/class_identity_enchanter_v1.md §3
// ============================================================

/// <summary>True when the first target carries a living Name.
/// JSON: { "type": "target_named" }</summary>
public sealed class TargetNamedPredicate : IPredicate
{
    public bool Evaluate(PredicateContext ctx)
    {
        var items = ctx?.Targets?.Items;
        if (items == null)
            return false;
        foreach (var o in items)
        {
            var u = TargetingHelpers.ResolveUnit(ctx.Game, o);
            if (u != null && GodotObject.IsInstanceValid(u))
                return u.Names.Any(n => n.TurnsRemaining > 0);
        }
        return false;
    }
}

/// <summary>True when a Named unit broke its condition this round or the last.
/// JSON: { "type": "name_broken_recently" }</summary>
public sealed class NameBrokenRecentlyPredicate : IPredicate
{
    public bool Evaluate(PredicateContext ctx) => Names.BrokeRecently;
}

/// <summary>
/// Speak It Wrongly: Name each targeted enemy (it pays <see cref="Damage"/> if it attacks)
/// and move its locked attack onto the nearest other enemy. Who it hits changes; when it
/// lands does not.
/// JSON: { "type": "misdirect_attack", "damage": n, "duration": n }
/// </summary>
public sealed class MisdirectAttackEffect : EffectBase
{
    public int Damage, Duration;
    public MisdirectAttackEffect(int damage, int duration)
    { Damage = Math.Max(0, damage); Duration = Math.Max(1, duration); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets?.Items == null)
            return;
        var owner = s?.ActiveCasterUnit;
        int team = owner?.TeamId ?? 0;
        foreach (var o in targets.Items)
        {
            var u = ResolveTargetUnit(s, o);
            if (u == null || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.TeamId == team)
                continue;
            string err = IntentTime.RedirectLock(u);
            Names.Write(u, new NameCondition
            {
                Triggers = NameTrigger.Attack,
                Damage = Damage,
                TurnsRemaining = Duration,
                OwnerUnit = owner,
                OwnerTeam = team,
                Source = s.ResolvingAbilityName ?? "a Name",
                Misdirect = err == null,
            }, s.Log);
            if (err != null)
                s.Log($"[Misdirect] {u.Name}: {err}.");
        }
    }
}

/// <summary>
/// Borrowed Will: a non-elite enemy's next attack strikes the tile the player chose
/// (unit_then_tile with any_tile). It moves to reach it on its own turn.
/// JSON: { "type": "borrow_will" }
/// </summary>
public sealed class BorrowWillEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (!TwoStep.Read(s, targets, "BorrowedWill", out var victim, out var tile))
            return;
        string err = IntentTime.CommandAttack(victim, tile.Axial);
        if (err != null)
            s.Log($"[BorrowedWill] {victim.Name}: {err}.");
    }
}

/// <summary>
/// Fine Print: until your next turn, whenever a Named enemy breaks its condition, the
/// caster draws a card (up to <see cref="Cap"/>).
/// JSON: { "type": "draw_on_break", "cap": n }
/// </summary>
public sealed class DrawOnBreakEffect : EffectBase
{
    public int Cap;
    public DrawOnBreakEffect(int cap) { Cap = Math.Max(1, cap); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null)
            return;
        int left = Names.DrawOnBreak.TryGetValue(owner, out var cur) && cur.untilRound >= Names.Round ? cur.left : 0;
        Names.DrawOnBreak[owner] = (Names.Round, left + Cap);
        s.Log($"[FinePrint] Until {owner.Name}'s next turn, each broken Name draws a card (up to {left + Cap}).");
    }
}

/// <summary>
/// The Price Compounds: for the rest of the fight, each time a Name written by the
/// caster's team is broken, it lasts 1 more turn and its penalty grows by
/// <see cref="Growth"/>. Casting it again keeps the larger growth.
/// JSON: { "type": "compound_names", "growth": n }
/// </summary>
public sealed class CompoundNamesEffect : EffectBase
{
    public int Growth;
    public CompoundNamesEffect(int growth) { Growth = Math.Max(1, growth); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        int team = s?.ActiveCasterUnit?.TeamId ?? 0;
        int now = Names.CompoundGrowth.TryGetValue(team, out var g) ? Math.Max(g, Growth) : Growth;
        Names.CompoundGrowth[team] = now;
        s?.Log($"[PriceCompounds] For the rest of the fight, each broken Name lasts 1 more turn and costs {now} more.");
    }
}
