using Godot;
using System;

// ============================================================
// WeaveAttunement.cs
//
// Purpose:        The Enchanter school mechanic — the Weave.
//                 A single control counter (0-4) that builds as
//                 the Enchanter prepares glyphs and as those
//                 glyphs trigger, representing the tightening
//                 layers of a working. Tiered thresholds grant
//                 escalating passive control (glyph damage, glyph
//                 duration, silence-on-Named); at 4 the Seventh
//                 Layer fires — the Enchanter Names an enemy
//                 (half damage, cannot move) and the Weave resets.
//                 Unlike Grief, the Weave does not bleed each
//                 turn; instead it unravels (resets to 0) at end
//                 of turn if the Enchanter holds no prepared tiles
//                 — control only persists while the web stands.
// Layer:          System
// Collaborators:  Unit.cs (owns instance via Attunement),
//                 GlyphManager.cs (calls OnGlyphPrepared /
//                 OnGlyphTriggered; reads Tier for passives),
//                 CombatManager.cs (calls OnTurnEnd; listens to
//                 OnSeventhLayer to Name an enemy),
//                 SchoolAttunementUI.cs (renders "Enchantment
//                 Weave"), EnchanterWeaveEffects.cs (gain_weave,
//                 damage_per_glyph read this).
// See:            README §6 — School Mechanics
// ============================================================

/// <summary>Control bands the Weave passes through. Each grants a passive read by the glyph/status systems; the systems query <see cref="WeaveAttunement.Tier"/> rather than hard-coding charge numbers.</summary>
public enum WeaveTier
{
    /// <summary>0 — no working in place.</summary>
    Loose,
    /// <summary>1 — prepared glyphs deal +1 damage.</summary>
    Taut,
    /// <summary>2 — prepared glyphs also last +1 turn.</summary>
    Woven,
    /// <summary>3 — Named / weakened enemies are also silenced (cannot cast).</summary>
    Bound,
    /// <summary>4 — the Seventh Layer: Name an enemy, then reset to 0.</summary>
    SeventhLayer
}

/// <summary>
/// The Enchanter class mechanic. A single non-decaying control counter that builds
/// from glyph activity and unravels only when the Enchanter has no prepared tiles on
/// the board at end of turn. The bands grant passive control; the cap triggers a Naming
/// burst. This is the "tightening web" companion to the glyph board state, the way Grief
/// is the companion to memorials.
/// </summary>
public class WeaveAttunement : ISchoolAttunement
{
    public CardSchool School => CardSchool.Enchanter;

    // ── Core state ───────────────────────────────────────────────────
    /// <summary>Current Weave, clamped to [0, <see cref="MaxWeave"/>]. Persists across turns while a web stands.</summary>
    public int Weave { get; private set; } = 0;

    /// <summary>The Seventh Layer cap. Reaching it fires <see cref="OnSeventhLayer"/> and resets to 0.</summary>
    public const int MaxWeave = 4;

    // ── Events for UI and combat wiring ──────────────────────────────
    /// <summary>Fired whenever Weave changes. Carries the new value.</summary>
    public event Action<int> OnWeaveChanged;

    /// <summary>Fired when Weave crosses a <see cref="WeaveTier"/> boundary upward. Carries the tier just entered.</summary>
    public event Action<WeaveTier> OnTierReached;

    /// <summary>Fired when the Weave reaches the cap. CombatManager listens and Names an enemy (applies the "named" status), since the attunement must not reach into the unit list itself.</summary>
    public event Action OnSeventhLayer;

    // ── ISchoolAttunement ────────────────────────────────────────────
    public void OnCombatStart() => SetWeave(0);

    /// <summary>
    /// No-op by design. The Weave does not bleed per turn like Grief — it unravels only
    /// when the web is gone. Use <see cref="OnTurnEnd"/> for that. Kept to satisfy
    /// <see cref="ISchoolAttunement"/>.
    /// </summary>
    public void Decay() { /* The Weave does not decay on a timer — see OnTurnEnd. */ }

    // ── Turn-end unravel ─────────────────────────────────────────────
    /// <summary>Call at the end of the Enchanter's turn. If no prepared tiles remain on the board, the working collapses and Weave resets to 0. CombatManager supplies the count from GlyphManager.</summary>
    public void OnTurnEnd(bool hasPreparedTiles)
    {
        if (!hasPreparedTiles && Weave > 0)
        {
            SetWeave(0);
        }
    }

    // ── Glyph hooks ──────────────────────────────────────────────────
    /// <summary>Call when the Enchanter prepares a glyph. Adds 1 Weave.</summary>
    public void OnGlyphPrepared() => Add(1);

    /// <summary>Call when one of the Enchanter's glyphs triggers. Adds 1 Weave.</summary>
    public void OnGlyphTriggered() => Add(1);

    // ── Weave mutation ───────────────────────────────────────────────
    /// <summary>Adds <paramref name="amount"/> Weave. If this reaches the cap, fires <see cref="OnSeventhLayer"/> and resets to 0. Returns the value after resolution.</summary>
    public int Add(int amount)
    {
        if (amount <= 0) return Weave;

        int target = Weave + amount;
        if (target >= MaxWeave)
        {
            SetWeave(MaxWeave);          // show the cap tick on the UI
            OnSeventhLayer?.Invoke();    // CombatManager Names an enemy
            SetWeave(0);                 // then unravel back to 0
            return Weave;
        }

        SetWeave(target);
        return Weave;
    }

    /// <summary>Sets Weave to an exact value (clamped), bypassing the Seventh Layer burst. For effects/tests that snap the counter.</summary>
    public void SetWeaveDirectly(int value) => SetWeave(value);

    /// <summary>Current control band for this Weave value.</summary>
    public WeaveTier Tier => TierFor(Weave);

    /// <summary>Maps a raw Weave value to its <see cref="WeaveTier"/>.</summary>
    public static WeaveTier TierFor(int weave) => weave switch
    {
        >= 4 => WeaveTier.SeventhLayer,
        3 => WeaveTier.Bound,
        2 => WeaveTier.Woven,
        1 => WeaveTier.Taut,
        _ => WeaveTier.Loose
    };

    /// <summary>Passive: bonus damage the current band grants to prepared glyphs. Read by GlyphManager when a glyph fires.</summary>
    public int GlyphDamageBonus => Weave >= 1 ? 1 : 0;

    /// <summary>Passive: extra turns the current band adds to prepared-glyph duration. Read by GlyphManager at placement.</summary>
    public int GlyphDurationBonus => Weave >= 2 ? 1 : 0;

    /// <summary>Passive: whether Named/weakened enemies are also silenced. Read by the status system.</summary>
    public bool SilencesControlled => Weave >= 3;

    // ── Internal ─────────────────────────────────────────────────────
    private void SetWeave(int value)
    {
        int clamped = Math.Clamp(value, 0, MaxWeave);
        if (clamped == Weave) return;

        WeaveTier before = TierFor(Weave);
        Weave = clamped;
        OnWeaveChanged?.Invoke(Weave);

        WeaveTier after = TierFor(Weave);
        if (after > before) OnTierReached?.Invoke(after);
    }
}
