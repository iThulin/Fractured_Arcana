// ============================================================
// RunResultsData.cs
//
// Purpose:        Static carrier for end-of-run summary stats
//                 surfaced on the campus screen. Set by the run
//                 manager when a run ends, cleared by the campus
//                 after the summary is displayed.
// Layer:          Data
// Collaborators:  OverworldRunManager.cs (writer),
//                 CampusScreen.cs (reader)
// See:            (none)
// ============================================================

/// <summary>Static scratchpad for the most recent run's summary stats. Lives only between run-end and the campus summary render — campus calls <see cref="Clear"/> after consuming.</summary>
public static class RunResultData
{
    public static bool HasResults { get; private set; } = false;

    public static bool ReachedObjective;
    public static int GoldEarned;
    public static int  ArcaneSplinters;
    public static int EncountersWon;
    public static int HPRemaining;

    public static void Set(bool reachedObjective, int gold,
                        int encounters, int hp, int splinters = 0)
    {
        ReachedObjective = reachedObjective;
        GoldEarned       = gold;
        EncountersWon    = encounters;
        HPRemaining      = hp;
        ArcaneSplinters  = splinters;  // ← add this line
        HasResults       = true;
    }

    public static void Clear()
    {
        HasResults = false;
    }
}