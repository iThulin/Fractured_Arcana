using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// HiringHallService.cs
//
// Purpose:        K3 hiring halls (companion_item_systems v2.1 §5a):
//                 per-city candidate stock, lazy per-lunation refresh,
//                 Steward-regard pricing, the hire itself (gold →
//                 roster move), and the R25 hiring-in-territory deed.
//                 Towns are DEFERRED — they have no interaction
//                 surface until they are enterable; halls live in the
//                 cities the services menu already reaches.
// Layer:          System (strategic)
// Collaborators:  HiringHallState.cs (save block),
//                 CandidateGenerator.cs (the matrix),
//                 CityExploreService.cs (the CityId convention),
//                 CityServicesHost.cs (UI), CouncilEcho.cs (R25 deed),
//                 CompanionRoster / GuildSaveData (the roster).
// Notes:          Save-adjacent. RoundTripAssert covers the new block;
//                 call it once from a debug entry after first build.
// ============================================================

/// <summary>Stateless hiring-hall logic over <see cref="CycleState.HiringHalls"/>.
/// Stock refreshes lazily when a hall is opened in a new lunation — no work on
/// the tick for cities the player never visits. Hiring MOVES the candidate
/// record into the save roster; the same Companion object is never in both
/// lists.</summary>
public static class HiringHallService
{
    // ── Tuning (K3 starting values) ──────────────────────────────────────

    /// <summary>Candidate count: ordinary city 1–3, seat/capital 2–3 (§5a
    /// "city halls outdraw town halls" — with towns deferred, the seat is
    /// the quality tier).</summary>
    public const int MinCandidates = 1;
    public const int MaxCandidates = 3;

    /// <summary>Steward discount: -5% per point of positive Steward Regard,
    /// capped at 25%. "Befriending the money-man legible" (§7c sibling rule).</summary>
    public const int DiscountPerRegard = 5;
    public const int DiscountCapPct = 25;

    /// <summary>An authored, still-unrecruited, available companion is
    /// GUARANTEED in every refreshed hall while any remain unoffered — the
    /// storefront's replacement path, made deterministic (ruling 2026-08-13):
    /// the politics game requires companions, and the first one must be a
    /// plan, not a lottery. At most one per hall, never the same person in
    /// two halls at once; the guarantee drains naturally as the starters are
    /// hired, and arc-gated people are untouched (not IsAvailable until
    /// their flags fire — rarity stays where rarity matters).</summary>
    public const bool AuthoredGuaranteed = true;

    // ═════════════════════════════════════════════════════════════════════
    // Stock
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Fetch the hall for a city, rolling fresh stock if this is the
    /// first open or a new lunation. Unsold candidates from earlier lunations
    /// are replaced — halls are a flow, not a warehouse (and the player can't
    /// stockpile options by never opening the menu).</summary>
    public static HiringHallState GetOrRefresh(CycleState cycle, WorldSettlement city)
    {
        if (cycle == null || city == null) return null;

        string id = CityExploreService.CityId(city);
        var hall = cycle.HiringHalls.FirstOrDefault(h => h.CityId == id);
        if (hall == null)
        {
            hall = new HiringHallState { CityId = id };
            cycle.HiringHalls.Add(hall);
        }

        // Prune stale entries: an authored companion listed here can have been
        // recruited (or killed) through another system since the roll — a hall
        // must never sell someone the guild already has, or a corpse.
        var save = SaveManager.ActiveSave;
        if (save != null)
        {
            hall.Candidates.RemoveAll(cand =>
            {
                var r = save.Companions.FirstOrDefault(x => x.Id == cand.Id);
                return r != null && (r.IsRecruited || r.IsPermadead);
            });
        }

        int now = cycle.Calendar.CurrentLunation;
        if (hall.LastRefreshLunation == now && hall.Candidates.Count > 0)
            return hall;
        if (hall.LastRefreshLunation == now)
            return hall; // rolled this lunation and sold out — no re-roll scumming

        RollStock(cycle, city, hall, now);
        hall.LastRefreshLunation = now;
        SaveManager.MarkDirty();
        return hall;
    }

    private static void RollStock(CycleState cycle, WorldSettlement city,
        HiringHallState hall, int lunation)
    {
        hall.Candidates.Clear();

        // Deterministic per (city, lunation): the rolled stock is persisted,
        // but the seed is stable too (FNV-1a — string.GetHashCode is NOT
        // stable across processes) so a regenerated state matches, same
        // discipline as CityExploreService.
        var rng = new RandomNumberGenerator();
        rng.Seed = Fnv1a(CityExploreService.CityId(city)) ^ (ulong)(lunation * 2654435761L);

        int quality = city.IsSeat ? 1 : 0;
        int count = quality > 0
            ? rng.RandiRange(2, MaxCandidates)
            : rng.RandiRange(MinCandidates, MaxCandidates);

        for (int i = 0; i < count; i++)
            hall.Candidates.Add(CandidateGenerator.Generate(
                rng, quality, hall.CityId, lunation, i));

        // K5 (§5a): corruption displacement — when any OTHER kingdom's region
        // sits at CorruptionLevel 2+, its desperate reach this hall at 60% of
        // the asking price ("the world's collapse priced into its labor
        // market"). Enters at 50 like everyone (v1 locked); the discount is
        // the only concession. SIMPLIFICATION (logged): "adjacent" halls
        // widened to any hall outside the collapsing kingdom — there is no
        // kingdom-adjacency table, and roads carry the desperate far.
        var campaign = SaveManager.ActiveSave?.Cycle?.Campaign;
        var kingdoms = SaveManager.ActiveSave?.Cycle?.Kingdoms;
        if (campaign != null && kingdoms != null && rng.RandiRange(1, 100) <= 40)
        {
            bool collapseElsewhere = false;
            foreach (var kv in kingdoms)
            {
                if (kv.Key == city.KingdomId || string.IsNullOrEmpty(kv.Value.TemplateRegionId))
                    continue;
                if (campaign.GetCorruption(kv.Value.TemplateRegionId) >= 2)
                {
                    collapseElsewhere = true;
                    break;
                }
            }
            if (collapseElsewhere)
            {
                var refugee = CandidateGenerator.Generate(
                    rng, 0, hall.CityId, lunation, 99);
                refugee.RecruitmentCost = refugee.RecruitmentCost * 60 / 100;
                refugee.Backstory = "Displaced by the corruption's spread — what they " +
                                    "carried is gone; what they know is for hire, cheap.";
                hall.Candidates.Add(refugee);
            }
        }

        // Authored drop-in: found people must stay findable (§2 — the
        // storefront dies, the people don't), and the FIRST companion must be
        // reachable deterministically (the bootstrap ruling above). Every
        // hall carries one while any IsAvailable authored companion remains
        // unoffered — walk into any city, hire a person, start the politics
        // game. Never the same person advertised in two halls at once.
        var save = SaveManager.ActiveSave;
        if (save != null && AuthoredGuaranteed)
        {
            var offered = cycle.HiringHalls
                .Where(h => h != hall)
                .SelectMany(h => h.Candidates)
                .Select(c => c.Id)
                .ToHashSet();

            var pool = save.Companions
                .Where(c => c.IsAvailable && !c.IsRecruited && !c.IsPermadead
                            && !offered.Contains(c.Id))
                .ToList();

            if (pool.Count > 0)
            {
                var authored = pool[rng.RandiRange(0, pool.Count - 1)];
                // Reference copy is fine: the hall entry is display-only for
                // authored people; hiring routes through the ROSTER record.
                hall.Candidates.Add(authored);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Pricing
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Final price after the Steward discount. Positive Steward
    /// Regard at this kingdom's court cuts the fee; a hostile Steward never
    /// RAISES it (no punitive surcharge — the court's displeasure has its
    /// own systems).</summary>
    public static int HirePrice(CycleState cycle, WorldSettlement city, Companion c)
    {
        if (c == null) return 0;
        int pct = 0;

        var court = (city != null && cycle?.Council != null &&
                     cycle.Council.Courts.TryGetValue(city.KingdomId, out var ct)) ? ct : null;
        var steward = court?.Courtiers.FirstOrDefault(x => x.Office == CourtVocab.OfficeSteward);
        if (steward != null && steward.Regard > 0)
            pct = Mathf.Min(steward.Regard * DiscountPerRegard, DiscountCapPct);

        return Mathf.Max(0, c.RecruitmentCost * (100 - pct) / 100);
    }

    // ═════════════════════════════════════════════════════════════════════
    // The hire
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Hire a candidate out of a hall. Procedural: the record moves
    /// into the roster. Authored: the roster record is recruited in place
    /// (the hall entry was a reference). Emits the R25 hiring-in-territory
    /// deed (+minor, Steward-routed). Returns the deed toast text, or null
    /// on failure (not found / can't afford).</summary>
    public static string TryHire(CycleState cycle, WorldSettlement city,
        HiringHallState hall, string candidateId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || hall == null) return null;

        var c = hall.Candidates.FirstOrDefault(x => x.Id == candidateId);
        if (c == null || c.IsPermadead) return null;

        // Same guard as the refresh prune, at the moment of purchase: never
        // charge for someone the roster already holds (recruited via an
        // encounter while this menu was open) or for the dead.
        var priorRecord = save.Companions.FirstOrDefault(x => x.Id == c.Id);
        if (priorRecord != null && (priorRecord.IsRecruited || priorRecord.IsPermadead))
        {
            hall.Candidates.Remove(c);
            return null;
        }

        int price = HirePrice(cycle, city, c);
        if (save.Gold < price) return null;

        save.Gold -= price;
        hall.Candidates.Remove(c);

        var rosterRecord = save.Companions.FirstOrDefault(x => x.Id == c.Id);
        if (rosterRecord != null)
        {
            // Authored companion offered through the hall — recruit the
            // roster's own record; the hall entry was the same object or a
            // stale twin either way.
            rosterRecord.IsAvailable = true;
            rosterRecord.IsRecruited = true;
        }
        else
        {
            c.IsRecruited = true;
            c.IsAvailable = true;
            save.Companions.Add(c);
        }

        string toast = CouncilEcho.EmitDeed(cycle, city?.KingdomId,
            CouncilEcho.HireGiven, positive: true, isMajor: false);

        SaveManager.Save();
        GD.Print($"[HiringHall] Hired {c.Name} for {price}g at {hall.CityId}.");
        return toast ?? $"{c.Name} joins the guild.";
    }

    /// <summary>Stable 64-bit FNV-1a — string.GetHashCode is not stable
    /// across .NET processes; seeds derived from ids must be.</summary>
    private static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Round-trip assertion (save-adjacent discipline)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Serialize → deserialize → field-compare one populated
    /// HiringHallState. Call once from a debug entry after the first build;
    /// prints PASS/FAIL. This is the guard against the FactionId failure
    /// mode (shipped, looked correct, silently empty on load).</summary>
    public static void RoundTripAssert()
    {
        var rng = new RandomNumberGenerator { Seed = 12345 };
        var src = new HiringHallState
        {
            CityId = "k_test:10,20",
            LastRefreshLunation = 7,
        };
        src.Candidates.Add(CandidateGenerator.Generate(rng, 1, src.CityId, 7, 0));

        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            IncludeFields = true,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(src, opts);
        var back = System.Text.Json.JsonSerializer.Deserialize<HiringHallState>(json, opts);

        bool ok = back != null
            && back.CityId == src.CityId
            && back.LastRefreshLunation == src.LastRefreshLunation
            && back.Candidates.Count == 1
            && back.Candidates[0].Id == src.Candidates[0].Id
            && back.Candidates[0].Name == src.Candidates[0].Name
            && back.Candidates[0].RecruitmentCost == src.Candidates[0].RecruitmentCost
            && back.Candidates[0].TrainedStanceIds.SequenceEqual(src.Candidates[0].TrainedStanceIds)
            && back.Candidates[0].BaseHP == src.Candidates[0].BaseHP;

        GD.Print(ok
            ? "[HiringHall] RoundTripAssert PASS"
            : $"[HiringHall] RoundTripAssert FAIL — json was: {json}");
    }
}
