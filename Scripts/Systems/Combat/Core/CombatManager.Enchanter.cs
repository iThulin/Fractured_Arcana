using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatManager.Enchanter.cs  (partial of CombatManager)
//
// Purpose:        Wiring for the Enchanter engine (EnchanterEngine.cs,
//                 class_identity_enchanter_v1 §2, slice 11):
//                 • Name breaks: a walked step (the unit's own move, or
//                   a Compel) breaks "if it moves"; ResolveStrike breaks
//                   "if it attacks"; channel, release and imbue intents
//                   break "if it casts".
//                 • A break gives the Namer +1 Weave (§2c).
//                 • Names tick down at each player turn start.
//                 • Compel previews: the walk path while dragging a
//                   toward-you Compel over an enemy, and every direction's
//                   path when a directional Compel picks its aim.
// Layer:          Combat
// Collaborators:  Names / NameCondition / CompelWalk (EnchanterEngine.cs),
//                 Unit.OnUnitEnteredTile, WeaveAttunement, CastPreview
// See:            docs/class_identity_enchanter_v1.md §2
// ============================================================

public partial class CombatManager
{
    private bool _enchanterHooksInstalled;

    private void InstallEnchanterHooks()
    {
        if (_enchanterHooksInstalled)
            return;
        Unit.OnUnitEnteredTile += OnEnchanterUnitEntered;
        Names.NameBroken += OnNameBroken;
        if (State != null)
        {
            State.OnIllusionsSummoned = ReplanIntentsAgainst;
            State.OnReplanIntent = ReplanEnemy;
        }
        Names.ResetForCombat();
        IntentTime.RedirectLockHook = RedirectLockToAlly;
        IntentTime.CommandAttackHook = CommandAttack;
        _enchanterHooksInstalled = true;
    }

    private void UninstallEnchanterHooks()
    {
        if (!_enchanterHooksInstalled)
            return;
        Unit.OnUnitEnteredTile -= OnEnchanterUnitEntered;
        Names.NameBroken -= OnNameBroken;
        if (State != null)
        {
            State.OnIllusionsSummoned = null;
            State.OnReplanIntent = null;
        }
        IntentTime.RedirectLockHook = null;
        IntentTime.CommandAttackHook = null;
        _enchanterHooksInstalled = false;
    }

    private void LogName(string msg) => combatUI?.AppendActionLog(msg);

    private void OnEnchanterUnitEntered(Unit unit, TileData tile, MovementKind kind)
    {
        // A unit that left its ward loses the aura, however it moved.
        State?.Glyphs?.OnUnitEntered(unit, tile);

        // Dispel Walk: the caster strips enemies it passes this round.
        if (unit != null && unit.DispelWalkRound == Names.Round && unit.DispelWalkRound >= 0)
            DispelEffect.DispelAdjacent(State, unit);

        // Absolute Territory: every tile an enemy walks inside costs it.
        if (kind == MovementKind.Walked && unit != null && tile != null && unit.Stats.IsAlive)
        {
            var zone = AbsoluteTerritoryZone.Holding(State, unit, tile.Axial);
            if (zone != null && zone.DamagePerTile > 0)
            {
                unit.ApplyDamage(zone.DamagePerTile);
                LogName($"{unit.Name} walks in Absolute Territory: {zone.DamagePerTile} damage.");
            }
        }
        if (kind == MovementKind.Walked && unit != null && unit.Names.Count > 0)
            Names.Break(unit, NameTrigger.Move, LogName);
    }

    // ── Strikes (slice 12a) ─────────────────────────────────────────────

    /// <summary>Enchanter adjustments to a strike before it lands: an attacker beside an
    /// opposing Sovereign Pillar deals less.</summary>
    private int EnchanterStrikeDamage(Unit attacker, int damage)
    {
        if (attacker == null || !IsInstanceValid(attacker) || State?.Glyphs == null || damage <= 0)
            return damage;
        int cut = State.Glyphs.PillarDamageReduction(attacker);
        if (cut > 0)
        {
            combatUI?.AppendActionLog($"A Sovereign Pillar blunts {attacker.Name}'s blow (-{cut}).");
            damage = Math.Max(0, damage - cut);
        }
        // Binding Chains: a Name that halves its bearer's attacks.
        if (attacker.Names.Any(n => n.HalveAttack && n.TurnsRemaining > 0))
        {
            damage /= 2;
            combatUI?.AppendActionLog($"{attacker.Name}'s chains halve the blow.");
        }
        // Absolute Territory: enemies inside deal half.
        if (attacker.CurrentTile != null && AbsoluteTerritoryZone.Holding(State, attacker, attacker.CurrentTile.Axial) != null)
        {
            damage /= 2;
            combatUI?.AppendActionLog($"Absolute Territory halves {attacker.Name}'s blow.");
        }
        return damage;
    }

    /// <summary>Mirror Ward: a strike on a unit standing on its own team's mirror glyph turns
    /// back on the attacker (with the ward's bonus). Returns true when it did, and the
    /// victim takes nothing. Spends a charge; a spent mirror is removed.</summary>
    private bool TryReflectStrike(Unit attacker, Unit victim, int damage)
    {
        if (attacker == null || victim == null || !IsInstanceValid(attacker) || !IsInstanceValid(victim)
            || damage <= 0 || victim.TeamId == attacker.TeamId)
            return false;
        var tile = victim.CurrentTile;
        var g = tile?.Glyph;
        if (g == null || g.ReflectCharges <= 0 || g.OwnerTeam != victim.TeamId)
            return false;

        int back = (int)Math.Ceiling(damage * (1f + g.ReflectBonus));
        string msg = $"The mirror under {victim.Name} turns {attacker.Name}'s blow back on it: {back} damage.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);
        attacker.ApplyDamage(back, victim, Delivery.Bolt);
        if (g.Owner?.Attunement is WeaveAttunement w)
            w.OnGlyphTriggered();
        if (g.ReflectCharges < 99)
        {
            g.ReflectCharges--;
            if (g.ReflectCharges <= 0)
                State.Glyphs.Remove(tile);
        }
        return true;
    }

    /// <summary>An enemy cast a spell (an intent that breaks "if it casts"): Fate Weaver
    /// glyphs near it go off too. "Any spell" means theirs as well.</summary>
    private void OnEnemySpellCast(Unit enemy)
    {
        if (enemy?.CurrentTile == null || State?.Glyphs == null)
            return;
        State.Glyphs.OnSpellCastAt(State, enemy.TeamId, enemy.CurrentTile.Axial);
    }

    private void OnNameBroken(Unit unit, NameCondition name)
    {
        var owner = name.OwnerUnit;
        if (owner != null && IsInstanceValid(owner) && owner.Attunement is WeaveAttunement weave)
        {
            weave.Add(1);
            GD.Print($"[Weave] {owner.Name}: a Name was broken, +1 Weave ({weave.Weave}/{WeaveAttunement.MaxWeave}).");
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
        RefreshSelectedUnitUI();
    }

    /// <summary>Start of a player turn: the break round advances and every Name ticks.</summary>
    private void TickNames()
    {
        Names.Round = roundNumber;
        if (State?.UnitsInPlay != null)
            Names.Tick(State.UnitsInPlay);
        TickIllusions();
    }

    /// <summary>Maze of Mirrors: timed copies fade at the start of a player turn.</summary>
    private void TickIllusions()
    {
        if (State?.UnitsInPlay == null)
            return;
        foreach (var u in State.UnitsInPlay.ToList())
        {
            if (u == null || !IsInstanceValid(u) || u.IllusionOwner == null || !u.Stats.IsAlive || u.IllusionTurnsLeft < 0)
                continue;
            u.IllusionTurnsLeft--;
            if (u.IllusionTurnsLeft <= 0)
            {
                LogName($"{u.Name} fades.");
                u.ApplyDamage(u.Stats.Health + u.Stats.Shield + u.Stats.Armor + 999);
            }
        }
    }

    /// <summary>Not Me: one enemy plans again (it may no longer pick the unit it was Named
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

    /// <summary>Maze of Mirrors: every enemy whose locked intent chose <paramref name="owner"/>
    /// plans again, so it can pick a copy this turn instead of next. Planning is safe to
    /// repeat (PlanAllEnemyIntents runs redundantly by design); the Defer/Advance timing
    /// and the reveal state are kept, because only the target changed.</summary>
    private void ReplanIntentsAgainst(Unit owner)
    {
        if (owner == null)
            return;
        bool any = false;
        foreach (var enemy in enemyUnits)
        {
            if (!IsValidActor(enemy) || enemy.CurrentIntent?.TargetUnit != owner)
                continue;
            bool revealed = enemy.CurrentIntent.Revealed;
            var plan = PlanIntent(enemy);
            if (plan == null)
                continue;
            plan.Revealed = revealed || enemy.IntentPermanentlyRevealed;
            enemy.CurrentIntent = plan;
            UpdateIntentDisplay(enemy);
            any = true;
            GD.Print($"[Mirror] {enemy.Name} re-aims at {plan.TargetUnit?.Name ?? "a tile"}.");
        }
        if (any)
            RefreshThreatTiles();
    }

    /// <summary>Maze of Mirrors: while copies of <paramref name="picked"/> stand, an enemy
    /// that chose it gets it or one of its copies, at random.</summary>
    private Unit MirrorPick(Unit picked)
    {
        if (picked == null || State?.UnitsInPlay == null)
            return picked;
        var pool = State.UnitsInPlay
            .Where(u => u != null && IsInstanceValid(u) && u.Stats.IsAlive && u.IllusionOwner == picked && u.CurrentTile != null)
            .ToList();
        if (pool.Count == 0)
            return picked;
        pool.Add(picked);
        return pool[(int)(GD.Randi() % (uint)pool.Count)];
    }

    // ── Compel previews ─────────────────────────────────────────────────

    private static CompelWalkEffect FindCompel(IEnumerable<IEffect> effects)
    {
        if (effects == null)
            return null;
        foreach (var e in effects)
        {
            if (e is CompelWalkEffect c)
                return c;
            if (e is EffectBase eb)
            {
                var nested = FindCompel(eb.Children);
                if (nested != null)
                    return nested;
            }
        }
        return null;
    }

    /// <summary>Drag-hover over an enemy with a toward-you Compel: outline the walk.</summary>
    private void ShowCompelPathPreview(CardHalf half, Vector2I aim)
    {
        var compel = FindCompel(half?.Effects);
        if (compel == null || compel.Direction || compel.Chosen || selectedUnit?.CurrentTile == null || _aimZone == null)
            return;
        var victim = grid.GetTile(aim)?.Occupant;
        if (victim == null || !victim.Stats.IsAlive || victim.TeamId == selectedUnit.TeamId)
            return;
        var path = CompelWalk.Plan(grid, victim, compel.Tiles, selectedUnit.CurrentTile.Axial, null);
        if (path.Count > 0)
            _aimZone.ShowOutline(new HashSet<Vector2I>(path), grid, AimGroundColor, 0.22f);
        combatUI?.SetHintText(path.Count == 0
            ? $"{victim.DisplayName} has nowhere to walk toward you."
            : $"Compel: {victim.DisplayName} walks {path.Count} tile(s). Glyphs and element tiles on the way trigger.");
    }

    /// <summary>Puppet's Errand waiting for its destination: outline every tile the walk
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

    /// <summary>Directional Compel waiting for its aim: outline where each of the six
    /// directions would walk it, and say so in the prompt.</summary>
    private bool ShowCompelDirectionPaths(CardHalf half, Unit victim)
    {
        var compel = FindCompel(half?.Effects);
        if (compel == null || !compel.Direction || victim?.CurrentTile == null)
            return false;
        EnsureCastRenderers();
        var all = new HashSet<Vector2I>();
        foreach (var dir in HexDirection.All)
            foreach (var c in CompelWalk.Plan(grid, victim, compel.Tiles, null, dir))
                all.Add(c);
        if (all.Count > 0)
            _aimZone.ShowOutline(all, grid, AimGroundColor, 0.16f);
        return true;
    }
}
