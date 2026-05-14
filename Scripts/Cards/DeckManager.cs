using Godot;
using System;
using System.Collections.Generic;

public partial class DeckManager : Node2D
{
    [Export] public int MaxHandSize = 5;

    private DeckUiManager uiManager;
    private Control handUIContainer;
    public Control HandContainer => handUIContainer;

    // The currently displayed unit's deck
    private UnitDeckData _activeDeck;

    // ── Public accessors (so existing code doesn't break) ───────
    public List<Card> DrawPile => _activeDeck?.DrawPile ?? new();
    public List<Card> Hand => _activeDeck?.Hand ?? new();
    public List<Card> DiscardPile => _activeDeck?.DiscardPile ?? new();

    public override void _Ready()
    {
        uiManager = GetNodeOrNull<DeckUiManager>("../../DeckUI/DeckUIManager");
        GD.Print(uiManager == null ? "DeckUIManager is NULL" : "DeckUIManager found");

        handUIContainer = GetNodeOrNull<Control>("../../DeckUI/HandUI");
        GD.Print(handUIContainer == null ? "HandUI is NULL" : "HandUI found");
    }

    // ── Switch which unit's deck is displayed ───────────────────
    public void SetActiveDeck(UnitDeckData deck)
    {
        _activeDeck = deck;
        uiManager?.SafeRefreshUI();
    }

    public UnitDeckData GetActiveDeck() => _activeDeck;

    // ── Deck operations (delegate to active deck) ───────────────
    public void InitializeDeck(List<Card> startingDeck)
    {
        if (_activeDeck == null) return;
        _activeDeck.Initialize(startingDeck);
        uiManager?.SafeRefreshUI();
    }

    public List<Card> GenerateStartingDeck(CardSchool school, int count = 5)
    {
        return CardDatabase.BuildRandomDeck(school, count);
    }

    public void ShuffleDrawPile()
    {
        _activeDeck?.Shuffle();
        uiManager?.SafeRefreshUI();
    }

    public void DrawCards(int count)
    {
        if (_activeDeck == null) return;

        var drawn = _activeDeck.Draw(count);
        foreach (var card in drawn)
            GD.Print($"Drew: {card.TopHalf?.Name ?? card.CardName} / {card.BottomHalf?.Name ?? ""}");

        uiManager?.SafeRefreshUI();
        CallDeferred(nameof(DeferredRefreshDiscardFlags));
    }

    private void DeferredRefreshDiscardFlags()
    {
        RefreshDiscardFlags();
    }

    public void RemoveCardFromHand(Card card)
    {
        if (_activeDeck == null) return;
        if (_activeDeck.Hand.Remove(card))
            GD.Print($"Removed card: {card.TopHalf?.Name ?? card.CardName}");
        uiManager?.SafeRefreshUI();
    }

    public void Reshuffle()
    {
        _activeDeck?.Reshuffle();
        uiManager?.SafeRefreshUI();
    }

    public void DiscardCard(Card card)
    {
        if (_activeDeck == null) return;
        _activeDeck.Discard(card);
        uiManager?.SafeRefreshUI();
    }

    public void PrintDeckState()
    {
        if (_activeDeck == null) { GD.Print("No active deck."); return; }
        GD.Print($"DeckManager state — Draw: {_activeDeck.DrawPile.Count}, Hand: {_activeDeck.Hand.Count}, Discard: {_activeDeck.DiscardPile.Count}");
    }

    public void RefreshDiscardFlags()
    {
        if (_activeDeck == null || handUIContainer == null) return;

        int overflowCount = _activeDeck.Hand.Count - _activeDeck.MaxHandSize;

        var cardUis = new List<CardUi>();
        foreach (Node child in handUIContainer.GetChildren())
        {
            if (child is CardUi cui) cardUis.Add(cui);
        }

        for (int i = 0; i < cardUis.Count; i++)
        {
            bool shouldFlag = overflowCount > 0 && i < overflowCount;
            cardUis[i].SetDiscardFlagged(shouldFlag);
        }
    }
}