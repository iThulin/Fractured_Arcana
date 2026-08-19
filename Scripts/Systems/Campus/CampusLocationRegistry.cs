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
    // The Atlas (9) and Window (10) prototype tabs were removed once their 3D renderers
    // moved into the real strategic scene and expedition overlay. Neither was routed from
    // a campus building, so dropping them shifts nothing else.
    /// <summary>Q5: the Enchanter's Workshop — item enchanting + Cleanse.</summary>
    Workshop = 9,
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
/// <para>Landmarks with an UNFINISHED restoration arc are not routed here — clicking one opens
/// its current beat (see <c>StrategicView.OnHomeLandmarkPicked</c> / <c>CampusScreen
/// .OnCampusLandmarkClicked</c>). Once a landmark's arc is complete, <see cref="ForLandmark"/>
/// decides whether it becomes a door: the restored Observatory hosts the Hall of Records
/// (its "Night-Ledgers" beat). The mapping lives here, not in <c>CampusLandmarkData</c>, so
/// every campus route — building or landmark — is resolved by one table.</para></summary>
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

    /// <summary>Where clicking a FULLY RESTORED landmark leads, or
    /// <see cref="CampusDestination.None"/> for a landmark that is not a door. Parallels
    /// <see cref="ForBuilding"/>: a restored landmark can host a campus system the same way a
    /// building does. Only consult this once a landmark's restoration chain is complete (its
    /// <c>GetEncounter</c> returns null) — a half-restored Observatory is still a narrative beat,
    /// not a door.</summary>
    public static CampusDestination ForLandmark(string landmarkId) => landmarkId switch
    {
        // The Observatory's final beat is "The Night-Ledgers" — the restored instrument becomes
        // the Hall of Records (deal ledger + enemy Marginalia). Records is floatable, so this
        // opens in place over the city like any building door.
        "observatory" => CampusDestination.ToPanel(CampusPanelId.Records),
        _             => CampusDestination.None,
    };

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
        "workshop"   => CampusDestination.ToPanel(CampusPanelId.Workshop),
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
