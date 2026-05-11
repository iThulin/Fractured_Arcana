public static class PlayerSession
{
    public static CardSchool SelectedSchool = CardSchool.Elementalist;
    public static bool DebugMode = false;
    public static int DeckSize = 10;

    // ── Debug flags (only active when DebugMode = true) ─────────────────
    public static bool NoFog = false;              // reveal all hexes
    public static bool UnlimitedSteps = false;     // step budget never decreases
    public static bool GodModeHP = false;          // HP never drops below 1
    public static bool StartWithGold = false;      // begin run with 500 gold
    public static bool SkipDeployment = false;     // auto-place units in combat

    // Force a specific POI type for the next encounter (-1 = no override)
    public static int ForceNextEncounterType = -1; // maps to OverworldHex.POIType int value
}