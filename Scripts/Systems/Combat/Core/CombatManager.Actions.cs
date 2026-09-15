using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.Actions.cs  (partial of CombatManager)
//
// Purpose:        The selected unit's action bar (2026-09-08). Until now
//                 every non-card action was a modifier click: plain click
//                 strike, Ctrl+click shove, Alt+click fire the station.
//                 Three hidden verbs is two too many. The bar lists what
//                 the selected unit can do right now with a cost, and a
//                 button ARMS the action: the next click on a target
//                 performs it. Plain click still strikes, and the modifier
//                 shortcuts still work for people who learned them.
//
//                 Actions come from the unit's state, not a registry: a
//                 martial has Strike, Shove and Brace; anyone manning a
//                 crew weapon has Fire <station>; anyone beside a lever or
//                 a breakable obstacle has Interact (the target says what
//                 it does); a dancing caster and its spirits have Swap.
//                 Stances keep their own row.
// Collaborators:  CombatUI (ActionRow, UnitActionRequested), TryMartialAttack,
//                 TryMartialShove, TryFireStation (CastleDefense), the
//                 hover preview in CastPreview (armed action picks the
//                 envelope shown).
// ============================================================

/// <summary>What a bar button does when armed.</summary>
public enum UnitAction { None, Strike, Shove, Station, Interact, Brace, Swap }

/// <summary>One button on the action bar.</summary>
public sealed class UnitActionDef
{
    public UnitAction Kind;
    public string Label = "";
    public string Tooltip = "";
    public bool Enabled = true;
    public bool Armed;
}

public partial class CombatManager
{
    private UnitAction _armedAction = UnitAction.None;

    /// <summary>The bar's contents for <paramref name="unit"/>, or empty when the
    /// unit has no non-card actions (casters off a station, enemies, wards).</summary>
    private List<UnitActionDef> ActionsFor(Unit unit)
    {
        var list = new List<UnitActionDef>();
        if (unit == null || !IsInstanceValid(unit) || !unit.IsPlayerControlled || !unit.Stats.IsAlive
            || unit.IsStructure || unit.IsObjectiveWard || unit.IsAwaitingArrival)
            return list;
        bool myTurn = currentPhase == CombatPhase.PlayerTurn && !isInDeploymentPhase;

        if (unit.IsMartial)
        {
            bool ranged = unit.AttackRange + (unit.ActiveStance?.AttackRangeBonus ?? 0) > 1;
            int cost = ranged ? MartialAPCosts.AttackRanged : MartialAPCosts.AttackMelee;
            list.Add(new UnitActionDef
            {
                Kind = UnitAction.Strike,
                Label = $"{(ranged ? "Shoot" : "Strike")} ({cost} AP)",
                Tooltip = $"{unit.AttackDamage} damage, range {unit.AttackRange + (unit.ActiveStance?.AttackRangeBonus ?? 0)}. Click a target. Plain click on an enemy does the same.",
                Enabled = myTurn && unit.CurrentActionPoints >= cost && unit.CanAct(),
                Armed = _armedAction == UnitAction.Strike,
            });
            list.Add(new UnitActionDef
            {
                Kind = UnitAction.Shove,
                Label = $"Shove ({MartialAPCosts.AttackMelee} AP)",
                Tooltip = $"Push an adjacent enemy one tile straight away. {BodyCheckCollision(unit)} damage if it hits something. Ctrl+click does the same.",
                Enabled = myTurn && unit.CurrentActionPoints >= MartialAPCosts.AttackMelee && unit.CanAct(),
                Armed = _armedAction == UnitAction.Shove,
            });
        }

        var interact = InteractTargetsFor(unit);
        if (interact.Count > 0)
        {
            string what = string.Join("; ", interact.ConvertAll(i => i.text));
            list.Add(new UnitActionDef
            {
                Kind = UnitAction.Interact,
                Label = $"Interact ({MartialAPCosts.AttackMelee} AP)",
                Tooltip = $"Adjacent: {what}. Click the lever or the wall. A plain click on a lever also works.",
                Enabled = myTurn && unit.CurrentActionPoints >= MartialAPCosts.AttackMelee && unit.CanAct(),
                Armed = _armedAction == UnitAction.Interact,
            });
        }

        if (unit.IsMartial)
        {
            list.Add(new UnitActionDef
            {
                Kind = UnitAction.Brace,
                Label = "Brace (all AP)",
                Tooltip = $"Spend the rest of this turn holding: +{BraceArmorBonus} armour until your next turn, cover armour refilled if standing in cover. Counts as acted.",
                Enabled = myTurn && unit.CurrentActionPoints >= 1 && unit.CanAct(),
                Armed = false,
            });
        }

        var dancer = DanceCaster();
        if (dancer != null && IsDanceEligible(unit, dancer))
        {
            list.Add(new UnitActionDef
            {
                Kind = UnitAction.Swap,
                Label = "Swap (free)",
                Tooltip = "The Dance: trade places with another dancer or any enemy. Shift+click does the same.",
                Enabled = myTurn,
                Armed = _armedAction == UnitAction.Swap,
            });
        }

        if (unit.StationWeapon is CastleStationSpec st)
        {
            bool spent = unit.StationShotsLeft <= 0;
            list.Add(new UnitActionDef
            {
                Kind = UnitAction.Station,
                Label = $"Fire {st.Label} ({st.Ap} AP)",
                Tooltip = $"{st.Damage} damage, range {st.Range}, throws the target {st.Push}. {st.Shots} shot(s) a round."
                          + (spent ? " Spent: reloads at your next turn." : "") + " Alt+click does the same.",
                Enabled = myTurn && !spent && unit.CurrentActionPoints >= st.Ap && unit.CanAct(),
                Armed = _armedAction == UnitAction.Station,
            });
        }
        return list;
    }

    /// <summary>A bar button was pressed: arm that action (or disarm it if it was
    /// armed). The next click on a target performs it.</summary>
    private void OnUnitActionRequested(int kindRaw)
    {
        var kind = (UnitAction)kindRaw;
        if (selectedUnit == null || currentPhase != CombatPhase.PlayerTurn)
            return;
        if (kind == UnitAction.Brace)
        {
            DisarmAction(refresh: false);
            TryBrace(selectedUnit);
            return;
        }
        _armedAction = _armedAction == kind ? UnitAction.None : kind;
        if (_armedAction != UnitAction.None)
        {
            string what = _armedAction switch
            {
                UnitAction.Strike => "Strike",
                UnitAction.Shove => "Shove",
                UnitAction.Station => $"Fire the {selectedUnit.StationWeapon?.Label ?? "station"}",
                UnitAction.Interact => "Interact",
                UnitAction.Swap => "Swap",
                _ => "",
            };
            combatUI?.SetHintText($"{what}: click a target. Right-click or Esc to cancel.");
        }
        else
            combatUI?.SetHintText("Select a unit, move, cast, then end turn.");
        RefreshSelectedUnitUI();
    }

    private void DisarmAction(bool refresh = true)
    {
        if (_armedAction == UnitAction.None)
            return;
        _armedAction = UnitAction.None;
        combatUI?.SetHintText("Select a unit, move, cast, then end turn.");
        if (refresh)
            RefreshSelectedUnitUI();
    }

    /// <summary>Route a click on an enemy through the armed action, the modifier
    /// shortcuts, or the default strike. Returns true when something was tried.</summary>
    private bool TryArmedOrDefaultAction(Unit target)
    {
        if (selectedUnit == null || target == null)
            return false;
        var kind = _armedAction;
        // A lever is the one target a plain click may interact with: it means one thing.
        if (kind == UnitAction.None && target.IsMapObject && target.MapObjectKind == "lever")
            kind = UnitAction.Interact;
        if (kind == UnitAction.None && Input.IsKeyPressed(Key.Shift) && DanceCaster() != null)
            kind = UnitAction.Swap;
        if (kind == UnitAction.None)
        {
            if (Input.IsKeyPressed(Key.Alt) && selectedUnit.StationWeapon != null)
                kind = UnitAction.Station;
            else if ((Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Meta)) && selectedUnit.IsMartial)
                kind = UnitAction.Shove;
            else if (selectedUnit.IsMartial)
                kind = UnitAction.Strike;
            else if (selectedUnit.StationWeapon != null)
                kind = UnitAction.Station;   // a caster on the ballista: the only thing a click can mean
            else
                return false;
        }
        switch (kind)
        {
            case UnitAction.Station:
                if (selectedUnit.StationWeapon == null) return false;
                TryFireStation(selectedUnit, target);
                break;
            case UnitAction.Shove:
                if (!selectedUnit.IsMartial) return false;
                TryMartialShove(selectedUnit, target);
                break;
            case UnitAction.Strike:
                if (!selectedUnit.IsMartial) return false;
                TryMartialAttack(selectedUnit, target);
                break;
            case UnitAction.Interact:
                if (!(target.IsMapObject && target.MapObjectKind == "lever")) return false;
                TryWorkLever(selectedUnit, target);
                break;
            case UnitAction.Swap:
                if (!TryDanceSwap(selectedUnit, target)) return false;
                break;
            default:
                return false;
        }
        DisarmAction();
        return true;
    }

    // ── Interact ──────────────────────────────────────────────────────────────

    /// <summary>What the unit could interact with from where it stands: levers
    /// beside it and breakable obstacles beside it, with the text the bar shows.</summary>
    private List<(string text, Vector2I at, Unit lever)> InteractTargetsFor(Unit unit)
    {
        var list = new List<(string, Vector2I, Unit)>();
        if (unit?.CurrentTile == null || grid == null)
            return list;
        foreach (var n in grid.GetNeighbors(unit.CurrentTile.Axial))
        {
            var t = grid.GetTile(n);
            if (t == null)
                continue;
            if (t.Occupant != null && t.Occupant.IsMapObject && t.Occupant.MapObjectKind == "lever" && t.Occupant.Stats.IsAlive)
            {
                var ev = LeverEventFor(t.Occupant);
                list.Add((ev != null ? LeverActionText(ev) : "pull the lever", n, t.Occupant));
            }
            else if (t.IsBlocked && t.ObstacleHp > 0)
            {
                string name = ObstacleCatalog.GetOrFallback(t.ObstacleKind)?.Label ?? t.ObstacleKind.Replace('_', ' ');
                list.Add(($"break the {name} ({t.ObstacleHp} hp, {BreakDamage(unit)} a blow)", n, null));
            }
        }
        return list;
    }

    private static int BreakDamage(Unit u) => Math.Max(2, u.AttackDamage);

    /// <summary>Interact on a tile: break the obstacle there (Interact must be armed;
    /// a plain click on a wall is a move, never a blow).</summary>
    private bool TryBreakObstacle(Unit unit, Vector2I at)
    {
        if (unit?.CurrentTile == null || grid == null)
            return false;
        var t = grid.GetTile(at);
        if (t == null || !t.IsBlocked || t.ObstacleHp <= 0)
        {
            combatUI?.AppendActionLog("Nothing to break there.");
            return false;
        }
        if (grid.Distance(unit.CurrentTile.Axial, at) != 1)
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} must stand beside it to break it.");
            return false;
        }
        if (!unit.CanAct())
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} is frozen!");
            return false;
        }
        if (!unit.TrySpendAP(MartialAPCosts.AttackMelee))
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} needs {MartialAPCosts.AttackMelee} AP to break it.");
            return false;
        }
        unit.Stats.HasActed = true;
        string name = ObstacleCatalog.GetOrFallback(t.ObstacleKind)?.Label ?? t.ObstacleKind;
        int dmg = BreakDamage(unit);
        combatUI?.AppendActionLog($"{unit.DisplayName} hammers the {name}: {dmg}.");
        grid.DamageObstacle(t, dmg, m => combatUI?.AppendActionLog(m));
        RefreshCoverMarkers();
        RefreshThreatTiles();
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        ClearMoveTiles();
        if (selectedUnit != null)
            ShowMoveTilesWithCost(selectedUnit);
        return true;
    }

    /// <summary>Interact on a lever: pull it (fires or wakes its event now) or, for
    /// hold and delay levers, work it so the boundary counts it as held.</summary>
    private void TryWorkLever(Unit unit, Unit lever)
    {
        if (unit?.CurrentTile == null || lever?.CurrentTile == null)
            return;
        var ev = LeverEventFor(lever);
        if (ev == null)
        {
            combatUI?.AppendActionLog("The lever is not connected to anything.");
            return;
        }
        if (grid.Distance(unit.CurrentTile.Axial, lever.CurrentTile.Axial) != 1)
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} must stand beside the lever.");
            return;
        }
        if (!unit.CanAct())
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} is frozen!");
            return;
        }
        if (!unit.TrySpendAP(MartialAPCosts.AttackMelee))
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} needs {MartialAPCosts.AttackMelee} AP to work the lever.");
            return;
        }
        unit.Stats.HasActed = true;
        if (ev.LeverMode.ToLowerInvariant() == "pull")
            PullLever(ev);
        else
        {
            ev.HeldByAction = true;
            combatUI?.AppendActionLog($"{unit.DisplayName} works the lever: {LeverActionText(ev)}.");
        }
        RefreshThreatTiles();
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
    }

    // ── Brace ─────────────────────────────────────────────────────────────────

    private const int BraceArmorBonus = 1;

    /// <summary>Spend the rest of the turn holding: armour up, cover armour refilled
    /// when in cover, and the unit counts as acted for the end-turn gate.</summary>
    private void TryBrace(Unit unit)
    {
        if (unit == null || !unit.IsMartial || unit.CurrentActionPoints < 1 || !unit.CanAct())
            return;
        unit.CurrentActionPoints = 0;
        unit.Stats.HasActed = true;
        unit.Stats.Armor += BraceArmorBonus;
        unit.BraceArmor += BraceArmorBonus;
        bool inCover = unit.CurrentTile != null && grid.HasAnyCover(unit.CurrentTile.Axial);
        if (inCover)
            unit.Stats.CoverArmor = Unit.CoverArmorPerTurn;
        unit.RefreshHealthBar();
        combatUI?.AppendActionLog($"{unit.DisplayName} braces: +{BraceArmorBonus} armour" + (inCover ? ", cover armour refilled." : "."));
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        ClearMoveTiles();
    }
}
