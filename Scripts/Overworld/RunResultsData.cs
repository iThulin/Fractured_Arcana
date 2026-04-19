/// <summary>
/// Static data carrier for run results. Set by RunManager when a run ends,
/// read by CampusScreen to display the summary.
/// </summary>
public static class RunResultData
{
    public static bool HasResults { get; private set; } = false;

    public static bool ReachedObjective;
    public static int GoldEarned;
    public static int EncountersWon;
    public static int HPRemaining;

    public static void Set(bool reachedObjective, int gold, int encounters, int hp)
    {
        ReachedObjective = reachedObjective;
        GoldEarned = gold;
        EncountersWon = encounters;
        HPRemaining = hp;
        HasResults = true;
    }

    public static void Clear()
    {
        HasResults = false;
    }
}