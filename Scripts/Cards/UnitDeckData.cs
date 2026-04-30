using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// UnitDeckData — Per-unit deck state (draw pile, hand, discard)
//
// This is a pure data class — no Godot nodes, no UI. It holds
// the cards and provides draw/discard/shuffle logic.
// ============================================================

public class UnitDeckData
{
	public List<Card> DrawPile = new();
	public List<Card> Hand = new();
	public List<Card> DiscardPile = new();
	public int MaxHandSize = 5;
	public CardSchool School = CardSchool.Generic;

	private Random _rng = new();

	public UnitDeckData(CardSchool school, int maxHandSize = 5)
	{
		School = school;
		MaxHandSize = maxHandSize;
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

			if (Hand.Count >= MaxHandSize)
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

	public void Discard(Card card)
	{
		if (Hand.Remove(card))
			DiscardPile.Add(card);
	}

	public void Reshuffle()
	{
		DrawPile.AddRange(DiscardPile);
		DiscardPile.Clear();
		Shuffle();
	}

	public int TotalCards => DrawPile.Count + Hand.Count + DiscardPile.Count;
}
