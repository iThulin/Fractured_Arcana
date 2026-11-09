using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ArcanistAnnotatorEffects.cs
//
// Purpose:        The Annotator (class_identity_arcanist_v1 §3C, slice 14b):
//                 the Arcanist's answer to an enemy spell is to change what
//                 it does.
//                 • strike_clause: the target's next attack or spell loses its
//                   rider (status, push, imbue); only its damage lands
//                 • red_ink: the next enemy spell landing within 3 of you
//                   deals less, and you gain that much Charge
//                 • purloin: steal an enemy caster's telegraphed spell and cast
//                   it yourself, centred on a tile you choose
//                 • blot_next_spell: the target's next spell deals less
//                 • target_has_buff predicate
// Layer:          Effects
// Collaborators:  IntentTime (PurloinHook), CombatManager.Annotator.cs
//                 (the enemy-side reads), StatusCatalog (the chips)
// See:            docs/class_identity_arcanist_v1.md §3C and §5
// ============================================================

/// <summary>What Purloin takes from an enemy caster: its damage, its footprint as offsets
/// from the aimed tile, its rider and, for an imbue, its element.</summary>
public sealed class StolenSpell
{
    public string Name = "a spell";
    public int Damage;
    public List<Vector2I> Offsets = new() { Vector2I.Zero };
    public string Status;
    public int StatusTurns;
    public TileElementType Element = TileElementType.None;
}

/// <summary>Target enemy's next attack or spell loses its rider. Only its damage remains.
/// JSON: { "type": "strike_clause" }</summary>
public sealed class StrikeClauseEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets?.Items == null || CombatSim.Active)
            return;
        int team = s.ActiveCasterUnit?.TeamId ?? 0;
        foreach (var o in targets.Items)
        {
            var u = ResolveTargetUnit(s, o);
            if (u == null || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.TeamId == team)
                continue;
            u.ApplyStatus("clause_struck", 99);
            u.RefreshHealthBar();
            s.Log($"[StrikeTheClause] {u.Name}'s next attack loses its rider.");
        }
    }
}

/// <summary>Until your next turn, the first enemy spell that lands within 3 of you deals
/// <see cref="Amount"/> less, and you gain Charge equal to what was cut.
/// JSON: { "type": "red_ink", "amount": n }</summary>
public sealed class RedInkEffect : EffectBase
{
    public int Amount;
    public RedInkEffect(int amount) { Amount = Math.Max(1, amount); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null || CombatSim.Active)
            return;
        owner.RedInkAmount = Math.Max(owner.RedInkAmount, Amount);
        owner.ApplyStatus("red_ink", 1);
        owner.RefreshHealthBar();
        s.Log($"[RedInk] {owner.Name} readies the red pen: the next enemy spell within 3 deals {owner.RedInkAmount} less.");
    }
}

/// <summary>Target enemy's next spell deals <see cref="Amount"/> less damage.
/// JSON: { "type": "blot_next_spell", "amount": n }</summary>
public sealed class BlotNextSpellEffect : EffectBase
{
    public int Amount;
    public BlotNextSpellEffect(int amount) { Amount = Math.Max(1, amount); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets?.Items == null || CombatSim.Active)
            return;
        int team = s.ActiveCasterUnit?.TeamId ?? 0;
        foreach (var o in targets.Items)
        {
            var u = ResolveTargetUnit(s, o);
            if (u == null || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.TeamId == team)
                continue;
            u.NextSpellPenalty += Amount;
            u.ApplyStatus("ink_blot", 99);
            u.RefreshHealthBar();
            s.Log($"[Blot] {u.Name}'s next spell deals {u.NextSpellPenalty} less.");
        }
    }
}

/// <summary>
/// Purloin: steal the telegraphed spell of the chosen enemy caster (a Channel, Release or
/// Imbue intent). It never lands. You cast it now, its footprint centred on the chosen
/// tile: its damage (plus <see cref="Bonus"/>) and rider land on enemies there, and an
/// imbue writes its element instead. unit_then_tile with from_caster and any_tile.
/// JSON: { "type": "purloin", "bonus": n, "charge": n }
/// </summary>
public sealed class PurloinEffect : EffectBase
{
    public int Bonus, Charge;
    public PurloinEffect(int bonus, int charge) { Bonus = Math.Max(0, bonus); Charge = Math.Max(0, charge); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (CombatSim.Active)
            return;
        if (!TwoStep.Read(s, targets, "Purloin", out var victim, out var center))
            return;
        var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
        var spell = IntentTime.Purloin(victim, out string err);
        if (spell == null)
        { s.Log($"[Purloin] {victim.Name}: {err}."); return; }

        int team = owner?.TeamId ?? 0;
        int hits = 0;
        foreach (var off in spell.Offsets)
        {
            var tile = s.Grid.GetTile(center.Axial + off);
            if (tile == null)
                continue;
            if (spell.Element != TileElementType.None)
                TileEntryReactions.ImbueTile(tile, spell.Element, 1f, owner);
            var occ = tile.Occupant;
            if (occ == null || !GodotObject.IsInstanceValid(occ) || !occ.Stats.IsAlive || occ.TeamId == team)
                continue;
            int dmg = spell.Damage > 0 ? spell.Damage + Bonus : 0;
            if (dmg > 0)
                occ.ApplyDamage(dmg, owner, Delivery.Arc);
            if (!string.IsNullOrEmpty(spell.Status) && occ.Stats.IsAlive)
                occ.ApplyStatus(spell.Status, spell.StatusTurns);
            hits++;
        }
        if (Charge > 0 && owner?.Attunement is ArcaneAttunement arc)
            arc.Add(Charge);
        s.Log($"[Purloin] {owner?.Name ?? "You"} casts the stolen {spell.Name}: {hits} enem{(hits == 1 ? "y" : "ies")} caught.");
    }
}

/// <summary>True when the first target carries a buff.
/// JSON: { "type": "target_has_buff" }</summary>
public sealed class TargetHasBuffPredicate : IPredicate
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
                return ChronoStatuses.Buffs.Any(u.HasStatus);
        }
        return false;
    }
}
