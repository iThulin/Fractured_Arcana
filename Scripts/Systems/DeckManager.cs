using Godot;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

public partial class DeckManager : Node2D
{
    [Export] public int MaxHandSize = 5;

    public Control DropSlotInstance { get; private set; }
    private Control handUIContainer;
    private DeckUiManager uiManager;

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
        uiManager = GetNodeOrNull<DeckUiManager>("../../DeckUI/DeckUIManager");
        GD.Print(uiManager == null ? "DeckUIManager is NULL" : "DeckUIManager found");

        handUIContainer = GetNodeOrNull<Control>("../../DeckUI/HandUI");
        GD.Print(handUIContainer == null ? "HandUI is NULL" : "HandUI found");

        PrintDeckState();
    }

    public override void _Input(InputEvent @event)
    {

    }

    public void InitializeDeck(List<SplitCard> startingDeck)
    {
        DrawPile = new List<SplitCard>(startingDeck);
        ShuffleDrawPile();
        uiManager.SafeRefreshUI();
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
                School = CardSchool.Elemental,
                Type = CardType.Attack,
                Target = TargetType.Self,
                Effects = new Dictionary<string, float> { { "damage", i * 2 } }
            };

            var bottom = new CardData
            {
                CardName = $"Bottom {i}",
                Description = $"Bottom spell {i}.",
                ManaCost = i % 4 + 1,
                School = CardSchool.Elemental,
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

        //uiManager.RefreshUI();
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

                GD.Print($"Drew card: {card.TopData.CardName} / {card.BottomData.CardName}");
            }
        }
        uiManager.SafeRefreshUI();
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

        uiManager.SafeRefreshUI();
    }

    public void RemoveCardFromHand(SplitCard card)
    {
        if (Hand.Remove(card))
        {
            //RefreshHandUI(); Call Deck UI Manager
            GD.Print($"Removed card: {card.TopData.CardName}");
        }
        uiManager.SafeRefreshUI();
    }
    public void Reshuffle()
    {
        DrawPile.AddRange(DiscardPile);
        DiscardPile.Clear();
        ShuffleDrawPile();
        uiManager.SafeRefreshUI();
    }

    public void DiscardCard(SplitCard card)
    {
        if (Hand.Contains(card))
        {
            Hand.Remove(card);
            DiscardPile.Add(card);
        }
        uiManager.SafeRefreshUI();
    }

    public void PrintDeckState()
    {
        GD.Print($"Draw: {DrawPile.Count}, Hand: {Hand.Count}, Discard: {DiscardPile.Count}");
    }
}
