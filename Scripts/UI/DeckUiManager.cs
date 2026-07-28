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

	// V1 (combat_ui_v2 §4): the old HandBound* screen-fraction exports are
	// deleted — they were dead (PositionHandCards never read them) and were
	// the documented GetVisibleRect trap. Hand geometry now comes from the
	// UITheme design-space constants (HandReserve*/HandCard*/HandArc*).
	// Under canvas_items stretch, GetVisibleRect().Size IS design space, so
	// the arc math below is resolution-independent by construction.

	private DeckManager deckManager;
	private Control handUIContainer;
	private Vector2 _lastFanScreen = Vector2.Zero;  // fan-math diagnostic (print on change)

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

		// Design-space size (canvas_items stretch: constant 1920×1080, wider
		// under aspect=expand on ultrawide — the reserves stay edge-anchored).
		Vector2 screen = GetViewport().GetVisibleRect().Size;

		float boxLeft = UITheme.HandReserveLeft;
		float boxRight = screen.X - UITheme.HandReserveRight;
		float boxCenterX = boxLeft + (boxRight - boxLeft) * 0.5f;

		// Diagnostic (stale-assembly canary + widescreen evidence): prints once
		// per viewport size change. boxCenter must equal screen.X/2 exactly —
		// if it doesn't, or this line never appears, the fan math isn't the
		// code you think it is.
		bool logFanDiag = screen != _lastFanScreen;
		if (logFanDiag)
		{
			_lastFanScreen = screen;
			GD.Print($"[HandFan] viewport={screen} box={boxLeft}..{boxRight} " +
					 $"center={boxCenterX} (screen/2={screen.X * 0.5f})");
		}

		float cardW = UITheme.HandCardWidth;
		float cardH = UITheme.HandCardHeight;

		// Cards flush with screen bottom
		float cardBotY = screen.Y - cardH * UITheme.HandBottomInsetFactor;
		float cardCenterY = cardBotY - cardH * 0.5f;

		// Very large radius = very flat arc = cards stay low and barely rotate
		float radius = screen.Y * UITheme.HandArcRadiusFactor;

		Vector2 arcCenter = new Vector2(boxCenterX, cardCenterY + radius);

		// Wider gap between cards
		float desiredGap = cardW * UITheme.HandCardGapFactor;
		float halfChord = desiredGap * (count - 1) * 0.5f;
		float arcSpanRad = 2f * Mathf.Asin(Mathf.Clamp(halfChord / radius, 0f, 1f));
		float arcSpan = Mathf.Clamp(arcSpanRad, Mathf.DegToRad(1f), Mathf.DegToRad(UITheme.HandMaxArcDegrees));

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

		// Second half of the diagnostic: where the cards ACTUALLY landed, in
		// GLOBAL canvas coords. If mathCenter is right but globalCenter is
		// shifted, an ancestor transform (HandUI/DeckUI/canvas) is the culprit,
		// not the fan math.
		if (logFanDiag && count > 0)
		{
			float minX = float.MaxValue, maxX = float.MinValue;
			foreach (Node child in handUIContainer.GetChildren())
			{
				if (child is not Control c)
					continue;
				var r = c.GetGlobalRect();
				minX = Mathf.Min(minX, r.Position.X);
				maxX = Mathf.Max(maxX, r.End.X);
			}
			GD.Print($"[HandFan] global card span {minX:0}..{maxX:0} " +
					 $"globalCenter={(minX + maxX) * 0.5f:0} " +
					 $"containerGlobal={handUIContainer.GlobalPosition}");
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

	private Func<int, int> _getEffectiveCost;

	/// <summary>U3e: maps a half's PRINTED mana cost to what it will actually cost,
	/// after tithe_aura. Wired by CombatManager to ManaCost.EffectiveAmount so the
	/// hand and the rules engine cannot disagree about a number the player is about
	/// to act on — the same "one formula, two readers" rule the R22 damage preview
	/// follows. Unset (menus, deck editor) = identity, so nothing outside combat
	/// changes.</summary>
	public void SetEffectiveCostProvider(Func<int, int> provider)
	{
		_getEffectiveCost = provider;
	}

	/// <summary>The taxed price of a printed cost. Identity when no provider is set.</summary>
	public int EffectiveCost(int printedCost)
		=> _getEffectiveCost?.Invoke(printedCost) ?? printedCost;

	public void RefreshAffordability()
	{
		int mana = _getMana?.Invoke() ?? 999;
		foreach (Node child in handUIContainer.GetChildren())
		{
			if (child is CardUi cardUi)
			{
				cardUi.SetReactionWindow(_reactionWindowOpen);
				cardUi.RefreshAffordability(mana);
			}
		}
	}

	// ── §7c reaction window ──────────────────────────────────────────────
	// The manager holds the flag so cards created mid-window (responder
	// switch, redraw) inherit it via RefreshAffordability's per-card sync.

	private bool _reactionWindowOpen = false;

	/// <summary>Set by CombatManager when a priority window opens/closes:
	/// non-Reflex halves darken + desaturate while open.</summary>
	public void SetReactionWindow(bool open)
	{
		if (_reactionWindowOpen == open)
			return;
		_reactionWindowOpen = open;
		foreach (Node child in handUIContainer.GetChildren())
			if (child is CardUi cardUi)
				cardUi.SetReactionWindow(open);
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
