using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// TinkerLinkEffects.cs
//
// Purpose:        JSON-driven effects for the Conduit Link
//                 layer — create a link (Split or Mirror, with
//                 optional line-cross damage), arc damage along
//                 a target's links, and collapse all of a team's
//                 links onto the lowest-HP enemy (Singularity).
// Layer:          Effects
// Collaborators:  ConduitLinkSystem.cs, Effect.cs (EffectBase),
//                 TinkerEffects.cs (TinkerFx helpers)
// ============================================================

// ── Create Conduit Link ────────────────────────────────────────────
/// <summary>
/// Links units together. With one targeted unit, links the caster to it. With two or
/// more, chains them consecutively (a web). Mode is Split (defensive damage-share) or
/// Mirror (offensive spread); lineDamage > 0 makes the hex line between linked units
/// zap enemies that step onto it.
/// </summary>
public sealed class CreateConduitLinkEffect : EffectBase
{
    private readonly LinkMode _mode;
    private readonly int _lineDamage;

    public CreateConduitLinkEffect(LinkMode mode, int lineDamage)
    {
        _mode = mode;
        _lineDamage = lineDamage;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        int team = TinkerFx.CasterTeam(s);

        var units = new List<Unit>();
        if (targets != null)
        {
            foreach (var o in targets.Items)
            {
                var u = TinkerFx.ResolveUnit(s, o);
                if (u != null && u.Stats.IsAlive && !units.Contains(u))
                    units.Add(u);
            }
        }

        var casterUnit = s?.ActiveCasterUnit;

        if (units.Count == 1 && casterUnit != null && casterUnit != units[0])
        {
            ConduitLinkSystem.CreatePair(casterUnit, units[0], _mode, team, _lineDamage);
            s?.Log($"[Link] {casterUnit.Name} \u21C4 {units[0].Name} ({_mode}).");
            return;
        }

        if (units.Count < 2)
        {
            s?.Log("[Link] Need at least two units to link.");
            return;
        }

        for (int i = 0; i < units.Count - 1; i++)
            ConduitLinkSystem.CreatePair(units[i], units[i + 1], _mode, team, _lineDamage);

        s?.Log($"[Link] Wove a {units.Count}-unit {_mode} web (line dmg {_lineDamage}).");
    }
}

// ── Arc Damage ──────────────────────────────────────────────────────
/// <summary>Damages the primary target, then arcs a smaller amount to each of its link partners.</summary>
public sealed class ArcDamageEffect : EffectBase
{
    private readonly int _amount, _arc;

    public ArcDamageEffect(int amount, int arc)
    {
        _amount = amount;
        _arc = arc;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        Unit primary = null;
        if (targets != null)
        {
            foreach (var o in targets.Items)
            {
                var u = TinkerFx.ResolveUnit(s, o);
                if (u != null && u.Stats.IsAlive) { primary = u; break; }
            }
        }

        if (primary == null)
        {
            s?.Log("[Arc] No target.");
            return;
        }

        // Skip the primary's own link redistribution so the arc is the only spread.
        primary.ApplyDamageSkippingLinks(_amount);

        if (_arc > 0)
        {
            foreach (var p in ConduitLinkSystem.PartnersOf(primary))
            {
                if (p.Stats.IsAlive)
                {
                    p.ApplyDamageSkippingLinks(_arc);
                    s?.Log($"[Arc] Arcs {_arc} to {p.Name}.");
                }
            }
        }
    }
}

// ── Conduit Singularity ────────────────────────────────────────────
/// <summary>Collapses all of the caster team's links, dealing per-link damage focused on the lowest-HP living enemy.</summary>
public sealed class ConduitSingularityEffect : EffectBase
{
    private readonly int _perLink;

    public ConduitSingularityEffect(int perLink) { _perLink = perLink; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.UnitsInPlay == null)
            return;

        int team = TinkerFx.CasterTeam(s);

        Unit focus = null;
        int lowest = int.MaxValue;
        foreach (var u in s.UnitsInPlay)
        {
            if (u == null || !u.Stats.IsAlive || u.TeamId == team)
                continue;
            if (u.Stats.Health < lowest)
            {
                lowest = u.Stats.Health;
                focus = u;
            }
        }

        if (focus == null)
        {
            s.Log("[Singularity] No enemy to converge on.");
            return;
        }

        int linkCount = ConduitLinkSystem.CountLinksForTeam(team);
        int dmg = _perLink * Math.Max(1, linkCount);

        focus.ApplyDamageSkippingLinks(dmg);
        s.Log($"[Singularity] {linkCount} link(s) converge on {focus.Name} — {dmg} damage.");

        ConduitLinkSystem.ClearTeam(team);
    }
}
