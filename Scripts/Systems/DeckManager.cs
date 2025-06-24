using Godot;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;

public partial class DeckManager : Node2D
{
    [Export] public int MaxHandSize = 5;
    [Export] public NodePath CardLoaderPath;
    //private CardLoader cardLoader;

    public Control DropSlotInstance { get; private set; }
    private Control handUIContainer;
    private DeckUiManager uiManager;

    public List<SplitCard> DrawPile = new();
    public List<SplitCard> DiscardPile = new();
    public List<SplitCard> Hand = new();

    public override void _Ready()
    {
        uiManager = GetNodeOrNull<DeckUiManager>("../../DeckUI/DeckUIManager");
        GD.Print(uiManager == null ? "DeckUIManager is NULL" : "DeckUIManager found");

        handUIContainer = GetNodeOrNull<Control>("../../DeckUI/HandUI");
        GD.Print(handUIContainer == null ? "HandUI is NULL" : "HandUI found");
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

    public List<SplitCard> GenerateStartingDeck(CardSchool school, int count = 5)
    {
        var rng = new Random();

        // Filter only cards fom school
        var candidates = CardLoader.MasterCardList
        .Where(c => c.TopData.School == school && c.BottomData.School == school)
        .ToList();

        GD.Print($"{candidates.Count} {school} cards available. Selecting {count} cards for starting deck.");

        if (candidates.Count < count)
        {
            GD.PrintErr($"Not Enough cards for {school}. Only {candidates.Count} available.");
            return new List<SplitCard>();
        }

        // Shuffle candidates
        for (int i = 0; i < candidates.Count; i++)
        {
            int j = rng.Next(i, candidates.Count);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        // Select the first 'count' cards
        return candidates.Take(count).ToList();
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
