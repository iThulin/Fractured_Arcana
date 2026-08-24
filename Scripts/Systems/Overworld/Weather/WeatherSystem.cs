using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// WeatherSystem.cs
//
// Purpose:        The overworld weather FIELD for the Mobile Fortress
//                 reframe: a set of moving fronts drifting across the
//                 expedition window. Different tiles carry different
//                 weather at once, so the castle can route around a storm.
//
//                 Static, like OverworldSpellEffects / OverworldMovementCost,
//                 so the field survives the combat/negotiation scene swap
//                 within one process (a sortie's weather does not reset when
//                 you drop into a fight and come back). Reseeded ONLY on a
//                 fresh deploy.
//
//                 Model: each front is a circle in render space (the same
//                 flat-top odd-q layout HexCoord uses), with a type, a
//                 radius, and a shared-wind velocity. WeatherAt(tile) = the
//                 worst-severity front covering that tile, else Clear.
//                 Advect() moves every front one step of wind per committed
//                 stride; a front that drifts fully off the window re-enters
//                 upwind with a freshly rolled (biome/season) type.
//
//                 Separation of concerns for scene-swap safety: the front
//                 DATA (_fronts, bounds, tiles, season) is pure and persists;
//                 the terrain SAMPLER is a delegate onto the live expedition
//                 and is re-bound every Deploy via Configure(), so Advect()
//                 never calls into a freed node.
// Layer:          System (static field sim; no nodes of its own)
// Collaborators:  ExpeditionManager (Configure/Seed/Advect/WeatherAt),
//                 WeatherCatalog (types + biome roll), HexCoord (layout).
// ============================================================

/// <summary>The moving-front overworld weather field. Configure() every
/// Deploy (rebinds the sampler + window), Seed() on a fresh deploy only,
/// Advect() per committed stride, WeatherAt() to read a tile.</summary>
public static class WeatherSystem
{
    public struct Front
    {
        public Vector2 Center;    // render-space (tilePx = 1), local window coords
        public float Radius;      // render-space tiles
        public Vector2 Velocity;  // render-space tiles per committed stride
        public WeatherType Type;
    }

    private static readonly List<Front> _fronts = new();
    private static readonly List<Vector2I> _tiles = new();   // window tiles (pure data)
    private static bool _active;
    private static Vector2 _min, _max;                        // render-space window bounds
    private static int _season;                              // 0..3
    private static RandomNumberGenerator _rng = new();
    private static Func<Vector2I, OverworldHex.TerrainType> _terrainAt;  // transient (re-bound each Deploy)

    /// <summary>True once a fresh deploy has seeded fronts (until Reset).</summary>
    public static bool Active => _active;
    public static IReadOnlyList<Front> Fronts => _fronts;

    /// <summary>Clears the field. Called on a fresh deploy before Seed and on
    /// every run-end path, so a stale sortie's fronts never leak into the next.</summary>
    public static void Reset()
    {
        _fronts.Clear();
        _tiles.Clear();
        _active = false;
        _terrainAt = null;
    }

    /// <summary>Bind the live window + terrain sampler + season. Call EVERY
    /// Deploy (fresh and combat-return) BEFORE Seed. Recomputes render-space
    /// bounds from the window tiles. Does not touch the fronts themselves, so
    /// a combat-return keeps the weather it left with — only the sampler is
    /// re-pointed at the new expedition instance.</summary>
    public static void Configure(IEnumerable<Vector2I> windowTiles,
                                 Func<Vector2I, OverworldHex.TerrainType> terrainAt,
                                 int season)
    {
        _terrainAt = terrainAt;
        _season = ((season % 4) + 4) % 4;

        _tiles.Clear();
        _min = new Vector2(float.MaxValue, float.MaxValue);
        _max = new Vector2(float.MinValue, float.MinValue);
        foreach (var t in windowTiles)
        {
            _tiles.Add(t);
            var p = Pos(t);
            _min.X = Mathf.Min(_min.X, p.X); _min.Y = Mathf.Min(_min.Y, p.Y);
            _max.X = Mathf.Max(_max.X, p.X); _max.Y = Mathf.Max(_max.Y, p.Y);
        }
        if (_tiles.Count == 0)
        {
            _min = Vector2.Zero;
            _max = Vector2.Zero;
        }
    }

    /// <summary>Seed a fresh sortie's fronts. Requires Configure() first.
    /// Rolls FrontCount fronts at random window tiles, each biome/season-typed,
    /// all sharing one wind direction with small per-front jitter.</summary>
    public static void Seed(ulong seed)
    {
        _fronts.Clear();
        _active = false;
        if (_tiles.Count == 0)
            return;

        _rng = new RandomNumberGenerator { Seed = seed };
        float windAng = _rng.RandfRange(0f, Mathf.Tau);

        for (int i = 0; i < WeatherCatalog.FrontCount; i++)
        {
            var centerTile = _tiles[_rng.RandiRange(0, _tiles.Count - 1)];
            float ang = windAng + _rng.RandfRange(-0.4f, 0.4f);
            _fronts.Add(new Front
            {
                Center = Pos(centerTile),
                Radius = RollRadius(),
                Velocity = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * WeatherCatalog.FrontSpeedTiles,
                Type = RollType(centerTile),
            });
        }
        _active = true;
    }

    /// <summary>Advance every front one wind-step. Call once per COMMITTED
    /// stride (not per preview), so the field only moves when the world does.
    /// A front that leaves the window re-enters from the upwind edge carrying
    /// a freshly rolled type.</summary>
    public static void Advect()
    {
        if (!_active)
            return;

        for (int i = 0; i < _fronts.Count; i++)
        {
            var f = _fronts[i];
            f.Center += f.Velocity;

            bool offMap = f.Center.X < _min.X - f.Radius || f.Center.X > _max.X + f.Radius ||
                          f.Center.Y < _min.Y - f.Radius || f.Center.Y > _max.Y + f.Radius;
            if (offMap)
            {
                var dir = f.Velocity.LengthSquared() > 0.0001f ? f.Velocity.Normalized() : Vector2.Right;
                var mid = (_min + _max) * 0.5f;
                var half = (_max - _min) * 0.5f + new Vector2(f.Radius, f.Radius);
                // Re-enter from the UPWIND edge (opposite the drift direction).
                f.Center = mid - new Vector2(dir.X * half.X, dir.Y * half.Y);
                f.Radius = RollRadius();
                f.Type = RollType(NearestTile(f.Center));
            }
            _fronts[i] = f;
        }
    }

    /// <summary>Weather over a local window tile: the worst-severity front
    /// covering it, or Clear if none does.</summary>
    public static WeatherType WeatherAt(Vector2I localCoord)
    {
        if (!_active)
            return WeatherType.Clear;

        var p = Pos(localCoord);
        WeatherType best = WeatherType.Clear;
        int bestSev = 0;
        foreach (var f in _fronts)
        {
            if (p.DistanceTo(f.Center) <= f.Radius)
            {
                int sev = WeatherCatalog.Severity(f.Type);
                if (sev > bestSev) { bestSev = sev; best = f.Type; }
            }
        }
        return best;
    }

    public static WeatherDef DefAt(Vector2I localCoord) => WeatherCatalog.Def(WeatherAt(localCoord));

    // ── helpers ──────────────────────────────────────────────────────────

    private static Vector2 Pos(Vector2I t) => HexCoord.OffsetRenderPosition(t.X, t.Y, 1f);

    private static float RollRadius()
        => Mathf.Max(1.5f, WeatherCatalog.FrontRadiusTiles +
                            _rng.RandfRange(-WeatherCatalog.FrontRadiusJitter, WeatherCatalog.FrontRadiusJitter));

    private static WeatherType RollType(Vector2I nearTile)
    {
        var terrain = _terrainAt != null ? _terrainAt(nearTile) : OverworldHex.TerrainType.Grassland;
        var weights = WeatherCatalog.BiomeWeights(terrain, _season);
        int total = 0;
        foreach (var (_, wt) in weights) total += wt;
        if (total <= 0) return WeatherType.Clear;

        int r = _rng.RandiRange(0, total - 1);
        foreach (var (type, wt) in weights)
        {
            if (r < wt) return type;
            r -= wt;
        }
        return WeatherType.Clear;
    }

    private static Vector2I NearestTile(Vector2 pos)
    {
        Vector2I best = _tiles.Count > 0 ? _tiles[0] : Vector2I.Zero;
        float bestD = float.MaxValue;
        foreach (var t in _tiles)
        {
            float d = Pos(t).DistanceSquaredTo(pos);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }
}
