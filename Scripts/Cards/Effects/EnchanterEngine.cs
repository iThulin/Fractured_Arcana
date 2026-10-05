using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// EnchanterEngine.cs
//
// Purpose:        The Enchanter's two engine verbs
//                 (class_identity_enchanter_v1 §2, slice 11):
//                 • Compel: the target WALKS. Each step enters its tile
//                   under the walked rules in TileEntryReactions
//                   (glyphs trigger, Fire sears, Frost slides; no
//                   collision damage, no falling, Storm does not
//                   conduct, Earth does not halt). A blocked step ends
//                   the walk. The Elementalist shoves (forced rules);
//                   the Enchanter compels (walked rules).
//                 • Names: conditions on a unit. "Name an enemy for N
//                   turns: if it moves / attacks / casts, it takes X."
//                   Names punish a choice; they never forbid it. A Name
//                   whose condition is met is BROKEN: its damage lands
//                   (once per round per Name), and NameBroken fires
//                   (+1 Weave for the Namer, cards that read breaks).
// Layer:          Effects + combat rules
// Collaborators:  Unit.PlaceOnTile (walked entry), Unit.Names,
//                 CombatManager.Enchanter.cs (break hooks, ticking,
//                 Weave, preview), StatusCatalog (nameplate chips)
// See:            docs/class_identity_enchanter_v1.md §2
// ============================================================

/// <summary>What a Name watches for.</summary>
[Flags]
public enum NameTrigger
{
    None = 0,
    Move = 1,
    Attack = 2,
    Cast = 4,
    Any = Move | Attack | Cast,
}

/// <summary>One Name on a unit: its condition and what breaking it costs.</summary>
public sealed class NameCondition
{
    public NameTrigger Triggers;
    public int Damage;
    public int TurnsRemaining;
    /// <summary>Who wrote it (Weave goes to them on a break).</summary>
    public Unit OwnerUnit;
    public int OwnerTeam;
    /// <summary>The card that wrote it, for the log and the nameplate tooltip.</summary>
    public string Source = "";
    /// <summary>Round of the last break: a Name punishes once per round, not per tile walked.</summary>
    public int BrokenRound = -1;
    /// <summary>Mana the Namer gains on each break (Mana Tithe's refund).</summary>
    public int OwnerMana;
    /// <summary>Binding Chains (§4): an attack by the Named unit deals half damage.</summary>
    public bool HalveAttack;
    /// <summary>Extra Weave the Namer gains on each break, on top of the standard +1.</summary>
    public int OwnerWeave;
    /// <summary>Forbidden Word (slice 13a): its spells land on its own tile instead.</summary>
    public bool Backfire;
    /// <summary>Not Me (slice 13a): it cannot pick this unit as a target.</summary>
    public Unit Shuns;
    /// <summary>Speak It Wrongly (slice 13a): its locked attack was moved onto an ally.</summary>
    public bool Misdirect;
    /// <summary>The Price Compounds bottom (slice 13a): it pays once per KIND of action each
    /// round (a move, an attack and a cast), not once per round.</summary>
    public bool PerTrigger;
    /// <summary>Kinds already paid this round (with <see cref="PerTrigger"/>).</summary>
    public NameTrigger BrokenMask;
    /// <summary>Terms and Conditions (slice 13a): Names written together. The first one
    /// broken pays, and the whole group falls away.</summary>
    public List<(Unit unit, NameCondition name)> Group;

    // ── Boons: Names on allies (Benefactor, slice 13b). An ally's Name never breaks. ──
    /// <summary>Name of Courage: +N damage to its spells and attacks.</summary>
    public int BonusDamage;
    /// <summary>Name of Courage: it cannot be Weakened.</summary>
    public bool ImmuneWeakened;
    /// <summary>Name of Warding: whoever strikes it is Weakened 1 turn and the Namer gains 1 Weave.</summary>
    public bool Warding;
    /// <summary>Litany of Names: its first card each turn costs N less.</summary>
    public int FirstCardDiscount;

    /// <summary>True for a Name that helps its bearer (written on an ally).</summary>
    public bool IsBoon => BonusDamage > 0 || ImmuneWeakened || Warding || FirstCardDiscount > 0;

    /// <summary>Nameplate chip text, e.g. "If it attacks: 5" or "Casts backfire".</summary>
    public string ChipText()
    {
        var parts = new List<string>();
        string words = TriggerWords();
        if (IsBoon)
        {
            if (BonusDamage > 0) parts.Add($"+{BonusDamage} damage");
            if (ImmuneWeakened) parts.Add("Can't be Weakened");
            if (Warding) parts.Add("Attackers Weakened");
            if (FirstCardDiscount > 0) parts.Add($"First card -{FirstCardDiscount}");
            return string.Join("; ", parts);
        }
        if (Damage > 0 || (!Backfire && Shuns == null && !Misdirect))
        {
            if (Group != null)
                parts.Add($"First to act: {Damage}");
            else if (PerTrigger)
                parts.Add($"Each time it {words}: {Damage}");
            else
                parts.Add($"If it {words}: {Damage}" + (HalveAttack ? ", half" : ""));
        }
        if (Backfire) parts.Add("Casts backfire");
        if (Misdirect) parts.Add("Misdirected");
        if (Shuns != null) parts.Add($"Can't target {Shuns.DisplayName}");
        return string.Join("; ", parts);
    }

    /// <summary>Nameplate tooltip body (without the turns-left suffix).</summary>
    public string Describe()
    {
        var parts = new List<string>();
        string words = TriggerWords();
        if (IsBoon)
        {
            if (BonusDamage > 0) parts.Add($"its spells and attacks deal {BonusDamage} more damage");
            if (ImmuneWeakened) parts.Add("it cannot be Weakened");
            if (Warding) parts.Add("whoever strikes it is Weakened for 1 turn and its Namer gains 1 Weave");
            if (FirstCardDiscount > 0) parts.Add($"its first card each turn costs {FirstCardDiscount} less");
            return $"Named by {Source}: " + string.Join(", ", parts) + ".";
        }
        if (Damage > 0)
        {
            if (Group != null)
                parts.Add($"the first Named enemy of its group to act takes {Damage} damage, and the group's Names end");
            else if (PerTrigger)
                parts.Add($"each time it {words}, it takes {Damage} damage (once per kind of action a round)");
            else
                parts.Add($"if it {words}, it takes {Damage} damage (once a round)");
        }
        if (HalveAttack) parts.Add("its attacks deal half damage");
        if (Backfire) parts.Add("any spell it casts lands on its own tile");
        if (Misdirect) parts.Add("its next attack was moved onto one of its allies");
        if (Shuns != null) parts.Add($"it cannot target {Shuns.DisplayName}");
        if (parts.Count == 0) parts.Add($"it is watched");
        return $"Named by {Source}: " + string.Join(", ", parts) + ".";
    }

    /// <summary>"moves or casts" style wording for chips and logs.</summary>
    public string TriggerWords()
    {
        var parts = new List<string>();
        if (Triggers.HasFlag(NameTrigger.Move)) parts.Add("moves");
        if (Triggers.HasFlag(NameTrigger.Attack)) parts.Add("attacks");
        if (Triggers.HasFlag(NameTrigger.Cast)) parts.Add("casts");
        return parts.Count switch
        {
            0 => "acts",
            1 => parts[0],
            2 => $"{parts[0]} or {parts[1]}",
            _ => $"{parts[0]}, {parts[1]} or {parts[2]}",
        };
    }
}

/// <summary>Rules for Names: writing, breaking, ticking. Static so effects, the enemy
/// executor and the tile-entry bus can all reach it without plumbing.</summary>
public static class Names
{
    /// <summary>The current round (set at each player turn start); breaks are once per round.</summary>
    public static int Round;

    /// <summary>Raised after a break has dealt its damage: (named unit, the Name).</summary>
    public static event Action<Unit, NameCondition> NameBroken;

    /// <summary>Round of the most recent break by anyone (Forbidden Word's bottom).</summary>
    public static int LastBreakRound = -99;

    /// <summary>The Price Compounds (rest of fight): team -> penalty growth per break.
    /// A broken Name of that team also lasts 1 more turn.</summary>
    public static readonly Dictionary<int, int> CompoundGrowth = new();

    /// <summary>Fine Print (Terms and Conditions bottom): the drawer, the last round it
    /// lasts, and how many draws are left.</summary>
    public static readonly Dictionary<Unit, (int untilRound, int left)> DrawOnBreak = new();

    /// <summary>The Seventh Name (rest of fight): team -> (extra turns on every Name it
    /// writes, damage of the glyph prepared beneath each enemy it Names).</summary>
    public static readonly Dictionary<int, (int extraTurns, int glyphDamage)> SeventhName = new();

    /// <summary>Combat start: forget the last fight's rest-of-fight effects.</summary>
    public static void ResetForCombat()
    {
        LastBreakRound = -99;
        CompoundGrowth.Clear();
        DrawOnBreak.Clear();
        SeventhName.Clear();
    }

    /// <summary>Before a Name is written: The Seventh Name lengthens it.</summary>
    public static void Lengthen(NameCondition name)
    {
        if (name != null && SeventhName.TryGetValue(name.OwnerTeam, out var sn) && sn.extraTurns > 0)
            name.TurnsRemaining += sn.extraTurns;
    }

    /// <summary>After an enemy is Named: The Seventh Name prepares a glyph beneath it
    /// (it pays at the start of its turn if it is still standing there).</summary>
    public static void GlyphBeneath(GameState s, Unit owner, Unit target)
    {
        if (s?.Glyphs == null || owner == null || target?.CurrentTile == null || target.TeamId == owner.TeamId)
            return;
        if (!SeventhName.TryGetValue(owner.TeamId, out var sn) || sn.glyphDamage <= 0)
            return;
        var g = s.Glyphs.Prepare(target.CurrentTile, owner, ng =>
        {
            ng.Trigger = GlyphTrigger.StartOfTurn;
            ng.Damage = sn.glyphDamage;
            ng.DurationTurns = 3;
        });
        if (g != null)
        {
            g.SourceName = "The Seventh Name";
            g.GlyphText = "If it starts its turn here, it takes {damage}.";
            s.Log($"[SeventhName] A glyph is written beneath {target.Name}.");
        }
    }

    /// <summary>A boon starts: Courage's damage is added (and taken off again in
    /// <see cref="EndBoon"/>), and Courage clears any Weakened already on it.</summary>
    public static void StartBoon(Unit u, NameCondition n)
    {
        if (u == null || n == null || !GodotObject.IsInstanceValid(u))
            return;
        if (n.BonusDamage > 0)
        {
            u.BonusSpellDamage += n.BonusDamage;
            u.AttackDamage += n.BonusDamage;
        }
        if (n.ImmuneWeakened && u.HasStatus("weakened"))
            u.RemoveStatus("weakened");
    }

    public static void EndBoon(Unit u, NameCondition n)
    {
        if (u == null || n == null || !GodotObject.IsInstanceValid(u))
            return;
        if (n.BonusDamage > 0)
        {
            u.BonusSpellDamage -= n.BonusDamage;
            u.AttackDamage -= n.BonusDamage;
        }
    }

    /// <summary>Litany of Names: the discount on this unit's first card this turn.</summary>
    public static int FirstCardDiscount(Unit u)
    {
        if (u == null || u.Stats.HasPlayedCardThisTurn || u.Names.Count == 0)
            return 0;
        int best = 0;
        foreach (var n in u.Names)
            if (n.TurnsRemaining > 0 && n.FirstCardDiscount > best)
                best = n.FirstCardDiscount;
        return best;
    }

    /// <summary>True when a living boon on the unit makes it immune to Weakened.</summary>
    public static bool ImmuneToWeakened(Unit u) =>
        u != null && u.Names.Any(n => n.ImmuneWeakened && n.TurnsRemaining > 0);

    /// <summary>Moves a Name from one unit to another (Name of Warding's bottom).</summary>
    public static bool Move(Unit from, Unit to, NameCondition n)
    {
        if (from == null || to == null || n == null || from == to || !from.Names.Remove(n))
            return false;
        EndBoon(from, n);
        to.Names.Add(n);
        StartBoon(to, n);
        from.RefreshHealthBar();
        to.RefreshHealthBar();
        return true;
    }

    /// <summary>True when a break happened this round or the one before (an enemy turn
    /// sits between two player turns, so "this round" alone would miss it).</summary>
    public static bool BrokeRecently => LastBreakRound >= Round - 1;

    public static void Write(Unit target, NameCondition name, Action<string> log = null)
    {
        if (target == null || name == null || !target.Stats.IsAlive)
            return;
        target.Names.Add(name);
        StartBoon(target, name);
        target.RefreshHealthBar();
        log?.Invoke($"[Name] {target.Name} is Named for {name.TurnsRemaining} turn(s): {name.ChipText()}.");
        RegisterManager.Fire("ench.first_name");
    }

    /// <summary>The unit did something a Name may watch for. Each matching Name not yet
    /// broken this round deals its damage and raises <see cref="NameBroken"/>.</summary>
    public static void Break(Unit unit, NameTrigger what, Action<string> log = null)
    {
        if (unit == null || !GodotObject.IsInstanceValid(unit) || unit.Names.Count == 0 || !unit.Stats.IsAlive)
            return;
        foreach (var name in unit.Names.ToList())
        {
            if ((name.Triggers & what) == 0 || name.TurnsRemaining <= 0)
                continue;
            if (name.BrokenRound != Round)
                name.BrokenMask = NameTrigger.None;
            bool paid = name.PerTrigger ? (name.BrokenMask & what) != 0 : name.BrokenRound == Round;
            if (paid)
                continue;
            name.BrokenRound = Round;
            name.BrokenMask |= what;
            LastBreakRound = Round;
            string verb = what switch { NameTrigger.Move => "moved", NameTrigger.Attack => "attacked", _ => "cast" };
            string msg = $"{unit.Name} {verb} and broke its Name ({name.Source}): {name.Damage} damage.";
            GD.Print($"[Name] {msg}");
            log?.Invoke(msg);
            if (name.Damage > 0)
                unit.ApplyDamage(name.Damage);
            var namer = name.OwnerUnit;
            if (namer != null && GodotObject.IsInstanceValid(namer) && namer.Stats.IsAlive)
            {
                if (name.OwnerMana > 0)
                    namer.GainMana(name.OwnerMana);
                if (name.OwnerWeave > 0 && namer.Attunement is WeaveAttunement nw)
                    nw.Add(name.OwnerWeave);
                if (name.OwnerMana > 0 || name.OwnerWeave > 0)
                    log?.Invoke($"{namer.Name} collects the tithe: +{name.OwnerMana} mana"
                                + (name.OwnerWeave > 0 ? $", +{name.OwnerWeave} Weave." : "."));
            }
            // The Price Compounds: a broken Name of that team lasts longer and bites harder.
            if (CompoundGrowth.TryGetValue(name.OwnerTeam, out int grow) && grow > 0 && name.Group == null)
            {
                name.TurnsRemaining += 1;
                name.Damage += grow;
                log?.Invoke($"The price compounds: {unit.Name}'s Name now costs {name.Damage} and lasts {name.TurnsRemaining} more turn(s).");
            }
            // Terms and Conditions: the first to act pays, and the whole group ends.
            if (name.Group != null)
            {
                foreach (var (gu, gn) in name.Group)
                {
                    gn.TurnsRemaining = 0;
                    if (gu != null && GodotObject.IsInstanceValid(gu) && gu.Names.Remove(gn))
                        gu.RefreshHealthBar();
                }
            }
            NameBroken?.Invoke(unit, name);
            RegisterManager.Fire("ench.name_broken");
            if (!unit.Stats.IsAlive)
                break;
        }
    }

    /// <summary>Start of a player turn: every Name loses a turn; spent Names fall away.</summary>
    public static void Tick(IEnumerable<Unit> units)
    {
        foreach (var u in units)
        {
            if (u == null || !GodotObject.IsInstanceValid(u) || u.Names.Count == 0)
                continue;
            foreach (var n in u.Names)
                n.TurnsRemaining--;
            foreach (var n in u.Names)
                if (n.TurnsRemaining <= 0)
                    EndBoon(u, n);
            if (u.Names.RemoveAll(n => n.TurnsRemaining <= 0) > 0)
                u.RefreshHealthBar();
        }
    }

    /// <summary>True when any living unit carries a Name written by <paramref name="team"/>.</summary>
    public static bool AnyWrittenBy(IEnumerable<Unit> units, int team) =>
        units.Any(u => u != null && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive
                       && u.Names.Any(n => n.OwnerTeam == team && n.TurnsRemaining > 0));
}

/// <summary>
/// Name each targeted unit: "for N turns, if it [moves/attacks/casts], it takes X".
/// Only units in the target set are Named (a two-step set's aim tile is skipped).
/// JSON: { "type": "name_condition", "trigger": "move" | "attack" | "cast" | "act"
///         | ["move","cast"], "damage": n, "duration": n }
/// The legacy "geas" key builds this too (move, plus cast with "punish_cast").
/// </summary>
public sealed class NameConditionEffect : EffectBase
{
    public NameTrigger Triggers;
    public int Damage;
    public int Duration;
    /// <summary>Payoffs to the Namer on each break (Mana Tithe).</summary>
    public int OwnerMana, OwnerWeave;
    /// <summary>The Named unit's attacks are halved (Binding Chains).</summary>
    public bool HalveAttack;
    /// <summary>Slice 13a flags: see the matching <see cref="NameCondition"/> fields.</summary>
    public bool Backfire, Shun, PerTrigger, Grouped;
    /// <summary>Slice 13b: Name allies instead of enemies; or every ally / every enemy
    /// on the board, whatever the targets.</summary>
    public bool Allies, AllAllies, AllEnemies;
    /// <summary>Slice 13b boons (see <see cref="NameCondition"/>).</summary>
    public int BonusDamage, FirstCardDiscount;
    public bool ImmuneWeakened, Warding;

    public NameConditionEffect(NameTrigger triggers, int damage, int duration)
    {
        Triggers = triggers == NameTrigger.None ? NameTrigger.Any : triggers;
        Damage = Math.Max(0, damage);
        Duration = Math.Max(1, duration);
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets?.Items == null)
            return;
        var owner = s?.ActiveCasterUnit;
        int team = owner?.TeamId ?? 0;
        bool any = false;
        var group = Grouped ? new List<(Unit, NameCondition)>() : null;
        bool allies = Allies || AllAllies;
        IEnumerable<object> pool = targets.Items;
        if ((AllAllies || AllEnemies) && s?.UnitsInPlay != null)
            pool = s.UnitsInPlay.Cast<object>().ToList();
        foreach (var obj in pool)
        {
            if (obj is not Unit u || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (allies ? u.TeamId != team : u.TeamId == team)
                continue;
            if (allies && (u.IsStructure || u.IsObjectiveWard || u.IsMapObject))
                continue;
            var written = new NameCondition
            {
                Triggers = Triggers,
                Damage = Damage,
                TurnsRemaining = Duration,
                OwnerUnit = owner,
                OwnerTeam = team,
                Source = s.ResolvingAbilityName ?? "a Name",
                OwnerMana = OwnerMana,
                OwnerWeave = OwnerWeave,
                HalveAttack = HalveAttack,
                Backfire = Backfire,
                Shuns = Shun ? owner : null,
                PerTrigger = PerTrigger,
                Group = group,
                BonusDamage = BonusDamage,
                ImmuneWeakened = ImmuneWeakened,
                Warding = Warding,
                FirstCardDiscount = FirstCardDiscount,
            };
            if (allies)
                written.Triggers = NameTrigger.None;   // a boon never breaks
            Names.Lengthen(written);
            group?.Add((u, written));
            Names.Write(u, written, s.Log);
            if (!allies)
                Names.GlyphBeneath(s, owner, u);
            any = true;
            // Not Me: an enemy already aimed at the Namer chooses again.
            if (Shun && owner != null && u.CurrentIntent?.TargetUnit == owner)
                s.OnReplanIntent?.Invoke(u);
        }
        if (!any)
            s?.Log(allies ? "[Name] No ally to Name." : "[Name] No enemy to Name.");
    }
}

/// <summary>Path planning and walking for Compel.</summary>
public static class CompelWalk
{
    private static bool CanStep(HexGridManager grid, Unit u, TileData from, TileData to) =>
        to != null && to.Occupant == null && to.CanEnter(u) && to.Height - from.Height < 2;

    /// <summary>The tiles the walk would enter, in order. Pure: moves nothing.</summary>
    public static List<Vector2I> Plan(HexGridManager grid, Unit victim, int tiles,
                                      Vector2I? towards, Vector2I? direction, bool onto = false)
    {
        var path = new List<Vector2I>();
        if (grid == null || victim?.CurrentTile == null || tiles <= 0)
            return path;
        if (victim.IsMapObject && !victim.Pushable)
            return path;
        var cur = victim.CurrentTile;
        for (int i = 0; i < tiles; i++)
        {
            TileData next = null;
            if (direction.HasValue)
            {
                next = grid.GetTile(cur.Axial + direction.Value);
                if (!CanStep(grid, victim, cur, next))
                    break;
            }
            else if (towards.HasValue)
            {
                int here = grid.Distance(cur.Axial, towards.Value);
                if (here <= (onto ? 0 : 1))
                    break;
                int best = here;
                foreach (var nb in grid.GetNeighbors(cur.Axial))
                {
                    var t = grid.GetTile(nb);
                    if (!CanStep(grid, victim, cur, t) || path.Contains(nb))
                        continue;
                    int d = grid.Distance(nb, towards.Value);
                    if (d < best)
                    {
                        best = d;
                        next = t;
                    }
                }
                if (next == null)
                    break;
            }
            else
                break;
            path.Add(next.Axial);
            cur = next;
        }
        return path;
    }

    /// <summary>Every tile the unit could walk to within <paramref name="tiles"/> steps
    /// (breadth first, the same step rule as the walk), with the step that reached it.</summary>
    public static Dictionary<Vector2I, Vector2I> Reachable(HexGridManager grid, Unit victim, int tiles)
    {
        var came = new Dictionary<Vector2I, Vector2I>();
        if (grid == null || victim?.CurrentTile == null || tiles <= 0)
            return came;
        if (victim.IsMapObject && !victim.Pushable)
            return came;
        var start = victim.CurrentTile.Axial;
        var depth = new Dictionary<Vector2I, int> { [start] = 0 };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (depth[c] >= tiles)
                continue;
            var from = grid.GetTile(c);
            foreach (var nb in grid.GetNeighbors(c))
            {
                if (depth.ContainsKey(nb))
                    continue;
                if (!CanStep(grid, victim, from, grid.GetTile(nb)))
                    continue;
                depth[nb] = depth[c] + 1;
                came[nb] = c;
                queue.Enqueue(nb);
            }
        }
        return came;
    }

    /// <summary>Puppet's Errand: the walk to the chosen tile by the shortest path. If it
    /// cannot get there within the steps, it walks to the reachable tile nearest to it.</summary>
    public static List<Vector2I> PlanChosen(HexGridManager grid, Unit victim, int tiles, Vector2I dest)
    {
        var path = new List<Vector2I>();
        var came = Reachable(grid, victim, tiles);
        if (came.Count == 0)
            return path;
        var start = victim.CurrentTile.Axial;
        Vector2I goal = dest;
        if (!came.ContainsKey(dest))
        {
            int best = grid.Distance(start, dest);
            bool found = false;
            foreach (var c in came.Keys)
            {
                int d = grid.Distance(c, dest);
                if (d < best || (d == best && found && grid.Distance(start, c) < grid.Distance(start, goal)))
                {
                    best = d;
                    goal = c;
                    found = true;
                }
            }
            if (!found)
                return path;
        }
        for (var c = goal; c != start; c = came[c])
            path.Add(c);
        path.Reverse();
        return path;
    }

    /// <summary>Walks the planned path one tile at a time under the walked rules. Each
    /// step is re-checked at execution (the board may have changed); a blocked step, a
    /// glyph that ends movement, or death ends the walk. Returns tiles walked.</summary>
    public static int Walk(HexGridManager grid, Unit victim, List<Vector2I> path, Action<string> log)
    {
        if (grid == null || victim == null || path == null || path.Count == 0)
            return 0;
        var ctx = new MoveContext(grid);
        victim.MovementInterrupted = false;
        int walked = 0;
        foreach (var coord in path)
        {
            if (!GodotObject.IsInstanceValid(victim) || !victim.Stats.IsAlive || victim.CurrentTile == null)
                break;
            var next = grid.GetTile(coord);
            if (!CanStep(grid, victim, victim.CurrentTile, next))
            {
                log?.Invoke($"[Compel] {victim.Name}'s way is blocked. The walk ends.");
                break;
            }
            victim.PlaceOnTile(next, MovementKind.Walked, ctx);
            walked++;
            if (victim.MovementInterrupted)
            {
                log?.Invoke($"[Compel] {victim.Name}'s walk is stopped.");
                break;
            }
        }
        victim.MovementInterrupted = false;
        return walked;
    }
}

/// <summary>
/// Compel: the target walks up to <see cref="Tiles"/> tiles, toward the caster or in a
/// chosen direction (unit_then_direction targeting).
/// JSON: { "type": "compel_walk", "tiles": n, "mode": "toward_caster" | "direction" }
/// </summary>
public sealed class CompelWalkEffect : EffectBase
{
    public int Tiles;
    public bool Direction;
    /// <summary>Puppet's Errand: walk to a tile the player picked (unit_then_tile).</summary>
    public bool Chosen;

    public CompelWalkEffect(int tiles, bool direction)
    {
        Tiles = Math.Max(1, tiles);
        Direction = direction;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        if (Chosen)
        {
            if (!TwoStep.Read(s, targets, "Compel", out var puppet, out var dest))
                return;
            Run(s, puppet, CompelWalk.PlanChosen(s.Grid, puppet, Tiles, dest.Axial));
            return;
        }
        if (Direction)
        {
            if (!TwoStep.Read(s, targets, "Compel", out var victim, out var aim))
                return;
            var dir = ForcedMove.StepToward(s.Grid, victim.CurrentTile.Axial, aim.Axial);
            if (dir == Vector2I.Zero)
            {
                s.Log("[Compel] The aim is the unit's own tile: no direction.");
                return;
            }
            Run(s, victim, CompelWalk.Plan(s.Grid, victim, Tiles, null, dir));
            return;
        }

        var home = s.ActiveCasterUnit?.CurrentTile?.Axial;
        if (home == null || targets?.Items == null)
            return;
        // Nearest first, so a line of compelled enemies does not block itself.
        var victims = targets.Items.OfType<Unit>()
            .Where(u => GodotObject.IsInstanceValid(u) && u.Stats.IsAlive && u.CurrentTile != null
                        && u.TeamId != (s.ActiveCasterUnit?.TeamId ?? 0))
            .OrderBy(u => s.Grid.Distance(u.CurrentTile.Axial, home.Value))
            .ToList();
        if (victims.Count == 0)
            s.Log("[Compel] No enemy to compel.");
        foreach (var v in victims)
            Run(s, v, CompelWalk.Plan(s.Grid, v, Tiles, home, null));
    }

    private static void Run(GameState s, Unit victim, List<Vector2I> path)
    {
        if (path.Count == 0)
        {
            s.Log($"[Compel] {victim.Name} has nowhere to walk.");
            return;
        }
        int walked = CompelWalk.Walk(s.Grid, victim, path, s.Log);
        s.Log($"[Compel] {victim.Name} walks {walked} tile(s).");
        RegisterManager.Fire("ench.compel");
    }
}
