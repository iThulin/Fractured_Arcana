using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// CombatManager.Maneuvers.cs
//
// Purpose:        Martial maneuvers (Martial Maneuvers & Edge spec v1
//                 §6, §8, D1 to D3), the technique tray that shows them,
//                 Honed riders on the basic attack (§4b), and Stagger
//                 with Poise (§2a). Arming follows the action bar's
//                 pattern (an armed card, then a click on the board);
//                 resolution reuses ForcedMove, statuses, the item
//                 trigger bus and EdgeRules, so a maneuver is an attack
//                 wherever the rules already say what an attack does (D2).
// Layer:          Combat rules (partial of CombatManager)
// Collaborators:  ManeuverRegistry.cs / ManeuverDefinition.cs (data),
//                 TechniqueTray.cs (display), EdgeRules.cs (Edge),
//                 CombatManager.EnemyIntents.cs (the staggered activation),
//                 CombatManager.Actions.cs (the bar this sits beside)
// ============================================================

public partial class CombatManager
{
    // ── State ──────────────────────────────────────────────────────────────

    /// <summary>The technique card the player clicked; the next legal board click
    /// resolves it. Mutually exclusive with _armedAction.</summary>
    private ManeuverDefinition _armedManeuver;
    private readonly HashSet<Vector2I> _maneuverLegalTiles = new();
    private readonly List<Unit> _maneuverRingedUnits = new();
    private TechniqueTray _techniqueTray;

    private static readonly Color ManeuverEnvelopeColor = new(0.55f, 0.85f, 1.0f, 0.95f);   // steel
    private const float ManeuverEnvelopeFill = 0.09f;

    private const string StaggerStatus = "stagger";

    // ── Tray ───────────────────────────────────────────────────────────────

    /// <summary>Creates the tray on the DeckUI layer beside the hand strip, once.</summary>
    private void EnsureTechniqueTray()
    {
        if (_techniqueTray != null && IsInstanceValid(_techniqueTray))
            return;
        var host = deckManager?.HandContainer?.GetParent() as Node ?? this;
        _techniqueTray = new TechniqueTray { Name = "TechniqueTray" };
        _techniqueTray.TechniquePressed += OnTechniquePressed;
        _techniqueTray.StancePressed += OnStanceSwitchRequested;   // stance row lives in the tray now
        host.CallDeferred("add_child", _techniqueTray);
    }

    /// <summary>Rebuilds the tray for the selected unit: pinned technique cards for a
    /// martial with a classed weapon, hidden for everyone else (spec §11d).</summary>
    private void RefreshTechniqueTray()
    {
        EnsureTechniqueTray();
        var unit = selectedUnit;
        if (unit == null || !IsInstanceValid(unit) || !unit.IsPlayerControlled || !unit.Stats.IsAlive
            || isInDeploymentPhase)
        {
            _techniqueTray.ShowCards(null, "");
            return;
        }

        // Edge M6: pinned items (belt, wand) for ANY player unit; techniques only
        // for classed martials. A wizard with an empty belt sees no tray.
        var pinned = PinnedItemsFor(unit);
        if (!EdgeRules.UsesEdge(unit))
        {
            if (pinned.Count == 0)
            {
                _techniqueTray.ShowCards(null, "");
                return;
            }
            var itemCards = new List<TechniqueCardView>();
            foreach (var (inst, def) in pinned)
                itemCards.Add(ItemCardView(unit, inst, def));
            _techniqueTray.ShowCards(itemCards, $"Belt: {pinned.Count} item(s). 1 AP each.");
            return;
        }

        var cards = new List<TechniqueCardView>();
        foreach (var m in ManeuverRegistry.ForWeaponClass(unit.WeaponClass))
        {
            string why = ManeuverBlockReason(unit, m);
            int cost = ManeuverEdgeCost(unit, m, null);
            cards.Add(new TechniqueCardView
            {
                Id = m.Id,
                Title = m.DisplayName,
                Cost = m.DrainAll ? $"{m.ApCost} AP · all Edge (min {m.MinEdge})" : $"{m.ApCost} AP · {cost} Edge",
                Text = m.Text,
                Role = m.Role,
                Enabled = why == null,
                Armed = _armedManeuver == m,
                Tooltip = why ?? (m.IsReaction
                    ? $"{m.DisplayName}: click to arm. {ReactionTriggerText(unit, m)}"
                    : $"{m.DisplayName}: click, then click a target. Right-click or Esc to cancel."),
                IsFinisher = m.IsFinisher,
                IsReaction = m.IsReaction,
                ArmedNow = unit.ArmedReaction == m,
            });
        }

        string header;
        if (unit.WeaponClass == WeaponClass.None)
            header = $"Edge {unit.Edge}/{unit.MaxEdge}   No classed weapon: basic attack only.";
        else if (cards.Count == 0)
            header = $"Edge {unit.Edge}/{unit.MaxEdge}   {WeaponClassLabel(unit.WeaponClass)}: maneuvers arrive post-launch.";
        else
        {
            string honed = EdgeRules.IsHoned(unit)
                ? $"Honed: {HonedRiderText(unit.WeaponClass)}"
                : $"Honed at {EdgeRules.HonedThreshold} Edge: {HonedRiderText(unit.WeaponClass)}";
            header = $"Edge {unit.Edge}/{unit.MaxEdge}   {WeaponClassLabel(unit.WeaponClass)}   {honed}";
        }
        if (unit.ArmedReaction != null)
            header += $"\nArmed: {unit.ArmedReaction.DisplayName}. {ReactionTriggerText(unit, unit.ArmedReaction)}";
        foreach (var (inst, def) in pinned)
            cards.Add(ItemCardView(unit, inst, def));   // Edge M6: items to the right
        _techniqueTray.ShowCards(cards, header, StanceViewsFor(unit));
    }

    /// <summary>The stance row for the tray (moved from the unit panel,
    /// 2026-09-28): one button per fielded stance, the active one highlighted and
    /// disabled. Same gates as TrySwitchStance: 1 AP, once per turn. Null for a
    /// unit with fewer than two stances, which hides the row.</summary>
    private List<TechniqueCardView> StanceViewsFor(Unit unit)
    {
        if (unit?.AvailableStances == null || unit.AvailableStances.Count < 2)
            return null;
        bool myTurn = currentPhase == CombatPhase.PlayerTurn && !isInDeploymentPhase;
        var list = new List<TechniqueCardView>();
        foreach (var st in unit.AvailableStances)
        {
            bool active = st == unit.ActiveStance;
            string why = active ? "Active stance."
                : !myTurn ? "Not your turn."
                : unit.HasSwitchedStanceThisTurn ? "Already switched this turn."
                : unit.CurrentActionPoints < MartialAPCosts.SwitchStance ? $"Needs {MartialAPCosts.SwitchStance} AP."
                : null;
            list.Add(new TechniqueCardView
            {
                Id = st.Id,
                Title = st.DisplayName,
                Armed = active,
                Enabled = why == null,
                Tooltip = st.Description + "\n" + (why ?? $"Switch: {MartialAPCosts.SwitchStance} AP, once per turn. Edge is kept."),
            });
        }
        return list;
    }

    private void HideTechniqueTray()
    {
        if (_techniqueTray != null && IsInstanceValid(_techniqueTray))
            _techniqueTray.ShowCards(null, "");
    }

    private void OnTechniquePressed(string maneuverId)
    {
        if (selectedUnit == null || currentPhase != CombatPhase.PlayerTurn || isInDeploymentPhase)
            return;
        if (TryHandleItemCardPressed(maneuverId))   // Edge M6: "item:<instanceId>"
            return;
        var m = ManeuverRegistry.Get(maneuverId);
        if (m == null)
            return;
        if (_armedManeuver == m)
        {
            DisarmManeuver();
            return;
        }
        ArmManeuver(selectedUnit, m);
    }

    /// <summary>Spec v1 §6 access: which weapon classes each martial class may
    /// field. Paired blades are the one both share. Used by the debug launcher's
    /// loadout block; the armory does not enforce it yet.</summary>
    public static bool WeaponClassAllowed(MartialClass martial, WeaponClass weapon) => martial switch
    {
        MartialClass.Fighter => weapon is WeaponClass.SwordShield or WeaponClass.Polearm or WeaponClass.Hammer
                                or WeaponClass.TwoHander or WeaponClass.PairedBlades,
        MartialClass.Ranger => weapon is WeaponClass.Bow or WeaponClass.Crossbow or WeaponClass.Sling
                               or WeaponClass.PairedBlades,
        _ => false,
    };

    public static string WeaponClassLabel(WeaponClass c) => c switch
    {
        WeaponClass.SwordShield => "Sword & shield",
        WeaponClass.Polearm => "Polearm",
        WeaponClass.Bow => "Bow",
        WeaponClass.Crossbow => "Crossbow",
        WeaponClass.Hammer => "Hammer",
        WeaponClass.TwoHander => "Two-hander",
        WeaponClass.PairedBlades => "Paired blades",
        WeaponClass.Sling => "Sling",
        _ => "Unclassed",
    };

    // ── Honed riders (spec §4b, §6a) ────────────────────────────────────────

    public static string HonedRiderText(WeaponClass c) => c switch
    {
        WeaponClass.SwordShield => "basic attack grants 1 shield",
        WeaponClass.Polearm => "basic attack has reach 2",
        WeaponClass.Bow => "+1 range",
        WeaponClass.Crossbow => "basic attack ignores 1 armor",
        _ => "no rider for this class yet",
    };

    /// <summary>Extra reach on the basic attack while Honed: Bow +1, Polearm to 2.
    /// Read by TryMartialAttack and the hover preview so both agree.</summary>
    private static int HonedRangeBonus(Unit u, int baseReach)
    {
        if (!EdgeRules.IsHoned(u))
            return 0;
        return u.WeaponClass switch
        {
            WeaponClass.Bow => 1,
            WeaponClass.Polearm => baseReach < 2 ? 2 - baseReach : 0,
            _ => 0,
        };
    }

    // ── Costs and legality ─────────────────────────────────────────────────

    /// <summary>Edge the maneuver will take from <paramref name="u"/> against
    /// <paramref name="target"/> (null = unknown yet), after stance riders.
    /// DrainAll returns the unit's whole pool.</summary>
    private int ManeuverEdgeCost(Unit u, ManeuverDefinition m, Unit target)
    {
        if (m.DrainAll)
            return u.Edge;
        int cost = m.EdgeCost;
        var rider = u.ActiveStance?.ManeuverRider ?? StanceManeuverRider.None;
        if (rider == StanceManeuverRider.FirstManeuverCheaper && u.ManeuversUsedThisCombat == 0)
            cost -= 1;
        if (rider == StanceManeuverRider.CheaperVsMarked && target != null && target.HasStatus("marked"))
            cost -= 1;
        return Math.Max(0, cost);
    }

    /// <summary>Why the card is greyed, or null when it can be armed right now.</summary>
    private string ManeuverBlockReason(Unit u, ManeuverDefinition m)
    {
        if (currentPhase != CombatPhase.PlayerTurn || isInDeploymentPhase)
            return "Not your turn.";
        if (!u.CanAct())
            return $"{u.Name} cannot act.";
        if (u.CurrentActionPoints < m.ApCost)
            return $"Needs {m.ApCost} AP (has {u.CurrentActionPoints}).";
        if (m.DrainAll)
        {
            if (u.Edge < m.MinEdge)
                return $"Finisher: needs at least {m.MinEdge} Edge (has {u.Edge}).";
        }
        else
        {
            int cost = ManeuverEdgeCost(u, m, null);
            if (u.Edge < cost && !OpeningsCouldCover(u, cost - u.Edge))
                return $"Needs {cost} Edge (has {u.Edge}).";
        }
        if (m.RequiresUnmoved && u.TilesMovedThisTurn > 0)
            return $"{m.DisplayName} needs a planted stance: {u.Name} has moved this turn.";
        if (m.IsReaction && u.ArmedReaction != null)
            return $"Already armed: {u.ArmedReaction.DisplayName}. It fires in the enemy phase or refunds at your next turn.";
        return null;
    }

    private int ManeuverReach(Unit u, ManeuverDefinition m)
    {
        int reach = m.Reach;
        var rider = u.ActiveStance?.ManeuverRider ?? StanceManeuverRider.None;
        if (m.ShapeValue == ManeuverShape.Range && reach > 1 && rider == StanceManeuverRider.RangePlusOne)
            reach += 1;
        if (m.ShapeValue == ManeuverShape.Line && rider == StanceManeuverRider.LinePlusOne)
            reach += 1;
        return reach;
    }

    // ── Arming ─────────────────────────────────────────────────────────────

    private void ArmManeuver(Unit u, ManeuverDefinition m)
    {
        string why = ManeuverBlockReason(u, m);
        if (why != null)
        {
            combatUI?.AppendActionLog(why);
            return;
        }

        DisarmAction(refresh: false);   // one armed thing at a time
        ClearArmedItem();               // Edge M6: nor an armed item card
        ClearManeuverHighlight();
        _armedManeuver = m;

        if (m.ShapeValue == ManeuverShape.Self)
        {
            // Nothing to aim: resolve on the spot.
            ResolveManeuver(u, m, null, null);
            return;
        }

        BuildManeuverLegalTiles(u, m);
        if (_maneuverLegalTiles.Count == 0)
        {
            combatUI?.AppendActionLog($"{m.DisplayName}: nothing in reach.");
            _armedManeuver = null;
            RefreshSelectedUnitUI();
            return;
        }

        EnsureCastRenderers();
        _castZone.ShowOutline(new HashSet<Vector2I>(_maneuverLegalTiles), grid, ManeuverEnvelopeColor, ManeuverEnvelopeFill);
        foreach (var coord in _maneuverLegalTiles)
        {
            var occ = grid.GetTile(coord)?.Occupant;
            if (occ != null && occ.Stats.IsAlive && occ.TeamId != u.TeamId)
            {
                occ.SetTargetable(true, ManeuverEnvelopeColor);
                _maneuverRingedUnits.Add(occ);
            }
        }

        string prompt = m.ShapeValue == ManeuverShape.Line
            ? "click a tile on the line to aim"
            : "click a target";
        combatUI?.SetHintText($"{m.DisplayName}: {prompt}. Right-click or Esc to cancel.");
        RefreshSelectedUnitUI();
    }

    /// <summary>True while a technique or an item card owns the next board click.</summary>
    private bool AnyCardArmed => _armedManeuver != null || _armedItem != null;

    private void DisarmManeuver(bool refresh = true)
    {
        if (_armedManeuver == null && _armedItem == null)
            return;
        _armedManeuver = null;
        ClearArmedItem();
        ClearManeuverHighlight();
        combatUI?.SetHintText("Select a unit, move, cast, then end turn.");
        if (refresh)
            RefreshSelectedUnitUI();
    }

    private void ClearManeuverHighlight()
    {
        _maneuverLegalTiles.Clear();
        foreach (var ru in _maneuverRingedUnits)
            if (ru != null && IsInstanceValid(ru))
                ru.SetTargetable(false);
        _maneuverRingedUnits.Clear();
        _castZone?.Clear();
    }

    /// <summary>Fills _maneuverLegalTiles for the shape. Range obeys the martial
    /// shot rules (line of sight and full cover beyond reach 1); Line lists every
    /// tile on the six axes up to reach, stopping at a sight blocker.</summary>
    private void BuildManeuverLegalTiles(Unit u, ManeuverDefinition m)
    {
        _maneuverLegalTiles.Clear();
        if (u?.CurrentTile == null || grid == null)
            return;
        var origin = u.CurrentTile.Axial;
        int reach = ManeuverReach(u, m);

        switch (m.ShapeValue)
        {
            case ManeuverShape.Adjacent:
                foreach (var n in grid.GetNeighbors(origin))
                {
                    var occ = grid.GetTile(n)?.Occupant;
                    if (occ != null && occ.Stats.IsAlive && occ.TeamId != u.TeamId
                        && Math.Abs(u.CurrentTile.Height - grid.GetTile(n).Height) <= grid.CliffHeightThreshold)
                        _maneuverLegalTiles.Add(n);
                }
                break;

            case ManeuverShape.Range:
                foreach (var kv in grid.Tiles)
                {
                    var occ = kv.Value?.Occupant;
                    if (occ == null || !occ.Stats.IsAlive || occ.TeamId == u.TeamId || occ.IsMapObject)
                        continue;   // units only: a Pinning Shot does not root a wall
                    int dist = grid.Distance(origin, kv.Key);
                    if (dist < 1 || dist > reach)
                        continue;
                    if (dist > 1)
                    {
                        if (!grid.HasLineOfSight(origin, kv.Key))
                            continue;
                        if (grid.CoverBetween(kv.Key, origin) == CoverKind.High)
                            continue;
                    }
                    else if (Math.Abs(u.CurrentTile.Height - kv.Value.Height) > grid.CliffHeightThreshold)
                        continue;
                    _maneuverLegalTiles.Add(kv.Key);
                }
                break;

            case ManeuverShape.Line:
                for (int d = 0; d < HexDirection.All.Length; d++)
                    foreach (var coord in LineTiles(origin, d, reach))
                        _maneuverLegalTiles.Add(coord);
                break;
        }
    }

    /// <summary>Tiles along axis <paramref name="dirIdx"/> from (not including)
    /// <paramref name="origin"/>, up to <paramref name="length"/>, stopping after
    /// a tile that blocks sight and at the map edge.</summary>
    private List<Vector2I> LineTiles(Vector2I origin, int dirIdx, int length)
    {
        var list = new List<Vector2I>();
        var step = HexDirection.All[dirIdx];
        var cur = origin;
        for (int i = 0; i < length; i++)
        {
            cur += step;
            var td = grid.GetTile(cur);
            if (td == null)
                break;
            list.Add(cur);
            if (td.BlocksLineOfSight)
                break;
        }
        return list;
    }

    /// <summary>Board click while a maneuver is armed. Returns true when the click
    /// was consumed (resolved or refused), false when nothing is armed.</summary>
    private bool TryHandleManeuverClick(Unit clickedUnit, HexTile clickedTile)
    {
        if (_armedItem != null)
            return TryHandleItemClick(clickedUnit, clickedTile);   // Edge M6
        if (_armedManeuver == null)
            return false;
        var u = selectedUnit;
        var m = _armedManeuver;
        if (u == null || !IsInstanceValid(u))
        { DisarmManeuver(); return true; }

        Vector2I? coord = clickedUnit?.CurrentTile?.Axial ?? clickedTile?.Axial;
        if (coord == null || !_maneuverLegalTiles.Contains(coord.Value))
        {
            combatUI?.AppendActionLog($"{m.DisplayName}: not a legal target. Right-click to cancel.");
            return true;
        }

        if (m.ShapeValue == ManeuverShape.Line)
        {
            int axis = HexDirection.SnapToAxis(u.CurrentTile.Axial, coord.Value, ManeuverReach(u, m));
            if (axis < 0)
            {
                combatUI?.AppendActionLog($"{m.DisplayName}: aim along a straight line.");
                return true;
            }
            ResolveManeuver(u, m, null, axis);
            return true;
        }

        var target = grid.GetTile(coord.Value)?.Occupant;
        if (target == null || !target.Stats.IsAlive)
        {
            combatUI?.AppendActionLog($"{m.DisplayName}: no one there.");
            return true;
        }
        ResolveManeuver(u, m, target, null);
        return true;
    }

    // ── Resolution ─────────────────────────────────────────────────────────

    /// <summary>Pays the costs, then applies the maneuver to its targets in
    /// effect order. <paramref name="target"/> for Adjacent and Range,
    /// <paramref name="lineAxis"/> for Line, neither for Self.</summary>
    private void ResolveManeuver(Unit u, ManeuverDefinition m, Unit target, int? lineAxis)
    {
        string why = ManeuverBlockReason(u, m);
        if (why != null)
        {
            combatUI?.AppendActionLog(why);
            DisarmManeuver();
            return;
        }

        // Targets first, so a Line with no one on it still spends nothing.
        var targets = new List<Unit>();
        if (target != null)
            targets.Add(target);
        else if (lineAxis.HasValue)
        {
            foreach (var coord in LineTiles(u.CurrentTile.Axial, lineAxis.Value, ManeuverReach(u, m)))
            {
                var occ = grid.GetTile(coord)?.Occupant;
                if (occ == null || !occ.Stats.IsAlive || occ == u)
                    continue;
                if (occ.TeamId == u.TeamId && !m.HitsAllies)
                    continue;
                targets.Add(occ);
            }
            if (targets.Count == 0)
            {
                combatUI?.AppendActionLog($"{m.DisplayName}: no one on that line.");
                return;
            }
        }

        // ── Costs ─────────────────────────────────────────────────────────
        int edgeBefore = u.Edge;
        int edgeCost = ManeuverEdgeCost(u, m, target);
        // Opportunist (M5): Openings on the target pay first. A finisher drains
        // the pool by definition, so Openings do not discount it.
        var openingsTarget = target ?? (targets.Count > 0 ? targets[0] : null);
        int openingsUsed = m.DrainAll ? 0 : OpeningsPayable(u, openingsTarget, edgeCost);
        int edgeToPay = edgeCost - openingsUsed;
        if (m.DrainAll && edgeCost < m.MinEdge)
        {
            combatUI?.AppendActionLog($"{m.DisplayName}: needs at least {m.MinEdge} Edge.");
            DisarmManeuver();
            return;
        }
        if (!u.TrySpendAP(m.ApCost))
        {
            combatUI?.AppendActionLog($"{u.Name} needs {m.ApCost} AP for {m.DisplayName}.");
            DisarmManeuver();
            return;
        }
        if (!EdgeRules.TrySpend(u, edgeToPay))
        {
            u.CurrentActionPoints += m.ApCost;   // refund: nothing happened
            combatUI?.AppendActionLog($"{u.Name} needs {edgeToPay} Edge for {m.DisplayName}.");
            DisarmManeuver();
            return;
        }
        if (openingsUsed > 0 && openingsTarget != null)
        {
            openingsTarget.Openings -= openingsUsed;
            combatUI?.AppendActionLog($"[Opportunist] {openingsUsed} Opening(s) on {openingsTarget.Name} spent as Edge.");
            UpdateIntentDisplay(openingsTarget);
        }

        _armedManeuver = null;
        ClearManeuverHighlight();
        combatUI?.SetHintText("Select a unit, move, cast, then end turn.");

        // ── Reactions arm; they resolve in the enemy phase (spec §7, M4) ──
        if (m.IsReaction)
        {
            ArmReaction(u, m, edgeCost);
            u.ManeuversUsedThisCombat++;
            u.Stats.HasActed = true;
            EdgeRules.OnAction(u, EdgeActionKind.Maneuver);
            RecordMartial(u, "reaction_arm", m.Id, edgeBefore);
            u.RefreshHealthBar();
            RefreshSelectedUnitUI();
            RefreshPlayerUnitBar();
            MaybeAdvanceToReadyUnit();
            return;
        }

        var stance = u.ActiveStance;
        var rider = stance?.ManeuverRider ?? StanceManeuverRider.None;
        string tag = $"[{m.DisplayName}]";
        GD.Print($"{tag} {u.Name} spends {m.ApCost} AP, {edgeCost} Edge.");
        combatUI?.AppendActionLog($"{u.Name} uses {m.DisplayName} ({m.ApCost} AP, {edgeCost} Edge).");

        // ── Damage figure (spec §6: ATK is the unit's AttackDamage) ──────
        bool dealsDamage = m.HasEffect("damage");
        int damage = 0;
        if (dealsDamage)
        {
            damage = u.AttackDamage + (m.Effect("damage")?.Value ?? 0);
            var scale = m.Effect("finisher_scale");
            if (scale != null)
                damage += scale.Value * edgeCost;
            if (rider == StanceManeuverRider.DamageBelowHalf && u.Stats.Health * 2 < u.Stats.MaxHealth)
                damage += 1;
        }
        bool ignoreArmor = m.HasEffect("ignore_armor");
        bool ignoreChitin = m.HasEffect("ignore_chitin");

        // Edge triggers read the pre-hit board (spec §5): Volley's extra targets,
        // Ambush's full-HP target, Duelist's Vulnerable target.
        if (dealsDamage && targets.Count > 0)
        {
            EdgeRules.OnAttackHits(u, targets, placeOpenings: openingsUsed == 0);
            RefreshOpeningsMarkers(u, targets);
        }
        bool anyMarked = false;

        // ── Per target ────────────────────────────────────────────────────
        foreach (var t in targets)
        {
            if (t == null || !IsInstanceValid(t) || !t.Stats.IsAlive)
                continue;
            bool wasMarked = t.HasStatus("marked");
            anyMarked |= wasMarked;

            if (dealsDamage)
            {
                int dmg = damage;
                if (rider == StanceManeuverRider.DamageVsVulnerable && t.HasStatus("vulnerable"))
                    dmg += 1;
                if (rider == StanceManeuverRider.DamageVsMostOpenings && t.Openings > 0 && t.Openings >= MaxOpeningsOnBoard())
                    dmg += 1;
                ManeuverStrike(u, t, dmg, ignoreArmor, ignoreChitin, m);
            }

            // Order: push, then stagger and root on whoever is still standing.
            var push = m.Effect("push");
            if (push != null && t.Stats.IsAlive && t.CurrentTile != null && u.CurrentTile != null)
            {
                int tiles = push.Value + (rider == StanceManeuverRider.PushPlusOne ? 1 : 0);
                var dir = ForcedMove.StepAwayFrom(grid, u.CurrentTile.Axial, t.CurrentTile.Axial);
                ForcedMove.Push(grid, t, dir, tiles, push.CollisionDamage, null,
                                msg => combatUI?.AppendActionLog(msg));
            }

            if (m.HasEffect("stagger") && t.Stats.IsAlive)
            {
                int ignorePoise = rider == StanceManeuverRider.IgnoreOnePoise ? 1 : 0;
                TryStagger(t, u, ignorePoise, m.DisplayName);
            }

            var root = m.Effect("root");
            if (root != null && t.Stats.IsAlive)
            {
                int turns = root.Value + (rider == StanceManeuverRider.StatusDurationPlusOne ? 1 : 0);
                t.ApplyStatus("rooted", turns);
                combatUI?.AppendActionLog($"{tag} {t.Name} is rooted for {turns} turn(s).");
            }
        }

        // ── After the action (D2, D3) ─────────────────────────────────────
        if (rider == StanceManeuverRider.GainShield)
        {
            u.Stats.Shield += 1;
            combatUI?.AppendActionLog($"[{stance.DisplayName}] {u.Name} gains 1 shield.");
        }
        u.ManeuversUsedThisCombat++;
        u.Stats.HasActed = true;
        if (dealsDamage)
        {
            u.HasAttackedThisCombat = true;
            u.HasAttackedThisTurn = true;
        }
        EdgeRules.OnAction(u, EdgeActionKind.Maneuver);
        if (anyMarked && dealsDamage)
            EdgeRules.OnAllyHitMarkedTarget(u, playerUnits);
        RecordMartial(u, "maneuver", m.Id, edgeBefore);

        u.RefreshHealthBar();
        ClearMoveTiles();
        ShowMoveTilesWithCost(selectedUnit);
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        MaybeAdvanceToReadyUnit();
        _pruneNeeded = true;
    }

    /// <summary>One damaging hit from a maneuver. Shares the basic attack's
    /// Marked bonus, item onAttack procs and presenter strike (D2: a damaging
    /// maneuver is an attack) but not the stance's basic-attack riders, which the
    /// §5 maneuver riders replace.</summary>
    private void ManeuverStrike(Unit attacker, Unit target, int damage, bool ignoreArmor, bool ignoreChitin,
                                ManeuverDefinition m)
    {
        var delivery = attacker.CurrentTile != null && target.CurrentTile != null
            && grid.Distance(attacker.CurrentTile, target.CurrentTile) > 1
            ? Delivery.Bolt : Delivery.Melee;

        int markedBonus = 0;
        if (target.HasStatus("marked"))
        {
            markedBonus = 3;
            target.RemoveStatus("marked");
            combatUI?.AppendActionLog($"[Mark] {target.Name} was marked, for +{markedBonus} damage!");
        }
        damage = Math.Max(1, damage + markedBonus);

        string msg = $"[{m.DisplayName}] {attacker.Name} hits {target.Name} for {damage} damage.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);
        CombatPresenter.EmitStrike(attacker, target, delivery);

        int savedArmor = target.Stats.Armor;
        int savedChitin = target.ChitinAmount;
        if (ignoreArmor)
            target.Stats.Armor = 0;
        if (ignoreChitin)
            target.ChitinAmount = 0;

        target.ApplyDamage(damage, attacker, delivery);

        if (target.Stats.IsAlive)
        {
            if (ignoreArmor)
                target.Stats.Armor = savedArmor;
            if (ignoreChitin)
                target.ChitinAmount = savedChitin;
        }
        if (ignoreArmor || ignoreChitin)
            combatUI?.AppendActionLog($"[{m.DisplayName}] " +
                (ignoreArmor && ignoreChitin ? "Armor and chitin ignored."
                 : ignoreArmor ? "Armor ignored." : "Chitin ignored."));

        // Q2 (§7a): onAttack item procs, exactly as ResolveMartialAttack queues them.
        if (target.Stats.IsAlive)
        {
            QueueItemAttackTriggers(attacker, target);
            if (!_priorityWindowOpen)
                KickTriggerDrain();
        }
    }

    // ── Opportunist: Openings (spec §4a, §5, M5) ─────────────────────────────

    private static bool StanceUsesOpenings(Unit u)
        => u?.ActiveStance != null && u.ActiveStance.EdgeTrigger == EdgeTrigger.Openings;

    /// <summary>Openings on <paramref name="target"/> that can stand in for Edge
    /// on a maneuver costing <paramref name="edgeCost"/>.</summary>
    private static int OpeningsPayable(Unit u, Unit target, int edgeCost)
    {
        if (!StanceUsesOpenings(u) || target == null || edgeCost <= 0)
            return 0;
        return Math.Min(edgeCost, Math.Max(0, target.Openings));
    }

    /// <summary>For the tray: could some enemy's Openings cover an Edge shortfall?</summary>
    private bool OpeningsCouldCover(Unit u, int shortfall)
    {
        if (!StanceUsesOpenings(u) || shortfall <= 0)
            return false;
        foreach (var e in enemyUnits)
            if (e != null && IsInstanceValid(e) && e.Stats.IsAlive && e.Openings >= shortfall)
                return true;
        return false;
    }

    /// <summary>The intent marker carries the Openings count; redraw it after a
    /// hit by an Opportunist so the token shows the moment it is placed.</summary>
    private void RefreshOpeningsMarkers(Unit attacker, IEnumerable<Unit> targets)
    {
        if (!StanceUsesOpenings(attacker) || targets == null)
            return;
        foreach (var t in targets)
            if (t != null && IsInstanceValid(t) && !t.IsPlayerControlled)
                UpdateIntentDisplay(t);
    }

    private int MaxOpeningsOnBoard()
    {
        int max = 0;
        foreach (var e in enemyUnits)
            if (e != null && IsInstanceValid(e) && e.Stats.IsAlive && e.Openings > max)
                max = e.Openings;
        return max;
    }

    // ── Vigilant: countered intents (spec §4a, §5, M5) ───────────────────────

    /// <summary>How many visible enemy intents <paramref name="u"/> stands ready to
    /// counter right now: adjacent to a channeling or releasing caster; adjacent to
    /// an attacker whose strike is aimed at someone else; or on a shortest path
    /// between a charger and its mark. EdgeRules caps what pays.</summary>
    private int CountCounteredIntents(Unit u)
    {
        if (u?.CurrentTile == null || grid == null || !StanceCountersIntents(u))
            return 0;
        var here = u.CurrentTile.Axial;
        int count = 0;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || !e.Stats.IsAlive || e.CurrentTile == null)
                continue;
            var intent = e.CurrentIntent;
            if (intent == null || !(ShowIntentKindByDefault || intent.Revealed))
                continue;
            bool adjacent = grid.Distance(here, e.CurrentTile.Axial) == 1;
            switch (intent.Kind)
            {
                case IntentKind.Channel:
                case IntentKind.Release:
                    if (adjacent) count++;
                    break;
                case IntentKind.Attack:
                case IntentKind.RangedAttack:
                    if (!intent.TargetTile.HasValue)
                        break;
                    var mark = intent.TargetTile.Value;
                    if (adjacent && mark != here)
                        count++;
                    else if (e.HasBehaviorTag("charge") && mark != here
                             && grid.Distance(e.CurrentTile.Axial, here) + grid.Distance(here, mark)
                                == grid.Distance(e.CurrentTile.Axial, mark))
                        count++;   // standing in the charger's path
                    break;
            }
        }
        return count;
    }

    private static bool StanceCountersIntents(Unit u)
        => u?.ActiveStance != null && u.ActiveStance.EdgeTrigger == EdgeTrigger.TurnEndCounteredIntents;

    // ── Measurement (spec §10, M5) ───────────────────────────────────────────

    /// <summary>One martial choice for the run log: kind (move, attack, shove,
    /// stance, item, maneuver, reaction_arm) plus the Edge it moved.</summary>
    private void RecordMartial(Unit u, string kind, string id, int edgeBefore)
    {
        if (u == null || !EdgeRules.UsesEdge(u))
            return;
        CombatTelemetry.RecordMartialAction(u.Name, kind, id ?? "", edgeBefore, u.Edge, roundNumber);
    }

    // ── Stagger and Poise (spec §2a, D1) ─────────────────────────────────────

    /// <summary>Applies a stagger to <paramref name="target"/>, or cracks one
    /// Poise instead when it has any. Returns true when the stagger landed.</summary>
    private bool TryStagger(Unit target, Unit source, int ignorePoise, string byWhat)
    {
        if (target == null || !IsInstanceValid(target) || !target.Stats.IsAlive || target.IsPlayerControlled)
            return false;
        if (target.IsStaggered)
        {
            combatUI?.AppendActionLog($"[{byWhat}] {target.Name} is already staggered.");
            return false;
        }

        int effectivePoise = Math.Max(0, target.Poise - Math.Max(0, ignorePoise));
        if (effectivePoise > 0)
        {
            target.Poise = Math.Max(0, target.Poise - 1);
            string crack = $"[{byWhat}] {target.Name}'s poise cracks ({target.Poise}/{target.MaxPoise} left).";
            GD.Print(crack);
            combatUI?.AppendActionLog(crack);
            UpdateIntentDisplay(target);
            return false;
        }

        target.IsStaggered = true;
        target.ApplyStatus(StaggerStatus, 99);   // icon only; the bool is the rule
        string msg = $"[{byWhat}] {target.Name} is STAGGERED: its next intent is cancelled.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);
        UpdateIntentDisplay(target);
        RefreshEnemyRoster();
        return true;
    }

    /// <summary>The activation a staggered enemy gets (spec §2a, D1). A Channel or
    /// Release loses its charge; Attack becomes a move-only beat; RangedAttack,
    /// Imbue and Shove do nothing; Guard is unaffected (the caller does not route
    /// Guard here). The cycle index holds, so the same beat is telegraphed again.
    /// Poise refills at the end.</summary>
    private async Task ExecuteStaggeredActivation(Unit enemy)
    {
        var intent = enemy.CurrentIntent ?? PlanIntent(enemy);
        enemy.CurrentIntent = intent;
        string kind = intent?.Kind.ToString() ?? "no intent";
        string msg = $"{enemy.Name} reels from the stagger: {kind} cancelled.";
        GD.Print($"[Stagger] {msg}");
        combatUI?.AppendActionLog(msg);

        if (intent != null)
        {
            switch (intent.Kind)
            {
                case IntentKind.Channel:
                case IntentKind.Release:
                    enemy.ChannelTile = null;
                    enemy.RemoveStatus("wizard_charging");
                    enemy.ChannelDelayRemaining = 0;
                    combatUI?.AppendActionLog($"{enemy.Name}'s channel collapses.");
                    break;

                case IntentKind.Attack:
                    // Move-only: it may still close, it may not swing.
                    if (intent.TargetTile.HasValue && IsValidActor(enemy)
                        && grid.Distance(enemy.CurrentTile.Axial, intent.TargetTile.Value) > 1
                        && MayMove(enemy, out _))
                    {
                        await MoveTowardTile(enemy, intent.TargetTile.Value);
                    }
                    break;

                default:
                    break;   // RangedAttack, Imbue, Shove, Unknown: the beat is lost
            }
        }

        if (enemy != null && IsInstanceValid(enemy))
        {
            enemy.IsStaggered = false;
            enemy.RemoveStatus(StaggerStatus);
            enemy.Poise = enemy.MaxPoise;
            enemy.CurrentIntent = null;      // index NOT advanced: the beat is retried
            enemy.ClearIntentDisplay();
        }
    }

    // ── Debug levers (D13) ─────────────────────────────────────────────────

    /// <summary>Ctrl+G: stagger the inspected (or hovered-selected) enemy outright.</summary>
    private void DebugStaggerInspected()
    {
        if (!OS.IsDebugBuild())
            return;
        var e = inspectedEnemyUnit;
        if (e == null || !IsInstanceValid(e) || e.IsPlayerControlled)
        {
            combatUI?.AppendActionLog("[Debug] Inspect an enemy first (Shift+1..4 or click it).");
            return;
        }
        TryStagger(e, null, 99, "Debug");
    }

    /// <summary>Ctrl+W: cycle the selected martial's weapon class through the
    /// launch classes so every technique set can be reached without an item.</summary>
    private void DebugCycleWeaponClass()
    {
        if (!OS.IsDebugBuild() || selectedUnit == null || !EdgeRules.UsesEdge(selectedUnit))
            return;
        var order = new[] { WeaponClass.SwordShield, WeaponClass.Polearm, WeaponClass.Bow, WeaponClass.Crossbow, WeaponClass.None };
        int idx = Array.IndexOf(order, selectedUnit.WeaponClass);
        selectedUnit.WeaponClass = order[(idx + 1) % order.Length];
        DisarmManeuver(refresh: false);
        combatUI?.AppendActionLog($"[Debug] {selectedUnit.Name} now wields: {WeaponClassLabel(selectedUnit.WeaponClass)}.");
        RefreshSelectedUnitUI();
    }
}
