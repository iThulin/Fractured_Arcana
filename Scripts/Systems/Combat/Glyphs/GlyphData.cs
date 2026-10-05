using Godot;
using System;
using System.Linq;

// ============================================================
// GlyphData.cs   (drop-in replacement that extends the original)
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
// See:            README §5.4 (Place Glyph effect)
// ============================================================

/// <summary>How a glyph is triggered.</summary>
public enum GlyphTrigger
{
    /// <summary>An enemy of the owner steps onto the tile (the original behaviour).</summary>
    Enter,
    /// <summary>An enemy of the owner begins its turn on the tile.</summary>
    StartOfTurn,
    /// <summary>An ally of the owner steps onto the tile, which applies the ally payload.</summary>
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

    /// <summary>
    /// Blueprint id of the card that placed this glyph, and which half of it. Copied by
    /// <c>PrepareGlyphEffect.Configure</c> from fields stamped onto the effect at LOAD
    /// time by <c>JsonCardLoader.StampGlyphSource</c>, deliberately not read from
    /// GameState during resolution, because casting pushes to the stack and the
    /// cast-context pins are cleared before the stack resolves. Empty for legacy glyphs
    /// and for anything created outside a cast (Runic Cascade's spread copies inherit
    /// nothing). <c>HexTile.ShowGlyph</c> falls back to the plain marker when they are
    /// empty, so this is additive and never load-bearing.
    /// </summary>
    public string SourceCardId = "";

    /// <summary>Half of <see cref="SourceCardId"/> that placed this glyph: <c>"top"</c> or <c>"bottom"</c>.</summary>
    public string SourceHalf = "";

    /// <summary>Display name of the half that placed this glyph (the tier actually cast),
    /// stamped at load time beside <see cref="SourceCardId"/>. Tooltip only.</summary>
    public string SourceName = "";

    /// <summary>Rules text of that half, for the tile tooltip. The sigil is decoration; this
    /// is how the player reads what they cast.</summary>
    public string SourceRulesText = "";

    /// <summary>Authored tooltip text of the half that placed this glyph (CardHalf.GlyphText),
    /// with {tokens} the tooltip fills from this glyph's live values.</summary>
    public string GlyphText = "";

    /// <summary>Authored override for the tooltip's trigger line (CardHalf.GlyphTriggerText).</summary>
    public string GlyphTriggerText = "";

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

    /// <summary>Cascade copies go off at once on any enemy already standing where they land (Chain Cascade).</summary>
    public bool InstantCopies;

    // ── Slice 12a payloads ─────────────────────────────────────────────
    /// <summary>Enter glyphs: the enemy's movement ends here (Snare Glyph, §4).</summary>
    public bool HaltsMovement;

    /// <summary>When &gt; 0, the enemy that sets it off is also Named for 1 turn: "if it
    /// moves, it takes this" (Binding Rune, §4: "and 3 more if it leaves this turn").</summary>
    public int NameOnTrigger;

    /// <summary>Self-stand (Sigil of Focus): while the OWNER stands on it, spells cost this much less.</summary>
    public int CasterCostReduction;
    /// <summary>Self-stand: while the owner stands on it, their spells deal this much more.</summary>
    public int CasterDamage;
    /// <summary>Self-stand: the owner gains this much Weave at each turn start spent on it.</summary>
    public int CasterWeavePerTurn;

    /// <summary>Spell Anchor: the next spell the owner casts while standing here resolves this
    /// many times in total (0 = not an anchor).</summary>
    public int AnchorCasts;
    /// <summary>Spell Anchor: the anchored spell costs this much less.</summary>
    public int AnchorCostReduction;
    /// <summary>Spell Anchor: Weave each time it is used.</summary>
    public int AnchorWeave;

    /// <summary>Ward aura: an ally standing here at turn start heals this much (Hallowed Ground).</summary>
    public int AllyHealPerTurn;

    /// <summary>Fate Weaver (The Great Loom): each time it goes off, a glyph dealing this much
    /// is also prepared on an empty tile near it.</summary>
    public int SpawnGlyphDamage;

    /// <summary>Mirror Ward: attacks or spells it will still turn back. 99 or more reads as unlimited.</summary>
    public int ReflectCharges;
    /// <summary>Mirror Ward: a reflected hit deals this fraction more (0.5 = +50%).</summary>
    public float ReflectBonus;

    /// <summary>Sovereign Pillar: an indestructible ward with an aura over adjacent enemies.</summary>
    public bool Pillar;
    /// <summary>Pillar: adjacent enemies deal this much less.</summary>
    public int AuraDamageReduction;
    /// <summary>Pillar: status each adjacent enemy carries out of the round (weakened, named).</summary>
    public string AuraStatus;
    /// <summary>Pillar: damage each adjacent enemy takes at each round's turn.</summary>
    public int AuraDamage;
    /// <summary>Weave the owner gains each turn while this glyph stands (Throne of Pillars).</summary>
    public int OwnerWeavePerTurn;

    /// <summary>A reusable ally-enter glyph is a WARD: its armor and spell damage last only
    /// while the ally stands on it, and its shield and mana are granted once per ally per
    /// round. A single-use ally glyph is a gift: everything it grants is kept.</summary>
    public bool IsWardAura => Reusable && Trigger == GlyphTrigger.AllyEnter;

    /// <summary>True for a glyph whose point is to hurt or hinder whoever sets it off
    /// (traps, snares, start-of-turn runes, manual damage glyphs); false for one you or
    /// your allies want to stand on (wards, sigils, anchors, mirrors, Fate Weaver).
    /// Drives the circle's shape and palette and the tooltip name colour.</summary>
    public bool AffectsEnemies => Trigger switch
    {
        GlyphTrigger.Enter or GlyphTrigger.StartOfTurn => true,
        GlyphTrigger.Manual => ReflectCharges <= 0,
        _ => false,
    };

    /// <summary>
    /// The damage this glyph deals right now: base, plus any linked-batch bonus, plus the
    /// owner's spell damage, doubled while its owner's Grand Design is active. One formula
    /// for both <see cref="Fire"/> and the tile tooltip, so the preview cannot drift.
    /// </summary>
    public int EffectiveDamage(GameState s, int bonusDamage = 0)
    {
        if (Damage + bonusDamage <= 0)
            return 0;
        int dmg = Damage + bonusDamage + (Owner?.BonusSpellDamage ?? 0);
        if (s?.ActiveEffects != null && OwnerTeam >= 0)
        {
            var arch = GrandDesignPersistentEffect.For(s, OwnerTeam);
            if (arch != null)
                dmg *= Math.Max(1, arch.TriggerCount);
        }
        return dmg;
    }

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
            int dmg = EffectiveDamage(s, bonusDamage);
            if (dmg > 0)
                who.ApplyDamage(dmg);
            if (!string.IsNullOrEmpty(Status))
                who.ApplyStatus(Status, StatusDuration);
            if (dmg > 0 || Status != null)
                s.Log($"[Glyph] {who.Name} triggers glyph: {dmg} dmg" + (Status != null ? $", {Status} {StatusDuration}t" : ""));
            if (HaltsMovement && who.Stats.IsAlive)
            {
                who.MovementInterrupted = true;
                s.Log($"[Glyph] {who.Name}'s movement ends on the glyph.");
            }
            if (NameOnTrigger > 0 && who.Stats.IsAlive)
            {
                Names.Write(who, new NameCondition
                {
                    Triggers = NameTrigger.Move,
                    Damage = NameOnTrigger,
                    TurnsRemaining = 1,
                    OwnerUnit = Owner,
                    OwnerTeam = OwnerTeam,
                    Source = string.IsNullOrEmpty(SourceName) ? "a glyph" : SourceName,
                }, s.Log);
            }
        }
        else if (friendlyToOwner && who != null && IsWardAura && s?.Glyphs != null)
        {
            // Wards are auras: GlyphManager grants and revokes them by position.
            s.Glyphs.GrantWard(who, this, s);
        }
        else if (friendlyToOwner && who != null)
        {
            if (AllyArmor > 0)
                who.Stats.Armor += AllyArmor;
            if (AllyShield > 0)
                who.Stats.Shield += AllyShield;
            // A single-use gift: spell damage is for the ally's NEXT spell, not forever.
            if (AllyDamage > 0)
                who.NextSpellBonusDamage += AllyDamage;
            if (AllyMana > 0)
                who.GainMana(AllyMana);
            who.RefreshHealthBar();
            s.Log($"[Glyph] {who.Name} steps on a glyph: +{AllyArmor} armor, +{AllyShield} shield, +{AllyDamage} damage on its next spell.");
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
