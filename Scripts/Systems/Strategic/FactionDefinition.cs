using System.Collections.Generic;

// ============================================================
// FactionDefinition.cs
//
// Purpose:        Minimal faction identity model + a static
//                 registry of the five primary factions. Phase 1
//                 needs factions to assign at world generation,
//                 but RegionDefinition carries no faction field
//                 and no faction system existed yet. This is the
//                 smallest thing that works; it is structured so
//                 it can move to JSON (like regions) later with
//                 no caller changes.
// Layer:          Data
// Collaborators:  StrategicMapGenerator.cs (assigns at gen),
//                 KingdomState.cs (ControllingFactionId),
//                 StrategicMapScreen.cs (display),
//                 FactionUnitProfile (Phase 2 — encounter comp),
//                 UITheme.cs (faction colors route through it)
// See:            open_world_refactor_v1.docx §3.1, §5
//
// NOTE on colors: ColorHex here is DATA (faction identity). Per
// the project's UI color rule, UI code must not inline new
// Color() — add a UITheme.FactionColor(id) helper that parses
// these hex strings, and have screens call that.
// ============================================================

/// <summary>One faction's identity. Id is the stable key used in
/// reputation dictionaries and KingdomState.ControllingFactionId.</summary>
public class FactionDefinition
{
    public string Id = "";
    public string DisplayName = "";

    /// <summary>One-line identity blurb for tooltips / UI.</summary>
    public string Blurb = "";

    /// <summary>
    /// Hex color string ("#RRGGBB") for the faction's territory and
    /// banner. DATA only — UI must convert via UITheme, not inline.
    /// </summary>
    public string ColorHex = "#888888";

    /// <summary>
    /// School this faction leans toward, used as a soft bias when the
    /// generator pairs factions with school-affinity regions. May be
    /// empty for factions with no magical allegiance.
    /// </summary>
    public string SchoolLean = "";
}

/// <summary>
/// Static source of the primary factions. Hardcoded for Phase 1;
/// swap the body of <see cref="All"/> for a JSON loader later and
/// nothing else changes.
/// </summary>
public static class FactionRegistry
{
    private static List<FactionDefinition> _all;

    /// <summary>The five primary factions. Built once, cached.</summary>
    public static List<FactionDefinition> All
    {
        get
        {
            _all ??= Build();
            return _all;
        }
    }

    public static FactionDefinition Get(string id)
    {
        foreach (var f in All)
            if (f.Id == id)
                return f;
        return null;
    }

    /// <summary>Display name for an id, or the id itself if unknown.</summary>
    public static string NameOf(string id)
    {
        var f = Get(id);
        return f != null ? f.DisplayName : id;
    }

    private static List<FactionDefinition> Build()
    {
        return new List<FactionDefinition>
        {
            new FactionDefinition
            {
                Id = "aegis_concordat",
                DisplayName = "The Aegis Concordat",
                Blurb = "An order of wardens who hold that magic exists to protect. " +
                        "Defensive, lawful, slow to trust and slower to forgive.",
                ColorHex = "#4A6FA5",   // steel blue
                SchoolLean = "Enchanter",
            },
            new FactionDefinition
            {
                Id = "cinderbound_pact",
                DisplayName = "The Cinderbound Pact",
                Blurb = "Sworn to the elements unleashed. They take what the world " +
                        "will not give freely and answer threats with fire.",
                ColorHex = "#C1440E",   // ember
                SchoolLean = "Elementalist",
            },
            new FactionDefinition
            {
                Id = "free_charter",
                DisplayName = "The Free Charter",
                Blurb = "A mercantile compact of independent holds. Loyal to contracts " +
                        "and coin; their friendship can be bought, and kept, fairly.",
                ColorHex = "#B8893A",   // brass
                SchoolLean = "Tinker",
            },
            new FactionDefinition
            {
                Id = "lantern_order",
                DisplayName = "The Lantern Order",
                Blurb = "Scholars and archivists who believe nothing should stay hidden. " +
                        "They trade in knowledge and remember every debt of it.",
                ColorHex = "#3D3B6E",   // indigo
                SchoolLean = "Arcanist",
            },
            new FactionDefinition
            {
                Id = "the_untamed",
                DisplayName = "The Untamed",
                Blurb = "Peoples of the wild places who keep the old pacts with living " +
                        "land. They do not recognize borders drawn on paper.",
                ColorHex = "#4E7A3A",   // moss
                SchoolLean = "Druid",
            },
        };
    }
}
