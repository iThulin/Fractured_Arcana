using Godot;

public partial class GameManager : Node3D
{
    private DeckManager deckManager;

    public override void _Ready()
    {
        // ✅ Load cards once (CSV, not the old txt loader)
        if (CardDatabase.Blueprints.Count == 0)
            CardDatabase.LoadFromCsv("res://Data/cards.csv"); // <-- use your actual CSV path

        deckManager = GetNodeOrNull<DeckManager>("Player/DeckManager");
        if (deckManager == null)
        {
            GD.PrintErr("DeckManager not found at Player/DeckManager");
            return;
        }

        // ✅ Build starting deck from CardDatabase (your updated DeckManager method)
        var startingDeck = deckManager.GenerateStartingDeck(CardSchool.Generic, 4);
        deckManager.InitializeDeck(startingDeck);

        // Optional: draw opening hand
        deckManager.DrawCards(3);
    }
}