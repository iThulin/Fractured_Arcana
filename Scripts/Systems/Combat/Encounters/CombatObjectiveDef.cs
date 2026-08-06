using System.Collections.Generic;

// ============================================================
// CombatObjectiveDef.cs
//
// Purpose:        Optional per-encounter objective + reinforcement
//                 wave data (O-track). An EncounterDefinition with
//                 no Objective and no Waves behaves exactly as it
//                 did before this file existed — bit for bit.
// Layer:          Data
// Collaborators:  EncounterDefinition.cs (host),
//                 EncounterPoolLoader.cs (JSON source),
//                 CombatManager.Objectives.cs (runtime),
//                 CombatUI.cs (banner line)
// See:            docs/combat_objectives_spec_v1.md 1-3
// ============================================================

/// <summary>
/// What a combat is FOR, when it is not simply "kill everything".
/// Evaluated at the single true round boundary (CombatManager.StartEnemyTurn,
/// after roundNumber++) so every state change is a fact the player can read
/// off the phase banner.
/// </summary>
public class CombatObjectiveDef
{
    public const string KindAnnihilate = "annihilate";
    public const string KindSurvive = "survive";
    public const string KindProtect = "protect";
    public const string KindHoldZone = "hold_zone";

    /// <summary>"annihilate" (default) | "survive" | "protect" | "hold_zone".</summary>
    public string Kind = KindAnnihilate;

    /// <summary>survive / hold_zone: victory at the END of this round (1-based).
    /// protect: optional — 0 means "kill them all before they kill the ward".</summary>
    public int Rounds = 0;

    /// <summary>protect: UnitRegistry id spawned player-side as the ward. (O3)</summary>
    public string WardUnitId = "";

    /// <summary>hold_zone: enemy-occupied round-ends tolerated before defeat. (O4)</summary>
    public int BreachLimit = 2;

    /// <summary>hold_zone siting: "player_spawn" (default) | "ward" | "center". (O4)</summary>
    public string ZoneAnchor = "player_spawn";

    public int ZoneRadius = 2;

    /// <summary>Banner label. Empty means the runtime generates one.</summary>
    public string Description = "";

    public static bool IsKnownKind(string kind) =>
        kind == KindAnnihilate || kind == KindSurvive ||
        kind == KindProtect || kind == KindHoldZone;

    /// <summary>True for kinds whose runtime this build actually implements.
    /// Authoring a not-yet-built kind is a loud load-time error, not a fight
    /// that silently behaves as annihilate.</summary>
    public static bool IsImplementedKind(string kind) =>
        kind == KindAnnihilate || kind == KindSurvive;
}

/// <summary>
/// One reinforcement group. Spawns at the START of the named round (1-based),
/// i.e. at the boundary entering it. Round 1 is invalid — that is the initial
/// roster's job, and the loader rejects it.
/// </summary>
public class ReinforcementWave
{
    public int Round = 3;
    public List<EnemySlot> Enemies = new();

    /// <summary>Log/banner line. Empty means a generic arrival line.</summary>
    public string Announce = "";
}
