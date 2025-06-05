using Godot;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

public partial class DeckManager : Node2D
{
    [Export] public PackedScene CardUIPackedScene;
    [Export] public PackedScene DropSlotScene;
    [Export] public int MaxHandSize = 5;

    public Control DropSlotInstance { get; private set; }
    private Control handUIContainer;

    private Label deckCountLabel;
    private Label handCountLabel;
    private Label discardCountLabel;

    private Button drawButton;
    private Button discardButton;
    private Button reshuffleButton;
    private Button removeButton;

    public struct SplitCard
    {
        public CardData TopData;
        public CardData BottomData;

        public SplitCard(CardData top, CardData bottom)
        {
            TopData = top;
            BottomData = bottom;
        }
    }

    public List<SplitCard> DrawPile = new();
    public List<SplitCard> DiscardPile = new();
    public List<SplitCard> Hand = new();

    public override void _Ready()
    {
        handUIContainer = GetNode<Control>("../../CanvasLayer/HandUI");
        DropSlotInstance = DropSlotScene.Instantiate<Control>();

        deckCountLabel = GetNode<Label>("../../CanvasLayer/DeckCountLabel");
        handCountLabel = GetNode<Label>("../../CanvasLayer/HandCountLabel");
        discardCountLabel = GetNode<Label>("../../CanvasLayer/DiscardCountLabel");

        drawButton = GetNode<Button>("../../DrawButton");
        discardButton = GetNode<Button>("../../DiscardButton");
        reshuffleButton = GetNode<Button>("../../ReshuffleButton");
        removeButton = GetNode<Button>("../../RemoveButton");

        PositionHandCards();

        drawButton.Pressed += OnDrawButtonPressed;
        discardButton.Pressed += OnDiscardButtonPressed;
        reshuffleButton.Pressed += OnReshuffleButtonPressed;
        removeButton.Pressed += OnRemoveButtonPressed;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            if (keyEvent.Keycode == Key.D)
            {
                DrawCards(1);
                PositionHandCards();
            }

            if (keyEvent.Keycode == Key.R)
            {
                RemoveLastCardFromHand();
                PositionHandCards();
            }
        }
    }

    private void UpdateCardCounts()
    {
        deckCountLabel.Text = $"{DrawPile.Count}";
        handCountLabel.Text = $"Hand: {Hand.Count}";
        discardCountLabel.Text = $"Discard: {DiscardPile.Count}";
    }

    public void InitializeDeck(List<SplitCard> startingDeck)
    {
        DrawPile = new List<SplitCard>(startingDeck);
        ShuffleDrawPile();
    }

    public List<SplitCard> GenerateTestDeck(int count)
    {
        var testDeck = new List<SplitCard>();
        for (int i = 1; i <= count; i++)
        {
            var top = new CardData
            {
                CardName = $"Top {i}",
                Description = $"Top spell {i}.",
                ManaCost = i % 3 + 1,
                School = "Fire",
                Type = CardType.Attack,
                Target = TargetType.Self,
                Effects = new Dictionary<string, float> { { "damage", i * 2 } }
            };

            var bottom = new CardData
            {
                CardName = $"Bottom {i}",
                Description = $"Bottom spell {i}.",
                ManaCost = i % 4 + 1,
                School = "Ice",
                Type = CardType.Skill,
                Target = TargetType.Self,
                Effects = new Dictionary<string, float> { { "block", i } }
            };

            testDeck.Add(new SplitCard(top, bottom));
        }
        return testDeck;
    }

    public void ShuffleDrawPile()
    {
        var rand = new Random();
        for (int i = DrawPile.Count - 1; i > 0; i--)
        {
            int j = rand.Next(i + 1);
            (DrawPile[i], DrawPile[j]) = (DrawPile[j], DrawPile[i]);
        }
    }

    public void DrawCards(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (Hand.Count >= MaxHandSize) return;
            if (DrawPile.Count == 0) Reshuffle();
            if (DrawPile.Count > 0)
            {
                var card = DrawPile[0];
                DrawPile.RemoveAt(0);
                Hand.Add(card);

                GD.Print($"Drew card: {card.TopData.CardName} / {card.BottomData.CardName}");
            }
        }
        RefreshHandUI();
    }

    private void RemoveLastCardFromHand()
    {
        int count = handUIContainer.GetChildCount();
        if (count == 0) return;

        var cardNode = handUIContainer.GetChild(count - 1);
        handUIContainer.RemoveChild(cardNode);
        cardNode.QueueFree();

        if (Hand.Count > 0)
            Hand.RemoveAt(Hand.Count - 1);
    }

    private void PositionHandCards()
    {
        int count = handUIContainer.GetChildCount();
        if (count == 0) return;

        Vector2 screenHeight = GetViewport().GetVisibleRect().Size;
        float radius = screenHeight.Y * 4f;

        Vector2 arcCenter = new Vector2(handUIContainer.Size.X / 2f, handUIContainer.Size.Y + radius * .95f);

        float maxArcSpanDeg = 20f;
        float minArcSpanDeg = 1f;
        float stepPerCard = 1.5f;
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
                card.Rotation = angle * 0.5f;
            }
        }
        UpdateCardCounts();
    }

    public void Reshuffle()
    {
        DrawPile.AddRange(DiscardPile);
        DiscardPile.Clear();
        ShuffleDrawPile();
    }

    public void DiscardCard(SplitCard card)
    {
        if (Hand.Contains(card))
        {
            Hand.Remove(card);
            DiscardPile.Add(card);
        }
    }

    private void OnDiscardButtonPressed()
    {
        if (Hand.Count == 0) return;

        var card = Hand[^1];
        Hand.RemoveAt(Hand.Count - 1);
        DiscardPile.Add(card);

        if (handUIContainer.GetChildCount() > 0)
        {
            handUIContainer.GetChild(handUIContainer.GetChildCount() - 1).QueueFree();
        }

        RefreshHandUI();
        GD.Print($"Discarded: {card.TopData.CardName} / {card.BottomData.CardName}");
    }

    private void OnReshuffleButtonPressed()
    {
        DrawPile.AddRange(DiscardPile);
        DiscardPile.Clear();
        ShuffleDrawPile();
        RefreshHandUI();
        GD.Print("Reshuffled discard pile into draw pile");
    }

    private void OnDrawButtonPressed()
    {
        DrawCards(1);
    }

    private void OnRemoveButtonPressed()
    {
        if (Hand.Count == 0) return;

        var card = Hand[^1];
        Hand.RemoveAt(Hand.Count - 1);
        DiscardPile.Add(card);

        RefreshHandUI();
        GD.Print($"Removed (discarded): {card.TopData.CardName} / {card.BottomData.CardName}");
    }

    private void RefreshHandUI()
    {
        List<Node> toRemove = new();
        foreach (Node child in handUIContainer.GetChildren())
        {
            if (child is CardUi)
                toRemove.Add(child);
        }
        foreach (Node node in toRemove)
        {
            handUIContainer.RemoveChild(node);
            node.QueueFree();
        }

        foreach (var card in Hand)
        {
            CardUi cardUiInstance = CardUIPackedScene.Instantiate<CardUi>();
            cardUiInstance.SetCard(card.TopData, card.BottomData);
            handUIContainer.AddChild(cardUiInstance);
            cardUiInstance.CardDropped += () => PositionHandCards();
        }

        PositionHandCards();
    }
}
