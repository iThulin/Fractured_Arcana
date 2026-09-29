using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// Networks.cs
//
// Purpose:        The Courier Station's and the Undercroft's doctrines
//                 (campus_building_upgrades_design_v1 §14).
//
//                 Courier Station T3:
//                   Spy Network   +2 informant cap, and every informant (not
//                                 only watchers) charts the ground around its
//                                 court each moon. Intelligence becomes map.
//                   Trade Routes  every guild-held anchor (held supply cache,
//                                 outpost, secured site, claimed shard zone)
//                                 runs a route to the guild home. A route pays
//                                 gold (4 + 1 per tile) and materials (2 + 1
//                                 per 2 tiles) each moon; the 4 best pay. Each
//                                 route rolls a raid (3% per tile, max 45%,
//                                 seeded per moon), and a raided route pays
//                                 nothing that moon.
//                 Undercroft T3:
//                   The Silent Floor  Assassination through the Concord, and
//                                     every contract leaves a shorter shadow
//                                     (-1 Marked). Removal play; was T3 itself.
//                   Whisper Market    a held secret may be sold to a RIVAL
//                                     court instead of the Concord: gold, and
//                                     regard at the buying court, but the court
//                                     it came from grows warier (+Exposure).
// Layer:          System (campaign)
// Collaborators:  Charters (the gate), ShadowVocab / ShadowMarket / ShadowTick
//                 (caps, Silent Floor), SupplyCacheSystem, WorldData,
//                 StrategicView (the lunation tick), CampusUndercroftPanel.
// ============================================================

/// <summary>Courier Station and Undercroft doctrine rules.</summary>
public static class Networks
{
    public const string CourierId = "courier_station";
    public const string SpyNetwork = "spy_network";
    public const string TradeRoutes = "trade_routes";

    public const string UndercroftId = "undercroft";
    public const string SilentFloor = "silent_floor";
    public const string WhisperMarket = "whisper_market";

    public const int SpyNetworkInformantBonus = 2;

    // Trade Routes tuning (starting values).
    public const int RouteGoldBase = 4;
    public const int RouteMaterialsBase = 2;
    public const int RouteRaidPercentPerTile = 3;
    public const int RouteRaidPercentCap = 45;
    public const int RouteMaxPaying = 4;
    /// <summary>Tiles past which a route pays no more (it still risks more).</summary>
    public const int RouteLengthPayCap = 20;

    // Whisper Market tuning.
    public const int WhisperGoldBase = 20;
    public const int WhisperGoldPerInfluence = 10;
    public const int WhisperExposure = 2;

    public static bool SpyNetworkActive(GuildSaveData save) => Charters.IsActive(save, CourierId, SpyNetwork);

    public static bool TradeRoutesActive(GuildSaveData save) => Charters.IsActive(save, CourierId, TradeRoutes);

    public static bool SilentFloorActive(GuildSaveData save) => Charters.IsActive(save, UndercroftId, SilentFloor);

    public static bool WhisperMarketActive(GuildSaveData save) => Charters.IsActive(save, UndercroftId, WhisperMarket);

    /// <summary>The Undercroft's tier as the espionage rules read it.</summary>
    public static int UndercroftTier(GuildSaveData save)
        => save != null ? CouncilQueries.BuildingTier(save, UndercroftId) : 0;

    /// <summary>Concurrent informant cap: the Undercroft's, +2 with the Spy Network.</summary>
    public static int InformantCap(GuildSaveData save)
        => ShadowVocab.InformantCap(UndercroftTier(save)) + (SpyNetworkActive(save) ? SpyNetworkInformantBonus : 0);

    // ── Whisper Market ───────────────────────────────────────────────────

    /// <summary>Gold a secret fetches at a rival court: more for a weightier courtier.</summary>
    public static int WhisperPrice(CourtierState courtier)
        => WhisperGoldBase + WhisperGoldPerInfluence * Math.Max(0, courtier?.Influence ?? 0);

    /// <summary>Sell a held secret to a rival court. The secret is spent; the buying
    /// court's most influential courtier warms to the guild (+1 Regard); the court
    /// the secret came from grows warier (+Exposure).</summary>
    public static ShadowMarketResult SellSecretToCourt(CycleState cycle, GuildSaveData save,
        string kingdomId, string courtierId, string buyerKingdomId)
    {
        var council = cycle?.Council;
        if (council == null)
        {
            return ShadowMarketResult.Fail("No council state.");
        }
        if (!WhisperMarketActive(save))
        {
            return ShadowMarketResult.Fail("Needs the Whisper Market chartered at the Grand Hall.");
        }
        if (string.IsNullOrEmpty(buyerKingdomId) || buyerKingdomId == kingdomId)
        {
            return ShadowMarketResult.Fail("Choose a rival court to sell to.");
        }
        if (!council.Courts.TryGetValue(kingdomId, out var court)
            || !council.Courts.TryGetValue(buyerKingdomId, out var buyer))
        {
            return ShadowMarketResult.Fail("No such court.");
        }
        if (!buyer.HasContact)
        {
            return ShadowMarketResult.Fail("The guild has no contact at that court to sell through.");
        }
        var courtier = court.GetCourtier(courtierId);
        if (courtier == null || !courtier.SecretKnown)
        {
            return ShadowMarketResult.Fail("No held secret to sell there.");
        }

        int gold = WhisperPrice(courtier);
        courtier.SecretKnown = false;
        save.Gold += gold;

        CourtierState patron = null;
        foreach (var c in buyer.Courtiers)
        {
            if (c != null && (patron == null || c.Influence > patron.Influence))
            {
                patron = c;
            }
        }
        if (patron != null)
        {
            patron.Regard = Mathf.Clamp(patron.Regard + 1, -3, 3);
        }
        court.Exposure = Mathf.Clamp(court.Exposure + WhisperExposure, 0, 10);
        SaveManager.MarkDirty();

        string from = CouncilTick.CourtDisplayName(cycle, kingdomId);
        string to = CouncilTick.CourtDisplayName(cycle, buyerKingdomId);
        return ShadowMarketResult.Pass(
            $"Sold {courtier.DisplayName}'s secret to {to} for {gold} gold."
            + (patron != null ? $" {patron.DisplayName} remembers the favour." : "")
            + $" {from} grows warier (exposure {court.Exposure}).");
    }

    // ── Trade Routes ─────────────────────────────────────────────────────

    /// <summary>One route from the guild home to a held anchor.</summary>
    public sealed class Route
    {
        public string Key = "";
        public string Name = "";
        public int Tiles;
        public int Gold;
        public int Materials;
        public int RaidPercent;
    }

    /// <summary>Every route the guild runs now, best-paying first. Empty without
    /// the doctrine or a world. Only the first RouteMaxPaying pay.</summary>
    public static List<Route> Routes(CycleState cycle, GuildSaveData save)
    {
        var routes = new List<Route>();
        var world = cycle?.World;
        if (world == null || !TradeRoutesActive(save) || !world.InBounds(world.HomeX, world.HomeY))
        {
            return routes;
        }
        var seen = new HashSet<(int, int)>();

        void Add(string key, string name, int x, int y)
        {
            if (!seen.Add((x, y)) || (x == world.HomeX && y == world.HomeY))
            {
                return;
            }
            int tiles = world.HexDistance(world.HomeX, world.HomeY, x, y);
            if (tiles <= 0)
            {
                return;
            }
            int paid = Math.Min(tiles, RouteLengthPayCap);
            routes.Add(new Route
            {
                Key = key,
                Name = name,
                Tiles = tiles,
                Gold = RouteGoldBase + paid,
                Materials = RouteMaterialsBase + paid / 2,
                RaidPercent = Math.Min(RouteRaidPercentCap, RouteRaidPercentPerTile * tiles),
            });
        }

        if (world.StagingPoints != null)
        {
            foreach (var sp in world.StagingPoints)
            {
                if (sp == null || !sp.Available)
                {
                    continue;
                }
                if (sp.Source == "Outpost" || sp.Source == "Secured" || sp.Source == "Shard")
                {
                    Add($"sp:{sp.X},{sp.Y}", string.IsNullOrEmpty(sp.Name) ? "a guild outpost" : sp.Name, sp.X, sp.Y);
                }
            }
        }
        if (world.Pois != null)
        {
            foreach (var poi in world.Pois)
            {
                if (poi != null && poi.Kind == PoiKind.SupplyCache
                    && SupplyCacheSystem.ControllerOf(poi) == SupplyCacheSystem.GuildId)
                {
                    Add($"cache:{poi.X},{poi.Y}", "the guild's supply cache", poi.X, poi.Y);
                }
            }
        }

        routes.Sort((a, b) => a.Gold != b.Gold ? b.Gold.CompareTo(a.Gold) : string.CompareOrdinal(a.Key, b.Key));
        return routes;
    }

    /// <summary>Lunation tick: the best routes pay, each after its raid roll.
    /// Returns report lines. Seeded per moon and route, so a reload cannot reroll.</summary>
    public static List<string> TickTradeRoutes(CycleState cycle, GuildSaveData save)
    {
        var lines = new List<string>();
        var routes = Routes(cycle, save);
        if (routes.Count == 0 || save == null)
        {
            return lines;
        }
        int lunation = cycle.Calendar?.CurrentLunation ?? 1;
        int gold = 0, materials = 0, paying = 0;
        var raided = new List<string>();
        for (int i = 0; i < routes.Count && i < RouteMaxPaying; i++)
        {
            var r = routes[i];
            if (RngFor(cycle, r.Key, lunation).Next(100) < r.RaidPercent)
            {
                raided.Add(r.Name);
                continue;
            }
            gold += r.Gold;
            materials += r.Materials;
            paying++;
        }
        if (gold > 0 || materials > 0)
        {
            save.Gold += gold;
            save.BuildMaterials += materials;
            lines.Add($"Trade routes: {paying} caravan(s) came home with {gold} gold and {materials} materials.");
        }
        if (raided.Count > 0)
        {
            string line = $"Raiders took the caravans on the road to {string.Join(", ", raided)}. Nothing came home from there this moon.";
            ScryInbox.Post(cycle, ScryChannel.Messenger, "Caravans raided", line, "", "trade");
            lines.Add(line);
        }
        SaveManager.MarkDirty();
        return lines;
    }

    private static Random RngFor(CycleState cycle, string salt, int lunation)
    {
        unchecked
        {
            int seed = (cycle.WorldSeed * 397) ^ (lunation * 7919);
            foreach (char ch in salt ?? "")
            {
                seed = (seed * 31) ^ ch;
            }
            return new Random(seed);
        }
    }
}
