using Godot;
using System.Collections.Generic;

// ============================================================
// CorruptionSpread.cs
//
// Purpose:        The per-lunation corruption tick — Model C
//                 (coupled, downward-only). Runs once per
//                 expedition deploy (one deploy = one lunation).
//                 Two layers:
//
//                 TERRITORY (0–3, Campaign.CorruptionLevels):
//                   The strategic layer. Corruption pressure
//                   spreads kingdom-to-kingdom along adjacency
//                   from the convergence outward. A kingdom next
//                   to a heavily-corrupted one gains pressure;
//                   when it hits 3 with an unresolved archmage,
//                   CampaignState.AdvanceCorruption flips them to
//                   Corrupted (re-activating the final-battle
//                   mechanic, now lunation-driven).
//
//                 TILE (0–100, WorldTile.Corruption):
//                   The visible layer. Tiles flood toward their
//                   kingdom's territory pressure, faster near
//                   already-corrupted neighbours (including across
//                   kingdom borders), so the red wash creeps
//                   smoothly across the strategic map.
//
//                 Coupling is strictly downward: territory level
//                 drives tile pressure; tiles never write back.
//                 No desync. The convergence seat is the eternal
//                 source (always max).
// Layer:          System
// Collaborators:  WorldData.cs (tiles), CampaignState.cs
//                 (territory levels + archmage flip),
//                 KingdomState.cs (territory ids),
//                 StrategicView (calls Tick on lunation boundary)
// See:            single_world_refactor_v2.docx §Phase 2b
// ============================================================

/// <summary>Per-lunation corruption spread. Stateless except for a cached
/// kingdom-adjacency map derived once from the tile array.</summary>
public static class CorruptionSpread
{
    // ── Tuning (set to reach: convergence-third heavy, mid creeping, start
    //    + far edge clean by lunation 12; tune freely after feeling it) ──

    /// <summary>Territory pressure (0–3) a kingdom gains per lunation when its
    /// hottest neighbour (attenuated by falloff) exceeds its own level.</summary>
    private const float TerritorySpreadPerLunation = 0.5f;

    /// <summary>Per-hop attenuation: corruption WEAKENS as it travels from the
    /// convergence, so a kingdom rises only toward (hottest neighbour − this).
    /// This is what keeps the start + far edge clean within 12 lunations rather
    /// than the whole graph saturating.</summary>
    private const float TerritoryHopFalloff = 1.0f;

    /// <summary>Convergence-adjacent kingdoms get a head start each lunation
    /// regardless of neighbours — the seat actively pushes outward.</summary>
    private const float ConvergencePushPerLunation = 0.4f;

    /// <summary>Tiles move at most this many points/lunation toward their target,
    /// so the visible creep is gradual rather than snapping.</summary>
    private const int TileFloodMaxStep = 14;

    /// <summary>Extra flood speed for a tile bordering an already-corrupted tile,
    /// which is what makes corruption visibly creep edge-to-edge.</summary>
    private const int TileFloodNeighbourBonus = 8;

    // Fractional territory pressure accumulator (0–3 exposed; finer internally),
    // keyed by kingdom id. Lives here rather than in the save: it's derived from
    // the integer CorruptionLevels each cycle and only needs sub-lunation memory
    // within a session. Rebuilt from the integer levels if missing.
    private static readonly Dictionary<string, float> _pressure = new();

    // Cached adjacency: kingdom id -> set of bordering kingdom ids. Derived once
    // per world from tile borders. Cleared when a new world is seen.
    private static Dictionary<string, HashSet<string>> _adjacency;
    private static WorldData _adjacencyWorld;

    // kingdom_N -> real region id, for translating to the campaign's region-keyed
    // corruption/archmage maps. Pressure + adjacency stay in kingdom space (tile
    // topology); only campaign reads/writes use the region key.
    private static readonly Dictionary<string, string> _kingdomToRegion = new();

    private static string RegionOf(string kingdomId)
        => _kingdomToRegion.TryGetValue(kingdomId, out var r) && !string.IsNullOrEmpty(r)
            ? r : kingdomId;

    /// <summary>Run one lunation of corruption spread on the cycle's world +
    /// campaign. Call from the lunation-boundary hook (one deploy = one lunation).</summary>
    public static void Tick(WorldData world, CampaignState campaign,
                            Dictionary<string, KingdomState> kingdoms)
    {
        if (world == null || campaign == null || world.Tiles.Length == 0)
            return;

        // Refresh kingdom->region map each tick (cheap; kingdoms is small).
        _kingdomToRegion.Clear();
        if (kingdoms != null)
            foreach (var kvp in kingdoms)
                _kingdomToRegion[kvp.Key] = kvp.Value.TemplateRegionId;

        EnsureAdjacency(world);
        SeedPressureFromLevels(campaign);

        // ── 1. Territory pressure spreads kingdom-to-kingdom ─────────────
        SpreadTerritoryPressure(world, campaign, kingdoms);

        // ── 2. Tiles flood toward their kingdom's pressure ───────────────
        FloodTiles(world, campaign);

        GD.Print("[Corruption] Lunation spread applied.");
    }

    // ════════════════════════════════════════════════════════════════════
    // Territory layer (0–3, kingdom-to-kingdom)
    // ════════════════════════════════════════════════════════════════════

    private static void SpreadTerritoryPressure(WorldData world, CampaignState campaign,
                                                Dictionary<string, KingdomState> kingdoms)
    {
        // The convergence kingdom is the eternal source (always max pressure).
        string convergenceKingdom = ConvergenceKingdomId(world);

        // Snapshot current pressure so spread is simultaneous, not order-dependent.
        var before = new Dictionary<string, float>(_pressure);

        foreach (var kvp in _adjacency)
        {
            string kid = kvp.Key;
            if (kid == convergenceKingdom)
            {
                _pressure[kid] = 3f; // source stays maxed
                continue;
            }

            // Highest neighbour pressure drives this kingdom upward.
            float maxNeighbour = 0f;
            bool touchesConvergence = false;
            foreach (var n in kvp.Value)
            {
                if (n == convergenceKingdom)
                    touchesConvergence = true;
                if (before.TryGetValue(n, out float np) && np > maxNeighbour)
                    maxNeighbour = np;
            }

            float cur = before.TryGetValue(kid, out float c) ? c : 0f;

            // Target = hottest neighbour attenuated by per-hop falloff, so
            // corruption weakens as it travels outward. Plus a convergence push
            // for kingdoms bordering the seat. This gradient keeps far + start
            // kingdoms clean within a cycle rather than the graph saturating.
            float target = Mathf.Max(0f, maxNeighbour - TerritoryHopFalloff);
            float gain = 0f;
            if (target > cur)
                gain += TerritorySpreadPerLunation;
            if (touchesConvergence)
                gain += ConvergencePushPerLunation;

            if (gain > 0f)
            {
                float ceil = target + (touchesConvergence ? ConvergencePushPerLunation : 0f) + 0.01f;
                float next = Mathf.Min(3f, Mathf.Min(ceil, cur + gain));
                if (next > cur)
                    _pressure[kid] = next;
            }
        }

        // Write integer levels back through AdvanceCorruption so the archmage-
        // flip-at-3 mechanic fires. The campaign keys corruption + archmagi by
        // REAL region id, so translate kingdom -> region at the call.
        foreach (var kvp in _pressure)
        {
            string kid = kvp.Key;
            string region = RegionOf(kid);
            int targetLevel = Mathf.FloorToInt(kvp.Value);
            int curLevel = campaign.GetCorruption(region);
            // AdvanceCorruption raises by 1 and handles the archmage flip; call it
            // until the integer level matches the accumulated pressure.
            while (curLevel < targetLevel && curLevel < 3)
            {
                bool flipped = campaign.AdvanceCorruption(region);
                curLevel = campaign.GetCorruption(region);
                if (flipped)
                    GD.Print($"[Corruption] Kingdom '{kid}' ({region}) archmage fell to corruption.");
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Tile layer (0–100, flood toward territory pressure)
    // ════════════════════════════════════════════════════════════════════

    private static void FloodTiles(WorldData world, CampaignState campaign)
    {
        int w = world.Width, h = world.Height;

        // Precompute each kingdom's tile-space target from its 0–3 territory level.
        // Level 0→0, 1→40, 2→70, 3→100 (a kingdom "fully fallen" saturates).
        float TargetFor(string kid)
        {
            if (string.IsNullOrEmpty(kid))
                return 0f;
            int lvl = campaign.GetCorruption(RegionOf(kid));
            return lvl switch { 0 => 0f, 1 => 40f, 2 => 70f, _ => 100f };
        }

        // Snapshot corruption so the flood is simultaneous (read old, write new).
        var old = new byte[world.Tiles.Length];
        for (int i = 0; i < world.Tiles.Length; i++)
            old[i] = world.Tiles[i].Corruption;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                var tile = world.Tiles[idx];
                if (tile.IsWater)
                    continue;

                float target = TargetFor(tile.KingdomId);
                int cur = old[idx];

                // The convergence seat and its bloom are already high from gen;
                // never reduce corruption (it only spreads, never recedes).
                if (target <= cur)
                    continue;

                // Flood speed: base step, faster if a neighbour is already corrupted
                // (this is what makes the red creep edge-to-edge across borders).
                int step = TileFloodMaxStep;
                if (HasCorruptedNeighbour(world, old, x, y))
                    step += TileFloodNeighbourBonus;

                int next = Mathf.Min((int)target, cur + step);
                if (next > cur)
                    tile.Corruption = (byte)Mathf.Clamp(next, 0, 100);

                world.Tiles[idx] = tile;
            }
        }
    }

    private static bool HasCorruptedNeighbour(WorldData world, byte[] old, int x, int y)
    {
        foreach (var (nx, ny) in HexCoord.Neighbors(x, y, world.Width, world.Height))
        {
            if (old[ny * world.Width + nx] >= 30)
                return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    // Adjacency (derived once from tile borders)
    // ════════════════════════════════════════════════════════════════════

    private static void EnsureAdjacency(WorldData world)
    {
        if (_adjacency != null && _adjacencyWorld == world)
            return;

        _adjacency = new Dictionary<string, HashSet<string>>();
        _adjacencyWorld = world;

        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                string kid = world.GetTile(x, y).KingdomId;
                if (string.IsNullOrEmpty(kid))
                    continue;
                if (!_adjacency.ContainsKey(kid))
                    _adjacency[kid] = new HashSet<string>();

                foreach (var (nx, ny) in HexCoord.Neighbors(x, y, world.Width, world.Height))
                {
                    string nkid = world.GetTile(nx, ny).KingdomId;
                    if (!string.IsNullOrEmpty(nkid) && nkid != kid)
                        _adjacency[kid].Add(nkid);
                }
            }
        }

        GD.Print($"[Corruption] Derived adjacency for {_adjacency.Count} kingdoms.");
    }

    private static string ConvergenceKingdomId(WorldData world)
    {
        if (world.ConvergenceX < 0 || world.ConvergenceY < 0)
            return "";
        return world.GetTile(world.ConvergenceX, world.ConvergenceY).KingdomId ?? "";
    }

    // Rebuild the fractional pressure accumulator (kingdom-keyed) from the
    // campaign's integer levels (region-keyed) if this is a fresh session.
    // Each kingdom inherits its region's level.
    private static void SeedPressureFromLevels(CampaignState campaign)
    {
        foreach (var kingdomEntry in _kingdomToRegion)
        {
            string kid = kingdomEntry.Key;
            string region = kingdomEntry.Value;
            int lvl = campaign.GetCorruption(region);
            if (lvl > 0 && (!_pressure.ContainsKey(kid) || _pressure[kid] < lvl))
                _pressure[kid] = lvl;
        }
    }

    /// <summary>Clear cached adjacency + pressure (call on cycle reset / new world).</summary>
    public static void Reset()
    {
        _adjacency = null;
        _adjacencyWorld = null;
        _pressure.Clear();
    }
}
