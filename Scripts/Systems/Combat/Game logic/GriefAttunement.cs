using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// GriefAttunement.cs
//
// Purpose:        The Necromancer school mechanic — a single
//                 Grief counter (0-4) that builds when units
//                 die nearby and decays each turn. Tiered
//                 thresholds unlock passive bonuses; at 4 a
//                 Flood triggers, refreshing all active spirits
//                 then resetting to 0.
// Layer:          System
// Collaborators:  Unit.cs (owns instance via Attunement),
//                 MemorialManager.cs (fires OnMemorialCreated),
//                 SchoolAttunementUI.cs (renders the lantern),
//                 CombatManager.cs (wires memorial events),
//                 NecromancerEffects.cs (GainGriefEffect etc.)
// See:            README §6 — School Mechanics
// ============================================================

public enum GriefTier
{
    Dim,      // 0 — no bonus
    Kindled,  // 1 — raised spirits gain +1 HP
    Burning,  // 2 — raised spirits act immediately on arrival
    Flooding, // 3 — communion spells enhanced
    Flood     // 4 — burst: refresh all spirits, reset to 0
}

public class GriefAttunement : ISchoolAttunement
{
    public CardSchool School => CardSchool.Necromancer;

    // ── Core state ───────────────────────────────────────────────────
    public int Charges { get; private set; } = 0;
    public const int MaxCharges = 4;
    public const int FloodThreshold = 4;

    // ── Events for UI and card effects ──────────────────────────────
    public event Action<int> OnChargeChanged;       // new charge value
    public event Action<GriefTier> OnTierReached;   // tier crossed upward
    public event Action OnFloodTriggered;           // burst fired

    // ── ISchoolAttunement ────────────────────────────────────────────
    public void OnCombatStart() => SetCharges(0);

    public void Decay()
    {
        if (Charges > 0)
            SetCharges(Charges - 1);
    }

    // ── Called by CombatManager when a memorial is created ───────────
    public void OnDeath(MemorialData memorial)
    {
        // Ally deaths give more grief than enemy deaths
        int gain = memorial.WasAlly ? 2 : 1;

        // Stronger memorials (bosses, champions) give an extra charge
        if (memorial.Strength == MemorialStrength.Strong)
            gain += 1;

        GainCharges(gain);
    }

    // ── Add grief charges, fire tier events, trigger Flood at max ────
    public void GainCharges(int amount)
    {
        int previous = Charges;
        int next = Math.Min(MaxCharges, Charges + amount);

        // Fire tier events for any thresholds crossed upward
        for (int i = previous + 1; i <= next; i++)
        {
            var tier = ChargeToTier(i);
            if (tier != GriefTier.Dim)
                OnTierReached?.Invoke(tier);
        }

        SetCharges(next);

        if (Charges >= FloodThreshold)
            TriggerFlood();
    }

    // ── Called by GriefDischargeDamageEffect and set_grief ───────────
    public void SetChargesDirectly(int value)
    {
        SetCharges(Math.Clamp(value, 0, MaxCharges));
    }

    // ── Current tier for UI and effect queries ───────────────────────
    public GriefTier CurrentTier => ChargeToTier(Charges);

    // ── Private helpers ──────────────────────────────────────────────
    private void SetCharges(int value)
    {
        Charges = Math.Clamp(value, 0, MaxCharges);
        OnChargeChanged?.Invoke(Charges);
    }

    private void TriggerFlood()
    {
        OnFloodTriggered?.Invoke();
        SetCharges(0);
    }

    private static GriefTier ChargeToTier(int charges) => charges switch
    {
        0 => GriefTier.Dim,
        1 => GriefTier.Kindled,
        2 => GriefTier.Burning,
        3 => GriefTier.Flooding,
        _ => GriefTier.Flood
    };
}
