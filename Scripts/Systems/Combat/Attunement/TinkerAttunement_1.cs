using System;

// ============================================================
// TinkerAttunement.cs
//
// Purpose:        The Tinker school mechanic — "Contraption
//                 Assembly", tracked as the Schematics ledger.
//                 A monotonic per-combat counter that ticks up
//                 every time one of the player's constructs is
//                 destroyed (by any cause, including Heat
//                 burnout). Each tier grants newly-deployed
//                 constructs a flat +HP / +primary-stat bonus.
//                 Does NOT decay — losing hardware is how Bram
//                 Korro iterates, so the lesson is permanent for
//                 the fight.
// Layer:          System
// Collaborators:  Unit.cs (each Tinker unit owns one via
//                 InitializeAttunement), CombatManager.cs
//                 (RegisterConstructLoss increments it; the
//                 summon handler reads DeployBonus + ConstructCap),
//                 ConstructRegistry.cs (cap enforcement),
//                 SchoolAttunementUI.cs (renders the tier — TODO,
//                 currently a "Coming soon" stub for Tinker)
// See:            ISchoolAttunement (ElementalAttunement.cs)
// ============================================================

/// <summary>
/// Schematics ledger for the Tinker school. Monotonic per-combat tier counter
/// (0..<see cref="MaxTier"/>) fed by construct destruction. <see cref="DeployBonus"/>
/// is the flat stat bump applied to each construct on deploy; <see cref="ConstructCap"/>
/// is the live limit on simultaneous constructs (raised by the Capacity card).
/// </summary>
public class TinkerAttunement : ISchoolAttunement
{
    public CardSchool School => CardSchool.Tinker;

    /// <summary>Highest Schematics tier reachable in a single combat.</summary>
    public const int MaxTier = 5;

    /// <summary>Default simultaneous-construct limit before the Capacity card modifies it.</summary>
    public const int BaseConstructCap = 5;

    /// <summary>Current Schematics tier (0..MaxTier). Climbs on construct loss, never falls.</summary>
    public int Tier { get; private set; } = 0;

    /// <summary>Live simultaneous-construct cap. Capacity (Legendary) raises this to 8 or removes it (int.MaxValue).</summary>
    public int ConstructCap = BaseConstructCap;

    /// <summary>Flat +HP / +primary-stat granted to each construct on deploy. Linear with tier.</summary>
    public int DeployBonus => Tier;

    /// <summary>Fires when the tier changes so the attunement UI can refresh. Argument is the new tier.</summary>
    public event Action<int> OnTierChanged;

    /// <summary>
    /// Called by CombatManager whenever a player-owned construct is destroyed.
    /// Bram notes the failure down — the next construct is stronger.
    /// </summary>
    public void RegisterConstructDestroyed()
    {
        if (Tier >= MaxTier)
            return;
        Tier++;
        OnTierChanged?.Invoke(Tier);
    }

    // ── ISchoolAttunement ───────────────────────────────────────────

    /// <summary>No-op. Schematics is monotonic within a combat — it does not decay between turns.</summary>
    public void Decay() { }

    /// <summary>Resets the ledger at the start of each combat.</summary>
    public void OnCombatStart()
    {
        Tier = 0;
        ConstructCap = BaseConstructCap;
        OnTierChanged?.Invoke(Tier);
    }
}
