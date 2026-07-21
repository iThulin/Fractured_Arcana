using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// ============================================================
// CombatManager.EnemyIntents.cs  (partial of CombatManager)
//
// Purpose:        The enemy intent system — Into-the-Breach-style
//                 telegraphed AI. Splits the old ActEnemyUnit flow
//                 into PLAN (end of enemy phase: every enemy decides
//                 and locks its action, visible to the player all
//                 turn) and EXECUTE (enemy phase: each unit carries
//                 out its locked plan, re-validated against the
//                 board the player just rearranged).
//
//                 LOCKING RULES:
//                 - Attacks / shots / channels are TILE-locked: the
//                   strike resolves against the planned tile and
//                   whatever stands on it at execution — including
//                   the enemy's own allies, or nothing. Repositioning
//                   and pushing things into/out of threat tiles is
//                   the core counterplay verb.
//                 - Guard/buff intents are UNIT-locked (self/ally).
//                 - Chase rule: melee units path toward the locked
//                   TILE, not the unit that used to be there.
//
//                 INFORMATION TIERS:
//                 - Intent KIND is always visible (glyph + "?").
//                 - Full details (value + threat tiles) require a
//                   reveal — RevealIntent / RevealAllIntents, the
//                   API the per-school Mage Sight cards will call.
//                 - Reveals last until the unit re-plans (one round)
//                   unless unit.IntentPermanentlyRevealed (the
//                   Adept/Namer "true name" hook).
//                 - To run FULLY hidden instead, set
//                   ShowIntentKindByDefault = false.
//
//                 CHRONOMANCER COMPATIBILITY (no effect changes):
//                 - PostponedTurns: the intent persists un-executed —
//                   the telegraphed strike visibly hangs for another
//                   round. Existing postpone effects work unchanged.
//                 - RedirectedChargeTile: consumed at execution as a
//                   retarget of the locked tile. Works on ANY
//                   tile-locked intent now, not just charges.
//                 - Decoys: planning uses the same targeting as
//                   before, so decoys now bait LOCKED attacks that
//                   keep swinging at the decoy's tile.
//
//                 U2 — BEHAVIOR DISPATCH + TAGS (units doc §4/4a):
//                 - PlanIntent dispatches on Unit.BehaviorKey via a
//                   string → handler map (the EnemyArchetype enum is
//                   deleted). Unknown keys warn once, fall back to
//                   melee_advance. New key: melee_hunt_wounded (the
//                   doc's 'stalker') — targets the lowest-current-HP
//                   player unit instead of the nearest.
//                 - BehaviorTags compose around the base routine:
//                     pack    — +1 dmg while adjacent to a living
//                               pack ally, checked at STRIKE time
//                               (splitting the pack before execution
//                               denies the bonus — same counterplay
//                               grammar as tile-locking); movement
//                               steps prefer pack-adjacent tiles on
//                               distance ties.
//                     bulwark — will not move while an adjacent ally
//                               is below half HP (plants; still
//                               strikes if its mark is in reach).
//                     charge  — melee sprint: takes steps until
//                               adjacent or AP runs out (legacy
//                               routines step once per turn); +1 dmg
//                               when it moved and arrived.
//                     scout   — re-aims at plan time to a target
//                               outside radius 2 when 2+ player units
//                               crowd within 2 ("breaks off");
//                               movement steps prefer flanking tiles
//                               (adjacent to the mark, not adjacent
//                               to other player units) on ties.
//                     immobile— never moves (turrets, rooted growths).
//                 - Telegraph honesty: EnemyIntent.Value is the
//                   plan-time ESTIMATE (base + tag bonuses as the
//                   board stood at planning); EnemyIntent.BaseValue
//                   is the untagged base, and execution recomputes
//                   tag bonuses against the board the player left.
//                   Untagged units: BaseValue == Value, identical to
//                   pre-U2 behaviour by construction.
//
// Layer:          System (combat AI)
// Collaborators:  CombatManager.cs (main partial: enemy/player unit
//                 lists, movement helpers, UI refresh, FindNearest-
//                 PlayerUnit), Unit.cs (CurrentIntent, ChannelTile,
//                 intent display), HexTile.cs (SetThreatHighlight),
//                 UITheme.cs (TileThreat), CameraController (FocusOn)
//
// REMOVE from the main CombatManager file when adding this partial:
//   RunEnemyTurn, ActEnemyUnit, ActSoldier, ActBrute, ActDefender,
//   ActRanger, ActWizard.
// KEEP in the main file (this partial calls them):
//   MoveToDistance, MoveAwayFrom, CountAdjacentAllies, IsValidActor,
//   FindNearestPlayerUnit, ProcessStatusEffects, ApplyHazardDamage,
//   PerformAttack, PerformRangedAttack (legacy callers may remain).
// ============================================================

public enum IntentKind
{
    Attack,        // melee strike at a locked tile
    RangedAttack,  // ranged shot at a locked tile (LOS at execution)
    Channel,       // turn 1 of the wizard's two-turn blast (locked tile)
    Release,       // turn 2 — the blast lands on the locked tile
    Guard,         // defender reposition + self armor
    Unknown
}

/// <summary>One enemy's locked plan for the coming enemy phase.</summary>
public class EnemyIntent
{
    public IntentKind Kind = IntentKind.Unknown;
    /// <summary>Unit reference for orientation only (ranged kiting, chase fallback). Attacks resolve against TargetTile, never this.</summary>
    public Unit TargetUnit;
    /// <summary>The locked tile for Attack / RangedAttack / Channel / Release.</summary>
    public Vector2I? TargetTile;
    /// <summary>Tiles painted as threatened when revealed.</summary>
    public List<Vector2I> ThreatTiles = new();
    /// <summary>Damage / armor value shown when revealed — the plan-time telegraph,
    /// including behavior-tag bonuses as estimated at planning.</summary>
    public int Value;
    /// <summary>Untagged base value. Execution recomputes tag bonuses from this
    /// against the board as the player left it (U2). Equals Value for untagged units.</summary>
    public int BaseValue;
    /// <summary>Full details visible (value + threat tiles). Kind glyph shows regardless when ShowIntentKindByDefault.</summary>
    public bool Revealed;
}

public partial class CombatManager
{
    // ── Tuning / configuration ───────────────────────────────────────────────

    /// <summary>True (default): intent KIND glyph always visible, details need a reveal. False: everything hidden until revealed.</summary>
    public bool ShowIntentKindByDefault = true;

    /// <summary>Camera beat before each enemy acts, so the glide arrives before the action lands.</summary>
    private const float EnemyFocusBeat = 0.4f;

    /// <summary>Bonus damage on the wizard's released channel blast.</summary>
    private const int ChannelReleaseBonus = 3;

    /// <summary>Armor a Guard intent grants its owner.</summary>
    private const int GuardArmorValue = 2;

    // Glyphs chosen from ranges Label3D fonts reliably cover (the project
    // already renders ✦ ✧ ● ◆). Swap here if any draw as boxes.
    private static string IntentGlyph(IntentKind kind) => kind switch
    {
        IntentKind.Attack => "▲",
        IntentKind.RangedAttack => "►",
        IntentKind.Channel => "✦",
        IntentKind.Release => "✸",
        IntentKind.Guard => "◆",
        _ => "?"
    };

    private readonly HashSet<Vector2I> _paintedThreatTiles = new();

    /// <summary>Floating reticle glyphs over threatened tiles, keyed by coord.
    /// Markers, not tints — the threat TINT layers under the move-zone overlay
    /// and disappears whenever the locked tile is inside the player's movement
    /// range (which, for shots aimed at player units, is nearly always).</summary>
    private readonly Dictionary<Vector2I, Label3D> _threatMarkers = new();

    /// <summary>Victim of the most recent ResolveStrike — read by the channel-release
    /// slow rider so dodges/redirects carry the rider to the right unit (or nobody).</summary>
    internal Unit LastStrikeVictim;

    // ════════════════════════════════════════════════════════════════════════
    // PLANNING — runs at the end of each enemy phase (and once after
    // deployment), so intents are visible for the entire player turn.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Locks a fresh intent for every living enemy and refreshes displays.
    /// Call from: (1) deployment confirmation / first player turn start,
    /// (2) the tail of RunEnemyTurn (already wired below). Safe to call
    /// redundantly.
    /// </summary>
    public void PlanAllEnemyIntents()
    {
        foreach (var enemy in enemyUnits)
        {
            if (!IsValidActor(enemy))
                continue;

            enemy.CurrentIntent = PlanIntent(enemy);

            if (enemy.CurrentIntent != null)
                enemy.CurrentIntent.Revealed = enemy.IntentPermanentlyRevealed;

            UpdateIntentDisplay(enemy);
        }

        RefreshThreatTiles();
    }

    // ── U2: BehaviorKey → planner map. The catalog here must stay in sync with
    // UnitRegistry.AssertParityAndRoundTrip's knownKeys oracle. Adding a roster
    // key (units doc §13, U3+) = one entry here + one handler method.
    private Dictionary<string, Func<Unit, EnemyIntent>> _behaviorPlanners;
    private readonly HashSet<string> _warnedBehaviorKeys = new();

    private EnemyIntent PlanIntent(Unit enemy)
    {
        _behaviorPlanners ??= new Dictionary<string, Func<Unit, EnemyIntent>>(StringComparer.OrdinalIgnoreCase)
        {
            { "melee_advance",           PlanSoldier },
            { "melee_target_highest_hp", PlanBrute },
            { "hold_until_near",         PlanDefender },
            { "ranged_kite",             PlanRanger },
            { "ranged_charge",           PlanWizard },
            { "melee_hunt_wounded",      PlanStalker },   // units doc §4 'stalker'
        };

        if (!_behaviorPlanners.TryGetValue(enemy.BehaviorKey ?? "", out var planner))
        {
            // Fail loudly once per unknown key, then run the safest routine.
            // The registry assertion catches authored typos before they get here.
            if (_warnedBehaviorKeys.Add(enemy.BehaviorKey ?? ""))
                GD.PrintErr($"[EnemyAI] Unknown BehaviorKey '{enemy.BehaviorKey}' on {enemy.Name} — falling back to melee_advance.");
            planner = PlanSoldier;
        }

        return ApplyPlanTags(enemy, planner(enemy));
    }

    private EnemyIntent PlanSoldier(Unit enemy)
    {
        var target = FindNearestPlayerUnit(enemy);
        if (target?.CurrentTile == null)
            return null;

        var tile = target.CurrentTile.Axial;
        int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5;
        return new EnemyIntent
        {
            Kind = IntentKind.Attack,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = dmg,
            BaseValue = dmg
        };
    }

    /// <summary>The units doc's 'stalker' routine: ignores nearest-target selection
    /// and hunts the lowest-current-HP player unit — assassin pressure that punishes
    /// leaving a wounded unit exposed. Spell-level target overrides (RedirectAll,
    /// decoy auras) still apply — they rewrite reality, not preference. Taunting
    /// does NOT divert it: taunt is a nearest-selection nudge, and ignoring
    /// nearest-selection is this key's entire identity (ruling logged here).</summary>
    private EnemyIntent PlanStalker(Unit enemy)
    {
        var target = FindTargetOverride(enemy);

        if (target == null)
        {
            int worstHp = int.MaxValue;
            foreach (var player in playerUnits)
            {
                if (player == null || !IsInstanceValid(player))
                    continue;
                if (!player.Stats.IsAlive || player.CurrentTile == null)
                    continue;
                if (player.HasStatus("untargetable"))
                    continue;
                if (player.Stats.Health < worstHp)
                { worstHp = player.Stats.Health; target = player; }
            }
        }

        if (target?.CurrentTile == null)
            return null;

        // Targeting evidence for console transcripts (Session F finding: absolute
        // current HP is the metric — a full-HP 14/14 companion outranks a wounded
        // 15/20 wizard. Doc-specified; kill-proximity, not wound-seeking.)
        GD.Print($"[Stalker] {enemy.Name} marks {target.Name} " +
                 $"({target.Stats.Health}/{target.Stats.MaxHealth} HP — lowest current).");

        var tile = target.CurrentTile.Axial;
        int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5;
        return new EnemyIntent
        {
            Kind = IntentKind.Attack,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = dmg,
            BaseValue = dmg
        };
    }

    // ── U2: plan-time tag pass — retargeting + telegraph estimates ──────────

    /// <summary>Applies behavior-tag effects that belong to PLANNING: the scout
    /// break-off retarget, and telegraph (Value) estimates for pack/charge.
    /// Execution recomputes damage bonuses from BaseValue against the real board.</summary>
    private EnemyIntent ApplyPlanTags(Unit enemy, EnemyIntent intent)
    {
        if (intent == null || enemy.BehaviorTags.Count == 0)
            return intent;

        bool isStrike = intent.Kind is IntentKind.Attack or IntentKind.RangedAttack;

        // scout: when 2+ player units crowd within 2 tiles, break off — re-aim at
        // the nearest player unit OUTSIDE that radius (the flank target). If every
        // living target is crowding it, keep the original mark.
        if (isStrike && enemy.HasBehaviorTag("scout") && enemy.CurrentTile != null)
        {
            int crowding = 0;
            foreach (var player in playerUnits)
            {
                if (player == null || !IsInstanceValid(player) || !player.Stats.IsAlive || player.CurrentTile == null)
                    continue;
                if (grid.Distance(enemy.CurrentTile, player.CurrentTile) <= 2)
                    crowding++;
            }

            if (crowding >= 2)
            {
                Unit flankTarget = null;
                int bestDist = int.MaxValue;
                foreach (var player in playerUnits)
                {
                    if (player == null || !IsInstanceValid(player) || !player.Stats.IsAlive || player.CurrentTile == null)
                        continue;
                    if (player.HasStatus("untargetable"))
                        continue;
                    int d = grid.Distance(enemy.CurrentTile, player.CurrentTile);
                    if (d > 2 && d < bestDist)
                    { bestDist = d; flankTarget = player; }
                }

                if (flankTarget != null)
                {
                    var flankTile = flankTarget.CurrentTile.Axial;
                    intent.TargetUnit = flankTarget;
                    intent.TargetTile = flankTile;
                    intent.ThreatTiles.Clear();
                    intent.ThreatTiles.Add(flankTile);
                }
            }
        }

        // Telegraph estimates for damage tags (melee strikes only). These are
        // estimates by design — the locked VALUE the player reads; execution
        // recomputes against the board they rearranged.
        if (intent.Kind == IntentKind.Attack && enemy.CurrentTile != null)
        {
            int estimate = intent.BaseValue;

            if (enemy.HasBehaviorTag("pack") &&
                CountAdjacentPackAllies(enemy, enemy.CurrentTile.Axial) > 0)
                estimate += 1;

            if (enemy.HasBehaviorTag("charge") && intent.TargetTile.HasValue)
            {
                int dist = grid.Distance(enemy.CurrentTile.Axial, intent.TargetTile.Value);
                if (dist > 1 && dist - 1 <= enemy.MaxActionPoints)
                    estimate += 1;
            }

            intent.Value = estimate;
        }

        return intent;
    }

    /// <summary>Living pack-tagged allies adjacent to <paramref name="coord"/>
    /// (excluding the unit itself). Both the damage check and the movement
    /// preference read this.</summary>
    private int CountAdjacentPackAllies(Unit unit, Vector2I coord)
    {
        int count = 0;
        foreach (var neighbor in grid.GetNeighbors(coord))
        {
            var occ = grid.GetTile(neighbor)?.Occupant;
            if (occ == null || occ == unit)
                continue;
            if (occ.TeamId == unit.TeamId && occ.Stats.IsAlive && occ.HasBehaviorTag("pack"))
                count++;
        }
        return count;
    }

    private EnemyIntent PlanBrute(Unit enemy)
    {
        // Brute targeting: highest current HP among living player units.
        Unit target = null;
        int bestHp = -1;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (u.Stats.Health > bestHp)
            { bestHp = u.Stats.Health; target = u; }
        }

        if (target == null)
            return null;

        var tile = target.CurrentTile.Axial;
        int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5;
        return new EnemyIntent
        {
            Kind = IntentKind.Attack,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = dmg,
            BaseValue = dmg
        };
    }

    private EnemyIntent PlanDefender(Unit enemy)
    {
        // Adjacent player at plan time → telegraph a locked strike on it.
        foreach (var neighbor in grid.GetNeighbors(enemy.CurrentTile.Axial))
        {
            var occ = grid.GetTile(neighbor)?.Occupant;
            if (occ != null && occ.TeamId != enemy.TeamId && occ.Stats.IsAlive)
            {
                int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5;
                return new EnemyIntent
                {
                    Kind = IntentKind.Attack,
                    TargetUnit = occ,
                    TargetTile = neighbor,
                    ThreatTiles = { neighbor },
                    Value = dmg,
                    BaseValue = dmg
                };
            }
        }

        // Otherwise: guard. Honest intent — no surprise attacks at execution.
        return new EnemyIntent
        {
            Kind = IntentKind.Guard,
            TargetUnit = enemy,
            Value = GuardArmorValue,
            BaseValue = GuardArmorValue
        };
    }

    private EnemyIntent PlanRanger(Unit enemy)
    {
        var target = FindNearestPlayerUnit(enemy);
        if (target?.CurrentTile == null)
            return null;

        var tile = target.CurrentTile.Axial;
        int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 4;
        return new EnemyIntent
        {
            Kind = IntentKind.RangedAttack,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = dmg,
            BaseValue = dmg
        };
    }

    private EnemyIntent PlanWizard(Unit enemy)
    {
        // Already channelling: the release is locked to the tile chosen when
        // the channel began — NOT re-aimed. Two full player turns of warning.
        if (enemy.HasStatus("wizard_charging") && enemy.ChannelTile.HasValue)
        {
            var locked = enemy.ChannelTile.Value;
            int rdmg = (enemy.AttackDamage > 0 ? enemy.AttackDamage : 4) + ChannelReleaseBonus;
            return new EnemyIntent
            {
                Kind = IntentKind.Release,
                TargetTile = locked,
                ThreatTiles = { locked },
                Value = rdmg,
                BaseValue = rdmg
            };
        }

        var target = FindNearestPlayerUnit(enemy);
        if (target?.CurrentTile == null)
            return null;

        var tile = target.CurrentTile.Axial;
        int dmg = (enemy.AttackDamage > 0 ? enemy.AttackDamage : 4) + ChannelReleaseBonus;
        return new EnemyIntent
        {
            Kind = IntentKind.Channel,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = dmg,
            BaseValue = dmg
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // REVEAL API — what the per-school Mage Sight cards will call.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Fully reveals one enemy's intent (value + threat tiles). Lasts until it re-plans, or permanently if markPermanent (the Adept/Namer "true name" hook).</summary>
    public void RevealIntent(Unit enemy, bool markPermanent = false)
    {
        if (enemy?.CurrentIntent == null)
            return;

        enemy.CurrentIntent.Revealed = true;
        if (markPermanent)
            enemy.IntentPermanentlyRevealed = true;

        UpdateIntentDisplay(enemy);
        RefreshThreatTiles();
        combatUI?.AppendActionLog($"{enemy.Name}'s intent is revealed!");
    }

    /// <summary>Fully reveals every living enemy's intent for this round.</summary>
    public void RevealAllIntents()
    {
        foreach (var enemy in enemyUnits)
        {
            if (IsValidActor(enemy) && enemy.CurrentIntent != null)
            {
                enemy.CurrentIntent.Revealed = true;
                UpdateIntentDisplay(enemy);
            }
        }
        RefreshThreatTiles();
        combatUI?.AppendActionLog("All enemy intents are laid bare!");
    }

    // ════════════════════════════════════════════════════════════════════════
    // DISPLAY — intent glyph over each enemy + threat tile painting.
    // ════════════════════════════════════════════════════════════════════════

    private void UpdateIntentDisplay(Unit enemy)
    {
        if (enemy == null || !IsInstanceValid(enemy))
            return;

        var intent = enemy.CurrentIntent;
        if (intent == null)
        {
            enemy.ClearIntentDisplay();
            return;
        }

        bool showKind = ShowIntentKindByDefault || intent.Revealed;
        if (!showKind)
        {
            enemy.ClearIntentDisplay();
            return;
        }

        string glyph = IntentGlyph(intent.Kind);
        string value = intent.Revealed ? intent.Value.ToString() : "?";
        string suffix = enemy.PostponedTurns > 0 ? "…" : "";

        Color color = intent.Revealed
            ? new Color(1.0f, 0.55f, 0.45f)      // revealed — hot
            : new Color(0.85f, 0.85f, 0.85f);    // kind-only — neutral

        enemy.SetIntentDisplay($"{glyph} {value}{suffix}", color);
    }

    /// <summary>
    /// Repaints threat highlights from all locked intents. Two tiers (info-tier
    /// change 2026-07-08, from Session F confusion — a locked shot whiffing on a
    /// tile the player never knew was threatened): when ShowIntentKindByDefault,
    /// EVERY locked tile paints as a dim reticle (the kind tier now includes
    /// WHERE); revealed intents paint hot. Hidden-intent mode (kind flag off)
    /// keeps the old reveal-only behaviour. Public so death handling and
    /// player-side effects can refresh after changing the board.
    /// </summary>
    public void RefreshThreatTiles()
    {
        foreach (var coord in _paintedThreatTiles)
            grid?.GetTileView(coord)?.SetThreatHighlight(false);
        _paintedThreatTiles.Clear();

        foreach (var marker in _threatMarkers.Values)
        {
            if (marker != null && IsInstanceValid(marker))
                marker.QueueFree();
        }
        _threatMarkers.Clear();

        // Merge first (OR on revealed) so an unrevealed intent sharing a tile
        // with a revealed one can never downgrade the hot tint to dim.
        var tiles = new Dictionary<Vector2I, bool>();
        foreach (var enemy in enemyUnits)
        {
            if (!IsValidActor(enemy) || enemy.CurrentIntent == null)
                continue;

            bool revealed = enemy.CurrentIntent.Revealed;
            if (!revealed && !ShowIntentKindByDefault)
                continue;

            foreach (var coord in enemy.CurrentIntent.ThreatTiles)
                tiles[coord] = tiles.TryGetValue(coord, out bool r) ? (r || revealed) : revealed;
        }

        foreach (var kvp in tiles)
        {
            var view = grid?.GetTileView(kvp.Key);
            if (view != null)
            {
                view.SetThreatHighlight(true, kvp.Value);
                SpawnThreatMarker(view, kvp.Key, kvp.Value);
                _paintedThreatTiles.Add(kvp.Key);
            }
        }
    }

    /// <summary>Creates the floating reticle over a threatened tile. Label3D via
    /// the proven glyph-label pattern (billboard, NoDepthTest, CallDeferred
    /// add_child per README §8) — sits above terrain, grass, and every tile-tint
    /// overlay, so it stays readable inside the player's move zone.</summary>
    private void SpawnThreatMarker(HexTile view, Vector2I coord, bool revealed)
    {
        var marker = new Label3D
        {
            Name = "ThreatReticle",
            Text = "◆",   // proven-render glyph set (see IntentGlyph note)
            FontSize = 44,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Position = new Vector3(0f, 0.85f, 0f),
            Modulate = revealed ? UITheme.TileThreatReticle : UITheme.TileThreatReticleDim,
        };
        _threatMarkers[coord] = marker;
        view.CallDeferred("add_child", marker);
    }

    // ════════════════════════════════════════════════════════════════════════
    // EXECUTION — replaces the old RunEnemyTurn. Each unit carries out its
    // locked intent against the board as the player left it.
    // ════════════════════════════════════════════════════════════════════════

    private async Task RunEnemyTurn()
    {
        // U3: settle any triggers queued since the last drain (deaths from the
        // player's turn resolve their stack before enemies act — e.g. terrain
        // ticks killing a Wake-Keeper's ally at the turn boundary).
        await DrainTriggerStackAsync();

        // Time Bank (2026-07-10): reaction costs may draw on banked Foresight for
        // the rest of this phase; a FULL bank grants one free Reaction.
        State.EnemyPhaseContext = true;
        foreach (var pu in playerUnits)
            if (pu != null && IsInstanceValid(pu) && pu.Stats.IsAlive
                && pu.Attunement is FateAttunement fateWindow)
                fateWindow.OnEnemyTurnStart();

        var snapshot = enemyUnits.ToList();
        foreach (var enemy in snapshot)
        {
            if (enemy == null || !IsInstanceValid(enemy) || !enemy.Stats.IsAlive)
                continue;

            CombatCamera?.FocusOn(enemy);
            combatUI?.SetActiveEnemy(enemy);   // V2: roster row = enemy-phase progress bar
            await ToSignal(GetTree().CreateTimer(EnemyFocusBeat), "timeout");

            // ── Negate (Adept Counterspell): the action is cancelled outright ──
            if (enemy.NegateNextAction)
            {
                enemy.NegateNextAction = false;
                GD.Print($"{enemy.Name}'s action is negated!");
                combatUI?.AppendActionLog($"{enemy.Name}'s action is negated!");
                enemy.CurrentIntent = null;
                enemy.ClearIntentDisplay();
                continue;
            }

            // ── Postpone (Chronomancer): the locked strike hangs un-executed ──
            if (enemy.PostponedTurns > 0)
            {
                enemy.PostponedTurns--;
                GD.Print($"{enemy.Name} is delayed — its strike hangs " +
                         $"({enemy.PostponedTurns} more turn(s)).");
                combatUI?.AppendActionLog($"{enemy.Name} is delayed!");
                UpdateIntentDisplay(enemy); // refresh the "…" suffix
                continue;                   // intent persists into next phase
            }

            // ── Disabled units lose their action; channels break ─────────────
            if (!enemy.CanAct())
            {
                string reason = enemy.HasStatus("bound") ? "bound"
                            : enemy.HasStatus("stunned") ? "stunned"
                            : "frozen";

                if (enemy.CurrentIntent?.Kind is IntentKind.Channel or IntentKind.Release)
                {
                    enemy.ChannelTile = null;
                    enemy.RemoveStatus("wizard_charging");
                    combatUI?.AppendActionLog($"{enemy.Name}'s channel is broken!");
                }

                GD.Print($"{enemy.Name} is {reason} — its plan fizzles.");
                combatUI?.AppendActionLog($"{enemy.Name} is {reason}!");
                enemy.CurrentIntent = null;
                enemy.ClearIntentDisplay();
                continue;
            }

            // ── Redirect (Chronomancer): retarget the locked tile ────────────
            if (enemy.RedirectedChargeTile.HasValue && enemy.CurrentIntent?.TargetTile != null)
            {
                var newTile = enemy.RedirectedChargeTile.Value;
                enemy.RedirectedChargeTile = null;
                enemy.CurrentIntent.TargetTile = newTile;
                enemy.CurrentIntent.ThreatTiles.Clear();
                enemy.CurrentIntent.ThreatTiles.Add(newTile);
                if (enemy.CurrentIntent.Kind == IntentKind.Release)
                    enemy.ChannelTile = newTile;
                GD.Print($"{enemy.Name}'s intent is redirected to {newTile}.");
                // §9 reaction grammar: intent retarget reads as a Redirect line.
                combatUI?.AppendActionLog(UIContent.FormatLogLine(enemy.Name, "Redirect",
                    $"locked strike drawn to ({newTile.X}, {newTile.Y})"));
            }

            await ExecuteIntent(enemy);

            // U3: an intent's kills queue triggers — resolve the stack (with
            // priority windows) before the next enemy acts.
            await DrainTriggerStackAsync();

            if (enemy != null && IsInstanceValid(enemy))
            {
                enemy.CurrentIntent = null;
                enemy.ClearIntentDisplay();
            }

            if (CheckCombatEnd())
                return;
        }

        GD.Print("=== Enemy Turn End ===");
        enemyPhaseRunning = false;
        combatUI?.SetActiveEnemy(null);   // V2: clear the acting-row marker

        // U3: settle stragglers BEFORE planning, so units risen by Deathburst
        // this turn lock visible intents for the coming player turn.
        await DrainTriggerStackAsync();

        // Lock next round's plans NOW so they're visible all player turn.
        PlanAllEnemyIntents();
    }

    private async Task ExecuteIntent(Unit enemy)
    {
        var intent = enemy.CurrentIntent;
        if (intent == null)
        {
            // No plan (spawned mid-round, or planning found no target) —
            // fall back to one fresh decision, unannounced. U2: routed through
            // PlanIntent so the unit's own key/tags apply, not a soldier default.
            intent = PlanIntent(enemy);
            if (intent == null)
                return;
        }

        switch (intent.Kind)
        {
            case IntentKind.Attack:
                await ExecuteMeleeIntent(enemy, intent);
                break;
            case IntentKind.RangedAttack:
                await ExecuteRangedIntent(enemy, intent);
                break;
            case IntentKind.Channel:
                await ExecuteChannelStart(enemy, intent);
                break;
            case IntentKind.Release:
                await ExecuteChannelRelease(enemy, intent);
                break;
            case IntentKind.Guard:
                await ExecuteGuardIntent(enemy, intent);
                break;
        }
    }

    // ── Melee: chase the LOCKED TILE, strike whatever stands on it ──────────

    private async Task ExecuteMeleeIntent(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || intent.TargetTile == null)
            return;

        var tile = intent.TargetTile.Value;
        bool chargedIn = false;

        if (grid.Distance(enemy.CurrentTile.Axial, tile) > 1)
        {
            if (!MayMove(enemy, out string plantReason))
            {
                if (plantReason != null)
                {
                    GD.Print(plantReason);
                    combatUI?.AppendActionLog(plantReason);
                }
            }
            else if (enemy.HasBehaviorTag("charge"))
            {
                // charge: sprint the full AP budget toward the mark instead of the
                // legacy single step; the +1 lands only if it MOVED and ARRIVED.
                bool moved = await SprintTowardTile(enemy, tile);
                chargedIn = moved && IsValidActor(enemy) &&
                            grid.Distance(enemy.CurrentTile.Axial, tile) <= 1;
            }
            else
            {
                await MoveTowardTile(enemy, tile);
            }
        }

        if (!IsValidActor(enemy))
            return;

        if (grid.Distance(enemy.CurrentTile.Axial, tile) <= 1)
        {
            // U2: recompute tag bonuses against the board as the player left it.
            // Tag-evidence lines are GD.Print-mirrored: console transcripts are the
            // verification medium (u2_verification.md), and AppendActionLog alone
            // is invisible there — caught in Session B, 2026-07-08.
            int dmg = intent.BaseValue;
            if (enemy.HasBehaviorTag("pack") &&
                CountAdjacentPackAllies(enemy, enemy.CurrentTile.Axial) > 0)
            {
                dmg += 1;
                string packMsg = $"{enemy.Name} strikes with the pack (+1).";
                GD.Print(packMsg);
                combatUI?.AppendActionLog(packMsg);
            }
            if (chargedIn)
            {
                dmg += 1;
                string chargeMsg = $"{enemy.Name} charges in (+1)!";
                GD.Print(chargeMsg);
                combatUI?.AppendActionLog(chargeMsg);
            }
            await StrikeTile(enemy, tile, dmg, ranged: false);
        }
        else
            combatUI?.AppendActionLog($"{enemy.Name} can't reach its mark.");
    }

    // ── U2: movement gates + tag-aware stepping ─────────────────────────────

    /// <summary>Movement gate for immobile/bulwark. False = the unit stays put
    /// this activation; <paramref name="reason"/> carries the log line (null for
    /// immobile — a turret not moving is not news).</summary>
    private bool MayMove(Unit enemy, out string reason)
    {
        reason = null;

        if (enemy.HasBehaviorTag("immobile"))
            return false;

        // bulwark: plants while an adjacent ally is below half HP.
        if (enemy.HasBehaviorTag("bulwark") && enemy.CurrentTile != null)
        {
            foreach (var neighbor in grid.GetNeighbors(enemy.CurrentTile.Axial))
            {
                var occ = grid.GetTile(neighbor)?.Occupant;
                if (occ == null || occ == enemy || occ.TeamId != enemy.TeamId)
                    continue;
                if (occ.Stats.IsAlive && occ.Stats.Health * 2 < occ.Stats.MaxHealth)
                {
                    reason = $"{enemy.Name} plants itself in front of {occ.Name}.";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>One pathfinder step toward <paramref name="goal"/>, with pack/scout
    /// destination preference applied on DISTANCE TIES only — a tag never trades
    /// progress toward the mark for formation (units doc §4a: "when movement
    /// options tie").</summary>
    private TileData ChooseStepTowardTile(Unit enemy, Vector2I goal)
    {
        var baseline = grid.GetFirstStepToward(enemy, goal);
        if (baseline == null)
            return null;
        if (!enemy.HasBehaviorTag("pack") && !enemy.HasBehaviorTag("scout"))
            return baseline;

        int baselineDist = grid.Distance(baseline.Axial, goal);
        var best = baseline;
        int bestScore = StepPreferenceScore(enemy, baseline.Axial, goal);

        foreach (var n in grid.GetNeighbors(enemy.CurrentTile.Axial))
        {
            var tile = grid.GetTile(n);
            if (tile == null || !tile.CanEnter(enemy))
                continue;
            if (grid.Distance(n, goal) != baselineDist)
                continue; // ties only
            int score = StepPreferenceScore(enemy, n, goal);
            if (score > bestScore)
            { bestScore = score; best = tile; }
        }

        return best;
    }

    private int StepPreferenceScore(Unit enemy, Vector2I coord, Vector2I goal)
    {
        int score = 0;

        if (enemy.HasBehaviorTag("pack"))
            score += 2 * CountAdjacentPackAllies(enemy, coord);

        if (enemy.HasBehaviorTag("scout") && grid.Distance(coord, goal) <= 1)
        {
            // Flanking destination: adjacent to the mark, NOT adjacent to any
            // OTHER player unit (the mark's occupant doesn't count against it).
            bool exposed = false;
            foreach (var n in grid.GetNeighbors(coord))
            {
                if (n == goal)
                    continue;
                var occ = grid.GetTile(n)?.Occupant;
                if (occ != null && occ.TeamId != enemy.TeamId && occ.Stats.IsAlive)
                { exposed = true; break; }
            }
            if (!exposed)
                score += 3;
        }

        return score;
    }

    /// <summary>charge: step toward the goal until adjacent, out of AP, or blocked.
    /// Each step re-pathfinds (honors terrain costs, rooted/slowed via TryMoveTo's
    /// own gates). Returns whether the unit moved at all.</summary>
    private async Task<bool> SprintTowardTile(Unit enemy, Vector2I goal)
    {
        bool moved = false;
        const int SafetyCap = 12;

        for (int i = 0; i < SafetyCap; i++)
        {
            if (!IsValidActor(enemy) || grid.Distance(enemy.CurrentTile.Axial, goal) <= 1)
                break;

            var next = ChooseStepTowardTile(enemy, goal);
            if (next == null || !enemy.TryMoveTo(grid, next))
                break;

            moved = true;
            await ToSignal(GetTree().CreateTimer(0.15f), "timeout");
        }

        if (moved)
        {
            string sprintMsg = $"{enemy.Name} charges toward its mark!";
            GD.Print(sprintMsg);
            combatUI?.AppendActionLog(sprintMsg);
        }
        return moved;
    }

    // ── Ranged: kite relative to the remembered unit, shoot the LOCKED TILE ──

    private async Task ExecuteRangedIntent(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || intent.TargetTile == null)
            return;

        var tile = intent.TargetTile.Value;

        // Reposition relative to the living target if it still exists —
        // orientation only; the shot stays locked to the tile.
        // U2: immobile turrets and planted bulwarks skip the kiting move.
        if (IsValidActor(intent.TargetUnit) && MayMove(enemy, out string kiteGate))
        {
            int dist = grid.Distance(enemy.CurrentTile, intent.TargetUnit.CurrentTile);
            int preferred = enemy.AttackRange;
            int minDist = preferred - 1;

            if (dist < minDist)
                await MoveAwayFrom(enemy, intent.TargetUnit, minDist);
            else if (dist > enemy.AttackRange)
                await MoveToDistance(enemy, intent.TargetUnit, preferred);
        }

        if (!IsValidActor(enemy))
            return;

        int tileDist = grid.Distance(enemy.CurrentTile.Axial, tile);
        if (tileDist > enemy.AttackRange)
        {
            combatUI?.AppendActionLog($"{enemy.Name} — mark out of range, shot wasted.");
            return;
        }

        if (!grid.HasLineOfSight(enemy.CurrentTile.Axial, tile))
        {
            combatUI?.AppendActionLog($"{enemy.Name} has no line of sight!");
            return;
        }

        await StrikeTile(enemy, tile, intent.Value, ranged: true);
    }

    // ── Channel start: reposition, lock the tile, begin charging ────────────

    private async Task ExecuteChannelStart(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || intent.TargetTile == null)
            return;

        // Reposition relative to the remembered target (old wizard behaviour).
        // U2: immobile/planted-bulwark units channel from where they stand.
        if (IsValidActor(intent.TargetUnit) && MayMove(enemy, out string channelGate))
        {
            int dist = grid.Distance(enemy.CurrentTile, intent.TargetUnit.CurrentTile);
            int preferred = enemy.AttackRange;

            if (dist < preferred)
                await MoveAwayFrom(enemy, intent.TargetUnit, preferred);
            else if (dist > preferred + 2)
                await MoveToDistance(enemy, intent.TargetUnit, preferred);
        }

        if (!IsValidActor(enemy))
            return;

        enemy.ChannelTile = intent.TargetTile;
        enemy.ApplyStatus("wizard_charging", 2);

        GD.Print($"{enemy.Name} begins channelling at {intent.TargetTile.Value}...");
        combatUI?.AppendActionLog($"{enemy.Name} begins channelling!");
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    // ── Channel release: the blast lands on the tile locked two phases ago ──

    private async Task ExecuteChannelRelease(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy))
            return;

        Vector2I? locked = enemy.ChannelTile ?? intent.TargetTile;
        enemy.ChannelTile = null;
        enemy.RemoveStatus("wizard_charging");

        if (locked == null)
            return;

        var tile = locked.Value;

        if (grid.Distance(enemy.CurrentTile.Axial, tile) > enemy.AttackRange ||
            !grid.HasLineOfSight(enemy.CurrentTile.Axial, tile))
        {
            string missMsg = $"{enemy.Name} — the blast point is beyond reach, charge wasted.";
            GD.Print(missMsg);
            combatUI?.AppendActionLog(missMsg);
            return;
        }

        GD.Print($"{enemy.Name} releases a charged blast!");
        combatUI?.AppendActionLog($"{enemy.Name} releases a charged blast!");

        LastStrikeVictim = null;
        await StrikeTile(enemy, tile, intent.Value, ranged: true, label: "Charged Blast");

        // Slow rider follows whoever was actually hit (dodge = nobody; redirect = new victim).
        var victim = LastStrikeVictim;
        ApplyCasterRider(enemy, victim);
    }

    // ── Per-school caster riders (Step 2) — the signature effect each wizard
    // school lands on release, on top of the tile-strike damage. The generic
    // wizard (CasterSpell == "") keeps the legacy slowed-1 rider so nothing
    // regresses. Riders use only proven, symmetric statuses: burn/bleed tick
    // 3/2 per turn in ProcessStatusEffects; slowed/rooted halve/zero reach
    // read-side; stunned skips the action; poisoned drains max HP. Arcanist has
    // no rider (its signature is raw damage, baked into attackDamage). Tinker
    // forgoes the offensive rider to ward its most-wounded ally.
    private void ApplyCasterRider(Unit caster, Unit victim)
    {
        string spell = caster?.CasterSpell ?? "";

        // Tinker: repair the most-wounded ally instead of debuffing the victim.
        if (spell == "forge")
        {
            var ally = MostWoundedAlly(caster);
            if (ally != null)
            {
                ally.Stats.Armor += 3;
                ally.RefreshHealthBar();
                combatUI?.AppendActionLog($"{caster.Name} shields {ally.Name} (+3 armor).");
            }
            return;
        }

        if (victim == null || !IsInstanceValid(victim) || !victim.Stats.IsAlive)
            return;

        switch (spell)
        {
            case "ember":                          // Elementalist — burn DoT
                victim.ApplyStatus("burn", 2);
                combatUI?.AppendActionLog($"{victim.Name} is set alight (burning)!");
                break;
            case "chrono":                         // Chronomancer — deeper slow
                victim.ApplyStatus("slowed", 2);
                combatUI?.AppendActionLog($"{victim.Name} is dragged through time (slowed)!");
                break;
            case "grave":                          // Necromancer — creeping poison
                victim.ApplyStatus("poisoned", 1);
                combatUI?.AppendActionLog($"{victim.Name} is poisoned — the tab grows!");
                break;
            case "thorn":                          // Druid — root in place
                victim.ApplyStatus("rooted", 1);
                combatUI?.AppendActionLog($"{victim.Name} is bound by roots (rooted)!");
                break;
            case "mind":                           // Adept — stun (lose the action)
                victim.ApplyStatus("stunned", 1);
                combatUI?.AppendActionLog($"{victim.Name} reels, mind struck (stunned)!");
                break;
            case "geas":                           // Enchanter — bleeding geas
                victim.ApplyStatus("bleed", 2);
                combatUI?.AppendActionLog($"{victim.Name} is bound by a bleeding geas!");
                break;
            case "arclance":                       // Arcanist — pure damage, no rider
                break;
            default:                                // generic wizard — legacy rider
                victim.ApplyStatus("slowed", 1);
                combatUI?.AppendActionLog($"{victim.Name} is slowed by arcane energy!");
                break;
        }
    }

    /// <summary>Living enemy unit (or the caster itself) with the largest
    /// missing-HP fraction — target for the Tinker caster's ward.</summary>
    private Unit MostWoundedAlly(Unit caster)
    {
        Unit best = null;
        float worst = -1f;
        foreach (var u in enemyUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive)
                continue;
            float missing = 1f - (float)u.Stats.Health / Math.Max(1, u.Stats.MaxHealth);
            if (missing > worst) { worst = missing; best = u; }
        }
        return best ?? caster;
    }

    // ── Guard: defender repositioning + telegraphed armor ───────────────────

    private async Task ExecuteGuardIntent(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy))
            return;

        // U2: immobile/planted-bulwark units brace in place, skipping the
        // reposition — the plant IS the guard.
        if (!MayMove(enemy, out string guardGate))
        {
            if (guardGate != null)
            {
                GD.Print(guardGate);
                combatUI?.AppendActionLog(guardGate);
            }
            enemy.Stats.Armor += intent.Value;
            enemy.RefreshHealthBar();
            combatUI?.AppendActionLog($"{enemy.Name} braces (+{intent.Value} armor).");
            return;
        }

        // Reposition toward the most allies (old defender logic, opportunistic
        // attack removed — Guard does exactly what it telegraphed, nothing else).
        Unit nearestAlly = null;
        int nearestAllyDist = int.MaxValue;
        foreach (var u in enemyUnits)
        {
            if (u == null || u == enemy || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            int d = grid.Distance(enemy.CurrentTile, u.CurrentTile);
            if (d < nearestAllyDist)
            { nearestAllyDist = d; nearestAlly = u; }
        }

        var moveOptions = grid.GetReachableTiles(enemy);
        Vector2I bestMove = enemy.CurrentTile.Axial;
        int bestAllyCount = CountAdjacentAllies(enemy, enemy.CurrentTile.Axial);

        foreach (var coord in moveOptions)
        {
            int allyCount = CountAdjacentAllies(enemy, coord);
            if (allyCount > bestAllyCount)
            {
                bestAllyCount = allyCount;
                bestMove = coord;
            }
        }

        if (bestAllyCount == 0 && nearestAlly != null)
        {
            var nextStep = grid.GetFirstStepToward(enemy, nearestAlly.CurrentTile.Axial);
            if (nextStep != null && enemy.TryMoveTo(grid, nextStep))
            {
                combatUI?.AppendActionLog($"{enemy.Name} moves to rejoin allies.");
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }
        }
        else if (bestMove != enemy.CurrentTile.Axial)
        {
            var tile = grid.GetTile(bestMove);
            if (tile != null && enemy.TryMoveTo(grid, tile))
            {
                combatUI?.AppendActionLog($"{enemy.Name} moves to protect allies.");
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }
        }

        enemy.Stats.Armor += intent.Value;
        enemy.RefreshHealthBar();
        combatUI?.AppendActionLog($"{enemy.Name} braces (+{intent.Value} armor).");
    }

    // ── Shared: tile-locked strike resolution ───────────────────────────────

    /// <summary>
    /// Resolves a locked strike against a TILE: hits whatever stands there —
    /// a player unit, the attacker's own ally (the push-into-harm payoff), or
    /// nothing (a visible whiff the player earned).
    /// </summary>
    private async Task StrikeTile(Unit attacker, Vector2I tile, int damage, bool ranged, string label = null)
    {
        // R3 follow-on (2026-07-10): when anyone can actually respond, the strike
        // enters the stack as a respondable object — dodge by vacating the tile,
        // shield up, or redirect it. Otherwise resolve directly (pacing unchanged).
        if (!_triggerDrainRunning
            && (PlayerHoldsCastableReaction() || PlayerSession.DebugStopOnTriggers))
        {
            var victimNow = grid.GetTile(tile)?.Occupant;
            string name = label ?? (ranged ? "Shot" : "Strike");
            string intel = victimNow != null && IsInstanceValid(victimNow) && victimNow.Stats.IsAlive
                ? $"{damage} damage → {victimNow.Name}"
                : $"{damage} damage → tile ({tile.X}, {tile.Y})";

            var ability = new EnemyTriggeredAbility(name, attacker.Name,
                new EnemyStrikeEffect(this, attacker, tile, damage, ranged, victimNow), intel);
            var strikeTargets = new TargetSet();
            if (victimNow != null)
                strikeTargets.Items.Add(victimNow);

            State.Stack.Push(new StackItem
            {
                Ability = ability,
                Caster = Opp,
                Targets = strikeTargets,
                Snapshot = new EffectSnapshot(),
            });
            State.Priority.OnStackItemAdded();

            string entered = $"[Stack] {name} ({attacker.Name}) enters the stack (size {State.StackCount()}).";
            GD.Print(entered);
            combatUI?.AppendActionLog(entered);

            await DrainTriggerStackAsync();
            return;
        }

        ResolveStrike(attacker, tile, damage, ranged, null);
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        RefreshDeckCounts();
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    /// <summary>Applies a strike's damage. <paramref name="redirected"/> is non-null when a
    /// Reaction replaced the victim — the strike then hits that unit directly wherever it
    /// stands; otherwise the tile's occupant is re-read at resolution so a dodge whiffs.
    /// <paramref name="originalVictim"/> is the unit listed when the strike entered the
    /// stack — when it vacated the tile, the whiff logs as a §9 Dodge reaction line
    /// instead of the generic empty-ground line. Records <see cref="LastStrikeVictim"/>
    /// for riders (channel slow).</summary>
    internal void ResolveStrike(Unit attacker, Vector2I tile, int damage, bool ranged,
                                Unit redirected, Unit originalVictim = null)
    {
        LastStrikeVictim = null;
        bool wasRedirected = redirected != null && IsInstanceValid(redirected) && redirected.Stats.IsAlive;
        Unit victim = wasRedirected ? redirected : grid.GetTile(tile)?.Occupant;
        string verb = ranged ? "shoots" : "strikes";
        string noun = ranged ? "shot" : "strike";
        string attackerName = attacker != null && IsInstanceValid(attacker) ? attacker.Name : "The attack";

        if (victim == null || !IsInstanceValid(victim) || !victim.Stats.IsAlive)
        {
            // §9 reaction grammar: a listed victim that left the tile is a Dodge,
            // not an aimless whiff — the log credits the vacate.
            bool dodged = originalVictim != null && IsInstanceValid(originalVictim)
                && originalVictim.Stats.IsAlive && originalVictim.CurrentTile?.Axial != tile;
            string whiff = dodged
                ? UIContent.ReactionDodgeLine(originalVictim.Name, attackerName, noun)
                : $"{attackerName} {verb} at empty ground!";
            GD.Print(whiff);
            combatUI?.AppendActionLog(whiff);
        }
        else if (attacker != null && IsInstanceValid(attacker) && victim.TeamId == attacker.TeamId)
        {
            string ff = $"{attackerName} {verb} its own ally {victim.Name} for {damage}!";
            GD.Print(ff);
            combatUI?.AppendActionLog(ff);
            victim.ApplyDamage(damage);
            LastStrikeVictim = victim;
        }
        else
        {
            // §9 reaction grammar: a redirected strike names its interceptor.
            string hit = wasRedirected
                ? UIContent.ReactionRedirectLine(victim.Name, attackerName, noun, damage)
                : $"{attackerName} {verb} {victim.Name} for {damage} damage.";
            GD.Print(hit);
            combatUI?.AppendActionLog(hit);
            victim.ApplyDamage(damage);
            LastStrikeVictim = victim;
        }
    }

    /// <summary>Move one step toward a coordinate (tile-chase variant of MoveToward).
    /// U2: routes through ChooseStepTowardTile so pack/scout tile preference applies.</summary>
    private async Task MoveTowardTile(Unit enemy, Vector2I goal)
    {
        if (!IsValidActor(enemy))
            return;

        var nextStep = ChooseStepTowardTile(enemy, goal);
        if (nextStep == null)
            return;

        int pathCost = grid.GetMoveCostTo(enemy, nextStep);
        if (pathCost < 0 || pathCost > enemy.EffectiveMoveRange)   // unified: honors rooted/slowed/grants
            return;

        if (enemy.TryMoveTo(grid, nextStep))
        {
            combatUI?.AppendActionLog($"{enemy.Name} advances on its mark.");
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
        }
    }
}
