using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// TinkerPipelineEffects.cs
//
// Purpose:        The three Tinker cards that touch the cast /
//                 deploy / damage pipeline rather than the
//                 construct phase:
//                   • Etching Ward: inscribe a tile rune; the
//                     next construct deployed there enters with a
//                     bonus (a tile-scoped Master Schematic).
//                   • Redirector Field: reroute the owner's next
//                     incoming damage to a construct.
//                   • Familiar: an echo aura (created on deploy)
//                     that replays the owner's spells while alive.
// Layer:          Effects
// Collaborators:  CombatManager.Constructs.cs (summon handler reads
//                 EtchingSystem.ConsumeWard; ConfigureTinkerConstruct
//                 creates FamiliarEchoAura), Unit.ConduitLink.cs
//                 (RedirectNextDamageTo), PersistentEffect.cs,
//                 GameState.cs (LastResolvedItem), TinkerEffects.cs
//                 (TinkerFx)
//
// WIRING REQUIRED:
//   • CombatManager._Ready          → `EtchingSystem.Clear();`
//   • RegisterSummonHandler (4a)    → fold ConsumeWard into the bonus
// ============================================================

/// <summary>Static registry of tile wards (Etching). A ward grants a one-time deploy bonus to the next construct placed on that tile. Reset per combat.</summary>
public static class EtchingSystem
{
    private static readonly Dictionary<Vector2I, int> Wards = new();

    public static void Clear() => Wards.Clear();

    public static void Inscribe(Vector2I axial, int amount)
    {
        if (amount <= 0) return;
        int existing = Wards.TryGetValue(axial, out var v) ? v : 0;
        Wards[axial] = Math.Max(existing, amount);
    }

    /// <summary>Returns the ward bonus on a tile and removes it (single use). 0 if none.</summary>
    public static int ConsumeWard(Vector2I axial)
    {
        if (Wards.TryGetValue(axial, out var v))
        {
            Wards.Remove(axial);
            return v;
        }
        return 0;
    }
}

// ── Etching Ward ───────────────────────────────────────────────────
/// <summary>Inscribes a ward on the targeted tile. The next construct deployed there enters with +amount HP and primary stat.</summary>
public sealed class EtchWardEffect : EffectBase
{
    private readonly int _amount;
    public EtchWardEffect(int amount) { _amount = amount; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        Vector2I? axial = null;
        if (targets != null)
        {
            foreach (var o in targets.Items)
            {
                if (o is TileData td) { axial = td.Axial; break; }
                if (o is HexTile tv) { axial = tv.Axial; break; }
                if (o is Unit u && u.CurrentTile != null) { axial = u.CurrentTile.Axial; break; }
            }
        }
        axial ??= s?.ActiveCasterUnit?.CurrentTile?.Axial;

        if (axial == null)
        {
            s?.Log("[Etching] No tile to inscribe.");
            return;
        }

        EtchingSystem.Inscribe(axial.Value, _amount);
        s?.Log($"[Etching] Ward inscribed at {axial.Value} (+{_amount} to next construct).");
    }
}

// ── Redirector Field ────────────────────────────────────────────────
/// <summary>Arms the caster so the next instance of damage they would take is rerouted in full to their sturdiest construct.</summary>
public sealed class RedirectorFieldEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var owner = s?.ActiveCasterUnit;
        if (owner == null)
        {
            s?.Log("[Redirector] No caster.");
            return;
        }

        int team = TinkerFx.CasterTeam(s);
        Unit best = null;
        int bestHp = -1;
        foreach (var c in ConstructRegistry.All(s, team))
        {
            if (c.Stats.Health > bestHp) { bestHp = c.Stats.Health; best = c; }
        }

        if (best == null)
        {
            s.Log("[Redirector] No construct to absorb. The field fizzles.");
            return;
        }

        owner.RedirectNextDamageTo = best;
        s.Log($"[Redirector] Next hit on {owner.Name} reroutes to {best.Name}.");
    }
}

// ── Familiar echo aura ──────────────────────────────────────────────
/// <summary>
/// Created when a Familiar is deployed. Once per turn, when its owner's spell
/// resolves, the Familiar replays that spell's effects. Lives as long as the
/// Familiar; culled the turn the Familiar dies. Does not echo the spell that
/// summoned it (starts the first turn already "spent").
/// </summary>
public sealed class FamiliarEchoAura : PersistentEffect
{
    public Unit Familiar;
    public Unit OwnerUnit;
    private bool _echoed = true;   // suppress echoing the summoning spell

    public FamiliarEchoAura(Unit familiar, Entity owner, Unit ownerUnit)
    {
        Familiar = familiar;
        Owner = owner;
        OwnerUnit = ownerUnit;
        TurnsRemaining = 1;   // stays alive (not decremented) until the Familiar dies
    }

    public override void Tick(GameState s)
    {
        if (Familiar == null || !Familiar.Stats.IsAlive || Familiar.IsDeathQueued)
        {
            TurnsRemaining = 0;   // Familiar gone, so expire
            return;
        }
        _echoed = false;          // refresh: ready to echo again this turn
        // intentionally no decrement: lifetime is tied to the Familiar
    }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (_echoed || casterUnit != OwnerUnit)
            return;
        if (Familiar == null || !Familiar.Stats.IsAlive)
            return;

        var item = s?.LastResolvedItem;
        if (item?.Ability?.Effects == null)
            return;

        _echoed = true;
        s.Log($"[Familiar] {Familiar.Name} echoes {item.Ability.Name ?? "the spell"}.");
        foreach (var eff in item.Ability.Effects)
            eff.Resolve(s, item.Caster, item.Targets, item.Snapshot);
    }
}
