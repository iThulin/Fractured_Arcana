// ============================================================
// CampusLocationRegistry.cs
//
// Purpose:        Resolves a thing on the campus map — a building
//                 today, a landmark or fixture later — to what
//                 clicking it should OPEN. The routing table the
//                 diegetic campus navigates by.
// Layer:          Data
// Collaborators:  BuildingDefinition.cs (Building.HostsSystem —
//                 the authored key this reads), BuildingDatabase.cs,
//                 CampusScreen.cs (the only caller today)
// See:            docs/foundational_buildings_v1.md §2;
//                 docs/campus_tab_extraction_v1.md §7
// ============================================================

/// <summary>Which campus panel is showing. Values are the tab-bar indices, and
/// <c>CampusScreen.BuildUI</c> asserts that this enum and its <c>tabNames</c> array stay the
/// same length — reorder one without the other and every door on the map opens the wrong room,
/// with no compile error to catch it.</summary>
public enum CampusPanelId
{
    Guild = 0,
    Companions = 1,
    Campus = 2,
    Expedition = 3,
    Armory = 4,
    Training = 5,
    Records = 6,
    Quests = 7,
    Council = 8,
    /// <summary>3D world-map prototype (WorldAtlas3D) — side-by-side comparison
    /// against the 2D strategic view. Not routed from any campus building.</summary>
    Atlas = 9,
}

/// <summary>What activating a campus location does. Two shapes, because the campus systems
/// are not uniform: most are panels on this screen, but the Sanctum's deck editor is a
/// separate SCENE (<c>DeckEditor.tscn</c>), as are the card library and upgrade screens.
///
/// Modelling this as "open a panel OR change scene" rather than forcing everything into a
/// panel id is the honest shape — collapsing it would mean either inventing placeholder
/// panels or special-casing the Sanctum at the call site, and the second one is how routing
/// tables rot.</summary>
public readonly struct CampusDestination
{
    /// <summary>Panel to show, or null when this destination is a scene change.</summary>
    public readonly CampusPanelId? Panel;

    /// <summary>Scene to load, or null when this destination is a panel.</summary>
    public readonly string ScenePath;

    /// <summary>False when a location has no authored destination — an ordinary constructed
    /// building. Those keep the existing select-and-label behaviour; they are not doors.</summary>
    public bool IsValid => Panel.HasValue || !string.IsNullOrEmpty(ScenePath);

    private CampusDestination(CampusPanelId? panel, string scenePath)
    {
        Panel = panel;
        ScenePath = scenePath;
    }

    public static CampusDestination ToPanel(CampusPanelId id) => new(id, null);
    public static CampusDestination ToScene(string path) => new(null, path);
    public static readonly CampusDestination None = new(null, null);
}

/// <summary>The campus routing table: map location → what it opens.
///
/// <para>Deliberately keyed off <see cref="Building.HostsSystem"/> in the building JSON rather
/// than a hardcoded id switch here. The foundational buildings were authored with those keys
/// (<c>guild</c>, <c>expedition</c>, <c>companions</c>, <c>armory</c>, <c>deck</c>) precisely so
/// this step would be data, not code — adding a door means editing one JSON file, and a
/// building with no key is simply not a door.</para>
///
/// <para>Landmarks are NOT routed here yet. All six have live restoration arcs, and clicking
/// one opens its current beat — see <c>CampusScreen.OnCampusLandmarkClicked</c>. Whether a
/// restored landmark should then become a door (the Observatory hosting dossiers, the Gatehouse
/// hosting expedition) is an open design question, not a wiring one.</para></summary>
public static class CampusLocationRegistry
{
    /// <summary>Where clicking this building leads, or <see cref="CampusDestination.None"/>
    /// for an ordinary building with no authored <c>hostsSystem</c>.</summary>
    public static CampusDestination ForBuilding(string buildingId)
    {
        if (string.IsNullOrEmpty(buildingId))
            return CampusDestination.None;
        var template = BuildingDatabase.GetTemplate(buildingId);
        return template == null ? CampusDestination.None : ForSystemKey(template.HostsSystem);
    }

    /// <summary>Resolve an authored <c>hostsSystem</c> key. Unknown or empty keys return
    /// <see cref="CampusDestination.None"/> rather than throwing — a typo in JSON should make
    /// a building inert, not crash the campus.</summary>
    public static CampusDestination ForSystemKey(string key) => key switch
    {
        "guild"      => CampusDestination.ToPanel(CampusPanelId.Guild),
        "companions" => CampusDestination.ToPanel(CampusPanelId.Companions),
        "expedition" => CampusDestination.ToPanel(CampusPanelId.Expedition),
        "armory"     => CampusDestination.ToPanel(CampusPanelId.Armory),
        "training"   => CampusDestination.ToPanel(CampusPanelId.Training),
        "records"    => CampusDestination.ToPanel(CampusPanelId.Records),
        "quests"     => CampusDestination.ToPanel(CampusPanelId.Quests),
        "council"    => CampusDestination.ToPanel(CampusPanelId.Council),
        // Scene destinations. These are the campus systems that were never tabs — they
        // are reached today only as buttons on the Guild tab, which is exactly the
        // arrangement the diegetic campus replaces: the Arcane Library IS the card
        // library, and the Scriptorum IS where cards are refined.
        "deck"         => CampusDestination.ToScene("res://Scenes/UI/DeckEditor.tscn"),
        "card_library" => CampusDestination.ToScene("res://Scenes/UI/CardLibrary.tscn"),
        "card_upgrade" => CampusDestination.ToScene("res://Scenes/UI/CardUpgradeScreen.tscn"),
        // NOT routed: the Dissolution Chamber. Disenchanting has no screen of its own —
        // it is a capability INSIDE DeckEditorUi, gated on the card_disenchant feature
        // flag. Pointing it at DeckEditor.tscn would put two doors on one room, which is
        // worse on a map than in a tab bar because the player has to guess which building
        // to walk to.
        _            => CampusDestination.None,
    };
}
