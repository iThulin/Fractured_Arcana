using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.CastPreview.cs
//
// Purpose:        What the player sees while a card half is dragged or
//                 hovered: the cast ENVELOPE (an outline around the
//                 tiles the selector would actually accept, so a wall
//                 bites a notch out of a bolt's reach), unit MARKERS on
//                 the enemies it can and cannot hit, a cursor-follow
//                 SHAPE for bursts, cones, rings, and lines, and a
//                 TRAJECTORY trace to the hovered target. Replaces the
//                 old per-tile range disc (SetRangeHighlight), which
//                 painted every tile within N whether or not the spell
//                 could reach it.
// Layer:          Systems / Combat / Core (partial of CombatManager)
// Collaborators:  MovementZoneRenderer (ShowOutline), TrajectoryTrace,
//                 HexGridManager.Cover.cs (CoverBetween, BurstFill),
//                 TargetSelectors, Unit (SetTargetable, SetBlockedReason)
// See:            docs/cover_and_zoc_v1.md §11
// ============================================================

public partial class CombatManager
{
    private MovementZoneRenderer _castZone;     // envelope outline
    private MovementZoneRenderer _aimZone;      // cursor-follow shape
    private TrajectoryTrace _trace;             // caster to hovered target
    private readonly List<Unit> _markedUnits = new();

    // Contrast rule: the envelope must win against the move zone. Saturated amber at
    // near-full alpha with a taller lip and a whisper of fill; the move zone is
    // dimmed to a quarter while a card is up (SetDim in ShowTargetHighlight).
    private static readonly Color EnvelopeColor = new(1.00f, 0.78f, 0.30f, 0.95f);  // amber
    private const float EnvelopeFill = 0.07f;
    private const float MoveZoneDimWhileAiming = 0.75f;
    private static readonly Color AimBurstColor = new(0.95f, 0.62f, 0.32f, 0.85f);  // ember
    private static readonly Color AimGroundColor = new(0.70f, 0.78f, 0.62f, 0.85f); // moss
    private static readonly Color TargetRingColor = new(0.98f, 0.86f, 0.45f, 0.85f);
    private static readonly Color BlockedRingColor = new(0.55f, 0.45f, 0.42f, 0.55f);

    /// <summary>The move zone's dim is a function of whether a cast envelope is
    /// currently drawn, evaluated every frame from _Process. Event-driven dimming
    /// stranded the zone dark when a card's hover-exit arrived out of order with
    /// the full-view swap; deriving it cannot.</summary>
    private void SyncMoveZoneDim()
    {
        if (_zoneRenderer == null)
            return;
        bool envelopeUp = _castZone != null && _castZone.HasTiles;
        _zoneRenderer.SetDim(envelopeUp ? MoveZoneDimWhileAiming : 0f);
    }

    private void EnsureCastRenderers()
    {
        if (grid == null)
            return;
        if (_castZone == null)
        {
            _castZone = new MovementZoneRenderer { Name = "CastEnvelopeRenderer", HexRadius = grid.HexRadius, PlayerLipHeight = 0.16f, LineWidth = 0.11f };
            grid.AddChild(_castZone);
        }
        if (_aimZone == null)
        {
            _aimZone = new MovementZoneRenderer { Name = "CastAimRenderer", HexRadius = grid.HexRadius, PlayerLipHeight = 0.1f };
            grid.AddChild(_aimZone);
        }
        if (_trace == null)
        {
            _trace = new TrajectoryTrace { Name = "TrajectoryTrace" };
            grid.AddChild(_trace);
        }
    }

    // ── Entry points (same names the drag / hover code already calls) ────────

    private void ShowTargetHighlight(CardHalf half)
    {
        ClearTargetHighlight();
        ClearConstructAura();   // §8: targeting takes over the board during a drag
        if (half == null || selectedUnit?.CurrentTile == null || grid == null)
            return;
        EnsureCastRenderers();

        _lastHighlightedHalf = half;
        var center = selectedUnit.CurrentTile.Axial;

        var envelope = CastEnvelope(half.Targeting, center);
        if (envelope.Count > 0)
        {
            _castZone.ShowOutline(envelope, grid, EnvelopeColor, EnvelopeFill);
        }

        MarkUnits(half.Targeting, center, envelope);
    }

    private void ClearTargetHighlight()
    {
        _martialPreviewUp = false;
        _castZone?.Clear();
        _aimZone?.Clear();
        _trace?.Clear();
        foreach (var u in _markedUnits)
        {
            if (u == null || !IsInstanceValid(u))
                continue;
            u.SetTargetable(false);
            u.SetBlockedReason("");
        }
        _markedUnits.Clear();

        // Legacy per-tile paints, if anything still adds to this set.
        foreach (var coord in _targetHighlightTiles)
        {
            var tileView = grid?.GetTileView(coord);
            tileView?.SetTargetHighlight(false);
            tileView?.SetRangeHighlight(false, false);
        }
        _targetHighlightTiles.Clear();
        _lastHighlightedHalf = null;
    }

    private void ClearCastAim()
    {
        _aimZone?.Clear();
        _trace?.Clear();
    }

    /// <summary>Cursor moved over <paramref name="tile"/> while a half is dragged:
    /// redraw the aim shape and the trajectory for that aim point. Called from the
    /// drag hover hook next to the damage preview.</summary>
    private void UpdateCastAim(HexTile tile)
    {
        _aimZone?.Clear();
        _trace?.Clear();
        if (!_isCardBeingDragged || _draggedHalf == null || tile == null
            || selectedUnit?.CurrentTile == null || grid == null)
            return;
        EnsureCastRenderers();

        var center = selectedUnit.CurrentTile.Axial;
        var aim = tile.Axial;
        var t = _draggedHalf.Targeting;

        switch (t)
        {
            case SelectUnitTarget u:
            {
                var victim = grid.GetTile(aim)?.Occupant;
                if (victim == null || !victim.Stats.IsAlive)
                    return;
                bool bolt = u.delivery == Delivery.Bolt;
                var blocker = grid.FirstLosBlocker(center, aim);
                bool coverStop = bolt && grid.CoverBetween(aim, center) == CoverKind.High;
                bool inRange = grid.Distance(center, aim) <= u.range;
                _trace.Show(grid, center, aim, bolt ? TrajectoryTrace.Style.Straight : TrajectoryTrace.Style.Lob,
                            blocker?.Axial, coverStop || !inRange || (blocker != null && (u.los || bolt)));
                break;
            }
            case SelectUnitThenTileTarget tsx:
            {
                var victim = grid.GetTile(aim)?.Occupant;
                if (victim == null || !victim.Stats.IsAlive)
                    return;
                _trace.Show(grid, center, aim, TrajectoryTrace.Style.Lob, null,
                            grid.Distance(center, aim) > tsx.range);
                break;
            }
            case SelectAreaTarget a:
                _aimZone.ShowOutline(grid.BurstReach(aim, a.Radius), grid, AimBurstColor, 0.16f);
                break;
            case SelectRingTarget r:
                _aimZone.ShowOutline(grid.BurstRing(aim, r.Radius), grid, AimBurstColor, 0.16f);
                break;
            case SelectConeTarget c:
            {
                if (aim == center)
                    return;
                int dirIdx = HexDirection.Pick(center, aim, c.Range);
                var cone = ConeCoords(center, dirIdx, c.Range);
                cone.IntersectWith(grid.BurstReach(center, c.Range));
                _aimZone.ShowOutline(cone, grid, AimBurstColor, 0.16f);
                break;
            }
            case SelectLineTarget l:
            {
                if (aim == center)
                    return;
                int dirIdx = HexDirection.Pick(center, aim, l.Length);
                _aimZone.ShowOutline(LineCoords(center, dirIdx, l.Length), grid, AimBurstColor, 0.16f);
                break;
            }
            case SelectAdjacentToTarget:
            {
                var set = new HashSet<Vector2I>();
                foreach (var n in grid.GetNeighbors(aim))
                    set.Add(n);
                _aimZone.ShowOutline(set, grid, AimBurstColor, 0.16f);
                break;
            }
            case SelectTileTarget:
            case SelectEmptyTileTarget:
            case SelectElementTileTarget:
            {
                var set = new HashSet<Vector2I> { aim };
                _aimZone.ShowOutline(set, grid, AimGroundColor, 0.22f);
                break;
            }
        }
    }

    // ── Martial attack preview ───────────────────────────────────────────────

    private bool _martialPreviewUp;

    /// <summary>Effective martial reach, mirroring TryMartialAttack: stance bonus,
    /// and +1 for a ranged shot from above the target.</summary>
    private int MartialReach(Unit attacker, Unit target)
    {
        int reach = attacker.AttackRange + (attacker.ActiveStance?.AttackRangeBonus ?? 0);
        if (reach > 1 && target?.CurrentTile != null && attacker.CurrentTile != null
            && attacker.CurrentTile.Height > target.CurrentTile.Height)
            reach += 1;
        return reach;
    }

    /// <summary>Why <paramref name="attacker"/> cannot strike <paramref name="target"/>
    /// from where it stands, or null when it can. Same rules as TryMartialAttack.</summary>
    private string MartialBlockReason(Unit attacker, Unit target)
    {
        int reach = MartialReach(attacker, target);
        var a = attacker.CurrentTile.Axial;
        var b = target.CurrentTile.Axial;
        int dist = grid.Distance(a, b);
        if (reach <= 1 && Math.Abs(attacker.CurrentTile.Height - target.CurrentTile.Height) > grid.CliffHeightThreshold)
            return "too high";
        if (dist > reach)
            return "out of range";
        if (dist > 1)
        {
            if (!grid.HasLineOfSight(a, b))
                return "no sight";
            if (grid.CoverBetween(b, a) == CoverKind.High)
                return "full cover";
        }
        return null;
    }

    private void ShowMartialPreview(Unit attacker, Unit hovered)
    {
        if (attacker?.CurrentTile == null || hovered?.CurrentTile == null || grid == null)
            return;
        EnsureCastRenderers();
        ClearMartialPreview();
        _martialPreviewUp = true;

        var center = attacker.CurrentTile.Axial;
        int reach = MartialReach(attacker, hovered);
        bool ranged = reach > 1;

        // Envelope: where a strike could land from here.
        var envelope = new HashSet<Vector2I>();
        foreach (var kv in grid.Tiles)
        {
            if (kv.Key == center || grid.Distance(center, kv.Key) > reach)
                continue;
            if (ranged && (!grid.HasLineOfSight(center, kv.Key) || grid.CoverBetween(kv.Key, center) == CoverKind.High))
                continue;
            if (!ranged && Math.Abs(kv.Value.Height - attacker.CurrentTile.Height) > grid.CliffHeightThreshold)
                continue;
            envelope.Add(kv.Key);
        }
        _castZone.ShowOutline(envelope, grid, EnvelopeColor, EnvelopeFill);

        // Markers on every enemy the strike would want.
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || !e.Stats.IsAlive || e.CurrentTile == null || e.IsMapObject)
                continue;
            string reason = MartialBlockReason(attacker, e);
            if (reason == null)
                e.SetTargetable(true, TargetRingColor);
            else if (grid.Distance(center, e.CurrentTile.Axial) <= reach + 2)
            {
                e.SetTargetable(true, BlockedRingColor);
                e.SetBlockedReason(reason);
            }
            else
                continue;
            _markedUnits.Add(e);
        }

        // Trajectory to the hovered enemy: a martial shot is a bolt.
        var aim = hovered.CurrentTile.Axial;
        if (grid.Distance(center, aim) > 1)
        {
            var blocker = grid.FirstLosBlocker(center, aim);
            _trace.Show(grid, center, aim, TrajectoryTrace.Style.Straight, blocker?.Axial,
                        MartialBlockReason(attacker, hovered) != null);
        }
    }

    private void ClearMartialPreview()
    {
        if (!_martialPreviewUp)
            return;
        _martialPreviewUp = false;
        ClearTargetHighlight();
    }

    // ── Envelope: the tiles the selector would accept from this caster ──────

    private HashSet<Vector2I> CastEnvelope(ITargetSelector t, Vector2I center)
    {
        var set = new HashSet<Vector2I>();
        switch (t)
        {
            case SelectUnitTarget u:
                foreach (var kv in grid.Tiles)
                {
                    if (grid.Distance(center, kv.Key) > u.range || kv.Key == center)
                        continue;
                    if ((u.los || u.delivery == Delivery.Bolt) && !grid.HasLineOfSight(center, kv.Key))
                        continue;
                    if (u.BlockedByCover(grid, center, kv.Key))
                        continue;
                    set.Add(kv.Key);
                }
                break;

            case SelectTwoStepTarget ts:
                foreach (var kv in grid.Tiles)
                    if (grid.Distance(center, kv.Key) <= ts.range && kv.Key != center)
                        set.Add(kv.Key);
                break;

            case SelectTileTarget tt:
                foreach (var kv in grid.Tiles)
                    if (grid.Distance(center, kv.Key) <= tt.range)
                        set.Add(kv.Key);
                break;

            case SelectEmptyTileTarget et:
                foreach (var kv in grid.Tiles)
                    if (grid.Distance(center, kv.Key) <= et.Range && kv.Value.CanEnter(selectedUnit))
                        set.Add(kv.Key);
                break;

            case SelectElementTileTarget el:
            {
                var needed = el.Element.ToLowerInvariant() switch
                {
                    "fire" => TileElementType.Fire,
                    "ice" => TileElementType.Frost,
                    "storm" => TileElementType.Lightning,
                    "stone" => TileElementType.Earth,
                    _ => TileElementType.None
                };
                foreach (var kv in grid.Tiles)
                    if (kv.Value?.ElementType == needed && grid.Distance(center, kv.Key) <= el.Range)
                        set.Add(kv.Key);
                break;
            }

            // Caster-centred shapes: the envelope IS the shape, bent by the map.
            case SelectAreaTarget a:
                set.UnionWith(grid.BurstReach(center, a.Radius));
                break;
            case SelectRingTarget r:
                set.UnionWith(grid.BurstRing(center, r.Radius));
                break;
            case SelectConeTarget c:
                set.UnionWith(grid.BurstReach(center, c.Range));
                set.Remove(center);
                break;
            case SelectLineTarget l:
                for (int d = 0; d < 6; d++)
                    set.UnionWith(LineCoords(center, d, l.Length));
                break;

            // Self, global, by-tag, nearest-to-target, memorial: nothing to outline.
        }
        return set;
    }

    // ── Unit markers ─────────────────────────────────────────────────────────

    private void MarkUnits(ITargetSelector t, Vector2I center, HashSet<Vector2I> envelope)
    {
        bool enemiesOnly, friendliesOnly;
        int range;
        switch (t)
        {
            case SelectUnitTarget u:
                enemiesOnly = u.enemyOnly; friendliesOnly = u.friendlyOnly; range = u.range; break;
            case SelectTwoStepTarget ts:
                enemiesOnly = ts.enemyOnly; friendliesOnly = ts.friendlyOnly; range = ts.range; break;
            case SelectAreaTarget a:
                enemiesOnly = a.EnemiesOnly; friendliesOnly = false; range = int.MaxValue; break;
            case SelectRingTarget r:
                enemiesOnly = r.EnemiesOnly; friendliesOnly = false; range = int.MaxValue; break;
            case SelectConeTarget c:
                enemiesOnly = c.EnemiesOnly; friendliesOnly = false; range = int.MaxValue; break;
            case SelectLineTarget l:
                enemiesOnly = l.EnemiesOnly; friendliesOnly = false; range = int.MaxValue; break;
            default:
                return;
        }

        var bolt = t is SelectUnitTarget su && su.delivery == Delivery.Bolt;
        var needsSight = t is SelectUnitTarget su2 && (su2.los || bolt);

        foreach (var unit in State.UnitsInPlay)
        {
            if (unit == null || !IsInstanceValid(unit) || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (unit == selectedUnit || unit.IsMapObject)
                continue;
            if (enemiesOnly && unit.TeamId == selectedUnit.TeamId)
                continue;
            if (friendliesOnly && unit.TeamId != selectedUnit.TeamId)
                continue;

            var at = unit.CurrentTile.Axial;
            if (envelope.Contains(at))
            {
                unit.SetTargetable(true, TargetRingColor);
                unit.SetBlockedReason("");
                _markedUnits.Add(unit);
                continue;
            }

            // Not reachable: say why, but only for units the card would otherwise want
            // (enemies within a tile or two of range, so the field is not littered).
            int dist = grid.Distance(center, at);
            string reason = null;
            if (t is SelectUnitTarget)
            {
                if (dist > range)
                    reason = dist <= range + 2 ? "out of range" : null;
                else if (bolt && grid.CoverBetween(at, center) == CoverKind.High)
                    reason = "full cover";
                else if (needsSight && !grid.HasLineOfSight(center, at))
                    reason = "no sight";
            }
            else if (dist <= 2)
            {
                reason = "out of reach";   // a burst or cone that stops short of it
            }

            if (reason != null)
            {
                unit.SetTargetable(true, BlockedRingColor);
                unit.SetBlockedReason(reason);
                _markedUnits.Add(unit);
            }
        }
    }

    // ── Shape helpers (mirrors of the selectors' geometry) ──────────────────

    private HashSet<Vector2I> ConeCoords(Vector2I origin, int dirIdx, int range)
    {
        var coords = new HashSet<Vector2I>();
        var forward = HexDirection.All[dirIdx];
        var leftDir = HexDirection.All[(dirIdx + 5) % 6];
        var rightDir = HexDirection.All[(dirIdx + 1) % 6];
        for (int step = 1; step <= range; step++)
        {
            var c = origin + forward * step;
            int spread = step - 1;
            for (int side = -spread; side <= spread; side++)
            {
                var tile = side == 0 ? c : (side < 0 ? c + leftDir * (-side) : c + rightDir * side);
                if (grid.GetTile(tile) != null)
                    coords.Add(tile);
            }
        }
        return coords;
    }

    private HashSet<Vector2I> LineCoords(Vector2I origin, int dirIdx, int length)
    {
        var coords = new HashSet<Vector2I>();
        var dir = HexDirection.All[dirIdx];
        for (int step = 1; step <= length; step++)
        {
            var c = origin + dir * step;
            var tile = grid.GetTile(c);
            if (tile == null)
                break;
            coords.Add(c);
            if (tile.BlocksLineOfSight)
                break;   // the line hits the wall and stops there
        }
        return coords;
    }
}
