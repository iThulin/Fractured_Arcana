using Godot;

/// <summary>
/// Maps each CardSchool to its visual identity: border color, badge text, mana pip color.
/// </summary>
public static class SchoolColors
{
    public static Color GetBorderColor(CardSchool school) => school switch
    {
        CardSchool.Elementalist  => new Color("#D85A30"),
        CardSchool.Arcanist      => new Color("#534AB7"),
        CardSchool.Necromancer   => new Color("#5F5E5A"),
        CardSchool.Enchanter     => new Color("#1D9E75"),
        CardSchool.Tinker        => new Color("#BA7517"),
        CardSchool.Chronomancer  => new Color("#378ADD"),
        _                        => new Color("#888780"),  // Generic
    };

    /// <summary>Darker variant for mana pips and badges.</summary>
    public static Color GetDarkColor(CardSchool school) => school switch
    {
        CardSchool.Elementalist  => new Color("#993C1D"),
        CardSchool.Arcanist      => new Color("#3C3489"),
        CardSchool.Necromancer   => new Color("#444441"),
        CardSchool.Enchanter     => new Color("#0F6E56"),
        CardSchool.Tinker        => new Color("#854F0B"),
        CardSchool.Chronomancer  => new Color("#185FA5"),
        _                        => new Color("#5F5E5A"),
    };

    public static string GetBadgeText(CardSchool school) => school switch
    {
        CardSchool.Elementalist  => "E",
        CardSchool.Arcanist      => "A",
        CardSchool.Necromancer   => "N",
        CardSchool.Enchanter     => "En",
        CardSchool.Tinker        => "T",
        CardSchool.Chronomancer  => "Ch",
        _                        => "G",
    };
}
