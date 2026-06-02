using Godot;

// ============================================================
// SchoolColors.cs
//
// Purpose:        Maps each CardSchool to its visual identity:
//                 card border colour, mana pip colour, and the
//                 short text badge shown in the corner.
// Layer:          Style
// Collaborators:  CardUi.cs (card border + pip colours),
//                 CardLibraryUi.cs, CampusScreen.cs,
//                 NewGameScreen.cs, ClassSelectUi.cs
// See:            README §3 — seven-school identity is one of the
//                 game's core design pillars
// ============================================================

/// <summary>
/// Static lookup table mapping <see cref="CardSchool"/> values to their visual identity.
/// Three accessors: a bright border colour, a darker variant for pips and badges, and a
/// 1–2 character badge label.
/// </summary>
public static class SchoolColors
{
    /// <summary>Bright accent colour — card border and primary school highlight.</summary>
    public static Color GetBorderColor(CardSchool school) => school switch
    {
        CardSchool.Adept => new Color("#A8A6A0"),  // warm grey
        CardSchool.Elementalist => new Color("#F06A35"),  // vivid ember orange
        CardSchool.Druid => new Color("#6DBF45"),  // living forest green
        CardSchool.Necromancer => new Color("#2BA888"),  // spectral teal-green
        CardSchool.Tinker => new Color("#E09420"),  // warm amber gold
        CardSchool.Enchanter => new Color("#C45B9E"),  // deep runic rose-magenta
        CardSchool.Arcanist => new Color("#7B6FE8"),  // bright arcane indigo
        CardSchool.Chronomancer => new Color("#5BAAEE"),  // clear sky blue
        _ => new Color("#A8A6A0"),
    };

    /// <summary>Darker variant — mana pips, badge backgrounds, tinted card fills.</summary>
    public static Color GetDarkColor(CardSchool school) => school switch
    {
        CardSchool.Adept => new Color("#6A6865"),
        CardSchool.Elementalist => new Color("#8C3A18"),
        CardSchool.Druid => new Color("#3A7A1E"),
        CardSchool.Necromancer => new Color("#1A7A68"),
        CardSchool.Tinker => new Color("#8A5A08"),
        CardSchool.Enchanter => new Color("#7A2A60"),
        CardSchool.Arcanist => new Color("#4A419E"),
        CardSchool.Chronomancer => new Color("#2470A8"),
        _ => new Color("#6A6865"),
    };

    /// <summary>Short 1–2 character badge label shown on card faces.</summary>
    public static string GetBadgeText(CardSchool school) => school switch
    {
        CardSchool.Adept => "Ad",
        CardSchool.Elementalist => "El",
        CardSchool.Druid => "Dr",
        CardSchool.Necromancer => "N",
        CardSchool.Tinker => "T",
        CardSchool.Enchanter => "En",
        CardSchool.Arcanist => "A",
        CardSchool.Chronomancer => "Ch",
        _ => "?",
    };
}
