using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// ============================================================
// WorldDebug.cs
//
// Purpose:        Headless verification for Phase 1a. Dumps an
//                 ASCII map of a generated world (terrain,
//                 territories, the convergence, the start) and
//                 runs invariant checks (every land tile owned,
//                 co-conspirator placed, exactly one start, seat
//                 POIs present). Call from a debug hook before any
//                 rendering exists — this is how Phase 1a is
//                 confirmed.
// Layer:          System (debug)
// Collaborators:  WorldGenerator.cs, WorldData.cs, KingdomState.cs
// See:            single_world_refactor_v2.docx §8 (Phase 1a exit)
//
// Usage (from any _Ready or debug command):
//   WorldDebug.GenerateAndDump(seed: 12345, school: "Elementalist");
// ============================================================

public static class WorldDebug
{
    // When set, dump output is appended here (user://) instead of only printed.
    private static System.Text.StringBuilder _log;

    /// <summary>Generate a world and write a full diagnostic to
    /// user://world_dump.txt (no Output-panel truncation). Returns the
    /// generated data. On Mac the file lands in
    /// ~/Library/Application Support/Godot/app_userdata/&lt;Project&gt;/world_dump.txt</summary>
    public static GeneratedWorldData GenerateAndDumpToFile(int seed, string school,
                                                           WorldGenerator.Params p = null,
                                                           string path = "user://world_dump.txt")
    {
        _log = new System.Text.StringBuilder();
        var g = WorldGenerator.Generate(seed, school, p);
        DumpTerrain(g.World);
        DumpTerritories(g.World, g.Kingdoms);
        DumpKingdoms(g.Kingdoms, g.Campaign);
        RunInvariants(g);

        using (var f = FileAccess.Open(path, FileAccess.ModeFlags.Write))
        {
            if (f != null)
            {
                f.StoreString(_log.ToString());
                GD.Print($"[WorldDebug] Full dump written to {path} " +
                         $"({_log.Length} chars). Globalized: {ProjectSettings.GlobalizePath(path)}");
            }
            else
            {
                GD.PrintErr($"[WorldDebug] Could not open {path} for writing.");
            }
        }
        _log = null;
        return g;
    }

    /// <summary>Print to the Output panel AND, if a file dump is in progress,
    /// append to the log buffer.</summary>
    private static void Emit(string s)
    {
        GD.Print(s);
        _log?.Append(s).Append('\n');
    }

    /// <summary>Generate a world and print a full diagnostic. Returns the
    /// generated data so a caller can inspect further.</summary>
    public static GeneratedWorldData GenerateAndDump(int seed, string school,
                                                     WorldGenerator.Params p = null)
    {
        var g = WorldGenerator.Generate(seed, school, p);
        DumpTerrain(g.World);
        DumpTerritories(g.World, g.Kingdoms);
        DumpKingdoms(g.Kingdoms, g.Campaign);
        RunInvariants(g);
        return g;
    }

    // ── ASCII terrain map (downsampled to fit the Output panel) ──────────
    public static void DumpTerrain(WorldData w)
    {
        var sb = new StringBuilder();
        int step = DownsampleStep(w);
        sb.AppendLine($"\n=== TERRAIN  {w.Width}x{w.Height}  (1 char = {step}x{step} tiles) ===");
        for (int y = 0; y < w.Height; y += step)
        {
            for (int x = 0; x < w.Width; x += step)
                sb.Append(TerrainGlyph(w.GetTile(x, y).Terrain));
            sb.Append('\n');
        }
        Emit(sb.ToString());
    }

    // ── ASCII territory map (downsampled; each kingdom a distinct glyph) ──
    public static void DumpTerritories(WorldData w, Dictionary<string, KingdomState> kingdoms)
    {
        var glyphOf = new Dictionary<string, char>();
        char next = 'A';
        foreach (var id in kingdoms.Keys.OrderBy(k => k))
            glyphOf[id] = next++;

        int step = DownsampleStep(w);
        var sb = new StringBuilder();
        sb.AppendLine($"\n=== TERRITORIES  (1 char = {step}x{step} tiles; " +
                      "'.'=water/wild, '*'=convergence, '@'=start) ===");
        for (int y = 0; y < w.Height; y += step)
        {
            for (int x = 0; x < w.Width; x += step)
            {
                // Anything within the sample cell counts: prefer markers.
                if (CellHas(w, x, y, step, (tx, ty) => tx == w.ConvergenceX && ty == w.ConvergenceY))
                { sb.Append('*'); continue; }
                if (CellHas(w, x, y, step, (tx, ty) => w.GetTile(tx, ty).IsStagingPoint))
                { sb.Append('@'); continue; }

                var t = w.GetTile(x, y);
                if (string.IsNullOrEmpty(t.KingdomId))
                { sb.Append('.'); continue; }
                sb.Append(glyphOf.TryGetValue(t.KingdomId, out var ch) ? ch : '?');
            }
            sb.Append('\n');
        }
        foreach (var kvp in glyphOf)
            sb.AppendLine($"  {kvp.Value} = {kvp.Key}");
        Emit(sb.ToString());
    }

    /// <summary>Sample step that keeps the printed map under ~48 columns.</summary>
    private static int DownsampleStep(WorldData w)
        => Mathf.Max(1, Mathf.CeilToInt(w.Width / 48f));

    /// <summary>True if any tile in the step×step sample cell satisfies the test.</summary>
    private static bool CellHas(WorldData w, int x0, int y0, int step,
                                System.Func<int, int, bool> test)
    {
        for (int y = y0; y < y0 + step && y < w.Height; y++)
            for (int x = x0; x < x0 + step && x < w.Width; x++)
                if (test(x, y))
                    return true;
        return false;
    }

    public static void DumpKingdoms(Dictionary<string, KingdomState> kingdoms, CampaignState campaign)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\n=== KINGDOMS ===");
        sb.AppendLine($"  co-conspirator: '{campaign.CoConspirator}'");
        foreach (var kvp in kingdoms.OrderBy(k => k.Value.Tier))
        {
            var k = kvp.Value;
            int corruption = campaign.GetCorruption(k.RegionId);
            sb.AppendLine($"  {k.RegionId,-12} tier {k.Tier}  " +
                            $"faction={k.ControllingFactionId,-18} " +
                            $"archmage={(string.IsNullOrEmpty(k.ArchmageId) ? "(none)" : k.ArchmageId),-12} " +
                            $"corruption={corruption}");
        }
        Emit(sb.ToString());
    }

    // ── Invariants ───────────────────────────────────────────────────────
    public static void RunInvariants(GeneratedWorldData g)
    {
        var w = g.World;
        var fails = new List<string>();

        // 1. Every land tile is owned by some kingdom.
        int unownedLand = 0;
        for (int i = 0; i < w.Tiles.Length; i++)
        {
            var t = w.Tiles[i];
            if (t.Terrain != OverworldHex.TerrainType.Water && string.IsNullOrEmpty(t.KingdomId))
                unownedLand++;
        }
        if (unownedLand > 0)
            fails.Add($"{unownedLand} land tiles have no kingdom.");

        // 2. Exactly one staging point at start.
        int staging = w.StagingPoints.Count;
        if (staging != 1)
            fails.Add($"expected exactly 1 starting staging point, found {staging}.");

        // 3. Co-conspirator placed.
        if (string.IsNullOrEmpty(g.Campaign.CoConspirator))
            fails.Add("co-conspirator is empty.");

        // 4. Convergence set and not owned by an archmage kingdom.
        if (w.ConvergenceX < 0 || w.ConvergenceY < 0)
            fails.Add("convergence location unset.");

        // 5. Each archmage-bearing kingdom has a Seat POI.
        int seats = g.World.Pois.Count(poi => poi.Kind == PoiKind.Seat);
        int archmageKingdoms = g.Kingdoms.Values.Count(k => !string.IsNullOrEmpty(k.ArchmageId));
        if (seats != archmageKingdoms)
            fails.Add($"{archmageKingdoms} archmage kingdoms but {seats} seat POIs.");

        // 6. At least one POI pre-discovered.
        int discovered = w.Pois.Count(poi => poi.Discovered);
        if (discovered == 0)
            fails.Add("no POIs pre-discovered — first strategic view would be blank.");

        if (fails.Count == 0)
            Emit($"\n[WorldDebug] INVARIANTS PASSED " +
                 $"({w.Pois.Count} POIs, {discovered} pre-discovered, " +
                 $"{archmageKingdoms} archmage kingdoms).");
        else
        {
            Emit("\n[WorldDebug] INVARIANT FAILURES:");
            GD.PrintErr("[WorldDebug] INVARIANT FAILURES (see log):");
            foreach (var f in fails)
            {
                Emit($"  - {f}");
                GD.PrintErr($"  - {f}");
            }
        }
    }

    private static char TerrainGlyph(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Water => '~',
        OverworldHex.TerrainType.Grassland => ',',
        OverworldHex.TerrainType.Forest => 'f',
        OverworldHex.TerrainType.Swamp => 's',
        OverworldHex.TerrainType.Mountain => '^',
        OverworldHex.TerrainType.Volcanic => 'v',
        OverworldHex.TerrainType.Road => '=',
        OverworldHex.TerrainType.Ruins => 'r',
        OverworldHex.TerrainType.ArcaneGround => 'a',
        OverworldHex.TerrainType.Hills => 'n',
        OverworldHex.TerrainType.Coast => '.',
        OverworldHex.TerrainType.Lake => 'o',
        OverworldHex.TerrainType.Desert => 'D',
        OverworldHex.TerrainType.Tundra => 'T',
        OverworldHex.TerrainType.Snow => '*',
        OverworldHex.TerrainType.Marsh => '%',
        _ => '?',
    };

    // ── Founding-scenario validation (Phase 1) ───────────────────────────
    /// <summary>Generate every curated start scenario and dump its geometry so
    /// seeds / StartHints can be curated: continent style + land fraction, the
    /// start tile + its region, the Convergence tile + hex distance, the tier
    /// spread, the nearest archmage seats, and staging/POI counts. Writes to
    /// user://scenario_dump.txt (Mac: ~/Library/Application Support/Godot/
    /// app_userdata/&lt;Project&gt;/). This is the seed-curation tool from
    /// docs/start_scenarios_curation_v1.md.</summary>
    public static void DumpScenarios(string school = "Elementalist",
                                     string path = "user://scenario_dump.txt")
    {
        _log = new System.Text.StringBuilder();
        var scenarios = StartScenarioLoader.LoadAll();
        Emit($"=== START SCENARIO VALIDATION  ({scenarios.Count} scenarios, school={school}) ===");

        foreach (var s in scenarios)
        {
            var p = ParamsFor(s);
            var g = WorldGenerator.Generate(s.Seed, school, p);
            DumpOneScenario(s, g);
        }

        using (var f = FileAccess.Open(path, FileAccess.ModeFlags.Write))
        {
            if (f != null)
            {
                f.StoreString(_log.ToString());
                GD.Print($"[WorldDebug] Scenario dump written to {path} " +
                         $"({_log.Length} chars). Globalized: {ProjectSettings.GlobalizePath(path)}");
            }
            else
            {
                GD.PrintErr($"[WorldDebug] Could not open {path} for writing.");
            }
        }
        _log = null;
    }

    /// <summary>Map a StartScenario onto WorldGenerator.Params. Delegates to the
    /// single source of the mapping, <see cref="StartScenario.ToWorldParams"/>.</summary>
    public static WorldGenerator.Params ParamsFor(StartScenario s)
        => s?.ToWorldParams() ?? new WorldGenerator.Params();

    private static void DumpOneScenario(StartScenario s, GeneratedWorldData g)
    {
        var w = g.World;

        // Land fraction.
        int land = 0;
        for (int i = 0; i < w.Tiles.Length; i++)
            if (w.Tiles[i].IsLand)
                land++;
        float landFrac = w.Tiles.Length > 0 ? (float)land / w.Tiles.Length : 0f;

        // Start tile = the single Source=="Start" staging point (fallback: first).
        int sx = -1, sy = -1;
        foreach (var sp in w.StagingPoints)
            if (sp.Source == "Start") { sx = sp.X; sy = sp.Y; break; }
        if (sx < 0 && w.StagingPoints.Count > 0) { sx = w.StagingPoints[0].X; sy = w.StagingPoints[0].Y; }

        string startKingdom = (sx >= 0) ? (w.GetTile(sx, sy).KingdomId ?? "") : "";
        string startRegion = "(none)";
        int startTier = 0;
        if (!string.IsNullOrEmpty(startKingdom) && g.Kingdoms.TryGetValue(startKingdom, out var sk))
        { startRegion = sk.TemplateRegionId; startTier = sk.Tier; }

        int convDist = (sx >= 0 && w.ConvergenceX >= 0)
            ? HexCoord.OffsetDistance(sx, sy, w.ConvergenceX, w.ConvergenceY) : -1;

        // Nearest archmage seats (+ max seat distance as a ramp-denominator proxy).
        var seatList = new List<(int d, string region, int tier)>();
        int maxSeatDist = 1;
        foreach (var poi in w.Pois)
        {
            if (poi.Kind != PoiKind.Seat) continue;
            int d = (sx >= 0) ? HexCoord.OffsetDistance(sx, sy, poi.X, poi.Y) : 0;
            if (d > maxSeatDist) maxSeatDist = d;
            string reg = poi.KingdomId;
            int tier = 0;
            if (!string.IsNullOrEmpty(poi.KingdomId) && g.Kingdoms.TryGetValue(poi.KingdomId, out var ks))
            { reg = ks.TemplateRegionId; tier = ks.Tier; }
            seatList.Add((d, reg, tier));
        }
        seatList.Sort((a, b) => a.d.CompareTo(b.d));

        int t1 = 0, t2 = 0, t3 = 0;
        foreach (var k in g.Kingdoms.Values)
        {
            if (k.Tier <= 1) t1++;
            else if (k.Tier == 2) t2++;
            else t3++;
        }

        int outpostPois = 0, discovered = 0;
        foreach (var poi in w.Pois)
        {
            if (poi.Kind == PoiKind.Outpost) outpostPois++;
            if (poi.Discovered) discovered++;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"\n=== {s.Id}  ({s.DifficultyTag}, rank {s.DifficultyRank})  seed={s.Seed} ===");
        sb.AppendLine($"  continent : {w.ContinentStyle}   land={landFrac * 100f:F0}%");
        sb.AppendLine($"  start     : ({sx},{sy})  kingdom={startKingdom}  region={startRegion}  tier={startTier}");
        float convFrac = maxSeatDist > 0 ? (float)convDist / maxSeatDist : 0f;
        sb.AppendLine($"  convergence: ({w.ConvergenceX},{w.ConvergenceY})  dist={convDist}  ({convFrac:F2} of max seat dist {maxSeatDist})");
        sb.AppendLine($"  tiers     : T1={t1} T2={t2} T3={t3}");
        var near = new System.Text.StringBuilder();
        int shown = 0;
        foreach (var seat in seatList)
        {
            if (seat.d == 0) continue; // skip the start's own colocated seat
            near.Append($"{seat.region}(T{seat.tier},d{seat.d}) ");
            if (++shown >= 4) break;
        }
        sb.AppendLine($"  near seats: {near}");
        sb.AppendLine($"  staging   : {w.StagingPoints.Count} start + {outpostPois} outpost POIs;  {discovered} POIs pre-discovered");
        Emit(sb.ToString());

        RunInvariants(g);
    }
}
