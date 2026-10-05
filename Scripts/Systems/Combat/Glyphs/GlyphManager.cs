using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// GlyphManager.cs
//
// Purpose:        Board-state manager for prepared glyphs, the
//                 Enchanter's equivalent of MemorialManager. Owns
//                 glyph lifetime (aging/expiry), start-of-turn
//                 triggers, linked-batch firing, re-arming, and the
//                 friendly-glyph queries card effects use. Feeds the
//                 WeaveAttunement on prepare and trigger.
// Layer:          System (Combat)
// Collaborators:  TileData.Glyph / GlyphData.cs,
//                 HexGridManager (Tiles, Distance),
//                 Unit.cs (PlaceOnTile fires enter/ally glyphs),
//                 WeaveAttunement.cs, GameState.cs (holds .Glyphs),
//                 GlyphEffects.cs (effects call Prepare/TriggerAll/…)
// See:            MemorialManager.cs (the template this mirrors)
// ============================================================

/// <summary>
/// Lifecycle + query manager for prepared glyphs. Held on <c>GameState.Glyphs</c> and
/// ticked once per turn from the same place <c>MemorialManager.Tick()</c> is called.
/// Movement-based triggers (enter/ally-enter) fire from <see cref="Unit.PlaceOnTile"/>;
/// this manager owns timed expiry, start-of-turn triggers, and batch operations.
/// </summary>
public sealed class GlyphManager
{
    private HexGridManager _grid;
    private GameState _state;
    private int _nextLinkId = 1;

    private static readonly Vector2I[] HexDirs =
    {
        new Vector2I(1, 0),  new Vector2I(1, -1), new Vector2I(0, -1),
        new Vector2I(-1, 0), new Vector2I(-1, 1), new Vector2I(0, 1)
    };

    public event Action<TileData> OnGlyphPlaced;
    public event Action<TileData> OnGlyphRemoved;

    public GlyphManager(HexGridManager grid = null) { _grid = grid; }

    /// <summary>Assign or replace the grid. Call wherever MemorialManager's grid is set.</summary>
    public void SetGrid(HexGridManager grid) => _grid = grid;

    /// <summary>Assign the owning GameState so placed glyphs carry it (Unit.PlaceOnTile reads glyph.GameState).</summary>
    public void SetState(GameState s) => _state = s;

    // ── Placement ────────────────────────────────────────────────────
    /// <summary>Prepare a glyph on a tile. Returns the created glyph, or null if the tile is blocked or already glyphed. Feeds the owner's Weave.</summary>
    /// <summary>
    /// The card half whose effects are resolving right now, set by the stack resolver
    /// (RulesManager) around each item. A glyph prepared while it is set takes that half's
    /// live name, rules text and authored glyph text, which is the tier actually cast
    /// (upgrade renames happen after load, so the load-time stamp carries the base name).
    /// It also gives bespoke glyph effects (pillars, wards, anchors) a source at all.
    /// </summary>
    public static CardHalf ResolvingHalf;

    /// <summary>Copies the resolving half's identity onto <paramref name="g"/>. No-op outside
    /// a card resolution.</summary>
    public static void StampFromCast(GlyphData g)
    {
        var h = ResolvingHalf;
        if (h == null || g == null)
            return;
        if (!string.IsNullOrEmpty(h.Name))
            g.SourceName = h.Name;
        g.SourceRulesText = h.RulesText ?? "";
        g.GlyphText = h.GlyphText ?? "";
        g.GlyphTriggerText = h.GlyphTriggerText ?? "";
        if (string.IsNullOrEmpty(g.SourceCardId))
        {
            g.SourceCardId = h.SourceCardId ?? "";
            g.SourceHalf = h.SourceHalf ?? "";
        }
    }

    public GlyphData Prepare(TileData tile, Unit owner, Action<GlyphData> configure, bool stampFromCast = true)
    {
        if (tile == null || tile.IsBlocked || tile.Glyph != null)
            return null;

        var g = new GlyphData
        {
            OwnerId = owner?.Name ?? "Enchanter",
            OwnerTeam = owner?.TeamId ?? 0,
            Owner = owner,
            GameState = _state
        };
        configure?.Invoke(g);
        // Ownerless glyphs (map events) and cascade copies are not the resolving spell's.
        if (stampFromCast && owner != null)
            StampFromCast(g);

        // A standing network ("link all glyphs for the rest of the fight") takes in
        // every glyph its team prepares afterwards.
        if (g.LinkId == 0 && _persistentLinks.TryGetValue(g.OwnerTeam, out var pl))
        {
            g.LinkId = pl.Id;
            g.CumulativeBonus = pl.Bonus;
        }

        tile.Glyph = g;

        if (!g.Invisible)
            tile.TileView?.ShowGlyph(g);
        OnGlyphPlaced?.Invoke(tile);

        if (owner?.Attunement is WeaveAttunement w)
            w.OnGlyphPrepared();

        // The Architecture: the same glyph on every tile within its spread, and Weave
        // banked per glyph the caster prepares. Copies do not spread again.
        if (stampFromCast && owner != null && !_spreading && _state != null)
        {
            var arch = GrandDesignPersistentEffect.For(_state, owner.TeamId);
            if (arch != null)
            {
                if (arch.WeavePerPrepare > 0 && owner.Attunement is WeaveAttunement aw)
                    aw.Add(arch.WeavePerPrepare);
                if (arch.SpreadRadius > 0 && _grid != null)
                {
                    _spreading = true;
                    try
                    {
                        foreach (var t in _grid.Tiles.Values.ToList())
                        {
                            if (t == tile || t.Glyph != null || t.IsBlocked)
                                continue;
                            if (_grid.Distance(tile.Axial, t.Axial) > arch.SpreadRadius)
                                continue;
                            Prepare(t, owner, configure, stampFromCast);
                        }
                    }
                    finally { _spreading = false; }
                }
            }
        }

        // A ward prepared under an ally (Sanctuary around you) covers them at once;
        // nobody should have to step off and back on.
        var occ = tile.Occupant;
        if (g.IsWardAura && occ != null && GodotObject.IsInstanceValid(occ)
            && occ.Stats.IsAlive && occ.TeamId == g.OwnerTeam && _state != null)
            GrantWard(occ, g, _state);
        return g;
    }

    private bool _spreading;

    /// <summary>Sets off the glyph on one tile: on the enemy standing there, or for its
    /// owner's payoffs when nobody is. Handles links, cascades and removal. Returns true
    /// when a glyph was there to fire.</summary>
    public bool TriggerTile(GameState s, TileData tile, int bonus)
    {
        var g = tile?.Glyph;
        if (g == null)
            return false;
        var occ = tile.Occupant;
        var who = occ != null && GodotObject.IsInstanceValid(occ) && occ.Stats.IsAlive ? occ : null;
        g.Fire(who, s, bonus);
        if (!OnGlyphFired(s, tile, who))
            Remove(tile);
        return true;
    }

    /// <summary>The Architecture's discount on a glyph spell for <paramref name="u"/>.</summary>
    public static int ArchitectureDiscount(GameState s, Unit u, CardHalf half)
    {
        if (u == null || half == null || !HalfPreparesGlyph(half))
            return 0;
        return GrandDesignPersistentEffect.For(s, u.TeamId)?.GlyphCostReduction ?? 0;
    }

    /// <summary>True when the half's effect tree prepares a glyph on the board.</summary>
    public static bool HalfPreparesGlyph(CardHalf half) => half != null && PreparesGlyph(half.Effects, 0);

    private static bool PreparesGlyph(IEnumerable<IEffect> effects, int depth)
    {
        if (effects == null || depth > 8)
            return false;
        foreach (var e in effects)
        {
            if (e is PrepareGlyphEffect or ReflectWardEffect or SpellAnchorEffect or EnchantPillarEffect or InscribeEffect)
                return true;
            if (PreparesGlyph(e?.Children, depth + 1))
                return true;
        }
        return false;
    }

    // ── Wards: auras by position (slice 12a) ─────────────────────────
    private sealed class WardGrant
    {
        public TileData Tile;
        public GlyphData Glyph;
        public int Armor, Damage;
    }

    private readonly Dictionary<Unit, WardGrant> _wards = new();
    private readonly Dictionary<(Unit, GlyphData), int> _wardConsumableRound = new();

    /// <summary>An ally entered (or stands on) a ward: its armor and spell damage hold while
    /// it stays; its shield and mana come once per ally per round, so stepping off and on
    /// again cannot farm them. Called from <see cref="GlyphData.Fire"/>.</summary>
    public void GrantWard(Unit unit, GlyphData g, GameState s)
    {
        if (unit == null || g == null || !GodotObject.IsInstanceValid(unit))
            return;
        var tile = unit.CurrentTile;
        if (_wards.TryGetValue(unit, out var cur) && cur.Glyph == g && cur.Tile == tile)
            return;   // already covered by this ward
        RevokeWard(unit);

        unit.Stats.Armor += g.AllyArmor;
        unit.BonusSpellDamage += g.AllyDamage;
        _wards[unit] = new WardGrant { Tile = tile, Glyph = g, Armor = g.AllyArmor, Damage = g.AllyDamage };

        var key = (unit, g);
        int shield = 0, mana = 0;
        if (!_wardConsumableRound.TryGetValue(key, out int r) || r != Names.Round)
        {
            _wardConsumableRound[key] = Names.Round;
            shield = g.AllyShield;
            mana = g.AllyMana;
            if (shield > 0)
                unit.Stats.Shield += shield;
            if (mana > 0)
                unit.GainMana(mana);
        }
        unit.RefreshHealthBar();
        s?.Log($"[Ward] {unit.Name} stands on {(string.IsNullOrEmpty(g.SourceName) ? "a ward" : g.SourceName)}: "
             + $"+{g.AllyArmor} armor and +{g.AllyDamage} spell damage while there"
             + (shield > 0 || mana > 0 ? $", +{shield} shield, +{mana} mana." : "."));
    }

    /// <summary>Takes back what a ward lent: the armor (as far as it is still there) and the
    /// spell damage. Shield and mana already granted are kept.</summary>
    public void RevokeWard(Unit unit)
    {
        if (unit == null || !_wards.TryGetValue(unit, out var w))
            return;
        _wards.Remove(unit);
        if (!GodotObject.IsInstanceValid(unit))
            return;
        unit.Stats.Armor = Math.Max(0, unit.Stats.Armor - w.Armor);
        unit.BonusSpellDamage -= w.Damage;
        unit.RefreshHealthBar();
    }

    /// <summary>Every tile entry: a unit that walked off its ward loses the aura.</summary>
    public void OnUnitEntered(Unit unit, TileData tile)
    {
        if (unit != null && _wards.TryGetValue(unit, out var w) && w.Tile != tile)
            RevokeWard(unit);
    }

    private void RevokeWardsOn(TileData tile)
    {
        foreach (var u in _wards.Where(kv => kv.Value.Tile == tile).Select(kv => kv.Key).ToList())
            RevokeWard(u);
    }

    // ── Standing glyphs: Sigil of Focus, Spell Anchor (slice 12a) ────
    /// <summary>The self-stand glyph <paramref name="u"/> owns and stands on, or null.</summary>
    public static GlyphData StandingGlyph(Unit u)
    {
        if (u == null || !GodotObject.IsInstanceValid(u))
            return null;
        var g = u.CurrentTile?.Glyph;
        return g != null && !g.Consumed && g.Trigger == GlyphTrigger.SelfStand && g.Owner == u ? g : null;
    }

    /// <summary>How much cheaper <paramref name="u"/>'s spells are right now from the glyph
    /// under it. Read by ManaCost.EffectiveAmount, so the pips, affordability and payment agree.</summary>
    public static int StandingCostReduction(Unit u)
    {
        var g = StandingGlyph(u);
        return g == null ? 0 : g.CasterCostReduction + (g.AnchorCasts > 0 ? g.AnchorCostReduction : 0);
    }

    /// <summary>Spell damage the glyph under <paramref name="u"/> adds to its casts.</summary>
    public static int StandingDamageBonus(Unit u) => StandingGlyph(u)?.CasterDamage ?? 0;

    /// <summary>The Spell Anchor under <paramref name="u"/>, or null.</summary>
    public static GlyphData StandingAnchor(Unit u)
    {
        var g = StandingGlyph(u);
        return g != null && g.AnchorCasts > 1 ? g : null;
    }

    /// <summary>A spell was anchored: pay out its Weave and, unless the anchor lasts, use it up.</summary>
    public void SpendAnchor(Unit u, GlyphData anchor, GameState s)
    {
        if (u == null || anchor == null)
            return;
        if (anchor.AnchorWeave > 0 && u.Attunement is WeaveAttunement w)
            w.Add(anchor.AnchorWeave);
        if (!anchor.Reusable && u.CurrentTile?.Glyph == anchor)
            Remove(u.CurrentTile);
        s?.Log($"[SpellAnchor] the anchored spell resolves {anchor.AnchorCasts} times.");
    }

    // ── Persistent networks (slice 12a) ──────────────────────────────
    private readonly Dictionary<int, (int Id, int Bonus)> _persistentLinks = new();

    /// <summary>Links every friendly glyph and keeps the network open: glyphs the team
    /// prepares later join it ("for the rest of the fight").</summary>
    public int LinkPersistent(int team, int cumulativeBonus)
    {
        int id = _persistentLinks.TryGetValue(team, out var pl) ? pl.Id : _nextLinkId++;
        _persistentLinks[team] = (id, cumulativeBonus);
        int n = 0;
        foreach (var tile in GetAllFriendly(team))
        {
            tile.Glyph.LinkId = id;
            tile.Glyph.CumulativeBonus = cumulativeBonus;
            n++;
        }
        return n;
    }

    // ── Per-turn tick (call alongside MemorialManager.Tick) ──────────
    /// <summary>
    /// Ages timed glyphs, fires StartOfTurn glyphs on any enemy standing on them, and
    /// removes expired/consumed glyphs. Pass the GameState so triggers can resolve.
    /// </summary>
    public void Tick(GameState s)
    {
        if (_grid?.Tiles == null)
            return;

        foreach (var tile in _grid.Tiles.Values.ToList())
        {
            var g = tile.Glyph;
            if (g == null)
                continue;

            TickAuras(tile, g, s);

            // Start-of-turn enemy triggers
            if (g.Trigger == GlyphTrigger.StartOfTurn && tile.Occupant != null && tile.Occupant.TeamId != g.OwnerTeam)
            {
                g.Fire(tile.Occupant, s);
                if (g.Owner?.Attunement is WeaveAttunement w)
                    w.OnGlyphTriggered();
                if (!g.Reusable)
                { Remove(tile); continue; }
            }

            // Age timed glyphs
            if (g.DurationTurns > 0)
            {
                g.DurationTurns--;
                if (g.DurationTurns <= 0)
                { Remove(tile); continue; }
            }

            if (g.Consumed && !g.Reusable)
                Remove(tile);
        }
    }

    /// <summary>Once per round, per glyph: ward heals, the self-stand Weave, pillar auras
    /// on adjacent enemies, and per-turn Weave for the owner.</summary>
    private void TickAuras(TileData tile, GlyphData g, GameState s)
    {
        var occ = tile.Occupant;
        bool occAlive = occ != null && GodotObject.IsInstanceValid(occ) && occ.Stats.IsAlive;

        if (g.AllyHealPerTurn > 0 && occAlive && occ.TeamId == g.OwnerTeam)
        {
            int before = occ.Stats.Health;
            occ.Stats.Health = Math.Min(occ.Stats.MaxHealth, occ.Stats.Health + g.AllyHealPerTurn);
            occ.RefreshHealthBar();
            if (occ.Stats.Health > before)
                s.Log($"[Ward] {occ.Name} heals {occ.Stats.Health - before} on the ward.");
        }

        if (g.CasterWeavePerTurn > 0 && occAlive && occ == g.Owner && occ.Attunement is WeaveAttunement sw)
        {
            sw.Add(g.CasterWeavePerTurn);
            s.Log($"[Sigil] {occ.Name} holds the sigil: +{g.CasterWeavePerTurn} Weave.");
        }

        if (g.OwnerWeavePerTurn > 0 && g.Owner != null && GodotObject.IsInstanceValid(g.Owner)
            && g.Owner.Stats.IsAlive && g.Owner.Attunement is WeaveAttunement ow)
            ow.Add(g.OwnerWeavePerTurn);

        if (g.Pillar && (g.AuraDamage > 0 || !string.IsNullOrEmpty(g.AuraStatus)) && _grid != null)
        {
            foreach (var n in _grid.GetNeighbors(tile.Axial))
            {
                var e = _grid.GetTile(n)?.Occupant;
                if (e == null || !GodotObject.IsInstanceValid(e) || !e.Stats.IsAlive || e.TeamId == g.OwnerTeam)
                    continue;
                if (g.AuraDamage > 0)
                    e.ApplyDamage(g.AuraDamage);
                if (!string.IsNullOrEmpty(g.AuraStatus) && e.Stats.IsAlive)
                    e.ApplyStatus(g.AuraStatus, 1);
                s.Log($"[Pillar] {e.Name} stands beside a Sovereign Pillar"
                      + (g.AuraDamage > 0 ? $": {g.AuraDamage} damage" : "")
                      + (!string.IsNullOrEmpty(g.AuraStatus) ? $", {g.AuraStatus}." : "."));
            }
        }
    }

    /// <summary>Damage an attack from <paramref name="attacker"/> loses for standing beside
    /// an opposing Sovereign Pillar (the largest reduction applies, not the sum).</summary>
    public int PillarDamageReduction(Unit attacker)
    {
        if (attacker?.CurrentTile == null || _grid == null)
            return 0;
        int best = 0;
        foreach (var n in _grid.GetNeighbors(attacker.CurrentTile.Axial))
        {
            var g = _grid.GetTile(n)?.Glyph;
            if (g != null && g.Pillar && g.OwnerTeam != attacker.TeamId)
                best = Math.Max(best, g.AuraDamageReduction);
        }
        return best;
    }

    /// <summary>
    /// Call from <see cref="Unit.PlaceOnTile"/> immediately after a glyph fires (and before
    /// removing it). Handles linked-batch firing and Runic Cascade self-spread. Returns true
    /// if the glyph should be kept on the board (reusable), false if the caller should remove it.
    /// </summary>
    public bool OnGlyphFired(GameState s, TileData tile, Unit cause)
    {
        var g = tile?.Glyph;
        if (g == null)
            return false;

        if (g.Owner?.Attunement is WeaveAttunement w)
            w.OnGlyphTriggered();

        // Linked batch
        if (g.LinkId != 0)
            FireLinked(s, g.LinkId, cause);

        // Cascade self-spread onto adjacent empty tiles
        if (g.CascadeSpread > 0 && _grid != null)
        {
            int spread = 0;
            foreach (var dir in HexDirs)
            {
                if (spread >= g.CascadeSpread)
                    break;
                var nbr = _grid.GetTile(tile.Axial + dir);
                if (nbr == null || nbr.IsBlocked || nbr.Glyph != null)
                    continue;
                int dmg = g.Damage, sp = g.CascadeSpread - 1;
                string st = g.Status;
                int sd = g.StatusDuration;
                var owner = g.Owner;
                var trig = g.Trigger;
                string srcName = g.SourceName, srcText = g.SourceRulesText;
                string srcGlyph = g.GlyphText, srcTrig = g.GlyphTriggerText;
                string srcId = g.SourceCardId, srcHalf = g.SourceHalf;
                // stampFromCast false: a cascade can go off during some OTHER spell's
                // resolution (a Compel walking an enemy onto it), and the copy is still
                // the cascade's, not that spell's.
                Prepare(nbr, owner, ng =>
                {
                    ng.SourceName = string.IsNullOrEmpty(srcName) ? "" : srcName + " (spread)";
                    ng.SourceRulesText = srcText;
                    ng.GlyphText = srcGlyph;
                    ng.GlyphTriggerText = srcTrig;
                    ng.SourceCardId = srcId;
                    ng.SourceHalf = srcHalf;
                    ng.Trigger = trig;
                    ng.Damage = dmg;
                    ng.Status = st;
                    ng.StatusDuration = sd;
                    ng.CascadeSpread = sp; // copies spread one fewer to terminate the chain
                    ng.InstantCopies = g.InstantCopies;
                }, stampFromCast: false);
                spread++;
                // Chain Cascade: a copy that lands under an enemy goes off at once. The
                // chain ends because every generation spreads one fewer.
                var copy = nbr.Glyph;
                var under = nbr.Occupant;
                if (g.InstantCopies && copy != null && under != null && GodotObject.IsInstanceValid(under)
                    && under.Stats.IsAlive && under.TeamId != copy.OwnerTeam)
                {
                    copy.Fire(under, s);
                    if (!OnGlyphFired(s, nbr, under))
                        Remove(nbr);
                }
            }
            s.Log($"[GlyphManager] Cascade spread to {spread} tile(s).");
        }

        // Tripwire Sentence: the rest of the pair is spent with it.
        if (g.PairId != 0 && _grid?.Tiles != null)
            foreach (var other in _grid.Tiles.Values.ToList())
                if (other != tile && other.Glyph != null && other.Glyph.PairId == g.PairId)
                    Remove(other);

        // Seven-Layer Ward: peel a layer; it stays until the last one is gone.
        if (g.Layers > 0)
        {
            g.Layers--;
            s.Log(g.Layers > 0
                ? $"[Glyph] A layer peels away: {g.Layers} left."
                : "[Glyph] The last layer peels away.");
            return g.Layers > 0;
        }

        return g.Reusable;
    }

    /// <summary>A fresh id for glyphs prepared together (Tripwire Sentence pairs).</summary>
    public int NewPairId() => _nextLinkId++;

    /// <summary>Removes a glyph from a tile and clears its visual.</summary>
    public void Remove(TileData tile)
    {
        if (tile?.Glyph == null)
            return;
        RevokeWardsOn(tile);
        tile.Glyph = null;
        tile.TileView?.ClearGlyph();
        OnGlyphRemoved?.Invoke(tile);
    }

    // ── Batch operations ─────────────────────────────────────────────
    /// <summary>Fire every friendly glyph at once (Glyph Network). Each adds <paramref name="bonusPerOther"/> damage per other glyph fired. Glyphs with no enemy present still "fire" their payoff/owner portion. Returns the count fired.</summary>
    public int TriggerAll(GameState s, int team, int bonusPerOther = 0, bool consume = true)
    {
        var glyphTiles = GetAllFriendly(team).ToList();
        int n = glyphTiles.Count, fired = 0;
        for (int i = 0; i < glyphTiles.Count; i++)
        {
            var tile = glyphTiles[i];
            var g = tile.Glyph;
            if (g == null)
                continue;
            int bonus = bonusPerOther * (n - 1);
            var target = (tile.Occupant != null && tile.Occupant.TeamId != team) ? tile.Occupant : null;
            g.Fire(target ?? tile.Occupant, s, bonus);
            if (g.Owner?.Attunement is WeaveAttunement w)
                w.OnGlyphTriggered();
            fired++;
            if (consume && !g.Reusable)
                Remove(tile);
        }
        s.Log($"[GlyphManager] TriggerAll fired {fired} glyph(s) (bonus/other {bonusPerOther}).");
        return fired;
    }

    /// <summary>Link up to <paramref name="count"/> friendly glyphs into a shared batch id so triggering one triggers the group. Returns the link id.</summary>
    public int Link(int team, int count, int cumulativeBonus = 0)
    {
        int id = _nextLinkId++;
        int linked = 0;
        foreach (var tile in GetAllFriendly(team))
        {
            if (linked >= count)
                break;
            tile.Glyph.LinkId = id;
            tile.Glyph.CumulativeBonus = cumulativeBonus;
            linked++;
        }
        return id;
    }

    /// <summary>Fire every glyph sharing <paramref name="linkId"/> (called when any one in the group triggers).</summary>
    public void FireLinked(GameState s, int linkId, Unit cause)
    {
        if (linkId == 0 || _grid?.Tiles == null)
            return;
        int idx = 0;
        foreach (var tile in _grid.Tiles.Values.ToList())
        {
            var g = tile.Glyph;
            if (g == null || g.LinkId != linkId)
                continue;
            g.Fire(tile.Occupant ?? cause, s, g.CumulativeBonus * idx);
            idx++;
            if (!g.Reusable)
                Remove(tile);
        }
    }

    /// <summary>Swap the contents of two glyph tiles.</summary>
    public void Swap(TileData a, TileData b)
    {
        if (a == null || b == null)
            return;
        RevokeWardsOn(a);
        RevokeWardsOn(b);
        (a.Glyph, b.Glyph) = (b.Glyph, a.Glyph);
        a.TileView?.ClearGlyph();
        b.TileView?.ClearGlyph();
        if (a.Glyph is { Invisible: false })
            a.TileView?.ShowGlyph(a.Glyph);
        if (b.Glyph is { Invisible: false })
            b.TileView?.ShowGlyph(b.Glyph);
    }

    /// <summary>Re-arm all consumed friendly glyphs (Rearm). Optionally grant +empower damage until they next fire.</summary>
    public int Rearm(int team, int empower = 0)
    {
        // Null-grid guard, matching every other board-wide method here. This
        // was the ONE unguarded _grid use. With the grid never assigned it
        // threw mid-enemy-turn and softlocked combat (2026-07-29 playtest).
        if (_grid?.Tiles == null)
            return 0;
        int n = 0;
        foreach (var tile in _grid.Tiles.Values)
        {
            var g = tile.Glyph;
            if (g == null || g.OwnerTeam != team)
                continue;
            if (g.Consumed)
            { g.Consumed = false; n++; }
            if (empower > 0)
                g.Damage += empower;
            if (!g.Invisible)
                tile.TileView?.ShowGlyph(g);
        }
        return n;
    }

    // ── Queries ──────────────────────────────────────────────────────
    public IEnumerable<TileData> GetAllFriendly(int team)
        => _grid?.Tiles?.Values.Where(t => t.Glyph != null && t.Glyph.OwnerTeam == team) ?? Enumerable.Empty<TileData>();

    public int CountFriendly(int team)
        => _grid?.Tiles?.Values.Count(t => t.Glyph != null && t.Glyph.OwnerTeam == team) ?? 0;

    public TileData NearestFriendly(int team, Vector2I from)
    {
        TileData best = null;
        int bestD = int.MaxValue;
        foreach (var t in GetAllFriendly(team))
        {
            int d = _grid.Distance(from, t.Axial);
            if (d < bestD)
            { bestD = d; best = t; }
        }
        return best;
    }

    /// <summary>A spell was cast (any side). Every SpellCastNear glyph within its radius of
    /// ANY of <paramref name="points"/> (the caster's tile and where it landed) goes off once:
    /// owner payoffs, plus a spawned glyph for The Great Loom. Called by the stack resolver for
    /// card casts and by the enemy executor for spell intents.</summary>
    public void OnSpellCastAt(GameState s, int casterTeam, IEnumerable<Vector2I> points)
    {
        if (_grid?.Tiles == null || points == null)
            return;
        var pts = points.ToList();
        if (pts.Count == 0)
            return;
        foreach (var tile in _grid.Tiles.Values.ToList())
        {
            var g = tile.Glyph;
            if (g == null || g.Trigger != GlyphTrigger.SpellCastNear)
                continue;
            if (!pts.Any(p => _grid.Distance(tile.Axial, p) <= g.Radius))
                continue;
            g.Fire(null, s);
            s?.Log($"[Glyph] {(string.IsNullOrEmpty(g.SourceName) ? "A glyph" : g.SourceName)} answers a spell cast nearby.");
            if (g.Owner?.Attunement is WeaveAttunement w)
                w.OnGlyphTriggered();
            if (g.SpawnGlyphDamage > 0)
                SpawnThread(tile, g, s);
            if (!g.Reusable)
                Remove(tile);
        }
    }

    public void OnSpellCastAt(GameState s, int casterTeam, Vector2I at)
        => OnSpellCastAt(s, casterTeam, new[] { at });

    // The Great Loom: a small enter glyph on the nearest empty tile within 2.
    private void SpawnThread(TileData from, GlyphData parent, GameState s)
    {
        if (_grid == null)
            return;
        var spot = _grid.Tiles.Values
            .Where(t => t != from && t.Glyph == null && !t.IsBlocked && t.Occupant == null && t.IsWalkable
                        && _grid.Distance(from.Axial, t.Axial) <= 2)
            .OrderBy(t => _grid.Distance(from.Axial, t.Axial))
            .ThenBy(t => t.Axial.X).ThenBy(t => t.Axial.Y)
            .FirstOrDefault();
        if (spot == null)
            return;
        int dmg = parent.SpawnGlyphDamage;
        Prepare(spot, parent.Owner, ng =>
        {
            ng.Trigger = GlyphTrigger.Enter;
            ng.Damage = dmg;
            ng.SourceName = "Thread of Fate";
            ng.GlyphText = "It takes {damage} damage.";
            ng.SourceCardId = parent.SourceCardId;
            ng.SourceHalf = parent.SourceHalf;
        }, stampFromCast: false);
        s?.Log($"[Glyph] A thread of fate is prepared at ({spot.Axial.X}, {spot.Axial.Y}).");
    }
}
