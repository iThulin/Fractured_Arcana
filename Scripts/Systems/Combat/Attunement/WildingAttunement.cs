using Godot;
using System;

// ============================================================
// WildingAttunement.cs
//
// Purpose:        The Druid school mechanic — a single "Wilding"
//                 counter (0-4) that builds as the owner's living
//                 terrain seeds, spreads, and advances, and decays
//                 each turn when the land is left untended. Tiered
//                 thresholds unlock passive bonuses; at 4 a Riot
//                 fires (mass-advance + wildlife via GrowthManager),
//                 then the counter resets to 0 — the wheel turns.
// Layer:          System
// Collaborators:  Unit.cs (each Druid unit owns one via Attunement),
//                 GrowthManager.cs (raises charges on growth events,
//                 reads tier bonuses, applies the Riot burst),
//                 SchoolAttunementUI.cs (renders charges),
//                 Effect.cs (GainWildingEffect mutates this later).
// See:            README §6 — School Mechanics
// ============================================================

/// <summary>
/// Folkloric stages of the Druid's Wilding counter — the wild stirring, spreading,
/// running rampant, then breaking into a Riot. Deliberately not clinical: this is
/// "nature taking its course faster," not an ecology textbook.
/// </summary>
public enum WildingTier
{
    Still,      // 0 — dormant, no bonus
    Stirring,   // 1 — living terrain spreads faster
    Spreading,  // 2 — owner heals while on/adjacent to their own living terrain
    Rampant,    // 3 — enemies on the owner's growth are rooted on the tick
    Riot        // 4 — burst: all owned growth advances a stage + wildlife, then reset to 0
}

public class WildingAttunement : ISchoolAttunement
{
    public CardSchool School => CardSchool.Druid;

    // ── Core state ───────────────────────────────────────────────────
    public int Charges { get; private set; } = 0;
    public const int MaxCharges = 4;
    public const int RiotThreshold = 4;

    // ── Tier-passive tuning (read by GrowthManager / upkeep) ─────────
    public const float StirringSpreadBonus = 0.15f;  // +flat spread chance at Stirring+
    public const int SpreadingHealPerTurn = 2;      // HP/turn at Spreading+ (applied at owner upkeep)
    public const int RampantRootDuration = 1;      // turns rooted at Rampant+

    // ── Events for UI and card effects ───────────────────────────────
    public event Action<int> OnChargeChanged;        // new charge value
    public event Action<WildingTier> OnTierReached;  // tier crossed upward
    public event Action OnRiotTriggered;             // burst fired — GrowthManager listens

    // ── ISchoolAttunement ─────────────────────────────────────────────
    public void OnCombatStart() => SetCharges(0);

    public void Decay()
    {
        if (Charges > 0)
            SetCharges(Charges - 1);
    }

    // ── Charge mutation ───────────────────────────────────────────────

    /// <summary>
    /// Add charges (clamped). Raised by GrowthManager whenever the owner's growth seeds,
    /// spreads, or advances, and by GainWildingEffect. Reaching the RiotThreshold fires the
    /// Riot burst then resets to 0. ApplyRiot must NOT call back into this (no re-entrancy).
    /// </summary>
    public void GainCharges(int amount)
    {
        if (amount <= 0) return;

        WildingTier before = CurrentTier;
        int next = Charges + amount;

        if (next >= RiotThreshold)
        {
            SetCharges(RiotThreshold);   // show the full gauge first
            OnRiotTriggered?.Invoke();   // GrowthManager applies the surge
            SetCharges(0);               // the wheel turns
            return;
        }

        SetCharges(next);
        if (CurrentTier > before)
            OnTierReached?.Invoke(CurrentTier);
    }

    private void SetCharges(int value)
    {
        int clamped = Mathf.Clamp(value, 0, MaxCharges);
        if (clamped == Charges) return;
        Charges = clamped;
        OnChargeChanged?.Invoke(Charges);
    }

    // ── Tier queries (single source of truth for GrowthManager/UI) ────
    public WildingTier CurrentTier => (WildingTier)Mathf.Clamp(Charges, 0, (int)WildingTier.Riot);

    /// <summary>Flat bonus added to spread chance once the owner reaches Stirring.</summary>
    public float SpreadBonus => Charges >= (int)WildingTier.Stirring ? StirringSpreadBonus : 0f;

    /// <summary>True once the owner heals from standing on/adjacent to their living terrain.</summary>
    public bool HealsOwner => Charges >= (int)WildingTier.Spreading;

    /// <summary>True once enemies on the owner's living terrain are rooted on the tick.</summary>
    public bool RootsEnemies => Charges >= (int)WildingTier.Rampant;
}
