using Godot;
using System;
using System.Collections.Generic;

public static class CardLoader
{
    public static List<SplitCard> MasterCardList { get; private set; } = new();

    public static void LoadCardsFromCSV(string path)
    {
        var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            GD.PrintErr("Could not open card CSV file.");
            return;
        }

        file.GetLine(); //skip Header
        while (!file.EofReached())
        {
            string line = file.GetLine();
            var fields = line.Split(',');

            if (fields.Length < 20) continue; // Check # of columns

            // Parse Enums
            Enum.TryParse(fields[0], true, out CardSchool school);
            int.TryParse(fields[2], out int topManaCost);
            int.TryParse(fields[18], out int bottomManaCost);

            // Parse floats
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
                /*
                Effects = new Dictionary<string, st>
                {
                    { "keyword" , fields[5]}
                },
                */
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
                //Effects = new Dictionary<string, float> { { "block", i } }
            };
            MasterCardList.Add(new SplitCard(top, bottom));
        }

        GD.Print($"Loaded {MasterCardList.Count} cards to MasterCardList.");
    }


}
