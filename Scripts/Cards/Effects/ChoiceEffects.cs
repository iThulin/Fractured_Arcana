using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ChoiceEffects.cs
//
// Purpose:        Effects built ON the post-cast choice seam
//                 (CardChoice.cs): Seek (filtered tutor),
//                 Foretell (delayed hand), and the discard-side
//                 choices. These are the second generation of
//                 choice effects: ScryEffect proved the seam;
//                 these consume it via GameState.RequestCardChoice.
// Layer:          Effects
// Collaborators:  CardChoice.cs / GameStateManager.cs (the seam),
//                 UnitDeckData.cs (zones), MemorialManager.cs
//                 (Remembrance), JsonCardLoader.cs (factories)
// See:            claude/post_cast_design_space_v1.md §3
// ============================================================

/// <summary>One card waiting out its Foretell delay. Held OUT of every deck pile
/// (the same convention as a scry's revealed cards) and delivered into the owner's
/// hand by CombatManager.StartPlayerTurn when <see cref="TurnsUntilArrival"/> hits 0.</summary>
public sealed class ForetoldEntry
{
	public Unit Owner;
	public Card Card;
	public int TurnsUntilArrival = 1;
}

/// <summary>
/// Seek: the filtered tutor. Reveals the top <see cref="Look"/> cards, offers the
/// player the ones matching <see cref="FilterTag"/> (any half's Tags; null/empty tag
/// = every revealed card matches), puts the kept card(s) in hand and everything else
/// on the bottom in revealed order.
///
/// A whiffed Seek (no revealed card matches) auto-resolves with a log line and
/// bottoms everything; the request seam's degenerate rule already guarantees no
/// no-choice modal. Optionally converts each bottomed card into Arcanist charge
/// (<see cref="ChargePerBottomed"/>, Grimoire Dive).
///
/// JSON: { "type": "seek", "look": n, "keep": n, "filter_tag": "construct",
///         "charge_per_bottomed": n }
/// </summary>
public sealed class SeekEffect : EffectBase
{
	public int Look;
	public int Keep;
	public string FilterTag;
	public int ChargePerBottomed;

	public SeekEffect(int look, int keep, string filterTag, int chargePerBottomed = 0)
	{
		Look = look;
		Keep = keep;
		FilterTag = filterTag;
		ChargePerBottomed = chargePerBottomed;
	}

	private bool Matches(Card c)
	{
		if (string.IsNullOrEmpty(FilterTag))
			return true;
		bool HalfHas(CardHalf h) => h?.Tags != null
			&& h.Tags.Any(t => string.Equals(t, FilterTag, StringComparison.OrdinalIgnoreCase));
		return HalfHas(c?.TopHalf) || HalfHas(c?.BottomHalf);
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		var deck = casterUnit?.DeckData;
		if (deck == null)
		{ s.Log("[Seek] No caster deck; no-op."); return; }

		// R22: preview replays real effects; one that moved cards would corrupt the
		// deck on hover.
		if (CombatSim.Active)
			return;

		if (deck.DrawPile.Count < Look && deck.DiscardPile.Count > 0)
			deck.Reshuffle();

		int look = Math.Min(Look, deck.DrawPile.Count);
		if (look <= 0)
		{ s.Log($"[Seek] {casterUnit.Name}'s deck is empty; nothing to reveal."); return; }

		var revealed = deck.DrawPile.GetRange(0, look);
		deck.DrawPile.RemoveRange(0, look);          // held out until answered

		void BankBottomedCharge(int bottomed)
		{
			if (ChargePerBottomed <= 0 || bottomed <= 0)
				return;
			if (casterUnit.Attunement is ArcaneAttunement arc)
			{
				arc.Add(ChargePerBottomed * bottomed);
				s.Log($"[Seek] +{ChargePerBottomed * bottomed} charge ({bottomed} card(s) bottomed).");
			}
		}

		var matching = revealed.Where(c => c != null && Matches(c)).ToList();
		if (matching.Count == 0)
		{
			// The whiff is public information the player paid for: say what was seen.
			string seen = string.Join(", ", revealed.ConvertAll(c => c?.CardName ?? "?"));
			foreach (var c in revealed)
				if (c != null) deck.DrawPile.Add(c);
			BankBottomedCharge(revealed.Count);
			s.Log($"[Seek] No '{FilterTag}' card in the top {look} ({seen}); all to the bottom.");
			s.OnDrawCards?.Invoke(casterUnit);
			return;
		}

		int keep = Math.Clamp(Keep, 0, matching.Count);
		string what = string.IsNullOrEmpty(FilterTag) ? "card(s)" : $"'{FilterTag}' card(s)";

		var req = new CardChoiceRequest
		{
			Title = "Seek",
			Prompt = $"Keep {keep} of the {matching.Count} matching {what}. Everything else goes to the bottom.",
			Owner = casterUnit,
			Candidates = matching,
			PickCount = keep,
			Source = "Seek",
			OnChosen = chosen =>
			{
				// Everything revealed-but-not-chosen bottoms in revealed order,
				// matching and non-matching alike. Both destination lists are drawn
				// from `revealed`, so no card can be lost or duplicated.
				int bottomed = 0;
				foreach (var c in revealed)
					if (c != null && (chosen == null || !chosen.Contains(c)))
					{ deck.DrawPile.Add(c); bottomed++; }

				if (chosen != null)
					foreach (var c in chosen)
						if (c != null) deck.Hand.Add(c);

				BankBottomedCharge(bottomed);
				s.OnDrawCards?.Invoke(casterUnit);
				string names = chosen == null || chosen.Count == 0
					? "nothing"
					: string.Join(", ", chosen.ConvertAll(c => c?.CardName ?? "a card"));
				s.Log($"[Seek] {casterUnit.Name} keeps {names} ({bottomed} to the bottom).");
			},
		};
		s.RequestCardChoice(req);
	}
}

/// <summary>
/// Foretell: the delayed hand. Reveals the top <see cref="Look"/>, the player sets
/// <see cref="SetAside"/> of them aside; at the start of their NEXT turn those cards
/// enter the hand carrying a per-card <see cref="Discount"/>. The rest bottom.
///
/// This is the correct shape for Borrowed Future ("set 2 aside. Next turn they're in
/// hand and cost 1 less"), which previously banked the cards into the hand
/// immediately: the delay, i.e. the card's entire cost, did not exist. Arrival is
/// CombatManager.StartPlayerTurn's job (GameState.Foretold), the sibling of the
/// Almanac tick: the Almanac schedules effects, Foretell schedules cards.
///
/// JSON: { "type": "foretell", "look": n, "set_aside": n, "discount": n }
/// </summary>
public sealed class ForetellEffect : EffectBase
{
	public int Look;
	public int SetAside;
	public int Discount;

	public ForetellEffect(int look, int setAside, int discount)
	{
		Look = look;
		SetAside = setAside;
		Discount = discount;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		var deck = casterUnit?.DeckData;
		if (deck == null)
		{ s.Log("[Foretell] No caster deck; no-op."); return; }

		if (CombatSim.Active)
			return;

		if (deck.DrawPile.Count < Look && deck.DiscardPile.Count > 0)
			deck.Reshuffle();

		int look = Math.Min(Look, deck.DrawPile.Count);
		if (look <= 0)
		{ s.Log($"[Foretell] {casterUnit.Name}'s deck is empty; nothing to reveal."); return; }

		var revealed = deck.DrawPile.GetRange(0, look);
		deck.DrawPile.RemoveRange(0, look);          // held out until answered

		int setAside = Math.Clamp(SetAside, 0, look);

		var req = new CardChoiceRequest
		{
			Title = "Foretell",
			Prompt = $"Set {setAside} aside. Next turn they arrive in hand" +
					 (Discount > 0 ? $", each costing {Discount} less." : "."),
			Owner = casterUnit,
			Candidates = revealed,
			PickCount = setAside,
			Source = "Foretell",
			OnChosen = chosen =>
			{
				foreach (var c in revealed)
					if (c != null && (chosen == null || !chosen.Contains(c)))
						deck.DrawPile.Add(c);

				if (chosen != null)
					foreach (var c in chosen)
						if (c != null)
						{
							s.Foretold.Add(new ForetoldEntry
							{ Owner = casterUnit, Card = c, TurnsUntilArrival = 1 });
							if (Discount > 0)
								s.AddCardDiscount(c, Discount);
						}

				s.OnDrawCards?.Invoke(casterUnit);
				string names = chosen == null || chosen.Count == 0
					? "nothing"
					: string.Join(", ", chosen.ConvertAll(c => c?.CardName ?? "a card"));
				s.Log($"[Foretell] {casterUnit.Name} sets aside {names}, arriving next turn" +
					  (Discount > 0 ? $" at -{Discount} cost." : "."));
			},
		};
		s.RequestCardChoice(req);
	}
}

/// <summary>
/// Remembrance (Necromancer): choose a card in your discard pile; exile it and leave
/// a memorial on the caster's tile. The discard is public information, so nothing is
/// revealed; the request goes straight to the pile's contents.
///
/// JSON: { "type": "exile_discard_for_memorial", "count": n, "strength": "solid" }
/// </summary>
public sealed class ExileDiscardForMemorialEffect : EffectBase
{
	public int Count;
	public MemorialStrength Strength;

	public ExileDiscardForMemorialEffect(int count, MemorialStrength strength)
	{
		Count = Math.Max(1, count);
		Strength = strength;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		var deck = casterUnit?.DeckData;
		if (deck == null)
		{ s.Log("[Remembrance] No caster deck; no-op."); return; }

		if (CombatSim.Active)
			return;

		if (deck.DiscardPile.Count == 0)
		{ s.Log($"[Remembrance] {casterUnit.Name}'s discard is empty; nothing to release."); return; }

		// Most recent first: that is how a player thinks about their discard.
		var candidates = Enumerable.Reverse(deck.DiscardPile).ToList();
		int pick = Math.Min(Count, candidates.Count);

		var req = new CardChoiceRequest
		{
			Title = "Remembrance",
			Prompt = $"Exile {pick} card(s) from your discard. Each leaves a memorial on your tile.",
			Owner = casterUnit,
			Candidates = candidates,
			PickCount = pick,
			Source = "Remembrance",
			OnChosen = chosen =>
			{
				int released = 0;
				if (chosen != null)
					foreach (var c in chosen)
					{
						if (c == null || !deck.DiscardPile.Remove(c))
							continue;
						deck.ExilePile.Add(c);
						released++;
						var tile = casterUnit.CurrentTile;
						if (tile != null)
							s.Memorials?.CreateMemorial(tile, c.CardName ?? "a memory",
								wasAlly: true, Strength, casterUnit.TeamId);
					}
				s.OnDrawCards?.Invoke(casterUnit);
				s.Log($"[Remembrance] {casterUnit.Name} released {released} card(s); " +
					  $"{released} memorial mark(s) on their tile.");
			},
		};
		s.RequestCardChoice(req);
	}
}
