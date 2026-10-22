using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ArcanistLeafEffects.cs
//
// Purpose:        The Register's leaf for Arcanists
//                 (class_identity_arcanist_v1 §2, slice 14a):
//                 • pen: hand / deck top / reveal-and-pick onto pages
//                 • free_leaf_cast: next leaf cast costs no Charge
//                 • memorize: Infinity Loop's fourth page
//                 • recite_oldest: Rapid Notation's on-demand babble
//                 • charge_lock: Overcharge's lockout
//                 • spend_charge: plain Charge spend (Cascade Bolt)
//                 Casting from a page and the end-of-turn babble live in
//                 CombatManager.Leaf.cs; the pages live on UnitDeckData.
// Layer:          Effects
// Collaborators:  UnitDeckData (Pages, Memorized), ArcaneAttunement,
//                 CardChoiceRequest, RegisterManager
// See:            docs/class_identity_arcanist_v1.md §2
// ============================================================

/// <summary>Shared leaf helpers.</summary>
public static class Leaf
{
    /// <summary>Pens a card, logs it, and fires the Register's first-pen note.</summary>
    public static bool Pen(GameState s, Unit owner, Card card, bool freeTop = false)
    {
        var deck = owner?.DeckData;
        if (deck == null || card == null)
            return false;
        if (deck.Pages.Count >= UnitDeckData.MaxPages)
        {
            s?.Log($"[Pen] {owner.Name}'s leaf is full; {card.CardName} stays where it was.");
            return false;
        }
        if (!deck.Pen(card))
            return false;
        if (freeTop)
            deck.FreeTopPages.Add(card);
        s?.Log($"[Pen] {card.CardName} is penned onto {owner.Name}'s leaf ({deck.Pages.Count}/{UnitDeckData.MaxPages})"
               + (freeTop ? "; its top half will cost no Charge." : "."));
        RegisterManager.Fire("arc.first_pen");
        if (deck.Pages.Count >= UnitDeckData.MaxPages)
            s?.Log($"[Leaf] The leaf is full. At the end of the turn it babbles: {deck.Pages[0].TopHalf?.Name ?? deck.Pages[0].CardName} is cast for free, then lost for the fight.");
        return true;
    }
}

/// <summary>
/// Pen cards onto the caster's leaf.
/// JSON: { "type": "pen", "from": "hand" | "deck_top" | "reveal", "count": n,
///         "up_to": bool, "free_top": bool, "reveal": n, "charge_per_bottomed": n }
/// "hand": the player picks (up_to lets them pick fewer). "deck_top": the top card(s).
/// "reveal": reveal the top N, pen one, the rest go to the bottom (Charge per bottomed).
/// </summary>
public sealed class PenEffect : EffectBase
{
    public string From;
    public int Count, Reveal, ChargePerBottomed;
    public bool UpTo, FreeTop;

    public PenEffect(string from, int count, bool upTo, bool freeTop, int reveal, int chargePerBottomed)
    {
        From = string.IsNullOrEmpty(from) ? "hand" : from;
        Count = Math.Max(1, count);
        UpTo = upTo;
        FreeTop = freeTop;
        Reveal = Math.Max(1, reveal);
        ChargePerBottomed = Math.Max(0, chargePerBottomed);
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        var deck = owner?.DeckData;
        if (deck == null)
        { s?.Log("[Pen] no deck."); return; }
        if (CombatSim.Active)
            return;

        int room = UnitDeckData.MaxPages - deck.Pages.Count;
        switch (From)
        {
            case "deck_top":
            {
                if (room <= 0)
                { s.Log($"[Pen] {owner.Name}'s leaf is full."); return; }
                for (int i = 0; i < Math.Min(Count, room); i++)
                {
                    if (deck.DrawPile.Count == 0 && deck.DiscardPile.Count > 0)
                        deck.Reshuffle();
                    if (deck.DrawPile.Count == 0)
                        break;
                    Leaf.Pen(s, owner, deck.DrawPile[0], FreeTop);
                }
                s.OnDrawCards?.Invoke(owner);
                return;
            }
            case "reveal":
                ResolveReveal(s, owner, deck, room);
                return;
            default:
                ResolveHand(s, owner, deck, room);
                return;
        }
    }

    private void ResolveHand(GameState s, Unit owner, UnitDeckData deck, int room)
    {
        if (room <= 0)
        { s.Log($"[Pen] {owner.Name}'s leaf is full."); return; }
        if (deck.Hand.Count == 0)
        { s.Log("[Pen] no card in hand to pen."); return; }
        int pick = Math.Min(Math.Min(Count, room), deck.Hand.Count);
        var req = new CardChoiceRequest
        {
            Title = "Pen",
            Prompt = pick == 1
                ? "Pen a card onto your leaf. Cast it later for its mana plus 1 Charge."
                : $"Pen {(UpTo ? "up to " : "")}{pick} cards onto your leaf. Cast them later for their mana plus 1 Charge.",
            Owner = owner,
            Candidates = new List<Card>(deck.Hand),
            PickCount = pick,
            AllowFewer = UpTo,
            Source = "Pen",
            OnChosen = chosen =>
            {
                if (chosen != null)
                    foreach (var c in chosen)
                        Leaf.Pen(s, owner, c, FreeTop);
                s.OnDrawCards?.Invoke(owner);
            },
        };
        s.RequestCardChoice(req);
    }

    private void ResolveReveal(GameState s, Unit owner, UnitDeckData deck, int room)
    {
        if (deck.DrawPile.Count < Reveal && deck.DiscardPile.Count > 0)
            deck.Reshuffle();
        int look = Math.Min(Reveal, deck.DrawPile.Count);
        if (look <= 0)
        { s.Log("[Pen] the deck is empty."); return; }
        var revealed = deck.DrawPile.GetRange(0, look);
        deck.DrawPile.RemoveRange(0, look);   // held out until answered

        var req = new CardChoiceRequest
        {
            Title = "Grimoire",
            Prompt = room > 0
                ? $"Pen 1 of the top {look} onto your leaf. The rest go to the bottom."
                : $"Your leaf is full: keep 1 of the top {look} in your hand. The rest go to the bottom.",
            Owner = owner,
            Candidates = new List<Card>(revealed),
            PickCount = 1,
            Source = "Pen",
            OnChosen = chosen =>
            {
                var keep = chosen != null && chosen.Count > 0 ? chosen[0] : null;
                int bottomed = 0;
                foreach (var c in revealed)
                {
                    if (c == keep)
                        continue;
                    deck.DrawPile.Add(c);
                    bottomed++;
                }
                if (keep != null)
                {
                    if (room > 0)
                    {
                        deck.DrawPile.Insert(0, keep);   // Pen takes it from the draw pile
                        if (!Leaf.Pen(s, owner, keep, FreeTop))
                        { deck.DrawPile.Remove(keep); deck.Hand.Add(keep); }
                    }
                    else
                        deck.Hand.Add(keep);
                }
                if (ChargePerBottomed > 0 && bottomed > 0 && owner.Attunement is ArcaneAttunement arc)
                {
                    int got = arc.Add(ChargePerBottomed * bottomed);
                    s.Log($"[Pen] +{got} Charge ({bottomed} card(s) to the bottom).");
                }
                s.OnDrawCards?.Invoke(owner);
            },
        };
        s.RequestCardChoice(req);
    }
}

/// <summary>Your next <see cref="Count"/> leaf cast(s) cost no Charge.
/// JSON: { "type": "free_leaf_cast", "count": n }</summary>
public sealed class FreeLeafCastEffect : EffectBase
{
    public int Count;
    public FreeLeafCastEffect(int count) { Count = Math.Max(1, count); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner?.DeckData == null)
            return;
        owner.DeckData.FreeLeafCasts += Count;
        s.Log($"[Leaf] {owner.Name}'s next {(Count == 1 ? "leaf cast costs" : $"{Count} leaf casts cost")} no Charge.");
    }
}

/// <summary>Memorize a card from your hand onto the leaf's fourth page for the rest of the
/// fight. At the end of each of your turns it recites its top half at the nearest enemy
/// for 1 Charge (skipped with none). A new memorization sends the old page to the discard.
/// JSON: { "type": "memorize" }</summary>
public sealed class MemorizeEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        var deck = owner?.DeckData;
        if (deck == null || CombatSim.Active)
            return;
        if (deck.Hand.Count == 0)
        { s.Log("[Memorize] no card in hand."); return; }
        var req = new CardChoiceRequest
        {
            Title = "Memorize",
            Prompt = "Memorize a spell onto your leaf's fourth page. At the end of each of your turns it recites its top half at the nearest enemy for 1 Charge.",
            Owner = owner,
            Candidates = new List<Card>(deck.Hand),
            PickCount = 1,
            Source = "Memorize",
            OnChosen = chosen =>
            {
                var card = chosen != null && chosen.Count > 0 ? chosen[0] : null;
                if (card == null || !deck.Hand.Remove(card))
                    return;
                if (deck.Memorized != null)
                    deck.DiscardPile.Add(deck.Memorized);
                deck.Memorized = card;
                s.Log($"[Memorize] {card.CardName} is memorized onto the fourth page.");
                s.OnDrawCards?.Invoke(owner);
            },
        };
        s.RequestCardChoice(req);
    }
}

/// <summary>The leaf recites its oldest page's top half now, for free; the card is exiled
/// for the fight. JSON: { "type": "recite_oldest" }</summary>
public sealed class ReciteOldestEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner == null || CombatSim.Active)
            return;
        if (s.OnReciteOldest == null)
        { s.Log("[Recite] no combat is running."); return; }
        s.OnReciteOldest(owner);
    }
}

/// <summary>Charge can't increase until the end of your next turn (Overcharge).
/// JSON: { "type": "charge_lock", "turns": n } (turn starts; 2 = this turn and the next)</summary>
public sealed class ChargeLockEffect : EffectBase
{
    public int Turns;
    public ChargeLockEffect(int turns) { Turns = Math.Max(1, turns); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner?.Attunement is ArcaneAttunement arc && !CombatSim.Active)
        {
            arc.LockCharge(Turns);
            s.Log($"[Overcharge] {owner.Name}'s Charge can't increase until the end of the next turn.");
        }
    }
}

/// <summary>Spend up to <see cref="Amount"/> Charge (Cascade Bolt's bottom).
/// JSON: { "type": "spend_charge", "amount": n }</summary>
public sealed class SpendChargeEffect : EffectBase
{
    public int Amount;
    public SpendChargeEffect(int amount) { Amount = Math.Max(1, amount); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner?.Attunement is ArcaneAttunement arc && !CombatSim.Active)
        {
            int spent = arc.Spend(Amount);
            s.Log($"[Charge] {owner.Name} spends {spent} Charge.");
        }
    }
}
