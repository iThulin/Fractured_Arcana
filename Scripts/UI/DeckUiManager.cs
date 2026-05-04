using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public partial class DeckUiManager : Node2D
{
	[Export] public PackedScene CardUIPackedScene;
	[Export] public PackedScene DropSlotScene;

	private DeckManager deckManager;
	private Control handUIContainer;

	private bool _isRefreshing = false;
	private bool _refreshPending = false;

	private Label deckCountLabel;
	private Label handCountLabel;
	private Label discardCountLabel;

	private Button drawButton;
	private Button discardButton;
	private Button reshuffleButton;
	private Button removeButton;

	public override void _Ready()
	{
		deckManager = GetNodeOrNull<DeckManager>("../../Player/DeckManager");
		if (deckManager == null)
			GD.PrintErr("DeckManager not found at ../../Player/DeckManager");

		handUIContainer = GetNode<Control>("../HandUI");

		deckCountLabel = GetNode<Label>("../DeckCountLabel");
		handCountLabel = GetNode<Label>("../HandCountLabel"); //DeckUI/HandCountLabel
		discardCountLabel = GetNode<Label>("../DiscardCountLabel");

		drawButton = GetNode<Button>("../DrawButton");
		discardButton = GetNode<Button>("../DiscardButton");
		reshuffleButton = GetNode<Button>("../ReshuffleButton");
		removeButton = GetNode<Button>("../RemoveButton");

		drawButton.Pressed += () => { deckManager.DrawCards(1); };
		discardButton.Pressed += () => { DiscardTopCard(); };
		reshuffleButton.Pressed += () => { deckManager.Reshuffle(); };
		removeButton.Pressed += () => { RemoveTopCard(); };
	}

	public async Task RefreshUI()
	{
		_isRefreshing = true;
		_refreshPending = false;

		try
		{
			// Snapshot hand RIGHT NOW for diffing
			var targetHand = new List<Card>(deckManager.Hand);

			// Find existing UI nodes
			var currentUiCards = new List<CardUi>();
			foreach (Node child in handUIContainer.GetChildren())
				if (child is CardUi c) currentUiCards.Add(c);

			// Cards whose UI node should be removed
			var toRemove = new List<CardUi>();
			foreach (var cardUi in currentUiCards)
				if (!targetHand.Contains(cardUi.CardInstance))
					toRemove.Add(cardUi);

			// Cards that need a new UI node
			var existingCards = new HashSet<Card>();
			foreach (var cardUi in currentUiCards)
				existingCards.Add(cardUi.CardInstance);

			// Animate out discarded cards
			foreach (var cardUi in toRemove)
				PlayDiscardAnimation(cardUi);

			// Add UI nodes for new cards immediately
			foreach (var card in targetHand)
			{
				if (!existingCards.Contains(card))
				{
					var cardUi = CardUIPackedScene.Instantiate<CardUi>();
					cardUi.SetCard(card);
					cardUi.SetDeckUiManager(this);
					cardUi.CardDropped += () => PositionHandCards();
					cardUi.CardHalfHovered += OnCardHalfHovered;
					handUIContainer.AddChild(cardUi);
				}
			}

			// Wait one frame for layout
			await ToSignal(GetTree().CreateTimer(0.0f), "timeout");

			// Wait for discard anims if any
			if (toRemove.Count > 0)
				await ToSignal(GetTree().CreateTimer(0.30f), "timeout");

			// --- RE-DIFF HERE against current hand, not the old snapshot ---
			// Hand may have changed during the await (draw, reshuffle, etc.)
			var finalHand = new HashSet<Card>(deckManager.Hand);

			// Remove any UI nodes that are still not in the final hand
			var allUiCards = new List<CardUi>();
			foreach (Node child in handUIContainer.GetChildren())
				if (child is CardUi c) allUiCards.Add(c);

			foreach (var cardUi in allUiCards)
			{
				if (!finalHand.Contains(cardUi.CardInstance))
				{
					if (cardUi.GetParent() == handUIContainer)
						handUIContainer.RemoveChild(cardUi);
					if (IsInstanceValid(cardUi))
						cardUi.QueueFree();
				}
			}

			// Add any UI nodes still missing after awaits
			var presentCards = new HashSet<Card>();
			foreach (Node child in handUIContainer.GetChildren())
				if (child is CardUi c) presentCards.Add(c.CardInstance);

			foreach (var card in deckManager.Hand)
			{
				if (!presentCards.Contains(card))
				{
					var cardUi = CardUIPackedScene.Instantiate<CardUi>();
					cardUi.SetCard(card);
					cardUi.SetDeckUiManager(this);
					cardUi.CardDropped += () => PositionHandCards();
					cardUi.CardHalfHovered += OnCardHalfHovered;
					handUIContainer.AddChild(cardUi);
				}
			}

			PositionHandCards();
			RefreshAffordability();
		}
		finally
		{
			_isRefreshing = false;
			if (_refreshPending)
				SafeRefreshUI();
		}
	}

	public void SafeRefreshUI()
	{
		if (_isRefreshing)
		{
			// Queue one pending refresh to run after current finishes
			_refreshPending = true;
			return;
		}
		_ = RefreshUI();
	}

	private void PositionHandCards()
	{
		int count = handUIContainer.GetChildCount();
		if (count == 0) return;

		Vector2 screenSize = GetViewport().GetVisibleRect().Size;

		float radius = screenSize.Y * 2.3f;
		Vector2 arcCenter = new Vector2(screenSize.X / 2f, screenSize.Y + radius * 0.6f);

		float maxArcSpanDeg = 30f;
		float minArcSpanDeg = 1f;
		float stepPerCard = 5f;
		float arcSpanDeg = Mathf.Min(maxArcSpanDeg, stepPerCard * (count - 1));

		arcSpanDeg = Mathf.Max(minArcSpanDeg, arcSpanDeg);
		float arcSpan = Mathf.DegToRad(arcSpanDeg);

		float angleStart = (count > 1) ? -arcSpan / 2f : 0f;
		float angleStep = (count > 1) ? arcSpan / (count - 1) : 0f;

		for (int i = 0; i < count; i++)
		{
			if (handUIContainer.GetChild(i) is Control card)
			{
				float angle = angleStart + angleStep * i;

				Vector2 arcOffset = new Vector2(
					Mathf.Sin(angle),
					-Mathf.Cos(angle)
				) * radius;

				Vector2 localPos = arcCenter + arcOffset;
				card.Position = localPos - (card.Size / 2f);
				card.Rotation = angle;

				if (card is CardUi cardUi)
    				cardUi.SetRestTransform(card.Position, card.Rotation);
			}
		}
		UpdateCardCounts();
	}

	private void UpdateCardCounts()
	{
		deckCountLabel.Text = $"{deckManager.DrawPile.Count}";
		handCountLabel.Text = $"Hand: {deckManager.Hand.Count}";
		discardCountLabel.Text = $"Discard: {deckManager.DiscardPile.Count}";
	}

	private void DiscardTopCard()
	{
		if (deckManager.Hand.Count == 0) return;
		var card = deckManager.Hand[^1];
		deckManager.DiscardCard(card);
	}

	private void RemoveTopCard()
	{
		if (deckManager.Hand.Count == 0) return;
		var card = deckManager.Hand[^1];
		deckManager.Hand.RemoveAt(deckManager.Hand.Count - 1);
		deckManager.DiscardPile.Add(card);
	}

	private void PlayDiscardAnimation(CardUi cardUi)
	{
		Vector2 screenSize = GetViewport().GetVisibleRect().Size;

		var tween = cardUi.CreateTween().SetParallel(true);
		tween.SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Cubic);
		tween.TweenProperty(cardUi, "position",
			cardUi.Position + new Vector2(0, screenSize.Y * 0.3f), 0.28f);
		tween.TweenProperty(cardUi, "modulate",
			new Color(1, 1, 1, 0f), 0.22f);
		tween.TweenProperty(cardUi, "scale",
			new Vector2(0.85f, 0.85f), 0.28f);
	}

	private Func<int> _getMana;

	public void SetManaProvider(Func<int> provider)
	{
		_getMana = provider;
	}

	public void RefreshAffordability()
	{
		int mana = _getMana?.Invoke() ?? 999;
		foreach (Node child in handUIContainer.GetChildren())
		{
			if (child is CardUi cardUi)
				cardUi.RefreshAffordability(mana);
		}
	}

	public void OnCardHoverChanged(CardUi hoveredCard, bool isEntering)
	{
		int count = handUIContainer.GetChildCount();
		int hoveredIndex = hoveredCard.GetIndex();

		for (int i = 0; i < count; i++)
		{
			if (handUIContainer.GetChild(i) is not CardUi neighbor) continue;
			if (neighbor == hoveredCard) continue;

			int dist = i - hoveredIndex;
			// Push neighbors outward by up to 18px, falling off with distance
			float push = isEntering ? 18f / Mathf.Abs(dist) * Mathf.Sign(dist) : 0f;

			var tween = neighbor.CreateTween();
			tween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
			// Shift along the arc tangent — approximate with X offset
			tween.TweenProperty(neighbor, "position",
				neighbor._restPosition + new Vector2(push, 0), 0.15f);
		}
	}

	[Signal] public delegate void CardHalfHoveredEventHandler(CardUi cardUi, bool isTop, bool isEntering);

	private void OnCardHalfHovered(CardUi cardUi, bool isTop, bool isEntering)
	{
		EmitSignal(SignalName.CardHalfHovered, cardUi, isTop, isEntering);
	}
	
}
