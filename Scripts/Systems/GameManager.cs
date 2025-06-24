using Godot;
using System;
using System.Collections.Generic;

public partial class GameManager : Node3D
{

    private DeckManager deckManager;

    public override void _Ready()
    {
        CardLoader.LoadCardsFromCSV("res://Data/MasterCards.txt");
        deckManager = GetNode<DeckManager>("Player/DeckManager");

        deckManager.InitializeDeck(deckManager.GenerateStartingDeck(CardSchool.Engineer, 10));
        deckManager.DrawCards(10);
    }
}
