using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

// ============================================================
// CombatManager.Troops.cs  (partial of CombatManager)
//
// Purpose:        The guild's troop lines in battle (docs/provinces_v1_
//                 12_troops.md, slice PV7, section 5; P6). One siege
//                 for now, the castle defence: every line in the army
//                 stands garrison on the deck, melee lines nearest the
//                 gate and ranged lines nearest the Heart (PR90). Each
//                 line plans its turn as the player's turn begins and
//                 shows it above its head, as enemies show theirs; it
//                 acts after the player's turn and the constructs, before
//                 the enemies (PR91). The player commands the lines only
//                 through the Orders card, dealt once onto the
//                 commander's deck: Charge! or Hold Fast for every line
//                 this turn (PR92). When the fight ends, each line's
//                 losses and the battle go back to the army (PR93).
//
//                 On the board a line is a team-0 unit that nobody's
//                 hand commands: IsPlayerControlled is false and
//                 IsStructure is true, as the gate doors are. So it has
//                 no deck, cannot be selected or moved by the player,
//                 is not a survivor in the defeat scan, and the enemy
//                 finds it as it finds a door (FindNearestPlayerUnit).
//                 A click on it never strikes it (IsFieldedLine).
//
// Layer:          System (combat)
// Collaborators:  CombatManager.cs (the five hooks: FieldGarrison after
//                 TranslocateWizardOut, PlanTroopIntents at the player's
//                 turn start, RunTroopPhase after the construct phase,
//                 RecordTroopsAfterFight in DeclareVictory and
//                 DeclareDefeat, IsFieldedLine in the click), the castle
//                 defence partial (IsCastleDefense, StationAt), the
//                 constructs partial (FindNearestEnemyTo), TroopLines.cs,
//                 ArmyMuster.cs, TroopEffects.cs (the Orders card's
//                 effect), Unit.cs (SetIntentDisplay, BraceArmor)
// See:            docs/provinces_v1_12_troops.md section 5
// ============================================================

public partial class CombatManager
{
    /// <summary>One line on the board: its unit, its definition, its numbers, and its plan.</summary>
    private sealed class FieldedLine
    {
        public Unit Unit;
        public TroopLineDef Def;
        public string LineStateId = "";
        public TroopStats Stats;
        public int HpAtStart;
        public TroopIntentKind Intent = TroopIntentKind.None;
        /// <summary>Hold Fast: it braces and strikes an enemy in reach as well.</summary>
        public bool StrikeToo;
    }

    private readonly List<FieldedLine> _fieldedLines = new();

    /// <summary>This turn's order for every line ("" for none). It holds until the lines act.</summary>
    private string _troopOrder = "";
    private bool _troopOrdersDealt;
    private bool _troopsRecorded;

    /// <summary>The fight whose lines an Orders card speaks to; null when no line stands.</summary>
    private static CombatManager _troopsHost;

    private static readonly Color TroopTint = new Color(0.32f, 0.52f, 0.82f);
    private static readonly Color TroopIntentColor = new Color(0.55f, 0.85f, 1.0f);

    // The glyph set the intent labels already render (CombatManager.EnemyIntents IntentGlyph).
    private const string TroopGlyphStrike = "\u25B2";
    private const string TroopGlyphShot = "\u25BA";
    private const string TroopGlyphAdvance = "\u00BB";
    private const string TroopGlyphBrace = "\u25C6";
    private const string TroopGlyphWard = "\u2726";
    private const string TroopGlyphMend = "\u25C8";

    // ══════════════════════════════════════════════════════════════════════
    // Fielding (PR90)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>After the party is fielded and the wizard translocated: in a castle defence, every
    /// line of the army takes a tile on the deck. Resets the troop state in every fight.</summary>
    private void FieldGarrison()
    {
        _fieldedLines.Clear();
        _troopOrder = "";
        _troopOrdersDealt = false;
        _troopsRecorded = false;
        _troopsHost = null;
        if (!IsCastleDefense || CombatSim.Active)
            return;
        var cycle = SaveManager.ActiveSave?.Cycle;
        var army = cycle?.Army;
        if (army?.Lines == null || army.Lines.Count == 0)
            return;

        var siege = grid.ActiveSiege;
        var free = GarrisonTiles(siege);
        var order = new List<TroopLineState>();
        foreach (var line in army.Lines)
        {
            var def = TroopLines.Get(line?.LineId);
            if (def != null && !def.Ranged && line.Strength > 0)
                order.Add(line);
        }
        foreach (var line in army.Lines)
        {
            var def = TroopLines.Get(line?.LineId);
            if (def != null && def.Ranged && line.Strength > 0)
                order.Add(line);
        }

        int fielded = 0, left = 0;
        var kinds = new Dictionary<string, int>();
        foreach (var line in order)
        {
            var def = TroopLines.Get(line.LineId);
            var tile = TakeGarrisonTile(free, siege, def.Ranged);
            if (tile == null)
            {
                left++;
                continue;
            }
            kinds[def.Id] = kinds.TryGetValue(def.Id, out int seen) ? seen + 1 : 1;
            var stats = TroopLines.Stats(def, line);
            string nodeName = kinds[def.Id] > 1 ? $"{def.Name} {kinds[def.Id]}" : def.Name;
            var unit = SpawnTroopUnit(stats, nodeName, ArmyMuster.LineName(cycle, line), tile);
            if (unit == null)
            {
                left++;
                continue;
            }
            _fieldedLines.Add(new FieldedLine
            {
                Unit = unit,
                Def = def,
                LineStateId = line.Id,
                Stats = stats,
                HpAtStart = stats.Hp,
            });
            fielded++;
        }

        if (fielded == 0)
            return;
        _troopsHost = this;
        GD.Print($"[Troops] {fielded} line(s) stand garrison; {left} found no room on the deck.");
        combatUI?.AppendActionLog($"── The army stands garrison: {fielded} line(s) on the deck. They act on what "
                                  + "they show, after your turn and before the enemy's. ──");
        if (left > 0)
            combatUI?.AppendActionLog($"{left} line(s) found no room on the deck and wait below.");
    }

    /// <summary>The deck's free tiles: not the gate, not the Heart, not a station, walkable and empty.</summary>
    private List<Vector2I> GarrisonTiles(SiegeSpec siege)
    {
        var tiles = new List<Vector2I>();
        if (siege?.ObjectiveZone == null)
            return tiles;
        var gate = new HashSet<Vector2I>(siege.GateGap ?? new List<Vector2I>());
        foreach (var coord in siege.ObjectiveZone)
        {
            if (gate.Contains(coord) || (siege.Heart.HasValue && siege.Heart.Value == coord) || StationAt(coord) != null)
                continue;
            var tile = grid.GetTile(coord);
            if (tile == null || !tile.CanEnter(null) || tiles.Contains(coord))
                continue;
            tiles.Add(coord);
        }
        return tiles;
    }

    /// <summary>Take the free tile a line wants: a melee line the one nearest the gate, a ranged
    /// line the one nearest the Heart; ties to the earlier row, then column.</summary>
    private TileData TakeGarrisonTile(List<Vector2I> free, SiegeSpec siege, bool ranged)
    {
        int best = -1, bestKey = int.MaxValue;
        for (int i = 0; i < free.Count; i++)
        {
            var tile = grid.GetTile(free[i]);
            if (tile == null || !tile.CanEnter(null))
                continue;
            int key = GarrisonKey(siege, free[i], ranged);
            if (best < 0 || key < bestKey || (key == bestKey && EarlierTile(free[i], free[best])))
            {
                best = i;
                bestKey = key;
            }
        }
        if (best < 0)
            return null;
        var taken = grid.GetTile(free[best]);
        free.RemoveAt(best);
        return taken;
    }

    private int GarrisonKey(SiegeSpec siege, Vector2I coord, bool ranged)
    {
        if (ranged || siege.GateGap == null || siege.GateGap.Count == 0)
            return siege.Heart.HasValue ? grid.Distance(coord, siege.Heart.Value) : 0;
        int nearest = int.MaxValue;
        foreach (var gate in siege.GateGap)
            nearest = Math.Min(nearest, grid.Distance(coord, gate));
        return nearest;
    }

    private static bool EarlierTile(Vector2I a, Vector2I b)
        => a.Y < b.Y || (a.Y == b.Y && a.X < b.X);

    /// <summary>A line's unit, built as a gate door is: team 0, commanded by nobody's hand, a
    /// structure to the controls, its death wired into the standard pipeline.</summary>
    private Unit SpawnTroopUnit(TroopStats stats, string nodeName, string displayName, TileData tile)
    {
        if (DummyUnitScene == null || tile == null)
            return null;
        var unit = DummyUnitScene.Instantiate<Unit>();
        unit.IsPlayerControlled = false;   // the lines answer to orders, not to the hand
        unit.TeamId = 0;
        unit.StartMaxHealth = stats.MaxHp;
        unit.StartHealth = stats.Hp;
        unit.StartBaseSpeed = 0;           // no move the player could spend
        unit.StartMaxMana = 0;
        unit.StartMana = 0;
        unit.StartArmor = stats.Armor;
        unit.StartShield = 0;

        AddChild(unit);
        unit.OnDied += HandleUnitDeath;
        unit.OnStruck += HandleUnitStruck;
        unit.OnAttacked += HandleUnitAttacked;
        unit.OnMoved += HandleUnitMoved;
        unit.PlaceOnTile(tile);
        unit.IsStructure = true;           // no deck, no selection, not a survivor in the defeat scan
        unit.MoveRange = Math.Max(1, stats.Move);
        unit.MaxActionPoints = 0;          // its own turn lends it the one move it takes
        unit.CurrentActionPoints = 0;
        unit.AttackRange = Math.Max(1, stats.Range);
        unit.AttackDamage = stats.Damage;
        unit.Name = nodeName;
        unit.DisplayName = displayName;
        unit.SetBodyColor(TroopTint);
        unit.RefreshNameLabel();

        playerUnits.Add(unit);
        State.UnitsInPlay.Add(unit);
        GD.Print($"[Troops] {displayName} at {tile.Axial} (HP {stats.Hp}/{stats.MaxHp}, armour {stats.Armor}, "
                 + $"damage {stats.Damage}, reach {stats.Range}, move {stats.Move}).");
        return unit;
    }

    /// <summary>True for a unit that is one of the guild's lines on this board.</summary>
    private bool IsFieldedLine(Unit unit)
    {
        if (unit == null)
            return false;
        foreach (var line in _fieldedLines)
        {
            if (line.Unit == unit)
                return true;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Planning (PR91)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The player's turn begins: the Orders card is dealt once, and every line plans its
    /// turn and shows it.</summary>
    private void PlanTroopIntents()
    {
        if (_fieldedLines.Count == 0 || TroopFightOver)
            return;
        DealTroopOrders();
        PlanFieldedLines();
    }

    /// <summary>Every living line chooses its intent under this turn's order and shows it; one log
    /// line says what they will do.</summary>
    private void PlanFieldedLines()
    {
        var words = new List<string>();
        foreach (var line in _fieldedLines)
        {
            if (!IsValidActor(line.Unit))
                continue;
            bool reach = FindTroopMark(line) != null;
            bool mend = FindTroopMendTarget(line) != null;
            bool ally = TroopAlliesBeside(line.Unit).Count > 0;
            line.Intent = TroopLines.Choose(line.Def, _troopOrder, reach, mend, ally);
            line.StrikeToo = _troopOrder == TroopLines.OrderHold && reach;
            ShowTroopIntent(line);
            words.Add($"{line.Unit.DisplayName} {TroopIntentWords(line)}");
        }
        if (words.Count > 0)
            combatUI?.AppendActionLog("Your lines: " + string.Join("; ", words) + ".");
    }

    /// <summary>The nearest living enemy the line can strike from where it stands: within its
    /// reach, in sight for a shot, not across a cliff for a blow. Ties to the weaker.</summary>
    private Unit FindTroopMark(FieldedLine line)
    {
        var unit = line?.Unit;
        if (!IsValidActor(unit))
            return null;
        Unit best = null;
        int bestDistance = int.MaxValue, bestHp = int.MaxValue;
        foreach (var enemy in enemyUnits)
        {
            if (!IsValidActor(enemy) || enemy.IsMapObject || enemy.TeamId == 0 || enemy.HasStatus("untargetable"))
                continue;
            int distance = grid.Distance(unit.CurrentTile, enemy.CurrentTile);
            if (distance > unit.AttackRange)
                continue;
            if (distance <= 1 && Math.Abs(unit.CurrentTile.Height - enemy.CurrentTile.Height) > grid.CliffHeightThreshold)
                continue;
            if (distance > 1 && !grid.HasLineOfSight(unit.CurrentTile.Axial, enemy.CurrentTile.Axial))
                continue;
            if (distance < bestDistance || (distance == bestDistance && enemy.Stats.Health < bestHp))
            {
                best = enemy;
                bestDistance = distance;
                bestHp = enemy.Stats.Health;
            }
        }
        return best;
    }

    /// <summary>For a line that mends: the Castle Heart, or a gate door, beside it and hurt.</summary>
    private Unit FindTroopMendTarget(FieldedLine line)
    {
        var unit = line?.Unit;
        if (line == null || line.Stats.MendHp <= 0 || !IsValidActor(unit))
            return null;
        foreach (var ally in TroopAlliesBeside(unit))
        {
            bool heart = ally == _wardUnit;
            bool door = ally.IsStructure && !IsFieldedLine(ally);
            if ((heart || door) && ally.Stats.Health < ally.Stats.MaxHealth)
                return ally;
        }
        return null;
    }

    /// <summary>The living units of the guild's side on the tiles beside this one.</summary>
    private List<Unit> TroopAlliesBeside(Unit unit)
    {
        var allies = new List<Unit>();
        if (!IsValidActor(unit))
            return allies;
        foreach (var coord in grid.GetNeighbors(unit.CurrentTile.Axial))
        {
            var other = grid.GetTile(coord)?.Occupant;
            if (other != null && other != unit && IsInstanceValid(other) && other.Stats.IsAlive
                && other.TeamId == unit.TeamId && !other.IsMapObject)
                allies.Add(other);
        }
        return allies;
    }

    private int TroopStrikeDamage(FieldedLine line, bool advancing)
        => line.Stats.Damage + (advancing ? line.Stats.AdvanceBonus : 0)
           + (_troopOrder == TroopLines.OrderCharge ? TroopLines.ChargeBonus : 0);

    private int TroopBraceValue(FieldedLine line)
        => line.Stats.BraceArmor + (_troopOrder == TroopLines.OrderHold ? TroopLines.HoldFastBonus : 0);

    /// <summary>The label above the line: the kind's glyph and its number, as an enemy's intent
    /// reads, in the guild's own colour.</summary>
    private void ShowTroopIntent(FieldedLine line)
    {
        string strike = $"{(line.Stats.Range > 1 ? TroopGlyphShot : TroopGlyphStrike)} {TroopStrikeDamage(line, false)}";
        string text = line.Intent switch
        {
            TroopIntentKind.Strike => strike,
            TroopIntentKind.Advance => $"{TroopGlyphAdvance} {TroopStrikeDamage(line, true)}",
            TroopIntentKind.Brace => line.StrikeToo
                ? $"{TroopGlyphBrace} {TroopBraceValue(line)}  {strike}"
                : $"{TroopGlyphBrace} {TroopBraceValue(line)}",
            TroopIntentKind.Ward => $"{TroopGlyphWard} {line.Stats.WardShield}",
            TroopIntentKind.Mend => $"{TroopGlyphMend} {line.Stats.MendHp}",
            _ => "",
        };
        if (text.Length == 0)
        {
            line.Unit.ClearIntentDisplay();
            return;
        }
        line.Unit.SetIntentDisplay(text, TroopIntentColor, 40);
    }

    /// <summary>The intent in words, for the log.</summary>
    private string TroopIntentWords(FieldedLine line)
    {
        string verb = line.Stats.Range > 1 ? "loose" : "strike";
        return line.Intent switch
        {
            TroopIntentKind.Strike => $"{verb} for {TroopStrikeDamage(line, false)}",
            TroopIntentKind.Advance => $"advance and {verb} for {TroopStrikeDamage(line, true)}",
            TroopIntentKind.Brace => line.StrikeToo
                ? $"brace for {TroopBraceValue(line)} and {verb} for {TroopStrikeDamage(line, false)}"
                : $"brace for {TroopBraceValue(line)}",
            TroopIntentKind.Ward => $"ward those beside them for {line.Stats.WardShield}",
            TroopIntentKind.Mend => $"mend for {line.Stats.MendHp}",
            _ => "wait",
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // Acting
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>After the player's turn and the constructs, before the enemies: every line does
    /// what it showed. A strike goes to the nearest enemy in reach when it lands, so a mark the
    /// player has already killed is no blow wasted. The order lapses after.</summary>
    private async Task RunTroopPhase()
    {
        if (_fieldedLines.Count == 0 || TroopFightOver)
            return;
        foreach (var line in _fieldedLines.ToArray())
        {
            var unit = line.Unit;
            if (!IsValidActor(unit))
                continue;
            if (unit.HasStatus("stunned") || unit.HasStatus("frozen") || unit.HasStatus("bound"))
            {
                combatUI?.AppendActionLog($"{unit.DisplayName} cannot act this turn.");
                unit.ClearIntentDisplay();
                continue;
            }
            switch (line.Intent)
            {
                case TroopIntentKind.Strike:
                    await TroopStrike(line, FindTroopMark(line), TroopStrikeDamage(line, false));
                    break;
                case TroopIntentKind.Advance:
                {
                    bool moved = false;
                    var target = FindNearestEnemyTo(unit);
                    if (target != null && FindTroopMark(line) == null)
                    {
                        var start = unit.CurrentTile.Axial;
                        unit.CurrentActionPoints = 1;   // one hop of up to its move
                        await MoveToward(unit, target);
                        if (IsInstanceValid(unit))
                        {
                            unit.CurrentActionPoints = 0;
                            moved = unit.CurrentTile != null && unit.CurrentTile.Axial != start;
                        }
                    }
                    if (IsValidActor(unit))
                        await TroopStrike(line, FindTroopMark(line), TroopStrikeDamage(line, moved));
                    break;
                }
                case TroopIntentKind.Brace:
                    TroopBrace(line);
                    if (line.StrikeToo && IsValidActor(unit))
                        await TroopStrike(line, FindTroopMark(line), TroopStrikeDamage(line, false));
                    break;
                case TroopIntentKind.Ward:
                    TroopWard(line);
                    break;
                case TroopIntentKind.Mend:
                    TroopMend(line);
                    break;
            }
            if (IsInstanceValid(unit))
                unit.ClearIntentDisplay();
            if (CheckCombatEnd())
                return;
        }
        _troopOrder = "";
        PruneDeadUnits();
    }

    /// <summary>True once the fight is decided: the lines neither plan nor act after it.</summary>
    private bool TroopFightOver => currentPhase == CombatPhase.Victory || currentPhase == CombatPhase.Defeat;

    private static string TroopTargetName(Unit target)
        => !string.IsNullOrEmpty(target.DisplayName) ? target.DisplayName : target.Name.ToString();

    private async Task TroopStrike(FieldedLine line, Unit target, int damage)
    {
        var unit = line.Unit;
        if (!IsValidActor(unit))
            return;
        if (!IsValidActor(target))
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} find no enemy in reach.");
            return;
        }
        bool shot = grid.Distance(unit.CurrentTile, target.CurrentTile) > 1;
        int dealt = unit.ModifyOutgoingAttackDamage(damage);
        if (dealt <= 0)
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} cannot hurt {TroopTargetName(target)}.");
            return;
        }
        var delivery = shot ? Delivery.Bolt : Delivery.Melee;
        string verb = shot ? "loose at" : "strike";
        combatUI?.AppendActionLog($"{unit.DisplayName} {verb} {TroopTargetName(target)} for {dealt}.");
        CombatPresenter.EmitStrike(unit, target, delivery);
        target.ApplyDamage(dealt, unit, delivery);
        unit.HasAttackedThisTurn = true;
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    /// <summary>Armour on the line until the guild's next turn (Unit.StartTurn takes a brace
    /// down), and on each ally beside it as well under Shieldwall.</summary>
    private void TroopBrace(FieldedLine line)
    {
        var unit = line.Unit;
        int armor = TroopBraceValue(line);
        if (armor > 0)
        {
            unit.Stats.Armor += armor;
            unit.BraceArmor += armor;
        }
        int wall = line.Stats.ShieldwallArmor;
        int walled = 0;
        if (wall > 0)
        {
            foreach (var ally in TroopAlliesBeside(unit))
            {
                ally.Stats.Armor += wall;
                ally.BraceArmor += wall;
                ally.RefreshHealthBar();
                walled++;
            }
        }
        unit.RefreshHealthBar();
        combatUI?.AppendActionLog(walled > 0
            ? $"{unit.DisplayName} brace for {armor} armour, and {walled} beside them gain {wall}."
            : $"{unit.DisplayName} brace for {armor} armour.");
    }

    /// <summary>Shield on every ally beside the line. Shields are cleared as the guild's turn
    /// ends, and the lines act after that, so a ward holds through the enemy's turn.</summary>
    private void TroopWard(FieldedLine line)
    {
        var unit = line.Unit;
        int shield = line.Stats.WardShield;
        int warded = 0;
        foreach (var ally in TroopAlliesBeside(unit))
        {
            ally.Stats.Shield += shield;
            ally.RefreshHealthBar();
            warded++;
        }
        combatUI?.AppendActionLog(warded > 0
            ? $"{unit.DisplayName} ward {warded} beside them for {shield} shield."
            : $"{unit.DisplayName} find no one beside them to ward.");
    }

    /// <summary>Restore the Heart, or a gate door, beside the line.</summary>
    private void TroopMend(FieldedLine line)
    {
        var unit = line.Unit;
        var target = FindTroopMendTarget(line);
        if (target == null)
        {
            combatUI?.AppendActionLog($"{unit.DisplayName} find nothing beside them to mend.");
            return;
        }
        int before = target.Stats.Health;
        target.Stats.Health = Math.Min(target.Stats.MaxHealth, target.Stats.Health + line.Stats.MendHp);
        target.RefreshHealthBar();
        combatUI?.AppendActionLog($"{unit.DisplayName} mend {TroopTargetName(target)} by {target.Stats.Health - before}.");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Orders (PR92)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Once a fight, on the first turn the lines plan: the Orders card goes on top of the
    /// commander's draw pile, the first unit on the board with a deck, or the wizard's while it
    /// is still in the waystone.</summary>
    private void DealTroopOrders()
    {
        if (_troopOrdersDealt)
            return;
        _troopOrdersDealt = true;
        Unit owner = null;
        foreach (var unit in playerUnits)
        {
            if (unit != null && IsInstanceValid(unit) && unit.DeckData != null && !unit.IsAwaitingArrival && unit.Stats.IsAlive)
            {
                owner = unit;
                break;
            }
        }
        if (owner == null)
        {
            foreach (var unit in playerUnits)
            {
                if (unit != null && IsInstanceValid(unit) && unit.DeckData != null)
                {
                    owner = unit;
                    break;
                }
            }
        }
        if (owner == null)
            return;
        var card = BuildOrdersCard(owner.DeckData.School);
        if (card == null)
            return;
        owner.DeckData.DrawPile.Insert(0, card);
        RefreshDeckCounts();
        string who = !string.IsNullOrEmpty(owner.DisplayName) ? owner.DisplayName : owner.Name.ToString();
        combatUI?.AppendActionLog($"Orders lie on top of {who}'s deck: Charge! or Hold Fast, for every line.");
    }

    /// <summary>The Orders card, compiled by the card loader from its JSON but never entered in
    /// the card database, so no draft, shop or deck can offer it. Its blueprint id is cleared
    /// after the build, so casting it counts toward no card's mastery.</summary>
    internal static Card BuildOrdersCard(CardSchool school)
    {
        CardScriptRegistry.RegisterEffect(TroopOrderEffect.Key, node =>
            new TroopOrderEffect(node.TryGetProperty("order", out var order) ? order.GetString() ?? "" : "").WithTag("Order"));
        var json = new JsonObject
        {
            ["id"] = "troop_orders",
            ["name"] = "Orders",
            ["school"] = school.ToString(),
            ["rarity"] = "Common",
            ["top"] = new JsonObject
            {
                ["name"] = "Charge!",
                ["mana"] = 1,
                ["speed"] = "Studied",
                ["rules_text"] = $"Every line advances on the nearest enemy and strikes it this turn, {TroopLines.ChargeBonus} more damage.",
                ["targeting"] = new JsonObject { ["type"] = "self" },
                ["effect"] = new JsonObject { ["type"] = TroopOrderEffect.Key, ["order"] = TroopLines.OrderCharge },
            },
            ["bottom"] = new JsonObject
            {
                ["name"] = "Hold Fast",
                ["mana"] = 1,
                ["speed"] = "Studied",
                ["rules_text"] = $"Every line holds its ground this turn: {TroopLines.HoldFastBonus} more armour on its brace, and it strikes an enemy in reach as well.",
                ["targeting"] = new JsonObject { ["type"] = "self" },
                ["effect"] = new JsonObject { ["type"] = TroopOrderEffect.Key, ["order"] = TroopLines.OrderHold },
            },
        };
        try
        {
            var root = JsonDocument.Parse(json.ToJsonString()).RootElement;
            var card = JsonCardLoader.BuildCardPublic(root);
            if (card != null)
                card.BlueprintId = "";
            return card;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Troops] The Orders card did not build: {e.Message}");
            return null;
        }
    }

    /// <summary>The Orders card's effect: every line takes the order for this turn and plans again,
    /// so the labels change at once. Returns what was said, or why nothing was.</summary>
    internal static string IssueTroopOrder(string order)
    {
        var host = _troopsHost;
        if (host == null || !IsInstanceValid(host) || host._fieldedLines.Count == 0)
            return "No line stands on the field to hear the order.";
        host._troopOrder = order == TroopLines.OrderCharge || order == TroopLines.OrderHold ? order : "";
        host.PlanFieldedLines();
        string said = host._troopOrder == TroopLines.OrderCharge
            ? "Charge! Every line will advance and strike."
            : host._troopOrder == TroopLines.OrderHold
                ? "Hold fast! Every line holds its ground."
                : "The order is not one the lines know.";
        host.combatUI?.AppendActionLog(said);
        return said;
    }

    // ══════════════════════════════════════════════════════════════════════
    // After the fight (PR93)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>From DeclareVictory and DeclareDefeat, once: each line's HP goes back to the army
    /// as its strength, the fallen are struck off, and the rest count the battle. Not for a debug
    /// fight or a simulated one, which leave the army as it was.</summary>
    private void RecordTroopsAfterFight(bool won)
    {
        if (_troopsRecorded || _fieldedLines.Count == 0)
            return;
        _troopsRecorded = true;
        _troopsHost = null;
        if (PlayerSession.DebugCombat || CombatSim.Active)
            return;
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
            return;
        var results = new List<TroopBattleResult>();
        foreach (var line in _fieldedLines)
        {
            int hp = line.Unit != null && IsInstanceValid(line.Unit) && line.Unit.Stats.IsAlive ? line.Unit.Stats.Health : 0;
            results.Add(new TroopBattleResult
            {
                LineStateId = line.LineStateId,
                HpAtStart = line.HpAtStart,
                HpLeft = hp,
                MaxHp = line.Stats.MaxHp,
            });
        }
        var news = ArmyMuster.RecordBattle(cycle, results, won);
        if (news.Count > 0)
        {
            cycle.PendingSiegeReports ??= new List<string>();
            cycle.PendingSiegeReports.AddRange(news);
            foreach (var line in news)
                combatUI?.AppendActionLog(line);
        }
        SaveManager.MarkDirty();
    }
}
