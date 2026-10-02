using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// GrowthManager.cs
//
// Purpose:        The Druid living-terrain engine. Owns the per-tick
//                 simulation of growth on the hex grid: living tiles
//                 spread to neighbours, age up through stages
//                 (sapling -> thicket -> old growth), wither or burn
//                 on hostile terrain, and leave fertile carcasses
//                 behind. Modeled structurally on MemorialManager.
// Layer:          System
// Collaborators:  HexGridManager.cs (owns the TileData grid),
//                 TileData.cs (GrowthStage/GrowthAge/GrowthOwner/
//                   CarcassTicks fields),
//                 WildingAttunement.cs (raised on growth events;
//                   read for spread/heal/root tier bonuses),
//                 Unit.cs (growth ownership + side checks via TeamId),
//                 GameState.cs (logging),
//                 CombatManager.cs (calls TickEndOfEnemyTurn();
//                   subscribes Riot; reports wildlife deaths).
//
// NOTE: ownership is a Unit, not an Entity. Unit does not derive from
//       Entity in this codebase, and we need TeamId/Attunement which
//       live on Unit. Effects resolve their Unit via FindCasterUnit
//       and pass it straight in.
//
// Integration seams (injected so we don't hardcode your APIs):
//   _wildlifeSpawner(tile, unitKey) -> your summon path
//   _rootHandler(unit, duration)    -> your apply-status("rooted")
//   TickEndOfEnemyTurn()            -> call once at end of enemy turn
//   ApplyRiot(owner)                -> subscribe per Druid unit's
//                                      WildingAttunement.OnRiotTriggered
//   LeaveCarcass(tile, owner)       -> call from wildlife death hook
//
// See:            README s3 (hex grid substrate), s6 (school mechanics)
// ============================================================

/// <summary>Per-terrain growth behaviour, loaded from Data/growth_profiles.json. Affinity strings drive the tick logic (Fertile/Sparse/Edge/Cold/Hostile/Aberrant).</summary>
public class GrowthProfile
{
    public string Affinity = "Fertile";
    public float SpreadMult = 1.0f;
    public int MaxStage = 3;
    public bool DestroysOutright = false;
    public List<string> Wildlife = new();
}

/// <summary>Root config object deserialized from growth_profiles.json.</summary>
public class GrowthConfig
{
    public float BaseSpreadChance = 0.35f;
    public int AdvanceAgeThreshold = 2;
    public int CarcassFertileTicks = 3;
    public float CarcassSpreadBonus = 0.4f;
    public int WildingPerTick = 1;     // Wilding gained per owner per tick when growth occurs (NOT per tile)
    public int RiotWildlifeCap = 2;    // max wildlife a single Riot may spawn
    public Dictionary<string, GrowthProfile> Profiles = new();
    public Dictionary<string, GrowthProfile> ImbueOverrides = new();
    public List<string> WildlifeAny = new();
}

public class GrowthManager
{
    // -- Stage constants (mirror TileData.GrowthStage semantics) ------
    public const int StageNone = 0;
    public const int StageSapling = 1;   // difficult terrain (pathfinding reads GrowthStage)
    public const int StageThicket = 2;   // blocks line of sight; a spread source
    public const int StageOldGrowth = 3; // living wall; wildlife-eligible

    // -- Standard axial neighbour offsets (q, r). If your grid uses a
    //    different axial convention, this is the one line to adjust. --
    private static readonly Vector2I[] AxialNeighbors =
    {
        new Vector2I( 1,  0), new Vector2I( 1, -1), new Vector2I( 0, -1),
        new Vector2I(-1,  0), new Vector2I(-1,  1), new Vector2I( 0,  1)
    };

    // -- Dependencies -------------------------------------------------
    private readonly HexGridManager _grid;
    private readonly GameState _state;
    private readonly Action<TileData, string> _wildlifeSpawner;
    private readonly Action<Unit, int> _rootHandler;

    private GrowthConfig _config = new();
    private bool _ticking;
    private bool _applyingRiot;
    private readonly GrowthProfile _defaultProfile = new() { Affinity = "Fertile", SpreadMult = 1.0f, MaxStage = 3 };

    // -- Events for UI / VFX / lore hooks -----------------------------
    public event Action<TileData> OnGrowthSeeded;
    public event Action<TileData> OnGrowthAdvanced;
    public event Action<TileData> OnCarcassLeft;
    public event Action<TileData> OnCarcassExpired;
    public event Action<TileData, string> OnWildlifeSpawned;
    public event Action<TileData> OnGrowthChanged;   // fired on every stage change (seed/advance/regress/clear) -> drives tile visuals

    public GrowthManager(
        HexGridManager grid,
        GameState state,
        RandomNumberGenerator rng = null,
        Action<TileData, string> wildlifeSpawner = null,
        Action<Unit, int> rootHandler = null)
    {
        _grid = grid;
        _state = state;
        _wildlifeSpawner = wildlifeSpawner;
        _rootHandler = rootHandler;

        // `rng` is accepted for call-site compatibility and ignored: growth is
        // deterministic as of 2026-10-02 (class_identity_druid_v1 §2a, "the
        // Witness"). Spread, Riot placement and wildlife choice all follow fixed
        // rules the player can predict.
        _ = rng;

        LoadProfiles();
    }

    // -- Config loading -----------------------------------------------
    public void LoadProfiles(string resPath = "res://Data/Growth/growth_profiles.json")
    {
        using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PushError($"[GrowthManager] Could not open {resPath} -- using defaults.");
            _config = new GrowthConfig();
            return;
        }

        string json = f.GetAsText();
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true
        };

        _config = JsonSerializer.Deserialize<GrowthConfig>(json, opts) ?? new GrowthConfig();
    }

    // -- The tick: called once at END OF ENEMY TURN -------------------

    public void TickEndOfEnemyTurn()
    {
        if (_grid == null || _ticking)
            return;

        var toSeed = new List<(TileData tile, Unit owner)>();
        var grewOwners = new HashSet<Unit>();

        _ticking = true;
        try
        {
            foreach (TileData tile in _grid.Tiles.Values)
            {
                if (tile.GrowthStage <= StageNone)
                {
                    DecayCarcass(tile);
                    continue;
                }

                GrowthProfile p = GetProfile(tile);

                switch (p.Affinity)
                {
                    case "Hostile":
                        if (p.DestroysOutright)
                            KillGrowth(tile);   // fire burns it to nothing
                        else
                            RegressGrowth(tile);                   // otherwise it recedes one stage
                        continue;

                    case "Cold":
                        tile.GrowthAge--;
                        if (tile.GrowthStage > p.MaxStage)
                        {
                            tile.GrowthStage = Mathf.Max(p.MaxStage, StageNone);
                            if (tile.GrowthStage == StageNone)
                            { ClearGrowth(tile); continue; }
                            RefreshGrowthFlags(tile);
                        }
                        if (tile.GrowthAge < -_config.AdvanceAgeThreshold)
                            RegressGrowth(tile);
                        break;

                    default: // Fertile / Sparse / Edge / Aberrant
                        tile.GrowthAge++;
                        if (tile.GrowthAge >= _config.AdvanceAgeThreshold && tile.GrowthStage < p.MaxStage)
                        {
                            int before = tile.GrowthStage;
                            Advance(tile);   // NOTE: Advance no longer raises Wilding (see below)
                            if (tile.GrowthStage > before && tile.GrowthOwner != null)
                                grewOwners.Add(tile.GrowthOwner);
                        }
                        break;
                }

                if (tile.GrowthStage <= StageNone)
                    continue;

                ApplyTickPassives(tile);

                // Thickets and Old Growth spread every tick; Saplings join in once
                // the owner reaches Stirring (Wilding tier 1).
                if (tile.GrowthStage >= StageThicket ||
                    (tile.GrowthStage == StageSapling && SaplingsSpread(tile.GrowthOwner)))
                    CollectSpread(tile, toSeed);

                DecayCarcass(tile);
            }

            // buffered spread -- seed only after the whole board has been resolved
            foreach (var (tile, owner) in toSeed)
            {
                int before = tile.GrowthStage;
                Seed(tile, StageSapling, owner, raiseWilding: false);
                if (tile.GrowthStage > before && owner != null)
                    grewOwners.Add(owner);
            }
        }
        finally
        {
            _ticking = false;
        }

        // ONE bounded Wilding gain per owner, AFTER all grid mutation is done.
        // This is the only place the passive tick feeds Wilding, so a wide tick
        // can no longer multi-trigger Riot mid-loop. If this tips an owner to 4,
        // the Riot now fires cleanly after the tick has finished.
        foreach (Unit owner in grewOwners)
            RaiseWilding(owner, _config.WildingPerTick);
    }

    /// <summary>
    /// Pure forecast of the next <see cref="TickEndOfEnemyTurn"/> (class_identity_druid_v1
    /// §2a, "Preview"): fills <paramref name="sprouts"/> with the tiles that will seed a
    /// Sapling and <paramref name="advances"/> with the living tiles that will grow a
    /// stage. Mirrors the tick's own rules (age threshold, Cold regression, Hostile
    /// recession, Saplings spreading at Stirring, deterministic CollectSpread) and
    /// mutates nothing, so the preview is exactly what the tick will do if the board
    /// does not change first.
    /// </summary>
    public void PreviewNextTick(List<TileData> sprouts, List<TileData> advances)
    {
        sprouts.Clear();
        advances.Clear();
        if (_grid == null)
            return;

        var claimed = new List<(TileData, Unit)>();
        foreach (TileData tile in _grid.Tiles.Values)
        {
            if (tile.GrowthStage <= StageNone)
                continue;

            GrowthProfile p = GetProfile(tile);
            int stage = tile.GrowthStage;

            switch (p.Affinity)
            {
                case "Hostile":
                    continue;   // it burns or recedes; it never spreads

                case "Cold":
                    if (stage > p.MaxStage)
                        stage = Mathf.Max(p.MaxStage, StageNone);
                    if (stage > StageNone && tile.GrowthAge - 1 < -_config.AdvanceAgeThreshold)
                        stage--;
                    break;

                default:
                    if (tile.GrowthAge + 1 >= _config.AdvanceAgeThreshold && stage < p.MaxStage)
                    {
                        stage++;
                        advances.Add(tile);
                    }
                    break;
            }

            if (stage <= StageNone)
                continue;
            if (stage >= StageThicket || (stage == StageSapling && SaplingsSpread(tile.GrowthOwner)))
                CollectSpread(tile, claimed);
        }

        foreach (var (t, _) in claimed)
            sprouts.Add(t);
    }

    // -- Riot burst (subscribe per Druid unit's OnRiotTriggered) ------

    public void ApplyRiot(Unit owner)
    {
        if (_grid == null || _applyingRiot)
            return;

        _applyingRiot = true;
        try
        {
            var advanced = new List<TileData>();
            var oldGrowth = new List<TileData>();

            foreach (TileData tile in _grid.Tiles.Values)
            {
                if (tile.GrowthStage <= StageNone)
                    continue;
                if (!SameSide(tile.GrowthOwner, owner))
                    continue;

                GrowthProfile p = GetProfile(tile);
                if (p.Affinity == "Hostile")
                    continue;

                if (tile.GrowthStage < p.MaxStage)
                {
                    tile.GrowthStage++;
                    tile.GrowthAge = 0;
                    RefreshGrowthFlags(tile);
                    advanced.Add(tile);
                }

                if (tile.GrowthStage >= StageOldGrowth)
                    oldGrowth.Add(tile);
            }

            foreach (TileData t in advanced)
                OnGrowthAdvanced?.Invoke(t);

            // Spawn at most RiotWildlifeCap wildlife (NOT one per tile, which
            // flooded the board), on the Old Growth nearest the owner's enemies.
            // Ties break by axial coordinate so the same board always answers
            // the same way.
            oldGrowth.Sort((a, b) =>
            {
                int da = NearestHostileDistance(a, owner);
                int db = NearestHostileDistance(b, owner);
                if (da != db)
                    return da.CompareTo(db);
                if (a.Axial.X != b.Axial.X)
                    return a.Axial.X.CompareTo(b.Axial.X);
                return a.Axial.Y.CompareTo(b.Axial.Y);
            });

            int cap = Math.Min(_config.RiotWildlifeCap, oldGrowth.Count);
            for (int i = 0; i < cap; i++)
                RequestWildlife(oldGrowth[i], "auto");

            _state?.Log($"[Wilding] Riot -- {advanced.Count} tiles surged, {cap} wildlife answered.");
        }
        finally
        {
            _applyingRiot = false;
        }
    }

    // -- Public surface for card effects ------------------------------

    /// <summary>Plant living terrain at a stage on a tile. Respects affinity: never roots on Hostile ground, never exceeds the terrain's max stage, never downgrades existing growth.</summary>
    public void Seed(TileData tile, int stage, Unit owner, bool raiseWilding = false)
    {
        if (tile == null)
            return;

        GrowthProfile p = GetProfile(tile);
        if (p.Affinity == "Hostile")
            return;

        int capped = Mathf.Min(stage, p.MaxStage);
        if (capped <= StageNone)
            return;
        if (tile.GrowthStage >= capped)
            return;

        tile.GrowthStage = capped;
        tile.GrowthAge = 0;
        tile.GrowthOwner = owner;
        RefreshGrowthFlags(tile);

        OnGrowthSeeded?.Invoke(tile);
        if (raiseWilding)
            RaiseWilding(owner, 1);
    }

    /// <summary>Force one tile up a stage (active "advance_growth").</summary>
    public void AdvanceTile(TileData tile) => Advance(tile);

    /// <summary>Force a local spread tick from a Thicket+ source (active "spread_growth").</summary>
    public void SpreadFrom(TileData source)
    {
        if (source == null || source.GrowthStage < StageThicket)
            return;
        var buf = new List<(TileData, Unit)>();
        CollectSpread(source, buf);
        foreach (var (t, o) in buf)
            Seed(t, StageSapling, o, raiseWilding: false);
    }

    /// <summary>Harvest a living tile: clear it and leave fertile carcass ground. Returns stages consumed so the effect can scale heal/draw.</summary>
    public int Harvest(TileData tile)
    {
        if (tile == null || tile.GrowthStage <= StageNone)
            return 0;

        int stages = tile.GrowthStage;
        Unit owner = tile.GrowthOwner;

        ClearGrowth(tile);
        LeaveCarcass(tile, owner);   // spent ground stays rich -- circle of life
        return stages;
    }

    /// <summary>Spawn wildlife at/near a tile. unitKey "auto" picks from the host terrain's pool.</summary>
    public void SummonWildlifeAt(TileData tile, string unitKey) => RequestWildlife(tile, unitKey);

    /// <summary>Mark a tile as fertile carcass ground for a few ticks. Call from the wildlife death hook.</summary>
    public void LeaveCarcass(TileData tile, Unit owner = null)
    {
        if (tile == null)
            return;
        tile.CarcassTicks = _config.CarcassFertileTicks;
        if (owner != null && tile.GrowthOwner == null)
            tile.GrowthOwner = owner;
        OnCarcassLeft?.Invoke(tile);
    }

    /// <summary>Root a unit via the injected status handler. Used by EntangleEffect and the Rampant tick.</summary>
    public void RootUnit(Unit u, int duration) => ApplyRoot(u, duration);

    /// <summary>Resolve the terrain/imbue profile for a tile. Imbue (Fire/Frost/Lightning) overrides terrain -- the free cross-school interaction.</summary>
    public GrowthProfile GetProfile(TileData tile)
    {
        if (tile != null && tile.ElementType != TileElementType.None)
        {
            string ek = tile.ElementType.ToString();
            if (_config.ImbueOverrides.TryGetValue(ek, out GrowthProfile op))
                return op;
        }

        string tk = tile?.TerrainType.ToString() ?? "Grass";
        if (_config.Profiles.TryGetValue(tk, out GrowthProfile p))
            return p;

        return _defaultProfile;
    }

    /// <summary>Per-owner heal at the Druid's upkeep when on/adjacent to their own living terrain. Returns 0 when not eligible.</summary>
    public int GetUpkeepHeal(Unit owner)
    {
        if (owner?.Attunement is not WildingAttunement w || !w.HealsOwner)
            return 0;
        if (owner.CurrentTile == null)
            return 0;

        if (IsOwnedGrowth(owner.CurrentTile, owner))
            return WildingAttunement.SpreadingHealPerTurn;

        foreach (Vector2I dir in AxialNeighbors)
        {
            TileData n = _grid.GetTile(owner.CurrentTile.Axial + dir);
            if (n != null && IsOwnedGrowth(n, owner))
                return WildingAttunement.SpreadingHealPerTurn;
        }
        return 0;
    }

    // -- Internals ----------------------------------------------------

    private void Advance(TileData tile)
    {
        GrowthProfile p = GetProfile(tile);
        if (tile.GrowthStage >= p.MaxStage)
            return;

        tile.GrowthStage++;
        tile.GrowthAge = 0;
        RefreshGrowthFlags(tile);

        OnGrowthAdvanced?.Invoke(tile);
        // Wilding is NOT raised here. Per-tile gain caused multi-Riot cascades;
        // the tick grants a single bounded charge per owner instead.
    }

    private void RegressGrowth(TileData tile)
    {
        tile.GrowthStage = Mathf.Max(StageNone, tile.GrowthStage - 1);
        tile.GrowthAge = 0;

        if (tile.GrowthStage == StageNone)
            ClearGrowth(tile);
        else
            RefreshGrowthFlags(tile);
    }

    private void KillGrowth(TileData tile) => ClearGrowth(tile);

    private void ClearGrowth(TileData tile)
    {
        tile.GrowthStage = StageNone;
        tile.GrowthAge = 0;
        tile.GrowthOwner = null;
        // NOTE: growth assumes terrain does not independently own these. If your terrain
        // sets BlocksLineOfSight/BlocksMovementByHeight, re-run your terrain recompute
        // after clearing so terrain-owned flags survive.
        tile.BlocksLineOfSight = false;
        tile.BlocksMovementByHeight = false;

        OnGrowthChanged?.Invoke(tile);
    }

    private void RefreshGrowthFlags(TileData tile)
    {
        bool thicket = tile.GrowthStage >= StageThicket;
        bool oldGrowth = tile.GrowthStage >= StageOldGrowth;

        tile.BlocksLineOfSight = thicket;          // thicket walls off sightlines
        tile.BlocksMovementByHeight = oldGrowth;   // old growth is a living wall
        // Sapling difficult-terrain is read by pathfinding off GrowthStage.

        OnGrowthChanged?.Invoke(tile);
    }

    /// <summary>
    /// Deterministic spread (class_identity_druid_v1 §2a): a source seeds ONE
    /// adjacent eligible tile per tick, chosen by, in order:
    ///   1. best terrain affinity (the profile's spreadMult, higher first),
    ///   2. carcass ground before plain ground (carrion enriches the soil),
    ///   3. nearest to the owner's enemies,
    ///   4. fixed neighbour order (AxialNeighbors).
    /// A tile another source already claimed this tick is skipped, so two
    /// sources never waste their spread on the same tile.
    /// </summary>
    private void CollectSpread(TileData source, List<(TileData, Unit)> buffer)
    {
        TileData best = null;
        float bestAffinity = 0f;
        bool bestCarcass = false;
        int bestDistance = int.MaxValue;

        foreach (Vector2I dir in AxialNeighbors)
        {
            TileData n = _grid.GetTile(source.Axial + dir);
            if (n == null)
                continue;
            if (n.GrowthStage > StageNone)
                continue;   // already growing
            if (n.Occupant != null)
                continue;          // don't sprout under a standing unit

            GrowthProfile np = GetProfile(n);
            if (np.Affinity == "Hostile" || np.MaxStage <= StageNone)
                continue;
            if (buffer.Exists(entry => entry.Item1 == n))
                continue;   // already claimed by another source this tick

            bool carcass = n.CarcassTicks > 0;
            int distance = NearestHostileDistance(n, source.GrowthOwner);

            bool better = best == null
                || np.SpreadMult > bestAffinity
                || (np.SpreadMult == bestAffinity && carcass && !bestCarcass)
                || (np.SpreadMult == bestAffinity && carcass == bestCarcass && distance < bestDistance);

            if (better)
            {
                best = n;
                bestAffinity = np.SpreadMult;
                bestCarcass = carcass;
                bestDistance = distance;
            }
        }

        if (best != null)
            buffer.Add((best, source.GrowthOwner));
    }

    private void ApplyTickPassives(TileData tile)
    {
        Unit owner = tile.GrowthOwner;
        if (owner?.Attunement is not WildingAttunement w)
            return;

        // Rampant: enemies standing on the owner's living terrain are rooted on the tick.
        if (w.RootsEnemies && tile.Occupant is Unit occ && IsHostile(occ, owner))
            ApplyRoot(occ, WildingAttunement.RampantRootDuration);

        // Spreading heal is applied once per owner at upkeep (GetUpkeepHeal), not here.
    }

    private void DecayCarcass(TileData tile)
    {
        if (tile.CarcassTicks <= 0)
            return;
        tile.CarcassTicks--;
        if (tile.CarcassTicks <= 0)
            OnCarcassExpired?.Invoke(tile);
    }

    private void RequestWildlife(TileData tile, string unitKey)
    {
        string key = unitKey;
        if (key == "auto")
        {
            GrowthProfile p = GetProfile(tile);
            List<string> pool = (p.Wildlife != null && p.Wildlife.Count > 0) ? p.Wildlife : _config.WildlifeAny;
            if (pool == null || pool.Count == 0)
                return;
            key = pool[0];   // deterministic: the terrain's first-listed species answers
        }

        TileData spawn = FindSpawnTile(tile);
        if (spawn == null)
        {
            _state?.Log($"[Wilding] No open tile near {tile.Axial} to summon {key}.");
            return;
        }

        if (_wildlifeSpawner != null)
            _wildlifeSpawner(spawn, key);
        else
            _state?.Log($"[Wilding] (no spawner wired) would summon {key} at {spawn.Axial}.");

        OnWildlifeSpawned?.Invoke(spawn, key);
    }

    /// <summary>The growth tile itself if open and walkable, else the first open walkable neighbour. Old Growth blocks movement, so wildlife emerges beside it.</summary>
    private TileData FindSpawnTile(TileData near)
    {
        if (near == null)
            return null;
        if (near.Occupant == null && near.IsWalkable && !near.IsBlocked && !near.BlocksMovementByHeight)
            return near;

        foreach (Vector2I dir in AxialNeighbors)
        {
            TileData n = _grid.GetTile(near.Axial + dir);
            if (n != null && n.Occupant == null && n.IsWalkable && !n.IsBlocked && !n.BlocksMovementByHeight)
                return n;
        }
        return null;
    }

    private void ApplyRoot(Unit u, int duration)
    {
        if (_rootHandler != null)
            _rootHandler(u, duration);
        else
            _state?.Log($"[Wilding] (no root handler) would root {u?.Name} for {duration}.");
    }

    // -- Owner / side helpers (TeamId-based, matching the effect layer) --

    private bool IsOwnedGrowth(TileData tile, Unit owner)
        => tile.GrowthStage > StageNone && SameSide(tile.GrowthOwner, owner);

    /// <summary>True once the owner's Wilding reaches Stirring: Saplings spread too.</summary>
    private static bool SaplingsSpread(Unit owner)
        => owner?.Attunement is WildingAttunement w && w.SaplingsSpread;

    /// <summary>Hex distance from a tile to the nearest living unit hostile to
    /// <paramref name="owner"/>. Ownerless growth has no preference (0 for every
    /// tile, so the fixed neighbour order decides).</summary>
    private int NearestHostileDistance(TileData tile, Unit owner)
    {
        if (owner == null || tile == null || _state?.UnitsInPlay == null)
            return 0;

        int best = int.MaxValue;
        foreach (var u in _state.UnitsInPlay)
        {
            if (u == null || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (u.TeamId == owner.TeamId)
                continue;
            int d = _grid.Distance(tile.Axial, u.CurrentTile.Axial);
            if (d < best)
                best = d;
        }
        return best;
    }

    private void RaiseWilding(Unit owner, int n)
    {
        if (owner?.Attunement is WildingAttunement w)
            w.GainCharges(n);
    }

    private static bool SameSide(Unit a, Unit b)
    {
        if (a == null || b == null)
            return ReferenceEquals(a, b);
        return a.TeamId == b.TeamId;
    }

    private static bool IsHostile(Unit a, Unit b)
        => a != null && b != null && a.TeamId != b.TeamId;
}
