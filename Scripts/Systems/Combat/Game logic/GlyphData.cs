using Godot;
using System;
using System.Linq;

// ============================================================
// GlyphData.cs   (drop-in replacement — extends the original)
//
// Purpose:        Data container for a prepared glyph on a hex
//                 tile. Originally enemy-enter only; now supports
//                 multiple trigger types, a lifetime, reusability,
//                 ally-benefit payloads, and on-trigger payoffs
//                 (draw / mana / Weave / heal). Backward-compatible
//                 with PlaceGlyphEffect, which still sets OwnerId/
//                 OwnerTeam/GameState/OnTrigger directly.
// Layer:          Data
// Collaborators:  TileData.cs (holds the Glyph ref),
//                 GlyphManager.cs (lifecycle + start-of-turn fire),
//                 Unit.cs (PlaceOnTile fires enter/ally-enter),
//                 PlaceGlyphEffect / Glyph effects (creators),
//                 WeaveAttunement.cs (prepare/trigger feed)
// See:            README §5.4 — Place Glyph effect
// ============================================================

/// <summary>How a glyph is triggered.</summary>
public enum GlyphTrigger
{
    /// <summary>An enemy of the owner steps onto the tile (the original behaviour).</summary>
    Enter,
    /// <summary>An enemy of the owner begins its turn on the tile.</summary>
    StartOfTurn,
    /// <summary>An ally of the owner steps onto the tile — applies the ally payload.</summary>
    AllyEnter,
    /// <summary>Any spell is cast within <see cref="Radius"/> tiles (fired by the cast pipeline / GlyphManager).</summary>
    SpellCastNear,
    /// <summary>Provides a benefit only while the owner stands on the tile (self throne).</summary>
    SelfStand,
    /// <summary>Only fired explicitly (Glyph Network, links). Never auto-fires on movement.</summary>
    Manual
}

/// <summary>One prepared glyph on a hex tile. Friendly units never trigger enemy glyphs and vice-versa; the owner team gates that. Enemy-harm glyphs carry <see cref="Damage"/>/<see cref="Status"/>; ally glyphs carry the Ally* payload; payoffs route to <see cref="Owner"/>.</summary>
public sealed class GlyphData
{
    // ── Identity (set by every creator, incl. legacy PlaceGlyphEffect) ──
    public string OwnerId;
    public int OwnerTeam;
    public GameState GameState;

    /// <summary>The unit that placed this glyph. Used for on-trigger payoffs (draw/mana/Weave/heal) and the Weave feed. May be null for legacy glyphs.</summary>
    public Unit Owner;

    /// <summary>Legacy closure trigger. When set, <see cref="Fire"/> invokes it and skips the declarative payload, so PlaceGlyphEffect-created glyphs behave exactly as before.</summary>
    public Action<Unit, GameState> OnTrigger;

    /// <summary>True once consumed (single-use glyphs). Reusable glyphs never set this.</summary>
    public bool Consumed;

    // ── Behaviour ──────────────────────────────────────────────────────
    public GlyphTrigger Trigger = GlyphTrigger.Enter;

    /// <summary>Turns before the glyph expires. -1 = lasts until triggered / permanent.</summary>
    public int DurationTurns = -1;

    /// <summary>When true, the glyph is not consumed when it fires.</summary>
    public bool Reusable;

    /// <summary>When true, hidden from the opponent (no visual hint shown).</summary>
    public bool Invisible;

    /// <summary>Detection radius for SpellCastNear / area glyphs. 0 = the tile itself.</summary>
    public int Radius;

    // ── Enemy-harm payload ─────────────────────────────────────────────
    public int Damage;
    public string Status;
    public int StatusDuration = 1;

    // ── Ally payload (AllyEnter / SelfStand) ───────────────────────────
    public int AllyArmor;
    public int AllyShield;
    public int AllyDamage;       // bonus spell damage granted while/standing
    public int AllyMana;

    // ── On-trigger payoffs to the owner ────────────────────────────────
    public int OwnerDraw;
    public int OwnerMana;
    public int OwnerWeave;
    public int OwnerHeal;

    // ── Linking (Sigil Link / Glyph Network / Web of Fate) ─────────────
    /// <summary>Glyphs sharing a non-zero link id trigger together. 0 = unlinked.</summary>
    public int LinkId;

    /// <summary>Extra damage added per other glyph that fires in the same batch (Glyph Network).</summary>
    public int CumulativeBonus;

    /// <summary>When &gt; 0, on trigger this glyph re-prepares a copy of itself on this many adjacent tiles (Runic Cascade). The GlyphManager handles the spread in OnGlyphFired.</summary>
    public int CascadeSpread;

    /// <summary>
    /// Applies this glyph's payload. If a legacy <see cref="OnTrigger"/> closure is set,
    /// that runs instead (preserving original PlaceGlyphEffect behaviour). Otherwise the
    /// declarative payload is applied: enemy-harm to <paramref name="who"/> for enemy
    /// triggers, the ally payload for ally triggers, plus owner payoffs.
    /// </summary>
    /// <param name="who">The unit that triggered the glyph (enemy for harm glyphs, ally for ward glyphs).</param>
    /// <param name="s">Active game state.</param>
    /// <param name="bonusDamage">Cumulative bonus from a linked/network batch.</param>
    public void Fire(Unit who, GameState s, int bonusDamage = 0)
    {
        if (OnTrigger != null)
        { OnTrigger.Invoke(who, s); return; }

        bool friendlyToOwner = who != null && who.TeamId == OwnerTeam;

        if (!friendlyToOwner && who != null)
        {
            int dmg = Damage + bonusDamage + (Owner?.BonusSpellDamage ?? 0);

            if (s?.ActiveEffects != null && OwnerTeam >= 0)
            {
                bool grandDesign = s.ActiveEffects.Any(e =>
                    e is GrandDesignPersistentEffect gd &&
                    gd.OwnerUnit?.TeamId == OwnerTeam &&
                    !e.IsExpired);
                if (grandDesign)
                    dmg *= 2;
            }
            if (dmg > 0)
                who.ApplyDamage(dmg);
            if (!string.IsNullOrEmpty(Status))
                who.ApplyStatus(Status, StatusDuration);
            if (dmg > 0 || Status != null)
                s.Log($"[Glyph] {who.Name} triggers glyph: {dmg} dmg" + (Status != null ? $", {Status} {StatusDuration}t" : ""));
        }
        else if (friendlyToOwner && who != null)
        {
            if (AllyArmor > 0)
                who.Stats.Armor += AllyArmor;
            if (AllyShield > 0)
                who.Stats.Shield += AllyShield;
            if (AllyDamage > 0)
                who.BonusSpellDamage += AllyDamage;
            if (AllyMana > 0)
                who.GainMana(AllyMana);
            who.RefreshHealthBar();
            s.Log($"[Glyph] {who.Name} steps on ward: +{AllyArmor} armor, +{AllyShield} shield, +{AllyDamage} dmg.");
        }

        // Owner payoffs
        if (Owner != null)
        {
            if (OwnerDraw > 0)
                Owner.DeckData?.Draw(OwnerDraw);
            if (OwnerMana > 0)
                Owner.GainMana(OwnerMana);
            if (OwnerHeal > 0)
            { Owner.Stats.Health = Math.Min(Owner.Stats.MaxHealth, Owner.Stats.Health + OwnerHeal); Owner.RefreshHealthBar(); }
            if (OwnerWeave > 0 && Owner.Attunement is WeaveAttunement w)
                w.Add(OwnerWeave);
        }
    }
}
