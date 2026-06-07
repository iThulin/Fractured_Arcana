using Godot;
using System;

// ============================================================
// ArcaneAttunement.cs
//
// Purpose:        The Arcanist school mechanic — the Grimoire.
//                 Two coupled pieces of state held in one
//                 ISchoolAttunement:
//                   (1) Charge — a banked combo currency (0-6),
//                       card-driven gain, spent by payoff cards,
//                       does NOT decay (knowledge is kept).
//                   (2) Grimoire memory — the last spell cast
//                       this turn and the spell-count this turn,
//                       which the school's "meta-magic" cards
//                       read (Replicate, Spell Surge, etc.).
//                 Charge persists across turns within a combat;
//                 Grimoire memory resets each turn.
// Layer:          System
// Collaborators:  Unit.cs (owns instance via Attunement),
//                 CombatManager.cs (subscribes the "AbilityCast"
//                 bus event -> OnSpellCast; calls OnTurnStart),
//                 SchoolAttunementUI.cs (renders the "Arcane
//                 Focus" panel), ArcanistChargeEffects.cs
//                 (GainChargeEffect / SpendChargeDamageEffect /
//                 DamagePerSpellCastEffect read this).
// See:            README §6 — School Mechanics
// ============================================================

/// <summary>Passive bands the Grimoire passes through as Charge accumulates. These are read by cards (e.g. "if 3+ charges …"); the bands themselves grant no automatic effect, matching the Arcanist's combo-driven, card-gated design rather than the Elementalist/Necromancer auto-threshold model.</summary>
public enum ChargeTier
{
    /// <summary>0-1 charge — no banked power worth spending.</summary>
    Latent,
    /// <summary>2-3 charge — enough for most single-spend payoffs.</summary>
    Resonant,
    /// <summary>4-5 charge — chain/AoE thresholds come online.</summary>
    Charged,
    /// <summary>6 charge — the cap; further gains overflow into card draw.</summary>
    Overflowing
}

/// <summary>
/// The Arcanist class mechanic. Unlike the Elementalist (four opposed counters)
/// or the Necromancer (one decaying Grief counter), the Arcanist banks a single
/// non-decaying <see cref="Charge"/> counter and keeps a short Grimoire memory
/// (<see cref="LastSpellId"/>, <see cref="SpellsCastThisTurn"/>) that meta-magic
/// cards consume. The bank-vs-spend tension is the school's identity: holding
/// charge unlocks higher card thresholds and bigger Overcharge/Disintegrate hits,
/// but every payoff card empties the reserve.
/// </summary>
public class ArcaneAttunement : ISchoolAttunement
{
    public CardSchool School => CardSchool.Arcanist;

    // ── Core state ───────────────────────────────────────────────────
    /// <summary>Banked combo currency. Card-driven gain, spent by payoff cards. Clamped to [0, <see cref="MaxCharge"/>]. Persists across turns within a combat.</summary>
    public int Charge { get; private set; } = 0;

    /// <summary>Hard ceiling on banked Charge. Excess gains overflow (see <see cref="OnChargeOverflow"/>).</summary>
    public const int MaxCharge = 6;

    /// <summary>Number of spells (card halves) the Arcanist has cast since the start of the current turn. Reset by <see cref="OnTurnStart"/>. Read by Spell Surge / Arcane Drift / Replicate-style cards.</summary>
    public int SpellsCastThisTurn { get; private set; } = 0;

    /// <summary>Card id of the most recent spell cast this turn, or null if none yet. The Grimoire's "open page" — what Replicate copies. Reset by <see cref="OnTurnStart"/>.</summary>
    public string LastSpellId { get; private set; } = null;

    /// <summary>Display name of the most recent spell cast this turn, for UI/logging. Reset by <see cref="OnTurnStart"/>.</summary>
    public string LastSpellName { get; private set; } = null;

    // ── Events for UI and card effects ──────────────────────────────
    /// <summary>Fired whenever Charge changes. Carries the new value.</summary>
    public event Action<int> OnChargeChanged;

    /// <summary>Fired when Charge crosses a <see cref="ChargeTier"/> boundary upward. Carries the tier just entered.</summary>
    public event Action<ChargeTier> OnTierReached;

    /// <summary>Fired when a Charge gain is wasted against the cap. Carries the overflow amount. CombatManager listens and converts overflow to card draw (the attunement must not touch the deck directly).</summary>
    public event Action<int> OnChargeOverflow;

    /// <summary>Fired when the Grimoire records a spell cast this turn. Carries the new <see cref="SpellsCastThisTurn"/> count.</summary>
    public event Action<int> OnSpellRecorded;

    // ── ISchoolAttunement ────────────────────────────────────────────
    public void OnCombatStart()
    {
        SetChargesDirectly(0);
        OnTurnStart();
    }

    /// <summary>
    /// No-op by design. Charge is banked knowledge — it does not bleed away between
    /// turns the way Grief does. The Arcanist manages the reserve by spending it.
    /// Kept to satisfy <see cref="ISchoolAttunement"/>.
    /// </summary>
    public void Decay() { /* Charge does not decay — see summary. */ }

    // ── Per-turn Grimoire reset ──────────────────────────────────────
    /// <summary>Clears the per-turn Grimoire memory (spell count + last-spell page). Call from CombatManager at the start of each Arcanist turn. Does NOT touch Charge.</summary>
    public void OnTurnStart()
    {
        SpellsCastThisTurn = 0;
        LastSpellId = null;
        LastSpellName = null;
        OnSpellRecorded?.Invoke(0);
    }

    // ── Hook: a spell was cast ───────────────────────────────────────
    /// <summary>Records that the Arcanist cast a spell this turn. Wire this to the GameState "AbilityCast" bus event from CombatManager. Note: this does NOT grant Charge — charge gain is explicit, via cards using the <c>gain_charge</c> effect.</summary>
    /// <param name="cardId">Stable id of the cast card (for Replicate lookup).</param>
    /// <param name="displayName">Human-readable name for UI/log.</param>
    public void OnSpellCast(string cardId, string displayName)
    {
        SpellsCastThisTurn++;
        LastSpellId = cardId;
        LastSpellName = displayName;
        OnSpellRecorded?.Invoke(SpellsCastThisTurn);
    }

    // ── Charge mutation ──────────────────────────────────────────────
    /// <summary>Adds <paramref name="amount"/> Charge, clamped to the cap. Any amount lost to the cap is reported via <see cref="OnChargeOverflow"/>. Returns the amount actually banked.</summary>
    public int Add(int amount)
    {
        if (amount <= 0)
            return 0;
        int before = Charge;
        int target = before + amount;
        int banked = Math.Min(target, MaxCharge) - before;
        int overflow = target - MaxCharge;

        GD.Print($"[ArcaneAttunement] Add({amount}): before={before} target={target} banked={banked} overflow={overflow}");

        if (banked > 0)
            SetCharges(before + banked);
        if (overflow > 0)
            OnChargeOverflow?.Invoke(overflow);
        return banked;
    }

    /// <summary>Spends up to <paramref name="amount"/> Charge and returns how much was actually spent (never more than the current reserve).</summary>
    public int Spend(int amount)
    {
        if (amount <= 0 || Charge <= 0)
            return 0;
        int spent = Math.Min(amount, Charge);
        SetCharges(Charge - spent);
        return spent;
    }

    /// <summary>Spends the entire reserve and returns the amount spent.</summary>
    public int SpendAll()
    {
        int spent = Charge;
        SetCharges(0);
        return spent;
    }

    /// <summary>Sets Charge to an exact value (clamped), bypassing overflow logic. For effects/tests that snap the counter.</summary>
    public void SetChargesDirectly(int value) => SetCharges(value);

    /// <summary>Current passive band for this Charge value (read by cards / UI).</summary>
    public ChargeTier Tier => TierFor(Charge);

    /// <summary>Maps a raw Charge value to its <see cref="ChargeTier"/> band.</summary>
    public static ChargeTier TierFor(int charge)
    {
        if (charge >= MaxCharge)
            return ChargeTier.Overflowing;
        if (charge >= 4)
            return ChargeTier.Charged;
        if (charge >= 2)
            return ChargeTier.Resonant;
        return ChargeTier.Latent;
    }

    // ── Internal ─────────────────────────────────────────────────────
    private void SetCharges(int value)
    {
        int clamped = Math.Clamp(value, 0, MaxCharge);
        if (clamped == Charge)
            return;

        ChargeTier before = TierFor(Charge);
        Charge = clamped;
        OnChargeChanged?.Invoke(Charge);

        ChargeTier after = TierFor(Charge);
        if (after > before)
            OnTierReached?.Invoke(after);
    }
}
