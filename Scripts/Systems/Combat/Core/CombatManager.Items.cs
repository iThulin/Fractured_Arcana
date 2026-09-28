using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatManager.Items.cs
//
// Purpose:        Pinned item actions in combat (Martial Maneuvers &
//                 Edge spec v1 §11, M6): the selected unit's belt
//                 (potions, scrolls, whetstones, spellglass) and its
//                 wand, shown as cards on the right of the action tray.
//                 Every pinned item costs 1 AP (§11c). A wand spends a
//                 charge; a martial firing one pays 1 Edge on top (R11).
//                 Spellglass shatters on use. Bound spells cast through
//                 RulesManager at printed values with the mana cost
//                 waived (GameState.ItemCastFree), so a 0-mana martial
//                 can fire a wand.
// Layer:          Combat rules (partial of CombatManager)
// Collaborators:  ItemDefinition.cs / ItemDatabase.cs (ActiveKind,
//                 BoundCardId, Charges, belts), TechniqueTray.cs (display),
//                 CombatManager.Maneuvers.cs (shares the arming machinery:
//                 legal tiles, rings, click routing), RulesManager
//                 (TryCastWithTargets), CardDatabase (blueprints)
// ============================================================

public partial class CombatManager
{
    /// <summary>Tray card ids for items are "item:<instanceId>", so one tray
    /// handler routes both technique and item cards.</summary>
    private const string ItemCardPrefix = "item:";

    /// <summary>The armed item card, mutually exclusive with _armedManeuver.</summary>
    private ItemInstance _armedItem;
    private ItemDefinition _armedItemDef;
    private CardHalf _armedItemHalf;

    /// <summary>The loadout key CombatManager already uses for equipment.</summary>
    private static string LoadoutKeyFor(Unit u)
        => string.IsNullOrEmpty(u?.CompanionId) ? "wizard" : u.CompanionId;

    // ── What a unit carries ──────────────────────────────────────────────────

    /// <summary>Belt items in slot order, then the wand if the trinket is one.</summary>
    private List<(ItemInstance inst, ItemDefinition def)> PinnedItemsFor(Unit u)
    {
        var list = new List<(ItemInstance, ItemDefinition)>();
        var armory = SaveManager.ActiveSave?.Armory;
        if (u == null || armory == null || !u.IsPlayerControlled || u.IsObjectiveWard || u.IsStructure)
            return list;
        string key = LoadoutKeyFor(u);
        foreach (var inst in armory.GetBelt(key))
        {
            var def = ItemDatabase.Get(inst.DefinitionId);
            if (def != null)
                list.Add((inst, def));
        }
        var trinketId = armory.GetLoadout(key).TrinketInstanceId;
        if (!string.IsNullOrEmpty(trinketId))
        {
            var inst = armory.GetInstance(trinketId);
            var def = inst != null ? ItemDatabase.Get(inst.DefinitionId) : null;
            if (def != null && def.IsWand)
                list.Add((inst, def));
        }
        return list;
    }

    /// <summary>Card view for one pinned item, costs and greying resolved for <paramref name="u"/>.</summary>
    private TechniqueCardView ItemCardView(Unit u, ItemInstance inst, ItemDefinition def)
    {
        string why = ItemBlockReason(u, inst, def);
        bool martialWand = def.IsWand && EdgeRules.UsesEdge(u);
        string cost = def.IsWand
            ? $"{MartialAPCosts.UseItem} AP · charge {Math.Max(0, inst.Charges)}/{def.MaxCharges}" + (martialWand ? " · 1 Edge" : "")
            : $"{MartialAPCosts.UseItem} AP · spent on use";
        string text = def.Description;
        string bound = inst.EffectiveBoundCardId(def);
        if (!string.IsNullOrEmpty(bound))
        {
            var half = BoundHalf(bound);
            if (half != null)
                text = $"{half.Name}: {half.RulesText}";
        }
        return new TechniqueCardView
        {
            Id = ItemCardPrefix + inst.InstanceId,
            Title = inst.Name,
            Cost = cost,
            Text = text,
            Role = def.IsWand ? "Wand" : def.IsSpellglass ? "Spellglass" : def.ConsumeKind == "scroll" ? "Scroll" : "Belt",
            Enabled = why == null,
            Armed = _armedItem == inst,
            Tooltip = why ?? (def.IsWand || def.IsSpellglass
                ? $"{inst.Name}: click, then click a target if the spell needs one."
                : $"{inst.Name}: click to use on {u.Name}."),
            IsItem = true,
        };
    }

    /// <summary>The top half of the bound card, or null when the id is unknown.</summary>
    private static CardHalf BoundHalf(string blueprintId)
    {
        if (string.IsNullOrEmpty(blueprintId))
            return null;
        var bp = CardDatabase.Blueprints.Find(b => b.Id == blueprintId) ?? CardDatabase.GetByName(blueprintId);
        if (bp == null)
            return null;
        var card = CardDatabase.Instantiate(bp);
        return card?.TopHalf;
    }

    /// <summary>Selectors the item path can aim without drag-and-drop. Anything
    /// else is refused at the card, with the reason shown.</summary>
    public static bool ItemCanAim(ITargetSelector t)
        => t == null || t is SelectSelfTarget || t is SelectGlobalTarget || t is SelectAreaTarget
           || t is SelectUnitTarget || t is SelectTileTarget;

    private string ItemBlockReason(Unit u, ItemInstance inst, ItemDefinition def)
    {
        if (currentPhase != CombatPhase.PlayerTurn || isInDeploymentPhase)
            return "Not your turn.";
        if (!u.CanAct())
            return $"{u.Name} cannot act.";
        if (u.CurrentActionPoints < MartialAPCosts.UseItem)
            return $"Needs {MartialAPCosts.UseItem} AP (has {u.CurrentActionPoints}).";

        if (def.IsWand)
        {
            if (inst.Charges <= 0)
                return "No charges left. Wands refill at campus.";
            if (EdgeRules.UsesEdge(u) && u.Edge < 1)
                return "A martial pays 1 Edge to fire a wand (has 0).";
        }
        string bound = inst.EffectiveBoundCardId(def);
        if (def.IsWand || def.IsSpellglass)
        {
            var half = BoundHalf(bound);
            if (half == null)
                return $"Bound spell '{bound}' is not in the card database.";
            if (!ItemCanAim(half.Targeting))
                return $"{half.Name} needs a targeting shape items cannot aim yet ({half.Targeting.GetType().Name}).";
        }
        else if (def.IsConsumable)
        {
            if (def.ConsumeKind == "scroll")
            {
                if (_scrollReadThisTurn)
                    return "The party's one scroll this turn is spent.";
            }
            else if (u.HasUsedConsumableThisTurn)
                return $"{u.Name} has already drunk this turn.";
            if (def.ConsumeEffect == "edge" && !EdgeRules.UsesEdge(u))
                return "Only a classed martial can use this.";
        }
        return null;
    }

    // ── Tray entry ───────────────────────────────────────────────────────────

    /// <summary>Route an item card press. Returns true when the id was an item id.</summary>
    private bool TryHandleItemCardPressed(string cardId)
    {
        if (string.IsNullOrEmpty(cardId) || !cardId.StartsWith(ItemCardPrefix, StringComparison.Ordinal))
            return false;
        string instanceId = cardId.Substring(ItemCardPrefix.Length);
        var u = selectedUnit;
        var armory = SaveManager.ActiveSave?.Armory;
        if (u == null || armory == null || currentPhase != CombatPhase.PlayerTurn)
            return true;
        var inst = armory.GetInstance(instanceId);
        var def = inst != null ? ItemDatabase.Get(inst.DefinitionId) : null;
        if (inst == null || def == null)
            return true;

        if (_armedItem == inst)
        {
            DisarmManeuver();
            return true;
        }
        string why = ItemBlockReason(u, inst, def);
        if (why != null)
        {
            combatUI?.AppendActionLog(why);
            return true;
        }

        // Plain consumables resolve on the spot.
        if (!def.IsWand && !def.IsSpellglass)
        {
            UsePinnedConsumable(u, inst, def);
            return true;
        }

        var half = BoundHalf(inst.EffectiveBoundCardId(def));
        if (half == null)
            return true;

        // Self, Global and Area spells need no board click.
        if (half.Targeting == null || half.Targeting is SelectSelfTarget
            || half.Targeting is SelectGlobalTarget || half.Targeting is SelectAreaTarget)
        {
            var targets = new TargetSet();
            targets.Items.Add(Me);
            ResolveItemCast(u, inst, def, half, targets);
            return true;
        }

        ArmItem(u, inst, def, half);
        return true;
    }

    private void ArmItem(Unit u, ItemInstance inst, ItemDefinition def, CardHalf half)
    {
        DisarmAction(refresh: false);
        DisarmManeuver(refresh: false);
        ClearManeuverHighlight();
        _armedItem = inst;
        _armedItemDef = def;
        _armedItemHalf = half;

        BuildItemLegalTiles(u, half);
        if (_maneuverLegalTiles.Count == 0)
        {
            combatUI?.AppendActionLog($"{inst.Name}: nothing in reach for {half.Name}.");
            ClearArmedItem();
            RefreshSelectedUnitUI();
            return;
        }

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
        combatUI?.SetHintText($"{inst.Name} ({half.Name}): click a target. Right-click or Esc to cancel.");
        RefreshSelectedUnitUI();
    }

    private void ClearArmedItem()
    {
        _armedItem = null;
        _armedItemDef = null;
        _armedItemHalf = null;
    }

    /// <summary>Legal tiles for a bound spell's selector, mirroring the drop
    /// path's checks for SelectUnitTarget (range, LOS, cover, side) and
    /// SelectTileTarget (range).</summary>
    private void BuildItemLegalTiles(Unit u, CardHalf half)
    {
        _maneuverLegalTiles.Clear();
        if (u?.CurrentTile == null || grid == null || half == null)
            return;
        var origin = u.CurrentTile.Axial;

        switch (half.Targeting)
        {
            case SelectUnitTarget ut:
                foreach (var kv in grid.Tiles)
                {
                    var occ = kv.Value?.Occupant;
                    if (occ == null || !occ.Stats.IsAlive || occ.IsMapObject)
                        continue;
                    int dist = grid.Distance(origin, kv.Key);
                    if (dist > ut.range)
                        continue;
                    if (ut.enemyOnly && occ.TeamId == u.TeamId)
                        continue;
                    if (ut.friendlyOnly && occ.TeamId != u.TeamId)
                        continue;
                    if (ut.los && dist > 0 && !grid.HasLineOfSight(origin, kv.Key))
                        continue;
                    if (dist > 0 && ut.BlockedByCover(grid, origin, kv.Key))
                        continue;
                    _maneuverLegalTiles.Add(kv.Key);
                }
                break;

            case SelectTileTarget tt:
                foreach (var kv in grid.Tiles)
                    if (grid.Distance(origin, kv.Key) <= tt.range)
                        _maneuverLegalTiles.Add(kv.Key);
                break;
        }
    }

    /// <summary>Board click while an item is armed. Returns true when consumed.</summary>
    private bool TryHandleItemClick(Unit clickedUnit, HexTile clickedTile)
    {
        if (_armedItem == null)
            return false;
        var u = selectedUnit;
        if (u == null || !IsInstanceValid(u) || _armedItemHalf == null)
        { DisarmManeuver(); return true; }

        Vector2I? coord = clickedUnit?.CurrentTile?.Axial ?? clickedTile?.Axial;
        if (coord == null || !_maneuverLegalTiles.Contains(coord.Value))
        {
            combatUI?.AppendActionLog($"{_armedItem.Name}: not a legal target. Right-click to cancel.");
            return true;
        }

        var targets = new TargetSet();
        switch (_armedItemHalf.Targeting)
        {
            case SelectUnitTarget ut:
                var target = grid.GetTile(coord.Value)?.Occupant;
                if (target == null || !target.Stats.IsAlive)
                {
                    combatUI?.AppendActionLog($"{_armedItem.Name}: no one there.");
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

        ResolveItemCast(u, _armedItem, _armedItemDef, _armedItemHalf, targets);
        return true;
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    /// <summary>A potion, scroll or whetstone from the belt: 1 AP, then the
    /// consumable's effect exactly as the satchel applies it.</summary>
    private void UsePinnedConsumable(Unit u, ItemInstance inst, ItemDefinition def)
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;
        if (!u.TrySpendAP(MartialAPCosts.UseItem))
        {
            combatUI?.AppendActionLog($"{u.Name} needs {MartialAPCosts.UseItem} AP to use {inst.Name}.");
            return;
        }
        if (!ApplyConsumableEffect(u, def, out string line))
        {
            u.CurrentActionPoints += MartialAPCosts.UseItem;
            return;
        }
        FinishConsumable(u, inst, def, line);
    }

    /// <summary>Casts the bound spell. Pays 1 AP, a charge or the glass, and a
    /// martial's 1 Edge for a wand. Mana is waived (GameState.ItemCastFree); the
    /// item is the cost. Everything is refunded if the rules refuse the cast.</summary>
    private void ResolveItemCast(Unit u, ItemInstance inst, ItemDefinition def, CardHalf half, TargetSet targets)
    {
        var save = SaveManager.ActiveSave;
        ClearArmedItem();
        ClearManeuverHighlight();
        combatUI?.SetHintText("Select a unit, move, cast, then end turn.");
        if (save == null || u == null || !IsInstanceValid(u))
            return;

        string why = ItemBlockReason(u, inst, def);
        if (why != null)
        {
            combatUI?.AppendActionLog(why);
            RefreshSelectedUnitUI();
            return;
        }
        if (!CheckCastRequirements(half, targets, out string failReason))
        {
            combatUI?.AppendActionLog($"{inst.Name}: {failReason}");
            RefreshSelectedUnitUI();
            return;
        }

        int edgeBefore = u.Edge;
        bool martialWand = def.IsWand && EdgeRules.UsesEdge(u);
        if (!u.TrySpendAP(MartialAPCosts.UseItem))
        {
            combatUI?.AppendActionLog($"{u.Name} needs {MartialAPCosts.UseItem} AP to use {inst.Name}.");
            return;
        }
        if (martialWand && !EdgeRules.TrySpend(u, 1))
        {
            u.CurrentActionPoints += MartialAPCosts.UseItem;
            combatUI?.AppendActionLog($"{u.Name} needs 1 Edge to fire {inst.Name}.");
            return;
        }

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
                State.ActiveCasterUnit = null;   // a refused or thrown cast leaves no pin behind
        }

        if (!ok)
        {
            u.CurrentActionPoints += MartialAPCosts.UseItem;
            if (martialWand)
                EdgeRules.Refund(u, 1);
            combatUI?.AppendActionLog($"{inst.Name}: {half.Name} could not be cast here.");
            RefreshSelectedUnitUI();
            return;
        }

        combatUI?.AppendActionLog($"{u.Name} uses {inst.Name}: {half.Name}." + (martialWand ? " (1 Edge)" : ""));
        if (!_priorityWindowOpen)
        {
            while (!State.Stack.IsEmpty)
                State.Resolver.ResolveTop(State);
        }
        State.ActiveCasterUnit = null;

        // The item is the cost.
        if (def.IsWand)
        {
            inst.Charges = Math.Max(0, inst.Charges - 1);
            combatUI?.AppendActionLog($"{inst.Name}: {inst.Charges}/{def.MaxCharges} charge(s) left.");
        }
        else if (def.IsSpellglass)
        {
            save.Armory.RemoveItem(inst.InstanceId);
            combatUI?.AppendActionLog($"{inst.Name} shatters.");
        }
        SaveManager.MarkDirty();

        u.Stats.HasActed = true;
        EdgeRules.OnAction(u, EdgeActionKind.Item);
        RecordMartial(u, "item", def.Id, edgeBefore);

        u.RefreshHealthBar();
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        MaybeAdvanceToReadyUnit();
        _pruneNeeded = true;
    }

    /// <summary>The consumable effect table, shared by the belt cards and the
    /// satchel popup. Returns false (nothing applied) for an unknown effect.</summary>
    private bool ApplyConsumableEffect(Unit unit, ItemDefinition def, out string line)
    {
        line = "";
        switch (def.ConsumeEffect)
        {
            case "heal":
                int before = unit.Stats.Health;
                unit.Stats.Health = Mathf.Min(unit.Stats.MaxHealth, unit.Stats.Health + def.ConsumeValue);
                line = $"{unit.DisplayName} drinks the {def.Name} and restores {unit.Stats.Health - before} HP.";
                return true;
            case "shield":
                unit.Stats.Shield += def.ConsumeValue;
                line = $"{unit.DisplayName} reads the {def.Name} and gains {def.ConsumeValue} shield.";
                return true;
            case "mana":
                unit.Stats.Mana = Mathf.Min(unit.Stats.MaxMana, unit.Stats.Mana + def.ConsumeValue);
                line = $"{unit.DisplayName} drinks the {def.Name}. Mana restored.";
                return true;
            case "ap":
                unit.CurrentActionPoints += def.ConsumeValue;
                line = $"{unit.DisplayName} drinks the {def.Name} for +{def.ConsumeValue} action points.";
                return true;
            case "edge":
                // Whetstone (spec §11c): +2 Edge. A gain, so it counts against the
                // per-round cap and marks the window; a stone at cap grants nothing.
                int gained = EdgeRules.TryGain(unit, def.ConsumeValue, def.Name);
                line = $"{unit.DisplayName} works the {def.Name}: +{gained} Edge.";
                return true;
            default:
                GD.PrintErr($"[Consumable] Unknown effect '{def.ConsumeEffect}' on {def.Id}.");
                return false;
        }
    }

    /// <summary>Post-use bookkeeping shared by belt and satchel: gates, Edge
    /// action, telemetry, removal, refresh.</summary>
    private void FinishConsumable(Unit unit, ItemInstance inst, ItemDefinition def, string line)
    {
        var save = SaveManager.ActiveSave;
        bool isScroll = def.ConsumeKind == "scroll";
        if (isScroll) _scrollReadThisTurn = true;
        else unit.HasUsedConsumableThisTurn = true;
        int edgeBeforeItem = unit.Edge;
        EdgeRules.OnAction(unit, EdgeActionKind.Item);   // Skirmish momentum
        RecordMartial(unit, "item", def.Id, edgeBeforeItem);
        save?.Armory.RemoveItem(inst.InstanceId);
        SaveManager.MarkDirty();
        unit.Stats.HasActed = true;
        unit.RefreshHealthBar();
        combatUI?.AppendActionLog(line);
        GD.Print($"[Consumable] {line}");
        combatUI?.CloseConsumableList();
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        MaybeAdvanceToReadyUnit();
    }

    // ── Staves (spec §11b, M7) ───────────────────────────────────────────────

    /// <summary>If <paramref name="u"/>'s equipped weapon is a staff with a bound
    /// card, appends one Innate copy of that card to <paramref name="cards"/>
    /// before the deck is initialised. UnitDeckData.Initialize surfaces Innate
    /// cards to the top of the shuffled pile, so the opening draw holds it; after
    /// that it lives and dies like any other card in the deck.</summary>
    private void AddStaffInnate(Unit u, List<Card> cards)
    {
        var armory = SaveManager.ActiveSave?.Armory;
        if (u == null || cards == null || armory == null || u.IsMartial)
            return;
        var weaponId = armory.GetLoadout(LoadoutKeyFor(u)).WeaponInstanceId;
        if (string.IsNullOrEmpty(weaponId))
            return;
        var inst = armory.GetInstance(weaponId);
        var def = inst != null ? ItemDatabase.Get(inst.DefinitionId) : null;
        if (def == null || !def.IsStaff)
            return;
        string bound = inst.EffectiveBoundCardId(def);
        if (string.IsNullOrEmpty(bound))
            return;

        var bp = CardDatabase.Blueprints.Find(b => b.Id == bound) ?? CardDatabase.GetByName(bound);
        if (bp == null)
        {
            GD.PushWarning($"[Staff] {def.Name} binds '{bound}', which is not in the card database.");
            return;
        }
        var card = CardDatabase.Instantiate(bp);
        if (card == null)
            return;
        card.Innate = true;
        cards.Add(card);
        GD.Print($"[Staff] {u.Name} carries {def.Name}: {card.CardName} joins the deck as Innate.");
    }

    // ── Debug (D13) ──────────────────────────────────────────────────────────

    /// <summary>Ctrl+R: every wand back to full, mid-expedition.</summary>
    private void DebugRefillWands()
    {
        if (!OS.IsDebugBuild())
            return;
        int n = SaveManager.ActiveSave?.Armory?.RefillWandCharges() ?? 0;
        SaveManager.MarkDirty();
        combatUI?.AppendActionLog($"[Debug] Refilled {n} wand(s).");
        RefreshSelectedUnitUI();
    }
}
