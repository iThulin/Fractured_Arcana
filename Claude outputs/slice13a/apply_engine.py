import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text()
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:80], s.count(o))
        s = s.replace(o, n)
    p.write_text(s)

E = 'Scripts/Cards/Effects/EnchanterEngine.cs'
edit(E, [
# NameCondition: new fields + chip text
('''    /// <summary>Extra Weave the Namer gains on each break, on top of the standard +1.</summary>
    public int OwnerWeave;
''',
'''    /// <summary>Extra Weave the Namer gains on each break, on top of the standard +1.</summary>
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
'''),
('''    /// <summary>"moves or casts" style wording for chips and logs.</summary>''',
'''    /// <summary>Nameplate chip text, e.g. "If it attacks: 5" or "Casts backfire".</summary>
    public string ChipText()
    {
        var parts = new List<string>();
        string words = TriggerWords();
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

    /// <summary>"moves or casts" style wording for chips and logs.</summary>'''),
# Names statics
('''    /// <summary>Raised after a break has dealt its damage: (named unit, the Name).</summary>
    public static event Action<Unit, NameCondition> NameBroken;
''',
'''    /// <summary>Raised after a break has dealt its damage: (named unit, the Name).</summary>
    public static event Action<Unit, NameCondition> NameBroken;

    /// <summary>Round of the most recent break by anyone (Forbidden Word's bottom).</summary>
    public static int LastBreakRound = -99;

    /// <summary>The Price Compounds (rest of fight): team -> penalty growth per break.
    /// A broken Name of that team also lasts 1 more turn.</summary>
    public static readonly Dictionary<int, int> CompoundGrowth = new();

    /// <summary>Fine Print (Terms and Conditions bottom): the drawer, the last round it
    /// lasts, and how many draws are left.</summary>
    public static readonly Dictionary<Unit, (int untilRound, int left)> DrawOnBreak = new();

    /// <summary>Combat start: forget the last fight's rest-of-fight effects.</summary>
    public static void ResetForCombat()
    {
        LastBreakRound = -99;
        CompoundGrowth.Clear();
        DrawOnBreak.Clear();
    }

    /// <summary>True when a break happened this round or the one before (an enemy turn
    /// sits between two player turns, so "this round" alone would miss it).</summary>
    public static bool BrokeRecently => LastBreakRound >= Round - 1;
'''),
('''        log?.Invoke($"[Name] {target.Name} is Named for {name.TurnsRemaining} turn(s): if it {name.TriggerWords()}, it takes {name.Damage}.");''',
 '''        log?.Invoke($"[Name] {target.Name} is Named for {name.TurnsRemaining} turn(s): {name.ChipText()}.");'''),
('''            if ((name.Triggers & what) == 0 || name.BrokenRound == Round || name.TurnsRemaining <= 0)
                continue;
            name.BrokenRound = Round;''',
'''            if ((name.Triggers & what) == 0 || name.TurnsRemaining <= 0)
                continue;
            if (name.BrokenRound != Round)
                name.BrokenMask = NameTrigger.None;
            bool paid = name.PerTrigger ? (name.BrokenMask & what) != 0 : name.BrokenRound == Round;
            if (paid)
                continue;
            name.BrokenRound = Round;
            name.BrokenMask |= what;
            LastBreakRound = Round;'''),
('''            NameBroken?.Invoke(unit, name);
            RegisterManager.Fire("ench.name_broken");''',
'''            // The Price Compounds: a broken Name of that team lasts longer and bites harder.
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
            RegisterManager.Fire("ench.name_broken");'''),
# NameConditionEffect flags
('''    /// <summary>The Named unit's attacks are halved (Binding Chains).</summary>
    public bool HalveAttack;
''',
'''    /// <summary>The Named unit's attacks are halved (Binding Chains).</summary>
    public bool HalveAttack;
    /// <summary>Slice 13a flags: see the matching <see cref="NameCondition"/> fields.</summary>
    public bool Backfire, Shun, PerTrigger, Grouped;
'''),
('''        var owner = s?.ActiveCasterUnit;
        int team = owner?.TeamId ?? 0;
        bool any = false;
        foreach (var obj in targets.Items)
        {
            if (obj is not Unit u || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.TeamId == team)
                continue;
            Names.Write(u, new NameCondition
            {''',
'''        var owner = s?.ActiveCasterUnit;
        int team = owner?.TeamId ?? 0;
        bool any = false;
        var group = Grouped ? new List<(Unit, NameCondition)>() : null;
        foreach (var obj in targets.Items)
        {
            if (obj is not Unit u || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.TeamId == team)
                continue;
            var written = new NameCondition
            {'''),
('''                HalveAttack = HalveAttack,
            }, s.Log);
            any = true;
        }''',
'''                HalveAttack = HalveAttack,
                Backfire = Backfire,
                Shuns = Shun ? owner : null,
                PerTrigger = PerTrigger,
                Group = group,
            };
            group?.Add((u, written));
            Names.Write(u, written, s.Log);
            any = true;
            // Not Me: an enemy already aimed at the Namer chooses again.
            if (Shun && owner != null && u.CurrentIntent?.TargetUnit == owner)
                s.OnReplanIntent?.Invoke(u);
        }'''),
# CompelWalk: chosen path
('''    /// <summary>Walks the planned path one tile at a time under the walked rules. Each''',
'''    /// <summary>Every tile the unit could walk to within <paramref name="tiles"/> steps
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

    /// <summary>Walks the planned path one tile at a time under the walked rules. Each'''),
('''    public int Tiles;
    public bool Direction;

    public CompelWalkEffect(int tiles, bool direction)''',
'''    public int Tiles;
    public bool Direction;
    /// <summary>Puppet's Errand: walk to a tile the player picked (unit_then_tile).</summary>
    public bool Chosen;

    public CompelWalkEffect(int tiles, bool direction)'''),
('''        if (s?.Grid == null)
            return;
        if (Direction)
        {''',
'''        if (s?.Grid == null)
            return;
        if (Chosen)
        {
            if (!TwoStep.Read(s, targets, "Compel", out var puppet, out var dest))
                return;
            Run(s, puppet, CompelWalk.PlanChosen(s.Grid, puppet, Tiles, dest.Axial));
            return;
        }
        if (Direction)
        {'''),
])
print("engine ok")
