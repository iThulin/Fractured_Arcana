using Godot;
using System.Collections.Generic;
using System.Text.RegularExpressions;

// ============================================================
// CardTextMarkup.cs
//
// Purpose:        Render-time emphasis for card rules text. Takes the
//                 authored rules_text (card_text_style_guide_v1) and
//                 returns BBCode with decision numbers in bold and
//                 status / school-resource keywords in bold keyword ink.
//                 The authored string is never changed; both the split
//                 halves and the full view run the same pass, so the two
//                 views still read identically.
// Layer:          UI
// Collaborators:  CardUi.cs (only caller), UITheme.cs (keyword ink)
// See:            docs/card_text_style_guide_v1.md (the keyword list is
//                 the guide's capitalization section, verbatim)
// ============================================================

/// <summary>
/// Turns plain rules text into BBCode for a <see cref="RichTextLabel"/>. Bold on
/// every number a player weighs when choosing a half (damage, range, shield,
/// draw), never on durations, ordinals, multipliers, or unit stat blocks, which
/// are reference data. Keywords come from the style guide's closed vocabulary.
/// </summary>
public static class CardTextMarkup
{
    // Capitalized status keywords + school resources from the style guide.
    private static readonly string[] Keywords =
    {
        "Slowed", "Rooted", "Frozen", "Stunned", "Burned", "Poisoned", "Weakened",
        "Blinded", "Haunted", "Hexed", "Named", "Delayed", "Shrouded", "Undying",
        "Hasted",
        "Charge", "Foresight", "Wilding", "Grief", "Weave", "Heat", "Schematics",
    };

    private static readonly Regex KeywordRx = new Regex(
        @"\b(" + string.Join("|", Keywords) + @")\b", RegexOptions.Compiled);

    // Numbers that are NOT decision numbers. Each alternative is lifted out of
    // the text before the bold pass and restored afterwards.
    //   \d+ turn(s)        durations and setup time ("for 2 turns", "1 turn of setup")
    //   \d+(st|nd|rd|th)   ordinals ("2nd+ spell")
    //   x\d+               multipliers ("hand x2")
    //   ( ... HP ... )     unit stat blocks ("(HP 12 / DMG 5 / SPD 2)", "(2 HP each)")
    private static readonly Regex ProtectRx = new Regex(
        @"\d+\s+turns?\b|\d+(?:st|nd|rd|th)\b|\bx\d+\b|\([^()]*\bHP\b[^()]*\)",
        RegexOptions.Compiled);

    private static readonly Regex NumberRx = new Regex(@"(?<![\w+\-\u0001])[+\-]?\d+\b", RegexOptions.Compiled);

    private static string _keywordHex;

    /// <summary>
    /// BBCode for <paramref name="rulesText"/>. Safe on null or empty (returns "").
    /// Literal square brackets in the source are escaped so authored text can
    /// never open a tag.
    /// </summary>
    public static string Emphasize(string rulesText)
    {
        if (string.IsNullOrEmpty(rulesText)) return "";

        _keywordHex ??= UITheme.CardKeywordInk.ToHtml(false);

        string text = rulesText.Replace("[", "[lb]").Replace("]", "[rb]");

        // Lift protected spans out so the number pass cannot see them.
        var held = new List<string>();
        text = ProtectRx.Replace(text, m =>
        {
            held.Add(m.Value);
            return "" + (held.Count - 1) + "";
        });

        text = NumberRx.Replace(text, m => "[b]" + m.Value + "[/b]");
        text = KeywordRx.Replace(text, m =>
            "[b][color=#" + _keywordHex + "]" + m.Value + "[/color][/b]");

        for (int i = 0; i < held.Count; i++)
            text = text.Replace("" + i + "", held[i]);

        return text;
    }
}
