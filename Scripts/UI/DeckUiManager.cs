using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// DeckUiManager.cs
//
// Purpose:        Owns the visible deck UI in the combat scene
//                 — the hand fan, the draw/discard counter
//                 labels, the test/debug buttons, and the
//                 diff-based hand refresh that keeps CardUi
//                 nodes stable across redraws.
// Layer:          UI
// Collaborators:  DeckManager.cs (active deck source),
//                 CardUi.cs (the per-card visual nodes),
//                 UITheme.cs (hand-arc layout constants)
// See:            README §6 — Per-Unit Deck Management
// ============================================================

/// <summary>Combat-scene UI controller for the hand and deck counters. Diff-driven refresh — existing <see cref="CardUi"/> nodes are kept and rearranged where possible rather than freed and recreated, so card hover/select state survives across draws.</summary>
public partial class DeckUiManager : Node2D
{
	[Export] public PackedScene CardUIPackedScene;
	[Export] public PackedScene DropSlotScene;

	// ── Bounding box for the hand — adjust these exports per screen ───────
	[Export] public float HandBoundLeft = 0.25f;  // fraction of screen width
	[Export] public float HandBoundRight = 0.75f;  // fraction of screen width
	[Export] public float HandBoundBottom = 0.92f;  // fraction of screen height (leaves room for bottom bar)
	[Export] public float HandBoundTop = 0.65f;  // fraction of screen height (cards don't go above this)

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
		handUIContainer = GetNode<Control>("../HandUI");

		CallDeferred(nameof(InitHandUISize));

		// Hide debug buttons — deck/grave managed by CombatUI bottom bar
		drawButton = GetNodeOrNull<Button>("../DrawButton");
		discardButton = GetNodeOrNull<Button>("../DiscardButton");
		reshuffleButton = GetNodeOrNull<Button>("../ReshuffleButton");
		removeButton = GetNodeOrNull<Button>("../RemoveButton");

		if (drawButton != null)
			drawButton.Visible = false;
		if (discardButton != null)
			discardButton.Visible = false;
		if (reshuffleButton != null)
			reshuffleButton.Visible = false;
		if (removeButton != null)
			removeButton.Visible = false;

		// Wire buttons even though hidden (DeckManager still calls them internally)
		if (drawButton != null)
			drawButton.Pressed += () => deckManager.DrawCards(1);
		if (discardButton != null)
			discardButton.Pressed += () => DiscardTopCard();
		if (reshuffleButton != null)
			reshuffleButton.Pressed += () => deckManager.Reshuffle();
		if (removeButton != null)
			removeButton.Pressed += () => RemoveTopCard();

		GetViewport().SizeChanged += OnViewportSizeChanged;
	}

	private void InitHandUISize()
	{
		if (handUIContainer == null)
			return;
		var vpSize = GetViewport().GetVisibleRect().Size;
		handUIContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		handUIContainer.Size = vpSize;
		handUIContainer.Position = Vector2.Zero;
	}

	private void OnViewportSizeChanged()
	{
		InitHandUISize();
		PositionHandCards();
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
				if (child is CardUi c)
					currentUiCards.Add(c);

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
				if (child is CardUi c)
					allUiCards.Add(c);

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
				if (child is CardUi c)
					presentCards.Add(c.CardInstance);

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
		if (count == 0)
			return;

		Vector2 screen = GetViewport().GetVisibleRect().Size;

		float leftReserve = 290f;
		float rightReserve = 230f;
		float boxLeft = leftReserve;
		float boxRight = screen.X - rightReserve;
		float boxCenterX = boxLeft + (boxRight - boxLeft) * 0.5f;

		float cardW = 200f;
		float cardH = 300f;

		// Cards flush with screen bottom
		float cardBotY = screen.Y - cardH * 0.15f;
		float cardCenterY = cardBotY - cardH * 0.5f;

		// Very large radius = very flat arc = cards stay low and barely rotate
		float radius = screen.Y * 1.8f;

		Vector2 arcCenter = new Vector2(boxCenterX, cardCenterY + radius);

		// Wider gap between cards
		float desiredGap = cardW * 1.8f;
		float halfChord = desiredGap * (count - 1) * 0.5f;
		float arcSpanRad = 2f * Mathf.Asin(Mathf.Clamp(halfChord / radius, 0f, 1f));
		float arcSpan = Mathf.Clamp(arcSpanRad, Mathf.DegToRad(1f), Mathf.DegToRad(25f));

		float angleStart = count > 1 ? -arcSpan / 2f : 0f;
		float angleStep = count > 1 ? arcSpan / (count - 1) : 0f;

		for (int i = 0; i < count; i++)
		{
			if (handUIContainer.GetChild(i) is not Control card)
				continue;

			float angle = angleStart + angleStep * i;
			Vector2 offset = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)) * radius;
			Vector2 pos = arcCenter + offset;

			Vector2 cs = card.Size.LengthSquared() > 0 ? card.Size
				: card.CustomMinimumSize.LengthSquared() > 0 ? card.CustomMinimumSize
				: new Vector2(cardW, cardH);

			card.Position = pos - cs * 0.5f;
			card.Rotation = angle;

			if (card is CardUi cardUi)
				cardUi.SetRestTransform(card.Position, card.Rotation);
		}

		UpdateCardCounts();
	}

	private void UpdateCardCounts()
	{
		if (deckCountLabel != null)
			deckCountLabel.Text = $"{deckManager.DrawPile.Count}";
		if (handCountLabel != null)
			handCountLabel.Text = $"Hand: {deckManager.Hand.Count}";
		if (discardCountLabel != null)
			discardCountLabel.Text = $"Discard: {deckManager.DiscardPile.Count}";
	}

	private void DiscardTopCard()
	{
		if (deckManager.Hand.Count == 0)
			return;
		var card = deckManager.Hand[^1];
		deckManager.DiscardCard(card);
	}

	private void RemoveTopCard()
	{
		if (deckManager.Hand.Count == 0)
			return;
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
			cardUi.Position + new Vector2(0, screenSize.Y * UITheme.DiscardAnimDropScale),
			UITheme.DiscardAnimDuration);
		tween.TweenProperty(cardUi, "modulate",
			new Color(1, 1, 1, 0f), UITheme.DiscardFadeDuration);
		tween.TweenProperty(cardUi, "scale",
			new Vector2(UITheme.DiscardEndScale, UITheme.DiscardEndScale),
			UITheme.DiscardAnimDuration);
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
			if (handUIContainer.GetChild(i) is not CardUi neighbor)
				continue;
			if (neighbor == hoveredCard)
				continue;

			int dist = i - hoveredIndex;
			// Push neighbors outward by up to 18px, falling off with distance
			float push = isEntering
				? UITheme.HandNeighborPushPx / Mathf.Abs(dist) * Mathf.Sign(dist)
				: 0f;

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
