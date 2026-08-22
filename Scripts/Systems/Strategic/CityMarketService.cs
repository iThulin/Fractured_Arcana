using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CityMarketService.cs
//
// Purpose:        Q4 city markets (companion_item_systems v2.1
//                 §7c): stock rolling (Common–Rare, rarity-
//                 weighted, seat cities draw one extra slot),
//                 Steward-priced purchase into the Armory.
//                 Legendaries NEVER appear — the Auction House
//                 (unbuilt) remains the only Legendary venue per
//                 guild_campus; the market must not pre-empt it.
// Layer:          System (strategic)
// Collaborators:  CityMarketState.cs (save block), ItemDatabase,
//                 CityExploreService (CityId), CityServicesHost
//                 (UI), CouncilState (Steward regard).
// Notes:          DEFERRED (logged): §7c's terrain-flavored pools
//                 (desert→Trinket, tundra→Armor) need per-kingdom
//                 terrain pool data that doesn't exist; v1 stock
//                 is rarity-weighted only. Revisit with region
//                 content work (Phase G).
// ============================================================

/// <summary>Stateless market logic over <see cref="CycleState.CityMarkets"/>.
/// Mirrors HiringHallService: lazy per-lunation refresh, stable FNV seeding,
/// persistence as truth, no re-roll on reopen.</summary>
public static class CityMarketService
{
    // ── Tuning (Q4 starting values) ──────────────────────────────────────

    /// <summary>Stock slots: town 2, ordinary city 3, seat/capital 4.</summary>
    public const int TownStock = 2;
    public const int CityStock = 3;
    public const int SeatStock = 4;

    /// <summary>Shops sell above book value; a friendly Steward talks them
    /// down toward par. Markup 125%, −5%/point of positive Steward Regard,
    /// floor 100% (never below book — merchants aren't charities).</summary>
    public const int MarkupPct = 125;
    public const int DiscountPerRegard = 5;
    public const int FloorPct = 100;

    /// <summary>Rarity weights for a stock slot (Legendary excluded — the
    /// Auction House venue rule).</summary>
    private static readonly (string rarity, int weight)[] RarityWeights =
    {
        ("Common", 45), ("Uncommon", 40), ("Rare", 15),
    };

    // ═════════════════════════════════════════════════════════════════════
    // Stock
    // ═════════════════════════════════════════════════════════════════════

    public static CityMarketState GetOrRefresh(CycleState cycle, WorldSettlement city)
    {
        if (cycle == null || city == null) return null;

        string id = CityExploreService.CityId(city);
        var market = cycle.CityMarkets.FirstOrDefault(m => m.CityId == id);
        if (market == null)
        {
            market = new CityMarketState { CityId = id };
            cycle.CityMarkets.Add(market);
        }

        int now = cycle.Calendar.CurrentLunation;
        if (market.LastRefreshLunation == now)
            return market; // this lunation's stock (possibly sold down) stands

        RollStock(city, market, now);
        market.LastRefreshLunation = now;
        SaveManager.MarkDirty();
        return market;
    }

    private static void RollStock(WorldSettlement city, CityMarketState market, int lunation)
    {
        market.StockItemIds.Clear();

        var rng = new RandomNumberGenerator();
        rng.Seed = Fnv1a(market.CityId) ^ (ulong)(lunation * 40503L + 7);

        var all = ItemDatabase.GetAll();
        if (all == null || all.Count == 0) return;

        int slots = city.IsSeat ? SeatStock
                  : city.Tier == SettlementTier.City ? CityStock : TownStock;
        int totalWeight = RarityWeights.Sum(w => w.weight);

        // Consumables (2026-08-13): every shop reliably carries sundries —
        // one guaranteed draught/scroll slot (two at seats), Common-leaning,
        // on TOP of the gear slots so potions never crowd out equipment.
        // (Note: existing saves show potions only after the NEXT lunation
        // refresh — stock is lazy-persisted per lunation by design.)
        var sundries = all.Where(d => d.IsConsumable
                                      && !market.StockItemIds.Contains(d.Id)).ToList();
        int sundrySlots = city.IsSeat ? 2 : 1;
        for (int i = 0; i < sundrySlots && sundries.Count > 0; i++)
        {
            // Common 60 / Uncommon 30 / Rare 10 within the sundry slot.
            int sr = rng.RandiRange(1, 100);
            string want = sr <= 60 ? "Common" : sr <= 90 ? "Uncommon" : "Rare";
            var band = sundries.Where(d => d.Rarity == want).ToList();
            if (band.Count == 0) band = sundries;
            var pick = band[rng.RandiRange(0, band.Count - 1)];
            market.StockItemIds.Add(pick.Id);
            sundries.Remove(pick);
        }

        for (int i = 0; i < slots; i++)
        {
            // Pick a rarity band, then a uniform item inside it. A band with
            // no unowned-eligible items falls through to the whole pool.
            int roll = rng.RandiRange(1, totalWeight);
            string rarity = null;
            foreach (var (r, w) in RarityWeights)
            {
                if (roll <= w) { rarity = r; break; }
                roll -= w;
            }

            var band = all.Where(d => d.Rarity == rarity
                                      && !market.StockItemIds.Contains(d.Id)).ToList();
            if (band.Count == 0)
                band = all.Where(d => d.Rarity != "Legendary"
                                      && !market.StockItemIds.Contains(d.Id)).ToList();
            if (band.Count == 0) break;

            market.StockItemIds.Add(band[rng.RandiRange(0, band.Count - 1)].Id);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Pricing + purchase
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Shelf price: book value at markup, Steward regard talking it
    /// down toward (never below) book. "The discount that makes befriending
    /// the money-man legible" (§7c).</summary>
    public static int Price(CycleState cycle, WorldSettlement city, ItemDefinition def)
    {
        if (def == null) return 0;
        int pct = MarkupPct;

        var court = (city != null && cycle?.Council != null &&
                     cycle.Council.Courts.TryGetValue(city.KingdomId, out var ct)) ? ct : null;
        var steward = court?.Courtiers.FirstOrDefault(x => x.Office == CourtVocab.OfficeSteward);
        if (steward != null && steward.Regard > 0)
            pct = Mathf.Max(FloorPct, pct - steward.Regard * DiscountPerRegard);

        return Mathf.Max(1, def.GoldValue * pct / 100);
    }

    /// <summary>Buy one item off the shelf into the Armory. Returns the
    /// purchase line, or null (unknown item / can't afford).</summary>
    public static string TryBuy(CycleState cycle, WorldSettlement city,
        CityMarketState market, string itemId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || market == null || !market.StockItemIds.Contains(itemId))
            return null;

        var def = ItemDatabase.Get(itemId);
        if (def == null)
        {
            market.StockItemIds.Remove(itemId); // bad id on a shelf — clear it
            return null;
        }

        int price = Price(cycle, city, def);
        if (save.Gold < price) return null;

        save.Gold -= price;
        market.StockItemIds.Remove(itemId);
        save.Armory.AddItem(def);
        SaveManager.Save();
        GD.Print($"[Market] Bought {def.Name} for {price}g at {market.CityId}.");
        return $"{def.Name} — bought for {price}g. It waits in the Armory.";
    }

    private static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }
}
