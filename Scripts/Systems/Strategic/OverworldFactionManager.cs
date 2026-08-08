using Godot;
using System.Collections.Generic;

// ============================================================
// OverworldFactionManager.cs
//
// Purpose:        Owns all faction tokens (patrols, and later
//                 hunters, raids, warfronts) for the current
//                 region. Spawns PatrolTokens from the region's
//                 resident archmage definition at run start;
//                 ticks all tokens once per player step; emits
//                 PatrolCapturedPlayer when a patrol reaches
//                 the party hex; supports save/restore of token
//                 positions across combat scene swaps.
//
//                 Spawn rules (deterministic per campaign seed):
//                   - Patrol count = ArchmageDefinition.BasePatrolCount
//                   - Starting coords chosen from passable, non-POI
//                     hexes that are ≥5 hexes from Entry, ≥3 from
//                     Objective, and ≥4 apart from each other.
//                   - Iterated q-then-r so ordering is reproducible.
//                   - If the region has no archmage (empty regionId
//                     in RegionArchmageMap), 0 patrols are spawned
//                     and every method is a safe no-op.
// Layer:          System
// Collaborators:  PatrolToken.cs (the unit),
//                 OverworldHexGrid.cs (coord helpers),
//                 ArchmageRegistry.cs + ArchmageDefinition.cs (data),
//                 CampaignState.cs (archmage placement),
//                 OverworldRunManager.cs (caller + signal consumer)
// ============================================================

/// <summary>Manages all faction tokens for one overworld run. Tick() on every player step; listens for PatrolCapturedPlayer to trigger ambush combat.</summary>
public partial class OverworldFactionManager : Node2D
{
    // ── State ─────────────────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private List<PatrolToken> _patrols = new();
    private string _archmageId = "";
    private ArchmageDefinition _archmage;
    private bool _initialized;

    // ── Step 4 (convergence spec): injected queries ──────────────────────
    // Spawn-site filters read DATA through these (wired by ExpeditionManager
    // BEFORE Initialize), and each spawned token inherits TileQuery/FogQuery.
    // Null (isolation) falls back to node reads, as everywhere in Step 4.

    /// <summary>World tile at a grid-local coord, or null off-world.</summary>
    public System.Func<Vector2I, WorldTile?> TileQuery;

    /// <summary>Fog at a grid-local coord (the ExpeditionFogModel).</summary>
    public System.Func<Vector2I, OverworldHex.FogState> FogQuery;

    /// <summary>Effective POI at a grid-local coord (the WindowOverlayModel).</summary>
    public System.Func<Vector2I, OverworldHex.POIType> PoiQuery;

    /// <summary>Terrain filter shared by every spawn path: no water, no mountains.
    /// Data-first with node fallback.</summary>
    private bool SpawnTerrainOk(Vector2I coord, OverworldHex hex)
    {
        if (TileQuery != null)
        {
            var t = TileQuery(coord);
            return t.HasValue && !t.Value.IsWater &&
                   t.Value.Terrain != OverworldHex.TerrainType.Mountain;
        }
        return hex != null && !hex.IsWater &&
               hex.Terrain != OverworldHex.TerrainType.Mountain;
    }

    // ── Signal ────────────────────────────────────────────────────────────
    /// <summary>Emitted when a patrol token arrives on the same hex as the player. OverworldRunManager should wire this to trigger ambush combat.</summary>
    [Signal]
    public delegate void PatrolCapturedPlayerEventHandler(Vector2I coord, string archmageId);

    // ═══════════════════════════════════════════════════════════════════════
    // Initialization
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Spawns patrol tokens for the archmage resident in this region.
    /// Call AFTER POIGenerator.Generate() so patrol placement can
    /// avoid POI hexes. If no archmage is assigned, spawns nothing.
    /// </summary>
    public void Initialize(
        OverworldHexGrid grid,
        string regionId,
        CampaignState campaign)
    {
        _grid = grid;
        _initialized = true;

        if (campaign == null)
        {
            GD.Print($"[FactionManager] No campaign — spawning a wilds patrol for '{regionId}'.");
            SpawnWildsPatrol(regionId);
            return;
        }

        _archmageId = campaign.GetArchmageForRegion(regionId);
        if (string.IsNullOrEmpty(_archmageId))
        {
            GD.Print($"[FactionManager] No archmage in '{regionId}' — spawning a wilds patrol.");
            SpawnWildsPatrol(regionId);
            return;
        }

        _archmage = ArchmageRegistry.Get(_archmageId);
        if (_archmage == null)
        {
            GD.PushWarning($"[FactionManager] Archmage '{_archmageId}' not found — wilds patrol instead.");
            SpawnWildsPatrol(regionId);
            return;
        }

        // Build the sorted list of candidate spawn positions.
        // Sorted by (q, r) so the order is deterministic regardless of
        // Dictionary iteration order.
        var candidates = BuildCandidateList();
        if (candidates.Count == 0)
        {
            GD.PushWarning($"[FactionManager] No valid patrol spawn positions in '{regionId}'.");
            return;
        }

        int patrolCount = _archmage.BasePatrolCount;
        // Supplies fund soldiers (docs/supply_cache_spec_v1): a kingdom flush
        // with harvested supply fields an extra patrol in its region.
        patrolCount += SupplyCacheSystem.PatrolBonusForRegion(regionId);
        var placedCoords = new List<Vector2I>();

        for (int i = 0; i < patrolCount; i++)
        {
            // Each patrol gets its own seeded RNG derived from the campaign seed
            // and its index so two patrols in the same region land at different spots.
            int patrolSeed = campaign.CampaignSeed ^ (i * 13337) ^ regionId.GetHashCode();
            var rng = new RandomNumberGenerator();
            rng.Seed = (ulong)patrolSeed;

            // Filter candidates by spacing from already-placed patrols
            var valid = new List<Vector2I>();
            foreach (var c in candidates)
            {
                bool tooClose = false;
                foreach (var placed in placedCoords)
                {
                    if (_grid.Distance(c, placed) < 4)
                    { tooClose = true; break; }
                }
                if (!tooClose)
                    valid.Add(c);
            }

            if (valid.Count == 0)
            {
                GD.Print($"[FactionManager] Could not place patrol {i} — no spaced positions.");
                break;
            }

            var startCoord = valid[(int)(rng.Randi() % (uint)valid.Count)];
            placedCoords.Add(startCoord);

            SpawnPatrol(i, startCoord, patrolSeed);
        }

        GD.Print($"[FactionManager] Spawned {_patrols.Count} patrol(s) for " +
                 $"'{_archmage.DisplayName}' in '{regionId}'.");
    }

    private void SpawnPatrol(int index, Vector2I startCoord, int seed)
    {
        var patrol = new PatrolToken { Name = $"Patrol_{index}" };
        patrol.TileQuery = TileQuery;   // Step 4: tokens read data, not nodes
        patrol.FogQuery = FogQuery;
        _grid.AddChild(patrol); // child of grid so Position uses grid-space coords
        patrol.Initialize(
            _grid,
            startCoord,
            homeCoord: startCoord, // home = spawn point; patrol wanders around it
            _archmage.FactionColorHex,
            _archmageId,
            seed);
        _patrols.Add(patrol);
    }

    /// <summary>
    /// Builds the sorted, filtered list of valid spawn positions. Iterates the
    /// grid's LIVE hex set, then sorts q-then-r so ordering is deterministic
    /// regardless of Dictionary iteration order.
    /// (W1 fix: the old 0..GridWidth loop only covered the positive quadrant of
    /// a window-mode grid, whose local coords span roughly [-R, R] around the
    /// staging point at (0,0) — patrols could only ever spawn southeast.)
    /// Excludes: Water, Mountain, POI hexes, hexes too close to Entry/Objective.
    /// </summary>
    private List<Vector2I> BuildCandidateList()
    {
        var result = new List<Vector2I>();

        foreach (var kvp in _grid.Hexes)
        {
            var coord = kvp.Key;
            var hex = kvp.Value;

            // Terrain filter (Step 4: data-first, node fallback)
            if (!SpawnTerrainOk(coord, hex))
                continue;

            // Don't spawn on a POI (player would arrive and trigger two events)
            var poiHere = PoiQuery != null ? PoiQuery(coord) : hex.POI;
            if (poiHere != OverworldHex.POIType.None)
                continue;

            // Keep clear of entry (patrol shouldn't ambush the player instantly)
            if (_grid.Distance(coord, _grid.EntryCoord) < 5)
                continue;

            // Keep clear of objective
            if (_grid.Distance(coord, _grid.ObjectiveCoord) < 3)
                continue;

            result.Add(coord);
        }

        // Deterministic ordering (q then r) — spawn choice must reproduce.
        result.Sort((a, b) => a.X != b.X ? a.X - b.X : a.Y - b.Y);
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // World tick
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Advance all faction tokens by one step. Call on every player step
    /// (from OverworldRunManager.OnPartyMoved). After ticking, checks for
    /// capture and emits PatrolCapturedPlayer if a patrol reached the party.
    /// </summary>
    public void Tick(Vector2I playerCoord)
    {
        if (!_initialized || _patrols.Count == 0)
            return;

        foreach (var patrol in _patrols)
        {
            patrol.Tick(playerCoord);

            // S3 (Fulminant Charge, Tinker): the first patrol to enter a
            // rigged hex springs the charge and freezes. Delay, not removal (G3).
            int trapStun = OverworldSpellEffects.ConsumeTrapAt(patrol.CurrentCoord);
            if (trapStun > 0)
            {
                patrol.Stun(trapStun);
                GD.Print($"[FactionManager] Patrol '{patrol.ArchmageId}' springs a fulminant " +
                         $"charge at {patrol.CurrentCoord} — stunned {trapStun} step(s).");
            }
        }

        // S3 (Veil, Enchanter): the party is imperceptible — interception
        // simply fails while the veil holds (G3).
        if (OverworldSpellEffects.VeilActive())
            return;

        // Check for capture after all patrols have moved
        foreach (var patrol in _patrols)
        {
            if (patrol.IsDisengaged)
                continue; // recovering from a fight — no re-capture
            if (patrol.IsStunned)
                continue; // frozen by a spell/trap — no capture either
            if (patrol.IsOnSameHex(playerCoord))
            {
                GD.Print($"[FactionManager] Patrol '{patrol.ArchmageId}' captured player " +
                         $"at {playerCoord}.");
                EmitSignal(SignalName.PatrolCapturedPlayer, playerCoord, patrol.ArchmageId);
                return;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Save / restore across combat scene swaps
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Returns the id of the resident archmage (for router persistence).</summary>
    public string GetArchmageId() => _archmageId;

    /// <summary>Returns the current coord of every patrol, in spawn order. Save this to EncounterRouter before entering combat.</summary>
    public List<Vector2I> GetPatrolPositions()
    {
        var result = new List<Vector2I>(_patrols.Count);
        foreach (var p in _patrols)
            result.Add(p.CurrentCoord);
        return result;
    }

    /// <summary>
    /// Teleports existing patrol tokens to the saved positions.
    /// Call in OverworldRunManager.RestoreFromCombat() to skip the
    /// seeded starting positions and use the pre-combat positions instead.
    /// </summary>
    public void RestorePatrolPositions(List<Vector2I> positions)
    {
        if (positions == null || positions.Count == 0)
            return;

        for (int i = 0; i < _patrols.Count && i < positions.Count; i++)
            _patrols[i].TeleportTo(positions[i]);

        GD.Print($"[FactionManager] Restored {System.Math.Min(_patrols.Count, positions.Count)} patrol position(s).");
    }

    /// <summary>Remaining recovery cooldown for every patrol, in spawn order. Save before combat.</summary>
    public List<int> GetPatrolCooldowns()
    {
        var result = new List<int>(_patrols.Count);
        foreach (var p in _patrols)
            result.Add(p.RecoveryCooldown);
        return result;
    }

    /// <summary>Restore remaining cooldowns after a scene swap. Call after RestorePatrolPositions.</summary>
    public void RestorePatrolCooldowns(List<int> cooldowns)
    {
        if (cooldowns == null)
            return;
        for (int i = 0; i < _patrols.Count && i < cooldowns.Count; i++)
            _patrols[i].SetRecoveryCooldown(cooldowns[i]);
    }

public void DisengagePatrolsAt(Vector2I coord, int cooldownSteps)
    {
        foreach (var p in _patrols)
            if (p.IsOnSameHex(coord))
            {
                p.Disengage(cooldownSteps);
                GD.Print($"[FactionManager] Patrol '{p.ArchmageId}' routed home, " +
                         $"recovering for {cooldownSteps} step(s).");
            }
    }

    /// <summary>S3 (Stasis Snare): freeze the patrol standing on a coord.
    /// Returns its archmage id, or null if no patrol stands there.</summary>
    public string TryStunPatrolAt(Vector2I coord, int steps)
    {
        foreach (var p in _patrols)
        {
            if (p.CurrentCoord == coord)
            {
                p.Stun(steps);
                GD.Print($"[FactionManager] Patrol '{p.ArchmageId}' held in stasis " +
                         $"for {steps} step(s).");
                return p.ArchmageId;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Court & Council
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>A cooldown so large it never expires within one expedition —
    /// "stood down for this expedition." Survives combat round-trips via the
    /// existing GetPatrolCooldowns / RestorePatrolCooldowns path.</summary>
    private const int StandDownSteps = 1_000_000;

    /// <summary>True if any patrol could still be stood down by a Military
    /// favor (i.e. not already permanently stood down).</summary>
    public bool HasStandablePatrol()
    {
        foreach (var p in _patrols)
            if (p.RecoveryCooldown < StandDownSteps / 2)
                return true;
        return false;
    }

    /// <summary>Military favor call-in: the patrol nearest the player retreats
    /// home and will not hunt or capture for the rest of the expedition.
    /// Returns its archmage id, or null if no patrol qualified.</summary>
    public string StandDownNearestPatrol(Vector2I playerCoord)
    {
        PatrolToken best = null;
        int bestDist = int.MaxValue;
        foreach (var p in _patrols)
        {
            if (p.RecoveryCooldown >= StandDownSteps / 2)
                continue; // already stood down
            int d = _grid.Distance(p.CurrentCoord, playerCoord);
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        if (best == null)
            return null;

        best.Disengage(StandDownSteps);
        GD.Print($"[FactionManager] Patrol '{best.ArchmageId}' stood down for the " +
                 $"expedition (Marshal favor).");
        return best.ArchmageId;
    }

    /// <summary>Passage favor call-in: every patrol breaks off and will not
    /// hunt or capture for cooldownSteps steps (safe conduct). Never shortens
    /// an existing longer cooldown (a stood-down patrol stays stood down).</summary>
    public void SuppressAllPatrols(int cooldownSteps)
    {
        foreach (var p in _patrols)
            if (p.RecoveryCooldown < cooldownSteps)
                p.Disengage(cooldownSteps);
        GD.Print($"[FactionManager] Safe conduct: patrols suppressed for {cooldownSteps} step(s).");
    }

    // One slow, generic patrol for archmage-less territory. Spawns at a passable
    // tile a few hexes from the grid's entry (the staging point in window mode) so
    // it starts at a distance and closes in. Uses a neutral color and a seeded RNG
    // derived from the region id for determinism within a session.

    /// <summary>Spawn a single generic wilds patrol when no archmage force applies.
    /// Guarantees every expedition has at least one pursuer.</summary>
    private void SpawnWildsPatrol(string regionId)
    {
        // Find a passable spawn tile 5–9 hexes from the entry, iterating
        // deterministically so the spawn is reproducible.
        Vector2I entry = _grid.EntryCoord;
        var candidates = new List<Vector2I>();
        foreach (var kvp in _grid.Hexes)
        {
            int d = _grid.Distance(kvp.Key, entry);
            if (d < 5 || d > 9)
                continue;
            if (!SpawnTerrainOk(kvp.Key, kvp.Value))   // Step 4
                continue;
            candidates.Add(kvp.Key);
        }
        if (candidates.Count == 0)
        {
            // W1: with a return-centered window built far from staging, the
            // 5–9 ring around the entry isn't loaded at all. The TOKEN must
            // still exist (RestorePatrolPositions can only teleport patrols
            // that spawned — no token, and the wilds patrol silently vanishes
            // for the rest of the expedition) — fall back to any passable
            // loaded tile; the spawn spot is irrelevant on a return path.
            foreach (var kvp in _grid.Hexes)
            {
                if (!SpawnTerrainOk(kvp.Key, kvp.Value))   // Step 4
                    continue;
                candidates.Add(kvp.Key);
            }
        }
        if (candidates.Count == 0)
        {
            GD.Print("[FactionManager] No wilds-patrol spawn site found — none spawned.");
            return;
        }

        // Sort for determinism, then pick by a region-seeded RNG.
        candidates.Sort((a, b) => a.X != b.X ? a.X - b.X : a.Y - b.Y);
        var rng = new RandomNumberGenerator { Seed = (ulong)(regionId.GetHashCode() ^ 0x5EED) };
        var start = candidates[(int)(rng.Randi() % (uint)candidates.Count)];

        var patrol = new PatrolToken { Name = "WildsPatrol_0" };
        patrol.TileQuery = TileQuery;   // Step 4
        patrol.FogQuery = FogQuery;
        _grid.AddChild(patrol);
        patrol.Initialize(
            _grid,
            start,
            homeCoord: start,
            factionColorHex: "#9A8478",   // neutral dun — clearly not a faction force
            archmageId: "wilds",
            seed: (int)rng.Randi());
        _patrols.Add(patrol);

        _initialized = true;
        GD.Print($"[FactionManager] Wilds patrol spawned at {start} (entry {entry}).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Queries
    // ═══════════════════════════════════════════════════════════════════════

    public bool HasPatrols => _patrols.Count > 0;
    public int PatrolCount => _patrols.Count;
    public bool IsInitialized => _initialized;
}
