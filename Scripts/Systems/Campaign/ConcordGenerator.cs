using Godot;
using System.Collections.Generic;

// ============================================================
// ConcordGenerator.cs
//
// Purpose:        Seeded, headless scatter of Veiled Concord nodes
//                 into a generated world (espionage phase E1c). The
//                 Concord is faction-neutral and lives in the shadow
//                 of the kingdoms: nodes are placed on owned, quiet
//                 wilderness tiles, spaced apart, undiscovered. First
//                 contact with any node flips CouncilState.Concord-
//                 Contacted (the Unaware-band gate); until then the
//                 shadow market is not on the table.
//
//                 Nodes are plain WorldPoi (Kind = Concord) appended
//                 to WorldData.Pois: NO new save struct, and the
//                 broker's archetype is DERIVED per node from the
//                 seed (ShadowVocab.BrokerArchetypeFor), never stored.
//                 The buyable/sellable contract catalogue is E3
//                 content and is deliberately NOT authored here (no
//                 dead data ahead of the loader that consumes it).
//
//                 DETERMINISM: runs on its OWN RNG, seeded
//                 seed ^ FNV1a("veiled_concord"), so it never draws
//                 from WorldGenerator's stream. Adding this pass
//                 leaves all existing world output bit-identical,
//                 exactly as CourtGenerator does for courts.
// Layer:          System
// Collaborators:  WorldGenerator.cs (calls after CourtGenerator),
//                 WorldData.cs (WorldPoi / tile PoiIndex),
//                 ShadowState.cs (node count + broker derivation),
//                 CouncilState.cs (ConcordContacted, set in play)
// See:            espionage_veiled_concord_spec_v1.md §3a, §10 (E1c)
// ============================================================

/// <summary>Scatters this cycle's Veiled Concord nodes. Headless.</summary>
public static class ConcordGenerator
{
    /// <summary>Minimum hex spacing between two Concord nodes, so the shadow
    /// market reads as a handful of secret doors across the world rather than a
    /// clustered market row. Severable tuning.</summary>
    private const int MinNodeSpacing = 8;

    /// <summary>Scatter Concord nodes into <paramref name="world"/>. Appends to
    /// WorldData.Pois and sets each host tile's PoiIndex. Returns the number of
    /// nodes actually placed (may be below the target if the world is too dense
    /// or too small to space them). Never throws; never draws from the world
    /// generator's RNG.</summary>
    public static int Generate(int seed, WorldData world,
                               Dictionary<string, KingdomState> kingdoms,
                               string convergenceKingdom)
    {
        if (world == null || world.Tiles.Length == 0)
        {
            return 0;
        }

        int target = ShadowVocab.NodeCountFor(kingdoms != null ? kingdoms.Count : 0);
        var rng = new Rng(unchecked((uint)seed ^ Fnv1a32("veiled_concord")));

        // Candidate tiles: land, owned by a non-convergence kingdom, no
        // settlement / shard vault / existing POI. The Concord hides in quiet
        // corners of stable territory, not in Kassian's fallen heartland.
        var candidates = new List<(int x, int y)>();
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var t = world.GetTile(x, y);
                if (string.IsNullOrEmpty(t.KingdomId)) { continue; }
                if (t.KingdomId == convergenceKingdom) { continue; }
                if (t.SettlementIndex >= 0) { continue; }
                if (t.ShardZoneIndex >= 0) { continue; }
                if (t.PoiIndex >= 0) { continue; }
                candidates.Add((x, y));
            }
        }

        // Deterministic Fisher-Yates shuffle, then greedy spaced placement.
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = (int)(rng.NextU32() % (uint)(i + 1));
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        var placed = new List<(int x, int y)>();
        foreach (var (x, y) in candidates)
        {
            if (placed.Count >= target) { break; }
            if (TooClose(placed, x, y, MinNodeSpacing)) { continue; }
            AddConcordNode(world, x, y);
            placed.Add((x, y));
        }

        GD.Print($"[ConcordGenerator] seed={seed}: placed {placed.Count}/{target} " +
                 $"Veiled Concord node(s) from {candidates.Count} candidate tile(s).");
        return placed.Count;
    }

    private static void AddConcordNode(WorldData world, int x, int y)
    {
        int poiIndex = world.Pois.Count;
        world.Pois.Add(new WorldPoi
        {
            X = x,
            Y = y,
            Kind = PoiKind.Concord,
            // Host kingdom for locational/patrol context. The Concord answers
            // to no throne, but its door is physically inside this territory.
            KingdomId = world.GetTile(x, y).KingdomId,
            Discovered = false,
            GrantsStaging = false,
        });

        int idx = y * world.Width + x;
        var t = world.Tiles[idx];
        t.PoiIndex = poiIndex;
        world.Tiles[idx] = t;
    }

    private static bool TooClose(List<(int x, int y)> placed, int x, int y, int minDist)
    {
        foreach (var (px, py) in placed)
        {
            if (HexCoord.OffsetDistance(px, py, x, y) < minDist)
            {
                return true;
            }
        }
        return false;
    }

    private static uint Fnv1a32(string s)
    {
        uint h = 2166136261u;
        foreach (char c in s) { h ^= c; h *= 16777619u; }
        return h;
    }

    /// <summary>xorshift32: the project's determinism-stable RNG shape (unsigned
    /// wraparound, reproducible across runtimes), local per the codebase pattern
    /// of one private Rng per generator.</summary>
    private struct Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }
        public uint NextU32()
        {
            uint x = _s;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            _s = x; return x;
        }
    }
}
