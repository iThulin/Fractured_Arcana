using Godot;

// ============================================================
// CastleMarch.cs
//
// Purpose:        The castle's SECOND movement mode: a strategic-scale
//                 relocation ordered from the world map, ruled 2026-09-21.
//
//                 Deliberately NOT a replacement for Stride Orders. The two
//                 modes have different characters and the player picks by
//                 what they want:
//
//                   MARCH   abstract. A flat fuel cost per tile, no terrain
//                           table, no patrols, no encounters, no window. The
//                           castle relocates and makes camp. This is how you
//                           reposition when you already know where you want
//                           to be.
//                   STRIDE  tactile. Real per-edge terrain cost, momentum,
//                           weather, patrol interception, fog revealed tile
//                           by tile. This is how you EXPLORE.
//
//                 The flat cost is the point, not a shortcut. A strategic
//                 move that priced every edge would just be a stride the
//                 player cannot watch, and the terrain economy is the thing
//                 that makes stride worth having.
//
//                 A march always ends parked, with a one lunation camp to
//                 re-establish. So repositioning is never free: it costs
//                 fuel, a lunation, and an exposed window at the far end.
//                 That is what stops the castle being walked across the
//                 continent for nothing.
// Layer:          System (strategic)
// Collaborators:  CycleState (CastleFuel, CastleX/Y, parking),
//                 ExpeditionAnchors.ParkCastleAt (the arrival),
//                 WorldData (bounds, terrain, discovery),
//                 CastleThreats (the camp at the far end is a target)
// See:            docs/session_log_2026-09-21_expedition_v2_step1_data_layer.md
// ============================================================

/// <summary>Orders the fortress to relocate across the world map.</summary>
public static class CastleMarch
{
    /// <summary>Fuel per world tile of straight-line distance. Flat by design: a
    /// march does not read the terrain table, which is exactly what separates it
    /// from a stride.
    ///
    /// <para>WAS 2, on my assumption that this was "roughly the average step
    /// cost". It was not, and the assumption was never checked against the world
    /// the generator actually builds. WorldGenerator seeds the bootstrap outposts
    /// by ACTUAL walked step cost: Frontier at 18 to 24, Distant at 28 to 34,
    /// against a 40 to 45 tank. A recorded world put them at hex distance 16
    /// (walked 19) and 21 (walked 34), so the real cost is 1.19 and 1.62 fuel per
    /// hex of straight-line distance. Charging 2 made the march cost 32 to reach
    /// somewhere worth 19 to walk to, and 42 for one worth 34. The ABSTRACT
    /// option was dearer than the detailed one, which inverts the entire trade,
    /// and the Distant outpost was simply unreachable on a full tank.</para>
    ///
    /// <para>At 1 the march is cheaper than striding the same ground (16 against
    /// 19, 21 against 34) and a full tank reaches 40 to 45 tiles. That is the
    /// trade it is meant to be: you pay less fuel and give up the exploring, the
    /// encounters and the ability to act on anything you pass.</para></summary>
    public const int FuelPerTile = 1;

    /// <summary>Lunations of camp owed on arrival. One, always: the castle has
    /// only to unpack, not to rebuild.</summary>
    public const int ArrivalCampLunations = 1;

    /// <summary>Radius charted along the route. The lens sees while the castle
    /// walks, but a march is not a survey: Charted only, never Explored, so a
    /// marched corridor still hides its POIs and the exploration game survives
    /// (single_world_refactor_v2 section 5).</summary>
    public const int ChartRadius = 2;

    /// <summary>How far the castle could march on the fuel it has.</summary>
    public static int RangeInTiles(CycleState cycle)
    {
        if (cycle == null || FuelPerTile <= 0)
        {
            return 0;
        }
        return cycle.CastleFuel / FuelPerTile;
    }

    /// <summary>Fuel a march to this tile would burn.</summary>
    public static int CostTo(CycleState cycle, int x, int y)
    {
        if (cycle?.World == null)
        {
            return 0;
        }
        return cycle.World.HexDistance(cycle.CastleX, cycle.CastleY, x, y) * FuelPerTile;
    }

    /// <summary>Can the castle march there, and if not, why not. The reason is
    /// written for the player, not the log.</summary>
    /// <summary>Can the castle march AT ALL, ignoring any destination? Split out
    /// so the UI can refuse to arm a march mode that would reject every tile, and
    /// say why once instead of on every click.</summary>
    public static bool CanMarchAtAll(CycleState cycle, out string reason)
    {
        reason = "";
        if (cycle?.World == null)
        {
            reason = "There is no world to march across.";
            return false;
        }
        if (!cycle.CastleParked)
        {
            reason = "The castle is out on a sortie. Recall or make camp first.";
            return false;
        }
        if (cycle.CastleRepairLunations > 0)
        {
            reason = $"The crews are still at work: {cycle.CastleRepairLunations} lunation(s) of resupply left.";
            return false;
        }
        if (!string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
        {
            reason = "There are soldiers in the camp. Answer them before you move.";
            return false;
        }
        if (cycle.CastleFuel < FuelPerTile)
        {
            reason = $"The furnace holds {cycle.CastleFuel} fuel, and a single tile costs {FuelPerTile}.";
            return false;
        }
        return true;
    }

    public static bool CanMarchTo(CycleState cycle, int x, int y, out string reason)
    {
        if (!CanMarchAtAll(cycle, out reason))
        {
            return false;
        }
        if (!cycle.World.InBounds(x, y))
        {
            reason = "That is off the map.";
            return false;
        }
        if (x == cycle.CastleX && y == cycle.CastleY)
        {
            reason = "The castle is already standing there.";
            return false;
        }

        var dest = cycle.World.GetTile(x, y);
        if (dest.IsWater)
        {
            reason = "The castle does not swim.";
            return false;
        }
        if (dest.Discovery == TileDiscovery.Unseen)
        {
            reason = "The lens cannot command ground it has never seen. Stride into it first.";
            return false;
        }

        int cost = CostTo(cycle, x, y);
        if (cost > cycle.CastleFuel)
        {
            reason = $"Not enough fuel: the march needs {cost} and the furnace holds " +
                     $"{cycle.CastleFuel}, which reaches {RangeInTiles(cycle)} tile(s).";
            return false;
        }
        return true;
    }

    /// <summary>Execute the march. Spends the fuel, charts the corridor, moves
    /// the castle and makes camp at the far end. Returns a line for the player,
    /// or empty when the march was refused.
    ///
    /// <para>Does not advance the calendar. The arrival camp IS the time cost:
    /// the castle cannot sortie until the crews unpack, and it sits exposed
    /// while they do. Charging a lunation on top would price the same beat
    /// twice.</para></summary>
    public static string Execute(CycleState cycle, int x, int y, string castleName)
    {
        if (!CanMarchTo(cycle, x, y, out string refusal))
        {
            GD.Print($"[CastleMarch] refused: {refusal}");
            return "";
        }

        int cost = CostTo(cycle, x, y);
        int fromX = cycle.CastleX, fromY = cycle.CastleY;
        int tiles = cycle.World.HexDistance(fromX, fromY, x, y);

        ChartCorridor(cycle.World, fromX, fromY, x, y);

        cycle.CastleFuel -= cost;
        if (cycle.CastleFuel < 0)
        {
            cycle.CastleFuel = 0;
        }

        // Arriving IS parking: the castle makes camp wherever the march ends,
        // which is what keeps a reposition honest about its cost.
        ExpeditionAnchors.ParkCastleAt(cycle, x, y, castleName, ArrivalCampLunations);

        GD.Print($"[CastleMarch] {castleName} marches {tiles} tile(s) to ({x},{y}) for {cost} fuel; " +
                 $"{cycle.CastleFuel} left.");
        return $"{castleName} marches {tiles} tile(s) and makes camp. " +
               $"Fuel {cycle.CastleFuel}/{cycle.CastleMaxFuel}. " +
               $"The crews need {ArrivalCampLunations} lunation(s) to unpack, and the camp is exposed until they do.";
    }

    /// <summary>Write Charted along the route. Sampled by interpolating axial
    /// coordinates rather than walking a true hex line: this paints a fog
    /// corridor, not a path, and being a tile out at the edges of it changes
    /// nothing a player can perceive. Never downgrades Explored ground.</summary>
    private static void ChartCorridor(WorldData world, int fromX, int fromY, int toX, int toY)
    {
        int steps = world.HexDistance(fromX, fromY, toX, toY);
        if (steps <= 0)
        {
            return;
        }

        var (aq, ar) = HexCoord.OffsetToAxial(fromX, fromY);
        var (bq, br) = HexCoord.OffsetToAxial(toX, toY);

        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            int q = Mathf.RoundToInt(Mathf.Lerp(aq, bq, t));
            int r = Mathf.RoundToInt(Mathf.Lerp(ar, br, t));
            var (col, row) = HexCoord.AxialToOffset(q, r);

            foreach (var (cx, cy) in world.Disc(col, row, ChartRadius))
            {
                if (!world.TryIndex(cx, cy, out int idx))
                {
                    continue;
                }
                if (world.Tiles[idx].Discovery == TileDiscovery.Unseen)
                {
                    world.Tiles[idx].Discovery = TileDiscovery.Charted;
                }
            }
        }
    }
}
