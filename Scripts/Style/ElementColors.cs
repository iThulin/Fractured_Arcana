using Godot;

// ============================================================
// ElementColors.cs
//
// Purpose:        Maps element tag strings (from card JSON) to
//                 pip colors and short single-character labels.
// Layer:          Style
// Collaborators:  CardUi.cs, SchoolAttunementUI.cs (consumers of
//                 the colour/label lookups for element pips)
// See:            README §5.2 (Card Half Fields — tags) for the
//                 set of element tag strings cards may emit
// ============================================================

/// <summary>
/// Static lookup table mapping element tag strings (as written in card JSON's `tags` field)
/// to the colour shown on element pips and the short label used in compact UI. Unknown tags
/// fall back to a neutral grey colour and a "?" label rather than throwing.
/// </summary>
public static class ElementColors
{
    /// <summary>Returns the pip colour for a given element tag. Tag matching is case-insensitive; unknown tags return a neutral grey.</summary>
    public static Color Get(string tag) => (tag ?? "").ToLower() switch
    {
        "fire" => new Color("#E24B4A"),
        "ice" => new Color("#378ADD"),
        "storm" => new Color("#639922"),
        "earth" => new Color("#854F0B"),
        "arcane" => new Color("#9B59B6"),
        "necrotic" => new Color("#2ECC71"),
        "temporal" => new Color("#1ABC9C"),
        "enchant" => new Color("#E91E8C"),
        "construct" => new Color("#F39C12"),
        _ => new Color("#888888"),
    };

    /// <summary>Returns the single-character label for an element tag. Unknown tags return "?".</summary>
    public static string GetLabel(string tag) => (tag ?? "").ToLower() switch
    {
        "fire" => "Fire",
        "ice" => "Ice",
        "storm" => "Storm",
        "earth" => "Earth",
        // Future schools
        "arcane" => "Arcane",
        "necrotic" => "Necrotic",
        "temporal" => "Temporal",
        "enchant" => "Enchant",
        "construct" => "Construct",
        _ => tag, // fall back to raw tag rather than "?" so unknown tags are still readable
    };
}
