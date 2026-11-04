using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// ProvinceBuilder.cs
//
// Purpose:        Cuts the world's land into provinces (docs/
//                 provinces_v1.md, slice PV1). Each kingdom's
//                 territory becomes about six, seeded on its seat,
//                 then its cities, then its towns, and grown by the
//                 same kind of cost flood that drew the kingdoms: dear
//                 to climb above the lowland line and dearer still
//                 across a river, so province borders fall on rivers
//                 and ridgelines. A big mountain range is a province of
//                 its own: its whole run of mountains seeds it, and it
//                 keeps to the high ground. Draws no RNG and reads only what
//                 generation fixed (territories, elevation, rivers,
//                 settlements), so a world always yields the same
//                 partition, and a legacy save gets the one a fresh
//                 generation of it would have made.
// Layer:          World generation
// Collaborators:  WorldGenerator.cs (step 5i), CycleInitializer.cs
//                 (the legacy build), WorldData.cs (Provinces,
//                 ProvinceMap), Province.cs, WorldAtlas3D.cs (the
//                 Political lens), ProvinceSaveAssert.cs,
//                 CampusGuildPanel.cs (the Province Report button)
// See:            docs/provinces_v1.md section 4
// ============================================================

/// <summary>Builds, checks and reports a world's provinces.</summary>
public static class ProvinceBuilder
{
    // ── Tuning (docs/provinces_v1.md section 4.6) ────────────────────────
    /// <summary>Provinces in a kingdom of average size (ruling P1). A kingdom gets
    /// this many scaled by its land against the average, held to the range below.</summary>
    public const int TargetPerKingdom = 6;
    public const int MinPerKingdom = 3;
    public const int MaxPerKingdom = 9;

    /// <summary>Settlement seeds closer than this many hexes would make two thin
    /// provinces, so the later one stays inside the earlier one's province.</summary>
    public const int MinSeedSpacing = 7;

    /// <summary>The flood's cost to enter a tile: one, plus ElevationWeight per unit
    /// of elevation above LowlandLevel, plus RiverWeight across a river edge. The
    /// kingdom partition has the same shape with a river weight of 5; provinces weigh
    /// a river more so their borders hold to it.</summary>
    public const double ElevationWeight = 12.0;
    public const double LowlandLevel = 0.50;
    public const double RiverWeight = 8.0;

    /// <summary>A province over SplitFactor times the mean size is cut in two; one
    /// under MergeFactor times the mean, and never under MergeFloor tiles, joins a
    /// neighbour. The same minimum decides whether an island holds a province of its
    /// own or joins the nearest province of its kingdom.</summary>
    public const double SplitFactor = 2.2;
    public const double MergeFactor = 0.35;
    public const int MergeFloor = 20;

    /// <summary>Rounds in which seeds standing on no settlement move to the middle of
    /// the land their province grew over before the land floods again (4.4), and
    /// rounds in which a cut's two seeds move to the middles of their halves (4.5).</summary>
    public const int RelaxRounds = 2;
    public const int SplitRounds = 4;

    /// <summary>A run of a kingdom's land at or above the mountain line (MountainShaper.
    /// MountainFloor) is a big range, and a province of its own, when at least this many
    /// of its tiles are core tiles (all six neighbours on the same run). A thin ridge has
    /// no core and stays a border between provinces.</summary>
    public const int RangeCoreMin = 8;

    /// <summary>What a range province pays per tile, on top of the usual cost, to enter
    /// land below the hills line (MountainShaper.HillsFloor). It keeps a range to its
    /// mountains and hills, yet lets it take a pocket of low land no other seed can
    /// reach except across it.</summary>
    public const double RangeDescentPenalty = 40.0;

    /// <summary>ProvinceMap stores a province's position as a byte and 255 means no
    /// province, so a world holds at most 255 provinces. A generated world holds about
    /// sixty.</summary>
    public const int MaxProvinces = 255;

    /// <summary>Map tones. Provinces of one kingdom that share a border take different
    /// tones where four are enough.</summary>
    public const int ToneCount = 4;

    private const int CharacterCount = (int)ProvinceCharacter.Ruined + 1;
    private const int MaxFailLines = 25;
    private const double Sqrt3 = 1.7320508075688772;

    /// <summary>A province while the build cuts, merges and splits.</summary>
    private sealed class Draft
    {
        public int Group;
        public int SeedTile;
        public int SeedSettlement = -1;
        public bool Alive = true;

        /// <summary>Seeded by a big range: its whole run floods as the source (Region),
        /// it keeps to the high ground, and it never moves in the relax rounds.</summary>
        public bool Range;
        public List<int> Region;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Build
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Build the world's provinces if it has none. True when it built them.
    /// CycleInitializer calls this on every open and saves when it returns true, which
    /// is how a world saved before provinces gets them.</summary>
    public static bool EnsureBuilt(WorldData world, bool log = true)
    {
        if (world == null || world.Tiles == null || world.Tiles.Length == 0 || world.HasProvinces())
            return false;
        Build(world, log);
        return world.HasProvinces();
    }

    /// <summary>Partition the world's land into provinces, replacing any it had. The
    /// steps are docs/provinces_v1.md sections 4.1 to 4.7. Prints one summary line
    /// unless log is false (the save assertions build scratch worlds quietly).</summary>
    public static void Build(WorldData world, bool log = true)
    {
        if (world == null || world.Tiles == null || world.Tiles.Length == 0)
            return;
        int w = world.Width, h = world.Height, n = w * h;
        if (world.Tiles.Length != n)
        {
            GD.PushError($"[Provinces] The world holds {world.Tiles.Length} tiles, not {w} x {h}. No provinces built.");
            return;
        }
        world.Provinces = new List<Province>();
        world.ProvinceMap = null;
        int[] nb = NeighborTable(w, h);

        // 1. Groups (4.1): one per kingdom. Land no kingdom owns joins the kingdom with
        // the nearest owned land; only a world where no land has a kingdom keeps a wild
        // group of its own.
        List<string> groupKeys = GroupKeys(world);
        int groupCount = groupKeys.Count;
        if (groupCount == 0)
            return;
        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int gi = 0; gi < groupCount; gi++)
            groupIndex[groupKeys[gi]] = gi;
        var groupOf = new int[n];
        var ownerless = new List<int>();
        for (int i = 0; i < n; i++)
        {
            groupOf[i] = -1;
            if (!world.Tiles[i].IsLand)
                continue;
            if (groupIndex.TryGetValue(world.Tiles[i].KingdomId ?? "", out int gi))
                groupOf[i] = gi;
            else
                ownerless.Add(i);
        }
        if (ownerless.Count > 0)
            AdoptOwnerless(nb, groupOf, ownerless);
        var landOf = new int[groupCount];
        int landTotal = 0;
        for (int i = 0; i < n; i++)
        {
            if (groupOf[i] < 0)
                continue;
            landOf[groupOf[i]]++;
            landTotal++;
        }

        // 2. Land pieces, targets and size limits (4.2).
        int[] comp = Components(groupOf, nb, out List<int> compSize, out List<int> compGroup);
        int[] target = Targets(groupKeys, landOf, out double meanProvince);
        int minTiles = Math.Max(MergeFloor, (int)Math.Round(MergeFactor * meanProvince));
        int maxTiles = Math.Max(2 * minTiles, (int)Math.Round(SplitFactor * meanProvince));
        int[] capitalSettlement = CapitalSettlements(world, groupKeys, groupOf);
        var capitalTile = new int[groupCount];
        for (int gi = 0; gi < groupCount; gi++)
        {
            int cs = capitalSettlement[gi];
            capitalTile[gi] = cs >= 0 ? world.Settlements[cs].CenterY * w + world.Settlements[cs].CenterX : -1;
        }

        // 3. Seeds (4.3).
        var drafts = new List<Draft>();
        var cost = new double[n];
        var owner = new int[n];
        for (int gi = 0; gi < groupCount; gi++)
            SeedGroup(world, nb, groupOf, comp, compSize, compGroup, gi, target[gi], minTiles, drafts, cost, owner);

        // 4. Growth (4.4): each group's seeds flood its land together, and a tile joins
        // the seed that reaches it cheapest. Seeds on no settlement then move to the
        // middle of what they won and the land floods again. Land no seed reaches is a
        // small island, which joins the nearest province of its kingdom.
        var prov = new int[n];
        for (int i = 0; i < n; i++)
            prov[i] = -1;
        var sourceDraft = new List<int>();
        var floors = new List<double>();
        for (int gi = 0; gi < groupCount; gi++)
        {
            List<int> sources = GroupSources(drafts, gi, sourceDraft, floors);
            if (sources.Count == 0)
                continue;
            Flood(world, nb, groupOf, gi, sources, cost, owner, floors);
            for (int i = 0; i < n; i++)
            {
                if (groupOf[i] == gi && owner[i] >= 0)
                    prov[i] = sourceDraft[owner[i]];
            }
        }
        for (int gi = 0; gi < groupCount; gi++)
            RelaxGroup(world, nb, groupOf, comp, gi, drafts, prov, cost, owner);
        AttachUnreached(world, groupOf, comp, compSize.Count, prov);

        // 5. Balance (4.5): split the oversized, merge the slivers, split again.
        SplitPass(world, nb, prov, drafts, minTiles, maxTiles, cost, owner);
        MergePass(world, nb, prov, drafts, capitalTile, minTiles);
        SplitPass(world, nb, prov, drafts, minTiles, maxTiles, cost, owner);

        // 6. Order: kingdom by kingdom, its capital first, then in seeding order.
        int[] size = Sizes(prov, drafts.Count);
        var order = new List<int>();
        for (int gi = 0; gi < groupCount; gi++)
        {
            int capitalDraft = capitalTile[gi] >= 0 ? prov[capitalTile[gi]] : -1;
            if (capitalDraft >= 0)
                order.Add(capitalDraft);
            for (int k = 0; k < drafts.Count; k++)
            {
                if (k != capitalDraft && drafts[k].Alive && drafts[k].Group == gi && size[k] > 0)
                    order.Add(k);
            }
        }
        var remap = new int[drafts.Count];
        for (int k = 0; k < remap.Length; k++)
            remap[k] = -1;
        for (int at = 0; at < order.Count; at++)
            remap[order[at]] = at;
        var map = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int p = prov[i] >= 0 ? remap[prov[i]] : -1;
            map[i] = p >= 0 ? (byte)p : WorldData.NoProvince;
        }

        // 7. Describe (4.7): size, ground, settlements, seat, neighbours, tone, id, name.
        var provinces = new List<Province>(order.Count);
        var seedOf = new int[order.Count];
        var rangeOf = new bool[order.Count];
        for (int at = 0; at < order.Count; at++)
        {
            provinces.Add(new Province { Index = at, KingdomId = groupKeys[drafts[order[at]].Group] });
            seedOf[at] = drafts[order[at]].SeedTile;
            rangeOf[at] = drafts[order[at]].Range;
        }
        Describe(world, nb, comp, map, provinces, seedOf, rangeOf, capitalSettlement, groupIndex);
        AssignIdsAndNames(world, provinces);

        world.Provinces = provinces;
        world.ProvinceMap = map;
        if (log)
            GD.Print($"[Provinces] Built {provinces.Count} provinces over {landTotal} land tiles in {groupCount} realm(s): " +
                 $"mean {meanProvince:F0} tiles, merge under {minTiles}, split over {maxTiles}.");
    }

    // ── 4.1 Groups and topology ──────────────────────────────────────────
    /// <summary>The kingdom ids that own land, in ordinal order. The empty id (land no
    /// kingdom owns) is a group only in a world where no land has a kingdom; anywhere
    /// else such land joins a kingdom (AdoptOwnerless).</summary>
    private static List<string> GroupKeys(WorldData world)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            if (world.Tiles[i].IsLand)
                seen.Add(world.Tiles[i].KingdomId ?? "");
        }
        var keys = new List<string>(seen);
        if (keys.Count > 1)
            keys.Remove("");
        keys.Sort(CompareGroupKeys);
        return keys;
    }

    /// <summary>Land no kingdom owns, in a world where kingdoms own land, takes the group
    /// of the nearest owned land: a breadth-first walk out from every owned tile at once,
    /// in tile order, over land and water alike. Generation leaves no such land; play
    /// can (water opened into marsh), and a world saved since then gets its provinces
    /// without a sliver of no kingdom's land becoming a province of its own.</summary>
    private static void AdoptOwnerless(int[] nb, int[] groupOf, List<int> ownerless)
    {
        int n = groupOf.Length;
        var reach = new int[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            reach[i] = groupOf[i];
            if (groupOf[i] >= 0)
                queue.Enqueue(i);
        }
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            for (int d = 0; d < 6; d++)
            {
                int next = nb[cur * 6 + d];
                if (next >= 0 && reach[next] < 0)
                {
                    reach[next] = reach[cur];
                    queue.Enqueue(next);
                }
            }
        }
        foreach (int t in ownerless)
            groupOf[t] = reach[t];
    }

    private static int CompareGroupKeys(string a, string b)
    {
        bool aWild = a.Length == 0, bWild = b.Length == 0;
        if (aWild != bWild)
            return aWild ? 1 : -1;
        return string.CompareOrdinal(a, b);
    }

    /// <summary>Entry i*6+d is the tile across edge d of tile i (HexCoord.
    /// AxialDirections order, which is the order of the river-edge bits), or -1 off
    /// the map.</summary>
    private static int[] NeighborTable(int w, int h)
    {
        var nb = new int[w * h * 6];
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                var (q, r) = HexCoord.OffsetToAxial(col, row);
                int at = (row * w + col) * 6;
                for (int d = 0; d < 6; d++)
                {
                    var (dq, dr) = HexCoord.AxialDirections[d];
                    var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                    nb[at + d] = (nc >= 0 && nr >= 0 && nc < w && nr < h) ? nr * w + nc : -1;
                }
            }
        }
        return nb;
    }

    /// <summary>Connected runs of tiles sharing a label (labels below zero belong to
    /// no run). Returns each tile's run, -1 for none; runs are numbered in the order
    /// of their lowest tile.</summary>
    private static int[] Components(int[] label, int[] nb, out List<int> runSize, out List<int> runLabel)
    {
        int n = label.Length;
        var run = new int[n];
        for (int i = 0; i < n; i++)
            run[i] = -1;
        runSize = new List<int>();
        runLabel = new List<int>();
        var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if (label[i] < 0 || run[i] >= 0)
                continue;
            int id = runSize.Count;
            int count = 0;
            run[i] = id;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                count++;
                for (int d = 0; d < 6; d++)
                {
                    int next = nb[cur * 6 + d];
                    if (next >= 0 && run[next] < 0 && label[next] == label[i])
                    {
                        run[next] = id;
                        stack.Push(next);
                    }
                }
            }
            runSize.Add(count);
            runLabel.Add(label[i]);
        }
        return run;
    }

    // ── 4.2 Targets ──────────────────────────────────────────────────────
    /// <summary>How many provinces each group should hold. A kingdom gets
    /// TargetPerKingdom scaled by its land against the average kingdom, held to
    /// MinPerKingdom..MaxPerKingdom; land no kingdom owns gets one per average
    /// province's worth. Also returns the mean province size those targets imply.</summary>
    private static int[] Targets(List<string> groupKeys, int[] landOf, out double meanProvince)
    {
        int groups = groupKeys.Count;
        double kingdomLand = 0.0;
        int kingdoms = 0, totalLand = 0;
        for (int gi = 0; gi < groups; gi++)
        {
            totalLand += landOf[gi];
            if (groupKeys[gi].Length > 0)
            {
                kingdomLand += landOf[gi];
                kingdoms++;
            }
        }
        double meanKingdom = kingdoms > 0 ? kingdomLand / kingdoms : totalLand;
        double perProvince = Math.Max(1.0, meanKingdom / TargetPerKingdom);
        var target = new int[groups];
        int sum = 0;
        for (int gi = 0; gi < groups; gi++)
        {
            int t = groupKeys[gi].Length == 0
                ? Math.Max(1, (int)Math.Round(landOf[gi] / perProvince))
                : Math.Clamp((int)Math.Round(TargetPerKingdom * landOf[gi] / Math.Max(1.0, meanKingdom)),
                             MinPerKingdom, MaxPerKingdom);
            t = Math.Max(1, Math.Min(t, landOf[gi]));
            target[gi] = t;
            sum += t;
        }
        meanProvince = (double)totalLand / Math.Max(1, sum);
        return target;
    }

    /// <summary>Each kingdom's seat city (the first settlement marked IsSeat on its
    /// land), or -1. Land no kingdom owns has no capital.</summary>
    private static int[] CapitalSettlements(WorldData world, List<string> groupKeys, int[] groupOf)
    {
        var capital = new int[groupKeys.Count];
        for (int gi = 0; gi < capital.Length; gi++)
            capital[gi] = -1;
        for (int s = 0; s < world.Settlements.Count; s++)
        {
            var st = world.Settlements[s];
            if (!st.IsSeat || !world.InBounds(st.CenterX, st.CenterY))
                continue;
            int gi = groupOf[st.CenterY * world.Width + st.CenterX];
            if (gi >= 0 && groupKeys[gi].Length > 0 && capital[gi] < 0)
                capital[gi] = s;
        }
        return capital;
    }

    // ── 4.3 Seeds ────────────────────────────────────────────────────────
    /// <summary>Seed one group's provinces. First its settlements in seeding order,
    /// each at least MinSeedSpacing from every seed so far, until the target is met.
    /// Then each big range (BigRanges), whatever the target. Then each piece of its land that no seed stands on and that is big enough for
    /// a province of its own, largest first, on its chief settlement or its middle
    /// (a group with no seed at all seeds its largest piece, whatever its size). Then
    /// the tile the flood finds farthest from every seed, until the target is met or
    /// that tile would crowd a seed.</summary>
    private static void SeedGroup(WorldData world, int[] nb, int[] groupOf, int[] comp, List<int> compSize,
                                  List<int> compGroup, int group, int target, int minTiles,
                                  List<Draft> drafts, double[] cost, int[] owner)
    {
        int w = world.Width, n = groupOf.Length;
        var seeds = new List<int>();
        var sources = new List<int>();
        var floors = new List<double>();
        List<int> order = SeedOrder(world, groupOf, group);

        foreach (int s in order)
        {
            if (seeds.Count >= target || drafts.Count >= MaxProvinces)
                break;
            var st = world.Settlements[s];
            int tile = st.CenterY * w + st.CenterX;
            if (Crowds(world, tile, seeds))
                continue;
            seeds.Add(tile);
            sources.Add(tile);
            floors.Add(double.NegativeInfinity);
            drafts.Add(new Draft { Group = group, SeedTile = tile, SeedSettlement = s });
        }

        // Big ranges are provinces of their own (ruling 2026-11-03), whatever the
        // target: the whole run seeds the province and it keeps to the high ground.
        foreach (var (rangeSeed, region) in BigRanges(world, nb, groupOf, group, seeds))
        {
            if (drafts.Count >= MaxProvinces)
                break;
            seeds.Add(rangeSeed);
            sources.AddRange(region);
            for (int r = 0; r < region.Count; r++)
                floors.Add(MountainShaper.HillsFloor);
            drafts.Add(new Draft { Group = group, SeedTile = rangeSeed, Range = true, Region = region });
        }

        var seededPieces = new HashSet<int>();
        foreach (int t in seeds)
            seededPieces.Add(comp[t]);
        var pieces = new List<int>();
        for (int c = 0; c < compSize.Count; c++)
        {
            if (compGroup[c] == group)
                pieces.Add(c);
        }
        pieces.Sort((a, b) => compSize[a] != compSize[b] ? compSize[b].CompareTo(compSize[a]) : a.CompareTo(b));
        foreach (int c in pieces)
        {
            if (drafts.Count >= MaxProvinces)
                break;
            if (seededPieces.Contains(c) || (compSize[c] < minTiles && seeds.Count > 0))
                continue;
            int tile = -1, settlement = -1;
            foreach (int s in order)
            {
                var st = world.Settlements[s];
                int at = st.CenterY * w + st.CenterX;
                if (comp[at] == c)
                {
                    tile = at;
                    settlement = s;
                    break;
                }
            }
            if (tile < 0)
            {
                var land = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (comp[i] == c)
                        land.Add(i);
                }
                tile = CentralTile(world, land);
            }
            if (tile < 0)
                continue;
            seeds.Add(tile);
            sources.Add(tile);
            floors.Add(double.NegativeInfinity);
            seededPieces.Add(c);
            drafts.Add(new Draft { Group = group, SeedTile = tile, SeedSettlement = settlement });
        }

        while (seeds.Count < target && drafts.Count < MaxProvinces)
        {
            Flood(world, nb, groupOf, group, sources, cost, owner, floors);
            int far = -1;
            double farCost = -1.0;
            for (int i = 0; i < n; i++)
            {
                if (groupOf[i] != group || double.IsPositiveInfinity(cost[i]))
                    continue;
                if (cost[i] > farCost)
                {
                    farCost = cost[i];
                    far = i;
                }
            }
            if (far < 0 || Crowds(world, far, seeds))
                break;
            seeds.Add(far);
            sources.Add(far);
            floors.Add(double.NegativeInfinity);
            drafts.Add(new Draft { Group = group, SeedTile = far });
        }
    }

    /// <summary>A group's big ranges, largest core first: runs of its land at or above
    /// the mountain line (MountainShaper.MountainFloor) with at least RangeCoreMin core
    /// tiles (all six neighbours on the same run). A run a seed already stands on (a
    /// mountain town) is left to that seed. Each comes back as its seed, the core tile
    /// nearest the core's middle, and its whole run, which floods as the province's
    /// source.</summary>
    private static List<(int seed, List<int> region)> BigRanges(WorldData world, int[] nb, int[] groupOf,
                                                                 int group, List<int> seeds)
    {
        int n = groupOf.Length;
        double level = MountainShaper.MountainFloor;
        var label = new int[n];
        for (int i = 0; i < n; i++)
            label[i] = (groupOf[i] == group && world.Tiles[i].Elevation >= level) ? 0 : -1;
        int[] run = Components(label, nb, out List<int> runSize, out _);
        var cores = new List<int>[runSize.Count];
        var members = new List<int>[runSize.Count];
        for (int c = 0; c < runSize.Count; c++)
        {
            cores[c] = new List<int>();
            members[c] = new List<int>();
        }
        for (int i = 0; i < n; i++)
        {
            if (run[i] < 0)
                continue;
            members[run[i]].Add(i);
            bool inner = true;
            for (int d = 0; d < 6 && inner; d++)
            {
                int next = nb[i * 6 + d];
                if (next < 0 || run[next] != run[i])
                    inner = false;
            }
            if (inner)
                cores[run[i]].Add(i);
        }
        var taken = new HashSet<int>();
        foreach (int t in seeds)
        {
            if (run[t] >= 0)
                taken.Add(run[t]);
        }
        var big = new List<int>();
        for (int c = 0; c < runSize.Count; c++)
        {
            if (cores[c].Count >= RangeCoreMin && !taken.Contains(c))
                big.Add(c);
        }
        big.Sort((a, b) => cores[a].Count != cores[b].Count ? cores[b].Count.CompareTo(cores[a].Count) : a.CompareTo(b));
        var found = new List<(int seed, List<int> region)>(big.Count);
        foreach (int c in big)
            found.Add((CentralTile(world, cores[c]), members[c]));
        return found;
    }

    /// <summary>The flood's sources for one group: each live draft's seed tile, or the
    /// whole run of a range draft. Fills, source by source, the draft it belongs to and
    /// its floor: a range pays RangeDescentPenalty a tile below the hills line
    /// (MountainShaper.HillsFloor); every other source has no floor.</summary>
    private static List<int> GroupSources(List<Draft> drafts, int group, List<int> sourceDraft, List<double> floors)
    {
        var sources = new List<int>();
        sourceDraft.Clear();
        floors.Clear();
        double hills = MountainShaper.HillsFloor;
        for (int k = 0; k < drafts.Count; k++)
        {
            var draft = drafts[k];
            if (draft.Group != group || !draft.Alive)
                continue;
            if (draft.Region != null)
            {
                foreach (int t in draft.Region)
                {
                    sources.Add(t);
                    sourceDraft.Add(k);
                    floors.Add(hills);
                }
            }
            else
            {
                sources.Add(draft.SeedTile);
                sourceDraft.Add(k);
                floors.Add(double.NegativeInfinity);
            }
        }
        return sources;
    }

    /// <summary>A group's settlements in seeding order: the seat, then cities, then
    /// towns; larger footprints first; then by index.</summary>
    private static List<int> SeedOrder(WorldData world, int[] groupOf, int group)
    {
        var order = new List<int>();
        for (int s = 0; s < world.Settlements.Count; s++)
        {
            var st = world.Settlements[s];
            if (world.InBounds(st.CenterX, st.CenterY) && groupOf[st.CenterY * world.Width + st.CenterX] == group)
                order.Add(s);
        }
        order.Sort((a, b) => CompareSeedOrder(world, a, b));
        return order;
    }

    private static int CompareSeedOrder(WorldData world, int a, int b)
    {
        var first = world.Settlements[a];
        var second = world.Settlements[b];
        int rankA = SeedRank(first), rankB = SeedRank(second);
        if (rankA != rankB)
            return rankA.CompareTo(rankB);
        int sizeA = first.Tiles?.Count ?? 0, sizeB = second.Tiles?.Count ?? 0;
        if (sizeA != sizeB)
            return sizeB.CompareTo(sizeA);
        return a.CompareTo(b);
    }

    private static int SeedRank(WorldSettlement s)
        => s.IsSeat ? 0 : (s.Tier == SettlementTier.City ? 1 : 2);

    private static bool Crowds(WorldData world, int tile, List<int> seeds)
    {
        int w = world.Width;
        int x = tile % w, y = tile / w;
        foreach (int s in seeds)
        {
            if (world.HexDistance(x, y, s % w, s / w) < MinSeedSpacing)
                return true;
        }
        return false;
    }

    // ── 4.4 The flood ────────────────────────────────────────────────────
    /// <summary>Multi-source cost flood over the tiles whose region label is id.
    /// cost[i] is the cheapest cost from any source (infinity where unreached) and
    /// owner[i] the position in sources of the seed that got there first (-1 where
    /// unreached). Ties go to the earlier relaxation, so it is deterministic. With
    /// floors (one per source), a flood entering a tile below its source's floor pays
    /// RangeDescentPenalty more.</summary>
    private static void Flood(WorldData world, int[] nb, int[] region, int id, List<int> sources,
                              double[] cost, int[] owner, List<double> floors = null)
    {
        int n = region.Length;
        for (int i = 0; i < n; i++)
        {
            cost[i] = double.PositiveInfinity;
            owner[i] = -1;
        }
        var done = new bool[n];
        var queue = new PriorityQueue<int, (double, int)>();
        for (int s = 0; s < sources.Count; s++)
        {
            int t = sources[s];
            if (t < 0 || t >= n || region[t] != id || owner[t] >= 0)
                continue;
            cost[t] = 0.0;
            owner[t] = s;
            queue.Enqueue(t, (0.0, t));
        }
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (done[cur])
                continue;
            done[cur] = true;
            for (int d = 0; d < 6; d++)
            {
                int next = nb[cur * 6 + d];
                if (next < 0 || done[next] || region[next] != id)
                    continue;
                double c = cost[cur] + EnterCost(world, cur, next, d);
                if (floors != null && world.Tiles[next].Elevation < floors[owner[cur]])
                    c += RangeDescentPenalty;
                if (c < cost[next])
                {
                    cost[next] = c;
                    owner[next] = owner[cur];
                    queue.Enqueue(next, (c, next));
                }
            }
        }
    }

    /// <summary>Cost to step from one tile into its neighbour across edge dir.</summary>
    private static double EnterCost(WorldData world, int from, int to, int dir)
    {
        double c = 1.0 + ElevationWeight * Math.Max(0.0, world.Tiles[to].Elevation - LowlandLevel);
        if ((world.Tiles[from].RiverEdges & (1 << dir)) != 0)
            c += RiverWeight;
        return c;
    }

    /// <summary>Seeds standing on no settlement move to the middle of the land their
    /// province grew over (the piece their seed is on), and the group floods again;
    /// settlement and range seeds stay put. RelaxRounds times, or until no seed moves. A seed the
    /// farthest-tile rule put at the tip of a peninsula comes back toward the middle of
    /// its province, so wild provinces come out rounder and nearer the average size.</summary>
    private static void RelaxGroup(WorldData world, int[] nb, int[] groupOf, int[] comp, int group,
                                   List<Draft> drafts, int[] prov, double[] cost, int[] owner)
    {
        int n = prov.Length;
        var ids = new List<int>();
        for (int k = 0; k < drafts.Count; k++)
        {
            if (drafts[k].Group == group)
                ids.Add(k);
        }
        if (ids.Count == 0)
            return;
        for (int round = 0; round < RelaxRounds; round++)
        {
            bool moved = false;
            foreach (int k in ids)
            {
                if (drafts[k].SeedSettlement >= 0 || drafts[k].Range)
                    continue;
                int seed = drafts[k].SeedTile;
                var body = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (prov[i] == k && comp[i] == comp[seed])
                        body.Add(i);
                }
                int middle = CentralTile(world, body);
                if (middle >= 0 && middle != seed)
                {
                    drafts[k].SeedTile = middle;
                    moved = true;
                }
            }
            if (!moved)
                return;
            var sourceDraft = new List<int>();
            var floors = new List<double>();
            List<int> sources = GroupSources(drafts, group, sourceDraft, floors);
            Flood(world, nb, groupOf, group, sources, cost, owner, floors);
            for (int i = 0; i < n; i++)
            {
                if (groupOf[i] == group)
                    prov[i] = owner[i] >= 0 ? sourceDraft[owner[i]] : -1;
            }
        }
    }

    /// <summary>Land no seed reached is a piece too small for a province of its own.
    /// Each such piece joins, whole, the province of the nearest tile of its kingdom
    /// that has one.</summary>
    private static void AttachUnreached(WorldData world, int[] groupOf, int[] comp, int compCount, int[] prov)
    {
        int w = world.Width, n = prov.Length;
        var reached = new bool[compCount];
        for (int i = 0; i < n; i++)
        {
            if (comp[i] >= 0 && prov[i] >= 0)
                reached[comp[i]] = true;
        }
        for (int c = 0; c < compCount; c++)
        {
            if (reached[c])
                continue;
            var piece = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (comp[i] == c)
                    piece.Add(i);
            }
            if (piece.Count == 0)
                continue;
            int group = groupOf[piece[0]];
            int best = -1, bestDistance = int.MaxValue;
            for (int j = 0; j < n; j++)
            {
                if (groupOf[j] != group || prov[j] < 0)
                    continue;
                int jx = j % w, jy = j / w;
                foreach (int t in piece)
                {
                    int dist = world.HexDistance(t % w, t / w, jx, jy);
                    if (dist < bestDistance)
                    {
                        bestDistance = dist;
                        best = j;
                    }
                }
            }
            if (best < 0)
                continue;
            foreach (int t in piece)
                prov[t] = prov[best];
        }
    }

    // ── 4.5 Balance ──────────────────────────────────────────────────────
    /// <summary>Cut the largest province over maxTiles in two, again and again. Two seeds
    /// go half a standard deviation either side of the middle of its body (the land its
    /// own seed reaches) along the body's long axis; each then moves to the middle of
    /// its half for up to SplitRounds rounds; the half holding the province's own seed
    /// keeps the province and the other becomes a new one. A cut that would leave
    /// either part under minTiles is refused and the province stays whole.</summary>
    private static void SplitPass(WorldData world, int[] nb, int[] prov, List<Draft> drafts,
                                  int minTiles, int maxTiles, double[] cost, int[] owner)
    {
        int n = prov.Length;
        var refused = new HashSet<int>();
        for (int guard = 0; guard < 64 && drafts.Count < MaxProvinces; guard++)
        {
            int[] size = Sizes(prov, drafts.Count);
            int p = -1;
            for (int k = 0; k < drafts.Count; k++)
            {
                if (!drafts[k].Alive || refused.Contains(k) || size[k] <= maxTiles)
                    continue;
                if (p < 0 || size[k] > size[p])
                    p = k;
            }
            if (p < 0)
                return;

            int seed = drafts[p].SeedTile;
            Flood(world, nb, prov, p, new List<int> { seed }, cost, owner);
            var body = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (prov[i] == p && !double.IsPositiveInfinity(cost[i]))
                    body.Add(i);
            }
            if (body.Count < 2 * minTiles)
            {
                refused.Add(p);
                continue;
            }
            var (seedA, seedB) = AxisSeeds(world, body);
            if (seedA < 0 || seedB < 0 || seedA == seedB)
            {
                refused.Add(p);
                continue;
            }
            for (int round = 0; round < SplitRounds; round++)
            {
                Flood(world, nb, prov, p, new List<int> { seedA, seedB }, cost, owner);
                var halfA = new List<int>();
                var halfB = new List<int>();
                foreach (int t in body)
                {
                    if (owner[t] == 0)
                        halfA.Add(t);
                    else if (owner[t] == 1)
                        halfB.Add(t);
                }
                int nextA = CentralTile(world, halfA), nextB = CentralTile(world, halfB);
                if (nextA < 0 || nextB < 0 || nextA == nextB || (nextA == seedA && nextB == seedB))
                    break;
                seedA = nextA;
                seedB = nextB;
            }
            Flood(world, nb, prov, p, new List<int> { seedA, seedB }, cost, owner);

            int keep = owner[seed];
            if (keep < 0)
            {
                refused.Add(p);
                continue;
            }
            int other = 1 - keep;
            int sizeOther = 0;
            for (int i = 0; i < n; i++)
            {
                if (prov[i] == p && owner[i] == other)
                    sizeOther++;
            }
            int sizeKeep = size[p] - sizeOther;
            if (sizeKeep < minTiles || sizeOther < minTiles)
            {
                refused.Add(p);
                continue;
            }
            int q = drafts.Count;
            drafts.Add(new Draft { Group = drafts[p].Group, SeedTile = other == 0 ? seedA : seedB, Range = drafts[p].Range });
            for (int i = 0; i < n; i++)
            {
                if (prov[i] == p && owner[i] == other)
                    prov[i] = q;
            }
        }
    }

    /// <summary>The body tiles nearest the two points half a standard deviation either
    /// side of the body's centroid along its long axis, as drawn.</summary>
    private static (int a, int b) AxisSeeds(WorldData world, List<int> body)
    {
        int w = world.Width, count = body.Count;
        var xs = new double[count];
        var ys = new double[count];
        double cx = 0.0, cy = 0.0;
        for (int k = 0; k < count; k++)
        {
            var (x, y) = RenderXY(body[k] % w, body[k] / w);
            xs[k] = x;
            ys[k] = y;
            cx += x;
            cy += y;
        }
        cx /= count;
        cy /= count;
        double sxx = 0.0, syy = 0.0, sxy = 0.0;
        for (int k = 0; k < count; k++)
        {
            sxx += (xs[k] - cx) * (xs[k] - cx);
            syy += (ys[k] - cy) * (ys[k] - cy);
            sxy += (xs[k] - cx) * (ys[k] - cy);
        }
        sxx /= count;
        syy /= count;
        sxy /= count;
        double half = (sxx - syy) / 2.0;
        double lambda = (sxx + syy) / 2.0 + Math.Sqrt(half * half + sxy * sxy);
        double ux, uy;
        if (Math.Abs(sxy) > 1e-12)
        {
            ux = lambda - syy;
            uy = sxy;
        }
        else if (sxx >= syy)
        {
            ux = 1.0;
            uy = 0.0;
        }
        else
        {
            ux = 0.0;
            uy = 1.0;
        }
        double length = Math.Sqrt(ux * ux + uy * uy);
        ux /= length;
        uy /= length;
        double reach = 0.5 * Math.Sqrt(Math.Max(0.0, lambda));
        return (NearestTo(body, xs, ys, cx - reach * ux, cy - reach * uy),
                NearestTo(body, xs, ys, cx + reach * ux, cy + reach * uy));
    }

    /// <summary>The body tile nearest a point (ties to the earlier tile).</summary>
    private static int NearestTo(List<int> body, double[] xs, double[] ys, double tx, double ty)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int k = 0; k < body.Count; k++)
        {
            double dist = (xs[k] - tx) * (xs[k] - tx) + (ys[k] - ty) * (ys[k] - ty);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                best = body[k];
            }
        }
        return best;
    }

    /// <summary>Fold the smallest province under minTiles into the neighbour of its
    /// kingdom it shares the most border with (then the smaller, then the lower
    /// number), again and again. A province with no such neighbour, an island, joins
    /// the nearest province of its kingdom instead. A kingdom's capital province is
    /// never folded away.</summary>
    private static void MergePass(WorldData world, int[] nb, int[] prov, List<Draft> drafts,
                                  int[] capitalTile, int minTiles)
    {
        int n = prov.Length;
        var refused = new HashSet<int>();
        for (int guard = 0; guard < 512; guard++)
        {
            int[] size = Sizes(prov, drafts.Count);
            int p = -1;
            for (int k = 0; k < drafts.Count; k++)
            {
                if (!drafts[k].Alive || refused.Contains(k) || size[k] >= minTiles)
                    continue;
                int capital = capitalTile[drafts[k].Group];
                if (capital >= 0 && prov[capital] == k)
                    continue;
                if (p < 0 || size[k] < size[p])
                    p = k;
            }
            if (p < 0)
                return;

            int group = drafts[p].Group;
            var shared = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                if (prov[i] != p)
                    continue;
                for (int d = 0; d < 6; d++)
                {
                    int j = nb[i * 6 + d];
                    if (j < 0)
                        continue;
                    int other = prov[j];
                    if (other < 0 || other == p || drafts[other].Group != group)
                        continue;
                    shared.TryGetValue(other, out int edges);
                    shared[other] = edges + 1;
                }
            }
            int q = -1;
            foreach (var pair in shared)
            {
                if (q < 0)
                {
                    q = pair.Key;
                    continue;
                }
                int best = shared[q];
                bool better = pair.Value > best
                    || (pair.Value == best && (size[pair.Key] < size[q]
                        || (size[pair.Key] == size[q] && pair.Key < q)));
                if (better)
                    q = pair.Key;
            }
            if (q < 0)
                q = NearestProvince(world, prov, drafts, p);
            if (q < 0)
            {
                refused.Add(p);
                continue;
            }
            for (int i = 0; i < n; i++)
            {
                if (prov[i] == p)
                    prov[i] = q;
            }
            drafts[p].Alive = false;
        }
    }

    /// <summary>The province of the same kingdom with the tile nearest any tile of
    /// province p (ties to the lower number), or -1.</summary>
    private static int NearestProvince(WorldData world, int[] prov, List<Draft> drafts, int p)
    {
        int w = world.Width, n = prov.Length, group = drafts[p].Group;
        var own = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (prov[i] == p)
                own.Add(i);
        }
        int best = -1, bestDistance = int.MaxValue;
        for (int j = 0; j < n; j++)
        {
            int q = prov[j];
            if (q < 0 || q == p || drafts[q].Group != group)
                continue;
            int jx = j % w, jy = j / w;
            foreach (int t in own)
            {
                int dist = world.HexDistance(t % w, t / w, jx, jy);
                if (dist < bestDistance || (dist == bestDistance && q < best))
                {
                    bestDistance = dist;
                    best = q;
                }
            }
        }
        return best;
    }

    private static int[] Sizes(int[] prov, int count)
    {
        var size = new int[count];
        foreach (int p in prov)
        {
            if (p >= 0)
                size[p]++;
        }
        return size;
    }

    // ── 4.7 Description ──────────────────────────────────────────────────
    /// <summary>Fill each province's size, ground, settlements, seat, capital flag,
    /// neighbours and tone from the finished map.</summary>
    private static void Describe(WorldData world, int[] nb, int[] comp, byte[] map, List<Province> provinces,
                                 int[] seedOf, bool[] rangeOf, int[] capitalSettlement, Dictionary<string, int> groupIndex)
    {
        int w = world.Width, n = map.Length, count = provinces.Count;

        var hist = new int[count, CharacterCount];
        for (int i = 0; i < n; i++)
        {
            if (map[i] == WorldData.NoProvince)
                continue;
            provinces[map[i]].TileCount++;
            hist[map[i], (int)CharacterOf(world.Tiles[i].Terrain)]++;
        }
        for (int p = 0; p < count; p++)
            provinces[p].Character = rangeOf[p] ? RangeCharacter(hist, p) : CharacterFrom(hist, p);

        for (int s = 0; s < world.Settlements.Count; s++)
        {
            var st = world.Settlements[s];
            if (!world.InBounds(st.CenterX, st.CenterY))
                continue;
            byte at = map[st.CenterY * w + st.CenterX];
            if (at != WorldData.NoProvince)
                provinces[at].Settlements.Add(s);
        }

        // Seat: the kingdom's seat city; else the chief settlement on the province's
        // main land (the piece its seed grew over); else the middle of that land.
        for (int p = 0; p < count; p++)
        {
            var pr = provinces[p];
            int mainPiece = comp[seedOf[p]];
            int seat = -1;
            if (groupIndex.TryGetValue(pr.KingdomId, out int gi))
            {
                int cs = capitalSettlement[gi];
                if (cs >= 0 && pr.Settlements.Contains(cs))
                {
                    seat = cs;
                    pr.IsCapital = true;
                }
            }
            if (seat < 0)
            {
                foreach (int s in pr.Settlements)
                {
                    var st = world.Settlements[s];
                    if (comp[st.CenterY * w + st.CenterX] != mainPiece)
                        continue;
                    if (seat < 0 || CompareSeedOrder(world, s, seat) < 0)
                        seat = s;
                }
            }
            if (seat >= 0)
            {
                pr.SeatSettlementIndex = seat;
                pr.SeatX = world.Settlements[seat].CenterX;
                pr.SeatY = world.Settlements[seat].CenterY;
            }
            else
            {
                var land = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (map[i] == p && comp[i] == mainPiece)
                        land.Add(i);
                }
                int middle = CentralTile(world, land);
                if (middle < 0)
                    middle = seedOf[p];
                pr.SeatX = middle % w;
                pr.SeatY = middle / w;
            }
        }

        var adjacent = new HashSet<int>[count];
        for (int p = 0; p < count; p++)
            adjacent[p] = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int a = map[i];
            if (a == WorldData.NoProvince)
                continue;
            for (int d = 0; d < 6; d++)
            {
                int j = nb[i * 6 + d];
                if (j < 0)
                    continue;
                int b = map[j];
                if (b != WorldData.NoProvince && b != a)
                    adjacent[a].Add(b);
            }
        }
        for (int p = 0; p < count; p++)
        {
            var list = new List<int>(adjacent[p]);
            list.Sort();
            provinces[p].Neighbors = list;
        }

        // Tone: the lowest tone no lower-numbered neighbour of the same kingdom has;
        // if all four are taken, the one fewest of them have.
        for (int p = 0; p < count; p++)
        {
            var used = new int[ToneCount];
            foreach (int q in provinces[p].Neighbors)
            {
                if (q < p && provinces[q].KingdomId == provinces[p].KingdomId)
                    used[provinces[q].Tone]++;
            }
            int tone = -1;
            for (int t = 0; t < ToneCount && tone < 0; t++)
            {
                if (used[t] == 0)
                    tone = t;
            }
            if (tone < 0)
            {
                tone = 0;
                for (int t = 1; t < ToneCount; t++)
                {
                    if (used[t] < used[tone])
                        tone = t;
                }
            }
            provinces[p].Tone = tone;
        }
    }

    /// <summary>Which ground a terrain counts as. Grassland, roads, coast and anything
    /// unlisted count as Lowland.</summary>
    private static ProvinceCharacter CharacterOf(OverworldHex.TerrainType terrain) => terrain switch
    {
        OverworldHex.TerrainType.Forest => ProvinceCharacter.Woodland,
        OverworldHex.TerrainType.Hills => ProvinceCharacter.Highland,
        OverworldHex.TerrainType.Mountain => ProvinceCharacter.Mountain,
        OverworldHex.TerrainType.Swamp => ProvinceCharacter.Wetland,
        OverworldHex.TerrainType.Marsh => ProvinceCharacter.Wetland,
        OverworldHex.TerrainType.Desert => ProvinceCharacter.Arid,
        OverworldHex.TerrainType.Volcanic => ProvinceCharacter.Volcanic,
        OverworldHex.TerrainType.Tundra => ProvinceCharacter.Frozen,
        OverworldHex.TerrainType.Snow => ProvinceCharacter.Frozen,
        OverworldHex.TerrainType.ArcaneGround => ProvinceCharacter.Arcane,
        OverworldHex.TerrainType.Ruins => ProvinceCharacter.Ruined,
        _ => ProvinceCharacter.Lowland,
    };

    /// <summary>Lowland when half or more of the province is lowland; otherwise the
    /// most common of the other grounds (ties to the earlier one).</summary>
    private static ProvinceCharacter CharacterFrom(int[,] hist, int p)
    {
        int total = 0;
        for (int c = 0; c < CharacterCount; c++)
            total += hist[p, c];
        int lowland = hist[p, (int)ProvinceCharacter.Lowland];
        if (total == 0 || lowland * 2 >= total)
            return ProvinceCharacter.Lowland;
        int best = -1;
        for (int c = 0; c < CharacterCount; c++)
        {
            if (c == (int)ProvinceCharacter.Lowland)
                continue;
            if (best < 0 || hist[p, c] > hist[p, best])
                best = c;
        }
        return best >= 0 && hist[p, best] > 0 ? (ProvinceCharacter)best : ProvinceCharacter.Lowland;
    }

    /// <summary>A range reads as its high ground: Frozen (snow) or Volcanic where more of
    /// it is that than mountain, otherwise Mountain, whatever its valleys.</summary>
    private static ProvinceCharacter RangeCharacter(int[,] hist, int p)
    {
        var best = ProvinceCharacter.Mountain;
        if (hist[p, (int)ProvinceCharacter.Frozen] > hist[p, (int)best])
            best = ProvinceCharacter.Frozen;
        if (hist[p, (int)ProvinceCharacter.Volcanic] > hist[p, (int)best])
            best = ProvinceCharacter.Volcanic;
        return best;
    }

    /// <summary>The word a placeholder name uses for a ground.</summary>
    public static string CharacterNoun(ProvinceCharacter character) => character switch
    {
        ProvinceCharacter.Woodland => "Weald",
        ProvinceCharacter.Highland => "Downs",
        ProvinceCharacter.Mountain => "Peaks",
        ProvinceCharacter.Wetland => "Fens",
        ProvinceCharacter.Arid => "Wastes",
        ProvinceCharacter.Volcanic => "Ashlands",
        ProvinceCharacter.Frozen => "Rime",
        ProvinceCharacter.Arcane => "Leylands",
        ProvinceCharacter.Ruined => "Barrows",
        _ => "Fields",
    };

    private static readonly string[] CompassPoints =
        { "East", "Southeast", "South", "Southwest", "West", "Northwest", "North", "Northeast" };

    /// <summary>Ids by place in the kingdom; placeholder names from the compass point
    /// of the province's seat seen from its capital's seat (the world's middle for
    /// land no kingdom owns) and the word for its ground. When one kingdom would use a
    /// name twice, the province nearer the capital keeps it and the farther ones are
    /// Far, then Outer, then numbered.</summary>
    private static void AssignIdsAndNames(WorldData world, List<Province> provinces)
    {
        var ordinal = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var province in provinces)
        {
            ordinal.TryGetValue(province.KingdomId, out int place);
            ordinal[province.KingdomId] = place + 1;
            string realm = string.IsNullOrEmpty(province.KingdomId) ? "wild" : province.KingdomId;
            province.Id = $"province:{realm}:{place}";
        }

        var origin = new Dictionary<string, (int x, int y)>(StringComparer.Ordinal);
        foreach (var province in provinces)
        {
            if (province.IsCapital && !origin.ContainsKey(province.KingdomId))
                origin[province.KingdomId] = (province.SeatX, province.SeatY);
        }
        var baseName = new string[provinces.Count];
        var distance = new int[provinces.Count];
        for (int i = 0; i < provinces.Count; i++)
        {
            var province = provinces[i];
            int ox = world.Width / 2, oy = world.Height / 2;
            if (origin.TryGetValue(province.KingdomId, out var capitalSeat))
            {
                ox = capitalSeat.x;
                oy = capitalSeat.y;
            }
            distance[i] = world.HexDistance(ox, oy, province.SeatX, province.SeatY);
            baseName[i] = province.IsCapital
                ? "Crownland"
                : $"{Compass(ox, oy, province.SeatX, province.SeatY)} {CharacterNoun(province.Character)}";
        }

        var sameName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < provinces.Count; i++)
        {
            string key = provinces[i].KingdomId + "|" + baseName[i];
            if (!sameName.TryGetValue(key, out var holders))
            {
                holders = new List<int>();
                sameName[key] = holders;
            }
            holders.Add(i);
        }
        foreach (var entry in sameName)
        {
            var holders = entry.Value;
            holders.Sort((a, b) => distance[a] != distance[b] ? distance[a].CompareTo(distance[b]) : a.CompareTo(b));
            for (int k = 0; k < holders.Count; k++)
            {
                string name = baseName[holders[k]];
                provinces[holders[k]].Name = k switch
                {
                    0 => name,
                    1 => $"Far {name}",
                    2 => $"Outer {name}",
                    _ => $"{name} {k + 1}",
                };
            }
        }
    }

    /// <summary>The eight-point compass direction from one tile to another as drawn
    /// (rows grow southward), or "Central" when they are the same tile.</summary>
    private static string Compass(int fromX, int fromY, int toX, int toY)
    {
        var (ax, ay) = RenderXY(fromX, fromY);
        var (bx, by) = RenderXY(toX, toY);
        double dx = bx - ax, dy = by - ay;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
            return "Central";
        int sector = (int)Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4.0));
        sector = ((sector % 8) + 8) % 8;
        return CompassPoints[sector];
    }

    /// <summary>A tile's centre in hex-size units on the flat-top odd-q layout: odd
    /// columns sit half a row lower.</summary>
    private static (double x, double y) RenderXY(int col, int row)
        => (1.5 * col, Sqrt3 * (row + 0.5 * (col & 1)));

    /// <summary>The tile of a list nearest the list's centroid as drawn (ties to the
    /// earlier tile), or -1 for an empty list.</summary>
    private static int CentralTile(WorldData world, List<int> tiles)
    {
        if (tiles == null || tiles.Count == 0)
            return -1;
        int w = world.Width;
        double sx = 0.0, sy = 0.0;
        foreach (int t in tiles)
        {
            var (x, y) = RenderXY(t % w, t / w);
            sx += x;
            sy += y;
        }
        sx /= tiles.Count;
        sy /= tiles.Count;
        int best = -1;
        double bestDistance = double.MaxValue;
        foreach (int t in tiles)
        {
            var (x, y) = RenderXY(t % w, t / w);
            double dist = (x - sx) * (x - sx) + (y - sy) * (y - sy);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                best = t;
            }
        }
        return best;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Checks (docs/provinces_v1.md section 4.8)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Every rule a built partition keeps. Empty when all hold. Reads the
    /// world and changes nothing, so it is safe on the live save.</summary>
    public static List<string> Validate(WorldData world)
    {
        var fails = new List<string>();
        int dropped = 0;
        void Fail(string message)
        {
            if (fails.Count < MaxFailLines)
                fails.Add(message);
            else
                dropped++;
        }

        if (world == null || world.Tiles == null || world.Tiles.Length == 0)
        {
            fails.Add("No world.");
            return fails;
        }
        if (!world.HasProvinces())
        {
            fails.Add("The world has no provinces.");
            return fails;
        }
        int w = world.Width, n = world.Tiles.Length;
        var provinces = world.Provinces;
        int count = provinces.Count;
        if (count > MaxProvinces)
            Fail($"{count} provinces, more than a byte map can name.");

        // Every tile: a kingdom's land is in a province of that kingdom, water in none.
        // Land no kingdom owns may be in a province of any kingdom (it joined the
        // nearest at the build) or in none (water opened into land since the build).
        var tilesIn = new int[count];
        for (int i = 0; i < n; i++)
        {
            int p = world.ProvinceMap[i];
            bool land = world.Tiles[i].IsLand;
            string kingdom = world.Tiles[i].KingdomId ?? "";
            if (p == WorldData.NoProvince)
            {
                if (land && kingdom.Length > 0)
                    Fail($"Land tile ({i % w},{i / w}) of '{kingdom}' is in no province.");
                continue;
            }
            if (!land)
            {
                Fail($"Water tile ({i % w},{i / w}) is in province {p}.");
                continue;
            }
            if (p >= count)
            {
                Fail($"Tile ({i % w},{i / w}) names province {p}, but there are {count}.");
                continue;
            }
            tilesIn[p]++;
            if (kingdom.Length > 0 && !string.Equals(provinces[p].KingdomId, kingdom, StringComparison.Ordinal))
                Fail($"Tile ({i % w},{i / w}) of '{kingdom}' is in {provinces[p].Id} of '{provinces[p].KingdomId}'.");
        }

        // The borders the map actually draws.
        int[] nb = NeighborTable(w, world.Height);
        var adjacent = new HashSet<int>[count];
        for (int p = 0; p < count; p++)
            adjacent[p] = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int a = world.ProvinceMap[i];
            if (a == WorldData.NoProvince || a >= count)
                continue;
            for (int d = 0; d < 6; d++)
            {
                int j = nb[i * 6 + d];
                if (j < 0)
                    continue;
                int b = world.ProvinceMap[j];
                if (b != WorldData.NoProvince && b < count && b != a)
                    adjacent[a].Add(b);
            }
        }

        // Every province.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var capitals = new HashSet<string>(StringComparer.Ordinal);
        for (int p = 0; p < count; p++)
        {
            var pr = provinces[p];
            string label = string.IsNullOrEmpty(pr.Id) ? $"Province {p}" : pr.Id;
            if (pr.Index != p)
                Fail($"{label} has Index {pr.Index} at position {p}.");
            if (pr.TileCount <= 0 || pr.TileCount != tilesIn[p])
                Fail($"{label} counts {pr.TileCount} tiles; the map gives it {tilesIn[p]}.");
            if (string.IsNullOrEmpty(pr.Id) || !ids.Add(pr.Id))
                Fail($"{label}: id missing or repeated.");
            if (string.IsNullOrEmpty(pr.Name) || !names.Add(pr.KingdomId + "|" + pr.Name))
                Fail($"{label}: name '{pr.Name}' missing or repeated in its kingdom.");
            if (!world.InBounds(pr.SeatX, pr.SeatY) || world.ProvinceMap[pr.SeatY * w + pr.SeatX] != p)
                Fail($"{label}: its seat ({pr.SeatX},{pr.SeatY}) is not in it.");
            bool seatSettlementValid = pr.SeatSettlementIndex >= -1 && pr.SeatSettlementIndex < world.Settlements.Count;
            if (!seatSettlementValid
                || (pr.SeatSettlementIndex >= 0
                    && (world.Settlements[pr.SeatSettlementIndex].CenterX != pr.SeatX
                        || world.Settlements[pr.SeatSettlementIndex].CenterY != pr.SeatY)))
                Fail($"{label}: seat settlement {pr.SeatSettlementIndex} is not at its seat.");
            if (pr.Tone < 0 || pr.Tone >= ToneCount)
                Fail($"{label}: tone {pr.Tone} out of range.");
            foreach (int s in pr.Settlements)
            {
                if (s < 0 || s >= world.Settlements.Count)
                {
                    Fail($"{label}: settlement {s} out of range.");
                    continue;
                }
                var st = world.Settlements[s];
                if (!world.InBounds(st.CenterX, st.CenterY) || world.ProvinceMap[st.CenterY * w + st.CenterX] != p)
                    Fail($"{label}: settlement {s} is centred outside it.");
            }
            bool ascending = true;
            for (int k = 1; k < pr.Neighbors.Count; k++)
            {
                if (pr.Neighbors[k - 1] >= pr.Neighbors[k])
                    ascending = false;
            }
            if (!ascending || !adjacent[p].SetEquals(pr.Neighbors))
                Fail($"{label}: neighbour list does not match its borders.");
            if (pr.IsCapital)
            {
                if (string.IsNullOrEmpty(pr.KingdomId))
                    Fail($"{label} is a capital of no kingdom.");
                else if (!capitals.Add(pr.KingdomId))
                    Fail($"{label}: a second capital for {pr.KingdomId}.");
                if (!seatSettlementValid || pr.SeatSettlementIndex < 0
                    || !world.Settlements[pr.SeatSettlementIndex].IsSeat)
                    Fail($"{label} is a capital without its seat city.");
            }
        }

        // Every seat city stands in its kingdom's capital province.
        for (int s = 0; s < world.Settlements.Count; s++)
        {
            var st = world.Settlements[s];
            if (!st.IsSeat || !world.InBounds(st.CenterX, st.CenterY))
                continue;
            int p = world.ProvinceMap[st.CenterY * w + st.CenterX];
            if (p == WorldData.NoProvince || p >= count || !provinces[p].IsCapital)
                Fail($"Seat city {s} at ({st.CenterX},{st.CenterY}) is not in a capital province.");
        }

        // A province is one body plus, at most, whole islands of its kingdom.
        foreach (string message in DetachedPieces(world, nb, out _, out _))
            Fail(message);

        if (dropped > 0)
            fails.Add($"...and {dropped} more.");
        return fails;
    }

    /// <summary>Counts the pieces of provinces lying apart from their main body (the
    /// largest piece) and returns a failure line for each such piece that is not a
    /// whole island of its kingdom.</summary>
    private static List<string> DetachedPieces(WorldData world, int[] nb, out int detached, out int detachedTiles)
    {
        var problems = new List<string>();
        int w = world.Width, n = world.Tiles.Length, count = world.Provinces.Count;
        detached = 0;
        detachedTiles = 0;

        // An island here is a connected run of one realm's land as the map holds it.
        var realmIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var realmOf = new int[n];
        var provOf = new int[n];
        for (int i = 0; i < n; i++)
        {
            int p = world.ProvinceMap[i];
            if (p == WorldData.NoProvince || p >= count)
            {
                realmOf[i] = -1;
                provOf[i] = -1;
                continue;
            }
            provOf[i] = p;
            string realm = world.Provinces[p].KingdomId ?? "";
            if (!realmIndex.TryGetValue(realm, out int ri))
            {
                ri = realmIndex.Count;
                realmIndex[realm] = ri;
            }
            realmOf[i] = ri;
        }
        int[] island = Components(realmOf, nb, out List<int> islandSize, out _);
        int[] piece = Components(provOf, nb, out List<int> pieceSize, out List<int> pieceProvince);

        var firstTile = new int[pieceSize.Count];
        for (int c = 0; c < firstTile.Length; c++)
            firstTile[c] = -1;
        for (int i = 0; i < n; i++)
        {
            if (piece[i] >= 0 && firstTile[piece[i]] < 0)
                firstTile[piece[i]] = i;
        }
        var main = new int[count];
        for (int p = 0; p < count; p++)
            main[p] = -1;
        for (int c = 0; c < pieceSize.Count; c++)
        {
            int p = pieceProvince[c];
            if (main[p] < 0 || pieceSize[c] > pieceSize[main[p]])
                main[p] = c;
        }
        for (int c = 0; c < pieceSize.Count; c++)
        {
            int p = pieceProvince[c];
            if (c == main[p])
                continue;
            detached++;
            detachedTiles += pieceSize[c];
            int at = firstTile[c];
            int whole = island[at] >= 0 ? islandSize[island[at]] : -1;
            if (whole != pieceSize[c])
                problems.Add($"{world.Provinces[p].Id} is split: a piece of {pieceSize[c]} tiles at ({at % w},{at / w}) is not a whole island.");
        }
        return problems;
    }

    /// <summary>A plain-text account of the partition for the guild panel's Province
    /// Report button: sizes, seats, how often borders follow rivers and high ground
    /// against the land as a whole, the grounds, each kingdom, and the checks.</summary>
    public static string Report(WorldData world)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PROVINCE REPORT (docs/provinces_v1.md) ===");
        if (world == null || world.Tiles == null || world.Tiles.Length == 0)
        {
            sb.AppendLine("No world in the active save.");
            return sb.ToString();
        }
        if (!world.HasProvinces())
        {
            sb.AppendLine("This world has no provinces yet. A world saved before them gets them the next time " +
                          "the cycle opens (the strategic view); a new world gets them when it is generated.");
            return sb.ToString();
        }

        var provinces = world.Provinces;
        int w = world.Width, n = world.Tiles.Length, count = provinces.Count;
        int land = 0, smallest = int.MaxValue, largest = 0, onSettlement = 0, capitalCount = 0;
        int degreeMin = int.MaxValue, degreeMax = 0, degreeSum = 0;
        var characters = new int[CharacterCount];
        var realms = new List<string>();
        var realmProvinces = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int p = 0; p < count; p++)
        {
            var pr = provinces[p];
            land += pr.TileCount;
            smallest = Math.Min(smallest, pr.TileCount);
            largest = Math.Max(largest, pr.TileCount);
            if (pr.SeatSettlementIndex >= 0)
                onSettlement++;
            if (pr.IsCapital)
                capitalCount++;
            characters[(int)pr.Character]++;
            degreeMin = Math.Min(degreeMin, pr.Neighbors.Count);
            degreeMax = Math.Max(degreeMax, pr.Neighbors.Count);
            degreeSum += pr.Neighbors.Count;
            if (!realmProvinces.TryGetValue(pr.KingdomId, out var list))
            {
                list = new List<int>();
                realmProvinces[pr.KingdomId] = list;
                realms.Add(pr.KingdomId);
            }
            list.Add(p);
        }
        sb.AppendLine($"{count} provinces over {land} land tiles in {realms.Count} realm(s): mean " +
                      $"{(double)land / Math.Max(1, count):F0} tiles, smallest {smallest}, largest {largest}.");
        sb.AppendLine($"Seats: {onSettlement} on a settlement ({capitalCount} capitals), " +
                      $"{count - onSettlement} at the middle of land with no settlement.");
        sb.AppendLine($"Neighbours per province: {degreeMin} to {degreeMax}, mean {(double)degreeSum / Math.Max(1, count):F1}.");

        // Edges inside one kingdom, each counted once (directions 0 to 2 from each tile).
        long edges = 0, edgesRiver = 0, edgesHigh = 0, borders = 0, bordersRiver = 0, bordersHigh = 0;
        for (int i = 0; i < n; i++)
        {
            if (world.ProvinceMap[i] == WorldData.NoProvince)
                continue;
            var (q, r) = HexCoord.OffsetToAxial(i % w, i / w);
            for (int d = 0; d < 3; d++)
            {
                var (dq, dr) = HexCoord.AxialDirections[d];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                if (!world.InBounds(nc, nr))
                    continue;
                int j = nr * w + nc;
                if (world.ProvinceMap[j] == WorldData.NoProvince
                    || !string.Equals(world.Tiles[i].KingdomId, world.Tiles[j].KingdomId, StringComparison.Ordinal))
                    continue;
                bool river = (world.Tiles[i].RiverEdges & (1 << d)) != 0;
                bool high = world.Tiles[i].Elevation > LowlandLevel && world.Tiles[j].Elevation > LowlandLevel;
                edges++;
                if (river)
                    edgesRiver++;
                if (high)
                    edgesHigh++;
                if (world.ProvinceMap[i] != world.ProvinceMap[j])
                {
                    borders++;
                    if (river)
                        bordersRiver++;
                    if (high)
                        bordersHigh++;
                }
            }
        }
        sb.AppendLine($"Borders inside kingdoms: {Percent(bordersRiver, borders)} run along a river " +
                      $"(of all edges inside kingdoms, {Percent(edgesRiver, edges)}); {Percent(bordersHigh, borders)} " +
                      $"cross high ground on both sides (of all edges, {Percent(edgesHigh, edges)}).");

        int[] nb = NeighborTable(w, world.Height);
        DetachedPieces(world, nb, out int detached, out int detachedTiles);
        sb.AppendLine($"Island pieces joined to a province across water: {detached} ({detachedTiles} tiles).");

        var grounds = new StringBuilder();
        for (int c = 0; c < CharacterCount; c++)
        {
            if (characters[c] == 0)
                continue;
            if (grounds.Length > 0)
                grounds.Append(", ");
            grounds.Append($"{(ProvinceCharacter)c} {characters[c]}");
        }
        sb.AppendLine($"Grounds: {grounds}.");

        sb.AppendLine("By realm:");
        foreach (string realm in realms)
        {
            var list = realmProvinces[realm];
            int tiles = 0, low = int.MaxValue, high = 0, abroad = 0;
            var line = new StringBuilder();
            foreach (int p in list)
            {
                var pr = provinces[p];
                tiles += pr.TileCount;
                low = Math.Min(low, pr.TileCount);
                high = Math.Max(high, pr.TileCount);
                foreach (int other in pr.Neighbors)
                {
                    if (provinces[other].KingdomId != realm)
                        abroad++;
                }
                if (line.Length > 0)
                    line.Append("; ");
                line.Append($"{pr.Name} {pr.TileCount}");
            }
            string realmName = string.IsNullOrEmpty(realm) ? "(no kingdom)" : realm;
            sb.AppendLine($"  {realmName}: {list.Count} provinces, {tiles} tiles, {low} to {high}, " +
                          $"{abroad} border(s) with other realms. {line}.");
        }

        var fails = Validate(world);
        if (fails.Count == 0)
        {
            sb.AppendLine("Checks: all hold.");
        }
        else
        {
            sb.AppendLine($"Checks: {fails.Count} failure line(s):");
            foreach (string fail in fails)
                sb.AppendLine("  " + fail);
        }
        return sb.ToString();
    }

    private static string Percent(long part, long whole)
        => whole <= 0 ? "n/a" : $"{100.0 * part / whole:F0}%";
}
