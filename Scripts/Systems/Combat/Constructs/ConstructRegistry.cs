using System.Collections.Generic;
using System.Linq;

// ============================================================
// ConstructRegistry.cs
//
// Purpose:        Stateless query helpers for the Tinker
//                 construct set. Rather than maintain a parallel
//                 list (which would need a GameState field and a
//                 reset hook), this reads straight off
//                 GameState.UnitsInPlay filtered by the
//                 Unit.IsConstruct flag and SummonerTeamId. Used
//                 by cards/effects that ask "do I have a
//                 construct?", "how many?", or "all of them" and
//                 by the summon handler for cap enforcement.
// Layer:          System
// Collaborators:  GameState.cs (UnitsInPlay), Unit.cs
//                 (IsConstruct / SummonerTeamId), CombatManager.cs
//                 (cap check in the summon handler),
//                 TinkerAttunement.cs (per-unit ConstructCap)
// ============================================================

/// <summary>
/// Pure query layer over the player's constructs. Holds no state, so nothing to
/// reset between fights — a construct is anything in play with
/// <see cref="Unit.IsConstruct"/> set and a matching <see cref="Unit.SummonerTeamId"/>.
/// </summary>
public static class ConstructRegistry
{
    /// <summary>Fallback simultaneous-construct cap when no TinkerAttunement is present.</summary>
    public const int DefaultCap = TinkerAttunement.BaseConstructCap;

    /// <summary>Live constructs owned by the given team.</summary>
    public static IEnumerable<Unit> All(GameState s, int team)
    {
        if (s?.UnitsInPlay == null)
            return System.Array.Empty<Unit>();
        return s.UnitsInPlay.Where(u =>
            u != null && u.IsConstruct && u.SummonerTeamId == team && u.Stats.IsAlive);
    }

    /// <summary>Count of live constructs owned by the given team.</summary>
    public static int Count(GameState s, int team) => All(s, team).Count();

    /// <summary>True if the team controls at least one live construct.</summary>
    public static bool Has(GameState s, int team) => All(s, team).Any();
}
