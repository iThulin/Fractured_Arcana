using Godot;
using System;
using System.Collections.Generic;
using Microsoft.VisualBasic.FileIO;

public static class CardLoader
{
    public static List<SplitCard> MasterCardList { get; private set; } = new();

    public static void LoadCardsFromCSV(string path)
    {
        MasterCardList.Clear();

        using var fileStream = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (fileStream == null)
        {
            GD.PrintErr("Could not open card CSV file.");
            return;
        }

        // Read entire file content into a string
        string csvText = fileStream.GetAsText();

        using var reader = new System.IO.StringReader(csvText);
        using var parser = new TextFieldParser(reader);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");

        parser.ReadLine(); // Skip header

        while (!parser.EndOfData)
        {
            string[] fields = parser.ReadFields();

            if (fields.Length < 21) continue;

            // Parse Enums and fields
            Enum.TryParse(fields[0], true, out CardSchool school);
            int.TryParse(fields[2], out int topManaCost);
            int.TryParse(fields[18], out int bottomManaCost);

            float.TryParse(fields[7], out float topValue);
            float.TryParse(fields[10], out float topChannelValue);
            float.TryParse(fields[17], out float bottomValue);
            float.TryParse(fields[20], out float bottomChannelValue);

            var top = new CardData
            {
                CardName = fields[1],
                Description = fields[3],
                ChannelDescription = fields[4],
                ManaCost = topManaCost,
                School = school,
                Type = CardType.Attack,
                Target = TargetType.Self,
            };

            var bottom = new CardData
            {
                CardName = fields[17],
                Description = fields[19],
                ChannelDescription = fields[20],
                ManaCost = bottomManaCost,
                School = school,
                Type = CardType.Skill,
                Target = TargetType.Self,
            };

            MasterCardList.Add(new SplitCard(top, bottom));
        }

        GD.Print($"Loaded {MasterCardList.Count} cards to MasterCardList.");
    }


}
