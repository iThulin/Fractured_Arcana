using Godot;
using System.Collections.Generic;

public static class CardLoader
{
    public static List<Card> MasterCardList { get; private set; } = new();

    public static void LoadCardsFromCSV(string path)
    {
        // Use the canonical reader/compiler pipeline
        var rows = CardCsvReader.Load(path, out var diags);
        foreach (var d in diags) GD.PrintErr(d);

        MasterCardList.Clear();
        foreach (var r in rows)
            MasterCardList.Add(CardCsvCompiler.Compile(r));

        GD.Print($"Loaded {MasterCardList.Count} cards to MasterCardList from {path}.");
    }
}