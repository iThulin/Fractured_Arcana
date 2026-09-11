using Godot;
using System.Collections.Generic;

// ============================================================
// VisualEvent.cs
//
// Purpose:        One presentation-side fact produced by the rules engine
//                 during a synchronous resolution: "a cast happened with this
//                 delivery toward these points", "this unit lost this much",
//                 "this unit died". The rules never wait on visuals; they
//                 emit these and move on. CombatPresenter replays them in
//                 order, on its own clock, and applies the DISPLAYED
//                 consequences (health bar refresh, damage number, hide on
//                 death) as each one lands.
// Layer:          Systems / Combat / Presentation
// Collaborators:  CombatPresenter (consumer), Resolver.ResolveTop and
//                 Unit.ApplyDamage / Unit.Die (producers), Delivery (the
//                 archetype key), SchoolColors (the palette key)
// See:            docs/spell_vfx_pipeline_v1.md
// ============================================================

public enum VisualEventKind
{
    /// <summary>A stack item began resolving. Carries delivery, school, caster and
    /// the world positions of everything it targeted. Plays as cast flash +
    /// projectile(s) or burst, and gates the Damage events that follow it.</summary>
    Cast,

    /// <summary>A unit's pools changed from a hit. Plays as a damage number and a
    /// health-bar refresh. Emitted from Unit.ApplyDamage AFTER mitigation, so the
    /// number shown is the number that actually landed.</summary>
    Damage,

    /// <summary>A unit died. Plays as the hide that Unit.Die deferred.</summary>
    Death,

    /// <summary>A unit's rules position changed (Unit.PlaceOnTile). Plays as a short
    /// tween of the body to the new tile — one event per tile stepped, so a walk hops
    /// tile by tile and a push lands AFTER the spell that caused it.</summary>
    Move
}

public sealed class VisualEvent
{
    public VisualEventKind Kind;

    // ── Cast ────────────────────────────────────────────────────────────────
    public Unit Caster;
    public Delivery Delivery = Delivery.Untyped;
    public CardSchool School = CardSchool.Adept;
    public bool SchoolKnown;
    /// <summary>World positions captured at emit time. Units may move or vanish
    /// before playback, so the presenter aims at these, never at live nodes.</summary>
    public List<Vector3> TargetPoints = new();
    /// <summary>True when at least one target was a unit (vs. a bare tile).</summary>
    public bool HasUnitTargets;
    /// <summary>From the card half's "vfx" block (phase 2b). Archetype forces the
    /// visual regardless of Delivery ("bolt"|"arc"|"burst"|"impact"|"none"); Style
    /// names a palette and/or an authored scene. Null = derive.</summary>
    public string VfxArchetype;
    public string VfxStyle;
    /// <summary>First palette-bearing element tag on the half ("fire", "ice", …), so
    /// an Elementalist fire card burns orange and an ice card glows pale blue with no
    /// vfx block at all. Null when the half carries none.</summary>
    public string ElementTag;

    // ── Damage / Death ──────────────────────────────────────────────────────
    public Unit Victim;
    public int HpLoss;
    public int ShieldLoss;
    public int ArmorLoss;
    /// <summary>Victim's world position at emit time, so the number still pops
    /// where the body was if GameRunner frees a killed unit before playback.</summary>
    public Vector3 VictimPoint;
    public bool VictimPointKnown;

    // ── Move ────────────────────────────────────────────────────────────────
    public Unit Mover;
    public Vector3 MoveTo;
    public MovementKind MoveKind = MovementKind.Teleport;

    /// <summary>Where the RULES say a unit is: its current tile's view position. The
    /// body may lag behind that while Move events are queued, so snapshots must never
    /// read GlobalPosition directly.</summary>
    public static Vector3 RulesPositionOf(Unit u)
    {
        if (u.CurrentTile?.TileView != null && GodotObject.IsInstanceValid(u.CurrentTile.TileView))
            return u.CurrentTile.TileView.GlobalPosition;
        return u.GlobalPosition;
    }

    public static VisualEvent ForMove(Unit mover, Vector3 to, MovementKind kind)
    {
        return new VisualEvent { Kind = VisualEventKind.Move, Mover = mover, MoveTo = to, MoveKind = kind };
    }

    public static VisualEvent ForDamage(Unit victim, int hpLoss, int shieldLoss, int armorLoss)
    {
        var ev = new VisualEvent
        {
            Kind = VisualEventKind.Damage,
            Victim = victim,
            HpLoss = hpLoss,
            ShieldLoss = shieldLoss,
            ArmorLoss = armorLoss
        };
        if (victim != null && GodotObject.IsInstanceValid(victim) && victim.IsInsideTree())
        {
            ev.VictimPoint = RulesPositionOf(victim);
            ev.VictimPointKnown = true;
        }
        return ev;
    }

    public static VisualEvent ForDeath(Unit victim)
    {
        return new VisualEvent { Kind = VisualEventKind.Death, Victim = victim };
    }

    /// <summary>Builds a Cast event from a resolving stack item. Pure read: it walks
    /// the target set and snapshots positions. Safe to call with a null card (enemy
    /// abilities) — the school is then unknown and the presenter uses the neutral
    /// palette.</summary>
    public static VisualEvent ForCast(StackItem item)
    {
        var ev = new VisualEvent
        {
            Kind = VisualEventKind.Cast,
            Caster = item.CasterUnit,
            Delivery = item.Targets?.Delivery ?? Delivery.Untyped
        };

        string schoolName = Rules.SchoolOfCard(item.SourceCard);
        if (!string.IsNullOrEmpty(schoolName)
            && System.Enum.TryParse<CardSchool>(schoolName, out var parsed))
        {
            ev.School = parsed;
            ev.SchoolKnown = true;
        }

        if (item.Ability is CardHalf half)
        {
            ev.VfxArchetype = half.VfxArchetype;
            ev.VfxStyle = half.VfxStyle;
            if (half.Tags != null)
                foreach (var tag in half.Tags)
                    if (VfxLibrary.IsElementTag(tag))
                    { ev.ElementTag = tag; break; }
        }

        if (item.Targets?.Items != null)
        {
            foreach (var obj in item.Targets.Items)
            {
                if (obj is Unit u && GodotObject.IsInstanceValid(u) && u.IsInsideTree())
                {
                    ev.TargetPoints.Add(RulesPositionOf(u));
                    ev.HasUnitTargets = true;
                }
                else if (obj is TileData td)
                {
                    if (td.Occupant != null && GodotObject.IsInstanceValid(td.Occupant)
                        && td.Occupant.IsInsideTree())
                    {
                        ev.TargetPoints.Add(RulesPositionOf(td.Occupant));
                        ev.HasUnitTargets = true;
                    }
                    else if (td.TileView != null && GodotObject.IsInstanceValid(td.TileView))
                        ev.TargetPoints.Add(td.TileView.GlobalPosition);
                }
                else if (obj is HexTile tile && GodotObject.IsInstanceValid(tile))
                {
                    ev.TargetPoints.Add(tile.GlobalPosition);
                }
            }
        }

        return ev;
    }
}
