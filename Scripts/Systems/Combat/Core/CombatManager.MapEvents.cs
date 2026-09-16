using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatManager.MapEvents.cs  (partial of CombatManager)
//
// Purpose:        Battlefield E4, scheduled map events. Hazards that
//                 spread, close, or scar the board over the course of a
//                 fight (ring_closes, hazard_spreads, hazard_patch).
// Ordering:       Resolved at the round boundary BEFORE objectives and
//                 wave spawns (called from AdvanceRound, ahead of
//                 EvaluateObjectiveRoundBoundary), so waves arrive onto
//                 the updated terrain and zones read against reality.
// Telegraph:      Non-destructive kinds this pass, announced one
//                 telegraph-window ahead via banner/log. A visual
//                 tile-highlight telegraph and the destructive
//                 ground_collapses kind are follow-ups.
// Collaborators:  HexGridManager (MapEvent* ops + ActiveMapEvents),
//                 MapRecipe/MapEventDef (schema), CombatManager (grid,
//                 roundNumber, combatUI).
// ============================================================
public partial class CombatManager : Node3D
{
    /// <summary>Resolve every scheduled map event for the round just entered. Executes
    /// events firing this round; announces events whose fire round is exactly one
    /// telegraph-window away.</summary>
    // ── map_pressure_v2 runtime ─────────────────────────────────────────────
    private bool _mapEventsInitialised;
    private int _fogUntilRound;
    private int _deathsThisCombat;
    private readonly HashSet<string> _destroyedObjectKinds = new();
    private readonly HashSet<string> _firedEventIds = new();

    private void EvaluateMapEvents()
    {
        if (grid == null)
            return;
        ClearTelegraph();   // clears prior visual highlight + the hazard-cost set
        var events = grid.ActiveMapEvents;
        if (events == null || events.Count == 0)
            return;

        // Recipe defs are shared across fights (MapRecipeRegistry): scrub the
        // per-combat state on the first evaluation of this combat.
        if (!_mapEventsInitialised)
        {
            _mapEventsInitialised = true;
            _deathsThisCombat = 0;
            _destroyedObjectKinds.Clear();
            _firedEventIds.Clear();
            _floodDryBaseline = -1;
            foreach (var ev in events)
                ev?.ResetRuntime();
        }

        // Fog lifts on its own clock.
        if (grid.SightCap > 0 && roundNumber >= _fogUntilRound)
        {
            grid.SightCap = 0;
            combatUI?.AppendActionLog("The fog lifts.");
        }

        // Levers first: they are read at this boundary and change the schedule
        // the rest of the pass sees.
        foreach (var ev in events)
            if (ev != null && !string.IsNullOrEmpty(ev.LeverAt))
                ServiceLever(ev);

        foreach (var ev in events)
        {
            if (ev == null || string.IsNullOrEmpty(ev.Kind) || ev.Spent)
                continue;

            // Sleeping events wake when their condition first holds.
            if (!string.IsNullOrEmpty(ev.When) && ev.AwakenedRound < 0)
            {
                if (!ConditionMet(ev.When))
                    continue;
                ev.AwakenedRound = roundNumber;
                string wake = ev.GetStr("wake", "");
                if (!string.IsNullOrEmpty(wake))
                    combatUI?.AppendActionLog($"⚠ {wake}");
            }

            if (ev.Suppressed)
            {
                if (ev.Telegraph > 0 && FiresOn(ev, roundNumber + ev.Telegraph))
                    combatUI?.AppendActionLog($"⚠ {EventName(ev)} is held back.");
                continue;
            }

            if (FiresOn(ev, roundNumber))
                ExecuteMapEvent(ev);
            else if (ev.Telegraph > 0 && FiresOn(ev, roundNumber + ev.Telegraph))
            {
                AnnounceMapEvent(ev, roundNumber + ev.Telegraph);
                MarkTelegraph(ev, roundNumber + ev.Telegraph);
            }
        }
    }

    private static string EventName(MapEventDef ev)
        => !string.IsNullOrEmpty(ev.Id) ? ev.Id.Replace('_', ' ') : ev.Kind.Replace('_', ' ');

    /// <summary>The round this event's clock starts on: its authored round, offset
    /// by its awakening (when it has a condition) and by lever delays.</summary>
    private static int StartRound(MapEventDef ev)
    {
        int start = string.IsNullOrEmpty(ev.When) ? ev.Round : ev.AwakenedRound + ev.Round - 1;
        return start + ev.Delay;
    }

    /// <summary>True when <paramref name="ev"/> fires on the given round: its start
    /// round, then every <c>repeat_every</c> after (0 = one-shot).</summary>
    private static bool FiresOn(MapEventDef ev, int round)
    {
        if (!string.IsNullOrEmpty(ev.When) && ev.AwakenedRound < 0)
            return false;
        int start = StartRound(ev);
        if (round < start)
            return false;
        if (round == start)
            return true;
        return ev.RepeatEvery > 0 && (round - start) % ev.RepeatEvery == 0;
    }

    /// <summary>Evaluate an awaken condition against the board right now.</summary>
    private bool ConditionMet(string when)
    {
        var parts = when.Split(':');
        string kind = parts[0].Trim().ToLowerInvariant();
        switch (kind)
        {
            case "player_enters":
            case "enemy_enters":
            {
                if (parts.Length < 2) return false;
                var at = grid.ResolveRecipeCoord(parts[1]);
                int radius = parts.Length >= 3 && int.TryParse(parts[2], out var r) ? r : 1;
                bool wantPlayer = kind == "player_enters";
                foreach (var u in State.UnitsInPlay)
                {
                    if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null || u.IsMapObject)
                        continue;
                    if (u.IsPlayerControlled != wantPlayer)
                        continue;
                    if (grid.Distance(at, u.CurrentTile.Axial) <= radius)
                        return true;
                }
                return false;
            }
            case "enemy_count_below":
            case "player_count_below":
            {
                if (parts.Length < 2 || !int.TryParse(parts[1], out var n)) return false;
                bool wantPlayer = kind == "player_count_below";
                int alive = 0;
                foreach (var u in State.UnitsInPlay)
                    if (u != null && IsInstanceValid(u) && u.Stats.IsAlive && !u.IsMapObject && u.IsPlayerControlled == wantPlayer)
                        alive++;
                return alive < n;
            }
            case "first_blood":
                return _deathsThisCombat > 0;
            case "object_destroyed":
                return parts.Length >= 2 && _destroyedObjectKinds.Contains(parts[1].Trim().ToLowerInvariant());
            case "event_fired":
                return parts.Length >= 2 && _firedEventIds.Contains(parts[1].Trim());
            case "round":
                return parts.Length >= 2 && int.TryParse(parts[1], out var rr) && roundNumber >= rr;
            default:
                GD.PushWarning($"[MapEvent] unknown condition '{when}'.");
                return false;
        }
    }

    private static string LeverHint(MapEventDef ev) => ev.LeverMode.ToLowerInvariant() switch
    {
        "hold" => $"stand beside it to hold back {EventName(ev)}.",
        "delay" => $"each round someone stands beside it, {EventName(ev)} is delayed.",
        "pull" => $"stand beside it at the round's end to pull it: {EventName(ev)}.",
        _ => ""
    };

    /// <summary>Spawn the lever on first sight, then read it: a living unit of either
    /// side adjacent to it (or on its tile, for walkable objects) is "holding" it.
    /// hold: the event is suppressed while held. delay: each held boundary pushes
    /// the clock back by amount. pull: the event fires now, once, and the lever
    /// breaks. The AI does not seek levers yet; it only trips them by standing there.</summary>
    private void ServiceLever(MapEventDef ev)
    {
        if (ev.LeverUnit == null || !IsInstanceValid(ev.LeverUnit))
        {
            if (ev.Spent)
                return;
            var at = grid.ResolveRecipeCoord(ev.LeverAt);
            var tile = grid.GetTile(at);
            if (tile == null || tile.IsBlocked || tile.IsOccupied)
            {
                // Nearest open tile, so a lever never lands in a wall or on a unit.
                TileData best = null; int bestD = int.MaxValue;
                foreach (var kv in grid.Tiles)
                    if (kv.Value.IsWalkable && !kv.Value.IsBlocked && !kv.Value.IsOccupied
                        && grid.Distance(at, kv.Key) < bestD)
                    { bestD = grid.Distance(at, kv.Key); best = kv.Value; }
                tile = best;
            }
            ev.LeverUnit = tile != null ? SpawnMapObject("lever", tile) : null;
            if (ev.LeverUnit != null)
            {
                string label = $"Lever ({EventName(ev)})";
                ev.LeverUnit.Name = label;
                ev.LeverUnit.DisplayName = label;
                ev.LeverUnit.RefreshNameLabel();
                combatUI?.AppendActionLog($"A lever stands at {ev.LeverUnit.CurrentTile?.Axial}: {LeverHint(ev)}");
            }
            return;   // spawned this boundary; read from the next one
        }
        if (!ev.LeverUnit.Stats.IsAlive || ev.LeverUnit.CurrentTile == null)
            return;

        bool held = ev.HeldByAction;
        ev.HeldByAction = false;
        if (!held)
            foreach (var n in grid.GetNeighbors(ev.LeverUnit.CurrentTile.Axial))
            {
                var occ = grid.GetTile(n)?.Occupant;
                if (occ != null && occ.Stats.IsAlive && !occ.IsMapObject)
                { held = true; break; }
            }

        switch (ev.LeverMode.ToLowerInvariant())
        {
            case "hold":
                ev.Suppressed = held;
                break;
            case "delay":
                if (held)
                {
                    ev.Delay += Math.Max(1, ev.LeverAmount);
                    combatUI?.AppendActionLog($"⚠ {EventName(ev)} is held back {ev.LeverAmount} round(s).");
                }
                break;
            case "pull":
                if (held)
                    PullLever(ev);
                break;
        }
    }

    /// <summary>A pull-mode lever fires its event now (or wakes it), then breaks.</summary>
    private void PullLever(MapEventDef ev)
    {
        combatUI?.AppendActionLog($"⚠ The lever is pulled: {EventName(ev)}.");
        if (string.IsNullOrEmpty(ev.When) || ev.AwakenedRound >= 0)
            ExecuteMapEvent(ev);
        else
            ev.AwakenedRound = roundNumber;   // a sleeping event is woken by the pull
        ev.Spent = ev.RepeatEvery == 0;
        var lever = ev.LeverUnit;
        ev.LeverUnit = null;
        if (lever != null && IsInstanceValid(lever) && lever.Stats.IsAlive)
            lever.ApplyDamage(999);   // the lever breaks: its death path frees the tile
    }

    /// <summary>The event whose lever this map object is, or null.</summary>
    private MapEventDef LeverEventFor(Unit lever)
    {
        if (lever == null || grid?.ActiveMapEvents == null)
            return null;
        foreach (var ev in grid.ActiveMapEvents)
            if (ev.LeverUnit == lever)
                return ev;
        return null;
    }

    /// <summary>What working this lever does, for the bar and the hint.</summary>
    private string LeverActionText(MapEventDef ev) => ev.LeverMode.ToLowerInvariant() switch
    {
        "hold" => $"hold the lever: {EventName(ev)} does not fire this round",
        "delay" => $"work the lever: {EventName(ev)} is held back {Math.Max(1, ev.LeverAmount)} round(s)",
        _ => $"pull the lever: {EventName(ev)} fires now",
    };

    private Vector2I MapEventCenter(MapEventDef ev)
        => grid.ResolveRecipeCoord(ev.GetStr("at", "midpoint"));

    /// <summary>How many times <paramref name="ev"/> has fired before <paramref name="round"/>
    /// (0 on its first firing). Drives every clock that advances per firing.</summary>
    private static int FiringIndex(MapEventDef ev, int round)
    {
        int start = StartRound(ev);
        return ev.RepeatEvery > 0 && round > start ? (round - start) / ev.RepeatEvery : 0;
    }

    /// <summary>ring_closes closes inward: each firing tightens the ring by
    /// <c>steps</c> from its <c>radius</c>, floored at 1.</summary>
    private int AdvanceRingRadius(MapEventDef ev) => AdvanceRingRadiusAt(ev, roundNumber);

    private void ExecuteMapEvent(MapEventDef ev)
    {
        var el = MapRecipe.ParseElement(ev.GetStr("element", "fire"));
        int radius = ev.GetInt("radius", 1);
        int count;
        string what;
        switch (ev.Kind)
        {
            case "hazard_patch":
                count = grid.MapEventImbuePatch(MapEventCenter(ev), radius, el);
                what = $"{el} scars {count} tile(s)";
                break;
            case "hazard_spreads":
                count = grid.MapEventSpreadElement(el, ev.GetInt("per_patch", 1));
                what = $"{el} spreads across {count} more tile(s)";
                break;
            case "ring_closes":
                int rr = AdvanceRingRadius(ev);
                count = grid.MapEventImbueRing(MapEventCenter(ev), rr, el);
                what = $"the {el} ring closes to {rr} ({count} tile(s))";
                break;
            case "object_drops":
                {
                    // "object", not "kind": "kind" is the event kind itself, so the old
                    // read always came back "object_drops" and never matched the catalog.
                    string okind = ev.GetStr("object", "boulder");
                    var ot = grid.GetTile(MapEventCenter(ev));
                    count = SpawnMapObject(okind, ot) != null ? 1 : 0;
                    what = count > 0 ? $"a {okind} drops onto the field" : $"a {okind} finds no room";
                }
                break;
            case "ground_collapses":
                count = CollapseTiles(MapEventCenter(ev), radius, ev.GetStr("into", "rubble"));
                what = $"the ground gives way ({count} tile(s))";
                break;
            case "ground_rises":
                count = ChangeTileHeights(MapEventCenter(ev), radius, System.Math.Max(1, ev.GetInt("delta", 1)));
                what = $"the ground rises ({count} tile(s))";
                break;
            case "ground_sinks":
                count = ChangeTileHeights(MapEventCenter(ev), radius, -System.Math.Max(1, ev.GetInt("delta", 1)));
                what = $"the ground sinks ({count} tile(s))";
                break;
            // ── Pressure clocks (map_pressure_v1) ───────────────────────────
            case "tide_rises":
                count = FloodTo(FloodLevel(ev, roundNumber), ev.GetInt("damage", 2));
                what = $"the water rises ({count} tile(s) drowned)";
                break;
            case "front_advances":
                {
                    int r = FrontRadius(ev, roundNumber);
                    count = grid.MapEventImbueRing(MapEventCenter(ev), r, el);
                    what = $"the {el} front sweeps on ({count} tile(s))";
                }
                break;
            case "edge_crumbles":
                count = CrumbleBeyond(MapEventCenter(ev), CrumbleRadius(ev, roundNumber), ev.GetStr("into", "chasm"));
                what = $"the edge falls away ({count} tile(s))";
                break;
            case "traps_arm":
                count = PlantTraps(ev);
                what = count > 0 ? $"{count} trap(s) lie in the lanes" : "no ground for traps";
                break;

            // ── map_pressure_v2 ─────────────────────────────────────────────
            case "wall_rises":
                count = RaiseWall(ev);
                what = $"a wall rises ({count} tile(s))";
                break;
            case "wall_drops":
                count = DropWall(ev);
                what = $"the wall comes down ({count} tile(s))";
                break;
            case "ground_heaves":
                count = ev.Has("ring") ? ShiftRing(ev) : ShiftBand(ev);
                what = $"the ground heaves ({count} unit(s) moved)";
                break;
            case "ground_crushes":
                count = Stomp(ev);
                what = $"the ground is crushed ({count} unit(s) struck)";
                break;
            case "fog_rolls_in":
                {
                    int cap = Math.Max(1, ev.GetInt("sight", 2));
                    int turns = Math.Max(1, ev.GetInt("turns", 1));
                    grid.SightCap = cap;
                    _fogUntilRound = roundNumber + turns;
                    count = cap;
                    what = $"sight closes to {cap} tile(s) for {turns} round(s)";
                }
                break;
            case "reinforcements_arrive":
                count = ReinforceFrom(ev);
                what = count > 0 ? $"{count} enemies arrive" : "no room for arrivals";
                break;
            case "objective_turns":
                // battlefield_variety v1.2 §8: the field turns. Not destructive, but
                // the loader/roster still give it telegraph >= 1 so the intel rows
                // warn a round ahead ("telegraphs are promises").
                count = ChangeObjectiveMidFight(ev) ? 1 : 0;
                what = count > 0 ? "the objective changes" : "the objective could not change";
                break;
            case "weather_turns":
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
        ev.FiredCount++;
        if (!string.IsNullOrEmpty(ev.Id))
            _firedEventIds.Add(ev.Id);
        string msg = ev.GetStr("announce", "");
        if (string.IsNullOrEmpty(msg))
            msg = what;
        GD.Print($"[MapEvent] {msg}");
        combatUI?.AppendActionLog($"⚠ {msg}");
        OnCastleBeat(ev);   // castle_defense_v2: the body plays the beat
    }

    // ── E4 destructive events + telegraph ─────────────────────────────────

    /// <summary>Tiles an event will hit on <paramref name="fireRound"/>, for the telegraph
    /// mark. Every kind that lands on specific ground pre-marks (map_pressure_v1 widened
    /// this from the destructive kinds alone): the player sees next round's fire, water,
    /// or crumble a round ahead, and enemy pathing prices the marked tiles as hazards.</summary>
    private List<TileData> MapEventAffectedTiles(MapEventDef ev, int fireRound)
    {
        var list = new List<TileData>();
        var center = MapEventCenter(ev);
        switch (ev.Kind)
        {
            case "ground_collapses":
            case "ground_rises":
            case "ground_sinks":
            case "hazard_patch":
            {
                int radius = ev.GetInt("radius", 1);
                foreach (var t in grid.Tiles.Values)
                    if (t != null && grid.Distance(center, t.Axial) <= radius)
                        list.Add(t);
                break;
            }
            case "ring_closes":
            {
                int rr = AdvanceRingRadiusAt(ev, fireRound);
                foreach (var t in grid.Tiles.Values)
                    if (t != null && grid.Distance(center, t.Axial) == rr && t.IsWalkable && !t.IsBlocked)
                        list.Add(t);
                break;
            }
            case "front_advances":
            {
                int r = FrontRadius(ev, fireRound);
                foreach (var t in grid.Tiles.Values)
                    if (t != null && grid.Distance(center, t.Axial) == r && t.IsWalkable && !t.IsBlocked)
                        list.Add(t);
                break;
            }
            case "tide_rises":
            {
                int level = FloodLevel(ev, fireRound);
                list.AddRange(FloodTilesAt(level));
                break;
            }
            case "edge_crumbles":
            {
                int r = CrumbleRadius(ev, fireRound);
                foreach (var t in grid.Tiles.Values)
                    if (t != null && grid.Distance(center, t.Axial) >= r && !t.IsBlocked)
                        list.Add(t);
                break;
            }
            case "wall_rises":
            case "wall_drops":
            case "ground_heaves":
                list.AddRange(ev.Has("ring") ? RingTiles(ev) : BandTiles(ev));
                break;
            case "ground_crushes":
                list.AddRange(StompTiles(ev));
                break;
            case "reinforcements_arrive":
                list.AddRange(ArrivalTilesNear(MapEventCenter(ev), CountUnits(ev)));
                break;
            case "weather_turns":
                if (ev.GetStr("weather", "storm") == "storm")
                {
                    var t = StormTargetForRound(fireRound);
                    if (t != null)
                        list.Add(t);
                }
                break;
        }
        return list;
    }

    // ── map_pressure_v2 kinds ───────────────────────────────────────────────

    /// <summary>The band an event acts on: `length` tiles through `at` along `dir`
    /// (a hex direction index, "axis", or "flank"; default flank), plus `width`
    /// extra rows on each side.</summary>
    private List<TileData> BandTiles(MapEventDef ev)
    {
        var list = new List<TileData>();
        var center = MapEventCenter(ev);
        var dir = grid.ResolveEventDirection(ev.GetStr("dir", "flank"));
        int length = Math.Max(1, ev.GetInt("length", 5));
        int width = Math.Max(0, ev.GetInt("width", 0));
        var side = grid.ResolveEventDirection(ev.GetStr("dir", "flank") == "axis" ? "flank" : "axis");
        int back = length / 2;
        for (int i = -back; i < length - back; i++)
            for (int w = -width; w <= width; w++)
            {
                var t = grid.GetTile(center + dir * i + side * w);
                if (t != null)
                    list.Add(t);
            }
        return list;
    }

    private int RaiseWall(MapEventDef ev)
    {
        string kind = ev.GetStr("kind_obstacle", ev.GetStr("wall", "high"));
        int gaps = Math.Max(0, ev.GetInt("gaps", 0));
        var band = BandTiles(ev);
        int n = 0;
        for (int i = 0; i < band.Count; i++)
        {
            var t = band[i];
            if (t.IsBlocked || t.IsOccupied || t.TerrainType == TileTerrainType.Water)
                continue;
            if (gaps > 0 && band.Count >= 3 && i == band.Count / 2)
                continue;   // one gate in the middle when asked
            grid.ApplyObstacle(t, kind);
            n++;
        }
        if (n > 0)
        {
            grid.RefreshObstacleVisuals();
            RefreshThreatTiles();
        }
        return n;
    }

    private int DropWall(MapEventDef ev)
    {
        int n = 0;
        foreach (var t in BandTiles(ev))
        {
            if (!t.IsBlocked || t.ObstacleKind.StartsWith("building:"))
                continue;
            grid.ClearObstacle(t);
            n++;
        }
        if (n > 0)
        {
            grid.RefreshObstacleVisuals();
            RefreshThreatTiles();
        }
        return n;
    }

    // ── castle_defense_v2: the castle's own beats ──────────────────────────

    /// <summary>Tiles at exactly `radius` from `ring` (a coord token), no higher
    /// than `max_height` when given. The curved counterpart of BandTiles, for
    /// bodies that act on everything pressed against them.</summary>
    private List<TileData> RingTiles(MapEventDef ev)
    {
        var list = new List<TileData>();
        var center = grid.ResolveRecipeCoord(ev.GetStr("ring", "center"));
        int radius = Math.Max(1, ev.GetInt("radius", 1));
        int maxH = ev.GetInt("max_height", int.MaxValue);
        foreach (var t in grid.Tiles.Values)
            if (t != null && grid.Distance(center, t.Axial) == radius && t.Height <= maxH)
                list.Add(t);
        return list;
    }

    /// <summary>shift with a `ring`: every unit on the ring is shoved `tiles`
    /// straight away from the ring's centre through the resolver.</summary>
    private int ShiftRing(MapEventDef ev)
    {
        var center = grid.ResolveRecipeCoord(ev.GetStr("ring", "center"));
        int tiles = Math.Max(1, ev.GetInt("tiles", 1));
        int collision = ev.GetInt("damage", 0);
        var movers = new List<Unit>();
        foreach (var t in RingTiles(ev))
            if (t.Occupant != null && t.Occupant.Stats.IsAlive && !(t.Occupant.IsMapObject && !t.Occupant.Pushable))
                movers.Add(t.Occupant);
        int n = 0;
        foreach (var u in movers)
        {
            if (u.CurrentTile == null || !u.Stats.IsAlive)
                continue;
            var dir = ForcedMove.StepAwayFrom(grid, center, u.CurrentTile.Axial);
            if (dir == Vector2I.Zero)
                continue;
            var r = ForcedMove.Push(grid, u, dir, tiles, collision, null, m => combatUI?.AppendActionLog(m));
            if (r.Pushed > 0 || r.Collided)
                n++;
        }
        RefreshThreatTiles();
        return n;
    }

    /// <summary>The disk a stomp lands on: `radius` around `at`, no higher than
    /// `max_height` (so a leg beside a raised deck never lands on the deck).</summary>
    private List<TileData> StompTiles(MapEventDef ev)
    {
        var list = new List<TileData>();
        var center = MapEventCenter(ev);
        int radius = Math.Max(0, ev.GetInt("radius", 1));
        int maxH = ev.GetInt("max_height", int.MaxValue);
        foreach (var t in grid.Tiles.Values)
            if (t != null && grid.Distance(center, t.Axial) <= radius && t.Height <= maxH)
                list.Add(t);
        return list;
    }

    /// <summary>A leg comes down. Everything on the disk takes `damage`; anything
    /// still standing on the rim of the disk is thrown one tile outward through
    /// the resolver; the ground under the foot sinks one step (a footprint) unless
    /// `crater` is false. Telegraphed a round ahead like every destructive kind.</summary>
    private int Stomp(MapEventDef ev)
    {
        var center = MapEventCenter(ev);
        int dmg = Math.Max(0, ev.GetInt("damage", 6));
        bool crater = !ev.Has("crater") || ev.GetVariant("crater").AsBool();
        var disk = StompTiles(ev);
        int struck = 0;
        var victims = new List<Unit>();
        foreach (var t in disk)
            if (t.Occupant != null && t.Occupant.Stats.IsAlive)
                victims.Add(t.Occupant);
        foreach (var u in victims)
        {
            if (dmg > 0 && !u.IsMapObject)
            {
                u.ApplyDamage(dmg);
                combatUI?.AppendActionLog($"{u.DisplayName} is crushed under the foot: {dmg} damage.");
            }
            struck++;
            if (u.CurrentTile == null || !u.Stats.IsAlive)
                continue;
            if (u.IsMapObject && !u.Pushable)
                continue;
            var dir = ForcedMove.StepAwayFrom(grid, center, u.CurrentTile.Axial);
            if (dir != Vector2I.Zero)
                ForcedMove.Push(grid, u, dir, 1, 0, null, m => combatUI?.AppendActionLog(m));
        }
        if (crater)
        {
            var footprint = disk.Where(t => t.IsWalkable && !t.IsBlocked && t.TerrainType != TileTerrainType.Water)
                                .Select(t => t.Axial).ToList();
            var changed = new List<Vector2I>();
            foreach (var c in footprint)
            {
                var t = grid.GetTile(c);
                if (t == null || t.Height <= -2)
                    continue;
                t.Height -= 1;
                changed.Add(c);
            }
            foreach (var c in changed)
                grid.RebuildTileAndNeighbors(c);
        }
        RefreshThreatTiles();
        return struck;
    }

    /// <summary>Every unit standing in the band is shoved `tiles` along `push`
    /// (a direction token; default the band's own dir) through the resolver, so
    /// walls, casks, and ledges all count. Units furthest along the push go first
    /// so they do not block the ones behind them.</summary>
    private int ShiftBand(MapEventDef ev)
    {
        var dir = grid.ResolveEventDirection(ev.GetStr("push", ev.GetStr("dir", "flank")));
        int tiles = Math.Max(1, ev.GetInt("tiles", 1));
        int collision = ev.GetInt("damage", 0);
        var movers = new List<Unit>();
        foreach (var t in BandTiles(ev))
            if (t.Occupant != null && t.Occupant.Stats.IsAlive && !(t.Occupant.IsMapObject && !t.Occupant.Pushable))
                movers.Add(t.Occupant);
        // Sort by projection along dir, descending: front of the shove moves first.
        movers.Sort((a, b) =>
            (b.CurrentTile.Axial.X * dir.X + b.CurrentTile.Axial.Y * dir.Y)
            .CompareTo(a.CurrentTile.Axial.X * dir.X + a.CurrentTile.Axial.Y * dir.Y));
        int n = 0;
        foreach (var u in movers)
        {
            if (u.CurrentTile == null || !u.Stats.IsAlive)
                continue;
            var r = ForcedMove.Push(grid, u, dir, tiles, collision, null, m => combatUI?.AppendActionLog(m));
            if (r.Pushed > 0 || r.Collided)
                n++;
        }
        RefreshThreatTiles();
        return n;
    }

    private List<string> UnitIds(MapEventDef ev)
    {
        var ids = new List<string>();
        if (!ev.Has("units"))
            return ids;
        foreach (var v in ev.GetVariant("units").AsGodotArray())
            ids.Add(v.AsString());
        return ids;
    }

    private int CountUnits(MapEventDef ev) => UnitIds(ev).Count;

    /// <summary>The nearest open, unoccupied, non-hazard tiles to <paramref name="at"/>,
    /// nearest first: where arrivals land.</summary>
    private List<TileData> ArrivalTilesNear(Vector2I at, int needed)
    {
        var list = new List<TileData>();
        if (needed <= 0)
            return list;
        var all = new List<TileData>();
        foreach (var kv in grid.Tiles)
            if (kv.Value.IsWalkable && !kv.Value.IsBlocked && !kv.Value.IsOccupied && !kv.Value.IsHazardous
                && kv.Value.TerrainType != TileTerrainType.Water)
                all.Add(kv.Value);
        all.Sort((a, b) => grid.Distance(at, a.Axial).CompareTo(grid.Distance(at, b.Axial)));
        for (int i = 0; i < all.Count && i < needed; i++)
            list.Add(all[i]);
        return list;
    }

    /// <summary>Spawn `units` (registry ids) at the tiles nearest `at`. A wave with a
    /// place on the map, so holding the tunnel mouth is a real decision.</summary>
    private int ReinforceFrom(MapEventDef ev)
    {
        var ids = UnitIds(ev);
        var tiles = ArrivalTilesNear(MapEventCenter(ev), ids.Count);
        int n = 0;
        for (int i = 0; i < ids.Count && i < tiles.Count; i++)
        {
            var u = SpawnRegistryUnit(ids[i], tiles[i], teamId: 1,
                                      difficultyMult: ev.GetFloat("difficulty", 1f),
                                      isMidFightSummon: false);
            if (u != null)
                n++;
        }
        if (n > 0)
        {
            RefreshEnemyRoster();
            RefreshThreatTiles();
        }
        return n;
    }

    // ── Pressure clocks (map_pressure_v1) ───────────────────────────────────

    private int AdvanceRingRadiusAt(MapEventDef ev, int round)
    {
        int start = ev.GetInt("radius", 4);
        int steps = Math.Max(1, ev.GetInt("steps", 1));
        return Math.Max(1, start - FiringIndex(ev, round) * steps);
    }

    /// <summary>flood: the height at or below which ground drowns. Starts at `level`
    /// (default -1, so nothing drowns until the first rise) and rises by `rise` per firing.</summary>
    private int FloodLevel(MapEventDef ev, int round)
        => ev.GetInt("level", -1) + (FiringIndex(ev, round) + 1) * Math.Max(1, ev.GetInt("rise", 1));

    /// <summary>front_advances: a hazard shell expanding from `at` (default enemy_anchor)
    /// by `steps` per firing from `radius`. Read from a side anchor it is a front
    /// sweeping across the field; from the midpoint it is the cauldron ring in reverse.</summary>
    private int FrontRadius(MapEventDef ev, int round)
        => ev.GetInt("radius", 1) + FiringIndex(ev, round) * Math.Max(1, ev.GetInt("steps", 1));

    /// <summary>edge_crumbles: everything at or beyond this distance from `at` falls
    /// away. Starts at `radius` (default: the map's radius) and shrinks by `steps`.</summary>
    private int CrumbleRadius(MapEventDef ev, int round)
        => Math.Max(2, ev.GetInt("radius", grid.MapRadius) - FiringIndex(ev, round) * Math.Max(1, ev.GetInt("steps", 1)));

    /// <summary>Drown every walkable tile at or below <paramref name="level"/>: occupants
    /// are shoved to the nearest dry tile and take <paramref name="damage"/>; the tile
    /// becomes water (impassable, sight clear). Spawn-reserved tiles are not exempt:
    /// the tide is the point.</summary>
    /// <summary>The share of the map's original dry ground a flood must leave dry.
    /// Flat maps (marsh_flats: heights -1..1, mostly 0) drown almost everything at
    /// level 0, which strands both sides in the water with nowhere to go (observed
    /// 2026-09-08). The tide is pressure, not a wipe.</summary>
    private const float FloodMinDryShare = 0.40f;
    private int _floodDryBaseline = -1;

    /// <summary>Water over drowned ground, world units. HeightStep (0.6) minus the grass
    /// noise amplitude (0.18) minus margin: drowned tiles sit under real water, dry tiles
    /// one step up keep their tops clear, and the surface just reaches their deepest
    /// noise dips (the at-grade marsh look). Raise toward 0.5 for a deeper tide.</summary>
    private const float FloodCoverDepth = 0.40f;

    /// <summary>How long the tide takes to sweep in and settle. The front crosses the
    /// board in the first ~60% (see painterly_water tide_sweep); 1.6 s read as a
    /// random welling-up (playtest 2026-09-16), 5 s reads as a wave coming in.</summary>
    private const float FloodRiseSeconds = 5.0f;

    /// <summary>The tiles a flood to <paramref name="level"/> would take, after the
    /// dry-share cap: shared by the telegraph and the firing so the warning is honest.</summary>
    private List<TileData> FloodTilesAt(int level)
    {
        int baseline = _floodDryBaseline;
        if (baseline < 0)
        {
            baseline = 0;
            foreach (var t in grid.Tiles.Values)
                if (t != null && t.IsWalkable && !t.IsBlocked && t.TerrainType != TileTerrainType.Water)
                    baseline++;
        }
        int mustStayDry = Mathf.CeilToInt(baseline * FloodMinDryShare);
        int effective = level;
        while (true)
        {
            var affected = new List<TileData>();
            int dryAfter = 0;
            foreach (var t in grid.Tiles.Values)
            {
                if (t == null || !t.IsWalkable || t.IsBlocked || t.TerrainType == TileTerrainType.Water)
                    continue;
                if (t.Height <= effective) affected.Add(t); else dryAfter++;
            }
            if (dryAfter >= mustStayDry || affected.Count == 0)
                return affected;
            effective--;
        }
    }

    private int FloodTo(int level, int damage)
    {
        // Baseline: the walkable ground the map started with, measured once.
        if (_floodDryBaseline < 0)
        {
            _floodDryBaseline = 0;
            foreach (var t in grid.Tiles.Values)
                if (t != null && t.IsWalkable && !t.IsBlocked && t.TerrainType != TileTerrainType.Water)
                    _floodDryBaseline++;
        }
        int mustStayDry = Mathf.CeilToInt(_floodDryBaseline * FloodMinDryShare);

        // Lower the level until enough ground stays dry. Integer heights on a flat
        // map make this all-or-nothing per step, so the cap can hold the tide at
        // the pockets it has already taken.
        int effective = level;
        List<TileData> affected = null;
        while (true)
        {
            affected = new List<TileData>();
            int dryAfter = 0;
            foreach (var t in grid.Tiles.Values)
            {
                if (t == null || !t.IsWalkable || t.IsBlocked || t.TerrainType == TileTerrainType.Water)
                    continue;
                if (t.Height <= effective)
                    affected.Add(t);
                else
                    dryAfter++;
            }
            if (dryAfter >= mustStayDry || affected.Count == 0)
                break;
            effective--;
        }
        if (effective < level)
            combatUI?.AppendActionLog(affected.Count > 0
                ? $"⚠ The water can rise only so far: it holds at the low ground."
                : "⚠ The water can rise no further.");

        // Raise the resting waterline FIRST: ConvertTile re-bakes each drowned tile's
        // splat copy, and the grid-line fade has to be in the template by then.
        if (affected.Count > 0)
            grid.SetFloodSurface(effective, FloodCoverDepth);
        foreach (var t in affected)
        {
            if (t.Occupant != null && t.Occupant.Stats.IsAlive)
                EvictToDry(t.Occupant, effective, damage);
            ConvertTile(t, "water");
        }
        if (affected.Count > 0)
            grid.SpawnWaterPlane(FloodRiseSeconds);   // the surface swells up over the drowned ground
        return affected.Count;
    }

    private void EvictToDry(Unit u, int level, int damage)
    {
        var from = u.CurrentTile;
        if (from != null)
        {
            TileData best = null;
            int bestD = int.MaxValue;
            foreach (var kv in grid.Tiles)
            {
                var t = kv.Value;
                if (t == null || t.Height <= level || !t.CanEnter(u))
                    continue;
                int d = grid.Distance(from.Axial, kv.Key);
                if (d < bestD) { bestD = d; best = t; }
            }
            if (best != null && bestD <= 3)
                u.PlaceOnTile(best, MovementKind.Forced, new MoveContext(grid) { SuppressFalling = true });
        }
        if (damage > 0)
            u.ApplyDamage(damage);
    }

    /// <summary>Everything at or beyond <paramref name="radius"/> from <paramref name="center"/>
    /// becomes <paramref name="into"/> (chasm by default), evicting occupants inward.</summary>
    private int CrumbleBeyond(Vector2I center, int radius, string into)
    {
        var affected = new List<TileData>();
        foreach (var t in grid.Tiles.Values)
            if (t != null && grid.Distance(center, t.Axial) >= radius && !t.IsBlocked)
                affected.Add(t);
        foreach (var t in affected)
        {
            if (t.Occupant != null && t.Occupant.Stats.IsAlive)
                EvictFromCollapse(t.Occupant);
            ConvertTile(t, into);
        }
        return affected.Count;
    }

    /// <summary>trap: plant `count` neutral glyphs (team 2, so both sides trip them) on
    /// open, unreserved, unoccupied tiles within `radius` of `at`, biased toward the
    /// player-enemy axis (the lanes). `damage` (default 4) and optional `status` /
    /// `duration`. Visible unless `hidden` is true. Deterministic per seed.</summary>
    private int PlantTraps(MapEventDef ev)
    {
        if (State?.Glyphs == null)
            return 0;
        int count = Math.Max(1, ev.GetInt("count", 2));
        int radius = ev.GetInt("radius", 3);
        var center = MapEventCenter(ev);
        var a = grid.PlayerLayoutAnchor;
        var b = grid.EnemyLayoutAnchor;

        var candidates = new List<(TileData t, int score)>();
        foreach (var kv in grid.Tiles)
        {
            var t = kv.Value;
            if (t == null || !t.IsWalkable || t.IsBlocked || t.IsOccupied || t.Glyph != null)
                continue;
            if (t.TerrainType == TileTerrainType.Water || t.IsHazardous)
                continue;
            if (grid.Distance(center, kv.Key) > radius)
                continue;
            if (grid.Distance(a, kv.Key) <= 2 || grid.Distance(b, kv.Key) <= 2)
                continue;   // never inside a deployment
            // Lane bias: distance from the straight anchor line, smaller is better.
            int off = Math.Abs(grid.Distance(a, kv.Key) + grid.Distance(kv.Key, b) - grid.Distance(a, b));
            candidates.Add((t, off * 10 + (kv.Key.X * 7 + kv.Key.Y * 13) % 10));
        }
        candidates.Sort((x, y) => x.score.CompareTo(y.score));

        int dmg = ev.GetInt("damage", 4);
        string status = ev.GetStr("status", "");
        int dur = ev.GetInt("duration", 1);
        bool hidden = ev.GetStr("hidden", "false") == "true" || (ev.Has("hidden") && ev.GetVariant("hidden").AsBool());
        int planted = 0;
        foreach (var (t, _) in candidates)
        {
            if (planted >= count)
                break;
            var g = State.Glyphs.Prepare(t, null, glyph =>
            {
                glyph.OwnerTeam = 2;            // the field: nobody's friend
                glyph.OwnerId = "the ground";
                glyph.Trigger = GlyphTrigger.Enter;
                glyph.Damage = dmg;
                glyph.Status = string.IsNullOrEmpty(status) ? null : status;
                glyph.StatusDuration = dur;
                glyph.Invisible = hidden;
            });
            if (g != null)
                planted++;
        }
        return planted;
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

    /// <summary>ground_collapses: convert every tile within radius, evicting occupants
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
        // Water shows itself: the plane rises over the tile (tide swell), so the blue
        // emission tint would only fight it. Chasm and rubble keep their scars.
        if (into != "water")
            grid.GetTileView(t.Axial)?.SetTerrainScar(into);
    }

    /// <summary>ground_rises / ground_sinks: shift the height of every tile within radius by
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
