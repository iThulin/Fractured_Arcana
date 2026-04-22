using Godot;

/// <summary>
/// Maps element tag strings (from card JSON) to pip colors and short labels.
/// </summary>
public static class ElementColors
{
    public static Color Get(string tag) => (tag ?? "").ToLower() switch
    {
        "fire"  => new Color("#E24B4A"),
        "ice"   => new Color("#378ADD"),
        "storm" => new Color("#639922"),
        "earth" => new Color("#854F0B"),
        _       => new Color("#888888"),
    };

    public static string GetLabel(string tag) => (tag ?? "").ToLower() switch
    {
        "fire"  => "F",
        "ice"   => "I",
        "storm" => "S",
        "earth" => "E",
        _       => "?",
    };
}
