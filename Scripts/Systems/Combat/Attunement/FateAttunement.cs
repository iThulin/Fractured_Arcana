using Godot;
using System;

// ============================================================
// FateAttunement.cs
//
// Purpose:        The Chronomancer school mechanic: a single
//                 Foresight counter (0-4) that builds when
//                 spells are cast off-turn (Instant/Reaction)
//                 or when more than one spell is cast per turn
//                 (tempo gain). Two passive thresholds unlock
//                 bonuses; at 4 a burst fires (grants one free
//                 Reaction this turn), then resets to 0.
//
//                 Unlike GriefAttunement, Foresight does NOT
//                 decay at turn end. It is only spent by
//                 effects or consumed by the burst.
//
// Layer:          System
// Collaborators:  Unit.cs (owns instance via Attunement field,
//                   InitializeAttunement switch),
//                 ChronomancerEffects.cs (GainForesightEffect),
//                 GameRunner.cs (calls OnSpellCast after each
//                   successful resolution),
//                 SchoolAttunementUI.cs (renders the bar),
//                 RulesManager.cs (queries GetInstantCostReduction,
//                   HasFreeReaction, ConsumeFreeReaction)
// See:            README §6, School Mechanics
//                 chronomancer_wiring.md for the exact changes needed
//                 in Unit.cs, GameRunner.cs, SchoolAttunementUI.cs
// ============================================================

public enum ForesightTier
{
    Blind,      // 0: no bonus
    Glimpsed,   // 1: minor awareness
    Aware,      // 2: Instant spells cost 1 less mana
    Prescient,  // 3: enhanced prediction
    Foreseen    // 4: burst, one free Reaction, then reset
}

public class FateAttunement : ISchoolAttunement
{
    public CardSchool School => CardSchool.Chronomancer;

    // ── Core state ───────────────────────────────────────────────────
    public int Charges { get; private set; } = 0;
    public const int MaxCharges = 4;
    public const int BurstThreshold = 4;

    // ── Events for UI and card effects ──────────────────────────────
    public event Action<int> OnChargeChanged;   // new charge value
    public event Action<ForesightTier> OnTierReached;     // tier crossed upward
    public event Action OnBurstTriggered;  // burst fired

    // ── Burst state, queried by RulesManager ─────────────────────────
    /// <summary>True after a burst until consumed by one free Reaction cast.</summary>
    public bool HasFreeReaction { get; private set; } = false;

    /// <summary>Call this from RulesManager after allowing the free Reaction cast.</summary>
    public void ConsumeFreeReaction() => HasFreeReaction = false;

    // ── ISchoolAttunement ────────────────────────────────────────────
    public void OnCombatStart() => SetCharges(0);

    /// <summary>
    /// Foresight does NOT decay at turn end, so this override does nothing.
    /// Called by GameRunner at the start of each turn but intentionally
    /// left as a no-op so Foresight can only leave by burst or spending.
    /// </summary>
    public void Decay() { }

    // ── Called by GameRunner after each successful spell resolution ──
    /// <summary>
    /// Hook called by the combat runner immediately after a spell resolves
    /// for a Chronomancer unit. Grants Foresight based on:
    ///   +1 if the half was cast at Instant or Reaction speed.
    ///   +1 if this is the 2nd-or-later spell the unit cast this turn.
    /// Both bonuses can stack (max +2 per cast).
    /// </summary>
    /// <param name="speed">The PlaySpeed of the half that just resolved.</param>
    /// <param name="spellsThisTurn">Total spells cast by this unit this turn, including this one.</param>
    public void OnSpellCast(PlaySpeed speed, int spellsThisTurn)
    {
        // Time Bank (2026-07-10): passive per-cast Foresight is RETIRED.
        // Foresight now comes from banking unspent mana at end of turn
        // (BankUnspentMana) and from explicit gain_foresight card effects.
    }

    // ── Gain / spend ─────────────────────────────────────────────────
    /// <summary>Add Foresight charges, fire tier events, and trigger burst at max.</summary>
    public void GainCharges(int amount)
    {
        int previous = Charges;
        int next = Math.Min(MaxCharges, Charges + amount);

        for (int i = previous + 1; i <= next; i++)
        {
            var tier = ChargeToTier(i);
            if (tier != ForesightTier.Blind)
                OnTierReached?.Invoke(tier);
        }

        SetCharges(next);
        GD.Print($"[TimeBank] +{amount} Foresight: {previous} -> {Charges}.");
    }

    /// <summary>Time Bank (2026-07-10, tick ruling same day): at end of the player
    /// turn the bank gains 1 automatically (time passes whether spent or not)
    /// PLUS any unspent mana (capped at MaxCharges). Holding mana is acceleration,
    /// not a prerequisite for reacting. Called from EndPlayerTurn.</summary>
    public void BankUnspentMana(int unspentMana)
    {
        int gain = 1 + Math.Max(0, unspentMana);
        if (Charges >= MaxCharges)
            return;
        GD.Print(unspentMana > 0
            ? $"[TimeBank] Time passes (+1) and {unspentMana} unspent mana banked."
            : "[TimeBank] Time passes (+1).");
        GainCharges(gain);
    }

    /// <summary>Time Bank (2026-07-10): a FULL bank at enemy-turn start grants one
    /// free Reaction this phase (no reset; the bank still backs later reactions).
    /// Called from RunEnemyTurn. Replaces the old burst-and-reset.</summary>
    public void OnEnemyTurnStart()
    {
        HasFreeReaction = Charges >= MaxCharges;
        if (HasFreeReaction)
        {
            GD.Print("[TimeBank] Full bank: first Reaction this enemy phase is free.");
            OnBurstTriggered?.Invoke();
        }
    }

    /// <summary>Spend Foresight charges (e.g. to upgrade redirect to chosen-target).</summary>
    public void SpendCharges(int amount)
    {
        SetCharges(Math.Max(0, Charges - amount));
    }

    /// <summary>Direct set, used by effects like set_foresight. Bypasses tier events.</summary>
    public void SetChargesDirectly(int value)
    {
        SetCharges(Math.Clamp(value, 0, MaxCharges));
    }

    /// <summary>Current tier for UI display and effect queries.</summary>
    public ForesightTier CurrentTier => ChargeToTier(Charges);

    // ── Private helpers ──────────────────────────────────────────────
    private void SetCharges(int value)
    {
        Charges = Math.Clamp(value, 0, MaxCharges);
        OnChargeChanged?.Invoke(Charges);
    }

    private static ForesightTier ChargeToTier(int charges) => charges switch
    {
        0 => ForesightTier.Blind,
        1 => ForesightTier.Glimpsed,
        2 => ForesightTier.Aware,
        3 => ForesightTier.Prescient,
        _ => ForesightTier.Foreseen
    };
}
