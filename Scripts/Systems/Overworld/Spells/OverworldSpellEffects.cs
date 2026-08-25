using Godot;
using System.Collections.Generic;

// ============================================================
// OverworldSpellEffects.cs  (S2, 2026-07-15)
//
// Purpose:        Run-scoped ACTIVE spell-effect state, meaning the
//                 timed windows ("Forest costs 1 for 5 steps",
//                 "corruption suppressed for 10 steps") that
//                 spells open and party steps tick down. A static
//                 class, matching the EquipmentLoadout /
//                 PlayerSession run-scoped pattern, for two
//                 reasons: (1) OverworldMovementCost.StepCost is
//                 the single source of truth both the CHARGE path
//                 and the PREVIEW path call, and a static hook there
//                 keeps them incapable of diverging; (2) statics
//                 survive the combat scene swap, so a 5-step
//                 window correctly persists across a mid-window
//                 fight. Cleared on fresh deploy and on
//                 extraction/failure.
//
//                 Nothing here grants steps (G1). Effects reduce
//                 costs or suppress drains within bounded windows.
// Layer:          System (run-scoped state)
// Collaborators:  OverworldSpellManager.cs (opens effects),
//                 OverworldMovementCost.cs (terrain-cost hook),
//                 ExpeditionManager.cs (drain hooks + per-step tick)
// See:            overworld_spell_system_v1_1.docx §3 (G1/G4), §7
// ============================================================

/// <summary>Active timed overworld-spell effects for the current expedition.
/// Static run-scoped state; see header for why.</summary>
public static class OverworldSpellEffects
{
    private class TimedEffect
    {
        public string Label;                                  // for logs
        public int StepsLeft;
        public List<OverworldHex.TerrainType> Terrains;       // affected terrains (null = all)
        public int CostCap;                                   // Traversal: terrain cost capped at this (-1 = n/a)
        public bool SuppressDrain;                            // Warding: negate terrain HP drain
        public bool SuppressCorruption;                       // Warding: negate corruption drain

        public bool Matches(OverworldHex.TerrainType t)
            => Terrains == null || Terrains.Contains(t);
    }

    private static readonly List<TimedEffect> _effects = new();

    /// <summary>Campward (§8): armed until the next Rest site consumes it.</summary>
    public static bool CampwardArmed = false;

    // ── S3: patrol-facing state ──────────────────────────────────────────

    /// <summary>Veil (Enchanter): steps of party imperceptibility remaining.
    /// Patrols keep their routes; detection and interception simply fail (G3).</summary>
    public static int VeilStepsLeft = 0;
    public static bool VeilActive() => VeilStepsLeft > 0;

    /// <summary>Attunement vision flags, set once per expedition by school
    /// (Chronomancer → Foreboding pursuit vectors; Enchanter → True Names
    /// identity labels). Read by PatrolToken each frame.</summary>
    public static bool ForebodingVision = false;
    public static bool TrueNamesVision = false;

    private class HexBlock { public Vector2I Coord; public int StepsLeft; }
    private static readonly List<HexBlock> _patrolBlocks = new();

    private class Trap { public Vector2I Coord; public int StunSteps; }
    private static readonly List<Trap> _traps = new();

    /// <summary>Thornwall (Druid): patrols cannot enter this hex for N party
    /// steps. Terrain denial, never removal (G3).</summary>
    public static void AddPatrolBlock(Vector2I coord, int steps)
        => _patrolBlocks.Add(new HexBlock { Coord = coord, StepsLeft = steps });

    /// <summary>True if patrol pathing must treat this hex as impassable.</summary>
    public static bool HexBlockedForPatrols(Vector2I coord)
    {
        foreach (var b in _patrolBlocks)
            if (b.StepsLeft > 0 && b.Coord == coord)
                return true;
        return false;
    }

    /// <summary>Fulminant Charge (Tinker): rig a hex; the FIRST patrol to
    /// enter is stunned. No expiry: a set charge waits (expedition-scoped).</summary>
    public static void AddTrap(Vector2I coord, int stunSteps)
        => _traps.Add(new Trap { Coord = coord, StunSteps = stunSteps });

    /// <summary>Spring the trap at a coord, if any. Returns stun steps (0 = no
    /// trap). Consumed on spring, so one patrol only.</summary>
    public static int ConsumeTrapAt(Vector2I coord)
    {
        for (int i = 0; i < _traps.Count; i++)
        {
            if (_traps[i].Coord == coord)
            {
                int stun = _traps[i].StunSteps;
                _traps.RemoveAt(i);
                return stun;
            }
        }
        return 0;
    }

    // ── Opening effects ──────────────────────────────────────────────────

    /// <summary>Traversal window: cap the terrain-cost component at
    /// <paramref name="costCap"/> for the listed terrains, for N steps.
    /// (Verdant Passage: Forest/Swamp → 1 for 5 steps.)</summary>
    public static void AddTerrainCostCap(string label,
        List<OverworldHex.TerrainType> terrains, int costCap, int steps)
        => _effects.Add(new TimedEffect
        { Label = label, Terrains = terrains, CostCap = costCap, StepsLeft = steps });

    /// <summary>Warding window: negate terrain HP drain on the listed terrains
    /// (null = all terrains) for N steps. (Ember Ward: Volcanic, 8 steps.)</summary>
    public static void AddDrainSuppression(string label,
        List<OverworldHex.TerrainType> terrains, int steps)
        => _effects.Add(new TimedEffect
        { Label = label, Terrains = terrains, SuppressDrain = true, CostCap = -1, StepsLeft = steps });

    /// <summary>Warding window: negate corruption attrition for N steps.
    /// (Purifying Rite: 10 steps.) Bounded relief, never immunity (G4).</summary>
    public static void AddCorruptionSuppression(string label, int steps)
        => _effects.Add(new TimedEffect
        { Label = label, SuppressCorruption = true, CostCap = -1, StepsLeft = steps });

    // ── Query hooks ──────────────────────────────────────────────────────

    /// <summary>Terrain-cost hook, called from INSIDE OverworldMovementCost.
    /// StepCost so charge and preview cannot diverge. Applies the best
    /// (lowest) active cap for the destination terrain; floor-1 is enforced
    /// by StepCost itself.</summary>
    public static int AdjustTerrainStep(OverworldHex.TerrainType t, int cost)
    {
        foreach (var e in _effects)
            if (e.CostCap >= 0 && e.StepsLeft > 0 && e.Matches(t) && e.CostCap < cost)
                cost = e.CostCap;
        return cost;
    }

    /// <summary>True if an active ward negates this terrain's HP drain.</summary>
    public static bool DrainSuppressed(OverworldHex.TerrainType t)
    {
        foreach (var e in _effects)
            if (e.SuppressDrain && e.StepsLeft > 0 && e.Matches(t))
                return true;
        return false;
    }

    /// <summary>True if corruption attrition is currently suppressed.</summary>
    public static bool CorruptionSuppressed()
    {
        foreach (var e in _effects)
            if (e.SuppressCorruption && e.StepsLeft > 0)
                return true;
        return false;
    }

    /// <summary>Consume the Campward charge, if armed. Called at a Rest site.</summary>
    public static bool ConsumeCampward()
    {
        if (!CampwardArmed)
            return false;
        CampwardArmed = false;
        return true;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    /// <summary>Tick all windows down by one party step; announce expiries.
    /// Call once per committed step, AFTER that step's costs/drains resolved
    /// (a 5-step window covers exactly 5 steps).</summary>
    public static void TickStep()
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            _effects[i].StepsLeft--;
            if (_effects[i].StepsLeft <= 0)
            {
                GD.Print($"[Spellcraft] {_effects[i].Label} fades.");
                _effects.RemoveAt(i);
            }
        }

        // S3: veil + patrol blocks tick on the same cadence. Traps do not,
        // because a set charge waits until sprung.
        if (VeilStepsLeft > 0 && --VeilStepsLeft == 0)
            GD.Print("[Spellcraft] The Veil lifts.");
        for (int i = _patrolBlocks.Count - 1; i >= 0; i--)
            if (--_patrolBlocks[i].StepsLeft <= 0)
            {
                GD.Print("[Spellcraft] The thornwall withers.");
                _patrolBlocks.RemoveAt(i);
            }
    }

    /// <summary>Wipe all run state. Call on fresh deploy and on
    /// extraction/failure. Statics outlive scenes by design (see header),
    /// so the boundaries must clear them explicitly.</summary>
    public static void Clear()
    {
        _effects.Clear();
        CampwardArmed = false;
        VeilStepsLeft = 0;
        ForebodingVision = false;
        TrueNamesVision = false;
        _patrolBlocks.Clear();
        _traps.Clear();
    }

    /// <summary>Short status line for HUD/logs; "" when nothing is active.</summary>
    public static string StatusSummary()
    {
        var parts = new List<string>();
        foreach (var e in _effects)
            parts.Add($"{e.Label} ({e.StepsLeft})");
        if (VeilStepsLeft > 0)
            parts.Add($"Veil ({VeilStepsLeft})");
        foreach (var b in _patrolBlocks)
            parts.Add($"Thornwall ({b.StepsLeft})");
        if (_traps.Count > 0)
            parts.Add($"Charge set ×{_traps.Count}");
        if (CampwardArmed)
            parts.Add("Campward (armed)");
        return parts.Count == 0 ? "" : string.Join("  ·  ", parts);
    }
}
