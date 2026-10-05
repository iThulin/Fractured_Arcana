import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text()
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:80], s.count(o))
        s = s.replace(o, n)
    p.write_text(s)

# IntentTime hooks
edit('Scripts/Cards/Effects/IntentTimeEffects.cs', [
('''    public static Func<Unit, Unit, string> SwapLocksHook;''',
'''    public static Func<Unit, Unit, string> SwapLocksHook;

    /// <summary>Speak It Wrongly: move an enemy's locked attack onto the nearest other enemy.</summary>
    public static Func<Unit, string> RedirectLockHook;
    public static string RedirectLock(Unit enemy) => RedirectLockHook != null ? RedirectLockHook(enemy) : "no combat is running";

    /// <summary>Borrowed Will: an enemy's next attack strikes the chosen tile.</summary>
    public static Func<Unit, Vector2I, string> CommandAttackHook;
    public static string CommandAttack(Unit enemy, Vector2I tile) => CommandAttackHook != null ? CommandAttackHook(enemy, tile) : "no combat is running";'''),
])

edit('Scripts/Systems/GameStateManager.cs', [
('''    public Action<Unit> OnIllusionsSummoned;''',
'''    public Action<Unit> OnIllusionsSummoned;
    /// <summary>Not Me (slice 13a): this enemy plans its intent again.</summary>
    public Action<Unit> OnReplanIntent;'''),
])

# Unit: shun via the untargetable read
edit('Scripts/Systems/Combat/Core/Unit.cs', [
('''        if (CombatSim.Active && CombatSim.WasStatusRemoved(this, status))
            return false;
        return Stats.StatusEffects.ContainsKey(status) && Stats.StatusEffects[status] > 0;''',
'''        if (CombatSim.Active && CombatSim.WasStatusRemoved(this, status))
            return false;
        // Not Me (Enchanter, slice 13a): to the enemy being planned, a unit it was Named
        // never to target reads as untargetable, so every planner skips it.
        if (status == "untargetable" && PlanningFor != null && PlanningFor != this && PlanningFor.ShunsUnit(this))
            return true;
        return Stats.StatusEffects.ContainsKey(status) && Stats.StatusEffects[status] > 0;'''),
('''    public List<NameCondition> Names = new();''',
'''    public List<NameCondition> Names = new();

    /// <summary>The enemy whose intent is being planned right now (null otherwise).</summary>
    public static Unit PlanningFor;

    /// <summary>True when a living Name on this unit forbids it to target <paramref name="u"/>.</summary>
    public bool ShunsUnit(Unit u)
    {
        foreach (var n in Names)
            if (n.Shuns == u && n.TurnsRemaining > 0)
                return true;
        return false;
    }'''),
])

# Status chips
edit('Scripts/UI/StatusCatalog.cs', [
('''            string words = name.TriggerWords();
            string half = name.HalveAttack ? ", half" : "";
            var info = new StatusInfo($"If it {words}: {name.Damage}{half}",
                                      $"IF {words.ToUpperInvariant().Replace(" OR ", "/").Replace(", ", "/")}: {name.Damage}{half.ToUpperInvariant()}",
                                      StatusTone.Mark,
                                      $"Named by {name.Source}: if it {words}, it takes {name.Damage} damage (once a round)"
                                      + (name.HalveAttack ? ", and its attacks deal half damage" : "")
                                      + ". {n} turn(s) left.");
            list.Add(new UnitCondition($"name:{(int)name.Triggers}:{name.Damage}", info, name.TurnsRemaining));''',
'''            string chip = name.ChipText();
            var info = new StatusInfo(chip,
                                      chip.ToUpperInvariant(),
                                      StatusTone.Mark,
                                      name.Describe() + " {n} turn(s) left.");
            list.Add(new UnitCondition($"name:{(int)name.Triggers}:{name.Damage}:{chip.GetHashCode()}", info, name.TurnsRemaining));'''),
])

# Targeter: any_tile
edit('Scripts/Cards/Targeting/TargetSelectors.cs', [
('''public sealed class SelectUnitThenTileTarget : SelectTwoStepTarget
{
    public int destRange;
''',
'''public sealed class SelectUnitThenTileTarget : SelectTwoStepTarget
{
    public int destRange;
    /// <summary>Any tile within reach, occupied ones included (Borrowed Will picks a
    /// tile to strike, not a place to stand).</summary>
    public bool anyTile;
'''),
('''    public override string StepTwoPrompt => "Click a destination tile.";''',
 '''    public override string StepTwoPrompt => anyTile ? "Click the tile it will strike." : "Click a destination tile.";'''),
])
edit('Scripts/Cards/Loader/JsonCardLoader.cs', [
('''            int destRange = n.TryGetProperty("dest_range", out var d) ? d.GetInt32() : 2;
            return new SelectUnitThenTileTarget(enemyOnly, range, destRange, friendlyOnly, constructsOnly);''',
'''            int destRange = n.TryGetProperty("dest_range", out var d) ? d.GetInt32() : 2;
            bool anyTile = n.TryGetProperty("any_tile", out var at) && at.ValueKind == JsonValueKind.True;
            return new SelectUnitThenTileTarget(enemyOnly, range, destRange, friendlyOnly, constructsOnly) { anyTile = anyTile };'''),
])

# Registry
edit('Scripts/Cards/Loader/CardScriptRegistry.Enchanter.cs', [
('''            int dmg = n.TryGetProperty("damage", out var dm) ? dm.GetInt32() : 4;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 2;
            bool halve = n.TryGetProperty("halve_attack", out var ha) && ha.ValueKind == System.Text.Json.JsonValueKind.True;
            return new NameConditionEffect(trig, dmg, dur) { HalveAttack = halve }.WithTag("Control");''',
'''            int dmg = n.TryGetProperty("damage", out var dm) ? dm.GetInt32() : 4;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 2;
            bool B(string k) => n.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            var eff = new NameConditionEffect(trig, dmg, dur)
            {
                HalveAttack = B("halve_attack"),
                Backfire = B("backfire"),
                Shun = B("shun"),
                PerTrigger = B("per_trigger"),
                Grouped = B("grouped"),
            };
            // "trigger": "none" is a Name that never breaks (Not Me only forbids a target).
            if (n.TryGetProperty("trigger", out var tn) && tn.ValueKind == JsonValueKind.String && tn.GetString() == "none")
                eff.Triggers = NameTrigger.None;
            return eff.WithTag("Control");'''),
('''            int tiles = n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 2;
            bool dir = n.TryGetProperty("mode", out var m) && m.GetString() == "direction";
            return new CompelWalkEffect(tiles, dir).WithTag("Movement");''',
'''            int tiles = n.TryGetProperty("tiles", out var t) ? t.GetInt32() : 2;
            string mode = n.TryGetProperty("mode", out var m) ? m.GetString() : "toward_caster";
            return new CompelWalkEffect(tiles, mode == "direction") { Chosen = mode == "chosen_path" }.WithTag("Movement");'''),
('''        // Mana Tithe (slice 12a, ruled 2026-10-04)''',
'''        // ── Slice 13a: Geas-Binder and Puppeteer ─────────────────────────
        // { "type": "misdirect_attack", "damage": n, "duration": n }
        RegisterEffect("misdirect_attack", n =>
        {
            int dmg = n.TryGetProperty("damage", out var dm) ? dm.GetInt32() : 0;
            int dur = n.TryGetProperty("duration", out var d) ? d.GetInt32() : 1;
            return new MisdirectAttackEffect(dmg, dur).WithTag("Control");
        });
        // { "type": "borrow_will" } with unit_then_tile + any_tile
        RegisterEffect("borrow_will", _ => new BorrowWillEffect().WithTag("Control"));
        // { "type": "draw_on_break", "cap": n }
        RegisterEffect("draw_on_break", n =>
            new DrawOnBreakEffect(n.TryGetProperty("cap", out var c) ? c.GetInt32() : 3).WithTag("CardDraw"));
        // { "type": "compound_names", "growth": n }
        RegisterEffect("compound_names", n =>
            new CompoundNamesEffect(n.TryGetProperty("growth", out var g) ? g.GetInt32() : 2).WithTag("Control"));
        // { "type": "target_named" } / { "type": "name_broken_recently" }
        RegisterPredicate("target_named", _ => new TargetNamedPredicate());
        RegisterPredicate("name_broken_recently", _ => new NameBrokenRecentlyPredicate());

        // Mana Tithe (slice 12a, ruled 2026-10-04)'''),
])

# CombatManager.Enchanter: hooks, draws, redirect, command, replan, preview
CE = 'Scripts/Systems/Combat/Core/CombatManager.Enchanter.cs'
edit(CE, [
('''        if (State != null)
            State.OnIllusionsSummoned = ReplanIntentsAgainst;
        _enchanterHooksInstalled = true;''',
'''        if (State != null)
        {
            State.OnIllusionsSummoned = ReplanIntentsAgainst;
            State.OnReplanIntent = ReplanEnemy;
        }
        Names.ResetForCombat();
        IntentTime.RedirectLockHook = RedirectLockToAlly;
        IntentTime.CommandAttackHook = CommandAttack;
        _enchanterHooksInstalled = true;'''),
('''        if (State != null)
            State.OnIllusionsSummoned = null;
        _enchanterHooksInstalled = false;''',
'''        if (State != null)
        {
            State.OnIllusionsSummoned = null;
            State.OnReplanIntent = null;
        }
        IntentTime.RedirectLockHook = null;
        IntentTime.CommandAttackHook = null;
        _enchanterHooksInstalled = false;'''),
('''            GD.Print($"[Weave] {owner.Name}: a Name was broken, +1 Weave ({weave.Weave}/{WeaveAttunement.MaxWeave}).");
        }
        RefreshSelectedUnitUI();''',
'''            GD.Print($"[Weave] {owner.Name}: a Name was broken, +1 Weave ({weave.Weave}/{WeaveAttunement.MaxWeave}).");
        }
        // Fine Print (Terms and Conditions bottom): each break draws, until the drawer's next turn.
        foreach (var drawer in Names.DrawOnBreak.Keys.ToList())
        {
            var (until, left) = Names.DrawOnBreak[drawer];
            if (until < Names.Round || left <= 0 || drawer == null || !IsInstanceValid(drawer) || drawer.DeckData == null)
            {
                Names.DrawOnBreak.Remove(drawer);
                continue;
            }
            var drawn = drawer.DeckData.Draw(1);
            Names.DrawOnBreak[drawer] = (until, left - 1);
            if (drawn.Count > 0)
            {
                LogName($"Fine Print: {drawer.Name} draws a card.");
                State?.OnDrawCards?.Invoke(drawer);
            }
        }
        RefreshSelectedUnitUI();'''),
('''    /// <summary>Maze of Mirrors: every enemy whose locked intent chose <paramref name="owner"/>''',
'''    /// <summary>Not Me: one enemy plans again (it may no longer pick the unit it was Named
    /// away from). Reveal state is kept; timing is untouched.</summary>
    private void ReplanEnemy(Unit enemy)
    {
        if (!IsValidActor(enemy) || enemy == _actingEnemy)
            return;
        bool revealed = enemy.CurrentIntent?.Revealed ?? false;
        var plan = PlanIntent(enemy);
        if (plan == null)
            return;
        plan.Revealed = revealed || enemy.IntentPermanentlyRevealed;
        enemy.CurrentIntent = plan;
        UpdateIntentDisplay(enemy);
        RefreshThreatTiles();
        LogName($"{enemy.Name} turns its attention elsewhere.");
    }

    /// <summary>Speak It Wrongly: the enemy's locked attack (and its threat footprint)
    /// moves onto the nearest other enemy. A channel already charging moves with it.</summary>
    private string RedirectLockToAlly(Unit enemy)
    {
        if (!IsValidActor(enemy))
            return "it cannot act";
        if (enemy == _actingEnemy)
            return "its attack is already under way";
        var intent = enemy.CurrentIntent;
        if (intent?.TargetTile == null)
            return "it has no attack locked on a tile";
        Unit other = null;
        int best = int.MaxValue;
        foreach (var e in enemyUnits)
        {
            if (!IsValidActor(e) || e == enemy || e.TeamId != enemy.TeamId || e.CurrentTile == null)
                continue;
            int d = grid.Distance(enemy.CurrentTile.Axial, e.CurrentTile.Axial);
            if (d < best) { best = d; other = e; }
        }
        if (other == null)
            return "it has no ally to hit";
        var dest = other.CurrentTile.Axial;
        var offset = dest - intent.TargetTile.Value;
        intent.TargetTile = dest;
        intent.ThreatTiles = intent.ThreatTiles?.Select(t => t + offset).ToList() ?? new List<Vector2I> { dest };
        if (!intent.ThreatTiles.Contains(dest))
            intent.ThreatTiles.Add(dest);
        intent.TargetUnit = other;
        if (enemy.ChannelTile != null)
            enemy.ChannelTile = dest;
        UpdateIntentDisplay(enemy);
        RefreshThreatTiles();
        LogName($"{enemy.Name} is told the wrong name: its attack now falls on {other.Name}.");
        return null;
    }

    /// <summary>Borrowed Will: a non-elite enemy's next attack strikes the chosen tile. A
    /// charging channel keeps its charge and is re-aimed; anything else becomes a plain
    /// attack at its own damage. The enemy moves to reach the tile on its turn.</summary>
    private string CommandAttack(Unit enemy, Vector2I tile)
    {
        if (!IsValidActor(enemy))
            return "it cannot act";
        if (enemy.Role == "elite" || enemy.Role == "boss")
            return "an elite's will cannot be borrowed";
        if (enemy == _actingEnemy)
            return "it is already acting";
        if (enemy.CurrentTile?.Axial == tile)
            return "it will not strike its own tile";
        var occupant = grid.GetTile(tile)?.Occupant;
        var old = enemy.CurrentIntent;
        EnemyIntent plan;
        if (old != null && old.Kind == IntentKind.Release)
        {
            plan = old;
            enemy.ChannelTile = tile;
        }
        else
        {
            int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5;
            plan = new EnemyIntent
            {
                Kind = enemy.AttackRange > 1 ? IntentKind.RangedAttack : IntentKind.Attack,
                Value = dmg,
                BaseValue = dmg,
            };
        }
        plan.TargetTile = tile;
        plan.ThreatTiles = new List<Vector2I> { tile };
        plan.TargetUnit = occupant;
        plan.Revealed = true;
        enemy.CurrentIntent = plan;
        UpdateIntentDisplay(enemy);
        RefreshThreatTiles();
        LogName(occupant != null
            ? $"{enemy.Name}'s will is borrowed: its next attack falls on {occupant.Name}."
            : $"{enemy.Name}'s will is borrowed: its next attack falls on the chosen tile.");
        return null;
    }

    /// <summary>Maze of Mirrors: every enemy whose locked intent chose <paramref name="owner"/>'''),
('''        if (compel == null || compel.Direction || selectedUnit?.CurrentTile == null || _aimZone == null)
            return;''',
'''        if (compel == null || compel.Direction || compel.Chosen || selectedUnit?.CurrentTile == null || _aimZone == null)
            return;'''),
('''    /// <summary>Directional Compel waiting for its aim: outline where each of the six''',
'''    /// <summary>Puppet's Errand waiting for its destination: outline every tile the walk
    /// can reach.</summary>
    private bool ShowCompelChosenPaths(CardHalf half, Unit victim)
    {
        var compel = FindCompel(half?.Effects);
        if (compel == null || !compel.Chosen || victim?.CurrentTile == null)
            return false;
        EnsureCastRenderers();
        var reach = CompelWalk.Reachable(grid, victim, compel.Tiles);
        if (reach.Count > 0)
            _aimZone.ShowOutline(new HashSet<Vector2I>(reach.Keys), grid, AimGroundColor, 0.16f);
        return true;
    }

    /// <summary>Puppet's Errand: the legal destinations (reachable within its steps).</summary>
    private HashSet<Vector2I> CompelChosenReach(CardHalf half, Unit victim)
    {
        var compel = FindCompel(half?.Effects);
        if (compel == null || !compel.Chosen || victim?.CurrentTile == null)
            return null;
        return new HashSet<Vector2I>(CompelWalk.Reachable(grid, victim, compel.Tiles).Keys);
    }

    /// <summary>Directional Compel waiting for its aim: outline where each of the six'''),
])

# CombatManager.cs: two-step legal tiles, prompt, requirement
C = 'Scripts/Systems/Combat/Core/CombatManager.cs'
edit(C, [
('''        ClearTargetHighlight();
        _twoStepLegalTiles.Clear();
        foreach (var coord in TwoStepLegalTiles(victim, ts))
        {''',
'''        ClearTargetHighlight();
        _twoStepLegalTiles.Clear();
        _twoStepLegalHalf = half;
        foreach (var coord in TwoStepLegalTiles(victim, ts))
        {'''),
('''        string stepTwo = ShowCompelDirectionPaths(half, victim)
            ? "Click a tile beside it: it walks that way (outlined: where each direction leads)."
            : ts.StepTwoPrompt;''',
'''        string stepTwo = ShowCompelChosenPaths(half, victim)
            ? "Click a tile it can reach: it walks there by the shortest path (outlined: everywhere it can reach)."
            : ShowCompelDirectionPaths(half, victim)
            ? "Click a tile beside it: it walks that way (outlined: where each direction leads)."
            : ts.StepTwoPrompt;'''),
('''        if (ts is SelectUnitThenTileTarget tt)
        {
            foreach (var td in grid.Tiles.Values)
            {
                if (td == null || td.Occupant != null || !td.CanEnter(victim))
                    continue;''',
'''        if (ts is SelectUnitThenTileTarget tt)
        {
            // Puppet's Errand: only where the walk can actually get to.
            var reach = CompelChosenReach(_twoStepLegalHalf, victim);
            foreach (var td in grid.Tiles.Values)
            {
                if (td == null)
                    continue;
                if (reach != null)
                {
                    if (reach.Contains(td.Axial))
                        yield return td.Axial;
                    continue;
                }
                if (tt.anyTile)
                {
                    // Borrowed Will: any tile in reach but its own, occupied or not.
                    if (td.Axial != victim.CurrentTile.Axial
                        && grid.Distance(victim.CurrentTile.Axial, td.Axial) <= tt.destRange)
                        yield return td.Axial;
                    continue;
                }
                if (td.Occupant != null || !td.CanEnter(victim))
                    continue;'''),
('''    private IEnumerable<Vector2I> TwoStepLegalTiles(Unit victim, SelectTwoStepTarget ts)''',
'''    /// <summary>The card half whose second pick is being armed (Puppet's Errand reads it).</summary>
    private CardHalf _twoStepLegalHalf;

    private IEnumerable<Vector2I> TwoStepLegalTiles(Unit victim, SelectTwoStepTarget ts)'''),
('''                case "friendly_glyph_tile":''',
'''                case "non_elite_target":
                {
                    Unit first = null;
                    if (targets?.Items != null)
                        foreach (var obj in targets.Items)
                        {
                            first = obj as Unit ?? (obj as TileData)?.Occupant;
                            if (first != null)
                                break;
                        }
                    if (first != null && (first.Role == "elite" || first.Role == "boss"))
                    {
                        failReason = "An elite's will cannot be borrowed!";
                        return false;
                    }
                    break;
                }

                case "friendly_glyph_tile":'''),
])

# EnemyIntents: PlanIntent wrapper (Not Me), backfire at release/imbue
EI = 'Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs'
edit(EI, [
('''    private EnemyIntent PlanIntent(Unit enemy)
    {
        _behaviorPlanners ??=''',
'''    private EnemyIntent PlanIntent(Unit enemy)
    {
        // Not Me (Enchanter): while this enemy plans, units it is Named away from read
        // as untargetable to every planner (Unit.HasStatus).
        var prev = Unit.PlanningFor;
        Unit.PlanningFor = enemy;
        try { return PlanIntentCore(enemy); }
        finally { Unit.PlanningFor = prev; }
    }

    private EnemyIntent PlanIntentCore(Unit enemy)
    {
        _behaviorPlanners ??='''),
('''                OnEnemySpellCast(enemy);
                if (IsRitualist(enemy))
                    await ExecuteRitualRelease(enemy, intent);''',
'''                OnEnemySpellCast(enemy);
                if (!IsRitualist(enemy) && !IsWarpChanneler(enemy))
                    ApplyBackfire(enemy, intent);
                if (IsRitualist(enemy))
                    await ExecuteRitualRelease(enemy, intent);'''),
('''                OnEnemySpellCast(enemy);
                await ExecuteImbueIntent(enemy, intent);''',
'''                OnEnemySpellCast(enemy);
                ApplyBackfire(enemy, intent);
                await ExecuteImbueIntent(enemy, intent);'''),
('''    private async Task ExecuteIntent(Unit enemy)
    {''',
'''    /// <summary>Forbidden Word: a caster Named with Backfire has its spell land on its own
    /// tile. The release strike is aimed at its feet (and lands: see ResolveStrike); an
    /// imbue's footprint is moved so it is centred on the caster.</summary>
    private readonly HashSet<Unit> _backfiring = new();

    private void ApplyBackfire(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || enemy.CurrentTile == null || intent == null)
            return;
        if (!enemy.Names.Any(n => n.Backfire && n.TurnsRemaining > 0))
            return;
        var home = enemy.CurrentTile.Axial;
        var from = enemy.ChannelTile ?? intent.TargetTile;
        if (from != null && intent.ThreatTiles != null)
        {
            var offset = home - from.Value;
            intent.ThreatTiles = intent.ThreatTiles.Select(t => t + offset).ToList();
        }
        intent.TargetTile = home;
        if (enemy.ChannelTile != null || intent.Kind == IntentKind.Release)
            enemy.ChannelTile = home;
        _backfiring.Add(enemy);
        string msg = $"The Forbidden Word turns {enemy.Name}'s spell back on itself!";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);
    }

    private async Task ExecuteIntent(Unit enemy)
    {'''),
('''        else if (victim == attacker)
        {''',
'''        else if (victim == attacker && _backfiring.Remove(attacker))
        {
            // Forbidden Word: the spell lands on its caster.
            string bf = $"{attackerName}'s spell lands on itself for {damage}!";
            GD.Print(bf);
            combatUI?.AppendActionLog(bf);
            victim.ApplyDamage(damage, attacker, Delivery.Bolt);
            LastStrikeVictim = victim;
        }
        else if (victim == attacker)
        {'''),
])
print("wiring ok")
