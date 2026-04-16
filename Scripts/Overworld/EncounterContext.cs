/// <summary>
/// Data passed from the overworld to the combat scene.
/// Describes the encounter parameters based on overworld context.
/// Phase 1: mostly defaults. Phase 2+ adds terrain→theme mapping,
/// difficulty→density mapping, weather modifiers, etc.
/// </summary>
public class EncounterContext
{
    // What triggered this encounter
    public OverworldHex.POIType SourcePOI;
    public OverworldHex.TerrainType SourceTerrain;

    // Combat parameters (Phase 2+ will map these from overworld context)
    public int EnemyCount = 3;
    public int PlayerCount = 2;

    // Results (filled after combat completes)
    public bool PlayerWon;
    public int GoldReward;
    public int DamageTaken;
}