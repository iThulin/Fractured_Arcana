using Godot;

// ============================================================
// SchoolColors.cs
//
// Purpose:        Maps each CardSchool to its visual identity:
//                 card border colour, mana pip colour, and the
//                 short text badge shown in the corner.
// Layer:          Style
// Collaborators:  CardUi.cs (card border + pip colours),
//                 CardLibraryUi.cs, CampusScreen.cs, ClassSelectUi.cs
//                 (anywhere a school's identity is rendered)
// See:            README §3 — six-school identity is one of the
//                 game's core design pillars
// ============================================================

/// <summary>
/// Static lookup table mapping <see cref="CardSchool"/> values to their visual identity.
/// Three accessors: a bright border colour, a darker variant for pips and badges, and a
/// 1–2 character badge label. Unknown / Generic schools fall back to neutral greys.
/// </summary>
public static class SchoolColors
{
    /// <summary>Bright accent colour used as the card border and primary school highlight.</summary>
    public static Color GetBorderColor(CardSchool school) => school switch
    {
        CardSchool.Elementalist => new Color("#D85A30"),
        CardSchool.Arcanist => new Color("#534AB7"),
        CardSchool.Necromancer => new Color("#5F5E5A"),
        CardSchool.Enchanter => new Color("#1D9E75"),
        CardSchool.Tinker => new Color("#BA7517"),
        CardSchool.Chronomancer => new Color("#378ADD"),
        _ => new Color("#888780"),  // Generic
    };

    /// <summary>Darker variant of the school colour used for mana pips and badge backgrounds.</summary>
    public static Color GetDarkColor(CardSchool school) => school switch
    {
        CardSchool.Elementalist => new Color("#993C1D"),
        CardSchool.Arcanist => new Color("#3C3489"),
        CardSchool.Necromancer => new Color("#444441"),
        CardSchool.Enchanter => new Color("#0F6E56"),
        CardSchool.Tinker => new Color("#854F0B"),
        CardSchool.Chronomancer => new Color("#185FA5"),
        _ => new Color("#5F5E5A"),
    };

    /// <summary>Short 1–2 character label shown in the school badge corner of a card.</summary>
    public static string GetBadgeText(CardSchool school) => school switch
    {
        CardSchool.Elementalist => "El",
        CardSchool.Arcanist => "A",
        CardSchool.Necromancer => "N",
        CardSchool.Enchanter => "En",
        CardSchool.Tinker => "T",
        CardSchool.Chronomancer => "Ch",
        _ => "G",
    };
}
