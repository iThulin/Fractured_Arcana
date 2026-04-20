using Godot;
using System.Collections.Generic;

// ============================================================
// Replacement for your existing CardLoader.LoadCardsFromCSV call.
//
// Wire this into the same place you currently call
// CardLoader.LoadCardsFromCSV — typically in your game bootstrap
// or main scene _Ready() method.
//
// During migration you can call BOTH old and new loaders. The
// MasterCardList will just contain cards from whichever loader(s)
// you run.
// ============================================================

public static class CardLoaderV2
{
    private static bool _registered = false;

    public static void LoadCardsFromJson(string directoryPath)
    {
        if (!_registered)
        {
            CardScriptRegistry.RegisterBuiltins();
            _registered = true;
        }

        var cards = JsonCardLoader.LoadAll(directoryPath);

        // Reuse the same MasterCardList your existing code reads from.
        CardLoader.MasterCardList.AddRange(cards);

        GD.Print($"[CardLoaderV2] MasterCardList now contains {CardLoader.MasterCardList.Count} cards total.");
    }
}
