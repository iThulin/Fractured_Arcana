using Godot;
using System;

// ============================================================
// CombatManager.Anchors.cs  (partial of CombatManager)
//
// Purpose:        The player-side controls for the Chronomancer's
//                 marked positions (2026-10-03). Temporal Anchor and
//                 Phase Anchor stored a tile but nothing ever let the
//                 player use it: the teleport effects existed and no
//                 card, button, or click reached them.
//                 • Snap Back: a free action-bar button, once per turn,
//                   while the selected unit has an anchor. Teleports it
//                   to the anchor and pays the anchor's bonuses (heal,
//                   shield, Foresight). A Fixed anchor (Temporal Anchor
//                   tier 4) also catches the first lethal hit.
//                 • Phase Step: a free action-bar button for the unit
//                   that made the Phase tiles, once per turn. Arms a
//                   click: click a Phase tile to teleport there.
//                 The tiles themselves are marked by the Almanac view
//                 (CombatManager.AlmanacView.cs).
// Layer:          Combat
// Collaborators:  CombatManager.Actions.cs (bar buttons, armed click),
//                 SetAnchorEffect / CreatePhaseTilesEffect (state),
//                 Unit.AnchorLethalSaved (lethal hook), FateAttunement
// ============================================================

public partial class CombatManager
{
    private bool _anchorHookInstalled;

    private void InstallAnchorHooks()
    {
        if (_anchorHookInstalled)
            return;
        Unit.AnchorLethalSaved += OnAnchorLethalSaved;
        _anchorHookInstalled = true;
    }

    private void UninstallAnchorHooks()
    {
        if (!_anchorHookInstalled)
            return;
        Unit.AnchorLethalSaved -= OnAnchorLethalSaved;
        _anchorHookInstalled = false;
    }

    // ── Snap Back ───────────────────────────────────────────────────────

    /// <summary>Null when <paramref name="unit"/> may snap back now; otherwise why not.</summary>
    private string WhyNoSnap(Unit unit)
    {
        if (unit?.AnchorCoord == null)
            return "no anchor";
        if (unit.AnchorSnapUsedRound == roundNumber)
            return "already used this turn";
        if (unit.CurrentTile != null && unit.CurrentTile.Axial == unit.AnchorCoord.Value)
            return "already standing on it";
        var tile = grid?.GetTile(unit.AnchorCoord.Value);
        if (tile == null)
            return "the anchor tile is gone";
        if (tile.Occupant != null && tile.Occupant != unit)
            return $"{tile.Occupant.DisplayName} is standing on it";
        if (!tile.CanEnter(unit))
            return "the anchor tile is blocked";
        return null;
    }

    private string SnapBonusText(Unit unit)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (unit.AnchorSnapHeal > 0) parts.Add($"heal {unit.AnchorSnapHeal}");
        if (unit.AnchorSnapShield > 0) parts.Add($"gain {unit.AnchorSnapShield} shield");
        if (unit.AnchorSnapForesight > 0) parts.Add($"gain {unit.AnchorSnapForesight} Foresight");
        return parts.Count == 0 ? "" : " Then " + string.Join(", ", parts) + ".";
    }

    private string SnapTooltip(Unit unit)
    {
        var at = unit.AnchorCoord.Value;
        string life = unit.AnchorTurnsRemaining >= 99 ? "for the rest of the fight"
                    : unit.AnchorTurnsRemaining <= 1 ? "until your next turn"
                    : $"for {unit.AnchorTurnsRemaining} more turns";
        string why = WhyNoSnap(unit);
        return $"Teleport to your anchor at ({at.X}, {at.Y}). Free, once per turn. The anchor holds {life}."
               + SnapBonusText(unit)
               + (unit.AnchorLethalSave ? " The first hit that would kill you snaps you back at 1 HP instead." : "")
               + (why != null ? $" Not now: {why}." : "");
    }

    /// <summary>Snap Back button: teleport to the anchor and pay its bonuses.</summary>
    private bool TrySnapToAnchor(Unit unit)
    {
        string why = WhyNoSnap(unit);
        if (why != null)
        {
            combatUI?.AppendActionLog($"{unit?.Name ?? "Unit"} can't snap back: {why}.");
            return false;
        }

        var tile = grid.GetTile(unit.AnchorCoord.Value);
        unit.PlaceOnTile(tile, MovementKind.Teleport);
        unit.AnchorSnapUsedRound = roundNumber;
        ApplySnapBonuses(unit);

        combatUI?.AppendActionLog($"{unit.Name} snaps back to the anchor.{SnapBonusText(unit)}");
        GD.Print($"[Anchor] {unit.Name} snapped back to {tile.Axial}.");
        AfterChronoTeleport();
        return true;
    }

    private void ApplySnapBonuses(Unit unit)
    {
        if (unit.AnchorSnapHeal > 0)
            unit.Stats.Health = Math.Min(unit.Stats.MaxHealth, unit.Stats.Health + unit.AnchorSnapHeal);
        if (unit.AnchorSnapShield > 0)
            unit.Stats.Shield += unit.AnchorSnapShield;
        if (unit.AnchorSnapForesight > 0 && unit.Attunement is FateAttunement fate)
            fate.GainCharges(unit.AnchorSnapForesight);
        unit.RefreshHealthBar();
    }

    /// <summary>A Fixed anchor caught a lethal hit (Unit set HP to 1 and spent the
    /// save). Teleport home if the tile is free; the 1 HP stands either way.</summary>
    private void OnAnchorLethalSaved(Unit unit)
    {
        if (unit == null || !IsInstanceValid(unit) || unit.AnchorCoord == null)
            return;
        var tile = grid?.GetTile(unit.AnchorCoord.Value);
        bool moved = false;
        if (tile != null && (tile.Occupant == null || tile.Occupant == unit) && (tile.Occupant == unit || tile.CanEnter(unit)))
        {
            if (unit.CurrentTile != tile)
                unit.PlaceOnTile(tile, MovementKind.Teleport);
            moved = true;
        }
        combatUI?.AppendActionLog(moved
            ? $"{unit.Name}'s anchor holds: snapped back at 1 HP."
            : $"{unit.Name}'s anchor holds at 1 HP, but the anchor tile is taken.");
        GD.Print($"[Anchor] Lethal save for {unit.Name} (moved={moved}).");
        AfterChronoTeleport();
    }

    // ── Phase Step ──────────────────────────────────────────────────────

    private bool HasPhaseStep(Unit unit) =>
        unit != null && State?.PhaseTiles != null && State.PhaseTiles.Count > 0
        && State.PhaseTileOwner == unit;

    private string WhyNoPhaseStep(Unit unit)
    {
        if (!HasPhaseStep(unit))
            return "no Phase tiles";
        if (unit.PhaseStepUsedRound == roundNumber)
            return "already used this turn";
        foreach (var c in State.PhaseTiles)
        {
            var t = grid?.GetTile(c);
            if (t != null && t.Occupant == null && t.CanEnter(unit))
                return null;
        }
        return "every Phase tile is taken";
    }

    private string PhaseTooltip(Unit unit)
    {
        string life = State.PhaseTileTurnsRemaining >= 99 ? "for the rest of the fight"
                    : State.PhaseTileTurnsRemaining <= 1 ? "until your next turn"
                    : $"for {State.PhaseTileTurnsRemaining} more turns";
        string why = WhyNoPhaseStep(unit);
        return $"Click one of your {State.PhaseTiles.Count} Phase tiles to teleport there. Free, once per turn. They last {life}."
               + (State.PhaseStepForesight > 0 ? $" Each step grants {State.PhaseStepForesight} Foresight." : "")
               + (why != null ? $" Not now: {why}." : "");
    }

    /// <summary>Armed Phase Step: a click on <paramref name="axial"/>.</summary>
    private bool TryPhaseStep(Unit unit, Vector2I axial)
    {
        string why = WhyNoPhaseStep(unit);
        if (why != null)
        {
            combatUI?.AppendActionLog($"{unit?.Name ?? "Unit"} can't phase step: {why}.");
            return false;
        }
        if (!State.PhaseTiles.Contains(axial))
        {
            combatUI?.SetHintText("Phase Step: click one of the marked Phase tiles. Right-click or Esc to cancel.");
            return false;
        }
        var tile = grid.GetTile(axial);
        if (tile == null || tile.Occupant != null || !tile.CanEnter(unit))
        {
            combatUI?.AppendActionLog("That Phase tile is taken.");
            return false;
        }

        unit.PlaceOnTile(tile, MovementKind.Teleport);
        unit.PhaseStepUsedRound = roundNumber;
        if (State.PhaseStepForesight > 0 && unit.Attunement is FateAttunement fate)
            fate.GainCharges(State.PhaseStepForesight);

        combatUI?.AppendActionLog($"{unit.Name} steps through the Phase network to ({axial.X}, {axial.Y})."
            + (State.PhaseStepForesight > 0 ? $" Gains {State.PhaseStepForesight} Foresight." : ""));
        GD.Print($"[PhaseTile] {unit.Name} phase-stepped to {axial}.");
        AfterChronoTeleport();
        return true;
    }

    /// <summary>Shared refresh after a free teleport (the Dance does the same).</summary>
    private void AfterChronoTeleport()
    {
        ClearMoveTiles();
        if (selectedUnit != null && currentPhase == CombatPhase.PlayerTurn)
            ShowMoveTilesWithCost(selectedUnit);
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        RefreshThreatTiles();
    }
}
