using System.Collections.Generic;

// ============================================================
// CombatSim.cs
//
// Purpose:        R22 no-mutation simulation mode for the drag
//                 damage preview. While Active, the mutation
//                 chokepoints divert here instead of touching
//                 live state:
//                   - Unit.ApplyDamage → RecordDamage (per-hit
//                     ledger, in order)
//                   - Unit.ApplyStatus → no-op
//                   - Unit.RemoveStatus → RecordStatusRemoved
//                     (so HasStatus can report the consumption
//                     WITHIN the sim — arcane_mark must pay out
//                     once, not once per damage step)
//                   - ImbueTileEffect / AttunementResolver tile
//                     writes → skipped
//                   - GameState.Log → suppressed
//                 The preview then runs REAL effect Resolve code
//                 through these gates — every number comes from
//                 the resolver itself, never a parallel formula.
// Layer:          Combat core (static; single-threaded like the
//                 rest of the combat loop)
// Collaborators:  CombatManager.UpdateDamagePreview (Begin/End),
//                 Unit.cs, GameStateManager.cs, Effect.cs,
//                 AttunementResolver.cs (gate sites)
// See:            combat_ui v2.1 §15 R22
// ============================================================

public static class CombatSim
{
    public static bool Active { get; private set; }

    // Per-hit ledger IN ORDER — mitigation (shrouded per-hit cap, shield,
    // armor) must replay hits sequentially, not against the sum.
    private static readonly List<(Unit victim, int amount)> _hits = new();

    // Statuses "consumed" during the sim (arcane_mark) — HasStatus consults
    // this so the real state is untouched but the sim sees the consumption.
    private static readonly HashSet<(Unit, string)> _removedStatuses = new();

    private static GameState _state;
    private static int _savedLastDamageDealt;

    /// <summary>Enter simulation mode. Always pair with End() in a finally —
    /// a stuck Active flag would swallow real damage.</summary>
    public static void Begin(GameState s)
    {
        Active = true;
        _hits.Clear();
        _removedStatuses.Clear();
        _state = s;
        _savedLastDamageDealt = s?.LastDamageDealt ?? 0;
    }

    public static void End()
    {
        if (_state != null)
            _state.LastDamageDealt = _savedLastDamageDealt;
        Active = false;
        _hits.Clear();
        _removedStatuses.Clear();
        _state = null;
    }

    public static void RecordDamage(Unit victim, int amount)
    {
        if (victim == null || amount <= 0)
            return;
        _hits.Add((victim, amount));
    }

    public static void RecordStatusRemoved(Unit unit, string status)
    {
        if (unit != null && !string.IsNullOrEmpty(status))
            _removedStatuses.Add((unit, status.ToLowerInvariant()));
    }

    public static bool WasStatusRemoved(Unit unit, string status)
        => unit != null && status != null
           && _removedStatuses.Contains((unit, status.ToLowerInvariant()));

    /// <summary>Recorded hits against one victim, in resolution order.</summary>
    public static IEnumerable<int> HitsTo(Unit victim)
    {
        foreach (var (v, amount) in _hits)
            if (v == victim)
                yield return amount;
    }

    /// <summary>Copy of the full per-hit ledger (victim, amount) in order — take
    /// this BEFORE End(), which clears it. Lets the preview flash every unit a
    /// spell would hit (chain bounces, AoE, retargets), not just the primary.</summary>
    public static List<(Unit victim, int amount)> SnapshotHits()
        => new List<(Unit, int)>(_hits);
}
