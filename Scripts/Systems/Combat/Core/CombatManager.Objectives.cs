using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatManager.Objectives.cs
//
// Purpose:        O-track runtime — non-kill victory conditions and
//                 reinforcement waves. Every state change happens at
//                 the ONE true round boundary (StartEnemyTurn, right
//                 after roundNumber++), and every win/loss is still
//                 DECLARED inside CheckCombatEnd, so objectives
//                 inherit the trigger-settle ordering and the
//                 emit-once guard that kill-victories already have.
// Layer:          Combat / runtime
// Collaborators:  CombatObjectiveDef.cs (data),
//                 EncounterDefinition.cs (carrier),
//                 CombatManager.cs (boundary hook, end-check,
//                   spawn sizing, SpawnRegistryUnit),
//                 CombatManager.EnemyIntents.cs (PlanAllEnemyIntents),
//                 CombatUI.cs (SetObjectiveText)
// See:            docs/combat_objectives_spec_v1.md
//
// Scope of this build: O1 (waves) + O2 (survive).
//   protect  (O3) — ward spawn/flags/exclusions: NOT YET.
//   hold_zone(O4) — zone build/render/breaches: NOT YET.
// Both are rejected loudly by EncounterPoolLoader, so a not-yet-built
// kind can never silently degrade into an ordinary kill-fight.
// ============================================================

public partial class CombatManager
{
    // ── Runtime state ────────────────────────────────────────────────────
    // All null/empty on a legacy encounter, which is every encounter
    // authored before the O-track. Combat state is not serialized today and
    // this spec keeps it that way — nothing here survives a scene swap.

    /// <summary>Null on an annihilate fight. Never set to the annihilate kind.</summary>
    private CombatObjectiveDef _objective;

    /// <summary>Waves not yet spawned, sorted ascending by round. Drained as
    /// they arrive. NOTE this is populated even when <see cref="_objective"/>
    /// is null — an ordinary kill-fight may carry waves, and ruling 4 says an
    /// empty board with a wave still pending is NOT a victory.</summary>
    private List<ReinforcementWave> _pendingWaves = new();

    /// <summary>Latched at the boundary, consumed by CheckCombatEnd. Kept as
    /// latches rather than direct declarations so an objective outcome cannot
    /// jump the trigger-settle deferral.</summary>
    private bool _objectiveVictory;
    private bool _objectiveDefeat;

    /// <summary>Guard so the banner does not re-announce every phase change.</summary>
    private string _lastObjectiveBanner = "";

    private bool ObjectiveWavesPending => _pendingWaves != null && _pendingWaves.Count > 0;

    // ── Init ─────────────────────────────────────────────────────────────

    /// <summary>Reads the objective/wave payload off the encounter definition.
    /// Called from QueueEncounterFromContext (the one place a real encounter
    /// lands) and defensively from QueueDefaultEncounter with null, so a debug
    /// or fallback fight always resets to the legacy path.</summary>
    private void InitObjectiveState(EncounterDefinition def)
    {
        _objective = null;
        _pendingWaves = new List<ReinforcementWave>();
        _objectiveVictory = false;
        _objectiveDefeat = false;
        _lastObjectiveBanner = "";

        if (def == null)
            return;

        if (def.Objective != null
            && CombatObjectiveDef.IsImplementedKind(def.Objective.Kind)
            && def.Objective.Kind != CombatObjectiveDef.KindAnnihilate)
        {
            _objective = def.Objective;
        }
        else if (def.Objective != null
                 && def.Objective.Kind != CombatObjectiveDef.KindAnnihilate)
        {
            // Belt and braces: the loader already refuses these, but a
            // hand-built definition (ConvergenceEncounterBuilder, a debug
            // launcher, a future caller) must not get a silent kill-fight.
            GD.PrintErr($"[Objective] Encounter '{def.Id}' asks for objective kind " +
                        $"'{def.Objective.Kind}', which this build does not implement — " +
                        "running as annihilate.");
        }

        if (def.Waves != null && def.Waves.Count > 0)
        {
            foreach (var w in def.Waves)
            {
                if (w == null || w.Enemies == null || w.Enemies.Count == 0)
                    continue;
                if (w.Round <= 1)
                {
                    GD.PrintErr($"[Objective] Encounter '{def.Id}' has a wave at round " +
                                $"{w.Round} — dropped (waves arrive at round 2 or later).");
                    continue;
                }
                _pendingWaves.Add(w);
            }
            _pendingWaves.Sort((a, b) => a.Round.CompareTo(b.Round));
        }

        if (_objective != null || ObjectiveWavesPending)
        {
            string kindLabel = _objective == null
                ? CombatObjectiveDef.KindAnnihilate
                : _objective.Kind;
            int roundsLabel = _objective == null ? 0 : _objective.Rounds;
            GD.Print($"[Objective] Armed: kind={kindLabel}, rounds={roundsLabel}, " +
                     $"waves={_pendingWaves.Count}.");
        }
    }

    // ── The round boundary ───────────────────────────────────────────────

    /// <summary>The single evaluation point. Called from StartEnemyTurn
    /// immediately after roundNumber++ — so roundNumber is now the round the
    /// player is ABOUT to play, and the round just finished was roundNumber-1.
    ///
    /// <para>Order: rounds check, then wave spawn, then declare. A wave due on
    /// the very round the objective completes is skipped rather than spawned
    /// and instantly discarded — a deviation from the spec's literal ordering,
    /// made because spawning bodies onto a board that is already won reads as
    /// a bug to the player and costs a frame of unit setup for nothing.</para></summary>
    private void EvaluateObjectiveRoundBoundary()
    {
        if (_objective == null && !ObjectiveWavesPending)
            return;

        // 1. Breach check (hold_zone) — O4. Deliberately absent, not stubbed:
        //    the loader refuses hold_zone, so nothing can reach this path.

        // 2. Rounds check (survive; later, protect-with-rounds).
        //    roundNumber has just been incremented, so "roundNumber > Rounds"
        //    means the player finished round Rounds and lived.
        if (_objective != null && _objective.Rounds > 0 && roundNumber > _objective.Rounds)
        {
            _objectiveVictory = true;
            GD.Print($"[Objective] Survived {_objective.Rounds} round(s) — objective met.");
            combatUI?.AppendActionLog($"You held for {_objective.Rounds} rounds. The objective is met.");
        }

        // 3. Wave arrivals.
        if (!_objectiveVictory && !_objectiveDefeat)
            SpawnDueWaves();

        RefreshObjectiveBanner();

        if (_objectiveVictory || _objectiveDefeat)
            CheckCombatEnd();
    }

    /// <summary>Spawns every wave whose round has arrived (<c>&lt;=</c>, not
    /// <c>==</c>, so a wave can never be skipped by a boundary that somehow
    /// advanced twice). Arrivals get one full player turn to be answered:
    /// RunEnemyTurn snapshots its actor list at its head, so a unit that
    /// appears at the start of the player's turn does not act until the NEXT
    /// enemy phase.</summary>
    private void SpawnDueWaves()
    {
        if (!ObjectiveWavesPending)
            return;

        var due = new List<ReinforcementWave>();
        for (int i = _pendingWaves.Count - 1; i >= 0; i--)
        {
            if (_pendingWaves[i].Round <= roundNumber)
            {
                due.Add(_pendingWaves[i]);
                _pendingWaves.RemoveAt(i);
            }
        }
        if (due.Count == 0)
            return;
        due.Reverse();   // restore authored order after the reverse walk

        int needed = 0;
        foreach (var w in due)
            needed += w.Enemies.Count;

        var arrivalTiles = CollectEnemyArrivalTiles(needed);
        int taken = 0;
        int spawned = 0;

        foreach (var w in due)
        {
            int spawnedThisWave = 0;
            foreach (var slot in w.Enemies)
            {
                if (taken >= arrivalTiles.Count)
                {
                    GD.PrintErr($"[Objective] Wave (round {w.Round}): out of arrival tiles — " +
                                $"{spawnedThisWave}/{w.Enemies.Count} placed.");
                    break;
                }

                var tile = arrivalTiles[taken++];
                var unit = SpawnRegistryUnit(slot.UnitId, tile, teamId: 1,
                                             difficultyMult: slot.DifficultyMult,
                                             isMidFightSummon: false);
                if (unit == null)
                    continue;
                spawnedThisWave++;
                spawned++;
            }

            string announce = string.IsNullOrEmpty(w.Announce)
                ? "Reinforcements arrive."
                : w.Announce;
            GD.Print($"[Objective] Wave round {w.Round}: {spawnedThisWave} arrival(s). {announce}");
            combatUI?.AppendActionLog($"── {announce} ──");
        }

        if (spawned > 0)
        {
            // Arrivals must show real intent markers on the turn they land, not
            // fall through to the improvise branch. Documented safe to call
            // redundantly.
            PlanAllEnemyIntents();
            RefreshEnemyRoster();
        }
    }

    /// <summary>Free tiles for wave arrivals, farthest-from-the-party first.
    ///
    /// <para>Deliberately NOT the deployment placer. SpawnAndPlaceEnemies solves
    /// a different problem: it places into an empty board using a local
    /// "claimed" set (nothing is on a tile yet) and sorts arrivals NEAREST the
    /// party so the opening fight has contact. A wave lands on a LIVE board —
    /// occupancy is real, so IsOccupied is authoritative — and should enter
    /// from the rear, because a body materialising in the player's face is a
    /// gotcha, not a mission.</para></summary>
    private List<TileData> CollectEnemyArrivalTiles(int needed)
    {
        var result = new List<TileData>();
        if (grid == null || needed <= 0)
            return result;

        var seen = new HashSet<Vector2I>();
        var frontier = new Queue<Vector2I>();

        foreach (var zone in grid.SpawnZones)
        {
            if (zone.Side != HexGridManager.SpawnSide.Enemy)
                continue;
            foreach (var coord in zone.Tiles)
            {
                if (!seen.Add(coord))
                    continue;
                frontier.Enqueue(coord);
                var td = grid.GetTile(coord);
                if (td != null && td.IsWalkable && !td.IsBlocked && !td.IsOccupied)
                    result.Add(td);
            }
        }

        // Ring-widen outward when the enemy zone cannot host the wave — the
        // usual cause is simply that the original roster is still standing in
        // it. Mirrors the deployment placer's shortfall fallback.
        while (result.Count < needed && frontier.Count > 0)
        {
            var cur = frontier.Dequeue();
            foreach (var n in grid.GetNeighbors(cur))
            {
                if (!seen.Add(n))
                    continue;
                frontier.Enqueue(n);
                var td = grid.GetTile(n);
                if (td != null && td.IsWalkable && !td.IsBlocked && !td.IsOccupied)
                    result.Add(td);
            }
        }

        Vector2I centroid = ComputePlayerCentroid();
        return result
            .OrderByDescending(t => grid.Distance(t.Axial, centroid))
            .ToList();
    }

    // ── Banner ───────────────────────────────────────────────────────────

    /// <summary>Pushes the objective line. Safe to call before CombatUI is
    /// built — SetObjectiveText carries the same pending-replay guard the
    /// phase and hint lines use.</summary>
    private void RefreshObjectiveBanner()
    {
        if (combatUI == null)
            return;
        string text = ObjectiveBannerText();
        if (text == _lastObjectiveBanner)
            return;
        _lastObjectiveBanner = text;
        combatUI.SetObjectiveText(text);
    }

    private string ObjectiveBannerText()
    {
        if (_objective == null && !ObjectiveWavesPending)
            return "";

        string line = "";

        if (_objective != null)
        {
            string label = string.IsNullOrEmpty(_objective.Description)
                ? DefaultObjectiveLabel(_objective.Kind)
                : _objective.Description;

            if (_objective.Rounds > 0)
            {
                // roundNumber is the round in progress; clamp so the banner
                // reads "8 / 8" on the last round rather than "9 / 8".
                int shown = Mathf.Min(roundNumber, _objective.Rounds);
                line = $"{label} — round {shown} / {_objective.Rounds}";
            }
            else
            {
                line = label;
            }
        }

        if (ObjectiveWavesPending)
        {
            var rounds = new List<string>();
            foreach (var w in _pendingWaves)
                rounds.Add(w.Round.ToString());
            string waveLine = rounds.Count == 1
                ? $"Reinforcements — round {rounds[0]}"
                : $"Reinforcements — rounds {string.Join(", ", rounds)}";
            line = line.Length > 0 ? $"{line}   ·   {waveLine}" : waveLine;
        }

        return line;
    }

    private static string DefaultObjectiveLabel(string kind) => kind switch
    {
        CombatObjectiveDef.KindSurvive => "Survive",
        CombatObjectiveDef.KindProtect => "Protect the ward",
        CombatObjectiveDef.KindHoldZone => "Hold the ground",
        _ => "",
    };
}
