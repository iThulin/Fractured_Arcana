using Godot;

// ============================================================
// CycleInitializer.cs
//
// Purpose:        Cycle-start / save-open initialization, extracted
//                 from CampusScreen (2026-08-19) so the strategic
//                 scene can be the game's hub without routing boot
//                 through the campus screen. Two idempotent verbs:
//                 EnsureSaveSeeded (roster, building list, starter
//                 armory) and EnsureCycleWorld (deterministic world
//                 generation for the active cycle). EnsureCycleWorld's
//                 old comment marked it for "a dedicated
//                 CycleInitializer"; this is that lift.
// Layer:          System (campaign lifecycle)
// Collaborators:  SaveManager.cs, CompanionRoster.cs,
//                 BuildingDatabase.cs, ItemDatabase.cs,
//                 StartScenarioLoader.cs, WorldGenerator.cs,
//                 EchoSeeder.cs, CompanionUnlocks.cs,
//                 CampusScreen.cs / StrategicView.cs (callers)
// ============================================================

/// <summary>Idempotent save/cycle initialization. Every verb is safe to call on
/// every open: seeding gates on absence, world generation gates on an already-
/// generated world. Callers: CampusScreen (campus shell) and StrategicView
/// (the hub scene's self-sufficient boot).</summary>
public static class CycleInitializer
{
    /// <summary>Seed everything a loaded save is expected to already contain: the
    /// companion roster, the building list, and the starter armory. Idempotent:
    /// starter seeding gates on an empty armory, demo grants skip existing items.</summary>
    public static void EnsureSaveSeeded()
    {
        if (SaveManager.ActiveSave == null)
            return;
        CompanionRoster.EnsureRoster(SaveManager.ActiveSave);
        BuildingDatabase.EnsureBuildings(SaveManager.ActiveSave);
        EnsureStarterItems();
    }

    private static void EnsureStarterItems()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;

        ItemDatabase.LoadAll();

        // Q2 (§7a) + Q3 (§4b) demo items: ensure the six exemplars exist even on
        // an ESTABLISHED armory, so they're equippable for verification without a
        // fresh save. Q2: trigger-bus (aegis/duelist/standard). Q3: overworld
        // traversal-resistance (wardstone/cinderweave/trailwarden). Runs before
        // the fresh-armory gate below.
        bool grantedDemo = false;
        foreach (var id in new[] { "aegis_charm", "duelists_brand", "standard_of_the_vigil",
                                   "wardstone_amulet", "cinderweave_cloak", "trailwardens_compass" })
        {
            if (save.Armory.OwnedItems.Exists(i => i.DefinitionId == id))
                continue;
            var demoDef = ItemDatabase.Get(id);
            if (demoDef != null)
            {
                save.Armory.AddItem(demoDef);
                grantedDemo = true;
            }
        }
        if (grantedDemo)
        {
            SaveManager.Save();
            GD.Print("[Armory] Q2/Q3 demo items granted (Aegis Charm, Duelist's Brand, Standard of the Vigil, Wardstone Amulet, Cinderweave Cloak, Trailwarden's Compass).");
        }

        // Only seed on a fresh armory
        if (save.Armory.OwnedItems.Count > 0)
            return;

        // Give one of each starter item
        var starterIds = new[]
        {
            "apprentices_focus", "travellers_robe", "mana_crystal",
            "stormcaller_staff", "warding_cloak", "spell_focus",
            "iron_sword", "leather_jerkin", "warriors_sigil",
            "hunters_bow", "chain_hauberk", "scouts_leathers",
        };

        foreach (var id in starterIds)
        {
            var def = ItemDatabase.Get(id);
            if (def != null)
                save.Armory.AddItem(def);
        }

        SaveManager.Save();
        GD.Print($"[Armory] Seeded {save.Armory.OwnedItems.Count} starter items.");
    }

    /// <summary>Generate the cycle's world on first entry if it doesn't exist yet.
    /// Deterministic per cycle + slot, stored in the cycle save, generated once.</summary>
    public static void EnsureCycleWorld()
    {
        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle == null)
            return;
        if (cycle.World != null && cycle.World.Tiles.Length > 0)
            return; // already generated this cycle

        // The founding scenario is guild-level (EternalLedger) and re-applied to
        // every cycle's world generation. The direct founding path sets it on the
        // ledger; the OnComplete host path leaves it null but stashes the id in
        // PlayerSession. Resolve that here and persist it onto the guild so it is
        // stable for every later cycle/load. Pre-feature saves → Standard.
        var scenario = save.Ledger?.FoundingScenario;
        if (scenario == null)
        {
            scenario = (!string.IsNullOrEmpty(PlayerSession.PendingStartScenarioId)
                            ? StartScenarioLoader.Load(PlayerSession.PendingStartScenarioId)
                            : null)
                       ?? StartScenarioLoader.Default();
            if (save.Ledger != null)
                save.Ledger.FoundingScenario = scenario;
        }

        if (cycle.WorldSeed == 0)              // 0 = "not yet rolled" sentinel
        {
            int baseSeed = scenario.Seed;
            if (baseSeed == 0)                 // scenario without a fixed seed → roll one
            {
                var rng = new RandomNumberGenerator();
                rng.Randomize();
                baseSeed = (int)rng.Randi();
            }
            // Cycle 1 uses the curated seed verbatim (the map the scenario was
            // balanced on); later timelines mix in the cycle number so each differs
            // while the founding difficulty levers stay constant.
            cycle.WorldSeed = WorldGenerator.DeriveCycleSeed(baseSeed, cycle.CycleNumber);
        }
        int seed = cycle.WorldSeed;
        var g = WorldGenerator.Generate(seed, cycle.SelectedSchool, scenario.ToWorldParams());
        cycle.World = g.World;
        cycle.Kingdoms = g.Kingdoms;
        cycle.Campaign = g.Campaign;
        cycle.Council = g.Council;
        // Stamp the founding scenario's runtime difficulty onto the timeline so the
        // combat + corruption layers can read it (consumed in milestone 2B).
        cycle.EnemyDifficultyMult = scenario.EnemyDifficultyMult;
        cycle.CorruptionSpreadMult = scenario.CorruptionSpreadMult;

        // Phase 2: resolve the campus entry dock ONCE from the home tile's terrain
        // (near water → Dock, else Skydock). Eternal campus property, never
        // recomputed once set, even as later cycles re-site the home elsewhere.
        var campusMap = save.Ledger?.CampusMap;
        if (campusMap != null && string.IsNullOrEmpty(campusMap.EntryDockType))
        {
            bool nearWater = false;
            if (g.World.InBounds(g.World.HomeX, g.World.HomeY))
            {
                var homeTile = g.World.GetTile(g.World.HomeX, g.World.HomeY);
                nearWater = homeTile.IsCoast || homeTile.IsWater;
                if (!nearWater)
                    foreach (var (nx, ny) in HexCoord.Neighbors(
                                 g.World.HomeX, g.World.HomeY, g.World.Width, g.World.Height))
                    {
                        var nt = g.World.GetTile(nx, ny);
                        if (nt.IsWater || nt.IsLake || nt.IsOcean)
                        { nearWater = true; break; }
                    }
            }
            campusMap.EntryDockType = nearWater ? "Dock" : "Skydock";
            GD.Print($"[Campus] Entry dock resolved to '{campusMap.EntryDockType}' " +
                     $"from home tile ({g.World.HomeX},{g.World.HomeY}).");
        }
        CorruptionSpread.Reset(); // new world, so drop cached adjacency + pressure
        KingdomTickSimulation.Reset(); // new world, so drop cached kingdom adjacency
        // Seed echo-eligible flags from permanent records (quest_hooks §5, step 6).
        // Runs after world generation so echo encounters can reference the new world.
        EchoSeeder.Seed(SaveManager.ActiveSave);
        // Roster rotation: which starters are present this rendering (2026-07-22).
        CompanionUnlocks.SeedCycleRotation(SaveManager.ActiveSave);
        SaveManager.Save();
        GD.Print($"[Cycle] Generated cycle {cycle.CycleNumber} world (scenario '{scenario.Id}', seed {seed}, " +
                 $"{g.Kingdoms.Count} territories, {g.World.Pois.Count} POIs, " +
                 $"{g.Council.Courts.Count} courts).");
    }
}
