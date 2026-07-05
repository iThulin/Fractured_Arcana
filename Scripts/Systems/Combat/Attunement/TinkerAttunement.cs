using System;

// ============================================================
// TinkerAttunement.cs
//
// Purpose:        The Tinker school mechanic — "Contraption
//                 Assembly", tracked as the Schematics ledger.
//                 A monotonic per-combat counter that ticks up
//                 every time one of the player's constructs is
//                 destroyed (any cause, including Heat burnout).
//                 Each tier grants newly-deployed constructs a
//                 flat +HP / +primary-stat bonus. Does NOT decay
//                 — losing hardware is how the Tinker iterates,
//                 so the lesson is permanent for the fight.
//                 Also holds the live construct cap (Capacity)
//                 and the Master Schematic pending-bonus queue.
// Layer:          System
// Collaborators:  Unit.cs (each Tinker unit owns one via
//                 InitializeAttunement), CombatManager.Constructs.cs
//                 (increments it; reads DeployBonus / ConstructCap /
//                 pending bonus on deploy), TinkerEffects.cs
//                 (Capacity / Master Schematic set these),
//                 ConstructRegistry.cs (cap enforcement)
// See:            ISchoolAttunement (ElementalAttunement.cs)
// ============================================================

/// <summary>
/// Schematics ledger for the Tinker school. Monotonic per-combat tier counter
/// (0..<see cref="MaxTier"/>) fed by construct destruction. <see cref="DeployBonus"/>
/// is the flat stat bump applied to each construct on deploy; <see cref="ConstructCap"/>
/// is the live simultaneous-construct limit (raised by Capacity); the pending-bonus
/// queue is the Master Schematic "next N constructs enter stronger" effect.
/// </summary>
public class TinkerAttunement : ISchoolAttunement
{
    public CardSchool School => CardSchool.Tinker;

    public const int MaxTier = 5;
    public const int BaseConstructCap = 5;

    /// <summary>Current Schematics tier (0..MaxTier). Climbs on construct loss, never falls.</summary>
    public int Tier { get; private set; } = 0;

    /// <summary>Live simultaneous-construct cap. Capacity raises this (e.g. 8, or int.MaxValue for unlimited).</summary>
    public int ConstructCap = BaseConstructCap;

    /// <summary>Flat +HP / +primary-stat granted to each construct on deploy from the Schematics tier alone. Linear with tier.</summary>
    public int DeployBonus => Tier;

    // ── Master Schematic: "the next N constructs enter with +amount" ──
    private int _pendingCharges = 0;
    private int _pendingAmount = 0;

    /// <summary>Fires when the tier changes so the attunement UI can refresh. Argument is the new tier.</summary>
    public event Action<int> OnTierChanged;

    /// <summary>The Tinker notes a failure down — the next construct is stronger.</summary>
    public void RegisterConstructDestroyed()
    {
        if (Tier >= MaxTier)
            return;
        Tier++;
        OnTierChanged?.Invoke(Tier);
    }

    /// <summary>Queues a temporary deploy bonus for the next <paramref name="charges"/> constructs (Master Schematic).</summary>
    public void AddPendingBonus(int charges, int amount)
    {
        if (charges <= 0 || amount <= 0)
            return;
        _pendingCharges += charges;
        _pendingAmount = Math.Max(_pendingAmount, amount);
    }

    /// <summary>Consumes one pending-bonus charge, returning the extra stat bump for a single deploy (0 if none queued).</summary>
    public int ConsumePendingBonus()
    {
        if (_pendingCharges <= 0)
            return 0;
        _pendingCharges--;
        return _pendingAmount;
    }

    // ── ISchoolAttunement ───────────────────────────────────────────

    /// <summary>No-op. Schematics is monotonic within a combat — it does not decay between turns.</summary>
    public void Decay() { }

    /// <summary>Resets the ledger at the start of each combat.</summary>
    public void OnCombatStart()
    {
        Tier = 0;
        ConstructCap = BaseConstructCap;
        _pendingCharges = 0;
        _pendingAmount = 0;
        OnTierChanged?.Invoke(Tier);
    }
}
