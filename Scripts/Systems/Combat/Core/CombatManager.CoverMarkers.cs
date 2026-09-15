using Godot;
using System;

// ============================================================
// CombatManager.CoverMarkers.cs
//
// Purpose:        Per-enemy COVER / FLANKED markers relative to the
//                 selected player unit, so the player can read at a
//                 glance which enemy a bolt reaches cleanly and which
//                 one needs a flank, an arc, or a burst. Refreshed on
//                 selection, on any move, and with the roster.
// Layer:          Systems / Combat / Core (partial of CombatManager)
// Collaborators:  HexGridManager.Cover.cs (CoverBetween), Unit
//                 (SetCoverMarker / ClearCoverMarker)
// See:            docs/cover_and_zoc_v1.md §10
// ============================================================

public partial class CombatManager
{
    private static readonly Color FlankedColor = new(0.95f, 0.45f, 0.30f);
    private static readonly Color LowCoverColor = new(0.80f, 0.78f, 0.62f);
    private static readonly Color HighCoverColor = new(0.62f, 0.64f, 0.70f);

    /// <summary>Rewrite every living enemy's marker against the selected unit's
    /// tile. No selection, or a selection with no tile, clears them all. Adjacent
    /// enemies get no marker: cover never applies to a melee swing.</summary>
    private void RefreshCoverMarkers()
    {
        if (grid == null)
            return;

        var from = selectedUnit != null && IsInstanceValid(selectedUnit) && selectedUnit.Stats.IsAlive
            ? selectedUnit.CurrentTile?.Axial
            : null;

        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e))
                continue;
            if (from == null || !e.Stats.IsAlive || e.CurrentTile == null || e.IsMapObject
                || currentPhase != CombatPhase.PlayerTurn)
            {
                e.ClearCoverMarker();
                continue;
            }

            var to = e.CurrentTile.Axial;
            if (grid.Distance(from.Value, to) <= 1)
            {
                e.ClearCoverMarker();
                continue;
            }

            switch (grid.CoverBetween(to, from.Value))
            {
                case CoverKind.High:
                    e.SetCoverMarker("FULL COVER", HighCoverColor);
                    break;
                case CoverKind.Low:
                    e.SetCoverMarker("COVER", LowCoverColor);
                    break;
                default:
                    // Only worth saying when the enemy HAS cover somewhere: an enemy
                    // in the open is not "flanked", it is just in the open.
                    if (grid.HasAnyCover(to))
                        e.SetCoverMarker("FLANKED", FlankedColor);
                    else
                        e.ClearCoverMarker();
                    break;
            }
        }
    }
}
