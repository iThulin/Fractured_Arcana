using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// UnitDeckData.cs
//
// Purpose:        Per-unit deck state — draw pile, hand, discard
//                 pile, max hand size, and the standard
//                 draw/discard/shuffle/reshuffle operations.
//                 Pure data; no Godot nodes, no UI.
// Layer:          Data
// Collaborators:  CardRuntime.cs (Card), CardDatabase.cs
//                 (builds the starting deck), Unit.cs (each unit
//                 holds one of these), DeckManager.cs,
//                 Effect.cs (DrawCardsEffect calls Draw)
// See:            README §6 — Per-Unit Deck Management
// ============================================================

/// <summary>One unit's deck state — draw pile, hand, discard pile — with the standard card-game operations (draw, discard, shuffle, reshuffle). Each combat unit owns exactly one of these.</summary>
public class UnitDeckData
{
	public List<Card> DrawPile = new();
	public List<Card> Hand = new();
	public List<Card> DiscardPile = new();

	/// <summary>U3e: cards removed from the fight entirely (redact). NOT reshuffled by
	/// <see cref="Reshuffle"/> — that is the whole point. A discarded card is a card you
	/// will see again in two shuffles; an exiled one is gone, which is what turns hand
	/// denial into attrition against a 10-card deck instead of a delay.</summary>
	public List<Card> ExilePile = new();

	/// <summary>Live hand cap. Lowered by the enemy `hand_cap` aura and restored when
	/// its carrier dies — CombatManager.ApplyEnemyAuras recomputes it from
	/// <see cref="BaseMaxHandSize"/> every pass, so it is never adjusted in place.</summary>
	public int MaxHandSize = 5;

	/// <summary>The unmodified cap this deck was built with. The restore point for
	/// hand_cap; never mutated after construction.</summary>
	public int BaseMaxHandSize = 5;

	public CardSchool School = CardSchool.Adept;

	private Random _rng = new();

	public UnitDeckData(CardSchool school, int maxHandSize = 5)
	{
		School = school;
		MaxHandSize = maxHandSize;
		BaseMaxHandSize = maxHandSize;
	}

	/// <summary>
	/// Build and shuffle the starting deck from the card database.
	/// </summary>
	public void Initialize(int deckSize)
	{
		DrawPile = CardDatabase.BuildRandomDeck(School, deckSize);
		Shuffle();
	}

	/// <summary>
	/// Initialize from an existing card list (for saved decks, curated decks, etc.)
	/// </summary>
	public void Initialize(List<Card> cards)
	{
		DrawPile = new List<Card>(cards);
		Shuffle();
	}

	/// <summary>Fisher-Yates shuffle of the draw pile in place.</summary>
	public void Shuffle()
	{
		for (int i = DrawPile.Count - 1; i > 0; i--)
		{
			int j = _rng.Next(i + 1);
			(DrawPile[i], DrawPile[j]) = (DrawPile[j], DrawPile[i]);
		}
	}

	/// <summary>
	/// Draw cards into hand. Returns the cards drawn.
	/// </summary>
	public List<Card> Draw(int count)
	{
		var drawn = new List<Card>();

		for (int i = 0; i < count; i++)
		{
			if (DrawPile.Count == 0 && DiscardPile.Count == 0)
				break;

			if (DrawPile.Count == 0)
				Reshuffle();

			if (DrawPile.Count > 0)
			{
				var card = DrawPile[0];
				DrawPile.RemoveAt(0);
				Hand.Add(card);
				drawn.Add(card);
			}
		}

		return drawn;
	}

	/// <summary>
	/// Draw up to max hand size.
	/// </summary>
	public List<Card> DrawToFull()
	{
		int need = MaxHandSize - Hand.Count;
		if (need <= 0) return new List<Card>();
		return Draw(need);
	}

	/// <summary>Moves a card from hand to discard pile. No-op if the card isn't in hand.</summary>
	public void Discard(Card card)
	{
		if (Hand.Remove(card))
			DiscardPile.Add(card);
	}

	/// <summary>U3e (redact): removes a card from hand for the rest of the combat.
	/// Goes to <see cref="ExilePile"/>, which <see cref="Reshuffle"/> never touches —
	/// so this shrinks the deck the player is drawing from rather than deferring the
	/// card by one cycle. Returns true when a card was actually taken.</summary>
	public bool ExileFromHand(Card card)
	{
		if (!Hand.Remove(card))
			return false;
		ExilePile.Add(card);
		return true;
	}

	/// <summary>Empties the discard pile back into the draw pile and shuffles. Called automatically by <see cref="Draw"/> when the draw pile runs dry.</summary>
	public void Reshuffle()
	{
		DrawPile.AddRange(DiscardPile);
		DiscardPile.Clear();
		Shuffle();
	}

	/// <summary>Sum of all cards across the three LIVE zones — what the unit can still
	/// draw or play. Deliberately excludes <see cref="ExilePile"/>: an exiled card is no
	/// longer part of this deck, and a count that pretends otherwise would hide exactly
	/// the attrition redact exists to cause. Used by save / sanity checks.</summary>
	public int TotalCards => DrawPile.Count + Hand.Count + DiscardPile.Count;

	/// <summary>Every card this deck started the combat with, exile included.</summary>
	public int TotalCardsEverOwned => TotalCards + ExilePile.Count;
}
