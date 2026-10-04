using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// IntentTimeEffects.cs
//
// Purpose:        The Chronomancer's core verbs on enemy attacks
//                 (class_identity_chronomancer_v1 §2a):
//                 • Defer: the attack is lifted out of the enemy's
//                   activation and lands at the end of the round, on
//                   the tile it was locked onto, hitting whoever
//                   stands there then. The enemy still moves.
//                 • Advance: the attack lands NOW on its locked tile;
//                   the enemy then takes its turn without it.
//                 Moving an attack in time cancels nothing, so these
//                 stack safely and need no stun cap (unlike Postpone).
//                 The effects only pick targets; the rules live in
//                 CombatManager.IntentTime.cs behind IntentTime's
//                 hooks, the same seam pattern as ElementReactions.
// Layer:          Effects
// Collaborators:  IntentTime (hooks), CombatManager.IntentTime.cs,
//                 CardScriptRegistry.Chronomancer.cs (JSON keys)
// See:            docs/class_identity_chronomancer_v1.md §2a
// ============================================================

/// <summary>When an enemy's current attack lands relative to its own activation.</summary>
public enum IntentTiming
{
    /// <summary>Lands in the enemy's own activation, as planned.</summary>
    Normal,
    /// <summary>Lands at the end of the round, on its locked tile (Defer).</summary>
    Deferred,
    /// <summary>Already landed (Advance); the enemy acts this round without it.</summary>
    Spent,
}

/// <summary>Seam between card effects and the combat runtime for Defer and Advance.
/// CombatManager installs the hooks at combat start. Each hook returns null on
/// success, or the reason the attack could not be moved (logged by the effect).</summary>
public static class IntentTime
{
    public static Func<Unit, string> DeferHook;
    public static Func<Unit, string> AdvanceHook;

    /// <summary>Reveals an enemy's current intent (value and marked tiles). With
    /// lookahead, also its NEXT intent (Chronomancer, §2d). Permanent keeps it revealed
    /// for the rest of the fight. True when there was an intent to reveal.
    /// Arguments: enemy, lookahead, permanent.</summary>
    public static Func<Unit, bool, bool, bool> RevealHook;

    public static bool Reveal(Unit enemy, bool lookahead = false, bool permanent = false)
        => RevealHook != null && RevealHook(enemy, lookahead, permanent);

    public static string Defer(Unit enemy) => DeferHook != null ? DeferHook(enemy) : "no combat is running";

    /// <summary>Cancel a Deferred attack: it never lands (The Hour Comes Due).</summary>
    public static Func<Unit, string> CancelHook;
    public static string Cancel(Unit enemy) => CancelHook != null ? CancelHook(enemy) : "no combat is running";
    public static string Advance(Unit enemy) => AdvanceHook != null ? AdvanceHook(enemy) : "no combat is running";

    /// <summary>The enemies an intent-time effect touches: each targeted enemy, plus every
    /// living enemy within <paramref name="radius"/> of one (0 = the target only;
    /// 99 or more = every enemy on the board). Player-side units are never touched.</summary>
    internal static List<Unit> Victims(GameState s, IEnumerable<Unit> primaries, int radius)
    {
        var result = new List<Unit>();
        var seen = new HashSet<Unit>();
        var centers = new List<Unit>();

        foreach (var p in primaries)
        {
            if (p == null || p.IsPlayerControlled || !p.Stats.IsAlive || !seen.Add(p))
                continue;
            result.Add(p);
            centers.Add(p);
        }

        if (radius <= 0 || s?.UnitsInPlay == null)
            return result;

        foreach (var u in s.UnitsInPlay)
        {
            if (u == null || u.IsPlayerControlled || !u.Stats.IsAlive || u.CurrentTile == null || seen.Contains(u))
                continue;
            bool inRange = radius >= 99;
            if (!inRange && s.Grid != null)
            {
                foreach (var c in centers)
                {
                    if (c.CurrentTile != null && s.Grid.Distance(c.CurrentTile.Axial, u.CurrentTile.Axial) <= radius)
                    {
                        inRange = true;
                        break;
                    }
                }
            }
            if (inRange && seen.Add(u))
                result.Add(u);
        }
        return result;
    }
}

/// <summary>
/// Defer target enemy's attack (class_identity_chronomancer_v1 §2a): it lands at the end
/// of the round on its locked tile. <see cref="Radius"/> widens it to every enemy within
/// that many tiles of the target (99 = every enemy).
/// Two other ways to pick who (slice 8, §4 rewording):
/// <see cref="AimedWithin"/> (0 or more) takes every enemy whose attack is locked onto a
/// tile within that many tiles of the caster (Misdirection: "every attack aimed at you");
/// <see cref="NearDecoy"/> takes every enemy within <see cref="Radius"/> of one of the
/// caster's echoes (Decoy of Hours) instead of widening around the target.
/// JSON: { "type": "defer_intent", "radius": n, "aimed_within": n, "near_decoy": bool }
/// </summary>
public sealed class DeferIntentEffect : EffectBase
{
    public int Radius;
    /// <summary>-1 = off.</summary>
    public int AimedWithin = -1;
    public bool NearDecoy;
    /// <summary>Foresight gained per attack actually Deferred (Grand Design).</summary>
    public int ForesightPer;
    public DeferIntentEffect(int radius = 0) { Radius = Math.Max(0, radius); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var primaries = new List<Unit>();
        if (targets?.Items != null)
            foreach (var obj in targets.Items)
                primaries.Add(ResolveTargetUnit(s, obj));

        var casterUnit = s?.ActiveCasterUnit;
        int radius = Radius;
        if (AimedWithin >= 0)
        {
            primaries.Clear();
            radius = 0;
            if (casterUnit?.CurrentTile != null && s.Grid != null && s.UnitsInPlay != null)
            {
                var home = casterUnit.CurrentTile.Axial;
                foreach (var u in s.UnitsInPlay)
                {
                    if (u == null || u.TeamId == casterUnit.TeamId || !u.Stats.IsAlive || u.CurrentIntent == null)
                        continue;
                    var tiles = new List<Vector2I>(u.CurrentIntent.ThreatTiles ?? new List<Vector2I>());
                    if (u.CurrentIntent.TargetTile.HasValue)
                        tiles.Add(u.CurrentIntent.TargetTile.Value);
                    if (tiles.Any(t => s.Grid.Distance(home, t) <= AimedWithin))
                        primaries.Add(u);
                }
            }
        }
        else if (NearDecoy)
        {
            primaries.Clear();
            radius = 0;
            if (s?.UnitsInPlay != null && s.Grid != null)
            {
                int team = casterUnit?.TeamId ?? 0;
                var decoys = s.UnitsInPlay.Where(d => d != null && d.IsDecoy && d.Stats.IsAlive
                                                      && d.TeamId == team && d.CurrentTile != null).ToList();
                if (decoys.Count == 0)
                    s.Log("[Defer] You have no echo on the board.");
                foreach (var u in s.UnitsInPlay)
                {
                    if (u == null || u.TeamId == team || u.IsDecoy || !u.Stats.IsAlive || u.CurrentTile == null)
                        continue;
                    if (decoys.Any(d => s.Grid.Distance(d.CurrentTile.Axial, u.CurrentTile.Axial) <= Math.Max(1, Radius)))
                        primaries.Add(u);
                }
            }
        }

        var victims = IntentTime.Victims(s, primaries, radius);
        if (victims.Count == 0)
        {
            s?.Log("[Defer] No enemy attack to defer.");
            return;
        }
        int deferred = 0;
        foreach (var enemy in victims)
        {
            string why = IntentTime.Defer(enemy);
            if (why != null)
                s?.Log($"[Defer] {enemy.Name}: {why}");
            else
                deferred++;
        }

        if (ForesightPer > 0 && deferred > 0 && casterUnit?.Attunement is FateAttunement fate)
        {
            fate.GainCharges(ForesightPer * deferred);
            s.Log($"[Defer] {deferred} attack(s) deferred: +{ForesightPer * deferred} Foresight.");
        }
    }
}

/// <summary>
/// Advance target enemy's attack (class_identity_chronomancer_v1 §2a): it lands now on
/// its locked tile, and the enemy takes its turn without it. <see cref="Radius"/> as
/// for <see cref="DeferIntentEffect"/>.
/// JSON: { "type": "advance_intent", "radius": n }
/// </summary>
public sealed class AdvanceIntentEffect : EffectBase
{
    public int Radius;
    /// <summary>Only attacks already Deferred (The Hour Comes Due), whoever was targeted.</summary>
    public bool DeferredOnly;
    /// <summary>Slowest attacker first (The Fixed Hour's bottom half); otherwise fastest first.</summary>
    public bool ReverseSpeed;
    public AdvanceIntentEffect(int radius = 0) { Radius = Math.Max(0, radius); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var primaries = new List<Unit>();
        if (targets?.Items != null)
            foreach (var obj in targets.Items)
                primaries.Add(ResolveTargetUnit(s, obj));

        List<Unit> victims;
        if (DeferredOnly)
            victims = s?.UnitsInPlay?.Where(u => u != null && !u.IsPlayerControlled && u.Stats.IsAlive
                                                 && u.AttackTiming == IntentTiming.Deferred).ToList()
                      ?? new List<Unit>();
        else
            victims = IntentTime.Victims(s, primaries, Radius);
        victims = ReverseSpeed
            ? victims.OrderBy(u => u.Stats.BaseSpeed).ToList()
            : victims.OrderByDescending(u => u.Stats.BaseSpeed).ToList();
        if (victims.Count == 0)
        {
            s?.Log("[Advance] No enemy attack to advance.");
            return;
        }
        foreach (var enemy in victims)
        {
            string why = IntentTime.Advance(enemy);
            if (why != null)
                s?.Log($"[Advance] {enemy.Name}: {why}");
        }
    }
}
