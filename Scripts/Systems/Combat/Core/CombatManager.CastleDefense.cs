using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.CastleDefense.cs  (partial of CombatManager)
//
// Purpose:        Runtime for "Defend the Castle" (castle_defense_v1,
//                 mobile fortress F6). Three things on top of the siege
//                 and protect machinery that already exist:
//                   1. The Castle Heart (the protect ward) spawns at the
//                      compiled heart tile, not at the spawn side.
//                   2. Stations: rampart tiles carrying an installed
//                      castle module. A unit standing on one at the
//                      turn boundaries gets the module's effect. No new
//                      verb: standing there is the action.
//                   3. The wizard arrives late: fielded normally (deck
//                      and all), then translocated off the board until
//                      the arrival round, when it steps out beside the
//                      Heart with translocation shock.
// Collaborators:  CastleDefenseCompiler / SiegeSpec (heart, stations),
//                 CastleModules (station specs), CombatManager.Objectives
//                 (protect ward), CombatManager.SiegeDoors (gate doors)
// See:            docs/castle_defense_v1.md
// ============================================================

public partial class CombatManager
{
    /// <summary>Set by the encounter router / debug launcher when this fight is a
    /// castle defence. Read by the spawn path and the turn boundaries.</summary>
    public static bool NextCombatIsCastleDefense;
    private bool _castleDefense;
    private Unit _castleWizard;
    private int _wizardArrivalRound = 0;    // 0 = present from the start
    private bool _wizardArrived = true;

    private const int BaseAmbushWizardDelay = 2;   // spec §13: arrives round 3

    private bool IsCastleDefense => _castleDefense && grid?.ActiveSiege?.Heart != null;

    /// <summary>Station spec on a tile, or null.</summary>
    private CastleStationSpec StationAt(Vector2I coord)
    {
        var siege = grid?.ActiveSiege;
        if (siege == null)
            return null;
        foreach (var (at, module) in siege.Stations)
            if (at == coord)
                return CastleModules.Get(module)?.Station;
        return null;
    }

    // ── Heart placement ──────────────────────────────────────────────────────

    /// <summary>Called from SpawnObjectiveWard when the recipe carries a heart tile:
    /// the ward stands THERE, not on a spawn slot. Returns the ward or null.</summary>
    private Unit SpawnWardAtHeart(UnitDefinition def)
    {
        var heartCoord = grid?.ActiveSiege?.Heart;
        if (heartCoord == null)
            return null;
        var tile = grid.GetTile(heartCoord.Value);
        if (tile == null || !tile.CanEnter(null))
        {
            // Nearest open courtyard tile.
            TileData best = null; int bestD = int.MaxValue;
            foreach (var kv in grid.Tiles)
                if (kv.Value.CanEnter(null) && grid.Distance(heartCoord.Value, kv.Key) < bestD)
                { bestD = grid.Distance(heartCoord.Value, kv.Key); best = kv.Value; }
            tile = best;
        }
        if (tile == null)
            return null;

        var ward = SpawnRegistryUnit(_objective.WardUnitId, tile, teamId: 0, isMidFightSummon: false);
        if (ward == null)
            return null;
        ward.IsStructure = false;   // the Heart is the ward, not a door: its death is the defeat
        ward.IsMartial = false;
        ward.MaxActionPoints = 0;
        ward.CurrentActionPoints = 0;
        ward.MoveRange = 0;
        ward.Name = "CastleHeart";
        return ward;
    }

    // ── Wizard arrival ───────────────────────────────────────────────────────

    /// <summary>After the party is fielded: pull the wizard off the board until
    /// the arrival round. The unit keeps its deck, hand, and roster slot; it has
    /// no tile, is hidden, and is unselectable until it steps out at the Heart.</summary>
    private void TranslocateWizardOut()
    {
        _castleDefense = NextCombatIsCastleDefense;
        NextCombatIsCastleDefense = false;
        if (!IsCastleDefense)
            return;

        int delay = BaseAmbushWizardDelay + CastleModules.AmbushDelayModifier()
                    + AmbushDelayFromCrew();
        delay = Math.Max(0, delay);
        _wizardArrivalRound = delay > 0 ? 1 + delay : 0;
        _wizardArrived = delay == 0;

        foreach (var u in playerUnits)
            if (u != null && u.CompanionId == "wizard")
            { _castleWizard = u; break; }

        if (_castleWizard == null || _wizardArrived)
            return;

        // Nobody to hold the walls (a debug launch with no crew, or a party of one):
        // the delay would leave the player with no unit to command. The wizard
        // stays, and the log says why.
        bool crewFielded = false;
        foreach (var u in playerUnits)
            if (u != null && u != _castleWizard && IsInstanceValid(u) && u.Stats.IsAlive
                && !u.IsStructure && !u.IsObjectiveWard && u.IsPlayerControlled)
            { crewFielded = true; break; }
        if (!crewFielded)
        {
            _wizardArrived = true;
            _wizardArrivalRound = 0;
            combatUI?.AppendActionLog("── No crew to hold the walls: the wizard is on the field from the start. ──");
            return;
        }

        _castleWizard.LiftFromBoard();
        _castleWizard.IsAwaitingArrival = true;
        combatUI?.AppendActionLog($"── The crew holds the walls alone. {_castleWizard.DisplayName} steps through the waystone on round {_wizardArrivalRound}. ──");
    }

    /// <summary>Wardroom crew station (CrewStations, F4): rounds off the delay.
    /// Read defensively; the expedition side owns it.</summary>
    private static int AmbushDelayFromCrew()
        => -Math.Max(0, PlayerSession.AmbushWizardDelayReduction);

    /// <summary>Round boundary: the wizard steps out at (or beside) the Heart with a
    /// one-round translocation shock: AP zeroed on arrival, so the arrival turn is a
    /// hand and a position, not a full turn. Called before the player turn starts.</summary>
    private void TryArriveWizard()
    {
        if (!IsCastleDefense || _wizardArrived || _castleWizard == null || !IsInstanceValid(_castleWizard))
            return;
        if (roundNumber < _wizardArrivalRound)
            return;

        var heart = grid.ActiveSiege.Heart.Value;
        TileData landing = null; int bestD = int.MaxValue;
        foreach (var kv in grid.Tiles)
            if (kv.Value.CanEnter(_castleWizard) && grid.Distance(heart, kv.Key) < bestD)
            { bestD = grid.Distance(heart, kv.Key); landing = kv.Value; }
        if (landing == null)
            return;   // try again next round

        _castleWizard.IsAwaitingArrival = false;
        _castleWizard.Visible = true;
        _castleWizard.PlaceOnTile(landing, MovementKind.Teleport);
        _castleWizard.Stats.HasActed = true;
        _castleWizard.CurrentActionPoints = 0;   // translocation shock
        _wizardArrived = true;
        combatUI?.AppendActionLog($"── {_castleWizard.DisplayName} steps through the waystone beside the Heart, still reeling. ──");
        // The hand appears with the wizard: if nothing else is selected, select it so
        // the deck that just became playable is the one on screen.
        if (selectedUnit == null || !IsInstanceValid(selectedUnit) || !selectedUnit.Stats.IsAlive)
            SelectUnit(_castleWizard);
        RefreshPlayerUnitBar();
        RefreshThreatTiles();
    }

    // ── Stations ─────────────────────────────────────────────────────────────

    /// <summary>Player turn start: apply standing bonuses to whoever mans a station.
    /// Ballista: attack range and damage while standing (cleared when the unit
    /// leaves, see ClearStationBonuses). Ward Lantern: shield to the keeper, cover
    /// armour to everyone in its glow.</summary>
    private void ApplyStationBonuses()
    {
        if (!IsCastleDefense)
            return;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            ClearStationBonuses(u);
            var st = StationAt(u.CurrentTile.Axial);
            if (st == null)
                continue;
            switch (st.Kind)
            {
                case "ballista":
                    if (u.IsMartial)
                    {
                        u.StationRangeBonus = st.RangeBonus;
                        u.StationDamageBonus = st.DamageBonus;
                        combatUI?.AppendActionLog($"{u.DisplayName} mans the {st.Label}: +{st.RangeBonus} range, +{st.DamageBonus} damage.");
                    }
                    break;
                case "ward_lantern":
                    if (st.Shield > 0)
                        u.Stats.Shield += st.Shield;
                    foreach (var ally in playerUnits)
                    {
                        if (ally == null || !IsInstanceValid(ally) || !ally.Stats.IsAlive || ally.CurrentTile == null)
                            continue;
                        if (grid.Distance(u.CurrentTile.Axial, ally.CurrentTile.Axial) <= st.Radius)
                            ally.Stats.CoverArmor += st.CoverBonus;
                    }
                    combatUI?.AppendActionLog($"{u.DisplayName} keeps the {st.Label}: the walls hold firmer within {st.Radius}.");
                    break;
            }
        }
    }

    private static void ClearStationBonuses(Unit u)
    {
        u.StationRangeBonus = 0;
        u.StationDamageBonus = 0;
    }

    /// <summary>Round boundary (after the enemy phase): stations that act on the
    /// castle rather than the keeper. Repair Winch mends the most damaged door or
    /// the Heart. Brazier Rack sets the three tiles before the gate alight.</summary>
    private void RunStationRoundEffects()
    {
        if (!IsCastleDefense)
            return;
        var siege = grid.ActiveSiege;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            var st = StationAt(u.CurrentTile.Axial);
            if (st == null)
                continue;
            switch (st.Kind)
            {
                case "repair_winch":
                {
                    Unit target = null; int worst = 0;
                    foreach (var p in playerUnits)
                    {
                        if (p == null || !IsInstanceValid(p) || !p.Stats.IsAlive) continue;
                        if (!p.IsStructure && !p.IsObjectiveWard) continue;
                        int missing = p.Stats.MaxHealth - p.Stats.Health;
                        if (missing > worst) { worst = missing; target = p; }
                    }
                    if (target != null)
                    {
                        target.Stats.Health = Math.Min(target.Stats.MaxHealth, target.Stats.Health + st.Repair);
                        target.RefreshHealthBar();
                        combatUI?.AppendActionLog($"{u.DisplayName} works the {st.Label}: {target.DisplayName} mended {st.Repair}.");
                    }
                    break;
                }
                case "brazier_rack":
                {
                    int lit = 0;
                    foreach (var g in siege.GateGap)
                    {
                        var dir = ForcedMove.StepAwayFrom(grid, siege.Heart.Value, g);
                        var outside = grid.GetTile(g + dir);
                        if (outside != null && outside.IsWalkable && !outside.IsBlocked && outside.TerrainType != TileTerrainType.Water)
                        {
                            TileEntryReactions.ImbueTile(outside, TileElementType.Fire);
                            lit++;
                        }
                    }
                    if (lit > 0)
                        combatUI?.AppendActionLog($"{u.DisplayName} tips the {st.Label}: fire before the gate ({lit} tile(s)).");
                    break;
                }
            }
        }
    }
}
