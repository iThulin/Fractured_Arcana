using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.RemoteCast.cs  (partial of CombatManager)
//
// Purpose:        The lens in combat (campus_building_upgrades_design_v1
//                 §20). When the wizard is not on the board, the tray of any
//                 fielded ally shows "The Lens": pick a spell from the
//                 wizard's deck that deals no damage (RemoteCasting decides),
//                 then aim it from that ally. It is cast through the item
//                 seam (mana waived, the ally as the focus), costs the ally
//                 nothing, and uses one of the fight's remote casts (and 2
//                 Essence in a dive).
// Layer:          Combat rules (partial of CombatManager)
// Collaborators:  RemoteCasting.cs (rules, which spells), CombatManager.Items
//                 (BuildItemLegalTiles, ItemCanAim, the cast call),
//                 CombatManager.Maneuvers (tray, arming, click routing),
//                 GrimoireState (Essence)
// ============================================================

public partial class CombatManager
{
    private const string RemoteCardId = "lens:open";

    private int _remoteCastsUsed = 0;
    private CardHalf _armedRemote;
    private List<CardHalf> _remoteSpells;       // cached per fight
    private PopupMenu _remotePicker;
    private Unit _remotePickerFocus;
    private Unit _remoteFocus;                  // the ally an armed remote spell is aimed from

    /// <summary>Where the lens reaches this fight, from who is on the board.</summary>
    private RemoteCasting.Reach CurrentRemoteReach()
    {
        foreach (var u in playerUnits)
            if (u != null && IsInstanceValid(u) && u.CompanionId == "wizard" && !u.IsAwaitingArrival)
                return RemoteCasting.Reach.None;   // the wizard is here in person (fallen or not)
        if (IsCastleDefense && !_wizardArrived)
            return RemoteCasting.Reach.Defense;
        if (PlayerSession.ExpeditionRunKind == ExpeditionRunKind.Field)
            return PlayerSession.IsOnExpedition ? RemoteCasting.Reach.Dive : RemoteCasting.Reach.Defense;
        return RemoteCasting.Reach.None;
    }

    private List<CardHalf> RemoteSpells()
        => _remoteSpells ??= RemoteCasting.Spells(SaveManager.ActiveSave);

    private int RemoteCastsLeft()
        => Math.Max(0, RemoteCasting.CastsPerFight(SaveManager.ActiveSave) - _remoteCastsUsed);

    /// <summary>Essence the lens costs here: in a dive only, and only when the
    /// expedition keeps an Essence pool at all.</summary>
    private static int RemoteEssenceCost(RemoteCasting.Reach reach)
    {
        var g = SaveManager.ActiveSave?.Cycle?.Grimoire;
        return reach == RemoteCasting.Reach.Dive && g != null && g.EssenceMax > 0 ? RemoteCasting.EssenceCost : 0;
    }

    /// <summary>Why the lens cannot be used through <paramref name="u"/> now, or null.</summary>
    private string RemoteBlockReason(Unit u)
    {
        var reach = CurrentRemoteReach();
        string why = RemoteCasting.UnavailableReason(SaveManager.ActiveSave, reach);
        if (why != null)
            return why;
        if (currentPhase != CombatPhase.PlayerTurn || isInDeploymentPhase)
            return "Not your turn.";
        if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || !u.IsPlayerControlled
            || u.IsObjectiveWard || u.IsStructure || u.CurrentTile == null)
            return "The lens needs a living ally on the field to look through.";
        if (!u.CanAct())
            return $"{u.Name} cannot act, and the lens cannot look through them.";
        if (RemoteCastsLeft() <= 0)
            return "The lens is spent for this fight.";
        int essence = RemoteEssenceCost(reach);
        var g = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (essence > 0 && (g == null || g.EssenceCurrent < essence))
            return $"The lens draws {essence} Essence (the expedition has {g?.EssenceCurrent ?? 0}).";
        if (RemoteSpells().Count == 0)
            return "No spell in the wizard's deck can be cast from afar (a remote spell never deals damage).";
        return null;
    }

    /// <summary>The Lens tray card for the selected ally, or null when the wizard
    /// is on the board (they cast in person).</summary>
    private TechniqueCardView RemoteCardViewFor(Unit u)
    {
        var reach = CurrentRemoteReach();
        if (reach == RemoteCasting.Reach.None)
            return null;
        string why = RemoteBlockReason(u);
        int essence = RemoteEssenceCost(reach);
        return new TechniqueCardView
        {
            Id = RemoteCardId,
            Title = "The Lens",
            Cost = $"{RemoteCastsLeft()} left" + (essence > 0 ? $" · {essence} Essence" : "") + " · no AP",
            Text = "The wizard casts from the campus through this ally: a spell from their deck that deals no damage.",
            Role = "Remote",
            Enabled = why == null,
            Armed = _armedRemote != null,
            Tooltip = why ?? "Click to choose a spell, then aim it from this ally. Right-click or Esc to cancel.",
            IsItem = true,
        };
    }

    // ── Picking ────────────────────────────────────────────────────────────

    private void OpenRemotePicker(Unit u)
    {
        string why = RemoteBlockReason(u);
        if (why != null)
        {
            combatUI?.AppendActionLog(why);
            return;
        }
        if (_armedRemote != null)
        {
            DisarmManeuver();
            return;
        }
        if (_remotePicker == null || !IsInstanceValid(_remotePicker))
        {
            _remotePicker = new PopupMenu { Name = "RemotePicker" };
            _remotePicker.IdPressed += OnRemoteSpellPicked;
            AddChild(_remotePicker);
        }
        _remotePicker.Clear();
        var spells = RemoteSpells();
        for (int i = 0; i < spells.Count; i++)
            _remotePicker.AddItem($"{spells[i].Name}: {spells[i].RulesText}", i);
        _remotePickerFocus = u;
        _remotePicker.Position = (Vector2I)GetViewport().GetMousePosition();
        _remotePicker.ResetSize();
        _remotePicker.Popup();
    }

    private void OnRemoteSpellPicked(long id)
    {
        var u = _remotePickerFocus;
        _remotePickerFocus = null;
        var spells = RemoteSpells();
        if (u == null || !IsInstanceValid(u) || id < 0 || id >= spells.Count)
            return;
        var half = spells[(int)id];
        string why = RemoteBlockReason(u);
        if (why != null)
        {
            combatUI?.AppendActionLog(why);
            return;
        }

        // Self, Global and Area spells need no board click.
        if (half.Targeting == null || half.Targeting is SelectSelfTarget
            || half.Targeting is SelectGlobalTarget || half.Targeting is SelectAreaTarget)
        {
            var targets = new TargetSet();
            targets.Items.Add(Me);
            ResolveRemoteCast(u, half, targets);
            return;
        }

        DisarmAction(refresh: false);
        DisarmManeuver(refresh: false);
        ClearManeuverHighlight();
        BuildItemLegalTiles(u, half);
        if (_maneuverLegalTiles.Count == 0)
        {
            combatUI?.AppendActionLog($"The Lens ({half.Name}): nothing in reach of {u.Name}.");
            RefreshSelectedUnitUI();
            return;
        }
        _armedRemote = half;
        _remoteFocus = u;
        EnsureCastRenderers();
        _castZone.ShowOutline(new HashSet<Vector2I>(_maneuverLegalTiles), grid, ManeuverEnvelopeColor, ManeuverEnvelopeFill);
        foreach (var coord in _maneuverLegalTiles)
        {
            var occ = grid.GetTile(coord)?.Occupant;
            if (occ != null && occ.Stats.IsAlive)
            {
                occ.SetTargetable(true, ManeuverEnvelopeColor);
                _maneuverRingedUnits.Add(occ);
            }
        }
        combatUI?.SetHintText($"The Lens ({half.Name}): click a target. Right-click or Esc to cancel.");
        RefreshSelectedUnitUI();
    }

    /// <summary>Board click while a remote spell is armed. Returns true when consumed.</summary>
    private bool TryHandleRemoteClick(Unit clickedUnit, HexTile clickedTile)
    {
        if (_armedRemote == null)
            return false;
        var u = _remoteFocus;   // the tiles were measured from the focus, so cast from it
        var half = _armedRemote;
        if (u == null || !IsInstanceValid(u))
        { DisarmManeuver(); return true; }

        Vector2I? coord = clickedUnit?.CurrentTile?.Axial ?? clickedTile?.Axial;
        if (coord == null || !_maneuverLegalTiles.Contains(coord.Value))
        {
            combatUI?.AppendActionLog($"The Lens ({half.Name}): not a legal target. Right-click to cancel.");
            return true;
        }

        var targets = new TargetSet();
        switch (half.Targeting)
        {
            case SelectUnitTarget ut:
                var target = grid.GetTile(coord.Value)?.Occupant;
                if (target == null || !target.Stats.IsAlive)
                {
                    combatUI?.AppendActionLog($"The Lens ({half.Name}): no one there.");
                    return true;
                }
                targets.Items.Add(target);
                targets.Delivery = ut.delivery;
                break;
            case SelectTileTarget:
                var td = grid.GetTile(coord.Value);
                if (td == null)
                    return true;
                targets.Items.Add(td);
                break;
            default:
                targets.Items.Add(Me);
                break;
        }
        ResolveRemoteCast(u, half, targets);
        return true;
    }

    // ── Resolution ─────────────────────────────────────────────────────────

    /// <summary>Cast <paramref name="half"/> through <paramref name="u"/>. Mana is
    /// waived and the ally spends nothing; the fight's remote cast (and a dive's
    /// Essence) is the cost, refunded if the rules refuse the cast.</summary>
    private void ResolveRemoteCast(Unit u, CardHalf half, TargetSet targets)
    {
        _armedRemote = null;
        _remoteFocus = null;
        ClearManeuverHighlight();
        combatUI?.SetHintText("Select a unit, move, cast, then end turn.");

        string why = RemoteBlockReason(u);
        if (why != null)
        {
            combatUI?.AppendActionLog(why);
            RefreshSelectedUnitUI();
            return;
        }
        if (!CheckCastRequirements(half, targets, out string failReason))
        {
            combatUI?.AppendActionLog($"The Lens ({half.Name}): {failReason}");
            RefreshSelectedUnitUI();
            return;
        }

        var reach = CurrentRemoteReach();
        int essence = RemoteEssenceCost(reach);
        var grimoire = SaveManager.ActiveSave?.Cycle?.Grimoire;

        State.ActiveCasterUnit = u;
        State.ItemCastFree = true;
        bool ok = false;
        try
        {
            ok = Rules.TryCastWithTargets(half, State, Me, targets, null);
        }
        finally
        {
            State.ItemCastFree = false;
            if (!ok)
                State.ActiveCasterUnit = null;
        }
        if (!ok)
        {
            combatUI?.AppendActionLog($"The Lens ({half.Name}) could not be cast here.");
            RefreshSelectedUnitUI();
            return;
        }

        _remoteCastsUsed++;
        if (essence > 0 && grimoire != null)
            grimoire.EssenceCurrent = Math.Max(0, grimoire.EssenceCurrent - essence);
        combatUI?.AppendActionLog($"From the campus, the wizard casts {half.Name} through {u.Name}."
            + (essence > 0 ? $" ({essence} Essence)" : "")
            + $" {RemoteCastsLeft()} remote cast(s) left this fight.");
        if (!_priorityWindowOpen)
        {
            while (!State.Stack.IsEmpty)
                State.Resolver.ResolveTop(State);
        }
        State.ActiveCasterUnit = null;
        SaveManager.MarkDirty();

        u.RefreshHealthBar();
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        _pruneNeeded = true;
    }
}
