using Godot;
using System;
using System.Collections.Generic;

public partial class DeckManager : Node2D
{
    [Export] public int MaxHandSize = 5;

    public Control DropSlotInstance { get; private set; }
    private Control handUIContainer;
    private DeckUiManager uiManager;

    public List<Card> DrawPile = new();
    public List<Card> DiscardPile = new();
    public List<Card> Hand = new();

    public override void _Ready()
    {
        uiManager = GetNodeOrNull<DeckUiManager>("../../DeckUI/DeckUIManager");
        GD.Print(uiManager == null ? "DeckUIManager is NULL" : "DeckUIManager found");

        handUIContainer = GetNodeOrNull<Control>("../../DeckUI/HandUI");
        GD.Print(handUIContainer == null ? "HandUI is NULL" : "HandUI found");
    }

    public void InitializeDeck(List<Card> startingDeck)
    {
        DrawPile = new List<Card>(startingDeck);
        ShuffleDrawPile();
        uiManager.SafeRefreshUI();
    }

    public List<Card> GenerateStartingDeck(CardSchool school, int count = 5)
    {
        // ✅ No CardLoader, returns fresh instances
        return CardDatabase.BuildRandomDeck(school, count);
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
            if (DrawPile.Count == 0 && DiscardPile.Count == 0)
            {
                GD.Print("No cards left to draw!");
                return;
            }

            if (Hand.Count >= MaxHandSize)
            {
                GD.Print("Hand is full!");
                return;
            }

            if (DrawPile.Count == 0) Reshuffle();

            if (DrawPile.Count > 0)
            {
                var card = DrawPile[0];
                DrawPile.RemoveAt(0);
                Hand.Add(card);

                GD.Print($"Drew card: {card.TopHalf?.Name ?? card.CardName} / {card.BottomHalf?.Name ?? ""}");
            }
        }
        uiManager.SafeRefreshUI();
    }

    public void RemoveCardFromHand(Card card)
    {
        if (Hand.Remove(card))
            GD.Print($"Removed card: {card.TopHalf?.Name ?? card.CardName}");

        uiManager.SafeRefreshUI();
    }

    public void Reshuffle()
    {
        DrawPile.AddRange(DiscardPile);
        DiscardPile.Clear();
        ShuffleDrawPile();
        uiManager.SafeRefreshUI();
    }

    public void DiscardCard(Card card)
    {
        if (Hand.Remove(card))
            DiscardPile.Add(card);

        uiManager.SafeRefreshUI();
    }

    public void PrintDeckState()
    {
        GD.Print($"Draw: {DrawPile.Count}, Hand: {Hand.Count}, Discard: {DiscardPile.Count}");
    }
}