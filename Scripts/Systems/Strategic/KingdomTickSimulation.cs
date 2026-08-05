using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// KingdomTickSimulation.cs
//
// Purpose:  The per-lunation POLITICAL tick — the living-world layer
//           that turns the corruption tide into consequences the map
//           can lose. Drives the KingdomState fields that shipped for
//           Phase 2 (Stability, BorderPressure, PlayerInfluence,
//           ControllingFactionId) but were never mutated.
//
//           Runs AFTER CorruptionSpread each lunation. Per province:
//             1. STABILITY drifts — own corruption erodes cohesion;
//                player influence steadies it; clean provinces heal.
//             2. BORDER PRESSURE accrues — a more-corrupted neighbour
//                leans on an unstable province; bleeds off when the
//                gradient reverses.
//             3. WARFRONTS — when pressure boils over (>= open
//                threshold) tension becomes an open WARFRONT (a visible,
//                multi-lunation Advance bar) rather than an instant flip.
//                Each lunation the aggressor pushes the bar; at 100 the
//                province FALLS (control flips to the aggressor), at 0
//                the invasion is REPELLED. The player deploys into a
//                warfront and takes a side (Defend / Seize / Aid); the
//                expedition outcome swings the bar (ApplyIntervention).
//
// Layer:    System
// Collaborators: CalendarState (lunation clock), CorruptionSpread (runs
//                first), CampaignState (corruption 0-3 truth), KingdomState
//                (the mutated block), Warfront / WarfrontSide (data),
//                WorldData (adjacency + border tiles + staging points),
//                StrategicView (calls Tick + ApplyIntervention; renders
//                Warfronts + CycleState.PendingSiegeReports).
// See:      open_world_refactor_v1 §3.2; single_world_refactor_v2 Phase 2;
//           run_structure_v2 (Warfront archetype).
// ============================================================

/// <summary>Per-lunation kingdom drift + warfront (siege) resolution. Stateless
/// except a cached kingdom-adjacency map derived once per world. Reset() on a new
/// world / cycle.</summary>
public static class KingdomTickSimulation
{
    // ── Stability / pressure dials (primary knobs; tune after a full cycle) ──
    private const int StabilityDecayPerCorruptionLevel = 6;
    private const int StabilityHealPerLunation = 2;
    private const int StabilityBaseline = 50;
    private const int InfluenceStabilizeThreshold = 40;
    private const int StabilityFromInfluence = 4;

    private const int BorderPressurePerCorruptionGap = 18;
    private const int BorderPressureInstabilityBonus = 14;
    private const int InstabilityFloor = 30;
    private const int BorderPressureRelief = 12;

    // ── Supply dials (docs/supply_cache_spec_v1 — stock fed by SupplyCacheSystem) ──
    /// <summary>SupplyStock at/above which a kingdom's granaries steady it.</summary>
    private const int SupplyStabilityThreshold = 40;
    /// <summary>Stability healed per lunation by a well-supplied kingdom.</summary>
    private const int SupplyStabilityBonus = 3;
    /// <summary>Stability lost per lunation by a STARVED kingdom (stock 0).</summary>
    private const int SupplyStarvedPenalty = 3;
    /// <summary>Stock points per ±1 warfront advance — supplies are war muscle:
    /// a flush aggressor pushes harder, a flush defender digs in.</summary>
    private const int SupplyMusclePerAdvance = 25;

    // ── Warfront dials ──────────────────────────────────────────────────────
    /// <summary>BorderPressure at which tension becomes an open warfront.</summary>
    private const int WarfrontOpenThreshold = 60;
    /// <summary>Advance a freshly-opened warfront starts at.</summary>
    private const int WarfrontOpenAdvance = 15;
    /// <summary>Baseline aggressor momentum per lunation while a front is open.</summary>
    private const int WarfrontBaseAdvance = 6;
    /// <summary>Extra advance per lunation per corruption level the aggressor leads by.</summary>
    private const int WarfrontAdvancePerGap = 10;
    /// <summary>Extra advance when the defender is below the instability floor.</summary>
    private const int WarfrontUnstableBonus = 8;
    /// <summary>Advance relief when the defender holds strong player influence.</summary>
    private const int WarfrontInfluenceRelief = 8;
    /// <summary>Advance the front loses when the aggressor no longer leads on corruption.</summary>
    private const int WarfrontStalemateDecay = 10;
    /// <summary>Bar swing from a WON intervention expedition (in the side's favour).</summary>
    private const int WarfrontWinSwing = 45;
    /// <summary>Bar swing from a LOST intervention expedition (against the side).</summary>
    private const int WarfrontLossSwing = 20;

    /// <summary>Stability a just-conquered province is knocked down to.</summary>
    private const int SiegeStabilityFloor = 20;
    /// <summary>PlayerInfluence at/above which losing a province reads as "yours".</summary>
    private const int PlayerHoldingInfluence = 40;
    /// <summary>Sentinel faction id a province seized by the guild carries.</summary>
    public const string GuildFactionId = "guild";

    // Cached adjacency: kingdom id -> bordering kingdom ids. Dropped on Reset().
    private static Dictionary<string, HashSet<string>> _adjacency;
    private static WorldData _adjacencyWorld;

    // ══════════════════════════════════════════════════════════════════════
    // Lunation tick
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Run one lunation of kingdom drift, then advance + resolve warfronts.
    /// <paramref name="factionDisplay"/> maps a ControllingFactionId to a display
    /// name for reports (pass StrategicView.FactionDisplay).</summary>
    public static void Tick(CycleState cycle, Func<string, string> factionDisplay = null)
    {
        if (cycle?.World == null || cycle.Campaign == null || cycle.Kingdoms == null)
            return;

        var world = cycle.World;
        var campaign = cycle.Campaign;
        var kingdoms = cycle.Kingdoms;
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.Warfronts ??= new List<Warfront>();

        EnsureAdjacency(world);
        string convergence = ConvergenceKingdomId(world);

        // Snapshot corruption + control so the tick is simultaneous.
        var corr = new Dictionary<string, int>();
        var controlBefore = new Dictionary<string, string>();
        foreach (var kvp in kingdoms)
        {
            corr[kvp.Key] = campaign.GetCorruption(kvp.Value.TemplateRegionId);
            controlBefore[kvp.Key] = kvp.Value.ControllingFactionId;
        }

        foreach (var kvp in kingdoms)
        {
            string kid = kvp.Key;
            var k = kvp.Value;
            if (kid == convergence)
                continue; // the seat corrupts but does not govern, and never falls

            int myCorr = corr.TryGetValue(kid, out var mc) ? mc : 0;

            // ── 1. Stability drift ──────────────────────────────────────────
            int delta = -myCorr * StabilityDecayPerCorruptionLevel;
            if (k.PlayerInfluence >= InfluenceStabilizeThreshold)
                delta += StabilityFromInfluence;
            if (myCorr == 0 && k.Stability < StabilityBaseline)
                delta += StabilityHealPerLunation;
            // Supplies are civic glue: full granaries steady a province, empty
            // ones starve it — cutting a kingdom's caches is a legitimate
            // pre-war softening move (user ruling, 2026-08-05).
            if (k.SupplyStock >= SupplyStabilityThreshold)
                delta += SupplyStabilityBonus;
            else if (k.SupplyStock <= 0)
                delta -= SupplyStarvedPenalty;
            k.Stability = Mathf.Clamp(k.Stability + delta, 0, 100);

            // ── 2. Border pressure from hotter-corrupted neighbours ─────────
            string topAggressor = null;
            int topPressure = 0;
            if (_adjacency.TryGetValue(kid, out var neighbours))
            {
                foreach (var n in neighbours)
                {
                    int nCorr = corr.TryGetValue(n, out var ncv) ? ncv : 0;
                    int gap = nCorr - myCorr;
                    int cur = k.BorderPressure.TryGetValue(n, out var bp) ? bp : 0;

                    if (gap > 0)
                    {
                        int gain = gap * BorderPressurePerCorruptionGap;
                        if (k.Stability < InstabilityFloor)
                            gain += BorderPressureInstabilityBonus;
                        cur += gain;
                    }
                    else
                    {
                        cur = Mathf.Max(0, cur - BorderPressureRelief);
                    }
                    k.BorderPressure[n] = cur;

                    string nFaction = controlBefore.TryGetValue(n, out var nf) ? nf : "";
                    if (cur > topPressure && !string.IsNullOrEmpty(nFaction))
                    {
                        topPressure = cur;
                        topAggressor = n;
                    }
                }
            }

            // ── 3. Boil over into a warfront (replaces the old instant flip) ─
            if (topAggressor != null && topPressure >= WarfrontOpenThreshold
                && !HasOpenWarfrontFor(cycle, kid))
            {
                OpenWarfront(cycle, k, kid, topAggressor, controlBefore[topAggressor], factionDisplay);
                k.BorderPressure[topAggressor] = 0; // tension converts into open war
            }
        }

        // ── 4. Advance + resolve every open warfront ────────────────────────
        AdvanceAndResolveWarfronts(cycle, corr, factionDisplay);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Warfront lifecycle
    // ══════════════════════════════════════════════════════════════════════

    private static bool HasOpenWarfrontFor(CycleState cycle, string defenderKid)
    {
        // Cache sieges (TargetPoiIndex ≥ 0) share DefenderKingdomId with the host
        // province but are node-scoped — they must not block a real province war.
        foreach (var w in cycle.Warfronts)
            if (!w.Closed && !w.IsCacheSiege && w.DefenderKingdomId == defenderKid)
                return true;
        return false;
    }

    private static void OpenWarfront(CycleState cycle, KingdomState def, string defenderKid,
                                     string aggressorKid, string aggressorFaction,
                                     Func<string, string> fd)
    {
        var (fc, fr, sc, sr) = FindFrontAndStronghold(cycle.World, defenderKid, aggressorKid);
        string defName = string.IsNullOrEmpty(def.DisplayName) ? defenderKid : def.DisplayName;
        string aggName = cycle.Kingdoms.TryGetValue(aggressorKid, out var ak)
            ? ResolveName(fd, ak.ControllingFactionId)
            : ResolveName(fd, aggressorFaction);

        var wf = new Warfront
        {
            Id = $"{aggressorKid}>{defenderKid}",
            AggressorKingdomId = aggressorKid,
            DefenderKingdomId = defenderKid,
            AggressorFactionId = aggressorFaction,
            DefenderFactionId = def.ControllingFactionId,
            AggressorName = aggName,
            DefenderName = defName,
            Advance = WarfrontOpenAdvance,
            OpenedLunation = cycle.Calendar?.CurrentLunation ?? 0,
            FocusCol = fc,
            FocusRow = fr,
            StrongholdCol = sc,
            StrongholdRow = sr,
        };
        cycle.Warfronts.Add(wf);

        string rep = $"War breaks out — {aggName} presses into {defName}.";
        cycle.PendingSiegeReports.Add(rep);
        GD.Print($"[Warfront] {rep} (front {fc},{fr} · stronghold {sc},{sr})");
    }

    private static void AdvanceAndResolveWarfronts(CycleState cycle, Dictionary<string, int> corr,
                                                   Func<string, string> fd)
    {
        var kingdoms = cycle.Kingdoms;
        foreach (var wf in cycle.Warfronts)
        {
            if (wf.Closed)
                continue;
            if (wf.IsCacheSiege)
                continue; // cache sieges advance/resolve in SupplyCacheSystem.Tick
            if (!kingdoms.TryGetValue(wf.DefenderKingdomId, out var def))
            {
                wf.Closed = true;
                continue;
            }

            int aggCorr = corr.TryGetValue(wf.AggressorKingdomId, out var ac) ? ac : 0;
            int defCorr = corr.TryGetValue(wf.DefenderKingdomId, out var dc) ? dc : 0;
            int gap = aggCorr - defCorr;

            int adv = WarfrontBaseAdvance + Mathf.Max(0, gap) * WarfrontAdvancePerGap;
            if (def.Stability < InstabilityFloor)
                adv += WarfrontUnstableBonus;
            if (def.PlayerInfluence >= InfluenceStabilizeThreshold)
                adv -= WarfrontInfluenceRelief;
            if (gap <= 0)
                adv -= WarfrontStalemateDecay;

            // Supplies are war muscle (user ruling, 2026-08-05): the flush side
            // pushes/holds harder. Fed by SupplyCacheSystem's harvest.
            if (kingdoms.TryGetValue(wf.AggressorKingdomId, out var aggK))
                adv += aggK.SupplyStock / SupplyMusclePerAdvance;
            adv -= def.SupplyStock / SupplyMusclePerAdvance;

            wf.Advance += adv;
            ResolveWarfront(cycle, wf, def, fd);
        }
        cycle.Warfronts.RemoveAll(w => w.Closed);
    }

    /// <summary>Apply the boundary rules after a warfront's Advance changed: fall at
    /// ≥100, repel (or guild seizure) at ≤0, otherwise clamp inside the bar.</summary>
    private static void ResolveWarfront(CycleState cycle, Warfront wf, KingdomState def,
                                        Func<string, string> fd)
    {
        if (wf.Advance >= 100)
        {
            FallToAggressor(cycle, def, wf.DefenderKingdomId, wf.AggressorFactionId, fd);
            wf.Closed = true;
            wf.Resolution = "fell";
        }
        else if (wf.Advance <= 0)
        {
            if (wf.PlayerSeizing)
            {
                SeizeForGuild(cycle, def, wf.DefenderKingdomId);
                wf.Resolution = "seized";
            }
            else
            {
                // Sentiment: siege repelled — the archmage's kingdom survived.
                var repCampaign = cycle?.Campaign;
                if (repCampaign != null)
                {
                    string repArch = repCampaign.GetArchmageForRegion(def.TemplateRegionId);
                    if (!string.IsNullOrEmpty(repArch))
                        repCampaign.ShiftSentiment(repArch, +8);
                }

                def.Stability = Mathf.Min(100, def.Stability + 10);
                string name = string.IsNullOrEmpty(def.DisplayName) ? wf.DefenderKingdomId : def.DisplayName;
                string rep = $"The invasion of {name} is thrown back.";
                cycle.PendingSiegeReports.Add(rep);
                GD.Print($"[Warfront] {rep}");
                wf.Resolution = "repelled";
            }
            wf.Closed = true;
        }
        else
        {
            wf.Advance = Mathf.Clamp(wf.Advance, 1, 99);
        }
    }

    private static void FallToAggressor(CycleState cycle, KingdomState def, string defenderKid,
                                        string aggressorFactionId, Func<string, string> fd)
    {
        // Sentiment: a kingdom falling is a major blow — the archmage blames
        // the player for not preventing it (or is weakened by the loss).
        var fallCampaign = cycle?.Campaign;
        if (fallCampaign != null)
        {
            string fallArch = fallCampaign.GetArchmageForRegion(def.TemplateRegionId);
            if (!string.IsNullOrEmpty(fallArch))
                fallCampaign.ShiftSentiment(fallArch, -15);
        }

        bool wasPlayers = def.PlayerInfluence >= PlayerHoldingInfluence
                          || HoldsStagingPoint(cycle.World, defenderKid);
        string fallenName = string.IsNullOrEmpty(def.DisplayName) ? defenderKid : def.DisplayName;
        string victor = ResolveName(fd, aggressorFactionId);

        def.ControllingFactionId = aggressorFactionId;
        def.Stability = SiegeStabilityFloor;
        def.PlayerInfluence = 0;
        def.BorderPressure.Clear();

        string report = wasPlayers
            ? $"⚠ {fallenName} has fallen to {victor} — your hold there is lost."
            : $"{fallenName} has fallen to {victor}.";
        cycle.PendingSiegeReports.Add(report);
        GD.Print($"[Warfront] {report}");
    }

    private static void SeizeForGuild(CycleState cycle, KingdomState def, string defenderKid)
    {
        // Sentiment: seizing an archmage's kingdom for the guild — hostile act.
        var seizeCampaign = cycle?.Campaign;
        if (seizeCampaign != null)
        {
            string seizeArch = seizeCampaign.GetArchmageForRegion(def.TemplateRegionId);
            if (!string.IsNullOrEmpty(seizeArch))
                seizeCampaign.ShiftSentiment(seizeArch, -12);
        }

        string name = string.IsNullOrEmpty(def.DisplayName) ? defenderKid : def.DisplayName;
        def.ControllingFactionId = GuildFactionId;
        def.PlayerInfluence = 100;
        def.Stability = Mathf.Max(def.Stability, 40);
        def.BorderPressure.Clear();

        string report = $"★ {name} answers to the guild now — seized from the war.";
        cycle.PendingSiegeReports.Add(report);
        GD.Print($"[Warfront] {report}");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Intervention (called by StrategicView on return from a warfront deploy)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Apply a returned intervention expedition's outcome to its warfront.
    /// <paramref name="success"/> = the party extracted alive (held/took the field).
    /// Resolves the front immediately so the consequence is visible on return.</summary>
    public static void ApplyIntervention(CycleState cycle, string warfrontId, WarfrontSide side,
                                         bool success, Func<string, string> fd = null)
    {
        if (cycle?.Kingdoms == null)
            return;
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.Warfronts ??= new List<Warfront>();

        Warfront wf = null;
        foreach (var w in cycle.Warfronts)
            if (!w.Closed && w.Id == warfrontId) { wf = w; break; }

        if (wf == null)
        {
            string late = "You reach the front, but the war there had already run its course.";
            cycle.PendingSiegeReports.Add(late);
            GD.Print($"[Warfront] {late}");
            return;
        }

        // Cache sieges resolve on their own (node-scoped) rules — the cache's
        // controller flips, never the province. Delegated wholesale.
        if (wf.IsCacheSiege)
        {
            SupplyCacheSystem.ApplyCacheIntervention(cycle, wf, side, success, fd);
            cycle.Warfronts.RemoveAll(w => w.Closed);
            return;
        }

        if (!cycle.Kingdoms.TryGetValue(wf.DefenderKingdomId, out var def))
            return;

        string defName = string.IsNullOrEmpty(def.DisplayName) ? wf.DefenderKingdomId : def.DisplayName;

        switch (side)
        {
            case WarfrontSide.Defend:
                wf.Advance += success ? -WarfrontWinSwing : WarfrontLossSwing;
                if (success)
                {
                    def.Stability = Mathf.Min(100, def.Stability + 12);
                    def.PlayerInfluence = Mathf.Min(100, def.PlayerInfluence + 15);
                }
                cycle.PendingSiegeReports.Add(success
                    ? $"You held the line at {defName} — the assault falters."
                    : $"Your defence of {defName} was broken.");
                break;

            case WarfrontSide.Seize:
                wf.Advance += success ? -WarfrontWinSwing : WarfrontLossSwing;
                if (success)
                {
                    wf.PlayerSeizing = true;
                    def.PlayerInfluence = Mathf.Min(100, def.PlayerInfluence + 20);
                }
                cycle.PendingSiegeReports.Add(success
                    ? $"You bleed both armies at {defName} and raise the guild's banner."
                    : $"Your bid for {defName} was thrown back.");
                break;

            case WarfrontSide.Aid:
                wf.Advance += success ? WarfrontWinSwing : -WarfrontLossSwing;
                cycle.PendingSiegeReports.Add(success
                    ? $"You spearhead the assault on {defName}."
                    : $"Your assault on {defName} stalled.");
                break;
        }

        // Sentiment: successful defence earns favor with the defender's archmage;
        // aiding the aggressor (or failing to defend) costs it.
        var intCampaign = cycle.Campaign;
        if (intCampaign != null)
        {
            string intArch = intCampaign.GetArchmageForRegion(def.TemplateRegionId);
            if (!string.IsNullOrEmpty(intArch))
            {
                int sentDelta = side switch
                {
                    WarfrontSide.Defend => success ? +12 : -3,
                    WarfrontSide.Seize  => success ? -8 : 0,    // seizing their kingdom = hostile
                    WarfrontSide.Aid    => success ? -10 : +3,   // aiding their attacker
                    _ => 0
                };
                if (sentDelta != 0)
                    intCampaign.ShiftSentiment(intArch, sentDelta);
            }
        }

        GD.Print($"[Warfront] Intervention ({side}, success={success}) at {defName}: Advance now {wf.Advance}.");
        ResolveWarfront(cycle, wf, def, fd);
        cycle.Warfronts.RemoveAll(w => w.Closed);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Site the front (a defender tile bordering the aggressor — the deploy
    /// target) and the besieging stronghold (an aggressor tile a few hexes deeper —
    /// the objective). Returns (-1,…) if no border exists. The stronghold prefers a
    /// tile 2–3 hexes into aggressor ground with no existing POI, falling back to the
    /// nearest aggressor tile.</summary>
    private static (int fc, int fr, int sc, int sr) FindFrontAndStronghold(
        WorldData world, string defenderKid, string aggressorKid)
    {
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var t = world.GetTile(x, y);
                if (t.KingdomId != defenderKid || t.IsWater)
                    continue;

                bool border = false;
                foreach (var (nx, ny) in HexCoord.Neighbors(x, y, world.Width, world.Height))
                    if (world.GetTile(nx, ny).KingdomId == aggressorKid) { border = true; break; }
                if (!border)
                    continue;

                // Front found. Site the stronghold among nearby aggressor tiles.
                int sc = -1, sr = -1, bestScore = int.MaxValue;
                int fbC = -1, fbR = -1, fbD = int.MaxValue; // nearest aggressor fallback
                for (int y2 = Math.Max(0, y - 4); y2 <= Math.Min(world.Height - 1, y + 4); y2++)
                {
                    for (int x2 = Math.Max(0, x - 4); x2 <= Math.Min(world.Width - 1, x + 4); x2++)
                    {
                        var at = world.GetTile(x2, y2);
                        if (at.KingdomId != aggressorKid || at.IsWater)
                            continue;
                        int d = HexCoord.OffsetDistance(x, y, x2, y2);
                        if (d > 0 && d < fbD) { fbD = d; fbC = x2; fbR = y2; }
                        if (d >= 2 && d <= 3)
                        {
                            int score = Math.Abs(d - 3) + (world.PoiAt(x2, y2) != null ? 100 : 0);
                            if (score < bestScore) { bestScore = score; sc = x2; sr = y2; }
                        }
                    }
                }
                if (sc < 0) { sc = fbC; sr = fbR; } // fall back to the nearest aggressor tile
                return (x, y, sc, sr);
            }
        }
        return (-1, -1, -1, -1);
    }

    private static bool HoldsStagingPoint(WorldData world, string kid)
    {
        if (world.StagingPoints == null)
            return false;
        foreach (var sp in world.StagingPoints)
            if (world.GetTile(sp.X, sp.Y).KingdomId == kid)
                return true;
        return false;
    }

    private static string ResolveName(Func<string, string> factionDisplay, string factionId)
    {
        if (factionDisplay != null)
        {
            string d = factionDisplay(factionId);
            if (!string.IsNullOrEmpty(d))
                return d;
        }
        return Prettify(factionId);
    }

    private static string Prettify(string factionId)
    {
        if (string.IsNullOrEmpty(factionId))
            return "an unknown power";
        var parts = factionId.Replace('_', ' ').Split(' ');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
        string joined = string.Join(" ", parts);
        return joined.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? joined : "The " + joined;
    }

    /// <summary>Bordering kingdom ids for <paramref name="kid"/> — the shared
    /// adjacency cache, exposed for SupplyCacheSystem's envy-pressure pass.</summary>
    public static IEnumerable<string> NeighborsOf(WorldData world, string kid)
    {
        EnsureAdjacency(world);
        return _adjacency.TryGetValue(kid, out var n)
            ? (IEnumerable<string>)n
            : System.Array.Empty<string>();
    }

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
    }

    private static string ConvergenceKingdomId(WorldData world)
    {
        if (world.ConvergenceX < 0 || world.ConvergenceY < 0)
            return "";
        return world.GetTile(world.ConvergenceX, world.ConvergenceY).KingdomId ?? "";
    }

    /// <summary>Drop cached adjacency (call on new world / cycle reset).</summary>
    public static void Reset()
    {
        _adjacency = null;
        _adjacencyWorld = null;
    }
}
