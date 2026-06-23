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
        GD.Print(sb.ToString());
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
                if (string.IsNullOrEmpty(t.KingdomId)) { sb.Append('.'); continue; }
                sb.Append(glyphOf.TryGetValue(t.KingdomId, out var ch) ? ch : '?');
            }
            sb.Append('\n');
        }
        foreach (var kvp in glyphOf)
            sb.AppendLine($"  {kvp.Value} = {kvp.Key}");
        GD.Print(sb.ToString());
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
                if (test(x, y)) return true;
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
                          $"stance={k.Stance} corruption={corruption}");
        }
        GD.Print(sb.ToString());
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
            GD.Print($"\n[WorldDebug] INVARIANTS PASSED " +
                     $"({w.Pois.Count} POIs, {discovered} pre-discovered, " +
                     $"{archmageKingdoms} archmage kingdoms).");
        else
        {
            GD.PrintErr("\n[WorldDebug] INVARIANT FAILURES:");
            foreach (var f in fails)
                GD.PrintErr($"  - {f}");
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
        _ => '?',
    };
}
