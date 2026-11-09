using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ArcanistStructures.cs
//
// Purpose:        The Architect (class_identity_arcanist_v1 §3D, slice 14d):
//                 structures that bend spells. They never attack and never
//                 move on their own (Structure rule, §1). Each is a stationary
//                 IsStructure unit with HP that enemies can break, plus an
//                 entry here saying what it does:
//                 • focus_stone: spells you cast while adjacent gain range and
//                   damage
//                 • mirror_plane: a single-target spell aimed at it bounces to
//                   the nearest enemy within 3, with bonus damage
//                 • grand_apparatus: Charge each turn per structure you own
//                 • nexus: Charge each turn; spells cast within its radius cost
//                   less; optional pulse on nearby enemies
//                 • prism: a single-target spell at an enemy near it splits to
//                   other enemies near it
//                 • singularity: spells cast near it give Charge; optional pulse
//                 And the casting circle (Spell Prism's bottom): a tile mark,
//                 not a structure. The next spell cast from it gains damage per
//                 Charge.
//                 Before this slice every one of these summoned the same
//                 attacking ArcaneConstruct and ignored its "kind".
// Layer:          Effects + rules (static registry, reset per combat)
// Collaborators:  CombatManager (summon seam, range, turn start),
//                 ManaCost.EffectiveAmount (Nexus discount), StructureSpellHook
// See:            docs/class_identity_arcanist_v1.md §1, §3D
// ============================================================

public sealed class ArcaneStructure
{
    public Unit Body;
    public Unit Owner;
    public string Kind = "";
    public string Title = "";
    public int Radius = 2, ChargePerTurn, CostReduction, EnemyDamagePerTurn;
    public int SplitRange = 2, SplitTargets = 1;
    public bool SplitAll;
    public int ChargePerHit, BonusDamage, BonusRange;

    public bool Alive => Body != null && GodotObject.IsInstanceValid(Body) && Body.Stats.IsAlive && Body.CurrentTile != null;
    public int Team => Owner != null && GodotObject.IsInstanceValid(Owner) ? Owner.TeamId : 0;
}

public sealed class CastingCircle
{
    public Vector2I Tile;
    public Unit Owner;
    public int BonusPerCharge = 2;
    public float ConsumeFraction = 1f, RefundFraction;
}

public static class ArcaneStructures
{
    public static readonly List<ArcaneStructure> All = new();
    public static readonly List<CastingCircle> Circles = new();

    public static void Reset()
    {
        All.Clear();
        Circles.Clear();
    }

    public static IEnumerable<ArcaneStructure> Living(int team) => All.Where(a => a.Alive && a.Team == team);

    public static ArcaneStructure Of(Unit u) => u == null ? null : All.FirstOrDefault(a => a.Body == u && a.Alive);

    public static int CountFor(Unit owner) => owner == null ? 0 : Living(owner.TeamId).Count();

    private static int Dist(GameState s, Unit a, Unit b) =>
        s?.Grid == null || a?.CurrentTile == null || b?.CurrentTile == null ? int.MaxValue
            : s.Grid.Distance(a.CurrentTile.Axial, b.CurrentTile.Axial);

    /// <summary>True when the unit stands beside one of its side's structures.</summary>
    public static bool AdjacentToOwn(GameState s, Unit u) =>
        u != null && Living(u.TeamId).Any(a => Dist(s, u, a.Body) == 1);

    /// <summary>Focus Stone: extra range for a spell cast by this unit.</summary>
    public static int FocusRange(GameState s, Unit caster) =>
        caster == null ? 0 : Living(caster.TeamId).Where(a => a.Kind == "focus_stone" && Dist(s, caster, a.Body) == 1)
                                                  .Select(a => a.BonusRange).DefaultIfEmpty(0).Max();

    /// <summary>Nexus: discount for spells cast within its radius.</summary>
    public static int CostReduction(GameState s, Unit caster) =>
        caster == null ? 0 : Living(caster.TeamId).Where(a => a.Kind == "nexus" && Dist(s, caster, a.Body) <= a.Radius)
                                                  .Select(a => a.CostReduction).DefaultIfEmpty(0).Max();

    public static bool IsMirror(Unit u) => Of(u)?.Kind == "mirror_plane";

    public static CastingCircle CircleAt(Vector2I tile) => Circles.FirstOrDefault(c => c.Tile == tile);

    /// <summary>Start of the owner's turn: Nexus and Apparatus pay Charge; Nexus and
    /// Singularity pulse. Returns log lines.</summary>
    public static List<string> TurnStart(GameState s, int team)
    {
        var log = new List<string>();
        foreach (var a in Living(team).ToList())
        {
            var arc = a.Owner?.Attunement as ArcaneAttunement;
            int charge = a.ChargePerTurn;
            if (a.Kind == "grand_apparatus")
                charge = Math.Min(3, CountFor(a.Owner)) + a.ChargePerTurn;
            if (charge > 0 && arc != null)
            {
                int got = arc.Add(charge);
                log.Add($"{a.Title} hums: +{got} Charge.");
            }
            if (a.EnemyDamagePerTurn > 0 && s?.UnitsInPlay != null)
            {
                int hit = 0;
                foreach (var e in s.UnitsInPlay.ToList())
                {
                    if (e == null || !GodotObject.IsInstanceValid(e) || !e.Stats.IsAlive || e.TeamId == team
                        || Dist(s, e, a.Body) > a.Radius)
                        continue;
                    e.ApplyDamage(a.EnemyDamagePerTurn, a.Owner, Delivery.Arc);
                    hit++;
                }
                if (hit > 0)
                    log.Add($"{a.Title} pulses: {a.EnemyDamagePerTurn} damage to {hit} enem{(hit == 1 ? "y" : "ies")}.");
            }
        }
        return log;
    }

    /// <summary>Makes sure the per-fight spell hook is installed.</summary>
    public static void EnsureHook(GameState s, Entity owner)
    {
        if (s == null)
            return;
        s.ActiveEffects ??= new List<PersistentEffect>();
        if (!s.ActiveEffects.Any(e => e is StructureSpellHook && !e.IsExpired))
            s.ActiveEffects.Add(new StructureSpellHook { Owner = owner, TurnsRemaining = 99999 });
    }
}

/// <summary>Reads the structures (and casting circles) on every spell your side casts:
/// Focus Stone damage, Mirror Plane bounce, Prism split, Singularity Charge, and the
/// casting circle's damage per Charge. Unwinds its damage bonus when the spell resolves.</summary>
public sealed class StructureSpellHook : PersistentEffect
{
    private Unit _armedCaster;
    private int _bonus;
    private CastingCircle _circleUsed;

    public override void Tick(GameState s) { }   // lasts the fight

    public override void OnSpellCast(GameState s, Unit caster, TargetSet targets)
    {
        if (caster == null || caster.CurrentTile == null || s?.Grid == null)
            return;
        var grid = s.Grid;
        int team = caster.TeamId;
        var mine = ArcaneStructures.Living(team).ToList();
        int bonus = 0;

        // Focus Stone: adjacent casts hit harder.
        foreach (var a in mine.Where(a => a.Kind == "focus_stone"))
            if (grid.Distance(caster.CurrentTile.Axial, a.Body.CurrentTile.Axial) == 1)
                bonus = Math.Max(bonus, a.BonusDamage);

        // The single unit this spell targets, if any.
        Unit single = null;
        if (targets?.Items != null)
        {
            var units = targets.Items.Select(o => TargetingHelpers.ResolveUnit(s, o))
                .Where(u => u != null && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive).Distinct().ToList();
            if (units.Count == 1)
                single = units[0];
        }

        // Mirror Plane: a spell aimed at your mirror bounces to the nearest enemy within 3.
        var mirror = single != null ? ArcaneStructures.Of(single) : null;
        if (mirror != null && mirror.Kind == "mirror_plane" && mirror.Team == team)
        {
            var to = NearestEnemy(s, mirror.Body, team, 3, null);
            targets.Items.RemoveAll(o => TargetingHelpers.ResolveUnit(s, o) == mirror.Body
                                         || (o is TileData td && td.Axial == mirror.Body.CurrentTile.Axial));
            if (to != null)
            {
                targets.Items.Add(to);
                bonus += mirror.BonusDamage;
                s.Log($"[Mirror Plane] The spell bounces off the mirror onto {to.Name} (+{mirror.BonusDamage}).");
                single = to;
            }
            else
                s.Log("[Mirror Plane] The spell bounces off the mirror and finds no one.");
        }

        // Prism: a single-target spell at an enemy near your Prism splits to others near it.
        if (single != null && single.TeamId != team)
        {
            foreach (var p in mine.Where(a => a.Kind == "prism"))
            {
                if (grid.Distance(single.CurrentTile.Axial, p.Body.CurrentTile.Axial) > p.SplitRange)
                    continue;
                var extra = s.UnitsInPlay
                    .Where(u => u != null && u != single && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive
                                && u.TeamId != team && u.CurrentTile != null
                                && grid.Distance(u.CurrentTile.Axial, p.Body.CurrentTile.Axial) <= p.SplitRange
                                && !targets.Items.Contains(u))
                    .OrderBy(u => grid.Distance(u.CurrentTile.Axial, p.Body.CurrentTile.Axial))
                    .Take(p.SplitAll ? 99 : Math.Max(1, p.SplitTargets)).ToList();
                foreach (var u in extra)
                    targets.Items.Add(u);
                if (extra.Count > 0)
                    s.Log($"[Prism] The spell splits to {extra.Count} more enem{(extra.Count == 1 ? "y" : "ies")}.");
                break;
            }
        }

        // Singularity: spells cast near it feed its owner Charge.
        foreach (var g in mine.Where(a => a.Kind == "singularity"))
            if (grid.Distance(caster.CurrentTile.Axial, g.Body.CurrentTile.Axial) <= g.Radius
                && g.Owner?.Attunement is ArcaneAttunement garc && g.ChargePerHit > 0)
            {
                garc.Add(g.ChargePerHit);
                s.Log($"[Singularity] +{g.ChargePerHit} Charge.");
            }

        // Casting circle: the next spell cast from it gains damage per Charge.
        var circle = ArcaneStructures.CircleAt(caster.CurrentTile.Axial);
        if (circle != null && circle.Owner?.TeamId == team && caster.Attunement is ArcaneAttunement carc && carc.Charge > 0)
        {
            int add = circle.BonusPerCharge * carc.Charge;
            bonus += add;
            _circleUsed = circle;
            s.Log($"[Casting Circle] +{add} damage ({carc.Charge} Charge).");
        }

        if (bonus > 0 || _circleUsed != null)
        {
            caster.BonusSpellDamage += bonus;
            _bonus = bonus;
            _armedCaster = caster;
        }
    }

    public override void OnSpellResolved(GameState s, Unit caster, TargetSet targets)
    {
        if (_armedCaster == null || caster != _armedCaster)
            return;
        caster.BonusSpellDamage -= _bonus;
        _bonus = 0;
        _armedCaster = null;
        if (_circleUsed != null)
        {
            var c = _circleUsed;
            _circleUsed = null;
            ArcaneStructures.Circles.Remove(c);
            s.OnCastingCircleChanged?.Invoke(c.Tile, false);
            if (caster.Attunement is ArcaneAttunement arc)
            {
                int spend = (int)Math.Ceiling(arc.Charge * Math.Clamp(c.ConsumeFraction, 0f, 1f));
                arc.Spend(spend);
                int refund = (int)Math.Floor(spend * Math.Clamp(c.RefundFraction, 0f, 1f));
                if (refund > 0)
                    arc.Add(refund);
                s.Log($"[Casting Circle] The circle is spent: -{spend} Charge" + (refund > 0 ? $", +{refund} back." : "."));
            }
        }
    }

    private static Unit NearestEnemy(GameState s, Unit from, int team, int range, Unit except) =>
        s.UnitsInPlay.Where(u => u != null && u != except && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive
                                 && u.TeamId != team && u.CurrentTile != null
                                 && s.Grid.Distance(u.CurrentTile.Axial, from.CurrentTile.Axial) <= range)
                     .OrderBy(u => s.Grid.Distance(u.CurrentTile.Axial, from.CurrentTile.Axial))
                     .FirstOrDefault();
}

/// <summary>
/// Raise an Arcanist structure on the target tile (or beside you).
/// JSON: { "type": "raise_structure", "kind": "focus_stone" | "mirror_plane" | "grand_apparatus"
///         | "nexus" | "prism" | "singularity", "hp": n, "radius": n, "charge_per_turn": n,
///         "cost_reduction": n, "enemy_damage_per_turn": n, "split_range": n, "split_targets": n,
///         "split_all": bool, "charge_per_hit": n, "pulse_damage": n, "bonus_damage": n, "bonus_range": n }
/// </summary>
public sealed class RaiseStructureEffect : EffectBase
{
    public string Kind = "focus_stone";
    public int HP = 8, Radius = 2, ChargePerTurn, CostReduction, EnemyDamagePerTurn;
    public int SplitRange = 2, SplitTargets = 1, ChargePerHit, BonusDamage, BonusRange;
    public bool SplitAll;

    private static string TitleOf(string kind) => kind switch
    {
        "focus_stone" => "Focus Stone",
        "mirror_plane" => "Mirror Plane",
        "grand_apparatus" => "Grand Apparatus",
        "nexus" => "Arcane Nexus",
        "prism" => "Prism",
        "singularity" => "Singularity Crystal",
        _ => "Structure",
    };

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (CombatSim.Active)
            return;
        if (s.OnSummonRequested == null)
        { s.Log("[Structure] No summon handler."); return; }
        var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
        TileData spot = null;
        if (targets?.Items != null)
            foreach (var o in targets.Items)
            {
                var t = InterfaceHelpers.ResolveTile(s, o);
                if (t != null && !t.IsBlocked && !t.IsOccupied) { spot = t; break; }
            }
        if (spot == null && owner?.CurrentTile != null && s.Grid != null)
            foreach (var c in s.Grid.GetNeighbors(owner.CurrentTile.Axial))
            {
                var t = s.Grid.GetTile(c);
                if (t != null && !t.IsBlocked && !t.IsOccupied) { spot = t; break; }
            }
        if (spot == null)
        { s.Log("[Structure] No open tile."); return; }

        var body = s.OnSummonRequested(Kind, spot, owner?.TeamId ?? 0);
        if (body == null)
        { s.Log($"[Structure] Could not raise {Kind}."); return; }
        body.Stats.MaxHealth = HP;
        body.Stats.Health = HP;
        body.Stats.BaseSpeed = 0;
        body.Stats.MovePoints = 0;
        body.AttackDamage = 0;
        body.IsStructure = true;      // no deck, no selection, not a survivor
        body.DisplayName = TitleOf(Kind);
        body.RefreshNameLabel();
        body.RefreshHealthBar();
        if (!s.UnitsInPlay.Contains(body))
            s.UnitsInPlay.Add(body);

        ArcaneStructures.All.Add(new ArcaneStructure
        {
            Body = body, Owner = owner, Kind = Kind, Title = TitleOf(Kind),
            Radius = Radius, ChargePerTurn = ChargePerTurn, CostReduction = CostReduction,
            EnemyDamagePerTurn = EnemyDamagePerTurn, SplitRange = SplitRange, SplitTargets = SplitTargets,
            SplitAll = SplitAll, ChargePerHit = ChargePerHit, BonusDamage = BonusDamage, BonusRange = BonusRange,
        });
        ArcaneStructures.EnsureHook(s, caster);
        s.Log($"[Structure] {TitleOf(Kind)} rises at {spot.Axial} (HP {HP}).");
    }
}

/// <summary>Spell Prism's bottom: draw a casting circle on a tile. The next spell you cast
/// while standing on it gains damage per Charge, then the circle is spent along with
/// (a fraction of) your Charge.
/// JSON: { "type": "casting_circle", "bonus_per_charge": n, "consume_fraction": f, "refund_fraction": f }</summary>
public sealed class CastingCircleEffect : EffectBase
{
    public int BonusPerCharge = 2;
    public float ConsumeFraction = 1f, RefundFraction;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (CombatSim.Active)
            return;
        var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
        TileData spot = null;
        if (targets?.Items != null)
            foreach (var o in targets.Items)
                if ((spot = InterfaceHelpers.ResolveTile(s, o)) != null)
                    break;
        spot ??= owner?.CurrentTile;
        if (spot == null)
            return;
        ArcaneStructures.Circles.RemoveAll(c => c.Tile == spot.Axial);
        ArcaneStructures.Circles.Add(new CastingCircle
        {
            Tile = spot.Axial, Owner = owner, BonusPerCharge = BonusPerCharge,
            ConsumeFraction = ConsumeFraction, RefundFraction = RefundFraction,
        });
        ArcaneStructures.EnsureHook(s, caster);
        s.OnCastingCircleChanged?.Invoke(spot.Axial, true);
        s.Log($"[Casting Circle] Drawn at {spot.Axial}: the next spell cast from it gains +{BonusPerCharge} per Charge.");
    }
}

/// <summary>Grand Apparatus's bottom: each of your structures moves up to N tiles toward you.
/// JSON: { "type": "move_structures", "tiles": n }</summary>
public sealed class MoveStructuresEffect : EffectBase
{
    public int Tiles = 2;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = FindCasterUnit(s, caster) ?? s?.ActiveCasterUnit;
        if (owner?.CurrentTile == null || s?.Grid == null || CombatSim.Active)
            return;
        int moved = 0;
        foreach (var a in ArcaneStructures.Living(owner.TeamId).ToList())
        {
            for (int i = 0; i < Tiles; i++)
            {
                var here = a.Body.CurrentTile.Axial;
                int d = s.Grid.Distance(here, owner.CurrentTile.Axial);
                if (d <= 1)
                    break;
                TileData best = null;
                foreach (var nb in s.Grid.GetNeighbors(here))
                {
                    var t = s.Grid.GetTile(nb);
                    if (t == null || t.IsBlocked || t.IsOccupied)
                        continue;
                    if (s.Grid.Distance(nb, owner.CurrentTile.Axial) < d)
                    { best = t; break; }
                }
                if (best == null)
                    break;
                a.Body.PlaceOnTile(best, MovementKind.Teleport);
            }
            moved++;
        }
        s.Log($"[Apparatus] {moved} structure(s) slide toward {owner.Name}.");
    }
}

/// <summary>True when the caster stands beside one of its side's structures; with
/// "or_target", also when the first target does.
/// JSON: { "type": "near_structure", "or_target": bool }</summary>
public sealed class NearStructurePredicate : IPredicate
{
    public bool OrTarget;

    public bool Evaluate(PredicateContext ctx)
    {
        var s = ctx?.Game;
        var caster = s?.ActiveCasterUnit;
        if (caster == null)
            return false;
        if (ArcaneStructures.AdjacentToOwn(s, caster))
            return true;
        if (!OrTarget || ctx.Targets?.Items == null)
            return false;
        foreach (var o in ctx.Targets.Items)
        {
            var u = TargetingHelpers.ResolveUnit(s, o);
            if (u?.CurrentTile == null)
                continue;
            return ArcaneStructures.Living(caster.TeamId)
                .Any(a => s.Grid.Distance(u.CurrentTile.Axial, a.Body.CurrentTile.Axial) == 1);
        }
        return false;
    }
}
