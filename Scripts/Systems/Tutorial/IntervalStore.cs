using Godot;
using System.Collections.Generic;

// ============================================================
// IntervalStore.cs
//
// Purpose:        Chapter II of the interval, the first heading
//                 (Standing Orders O3, docs/standing_orders_o3_chapter_two_v1.md;
//                 the design is section 4.2 of
//                 docs/standing_orders_first_reading_v1.md). The
//                 Academy's field store as a real place on the route:
//                 the Register's itinerary and its lead, the looters
//                 who hold the store, and the provisions it gives when
//                 it is taken back. Every test the general systems make
//                 for the store is here, and each is a no-op unless the
//                 interval walks and the place is the store.
// Layer:          System (tutorial; static, no nodes)
// Collaborators:  Interval.cs (Begin detaches the store, EnsureStarted
//                 places it), IntervalState.cs (StoreX, StoreY,
//                 StorePoiIndex), StandingOrders.cs (the store step),
//                 LeadService.cs (the Register's lead),
//                 PrologueDirector.cs (the two panels, in
//                 Data/Encounters/prologue.json), CampSight.cs (the
//                 store's name in the camp's report),
//                 ExpeditionManager.cs (the looters pinned, the
//                 provisions on the win, the store's page, its name on
//                 its beacon), StrategicView.cs and
//                 StrategicView.Tutorial.cs (the itinerary, the Next
//                 line's ring), IntervalSaveAssert.cs (runs the assert)
// ============================================================

/// <summary>The Academy's field store, chapter II of the interval (see the file header).</summary>
public static class IntervalStore
{
    /// <summary>The store's name wherever the game says it: the camp's report, the "held true" note,
    /// and its beacon in the field (D191).</summary>
    public const string StoreName = "the Academy's field store";

    /// <summary>The itinerary is read (an Eternal flag, so a quit does not play it again).</summary>
    public const string ItineraryFlag = "interval_itinerary";

    /// <summary>The two panels, in Data/Encounters/prologue.json beside chapter I's (D189, D194).</summary>
    public const string ItineraryPanelId = "interval_itinerary";
    public const string StorePanelId = "interval_field_store";

    /// <summary>The Register's explanation of the furnace, after the itinerary (D189).</summary>
    public const string FurnaceBark = "interval.first_furnace";

    /// <summary>Relight the Refectory's recover beat: the kitchen-master's order book (U10).</summary>
    public const string KitchenOrderFlag = "kitchen_ledger_recovered";

    /// <summary>The Register's lead on the store: its source, its ring and its words (D189, D197).</summary>
    public const string LeadSource = "register";
    public const int LeadRadius = 3;
    public const string LeadLine = "The Register: the Academy's field store, the first station on the summer road.";

    /// <summary>The store's cash box, straight to the purse (D193). A starting value.</summary>
    public const int StoreCoin = 60;

    /// <summary>The looters (D192): two soldiers and a ranger from the generic roster, at 0.8. Starting
    /// values, tuned in play.</summary>
    public const string LootersId = "interval_store_looters";
    public const float LootersMult = 0.8f;
    private static readonly string[] LooterUnits = { "generic_soldier", "generic_soldier", "generic_ranger" };

    // ════════════════════════════════════════════════════════════════════════
    //  The place
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The store, while it stands at its tile: the place recorded on the interval's state, at
    /// the store's coordinates, with its tile pointing at it. Null before the route is placed, while a
    /// walk begun again has detached it, or when there is no interval.</summary>
    public static WorldPoi Store(CycleState cycle)
    {
        var iv = cycle?.Interval;
        var world = cycle?.World;
        if (iv == null || world?.Pois == null || iv.StorePoiIndex < 0 || iv.StorePoiIndex >= world.Pois.Count
            || !world.TryIndex(iv.StoreX, iv.StoreY, out int tile) || tile >= world.Tiles.Length
            || world.Tiles[tile].PoiIndex != iv.StorePoiIndex)
        {
            return null;
        }
        return world.Pois[iv.StorePoiIndex];
    }

    /// <summary>This place, in this world, is the store.</summary>
    public static bool IsStore(CycleState cycle, WorldData world, WorldPoi poi)
        => poi != null && world != null && cycle?.World == world && Store(cycle) == poi;

    /// <summary>The store stands at world tile (x, y).</summary>
    public static bool IsStoreAt(CycleState cycle, int x, int y)
    {
        var poi = Store(cycle);
        return poi != null && poi.X == x && poi.Y == y;
    }

    /// <summary>Put the store on its tile (D187): when the route is placed, and on each map load for a
    /// walk placed before the store was a place. A place of kind Combat (the looters hold it), not yet
    /// sighted, owned by the tile's kingdom. A walk begun again reuses the place its last walk had
    /// (D188), so a save never holds more than one store. Does nothing while the store stands, when
    /// there is no route, once the store step is done, or when something else holds the tile.</summary>
    public static void Place(CycleState cycle)
    {
        var iv = cycle?.Interval;
        var world = cycle?.World;
        if (iv == null || world?.Pois == null || !Interval.Walking(cycle) || Store(cycle) != null)
        {
            return;
        }
        if (!world.TryIndex(iv.StoreX, iv.StoreY, out int tile) || tile >= world.Tiles.Length)
        {
            return;   // no route, so no store
        }
        var active = SaveManager.ActiveSave;
        if (active?.Cycle == cycle && StandingOrders.Done(active, StandingOrders.Store))
        {
            return;   // already provisioned (the debug panel's Next Standing Order): nothing to take back
        }
        if (world.Tiles[tile].PoiIndex >= 0)
        {
            GD.PrintErr($"[IntervalStore] The store's tile ({iv.StoreX},{iv.StoreY}) already holds a place, "
                        + "so the store is not placed.");
            return;
        }

        int index = iv.StorePoiIndex;
        WorldPoi poi = null;
        if (index >= 0 && index < world.Pois.Count && world.Pois[index] != null && !HoldsItsTile(world, index))
        {
            poi = world.Pois[index];   // a walk begun again: its store moves to this route
        }
        if (poi == null)
        {
            poi = new WorldPoi();
            world.Pois.Add(poi);
            index = world.Pois.Count - 1;
        }
        poi.X = iv.StoreX;
        poi.Y = iv.StoreY;
        poi.Kind = PoiKind.Combat;
        poi.KingdomId = world.Tiles[tile].KingdomId ?? "";
        poi.Discovered = false;
        poi.Known = false;
        poi.Consumed = false;
        poi.GrantsStaging = false;
        poi.Harvestable = false;
        world.Tiles[tile].PoiIndex = index;
        iv.StorePoiIndex = index;
        SaveManager.MarkDirty();
        Log($"[IntervalStore] The Academy's field store stands at ({poi.X},{poi.Y}) as place {index}. The looters hold it.");
    }

    /// <summary>For Interval.Begin (D188): take the store off its tile before a new walk replaces the
    /// interval's state. The tile is freed, the place is left inert (seen and spent, so nothing sights
    /// or offers it), and the Register's lead on it goes. Returns the place's index, for the new walk to
    /// reuse, or -1 when there is no store.</summary>
    public static int Detach(CycleState cycle)
    {
        var poi = Store(cycle);
        if (poi == null)
        {
            return -1;
        }
        var world = cycle.World;
        int index = cycle.Interval.StorePoiIndex;
        if (world.TryIndex(poi.X, poi.Y, out int tile) && tile < world.Tiles.Length)
        {
            world.Tiles[tile].PoiIndex = -1;
        }
        poi.Discovered = true;
        poi.Known = true;
        poi.Consumed = true;
        cycle.Leads?.RemoveAll(l => l != null && l.PoiIndex == index && l.Source == LeadSource);
        Log($"[IntervalStore] The store (place {index}) is taken off the road for a new walk.");
        return index;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The itinerary and the Register's lead
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The itinerary plays at the map's next quiet moment (D189): the interval walks, the store
    /// is the current step and stands on its tile, and the itinerary is unread.</summary>
    public static bool ItineraryDue(GuildSaveData save)
        => save?.Ledger != null && Interval.Walking(save.Cycle) && !HasMeta(save, ItineraryFlag)
           && StandingOrders.Current(save) == StandingOrders.Store && Store(save.Cycle) != null;

    public static NarrativeEncounterData ItineraryPanel(GuildSaveData save)
        => save == null ? null : PrologueDirector.Panel(ItineraryPanelId, save);

    /// <summary>The itinerary is read: its flag is written (as its choice writes it too), and the
    /// Register's lead rings the store (LeadService.AddScripted). Hearing it again adds nothing. Returns
    /// the lead, or null when there is no store or it is already Sighted. Changes only the save: the
    /// caller saves, redraws the map and explains the furnace.</summary>
    public static Lead HearItinerary(GuildSaveData save)
    {
        if (save?.Ledger == null)
        {
            return null;
        }
        SetMeta(save, ItineraryFlag);
        var cycle = save.Cycle;
        if (Store(cycle) == null)
        {
            return null;
        }
        return LeadService.AddScripted(cycle, cycle.Interval.StorePoiIndex, LeadSource, LeadLine, LeadRadius);
    }

    /// <summary>The Register's lead on the store, while it stands; null once the store is Sighted or
    /// when there is none. The Next line points at its ring (D190).</summary>
    public static Lead StandingLead(CycleState cycle)
    {
        var poi = Store(cycle);
        if (poi == null || cycle.Leads == null)
        {
            return null;
        }
        int index = cycle.Interval.StorePoiIndex;
        foreach (var lead in cycle.Leads)
        {
            if (lead != null && lead.PoiIndex == index && lead.Source == LeadSource
                && LeadService.Stands(lead, cycle.World.Pois))
            {
                return lead;
            }
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The looters and the provisions
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The looters hold the store (D192): the interval walks, (x, y) is the store, and the store
    /// step is not yet done.</summary>
    public static bool LootersDue(GuildSaveData save, int x, int y)
        => save?.Cycle != null && Interval.Walking(save.Cycle) && IsStoreAt(save.Cycle, x, y)
           && !StandingOrders.Done(save, StandingOrders.Store);

    /// <summary>The fight the store's scout report pins at world tile (x, y), or null when it is not the
    /// store or the looters are gone.</summary>
    public static EncounterDefinition LootersAt(GuildSaveData save, int x, int y, string regionId, string terrainType)
        => LootersDue(save, x, y) ? Looters(regionId, terrainType) : null;

    /// <summary>The looters: two soldiers and a ranger at <see cref="LootersMult"/>, a Battle.</summary>
    public static EncounterDefinition Looters(string regionId, string terrainType)
    {
        var def = new EncounterDefinition
        {
            Id = LootersId,
            DisplayName = "Looters",
            Tier = EncounterTier.Battle,
            RegionId = regionId ?? "",
            TerrainType = terrainType ?? "",
            DifficultyMult = LootersMult,
        };
        foreach (string unit in LooterUnits)
        {
            string id = UnitRegistry.TryResolveId(unit, out var resolved) && !string.IsNullOrEmpty(resolved)
                ? resolved
                : unit;
            def.Enemies.Add(new EnemySlot(id, LootersMult));
        }
        return def;
    }

    /// <summary>The store is taken back (D193): the furnace is filled wherever the castle stands, the
    /// cash box goes straight to the purse, the kitchen-master's order book is recovered (U10), and the
    /// store step is ticked. A line of frontier news waits on the map. Returns the spoils card's lines.
    /// Applied in the fight's return, before the spoils card, so a quit after the fight loses nothing.</summary>
    public static List<string> Provision(GuildSaveData save)
    {
        var lines = new List<string>();
        var cycle = save?.Cycle;
        if (cycle == null || save.Ledger == null)
        {
            return lines;
        }
        if (cycle.CastleMaxFuel > 0)
        {
            cycle.CastleFuel = cycle.CastleMaxFuel;
        }
        lines.Add($"The store's wagon takes the fuel to the castle: the furnace is full ({cycle.CastleFuel}/{cycle.CastleMaxFuel}).");
        save.Gold += StoreCoin;
        lines.Add($"+{StoreCoin} gold from the store's cash box.");
        SetMeta(save, KitchenOrderFlag);
        lines.Add("The kitchen-master's order book, open at the feast's last order, never delivered.");
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.PendingSiegeReports.Add("The field store is taken back from the looters, and its wagon fills the "
                                      + $"castle's furnace ({cycle.CastleFuel}/{cycle.CastleMaxFuel}).");
        StandingOrders.Tick(save, StandingOrders.Store);
        SaveManager.MarkDirty();
        Log($"[IntervalStore] The store is taken back: the furnace {cycle.CastleFuel}/{cycle.CastleMaxFuel}, "
                 + $"+{StoreCoin} gold, the order book recovered.");
        return lines;
    }

    /// <summary>Debug (Next Standing Order, D196): the store as the chapter does it. The store is seen,
    /// known and spent, and the provisions are applied.</summary>
    public static void ProvisionByDebug(GuildSaveData save)
    {
        var poi = Store(save?.Cycle);
        if (poi != null)
        {
            poi.Discovered = true;
            poi.Known = true;
            poi.Consumed = true;
            LeadService.Prune(save.Cycle);
        }
        Provision(save);
    }

    /// <summary>The store's page, after the spoils card (D194).</summary>
    public static NarrativeEncounterData StorePanel(GuildSaveData save)
        => save == null ? null : PrologueDirector.Panel(StorePanelId, save);

    // ════════════════════════════════════════════════════════════════════════
    //  The assert
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The store's checks (docs/standing_orders_o3_chapter_two_v1.md section 5). Nothing here
    /// touches the active save's state.
    /// <list type="number">
    /// <item>Both panels are in prologue.json with one choice each, the itinerary's choice writes its
    /// flag, and the furnace's explanation is in barks.json as a once explanation.</item>
    /// <item>On scratch saves with a small world: the store is placed once, as an unsighted combat place
    /// at its tile; a walk begun again reuses it, unsighted and untaken; the itinerary is due only while
    /// it is unread, and hearing it adds the Register's lead once; the looters are due only at the store
    /// while the interval walks and before the provisions, three of them at 0.8; and the provisions fill
    /// the furnace, pay the cash box, recover the order book and tick the step.</item>
    /// </list></summary>
    public static List<string> AssertFailures()
    {
        GD.Print("[Interval Store Assert] The [Leads] and [StandingOrders] lines that follow come from scratch saves, "
                 + "not your guild.");
        _quiet = true;
        try
        {
            return Failures();
        }
        finally
        {
            _quiet = false;
        }
    }

    private static List<string> Failures()
    {
        var fails = new List<string>();

        // 1. The panels.
        var probe = new GuildSaveData();
        var itinerary = PrologueDirector.Panel(ItineraryPanelId, probe);
        if (itinerary?.Choices == null || itinerary.Choices.Count != 1)
        {
            fails.Add($"the panel {ItineraryPanelId} is missing from prologue.json, or has other than one choice");
        }
        else if (itinerary.Choices[0].SetMetaFlags == null || !itinerary.Choices[0].SetMetaFlags.Contains(ItineraryFlag))
        {
            fails.Add($"the itinerary's choice does not write {ItineraryFlag}, so it would play after every load");
        }
        var page = PrologueDirector.Panel(StorePanelId, probe);
        if (page?.Choices == null || page.Choices.Count != 1)
        {
            fails.Add($"the panel {StorePanelId} is missing from prologue.json, or has other than one choice");
        }
        var furnace = RegisterBarks.For(FurnaceBark);
        if (furnace == null || furnace.Count == 0 || !furnace[0].Once)
        {
            fails.Add($"barks.json lacks {FurnaceBark}, or it is not a once explanation");
        }

        // 2. A scratch walk.
        var save = Scratch();
        var cycle = save.Cycle;
        var world = cycle.World;
        Place(cycle);
        var store = Store(cycle);
        if (store == null)
        {
            fails.Add("placing the store made no place at its tile");
            return fails;
        }
        if (store.Kind != PoiKind.Combat || store.Discovered || store.Consumed || store.X != 12 || store.Y != 12)
        {
            fails.Add("the store was not placed as an unsighted, untaken combat place at its tile");
        }
        int count = world.Pois.Count;
        int index = cycle.Interval.StorePoiIndex;
        Place(cycle);
        if (world.Pois.Count != count)
        {
            fails.Add("placing the store again added a second place");
        }
        store.Discovered = true;
        if (Detach(cycle) != index || Store(cycle) != null || !store.Consumed)
        {
            fails.Add("detaching the store for a new walk left it standing, active, or lost its index");
        }
        Place(cycle);
        var again = Store(cycle);
        if (again != store || world.Pois.Count != count || cycle.Interval.StorePoiIndex != index
            || again.Discovered || again.Consumed)
        {
            fails.Add("a walk begun again did not reuse its store, unsighted and untaken");
        }

        // The itinerary and the Register's lead.
        if (!ItineraryDue(save))
        {
            fails.Add("the itinerary is not due at chapter II's start");
        }
        var lead = HearItinerary(save);
        if (lead == null || lead.Source != LeadSource || lead.PoiIndex != index || lead.Radius != LeadRadius)
        {
            fails.Add("hearing the itinerary did not add the Register's lead on the store");
        }
        if (ItineraryDue(save))
        {
            fails.Add("the itinerary is still due once it is read");
        }
        int leads = cycle.Leads.Count;
        HearItinerary(save);
        if (cycle.Leads.Count != leads || StandingLead(cycle) != lead)
        {
            fails.Add("hearing the itinerary twice added a second lead, or the standing lead is not the Register's");
        }

        // The looters.
        if (!LootersDue(save, 12, 12) || LootersDue(save, 13, 12))
        {
            fails.Add("the looters are not due at the store alone");
        }
        var looters = Looters("", "Grassland");
        if (looters.Enemies.Count != 3 || System.Math.Abs(looters.DifficultyMult - LootersMult) > 0.001f)
        {
            fails.Add($"the looters are {looters.Enemies.Count} at {looters.DifficultyMult}, not 3 at {LootersMult}");
        }
        cycle.Interval.Active = false;
        if (LootersDue(save, 12, 12))
        {
            fails.Add("the looters are due with the interval not walking");
        }
        cycle.Interval.Active = true;

        // The provisions.
        cycle.CastleMaxFuel = 45;
        cycle.CastleFuel = 3;
        save.Gold = 100;
        var lines = Provision(save);
        if (cycle.CastleFuel != 45 || save.Gold != 100 + StoreCoin || !HasMeta(save, KitchenOrderFlag))
        {
            fails.Add($"the provisions left the furnace at {cycle.CastleFuel}/45 and the purse at {save.Gold}, "
                      + $"not full and {100 + StoreCoin}, or the order book unrecovered");
        }
        if (!StandingOrders.Done(save, StandingOrders.Store) || LootersDue(save, 12, 12) || lines.Count != 3)
        {
            fails.Add("the provisions did not tick the store step, or left the looters due");
        }
        return fails;
    }

    /// <summary>A scratch save walking the interval over a small all-grass world, its route placed with
    /// the store at (12, 12). Never saved.</summary>
    private static GuildSaveData Scratch()
    {
        var save = new GuildSaveData();
        SetMeta(save, Interval.BegunFlag);
        var cycle = save.Cycle;
        cycle.WorldSeed = 7;
        var world = cycle.World;
        world.Width = 24;
        world.Height = 24;
        world.Tiles = new WorldTile[world.Width * world.Height];
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            world.Tiles[i].Terrain = OverworldHex.TerrainType.Grassland;
            world.Tiles[i].PoiIndex = -1;
            world.Tiles[i].SettlementIndex = -1;
            world.Tiles[i].ShardZoneIndex = -1;
        }
        cycle.Interval = new IntervalState
        {
            Active = true,
            Placed = true,
            StoreX = 12,
            StoreY = 12,
            RoadX = 12,
            RoadY = 17,
        };
        return save;
    }

    // ── Small helpers ────────────────────────────────────────────────────

    /// <summary>True while the checks run on scratch saves, so the store's own lines stay out of the log.</summary>
    private static bool _quiet;

    private static void Log(string line)
    {
        if (!_quiet)
        {
            GD.Print(line);
        }
    }

    /// <summary>The place's own tile still points at it.</summary>
    private static bool HoldsItsTile(WorldData world, int index)
    {
        var poi = world.Pois[index];
        return world.TryIndex(poi.X, poi.Y, out int tile) && tile < world.Tiles.Length
               && world.Tiles[tile].PoiIndex == index;
    }

    private static bool HasMeta(GuildSaveData save, string flag) =>
        !string.IsNullOrEmpty(flag) && (save?.Ledger?.MetaNarrativeFlags?.Contains(flag) ?? false);

    private static void SetMeta(GuildSaveData save, string flag)
    {
        if (string.IsNullOrEmpty(flag) || save?.Ledger == null)
        {
            return;
        }
        save.Ledger.MetaNarrativeFlags ??= new List<string>();
        if (!save.Ledger.MetaNarrativeFlags.Contains(flag))
        {
            save.Ledger.MetaNarrativeFlags.Add(flag);
        }
    }
}
