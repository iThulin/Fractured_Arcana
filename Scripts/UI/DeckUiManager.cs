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
		// Clear UI
		foreach (Node child in handUIContainer.GetChildren())
		{
			if (child is CardUi cardUi)
				cardUi.QueueFree();
		}

		// Rebuild UI from deck manager state
		foreach (var card in deckManager.Hand)
		{
			var cardUi = CardUIPackedScene.Instantiate<CardUi>();
			cardUi.SetCard(card);
			cardUi.CardDropped += () => PositionHandCards();
			handUIContainer.AddChild(cardUi);
		}

		await ToSignal(GetTree().CreateTimer(0.0f), "timeout");

		PositionHandCards();
	}

	public void SafeRefreshUI()
	{
		_ = RefreshUI(); // fire-and-forget; doesn't block
	}

	private void PositionHandCards()
	{
		int count = handUIContainer.GetChildCount();
		if (count == 0) return;

		Vector2 screenSize = GetViewport().GetVisibleRect().Size;
		float radius = screenSize.Y * 4f;

		Vector2 arcCenter = new Vector2(screenSize.X / 2f, screenSize.Y + radius * .95f);

		float maxArcSpanDeg = 40f;
		float minArcSpanDeg = .5f;
		float stepPerCard = 2f;
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
				card.Rotation = angle * 1f;
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
}
