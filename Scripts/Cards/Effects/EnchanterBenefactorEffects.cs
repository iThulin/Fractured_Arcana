using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// EnchanterBenefactorEffects.cs
//
// Purpose:        Effects for the slice 13b Enchanter cards
//                 (class_identity_enchanter_v1 §3 A, D and the
//                 legendary):
//                 • weave_per_named: Name of Courage's bottom
//                 • move_name: Name of Warding's bottom
//                 • shield_named_allies: Litany of Names' bottom
//                 • inscribe: a glyph holding another card's bottom half
//                 • seventh_name: The Seventh Name (rest of fight)
//                 Boons themselves are name_condition with "allies".
// Layer:          Effects
// Collaborators:  EnchanterEngine.cs (Names, NameCondition boons),
//                 GlyphManager / GlyphData (Inscribe's glyph),
//                 CardChoiceRequest (Inscribe's discard pick)
// See:            docs/class_identity_enchanter_v1.md §3
// ============================================================

/// <summary>Gain 1 Weave per unit on the board that carries a Name (yours or anyone's),
/// up to <see cref="Max"/>.
/// JSON: { "type": "weave_per_named", "max": n }</summary>
public sealed class WeavePerNamedEffect : EffectBase
{
    public int Max;
    public WeavePerNamedEffect(int max) { Max = Math.Max(1, max); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner?.Attunement is not WeaveAttunement w || s?.UnitsInPlay == null)
            return;
        int named = s.UnitsInPlay.Count(u => u != null && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive
                                             && u.Names.Any(n => n.TurnsRemaining > 0));
        int gain = Math.Min(Max, named);
        if (gain > 0)
            w.Add(gain);
        s.Log($"[WeavePerNamed] {named} Named unit(s): +{gain} Weave.");
    }
}

/// <summary>Move a Name you wrote from one unit to another of the same side (unit_then_unit,
/// friendlies only). The most recent such Name moves.
/// JSON: { "type": "move_name" }</summary>
public sealed class MoveNameEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var units = new List<Unit>();
        if (targets?.Items != null)
            foreach (var o in targets.Items)
            { var u = ResolveTargetUnit(s, o); if (u != null && !units.Contains(u)) units.Add(u); }
        if (units.Count < 2)
        { s.Log("[MoveName] needs two units."); return; }
        var from = units[0];
        var to = units[1];
        if (from.TeamId != to.TeamId)
        { s.Log("[MoveName] a Name moves only between units on the same side."); return; }
        int team = s.ActiveCasterUnit?.TeamId ?? 0;
        var name = from.Names.LastOrDefault(n => n.TurnsRemaining > 0 && n.OwnerTeam == team);
        if (name == null)
        { s.Log($"[MoveName] {from.Name} carries no Name of yours."); return; }
        if (Names.Move(from, to, name))
            s.Log($"[MoveName] {from.Name}'s Name passes to {to.Name}: {name.ChipText()}.");
    }
}

/// <summary>Each ally carrying a Name gains shield equal to <see cref="PerWeave"/> times the
/// caster's Weave.
/// JSON: { "type": "shield_named_allies", "per_weave": n }</summary>
public sealed class ShieldNamedAlliesEffect : EffectBase
{
    public int PerWeave;
    public ShieldNamedAlliesEffect(int perWeave) { PerWeave = Math.Max(1, perWeave); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null || s?.UnitsInPlay == null)
            return;
        int weave = owner.Attunement is WeaveAttunement w ? w.Weave : 0;
        int amount = weave * PerWeave;
        if (amount <= 0)
        { s.Log("[ShieldNamed] No Weave: no shield."); return; }
        int count = 0;
        foreach (var u in s.UnitsInPlay)
        {
            if (u == null || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.TeamId != owner.TeamId)
                continue;
            if (!u.Names.Any(n => n.TurnsRemaining > 0))
                continue;
            u.Stats.Shield += amount;
            u.RefreshHealthBar();
            count++;
        }
        s.Log($"[ShieldNamed] {count} Named all{(count == 1 ? "y" : "ies")} gain {amount} shield.");
    }
}

/// <summary>
/// Inscribe: prepare a glyph on the target tile, then discard a card from your hand. The
/// glyph holds that card's bottom half and resolves it on the enemy that triggers it, as
/// if you had cast it at that enemy. With no card to discard it deals
/// <see cref="FallbackDamage"/>. With <see cref="Weave"/>, gain that much on the trigger.
/// JSON: { "type": "inscribe", "fallback_damage": n, "weave_on_trigger": n }
/// </summary>
public sealed class InscribeEffect : EffectBase
{
    public int FallbackDamage, Weave;
    public InscribeEffect(int fallbackDamage, int weave)
    { FallbackDamage = Math.Max(0, fallbackDamage); Weave = Math.Max(0, weave); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (s?.Glyphs == null || owner == null)
            return;
        TileData tile = null;
        if (targets?.Items != null)
            foreach (var o in targets.Items)
                if ((tile = InterfaceHelpers.ResolveTile(s, o)) != null)
                    break;
        if (tile == null)
        { s.Log("[Inscribe] no tile."); return; }

        int weave = Weave;
        var g = s.Glyphs.Prepare(tile, owner, ng =>
        {
            ng.Trigger = GlyphTrigger.Enter;
            ng.Damage = FallbackDamage;
            ng.OwnerWeave = weave;
        });
        if (g == null)
        { s.Log("[Inscribe] the tile cannot hold a glyph."); return; }

        if (CombatSim.Active)
            return;

        var deck = owner.DeckData;
        var hand = deck?.Hand;
        if (hand == null || hand.Count == 0)
        {
            s.Log($"[Inscribe] No card to inscribe: the glyph deals {FallbackDamage}.");
            return;
        }

        var req = new CardChoiceRequest
        {
            Title = "Inscribe",
            Prompt = "Discard a card. Its bottom half is written into the glyph and resolves on the enemy that triggers it.",
            Owner = owner,
            Candidates = new List<Card>(hand),
            PickCount = 1,
            Source = "Inscribe",
            OnChosen = chosen =>
            {
                var card = chosen != null && chosen.Count > 0 ? chosen[0] : null;
                var half = card?.BottomHalf;
                if (card == null || half == null || !deck.Hand.Contains(card))
                    return;
                deck.Discard(card);
                s.OnDrawCards?.Invoke(owner);   // refresh the hand
                if (tile.Glyph != g)
                    return;                     // the glyph went off or was removed meanwhile
                g.Damage = 0;
                g.GlyphText = $"It suffers {half.Name}: {half.RulesText}";
                g.OnTrigger = (who, st) => ResolveStored(st, owner, g, half, who);
                s.Log($"[Inscribe] {half.Name} is written into the glyph.");
            },
        };
        s.RequestCardChoice(req);
    }

    /// <summary>Resolves the stored half with the triggering unit as its only target and
    /// the glyph's owner as the caster.</summary>
    private static void ResolveStored(GameState s, Unit owner, GlyphData g, CardHalf half, Unit who)
    {
        if (s == null || half?.Effects == null || who == null || !GodotObject.IsInstanceValid(who))
            return;
        if (who.TeamId == g.OwnerTeam)
            return;   // an inscription is written for the enemy
        var prevCaster = s.ActiveCasterUnit;
        var prevName = s.ResolvingAbilityName;
        s.ActiveCasterUnit = owner != null && GodotObject.IsInstanceValid(owner) ? owner : prevCaster;
        s.ResolvingAbilityName = half.Name;
        try
        {
            s.Log($"[Inscribe] {who.Name} sets off the inscription: {half.Name}.");
            var targets = new TargetSet();
            targets.Items.Add(who);
            var snap = new EffectSnapshot();
            foreach (var e in half.Effects)
                e?.Resolve(s, s.PlayerA, targets, snap);   // PlayerA + ActiveCasterUnit = the owner
            if (g.OwnerWeave > 0 && owner?.Attunement is WeaveAttunement w)
                w.Add(g.OwnerWeave);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Inscribe] stored half '{half.Name}' threw: {ex.Message}");
        }
        finally
        {
            s.ActiveCasterUnit = prevCaster;
            s.ResolvingAbilityName = prevName;
        }
    }
}

/// <summary>
/// The Seventh Name: for the rest of the fight, every Name your side writes lasts
/// <see cref="ExtraTurns"/> more turns, and Naming an enemy also prepares a glyph beneath
/// it that deals <see cref="GlyphDamage"/> if it starts its turn there. Casting it again
/// keeps the larger of each.
/// JSON: { "type": "seventh_name", "extra_turns": n, "glyph_damage": n }
/// </summary>
public sealed class SeventhNameEffect : EffectBase
{
    public int ExtraTurns, GlyphDamage;
    public SeventhNameEffect(int extraTurns, int glyphDamage)
    { ExtraTurns = Math.Max(0, extraTurns); GlyphDamage = Math.Max(0, glyphDamage); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        int team = s?.ActiveCasterUnit?.TeamId ?? 0;
        var cur = Names.SeventhName.TryGetValue(team, out var c) ? c : (0, 0);
        Names.SeventhName[team] = (Math.Max(cur.Item1, ExtraTurns), Math.Max(cur.Item2, GlyphDamage));
        s?.Log($"[SeventhName] For the rest of the fight, your Names last {Names.SeventhName[team].extraTurns} more turn(s), and each Named enemy gets a glyph beneath it.");
    }
}
