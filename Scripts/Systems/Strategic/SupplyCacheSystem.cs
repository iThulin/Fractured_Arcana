using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// SupplyCacheSystem.cs
//
// Purpose:  Supply caches — the strategic POI the factions fight over
//           (docs/supply_cache_spec_v1). Each kingdom seeds with a set
//           number of caches in its territory; whoever CONTROLS a cache
//           harvests Supplies from it every lunation. Kingdom supply
//           stock is war muscle (warfront advance), civic glue
//           (stability), and patrol funding; supply ENVY between
//           neighbours feeds border pressure so wars erupt over access.
//           Guild-controlled caches pay into the treasury
//           (CycleState.Supplies) and can be overseen by a companion
//           for a yield boost — at the cost of that companion being
//           injured if the cache falls.
//
//           Sieges reuse the Warfront machinery: a Warfront with
//           TargetPoiIndex >= 0 is a CACHE siege — same markers, same
//           intervention dialog, same deploy round-trip — but it
//           advances here (supply-muscle formula, not corruption) and
//           resolves by flipping the CACHE's controller, never the
//           province.
//
// Layer:    System
// Collaborators: KingdomTickSimulation (delegates cache-siege advance /
//                intervention here; reads SupplyStock for war muscle +
//                stability), StrategicView (markers, cache dialog,
//                lay-siege deploys, calls TickPressure/Tick in
//                RunLunationTick), WorldData (WorldPoi.SupplyControllerId
//                / OverseerCompanionId), CycleState (Supplies treasury),
//                OverworldFactionManager (patrol bonus), CompanionRoster
//                (overseers are unavailable, like envoys).
// See:      docs/supply_cache_spec_v1.md
// ============================================================

/// <summary>Per-lunation supply-cache economy + cache-siege lifecycle. Stateless;
/// all state lives on WorldPoi / KingdomState / CycleState / Warfront.</summary>
public static class SupplyCacheSystem
{
    // ── Seeding ─────────────────────────────────────────────────────────────
    /// <summary>Caches seeded into each kingdom's territory (the user-ruled
    /// "set number per kingdom").</summary>
    public const int CachesPerKingdom = 2;
    private const int SeedMinSeatDist = 3;
    private const int SeedMaxSeatDist = 10;
    private const int SeedSpacing = 4;

    // ── Harvest dials ───────────────────────────────────────────────────────
    /// <summary>Supplies one cache yields its controller per lunation.</summary>
    public const int BaseYield = 6;
    /// <summary>Extra yield when a guild cache has an overseer (+50%).</summary>
    public const int OverseerYieldBonus = 3;
    /// <summary>Kingdom supply stock ceiling.</summary>
    public const int KingdomStockCap = 100;
    /// <summary>Stock every kingdom burns per lunation (armies eat).</summary>
    public const int KingdomUpkeep = 2;

    // ── War-over-supplies dials ─────────────────────────────────────────────
    /// <summary>Cache-count lead a neighbour needs before envy pressure flows.</summary>
    public const int EnvyCacheLead = 2;
    /// <summary>Border pressure per lunation from supply envy. Deliberately larger
    /// than KingdomTickSimulation.BorderPressureRelief (12) so envy overcomes calm
    /// borders and wars genuinely erupt over cache access.</summary>
    public const int EnvyPressure = 18;

    // ── Cache-siege dials ───────────────────────────────────────────────────
    private const int CacheSiegeOpenAdvance = 15;
    private const int CacheSiegeBaseAdvance = 10;
    /// <summary>Stock points per +1 siege advance (aggressor) / -1 (defender).</summary>
    private const int MusclePerAdvance = 25;
    /// <summary>Advance relief when the guild cache has an overseer on site.</summary>
    private const int OverseerDefenseRelief = 12;
    /// <summary>Advance relief for an unovergseen guild cache (the garrison alone).</summary>
    private const int GuildBaseRelief = 4;
    /// <summary>A player-laid siege wanes this much per lunation left unattended.</summary>
    private const int PlayerSiegeWane = 15;
    /// <summary>%-chance per lunation a kingdom moves to retake a foreign-held
    /// cache inside its own territory.</summary>
    private const int RecaptureChancePct = 40;
    /// <summary>Advance swing when the player defends and fails / aids and wins.</summary>
    private const int CacheInterventionSwing = 25;

    // ── Kingdom-power dials (read by collaborators) ─────────────────────────
    /// <summary>Stock at/above which the archmage fields an extra patrol.</summary>
    public const int PatrolStockThreshold = 60;

    public const string GuildId = KingdomTickSimulation.GuildFactionId;

    // ══════════════════════════════════════════════════════════════════════
    // Seeding
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Idempotent: seed CachesPerKingdom supply caches into every
    /// non-convergence kingdom's wilderness if the world has none yet. Covers
    /// both new worlds and existing mid-cycle saves (runs from StrategicView
    /// _Ready and from the lunation tick). Caches seed UNDISCOVERED (v1.1 user
    /// ruling): they surface through play — exploration (the expedition
    /// discovery sweep), siege news naming them, the Spymaster chart packet,
    /// or bargained supply-lines intel — never as free map knowledge.</summary>
    public static void EnsureSeeded(CycleState cycle)
    {
        AssertRoundTripOnce();
        var world = cycle?.World;
        if (world == null || cycle.Kingdoms == null)
            return;
        foreach (var p in world.Pois)
            if (p.Kind == PoiKind.SupplyCache)
            {
                MigrateFog(cycle); // v1 seeded everything Discovered — re-hide once
                return;            // already seeded
            }

        var rng = new RandomNumberGenerator { Seed = (ulong)(cycle.WorldSeed ^ 0x5CA1AB1E) };
        string convergence = "";
        if (world.InBounds(world.ConvergenceX, world.ConvergenceY))
            convergence = world.GetTile(world.ConvergenceX, world.ConvergenceY).KingdomId ?? "";

        int seeded = 0;
        foreach (var kvp in cycle.Kingdoms)
        {
            string kid = kvp.Key;
            if (kid == convergence)
                continue;

            // Anchor spacing on the seat when one exists.
            int seatX = -1, seatY = -1;
            foreach (var p in world.Pois)
                if (p.Kind == PoiKind.Seat && p.KingdomId == kid)
                { seatX = p.X; seatY = p.Y; break; }

            var candidates = new List<(int x, int y)>();
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    var t = world.GetTile(x, y);
                    if (t.KingdomId != kid || t.IsWater || t.PoiIndex >= 0 ||
                        t.SettlementIndex >= 0 || t.ShardZoneIndex >= 0 || t.IsStagingPoint)
                        continue;
                    if (seatX >= 0)
                    {
                        int d = HexCoord.OffsetDistance(seatX, seatY, x, y);
                        if (d < SeedMinSeatDist || d > SeedMaxSeatDist)
                            continue;
                    }
                    candidates.Add((x, y));
                }
            }

            var placed = new List<(int x, int y)>();
            int attempts = 0;
            while (placed.Count < CachesPerKingdom && attempts < 200 && candidates.Count > 0)
            {
                attempts++;
                var (x, y) = candidates[(int)(rng.Randi() % (uint)candidates.Count)];
                bool tooClose = false;
                foreach (var (px, py) in placed)
                    if (HexCoord.OffsetDistance(px, py, x, y) < SeedSpacing)
                    { tooClose = true; break; }
                if (tooClose || world.GetTile(x, y).PoiIndex >= 0)
                    continue;
                AddCachePoi(world, x, y, kid);
                placed.Add((x, y));
                seeded++;
            }
        }
        cycle.SupplyCacheFogApplied = true; // fresh seeds are already fog-correct
        if (seeded > 0)
        {
            GD.Print($"[SupplyCache] Seeded {seeded} supply caches across the kingdoms (undiscovered).");
            SaveManager.MarkDirty();
        }
    }

    /// <summary>One-time v1 → v1.1 fog migration: v1 seeded every cache
    /// Discovered with a Charted tile. Re-hide the ones the player hasn't
    /// EARNED knowledge of — anything not guild-controlled and not sitting on
    /// a tile the player actually explored. Guild caches (seized ground) and
    /// caches in explored country stay on the map; you don't forget a depot
    /// you've walked past.</summary>
    private static void MigrateFog(CycleState cycle)
    {
        if (cycle.SupplyCacheFogApplied)
            return;
        var world = cycle.World;
        int hidden = 0;
        foreach (var p in world.Pois)
        {
            if (p.Kind != PoiKind.SupplyCache || !p.Discovered)
                continue;
            if (ControllerOf(p) == GuildId)
                continue;
            if (world.InBounds(p.X, p.Y) &&
                world.GetTile(p.X, p.Y).Discovery == TileDiscovery.Explored)
                continue;
            p.Discovered = false;
            hidden++;
        }
        cycle.SupplyCacheFogApplied = true;
        if (hidden > 0)
            GD.Print($"[SupplyCache] Fog migration: re-hid {hidden} unearned cache(s).");
        SaveManager.MarkDirty();
    }

    private static void AddCachePoi(WorldData world, int x, int y, string kingdomId)
    {
        int poiIndex = world.Pois.Count;
        world.Pois.Add(new WorldPoi
        {
            X = x,
            Y = y,
            Kind = PoiKind.SupplyCache,
            KingdomId = kingdomId,
            Discovered = false,         // surfaces through play, never for free
            GrantsStaging = false,
            SupplyControllerId = kingdomId,
        });
        if (world.TryIndex(x, y, out int idx))
            world.Tiles[idx].PoiIndex = poiIndex;
    }

    /// <summary>Reveal every cache in a kingdom (bargained supply-lines intel,
    /// or any future diplomacy channel). Charts the tiles so the map context
    /// isn't a marker floating in blank fog. Returns how many were NEW.</summary>
    public static int RevealCachesInKingdom(CycleState cycle, string kingdomId)
    {
        var world = cycle?.World;
        if (world == null || string.IsNullOrEmpty(kingdomId))
            return 0;
        int revealed = 0;
        foreach (var p in world.Pois)
        {
            if (p.Kind != PoiKind.SupplyCache || p.KingdomId != kingdomId || p.Discovered)
                continue;
            Discover(world, p);
            revealed++;
        }
        if (revealed > 0)
            SaveManager.MarkDirty();
        return revealed;
    }

    /// <summary>Mark a cache Discovered and chart its tile — the one write path
    /// every intel channel (news, negotiation, spymaster arrives pre-charted)
    /// shares, so a marker never floats on Unseen fog.</summary>
    private static void Discover(WorldData world, WorldPoi p)
    {
        p.Discovered = true;
        if (world.TryIndex(p.X, p.Y, out int idx) &&
            world.Tiles[idx].Discovery == TileDiscovery.Unseen)
            world.Tiles[idx].Discovery = TileDiscovery.Charted;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Queries
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Who harvests this cache: a kingdom id or "guild". Empty
    /// SupplyControllerId (pre-feature POIs) falls back to the host kingdom.</summary>
    public static string ControllerOf(WorldPoi p) =>
        string.IsNullOrEmpty(p.SupplyControllerId) ? (p.KingdomId ?? "") : p.SupplyControllerId;

    /// <summary>Display name for a controller id ("guild" or kingdom id).</summary>
    public static string ControllerDisplay(CycleState cycle, string controllerId)
    {
        if (controllerId == GuildId)
            return "the Guild";
        if (cycle?.Kingdoms != null && cycle.Kingdoms.TryGetValue(controllerId, out var k)
            && !string.IsNullOrEmpty(k.DisplayName))
            return k.DisplayName;
        return controllerId;
    }

    /// <summary>Caches currently harvested by <paramref name="controllerId"/>.</summary>
    public static int CountControlledBy(CycleState cycle, string controllerId)
    {
        int n = 0;
        var world = cycle?.World;
        if (world == null) return 0;
        foreach (var p in world.Pois)
            if (p.Kind == PoiKind.SupplyCache && ControllerOf(p) == controllerId)
                n++;
        return n;
    }

    /// <summary>This cache's per-lunation yield (overseer bonus included).</summary>
    public static int YieldOf(WorldPoi p) =>
        BaseYield + (ControllerOf(p) == GuildId && !string.IsNullOrEmpty(p.OverseerCompanionId)
            ? OverseerYieldBonus : 0);

    /// <summary>True if the kingdom holds at least one still-hidden cache —
    /// gates the supply-lines-intel negotiation offer.</summary>
    public static bool HasUndiscoveredCache(CycleState cycle, string kingdomId)
    {
        var world = cycle?.World;
        if (world == null) return false;
        foreach (var p in world.Pois)
            if (p.Kind == PoiKind.SupplyCache && p.KingdomId == kingdomId && !p.Discovered)
                return true;
        return false;
    }

    /// <summary>The open cache siege targeting POI <paramref name="poiIndex"/>, or null.</summary>
    public static Warfront SiegeFor(CycleState cycle, int poiIndex)
    {
        if (cycle?.Warfronts == null) return null;
        foreach (var w in cycle.Warfronts)
            if (!w.Closed && w.TargetPoiIndex == poiIndex)
                return w;
        return null;
    }

    /// <summary>True if this companion is posted as a cache overseer (unavailable
    /// for party / envoy duty — same single-source discipline as envoy missions:
    /// derived from WorldPoi.OverseerCompanionId, never a flag on Companion).</summary>
    public static bool IsOverseer(string companionId)
    {
        var world = SaveManager.ActiveSave?.Cycle?.World;
        if (world == null || string.IsNullOrEmpty(companionId)) return false;
        foreach (var p in world.Pois)
            if (p.Kind == PoiKind.SupplyCache && p.OverseerCompanionId == companionId)
                return true;
        return false;
    }

    /// <summary>+1 patrol in regions whose kingdom is flush with supplies —
    /// the "harvested supply becomes local power" knob for expeditions.
    /// Keyed by TEMPLATE region id (what OverworldFactionManager has).</summary>
    public static int PatrolBonusForRegion(string regionId)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.Kingdoms == null || string.IsNullOrEmpty(regionId)) return 0;
        foreach (var kvp in cycle.Kingdoms)
            if (kvp.Value.TemplateRegionId == regionId)
                return kvp.Value.SupplyStock >= PatrolStockThreshold ? 1 : 0;
        return 0;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Lunation tick — pressure half (runs BEFORE KingdomTickSimulation.Tick)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Supply envy: a kingdom whose neighbour harvests EnvyCacheLead
    /// more caches accrues border pressure AGAINST that neighbour — written
    /// before the kingdom tick so it feeds the same boil-over that opens
    /// warfronts. This is how wars erupt over access to supply nodes.</summary>
    public static void TickPressure(CycleState cycle)
    {
        if (cycle?.World == null || cycle.Kingdoms == null)
            return;
        EnsureSeeded(cycle);

        // Count caches per controller once.
        var counts = new Dictionary<string, int>();
        foreach (var p in cycle.World.Pois)
        {
            if (p.Kind != PoiKind.SupplyCache) continue;
            string c = ControllerOf(p);
            counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
        }

        foreach (var kvp in cycle.Kingdoms)
        {
            string kid = kvp.Key;
            var k = kvp.Value;
            int mine = counts.TryGetValue(kid, out var m) ? m : 0;
            foreach (var n in KingdomTickSimulation.NeighborsOf(cycle.World, kid))
            {
                if (!cycle.Kingdoms.TryGetValue(n, out var nk) ||
                    string.IsNullOrEmpty(nk.ControllingFactionId))
                    continue;
                // Guild-held provinces never turn hungry aggressor on their own —
                // the player starts the player's wars (mirrors the step-3 guard
                // that bars guild kingdoms from opening cache sieges).
                if (nk.ControllingFactionId == GuildId)
                    continue;
                int theirs = counts.TryGetValue(n, out var t) ? t : 0;
                // The HUNGRY side is n: they covet k's caches → pressure ON k FROM n.
                if (mine - theirs >= EnvyCacheLead)
                {
                    int cur = k.BorderPressure.TryGetValue(n, out var bp) ? bp : 0;
                    k.BorderPressure[n] = cur + EnvyPressure;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Lunation tick — harvest + cache sieges (runs AFTER KingdomTickSimulation)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Harvest income for every controller (post-flip control, so a
    /// province that just fell pays its new master), burn kingdom upkeep, let
    /// the AI open recapture sieges, then advance + resolve every open cache
    /// siege. Appends "Word from the frontier" report lines.</summary>
    public static void Tick(CycleState cycle, Func<string, string> factionDisplay = null)
    {
        if (cycle?.World == null || cycle.Kingdoms == null)
            return;
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.Warfronts ??= new List<Warfront>();
        EnsureSeeded(cycle);

        var world = cycle.World;

        // ── 1. Harvest ──────────────────────────────────────────────────────
        int guildIncome = 0, guildCaches = 0;
        foreach (var p in world.Pois)
        {
            if (p.Kind != PoiKind.SupplyCache) continue;
            string ctrl = ControllerOf(p);
            int yield = YieldOf(p);
            if (ctrl == GuildId)
            {
                cycle.Supplies += yield;
                guildIncome += yield;
                guildCaches++;
            }
            else if (cycle.Kingdoms.TryGetValue(ctrl, out var k))
            {
                k.SupplyStock = Mathf.Min(KingdomStockCap, k.SupplyStock + yield);
            }
        }
        if (guildCaches > 0)
        {
            cycle.PendingSiegeReports.Add(
                $"Supply lines: +{guildIncome} supplies from {guildCaches} " +
                (guildCaches == 1 ? "cache." : "caches."));
        }

        // ── 2. Upkeep — every kingdom eats, cache or no cache ───────────────
        foreach (var kvp in cycle.Kingdoms)
            kvp.Value.SupplyStock = Mathf.Max(0, kvp.Value.SupplyStock - KingdomUpkeep);

        // ── 3. AI recapture: a kingdom moves on foreign-held caches in its own
        //       territory (this is also how the enemy besieges PLAYER caches —
        //       guild holdings are almost always seized ground). Friendly and
        //       allied courts leave guild caches alone. One siege per kingdom
        //       at a time; besieged caches aren't double-besieged. ───────────
        foreach (var kvp in cycle.Kingdoms)
        {
            string kid = kvp.Key;
            var k = kvp.Value;
            if (string.IsNullOrEmpty(k.ControllingFactionId) ||
                k.ControllingFactionId == GuildId)
                continue;
            if (HasOpenCacheSiegeBy(cycle, kid))
                continue;

            for (int i = 0; i < world.Pois.Count; i++)
            {
                var p = world.Pois[i];
                if (p.Kind != PoiKind.SupplyCache || p.KingdomId != kid)
                    continue;
                string ctrl = ControllerOf(p);
                if (ctrl == kid || SiegeFor(cycle, i) != null)
                    continue;
                if (ctrl == GuildId &&
                    CouncilQueries.StanceFor(cycle, kid) >= KingdomStance.Friendly)
                    continue; // friendly courts tolerate the guild's holdings
                if (GD.Randi() % 100 >= RecaptureChancePct)
                    continue;
                OpenAiCacheSiege(cycle, i, kid, factionDisplay);
                break; // one new siege per kingdom per lunation
            }
        }

        // ── 4. Advance + resolve open cache sieges ──────────────────────────
        foreach (var wf in cycle.Warfronts)
        {
            if (wf.Closed || wf.TargetPoiIndex < 0)
                continue;
            if (wf.TargetPoiIndex >= world.Pois.Count)
            { wf.Closed = true; continue; }
            var poi = world.Pois[wf.TargetPoiIndex];

            if (wf.AggressorKingdomId == GuildId)
            {
                // A player-laid siege wanes without the player pushing it.
                wf.Advance -= PlayerSiegeWane;
                if (wf.Advance <= 0)
                {
                    wf.Closed = true;
                    wf.Resolution = "dispersed";
                    cycle.PendingSiegeReports.Add(
                        $"Your siege of the supply cache in {HostName(cycle, poi)} dispersed for want of pressure.");
                }
                continue;
            }

            int aggStock = cycle.Kingdoms.TryGetValue(wf.AggressorKingdomId, out var agg)
                ? agg.SupplyStock : 0;
            int adv = CacheSiegeBaseAdvance + aggStock / MusclePerAdvance;

            string ctrl = ControllerOf(poi);
            if (ctrl == GuildId)
                adv -= string.IsNullOrEmpty(poi.OverseerCompanionId)
                    ? GuildBaseRelief : OverseerDefenseRelief;
            else if (cycle.Kingdoms.TryGetValue(ctrl, out var ck))
                adv -= ck.SupplyStock / MusclePerAdvance;

            wf.Advance += adv;
            ResolveCacheSiege(cycle, wf, poi, factionDisplay);
        }
        cycle.Warfronts.RemoveAll(w => w.Closed);
    }

    private static bool HasOpenCacheSiegeBy(CycleState cycle, string aggressorKid)
    {
        foreach (var w in cycle.Warfronts)
            if (!w.Closed && w.TargetPoiIndex >= 0 && w.AggressorKingdomId == aggressorKid)
                return true;
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Siege lifecycle
    // ══════════════════════════════════════════════════════════════════════

    private static void OpenAiCacheSiege(CycleState cycle, int poiIndex, string aggressorKid,
                                         Func<string, string> fd)
    {
        var world = cycle.World;
        var poi = world.Pois[poiIndex];
        cycle.Kingdoms.TryGetValue(aggressorKid, out var aggK);
        string aggName = ResolveName(fd, aggK?.ControllingFactionId ?? aggressorKid);
        string ctrlName = ControllerDisplay(cycle, ControllerOf(poi));

        var (sc, sr) = SiteSiegeCamp(world, poi);
        var wf = new Warfront
        {
            Id = $"cache:{poiIndex}:{aggressorKid}",
            AggressorKingdomId = aggressorKid,
            DefenderKingdomId = poi.KingdomId,       // host province: corr/def lookups stay valid
            AggressorFactionId = aggK?.ControllingFactionId ?? "",
            DefenderFactionId = ControllerOf(poi),
            AggressorName = aggName,
            DefenderName = $"the supply cache in {HostName(cycle, poi)}",
            Advance = CacheSiegeOpenAdvance,
            OpenedLunation = cycle.Calendar?.CurrentLunation ?? 0,
            FocusCol = poi.X,
            FocusRow = poi.Y,
            StrongholdCol = sc,
            StrongholdRow = sr,
            TargetPoiIndex = poiIndex,
        };
        cycle.Warfronts.Add(wf);

        // News travels: a report that NAMES a cache is intelligence — the
        // frontier panel telling you about it IS discovering it.
        Discover(world, poi);

        bool ours = ControllerOf(poi) == GuildId;
        string rep = ours
            ? $"⚠ {aggName} lays siege to YOUR supply cache in {HostName(cycle, poi)}."
            : $"{aggName} lays siege to the supply cache in {HostName(cycle, poi)} (held by {ctrlName}).";
        cycle.PendingSiegeReports.Add(rep);
        GD.Print($"[SupplyCache] {rep}");
    }

    /// <summary>Player-initiated siege on a cache the guild doesn't control.
    /// Creates the cache warfront at Advance 50 with the guild as aggressor;
    /// the caller immediately deploys into it (side Seize) — one successful
    /// expedition flips the cache. Returns the new warfront (or the existing
    /// open siege if one is already running).</summary>
    public static Warfront OpenPlayerSiege(CycleState cycle, int poiIndex)
    {
        var existing = SiegeFor(cycle, poiIndex);
        if (existing != null)
            return existing;

        var world = cycle.World;
        var poi = world.Pois[poiIndex];
        var (sc, sr) = SiteSiegeCamp(world, poi);
        var wf = new Warfront
        {
            Id = $"cache:{poiIndex}:guild",
            AggressorKingdomId = GuildId,
            DefenderKingdomId = poi.KingdomId,
            AggressorFactionId = GuildId,
            DefenderFactionId = ControllerOf(poi),
            AggressorName = "the Guild",
            DefenderName = $"the supply cache in {HostName(cycle, poi)}",
            Advance = 50,
            OpenedLunation = cycle.Calendar?.CurrentLunation ?? 0,
            FocusCol = poi.X,
            FocusRow = poi.Y,
            StrongholdCol = sc,
            StrongholdRow = sr,
            TargetPoiIndex = poiIndex,
            PlayerSeizing = true,
        };
        cycle.Warfronts.Add(wf);
        return wf;
    }

    /// <summary>Boundary rules for a cache siege after its Advance moved:
    /// ≥100 the cache falls to the aggressor, ≤0 the siege breaks.</summary>
    private static void ResolveCacheSiege(CycleState cycle, Warfront wf, WorldPoi poi,
                                          Func<string, string> fd)
    {
        if (wf.Advance >= 100)
        {
            FlipCache(cycle, poi, wf.AggressorKingdomId, fd);
            wf.Closed = true;
            wf.Resolution = "fell";
        }
        else if (wf.Advance <= 0)
        {
            wf.Closed = true;
            wf.Resolution = "repelled";
            cycle.PendingSiegeReports.Add(
                $"The siege of the supply cache in {HostName(cycle, poi)} is broken.");
        }
        else
        {
            wf.Advance = Mathf.Clamp(wf.Advance, 1, 99);
        }
    }

    /// <summary>Cache-scoped intervention outcomes — the branch ApplyIntervention
    /// delegates to for warfronts with TargetPoiIndex ≥ 0. Cache fights resolve
    /// FAST: a successful Defend breaks the siege outright; a successful Seize
    /// flips the cache to the guild on the spot. Only failures leave the bar
    /// running.</summary>
    public static void ApplyCacheIntervention(CycleState cycle, Warfront wf,
                                              WarfrontSide side, bool success,
                                              Func<string, string> fd = null)
    {
        cycle.PendingSiegeReports ??= new List<string>();
        var world = cycle.World;
        if (wf.TargetPoiIndex < 0 || wf.TargetPoiIndex >= world.Pois.Count)
        { wf.Closed = true; return; }
        var poi = world.Pois[wf.TargetPoiIndex];
        string place = HostName(cycle, poi);

        switch (side)
        {
            case WarfrontSide.Defend:
                if (success)
                {
                    wf.Closed = true;
                    wf.Resolution = "repelled";
                    cycle.PendingSiegeReports.Add(
                        $"You broke the siege of the supply cache in {place} — the lines hold.");
                    ShiftHostSentiment(cycle, poi, ControllerOf(poi) == GuildId ? 0 : +8);
                }
                else
                {
                    wf.Advance += CacheInterventionSwing;
                    cycle.PendingSiegeReports.Add(
                        $"Your defence of the supply cache in {place} failed — the siege tightens.");
                    ResolveCacheSiege(cycle, wf, poi, fd);
                }
                break;

            case WarfrontSide.Seize:
                if (success)
                {
                    FlipCache(cycle, poi, GuildId, fd);
                    wf.Closed = true;
                    wf.Resolution = "seized";
                    ShiftHostSentiment(cycle, poi, -8);
                }
                else if (wf.AggressorKingdomId == GuildId)
                {
                    wf.Closed = true;
                    wf.Resolution = "repelled";
                    cycle.PendingSiegeReports.Add(
                        $"Your siege of the supply cache in {place} was thrown back.");
                }
                else
                {
                    wf.Advance += CacheInterventionSwing / 2;
                    cycle.PendingSiegeReports.Add(
                        $"Your bid for the supply cache in {place} failed amid the fighting.");
                    ResolveCacheSiege(cycle, wf, poi, fd);
                }
                break;

            case WarfrontSide.Aid:
                wf.Advance += success ? CacheInterventionSwing : -CacheInterventionSwing / 2;
                cycle.PendingSiegeReports.Add(success
                    ? $"You spearhead the assault on the supply cache in {place}."
                    : $"Your assault on the supply cache in {place} stalled.");
                ResolveCacheSiege(cycle, wf, poi, fd);
                break;
        }
        GD.Print($"[SupplyCache] Intervention ({side}, success={success}) at cache " +
                 $"{wf.TargetPoiIndex}: advance {wf.Advance}, closed={wf.Closed}.");
    }

    /// <summary>Control flips. If the guild loses an overseen cache, the overseer
    /// is injured in the rout (the user-ruled stake of the posting) and sent home.</summary>
    private static void FlipCache(CycleState cycle, WorldPoi poi, string newController,
                                  Func<string, string> fd)
    {
        string old = ControllerOf(poi);
        string place = HostName(cycle, poi);

        if (old == GuildId && !string.IsNullOrEmpty(poi.OverseerCompanionId))
        {
            var c = cycle.Companions?.Find(x => x.Id == poi.OverseerCompanionId);
            if (c != null && !c.IsPermadead)
            {
                c.InjuredLunationsRemaining =
                    Math.Max(c.InjuredLunationsRemaining, 1 + (int)(GD.Randi() % 2));
                cycle.PendingSiegeReports.Add(
                    $"⚠ {c.Name} was overseeing the cache — wounded in the rout, " +
                    $"home in {c.InjuredLunationsRemaining} lunation(s).");
            }
            poi.OverseerCompanionId = "";
        }

        poi.SupplyControllerId = newController;
        Discover(cycle.World, poi); // the report below names it — news is discovery

        string report = newController == GuildId
            ? $"★ The supply cache in {place} answers to the guild now."
            : old == GuildId
                ? $"⚠ YOUR supply cache in {place} has fallen to {ControllerDisplay(cycle, newController)}."
                : $"The supply cache in {place} falls to {ControllerDisplay(cycle, newController)}.";
        cycle.PendingSiegeReports.Add(report);
        GD.Print($"[SupplyCache] {report}");
        SaveManager.MarkDirty();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Overseers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Post a companion to a guild-controlled cache (+50% yield, injured
    /// if it falls). Fails (false) if the cache isn't the guild's, already has an
    /// overseer, or the companion is unavailable.</summary>
    public static bool AssignOverseer(GuildSaveData save, int poiIndex, string companionId)
    {
        var cycle = save?.Cycle;
        var world = cycle?.World;
        if (world == null || poiIndex < 0 || poiIndex >= world.Pois.Count)
            return false;
        var poi = world.Pois[poiIndex];
        if (poi.Kind != PoiKind.SupplyCache || ControllerOf(poi) != GuildId ||
            !string.IsNullOrEmpty(poi.OverseerCompanionId))
            return false;

        var c = cycle.Companions?.Find(x => x.Id == companionId);
        if (c == null || !OverseerEligible(save, c))
            return false;

        poi.OverseerCompanionId = companionId;
        SaveManager.MarkDirty();
        SaveManager.Save();
        GD.Print($"[SupplyCache] {c.Name} posted as overseer of cache {poiIndex}.");
        return true;
    }

    /// <summary>Recall the overseer from a cache (no cost — they walk home).</summary>
    public static bool RecallOverseer(GuildSaveData save, int poiIndex)
    {
        var world = save?.Cycle?.World;
        if (world == null || poiIndex < 0 || poiIndex >= world.Pois.Count)
            return false;
        var poi = world.Pois[poiIndex];
        if (string.IsNullOrEmpty(poi.OverseerCompanionId))
            return false;
        poi.OverseerCompanionId = "";
        SaveManager.MarkDirty();
        SaveManager.Save();
        return true;
    }

    /// <summary>Recruited, alive, healthy, home, and not otherwise committed —
    /// the same bar envoy missions set, plus "not in the active party".</summary>
    public static bool OverseerEligible(GuildSaveData save, Companion c)
    {
        if (c == null || !c.IsRecruited || c.IsPermadead || c.IsInjured)
            return false;
        if (save.ActivePartyCompanionIds.Contains(c.Id))
            return false;
        if (CouncilQueries.IsOnMission(c.Id) || CouncilQueries.IsImprisoned(c.Id))
            return false;
        if (IsOverseer(c.Id))
            return false;
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The host kingdom's display name (caches are referred to by
    /// WHERE they sit, whoever currently holds them).</summary>
    public static string HostName(CycleState cycle, WorldPoi poi)
    {
        if (cycle?.Kingdoms != null && !string.IsNullOrEmpty(poi.KingdomId) &&
            cycle.Kingdoms.TryGetValue(poi.KingdomId, out var k) &&
            !string.IsNullOrEmpty(k.DisplayName))
            return k.DisplayName;
        return "the wilds";
    }

    /// <summary>Site the besieger camp — the intervention objective — on a free
    /// land tile 2–3 hexes from the cache (any kingdom; sieges camp where they
    /// can). Returns (-1,-1) when nothing fits; the expedition then falls back
    /// to its no-stronghold rule (first combat won counts).</summary>
    private static (int sc, int sr) SiteSiegeCamp(WorldData world, WorldPoi poi)
    {
        int bestX = -1, bestY = -1, bestScore = int.MaxValue;
        for (int y = Math.Max(0, poi.Y - 3); y <= Math.Min(world.Height - 1, poi.Y + 3); y++)
        {
            for (int x = Math.Max(0, poi.X - 3); x <= Math.Min(world.Width - 1, poi.X + 3); x++)
            {
                var t = world.GetTile(x, y);
                if (t.IsWater || t.PoiIndex >= 0 || t.IsStagingPoint)
                    continue;
                int d = HexCoord.OffsetDistance(poi.X, poi.Y, x, y);
                if (d < 2 || d > 3)
                    continue;
                int score = Math.Abs(d - 2) + (t.SettlementIndex >= 0 ? 10 : 0);
                if (score < bestScore)
                { bestScore = score; bestX = x; bestY = y; }
            }
        }
        return (bestX, bestY);
    }

    private static void ShiftHostSentiment(CycleState cycle, WorldPoi poi, int delta)
    {
        if (delta == 0 || cycle?.Campaign == null || cycle.Kingdoms == null)
            return;
        if (!cycle.Kingdoms.TryGetValue(poi.KingdomId ?? "", out var k))
            return;
        string arch = cycle.Campaign.GetArchmageForRegion(k.TemplateRegionId);
        if (!string.IsNullOrEmpty(arch))
            cycle.Campaign.ShiftSentiment(arch, delta);
    }

    private static string ResolveName(Func<string, string> fd, string factionId)
    {
        if (fd != null)
        {
            string d = fd(factionId);
            if (!string.IsNullOrEmpty(d))
                return d;
        }
        return string.IsNullOrEmpty(factionId) ? "an unknown power" : factionId;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Save-shape assertion (house rule for new serialized fields)
    // ══════════════════════════════════════════════════════════════════════

    private static bool _assertedOnce;

    /// <summary>One-shot round-trip assertion through the REAL SaveManager
    /// serializer for every field this feature added (WorldPoi controller/
    /// overseer, Warfront.TargetPoiIndex, KingdomState.SupplyStock,
    /// CycleState.Supplies). Mirrors CompanionInjurySystem.AssertRoundTripOnce.</summary>
    public static void AssertRoundTripOnce()
    {
        if (_assertedOnce) return;
        _assertedOnce = true;
        try
        {
            var poi = new WorldPoi { Kind = PoiKind.SupplyCache, SupplyControllerId = "guild", OverseerCompanionId = "comp_x" };
            var poiBack = System.Text.Json.JsonSerializer.Deserialize<WorldPoi>(
                System.Text.Json.JsonSerializer.Serialize(poi, SaveManager.JsonOptions), SaveManager.JsonOptions);
            var wf = new Warfront { TargetPoiIndex = 7 };
            var wfBack = System.Text.Json.JsonSerializer.Deserialize<Warfront>(
                System.Text.Json.JsonSerializer.Serialize(wf, SaveManager.JsonOptions), SaveManager.JsonOptions);
            var ks = new KingdomState { SupplyStock = 42 };
            var ksBack = System.Text.Json.JsonSerializer.Deserialize<KingdomState>(
                System.Text.Json.JsonSerializer.Serialize(ks, SaveManager.JsonOptions), SaveManager.JsonOptions);

            if (poiBack.SupplyControllerId != "guild" || poiBack.OverseerCompanionId != "comp_x" ||
                wfBack.TargetPoiIndex != 7 || ksBack.SupplyStock != 42)
                GD.PushError("[SupplyCache] Save round-trip FAILED — supply fields are not serializing.");
            else
                GD.Print("[SupplyCache] Save round-trip OK.");
        }
        catch (Exception e)
        {
            GD.PushError($"[SupplyCache] Save round-trip assertion threw: {e.Message}");
        }
    }
}
