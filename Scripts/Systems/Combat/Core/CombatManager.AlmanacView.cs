using Godot;
using System.Collections.Generic;
using System.Text;

// ============================================================
// CombatManager.AlmanacView.cs
//
// Purpose:        Makes the Chronomancer's Almanac (scheduled
//                 spells, GameState.Almanac) visible
//                 (class_identity_chronomancer_v1 §2c, "the Almanac
//                 UI shows each entry"). Two surfaces:
//                 • a list in the unit panel: each scheduled spell,
//                   when it lands, and where;
//                 • a marker on each target tile, so the board shows
//                   where the future is aimed.
//                 Also lists recurring foreseen strikes on a tile
//                 (delayed_damage: Foregone Conclusion), which live in
//                 ActiveEffects rather than the Almanac.
//                 Rebuilt only when the Almanac's content changes
//                 (a cheap signature compare each frame), and only by
//                 retexting persistent nodes: nothing is created or
//                 freed per change (see RegisterManager's lifetime rule).
//                 Maturity (§2c) is not built yet, so it is not shown.
// Layer:          Combat
// Collaborators:  GameState.Almanac / AlmanacEntry, CombatUI.SetAlmanac,
//                 HexTile.SetScheduleMark, ScheduleLeafEffect (labels)
// See:            docs/class_identity_chronomancer_v1.md §2c
// ============================================================

public partial class CombatManager
{
    private string _almanacSignature = null;
    private readonly HashSet<TileData> _almanacMarkedTiles = new();

    /// <summary>Repaints the Almanac list and tile markers when the Almanac changed.</summary>
    private void RefreshAlmanacView()
    {
        if (DragPayloadManager.IsDragging || GetViewport().GuiIsDragging())
            return;   // lifetime rule: no scene edits during a drag (see AimLines)
        var entries = State?.Almanac;
        var sig = new StringBuilder();
        if (entries != null)
        {
            foreach (var e in entries)
            {
                if (e == null || e.Hidden)
                    continue;
                var tile = AlmanacTile(e);
                sig.Append(e.Label).Append('|').Append(e.TurnsRemaining).Append('|').Append(e.MaturityBonus).Append('|')
                   .Append(tile != null ? tile.Axial.ToString() : "-").Append(';');
            }
        }
        if (State?.ActiveEffects != null)
        {
            foreach (var pe in State.ActiveEffects)
            {
                if (pe is DelayedDamageEffect dd && !dd.IsExpired)
                    sig.Append(dd.Label).Append('|').Append(dd.TurnsRemaining).Append('|')
                       .Append(dd.TargetCoord.ToString()).Append(';');
            }
        }
        foreach (var u in playerUnits)
        {
            if (u != null && IsInstanceValid(u) && u.Stats.IsAlive && u.AnchorCoord != null)
                sig.Append("A|").Append(u.GetInstanceId()).Append('|').Append(u.AnchorCoord.Value).Append('|')
                   .Append(u.AnchorTurnsRemaining).Append(';');
        }
        if (State?.PhaseTiles != null && State.PhaseTiles.Count > 0)
        {
            sig.Append("P|").Append(State.PhaseTileTurnsRemaining);
            foreach (var c in State.PhaseTiles)
                sig.Append('|').Append(c);
            sig.Append(';');
        }

        string s = sig.ToString();
        if (s == _almanacSignature)
            return;
        _almanacSignature = s;

        foreach (var t in _almanacMarkedTiles)
            t.TileView?.SetScheduleMark(null);
        _almanacMarkedTiles.Clear();

        if (string.IsNullOrEmpty(s))
        {
            combatUI?.SetAlmanac(null);
            return;
        }

        var text = new StringBuilder();
        var perTile = new Dictionary<TileData, List<string>>();
        foreach (var e in entries ?? new List<AlmanacEntry>())
        {
            if (e == null || e.Hidden)
                continue;
            string name = string.IsNullOrEmpty(e.Label) ? "Scheduled spell" : e.Label;
            string when = e.TurnsRemaining <= 1 ? "next turn" : $"in {e.TurnsRemaining} turns";
            var tile = AlmanacTile(e);
            var unit = AlmanacUnit(e);
            string where = unit != null && !unit.IsPlayerControlled ? $" on {unit.DisplayName}"
                         : tile != null ? $" at ({tile.Axial.X}, {tile.Axial.Y})"
                         : "";
            if (text.Length > 0)
                text.Append('\n');
            text.Append($"{name}: {when}{where}");
            if (e.MaturityBonus > 0)
                text.Append($", matured +{e.MaturityBonus} damage");

            if (tile != null)
            {
                if (!perTile.TryGetValue(tile, out var list))
                    perTile[tile] = list = new List<string>();
                list.Add($"{name.ToUpperInvariant()} {(e.TurnsRemaining <= 1 ? "NEXT TURN" : $"IN {e.TurnsRemaining}")}");
            }
        }

        // Recurring strikes on a tile (Foregone Conclusion: delayed_damage).
        if (State?.ActiveEffects != null)
        {
            foreach (var pe in State.ActiveEffects)
            {
                if (pe is not DelayedDamageEffect dd || dd.IsExpired)
                    continue;
                string name = string.IsNullOrEmpty(dd.Label) ? "Foreseen strike" : dd.Label;
                string times = dd.TurnsRemaining == 1 ? "once more" : $"{dd.TurnsRemaining} more times";
                if (text.Length > 0)
                    text.Append('\n');
                text.Append($"{name}: {dd.DamagePerTick} damage {times} at ({dd.TargetCoord.X}, {dd.TargetCoord.Y})");

                var tile = grid?.GetTile(dd.TargetCoord);
                if (tile != null)
                {
                    if (!perTile.TryGetValue(tile, out var list))
                        perTile[tile] = list = new List<string>();
                    list.Add($"{name.ToUpperInvariant()} {dd.DamagePerTick} x{dd.TurnsRemaining}");
                }
            }
        }

        // Marked positions (Temporal Anchor, Phase Anchor): the tile shows what it is,
        // whose it is, and how long it holds. Snap Back / Phase Step are on the action bar.
        void Mark(Vector2I at, string line)
        {
            var tile = grid?.GetTile(at);
            if (tile == null)
                return;
            if (!perTile.TryGetValue(tile, out var list))
                perTile[tile] = list = new List<string>();
            list.Add(line);
        }
        static string Holds(int turns) => turns >= 99 ? "" : turns <= 1 ? " (THIS TURN)" : $" ({turns} TURNS)";
        foreach (var u in playerUnits)
        {
            if (u != null && IsInstanceValid(u) && u.Stats.IsAlive && u.AnchorCoord != null)
                Mark(u.AnchorCoord.Value, $"ANCHOR: {u.DisplayName.ToUpperInvariant()}{Holds(u.AnchorTurnsRemaining)}");
        }
        if (State?.PhaseTiles != null)
        {
            foreach (var c in State.PhaseTiles)
                Mark(c, $"PHASE TILE{Holds(State.PhaseTileTurnsRemaining)}");
        }

        combatUI?.SetAlmanac(text.Length > 0 ? text.ToString() : null);
        foreach (var kv in perTile)
        {
            kv.Key.TileView?.SetScheduleMark(string.Join("\n", kv.Value));
            _almanacMarkedTiles.Add(kv.Key);
        }
    }

    /// <summary>The unit an entry was aimed at, if its first target is a living unit.</summary>
    private static Unit AlmanacUnit(AlmanacEntry e)
    {
        if (e?.Targets?.Items == null || e.Targets.Items.Count == 0)
            return null;
        return e.Targets.Items[0] is Unit u && IsInstanceValid(u) && u.Stats.IsAlive ? u : null;
    }

    /// <summary>The tile an entry lands on: its targeted tile, or where its targeted
    /// unit stands now. Null for untargeted entries.</summary>
    private static TileData AlmanacTile(AlmanacEntry e)
    {
        if (e?.Targets?.Items == null || e.Targets.Items.Count == 0)
            return null;
        return e.Targets.Items[0] switch
        {
            TileData td => td,
            Unit u when IsInstanceValid(u) && u.Stats.IsAlive => u.CurrentTile,
            HexTile ht => ht.Data,
            _ => null,
        };
    }
}
