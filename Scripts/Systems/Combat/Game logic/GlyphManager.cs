using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// GlyphManager.cs
//
// Purpose:        Board-state manager for prepared glyphs — the
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
    public GlyphData Prepare(TileData tile, Unit owner, Action<GlyphData> configure)
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
        tile.Glyph = g;

        if (!g.Invisible)
            tile.TileView?.ShowGlyph();
        OnGlyphPlaced?.Invoke(tile);

        if (owner?.Attunement is WeaveAttunement w)
            w.OnGlyphPrepared();
        return g;
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
                Prepare(nbr, owner, ng =>
                {
                    ng.Trigger = trig;
                    ng.Damage = dmg;
                    ng.Status = st;
                    ng.StatusDuration = sd;
                    ng.CascadeSpread = sp; // copies spread one fewer to terminate the chain
                });
                spread++;
            }
            s.Log($"[GlyphManager] Cascade spread to {spread} tile(s).");
        }

        return g.Reusable;
    }

    /// <summary>Removes a glyph from a tile and clears its visual.</summary>
    public void Remove(TileData tile)
    {
        if (tile?.Glyph == null)
            return;
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
        (a.Glyph, b.Glyph) = (b.Glyph, a.Glyph);
        a.TileView?.ClearGlyph();
        b.TileView?.ClearGlyph();
        if (a.Glyph is { Invisible: false })
            a.TileView?.ShowGlyph();
        if (b.Glyph is { Invisible: false })
            b.TileView?.ShowGlyph();
    }

    /// <summary>Re-arm all consumed friendly glyphs (Rearm). Optionally grant +empower damage until they next fire.</summary>
    public int Rearm(int team, int empower = 0)
    {
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
                tile.TileView?.ShowGlyph();
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

    /// <summary>Fired by the cast pipeline when a spell resolves, to trigger SpellCastNear glyphs in range. Optional — wire from Resolver/CombatManager if you want Fate Weaver to work.</summary>
    public void OnSpellCastAt(GameState s, int casterTeam, Vector2I at)
    {
        if (_grid?.Tiles == null)
            return;
        foreach (var tile in _grid.Tiles.Values.ToList())
        {
            var g = tile.Glyph;
            if (g == null || g.Trigger != GlyphTrigger.SpellCastNear)
                continue;
            if (_grid.Distance(tile.Axial, at) > g.Radius)
                continue;
            g.Fire(null, s);
            if (g.Owner?.Attunement is WeaveAttunement w)
                w.OnGlyphTriggered();
            if (!g.Reusable)
                Remove(tile);
        }
    }
}
