using Godot;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

public partial class DeckManager : Node2D
{
    // Exported Features 
    [Export] public PackedScene CardUIPackedScene;
    [Export] public PackedScene DropSlotScene;
    [Export] public int MaxHandSize = 5;

    // Positioning tools
    public Control DropSlotInstance { get; private set; }
    private Control handUIContainer;

    // UI Elements associated with deck
    private Label deckCountLabel;
    private Label handCountLabel;
    private Label discardCountLabel;

    // Deck Management buttons ||SHOULD BE TEMPORARY||
    private Button drawButton;
    private Button discardButton;
    private Button reshuffleButton;
    private Button removeButton;

    // Card Zones for use in the deck manager
    public List<CardData> DrawPile = new();
    public List<CardData> DiscardPile = new();
    public List<CardData> Hand = new();

    public override void _Ready()
    {
        // Get position tools
        handUIContainer = GetNode<Control>("../../CanvasLayer/HandUI");
        DropSlotInstance = DropSlotScene.Instantiate<Control>();

        // Get UI elements
        deckCountLabel = GetNode<Label>("../../CanvasLayer/DeckCountLabel");
        handCountLabel = GetNode<Label>("../../CanvasLayer/HandCountLabel");
        discardCountLabel = GetNode<Label>("../../CanvasLayer/DiscardCountLabel");

        // Get buttons
        drawButton = GetNode<Button>("../../DrawButton");
        discardButton = GetNode<Button>("../../DiscardButton");
        reshuffleButton = GetNode<Button>("../../ReshuffleButton");
        removeButton = GetNode<Button>("../../RemoveButton");

        // Update card positions
        PositionHandCards();

        // TESTING FUNCTIONS !! SHOULD BE REMOVED AFTER INTEGRATION!!
        drawButton.Pressed += OnDrawButtonPressed;
        discardButton.Pressed += OnDiscardButtonPressed;
        reshuffleButton.Pressed += OnReshuffleButtonPressed;
        removeButton.Pressed += OnRemoveButtonPressed;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            // Press D to draw a card
            if (keyEvent.Keycode == Key.D)
            {
                DrawCards(1);
                PositionHandCards();
            }

            // Press R to remove the last card in the hand
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

    public void InitializeDeck(List<CardData> startingDeck)
    {
        DrawPile = new List<CardData>(startingDeck);
        ShuffleDrawPile();
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

                //CardUi cardUiInstance = CardUIPackedScene.Instantiate<CardUi>();
                //cardUiInstance.SetCard(card);
                //handUIContainer.AddChild(cardUiInstance);

                GD.Print($"Drew card: {card.CardName}");
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
        float radius = screenHeight.Y * 4f; // Adjust as needed

        // Arc center: middle X, and below container to create upward arc
        Vector2 arcCenter = new Vector2(handUIContainer.Size.X / 2f, handUIContainer.Size.Y + radius * .95f);

        float maxArcSpanDeg = 20f; // largest arc possible
        float minArcSpanDeg = 1f; // smallest arc
        float stepPerCard = 1.5f; // spacing between cards, smaller # allows more cards to stack
        float arcSpanDeg = Mathf.Min(maxArcSpanDeg, stepPerCard * (count - 1));

        arcSpanDeg = Mathf.Max(minArcSpanDeg, arcSpanDeg);
        float arcSpan = Mathf.DegToRad(arcSpanDeg);

        float angleStart = (count > 1) ? -arcSpan / 2f : 0f;
        //divide space between cards based on number of cards and allowed arc span
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

                // Store position after layout for hover reference
                //if (card is CardUi cardUi) cardUi.StoreCurrentPosition();
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

    public void DiscardCard(CardData card)
    {
        if (Hand.Contains(card))
        {
            Hand.Remove(card);
            DiscardPile.Add(card);
        }
    }

    // Testing functions !! THESE SHOULD BE REMOVED WHEN FUNCTION IS INTEGRATED!!
    public List<CardData> GenerateTestDeck(int count)
    {
        var testDeck = new List<CardData>();
        for (int i = 1; i <= count; i++)
        {
            testDeck.Add(new CardData
            {
                CardName = $"Test Card {i}",
                Description = $"This is test card number {i}.",
                ManaCost = i % 5 + 1,
                School = "Test",
                Type = CardType.Skill,
                Target = TargetType.Self,
                Effects = new Dictionary<string, float> { { "testEffect", i * 2 } }
            });
        }
        return testDeck;
    }

    private void OnDiscardButtonPressed()
    {
        if (Hand.Count == 0) return;

        var card = Hand[^1]; // Last card
        Hand.RemoveAt(Hand.Count - 1);
        DiscardPile.Add(card);

        if (handUIContainer.GetChildCount() > 0)
        {
            handUIContainer.GetChild(handUIContainer.GetChildCount() - 1).QueueFree();
        }

        RefreshHandUI();
        GD.Print($"Discarded: {card.CardName}");
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
        GD.Print($"Removed (discarded): {card.CardName}");
    }

    private void RefreshHandUI()
    {
        //GD.Print($"[RefreshHandUI] Start: {Hand.Count} cards in hand");

        // Clear only CardUi instances
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
            cardUiInstance.SetCard(card);
            handUIContainer.AddChild(cardUiInstance);
            
            cardUiInstance.CardDropped += () => PositionHandCards();
        }

        //GD.Print($"[RefreshHandUI] End: {handUIContainer.GetChildCount()} card UIs displayed");


        PositionHandCards();
    }

}
