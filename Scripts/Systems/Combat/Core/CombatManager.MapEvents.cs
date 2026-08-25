using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.MapEvents.cs  (partial of CombatManager)
//
// Purpose:        Battlefield E4, scheduled map events. Hazards that
//                 spread, close, or scar the board over the course of a
//                 fight (advance_hazard_ring, spread_element, imbue_patch).
// Ordering:       Resolved at the round boundary BEFORE objectives and
//                 wave spawns (called from AdvanceRound, ahead of
//                 EvaluateObjectiveRoundBoundary), so waves arrive onto
//                 the updated terrain and zones read against reality.
// Telegraph:      Non-destructive kinds this pass, announced one
//                 telegraph-window ahead via banner/log. A visual
//                 tile-highlight telegraph and the destructive
//                 collapse_tiles kind are follow-ups.
// Collaborators:  HexGridManager (MapEvent* ops + ActiveMapEvents),
//                 MapRecipe/MapEventDef (schema), CombatManager (grid,
//                 roundNumber, combatUI).
// ============================================================
public partial class CombatManager : Node3D
{
    /// <summary>Resolve every scheduled map event for the round just entered. Executes
    /// events firing this round; announces events whose fire round is exactly one
    /// telegraph-window away.</summary>
    private void EvaluateMapEvents()
    {
        if (grid == null)
            return;
        ClearTelegraph();   // clears prior visual highlight + the hazard-cost set
        var events = grid.ActiveMapEvents;
        if (events == null || events.Count == 0)
            return;

        foreach (var ev in events)
        {
            if (ev == null || string.IsNullOrEmpty(ev.Kind))
                continue;
            if (FiresOn(ev, roundNumber))
                ExecuteMapEvent(ev);
            else if (ev.Telegraph > 0 && FiresOn(ev, roundNumber + ev.Telegraph))
            {
                AnnounceMapEvent(ev, roundNumber + ev.Telegraph);
                MarkTelegraph(ev, roundNumber + ev.Telegraph);
            }
        }
    }

    /// <summary>True when <paramref name="ev"/> fires on the given round: its start
    /// round, then every <c>repeat_every</c> after (0 = one-shot).</summary>
    private static bool FiresOn(MapEventDef ev, int round)
    {
        if (round < ev.Round)
            return false;
        if (round == ev.Round)
            return true;
        return ev.RepeatEvery > 0 && (round - ev.Round) % ev.RepeatEvery == 0;
    }

    private Vector2I MapEventCenter(MapEventDef ev)
    {
        string at = ev.GetStr("at", "midpoint");
        return at == "center" ? grid.RecipeCenter : grid.RecipeMidpoint;
    }

    /// <summary>advance_hazard_ring closes inward: each firing tightens the ring by
    /// <c>steps</c> from its <c>radius</c>, floored at 1.</summary>
    private int AdvanceRingRadius(MapEventDef ev)
    {
        int start = ev.GetInt("radius", 4);
        int steps = Math.Max(1, ev.GetInt("steps", 1));
        int k = ev.RepeatEvery > 0 ? (roundNumber - ev.Round) / ev.RepeatEvery : 0;
        return Math.Max(1, start - k * steps);
    }

    private void ExecuteMapEvent(MapEventDef ev)
    {
        var el = MapRecipe.ParseElement(ev.GetStr("element", "fire"));
        int radius = ev.GetInt("radius", 1);
        int count;
        string what;
        switch (ev.Kind)
        {
            case "imbue_patch":
                count = grid.MapEventImbuePatch(MapEventCenter(ev), radius, el);
                what = $"{el} scars {count} tile(s)";
                break;
            case "spread_element":
                count = grid.MapEventSpreadElement(el, ev.GetInt("per_patch", 1));
                what = $"{el} spreads across {count} more tile(s)";
                break;
            case "advance_hazard_ring":
                int rr = AdvanceRingRadius(ev);
                count = grid.MapEventImbueRing(MapEventCenter(ev), rr, el);
                what = $"the {el} ring closes to {rr} ({count} tile(s))";
                break;
            case "spawn_object":
                {
                    string okind = ev.GetStr("kind", "boulder");
                    var ot = grid.GetTile(MapEventCenter(ev));
                    count = SpawnMapObject(okind, ot) != null ? 1 : 0;
                    what = count > 0 ? $"a {okind} drops onto the field" : $"a {okind} finds no room";
                }
                break;
            case "collapse_tiles":
                count = CollapseTiles(MapEventCenter(ev), radius, ev.GetStr("into", "rubble"));
                what = $"the ground gives way ({count} tile(s))";
                break;
            case "raise_tiles":
                count = ChangeTileHeights(MapEventCenter(ev), radius, System.Math.Max(1, ev.GetInt("delta", 1)));
                what = $"the ground rises ({count} tile(s))";
                break;
            case "lower_tiles":
                count = ChangeTileHeights(MapEventCenter(ev), radius, -System.Math.Max(1, ev.GetInt("delta", 1)));
                what = $"the ground sinks ({count} tile(s))";
                break;
            case "weather_tick":
                {
                    string w = ev.GetStr("weather", "storm");
                    if (w == "storm") { count = StormStrike(); what = $"lightning strikes ({count})"; }
                    else if (w == "rain") { count = RainTick(ev.GetInt("per_patch", 1)); what = $"the water rises across {count} tile(s)"; }
                    else if (w == "snow") { count = SnowTick(ev.GetInt("per_patch", 2)); what = $"ice creeps across {count} tile(s)"; }
                    else { count = 0; what = $"unknown weather '{w}'"; }
                }
                break;
            default:
                GD.PushWarning($"[MapEvent] unknown kind '{ev.Kind}'.");
                return;
        }
        string msg = ev.GetStr("announce", "");
        if (string.IsNullOrEmpty(msg))
            msg = what;
        GD.Print($"[MapEvent] {msg}");
        combatUI?.AppendActionLog($"⚠ {msg}");
    }

    // ── E4 destructive events + telegraph ─────────────────────────────────

    /// <summary>Tiles a destructive event will hit, used for both the telegraph mark and
    /// (indirectly) resolution. Empty for non-destructive kinds (they do not pre-mark).</summary>
    private List<TileData> MapEventAffectedTiles(MapEventDef ev, int fireRound)
    {
        var list = new List<TileData>();
        if (ev.Kind == "collapse_tiles" || ev.Kind == "raise_tiles" || ev.Kind == "lower_tiles")
        {
            var center = MapEventCenter(ev);
            int radius = ev.GetInt("radius", 1);
            foreach (var t in grid.Tiles.Values)
                if (t != null && grid.Distance(center, t.Axial) <= radius)
                    list.Add(t);
        }
        else if (ev.Kind == "weather_tick" && ev.GetStr("weather", "storm") == "storm")
        {
            var t = StormTargetForRound(fireRound);
            if (t != null)
                list.Add(t);
        }
        return list;
    }

    /// <summary>Mark a coming destructive event's tiles: fill grid.TelegraphedTiles (enemy
    /// pathing treats them as hazard-cost) AND light the telegraph highlight so the player
    /// sees the doomed tiles a round ahead.</summary>
    private void MarkTelegraph(MapEventDef ev, int fireRound)
    {
        foreach (var t in MapEventAffectedTiles(ev, fireRound))
        {
            grid.TelegraphedTiles.Add(t.Axial);
            grid.GetTileView(t.Axial)?.SetTelegraphHighlight(true);
        }
    }

    /// <summary>Clear last round's telegraph, both the visual highlight and the
    /// hazard-cost set, before rebuilding it this boundary.</summary>
    private void ClearTelegraph()
    {
        foreach (var c in grid.TelegraphedTiles)
            grid.GetTileView(c)?.SetTelegraphHighlight(false);
        grid.TelegraphedTiles.Clear();
    }

    /// <summary>collapse_tiles: convert every tile within radius, evicting occupants
    /// (a forced 1-tile shove, with slides/fire/glyphs applying, then 3 damage). Returns the
    /// number of tiles converted.</summary>
    private int CollapseTiles(Vector2I center, int radius, string into)
    {
        var affected = new List<TileData>();
        foreach (var t in grid.Tiles.Values)
            if (t != null && grid.Distance(center, t.Axial) <= radius)
                affected.Add(t);
        foreach (var t in affected)
        {
            if (t.Occupant != null && t.Occupant.Stats.IsAlive)
                EvictFromCollapse(t.Occupant);
            ConvertTile(t, into);
        }
        return affected.Count;
    }

    private void ConvertTile(TileData t, string into)
    {
        switch (into)
        {
            case "water":
                t.TerrainType = TileTerrainType.Water;
                t.IsWalkable = false;
                t.BlocksLineOfSight = false;
                grid.RebuildTileAndNeighbors(t.Axial);   // re-bake bed as water; scar tint (below) reads the surface
                break;
            case "chasm":
                t.IsBlocked = true;
                t.BlocksLineOfSight = false;
                break;
            case "rubble":
            default:
                t.MoveCost = 2;
                t.BaseMoveCost = 2;
                t.ObstacleKind = "rubble";
                break;
        }
        grid.GetTileView(t.Axial)?.SetTerrainScar(into);
    }

    /// <summary>raise_tiles / lower_tiles: shift the height of every tile within radius by
    /// delta and re-seat its mesh. Cliffs and LoS recompute live from Height, so no extra
    /// pass is needed.</summary>
    private int ChangeTileHeights(Vector2I center, int radius, int delta)
    {
        // Change every height FIRST, then re-bake. A tile's blended mesh (cliffs, skirts,
        // corner averages) depends on its neighbours, so raising one tile without re-baking
        // the ring leaves a seam. RebuildTileAndNeighbors re-seats height + re-meshes the
        // tile and its six neighbours against the FINAL heights, the same stitch the
        // generation pass does, so edges close cleanly.
        var changed = new List<Vector2I>();
        foreach (var t in grid.Tiles.Values)
        {
            if (t == null || grid.Distance(center, t.Axial) > radius)
                continue;
            int nh = System.Math.Clamp(t.Height + delta, -4, 6);
            if (nh == t.Height)
                continue;
            t.Height = nh;
            changed.Add(t.Axial);
        }
        foreach (var c in changed)
            grid.RebuildTileAndNeighbors(c);
        return changed.Count;
    }

    /// <summary>Shove a unit one tile to the nearest tile it can legally enter, then deal
    /// 3. If boxed in, it just takes the 3.</summary>
    private void EvictFromCollapse(Unit u)
    {
        var from = u.CurrentTile;
        if (from != null)
        {
            TileData best = null;
            foreach (var nb in grid.GetNeighbors(from.Axial))
            {
                var t = grid.GetTile(nb);
                if (t != null && t.CanEnter(u)) { best = t; break; }
            }
            if (best != null)
                u.PlaceOnTile(best, MovementKind.Forced, new MoveContext(grid));
        }
        u.ApplyDamage(3);
    }

    /// <summary>weather storm: 3 damage to the deterministically-chosen open tile's
    /// occupant + a lightning imbue. Same tile as the telegraph (both derive from the
    /// fire round), so the warning is honest.</summary>
    private int StormStrike()
    {
        var t = StormTargetForRound(roundNumber);
        if (t == null)
            return 0;
        if (t.Occupant != null && t.Occupant.Stats.IsAlive)
            t.Occupant.ApplyDamage(3);
        TileEntryReactions.ImbueTile(t, TileElementType.Lightning);
        return 1;
    }

    /// <summary>Deterministic per-round open-tile pick so the storm telegraph (round N-1)
    /// and the strike (round N) hit the same tile.</summary>
    private TileData StormTargetForRound(int round)
    {
        var open = new List<TileData>();
        foreach (var t in grid.Tiles.Values)
            if (t != null && t.IsWalkable && !t.IsBlocked)
                open.Add(t);
        if (open.Count == 0)
            return null;
        open.Sort((a, b) => a.Axial.X != b.Axial.X ? a.Axial.X.CompareTo(b.Axial.X) : a.Axial.Y.CompareTo(b.Axial.Y));
        uint h = (uint)round * 2654435761u;
        return open[(int)(h % (uint)open.Count)];
    }

    /// <summary>weather rain: each water tile floods up to perPatch adjacent unoccupied
    /// land tiles (deterministic, lowest-axial first). Occupied tiles are spared so it
    /// never drowns a unit without warning. The map just closes over time.</summary>
    private int RainTick(int perPatch)
    {
        var water = new List<TileData>();
        foreach (var t in grid.Tiles.Values)
            if (t != null && t.TerrainType == TileTerrainType.Water)
                water.Add(t);
        water.Sort((a, b) => a.Axial.X != b.Axial.X ? a.Axial.X.CompareTo(b.Axial.X) : a.Axial.Y.CompareTo(b.Axial.Y));
        var targets = new List<TileData>();
        foreach (var w in water)
        {
            int added = 0;
            foreach (var nb in grid.GetNeighbors(w.Axial))
            {
                if (added >= perPatch)
                    break;
                var n = grid.GetTile(nb);
                if (n != null && n.IsWalkable && !n.IsBlocked && !n.IsOccupied
                    && n.TerrainType != TileTerrainType.Water && !targets.Contains(n))
                {
                    targets.Add(n);
                    added++;
                }
            }
        }
        foreach (var t in targets)
            ConvertTile(t, "water");
        return targets.Count;
    }

    /// <summary>weather snow: imbue Frost on the perPatch open tiles nearest the centre
    /// that aren't already frost, accumulating outward each tick (frost = the tile
    /// system's slippery/ice move-cost).</summary>
    private int SnowTick(int perPatch)
    {
        var center = grid.RecipeMidpoint;
        var cands = new List<TileData>();
        foreach (var t in grid.Tiles.Values)
            if (t != null && t.IsWalkable && !t.IsBlocked && t.ElementType != TileElementType.Frost)
                cands.Add(t);
        cands.Sort((a, b) => grid.Distance(center, a.Axial).CompareTo(grid.Distance(center, b.Axial)));
        int n = Math.Min(perPatch, cands.Count);
        for (int i = 0; i < n; i++)
            TileEntryReactions.ImbueTile(cands[i], TileElementType.Frost);
        return n;
    }

    private void AnnounceMapEvent(MapEventDef ev, int fireRound)
    {
        string msg = ev.GetStr("announce", "");
        if (string.IsNullOrEmpty(msg))
            msg = $"a {ev.Kind} approaches";
        GD.Print($"[MapEvent] telegraph: {msg} (round {fireRound}).");
        combatUI?.AppendActionLog($"⚠ {msg}, round {fireRound}.");
    }
}
