using System.Collections.Generic;

// ============================================================
// ArchmageDefinition.cs
//
// Purpose:        Data model for one archmage — the named
//                 faction leaders who control regions in the
//                 overworld. Each archmage has a school, a
//                 faction identity (two states: normal and
//                 Chronomancer-corrupted), encounter pools for
//                 their faction's overworld presence, boss
//                 combat modifiers, and final-battle contribution
//                 data depending on how the player resolved them.
//                 Loaded from Data/Archmagi/{id}.json by
//                 ArchmageRegistry.
// Layer:          Data
// Collaborators:  ArchmageRegistry.cs (loader),
//                 CampaignState.cs (per-archmage disposition),
//                 CampaignGenerator.cs (placement),
//                 OverworldFactionManager.cs (overworld faction),
//                 FinalBattleManager.cs (final battle roster)
// See:            README §5 — Campaign Layer
// ============================================================

/// <summary>Full definition of one archmage. One instance per school, loaded from JSON. The Astrologer (Chronomancer) is included as his villain-faction definition; he is never placed in a region.</summary>
public class ArchmageDefinition
{
    // ── Identity ─────────────────────────────────────────────────────────
    public string Id = "";              // "conductor" — matches filename
    public string DisplayName = "";     // "The Conductor"
    public string Title = "";           // "Archmagus of the Dead"
    public string School = "";          // "Necromancer" — matches CardSchool enum
    public string Description = "";     // flavor text for UI panels
    public string PersonalityTrait = ""; // single-word trait for mentor hints
    public bool IsVillainFaction = false; // true only for "astrologer"

    // ── Faction identity ─────────────────────────────────────────────────
    /// <summary>Name of the archmage's faction in its normal (uncorrupted) state.</summary>
    public string FactionName = "";
    /// <summary>Name when the archmage has been fully corrupted by the Chronomancer.</summary>
    public string CorruptedFactionName = "";
    /// <summary>CSS hex color for map overlay on this faction's territory (e.g. "#7B3FA0").</summary>
    public string FactionColorHex = "#888888";

    // ── Overworld presence ────────────────────────────────────────────────
    /// <summary>How many patrol tokens this archmage maintains at full strength.</summary>
    public int BasePatrolCount = 2;
    /// <summary>
    /// 0–1. When a combat POI fires and an archmage is present in the region,
    /// this is the probability the encounter draws from the archmage's faction
    /// pool rather than the region's geographic pool.
    /// </summary>
    public float ArchmageFactionChance = 0.40f;

    // ── Corruption thresholds ─────────────────────────────────────────────
    /// <summary>Maximum CorruptionLevel (0–3) at which Unite is still achievable. Above this, only Coerce or Overthrow remain.</summary>
    public int MaxCorruptionForUnite = 1;
    /// <summary>Maximum CorruptionLevel at which Coerce is still achievable. Above this, only Overthrow remains (or already Corrupted).</summary>
    public int MaxCorruptionForCoerce = 2;

    // ── Boss combat ───────────────────────────────────────────────────────
    /// <summary>HP multiplier for the boss encounter when the player's school does NOT match this archmage's school.</summary>
    public float StandardBossHealthMult = 1.0f;
    /// <summary>HP multiplier when the player IS facing their own school's archmage (betrayal encounter — harder).</summary>
    public float BetrayalBossHealthMult = 1.5f;
    /// <summary>When true, the betrayal encounter has a second phase with changed AI behavior.</summary>
    public bool BetrayalHasSecondPhase = false;
    /// <summary>Encounter pool id override for the boss fight (drawn from region encounter JSON by name). Empty = use default boss pool.</summary>
    public string BossEncounterPoolId = "";

    // ── Final battle contribution ─────────────────────────────────────────
    /// <summary>Description of what this archmage contributes when Allied in the final battle.</summary>
    public string AllyAbilityDescription = "";
    /// <summary>Description of the shard invocation (one-use powerful effect) available when the archmage was Overthrown.</summary>
    public string ShardInvocationDescription = "";
    /// <summary>0–1 effectiveness multiplier when Allied. 1.0 = full strength.</summary>
    public float AlliedStrength = 1.0f;
    /// <summary>0–1 effectiveness multiplier when Coerced. Less than Allied because the alliance is forced.</summary>
    public float CoercedStrength = 0.6f;
    /// <summary>When true, the Chronomancer can attempt to flip this archmage mid-final-battle if they were only Coerced.</summary>
    public bool CoercedCanFlip = true;

    // ── Mentor dialogue hooks ─────────────────────────────────────────────
    /// <summary>Fragments the good Chronomancer uses when warning about this archmage's weaknesses or approach.</summary>
    public List<string> WeaknessHints = new();
    /// <summary>Personality notes informing how to roleplay the Unite vs Coerce approach.</summary>
    public List<string> PersonalityNotes = new();
    /// <summary>Mentor reaction lines displayed after the player successfully Unites this archmage.</summary>
    public List<string> PostUniteDialogue = new();
    /// <summary>Mentor reaction lines when this archmage becomes Corrupted.</summary>
    public List<string> PostCorruptedDialogue = new();

    // ── Faction encounter pools ───────────────────────────────────────────
    /// <summary>
    /// Encounters drawn when this archmage's faction is selected (per ArchmageFactionChance).
    /// Same structure as a region's encounterPools block so EncounterPoolLoader can
    /// process both with one code path.
    /// </summary>
    public ArchmageEncounterPools FactionEncounters = new();
}

/// <summary>Encounter pool for an archmage's faction, parallel to the region encounterPools schema.</summary>
public class ArchmageEncounterPools
{
    public List<ArchmageEncounterGroup> Skirmish = new();
    public List<ArchmageEncounterGroup> Battle = new();
    public List<ArchmageEncounterGroup> Siege = new();
    public List<ArchmageEncounterGroup> Ambush = new();

    /// <summary>Returns the pool list for the given tier string ("Skirmish", "Battle", "Siege", "Ambush").</summary>
    public List<ArchmageEncounterGroup> GetTier(string tier) => tier switch
    {
        "Skirmish" => Skirmish,
        "Battle" => Battle,
        "Siege" => Siege,
        "Ambush" => Ambush,
        _ => Battle
    };
}

/// <summary>One named encounter group in an archmage's faction pool.</summary>
public class ArchmageEncounterGroup
{
    public string Name = "";
    public float DifficultyMult = 1.0f;
    public List<ArchmageEnemySlot> Enemies = new();
}

/// <summary>One enemy in an archmage faction encounter.</summary>
public class ArchmageEnemySlot
{
    public string Archetype = "Soldier";
    public float DifficultyMult = 1.0f;
}
