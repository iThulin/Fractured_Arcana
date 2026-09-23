using Godot;

// ============================================================
// FieldMarch.cs
//
// Purpose:        The field party's movement across the WORLD MAP.
//                 Ruled 2026-09-22 (Magos): the party is its own piece
//                 with its own marker, commanded independently of the
//                 castle, and it does everything the single expedition
//                 token used to do.
//
//                 This REPLACES the abstract phase journey that shipped
//                 on 2026-09-21. That model gave the party no marker, no
//                 visible position between anchors, and destinations
//                 restricted to the anchor list. It was a travel
//                 ACCOUNTING system, not a piece on a board, and it made
//                 the party something the player configured rather than
//                 something they moved.
//
//                 Two ways to cover ground, and the difference is the
//                 whole strategic texture:
//
//                   MARCH     walks, at TilesPerLunation a turn of the
//                             moon, anywhere charted. The marker moves,
//                             the lens charts a thin corridor as they go,
//                             and FieldThreats can catch them on the road.
//                   TELEPORT  instant, but only to a WAYSTONE: the parked
//                             castle, a built waypoint, or the guild dock.
//                             This is what the castle's mobile node is
//                             FOR, and it is why parking the fortress
//                             somewhere useful is a real decision rather
//                             than a place to leave it.
//
//                 Contrast with CastleMarch, deliberately: the castle
//                 relocates INSTANTLY and pays with fuel and an exposed
//                 camp at the far end. It is a machine that packs up and
//                 redeploys. The party walks, pays with time, and can be
//                 intercepted. Neither is the other with different
//                 numbers.
// Layer:          System (strategic)
// Collaborators:  CycleState, FieldParty (X/Y, DestX/DestY, State,
//                 TravelPhasesRemaining as the tile counter),
//                 ExpeditionAnchors (anchors, the castle park),
//                 FieldThreats (the road is watched),
//                 StrategicView.RunLunationTick (the caller)
// See:            docs/session_log_2026-09-21_expedition_v2_step1_data_layer.md
// ============================================================

/// <summary>Moves the field party across the world map.</summary>
public static class FieldMarch
{
    /// <summary>World tiles the party covers per turn of the moon.
    ///
    /// <para>Sized against the castle so the two pieces feel different rather
    /// than ranked. A full castle tank marches 40 to 45 tiles in one order and
    /// then owes a camp; the party covers 8 a lunation and owes nothing, so it
    /// is slower over a long haul and more responsive over a short one. Crossing
    /// the continent on foot is meant to be a bad idea, which is what makes the
    /// castle's node worth having.</para></summary>
    public const int TilesPerLunation = 8;

    /// <summary>Radius charted along the party's route. ONE, against the
    /// castle's two: a handful of people on foot see less than a fortress with a
    /// lens on top, and keeping survey value on the castle is what stops the
    /// party being a strictly better scout that also works sites.</summary>
    public const int ChartRadius = 1;

    // ── Marching ─────────────────────────────────────────────────────────

    /// <summary>Can the party be given a move order at all?</summary>
    public static bool CanOrderAtAll(CycleState cycle, FieldParty party, out string reason)
    {
        reason = "";
        if (cycle?.World == null || party == null)
        {
            reason = "There is no party to command.";
            return false;
        }
        if (party.State == FieldPartyState.Travelling)
        {
            reason = $"The party is already on the road, {party.TravelPhasesRemaining} tile(s) out.";
            return false;
        }
        if (party.X < 0 || party.Y < 0)
        {
            reason = "The party has not taken the field yet.";
            return false;
        }
        return true;
    }

    /// <summary>Can the party march to this tile, and if not, why not.</summary>
    public static bool CanMarchTo(CycleState cycle, FieldParty party, int x, int y, out string reason)
    {
        if (!CanOrderAtAll(cycle, party, out reason))
        {
            return false;
        }
        if (!cycle.World.InBounds(x, y))
        {
            reason = "That is off the map.";
            return false;
        }
        if (x == party.X && y == party.Y)
        {
            reason = "The party is already standing there.";
            return false;
        }

        var dest = cycle.World.GetTile(x, y);
        if (dest.IsWater)
        {
            reason = "They cannot cross open water on foot.";
            return false;
        }
        if (dest.Discovery == TileDiscovery.Unseen)
        {
            // The castle's job, stated as the refusal so the rule teaches itself
            // at the moment the player runs into it.
            reason = "Nobody has charted that ground. Send the castle to scout it first.";
            return false;
        }
        return true;
    }

    /// <summary>Lunations a march to this tile would take.</summary>
    public static int LunationsTo(CycleState cycle, FieldParty party, int x, int y)
    {
        if (cycle?.World == null || party == null || TilesPerLunation <= 0)
        {
            return 0;
        }
        int dist = cycle.World.HexDistance(party.X, party.Y, x, y);
        if (dist <= 0)
        {
            return 0;
        }
        return (dist + TilesPerLunation - 1) / TilesPerLunation;
    }

    /// <summary>Order the march. Does NOT move anybody: the party steps off on
    /// the next turn of the moon, which is what makes the marker something the
    /// player watches cross the map rather than a value that changes.
    ///
    /// <para>Departing the castle's park loads the hold, exactly as the abstract
    /// journey did. That ruling is unchanged; only the movement under it is.</para>
    ///
    /// <para>Returns a line for the report, or null if refused.</para></summary>
    public static string Order(CycleState cycle, FieldParty party, int x, int y)
    {
        if (!CanMarchTo(cycle, party, x, y, out _))
        {
            return null;
        }

        string carried = "";
        bool leavingCastlePark = cycle.CastleParked
                                 && party.X == cycle.CastleX && party.Y == cycle.CastleY;
        if (leavingCastlePark)
        {
            var hold = cycle.CastleHold;
            if (hold != null && !hold.IsEmpty)
            {
                party.Carrying ??= new CastleHold();
                party.Carrying.Add(hold.Gold, hold.Splinters, hold.Materials, hold.Supplies);
                carried = $" They carry the hold: {hold}.";
                hold.Clear();
            }
        }

        int dist = cycle.World.HexDistance(party.X, party.Y, x, y);
        party.State = FieldPartyState.Travelling;
        party.DestX = x;
        party.DestY = y;
        party.DestinationAnchorKey = ExpeditionAnchors.StagingKeyOf(x, y);

        // Tiles, not phases. The field is the generic "distance still owed"
        // counter and it is read that way by FieldThreats, whose per-unit risk
        // means the same thing either way: a longer road is a riskier one.
        party.TravelPhasesRemaining = dist;

        string line = $"{party.Name} sets out for ({x},{y}): {dist} tile(s), "
                    + $"about {LunationsTo(cycle, party, x, y)} lunation(s).{carried}";
        GD.Print($"[FieldMarch] {line}");
        return line;
    }

    /// <summary>Advance every marching party by one lunation. Called from the
    /// world tick AFTER FieldThreats has rolled, so a party turned back on the
    /// road does not also take a step along it.
    ///
    /// <para>Steps along the straight line toward the destination and charts a
    /// thin corridor behind them. Returns a line for the report, or null.</para></summary>
    public static string Tick(CycleState cycle)
    {
        if (cycle?.FieldParties == null || cycle.World == null)
        {
            return null;
        }

        string report = null;
        foreach (var party in cycle.FieldParties)
        {
            if (party == null || party.State != FieldPartyState.Travelling)
            {
                continue;
            }
            if (party.DestX < 0 || party.DestY < 0)
            {
                // A destination that went missing is a stop, not a crash.
                party.State = FieldPartyState.AtAnchor;
                party.TravelPhasesRemaining = 0;
                continue;
            }

            int remaining = cycle.World.HexDistance(party.X, party.Y, party.DestX, party.DestY);
            int step = remaining <= TilesPerLunation ? remaining : TilesPerLunation;
            int fromX = party.X, fromY = party.Y;

            var (nx, ny) = StepToward(cycle.World, party.X, party.Y, party.DestX, party.DestY, step);
            party.X = nx;
            party.Y = ny;
            ChartCorridor(cycle.World, fromX, fromY, nx, ny);

            remaining = cycle.World.HexDistance(party.X, party.Y, party.DestX, party.DestY);
            party.TravelPhasesRemaining = remaining;

            if (remaining > 0)
            {
                continue;
            }

            Arrive(cycle, party, ref report);
        }
        return report;
    }

    /// <summary>Land the party and settle what arriving means: the anchor they
    /// are standing on, and the hold if there is anywhere here to bank it.</summary>
    private static void Arrive(CycleState cycle, FieldParty party, ref string report)
    {
        party.State = FieldPartyState.AtAnchor;
        party.TravelPhasesRemaining = 0;
        party.DestX = -1;
        party.DestY = -1;
        party.DestinationAnchorKey = "";

        var here = ExpeditionAnchors.FindAt(cycle, party.X, party.Y);
        party.AnchorKey = here != null ? here.Key : ExpeditionAnchors.StagingKeyOf(party.X, party.Y);

        string where = here != null && !string.IsNullOrEmpty(here.Name)
            ? here.Name
            : $"({party.X},{party.Y})";
        string line = $"{party.Name} reaches {where}.";
        line += BankHere(cycle, party, here);

        GD.Print($"[FieldMarch] {line}");
        report = report == null ? line : report + " " + line;
    }

    /// <summary>Bank what the party is carrying, if this is somewhere with
    /// people and a counting house. A shard zone and a bare tile are neither, so
    /// the hold rides on and the report says so rather than silently keeping it.</summary>
    public static string BankHere(CycleState cycle, FieldParty party, AnchorRef here)
    {
        var carrying = party.Carrying;
        if (carrying == null || carrying.IsEmpty)
        {
            return "";
        }
        if (here == null || here.Kind != AnchorKind.Staging)
        {
            return $" They still carry {carrying}, with nowhere here to bank it.";
        }

        cycle.Gold += carrying.Gold;
        cycle.ArcaneSplinters += carrying.Splinters;
        cycle.BuildMaterials += carrying.Materials;
        cycle.Supplies += carrying.Supplies;
        string line = $" The hold is banked: {carrying}.";
        carrying.Clear();
        return line;
    }

    // ── Teleporting ──────────────────────────────────────────────────────

    /// <summary>Is this tile a WAYSTONE the party can step to instantly?
    ///
    /// <para>Deliberately narrow. The parked castle is the mobile node the whole
    /// redesign is built around; a built waypoint is a waystone the guild paid
    /// materials to raise; the dock is home. Cities and secured outposts are
    /// places you WALK to, because if every anchor were a teleport target the
    /// map would have no distance in it and the castle's node would be one
    /// convenience among many instead of the reason to move the fortress.</para></summary>
    public static bool IsWaystone(CycleState cycle, int x, int y, out string what)
    {
        what = "";
        if (cycle?.World == null)
        {
            return false;
        }

        if (cycle.CastleParked && cycle.CastleRepairLunations <= 0
            && cycle.CastleX == x && cycle.CastleY == y)
        {
            what = "the castle";
            return true;
        }

        if (cycle.Waypoints != null)
        {
            foreach (var w in cycle.Waypoints)
            {
                if (w != null && !w.IsSpent && w.X == x && w.Y == y)
                {
                    what = "a waystone";
                    return true;
                }
            }
        }

        if (cycle.World.StagingPoints != null)
        {
            foreach (var sp in cycle.World.StagingPoints)
            {
                if (sp != null && sp.Source == "Start" && sp.X == x && sp.Y == y)
                {
                    what = "the guild dock";
                    return true;
                }
            }
        }

        return false;
    }

    public static bool CanTeleportTo(CycleState cycle, FieldParty party, int x, int y, out string reason)
    {
        if (!CanOrderAtAll(cycle, party, out reason))
        {
            return false;
        }
        if (x == party.X && y == party.Y)
        {
            reason = "The party is already standing there.";
            return false;
        }
        if (!IsWaystone(cycle, x, y, out _))
        {
            reason = "There is no waystone there. The party can only step to the castle, "
                   + "a raised waystone, or the dock; anywhere else they walk.";
            return false;
        }
        if (cycle.CastleParked && cycle.CastleX == x && cycle.CastleY == y
            && cycle.CastleRepairLunations > 0)
        {
            reason = "The castle's waystone is busy with the resupply.";
            return false;
        }
        return true;
    }

    /// <summary>Step the party to a waystone. Instant and free: the cost was
    /// paid when the castle was marched there, or when the waypoint was raised.
    /// Charging again for using the thing you already bought is the kind of
    /// second toll that makes a mechanic feel like an obstacle.</summary>
    public static string Teleport(CycleState cycle, FieldParty party, int x, int y)
    {
        if (!CanTeleportTo(cycle, party, x, y, out _))
        {
            return null;
        }

        IsWaystone(cycle, x, y, out string what);
        party.X = x;
        party.Y = y;
        party.State = FieldPartyState.AtAnchor;
        party.TravelPhasesRemaining = 0;
        party.DestX = -1;
        party.DestY = -1;
        party.DestinationAnchorKey = "";

        var here = ExpeditionAnchors.FindAt(cycle, x, y);
        party.AnchorKey = here != null ? here.Key : ExpeditionAnchors.StagingKeyOf(x, y);

        string line = $"{party.Name} steps through to {what} at ({x},{y}).";
        line += BankHere(cycle, party, here);
        GD.Print($"[FieldMarch] {line}");
        return line;
    }

    // ── Geometry ─────────────────────────────────────────────────────────

    /// <summary>The tile `steps` along the straight line from one tile toward
    /// another, in axial space. Interpolated rather than pathfound: the party
    /// walks a corridor across charted ground, and a true path would need a
    /// cost model the world map does not have.</summary>
    private static (int x, int y) StepToward(WorldData world, int fromX, int fromY,
                                             int toX, int toY, int steps)
    {
        int total = world.HexDistance(fromX, fromY, toX, toY);
        if (total <= 0 || steps <= 0)
        {
            return (fromX, fromY);
        }
        if (steps >= total)
        {
            return (toX, toY);
        }

        var (aq, ar) = HexCoord.OffsetToAxial(fromX, fromY);
        var (bq, br) = HexCoord.OffsetToAxial(toX, toY);
        float t = (float)steps / total;
        int q = Mathf.RoundToInt(Mathf.Lerp(aq, bq, t));
        int r = Mathf.RoundToInt(Mathf.Lerp(ar, br, t));
        var (col, row) = HexCoord.AxialToOffset(q, r);

        // Never stop in water or off the map: back off toward the origin until
        // the tile is one people could stand on.
        for (int back = 0; back < total && !Standable(world, col, row); back++)
        {
            float bt = (float)(steps - back - 1) / total;
            q = Mathf.RoundToInt(Mathf.Lerp(aq, bq, bt));
            r = Mathf.RoundToInt(Mathf.Lerp(ar, br, bt));
            (col, row) = HexCoord.AxialToOffset(q, r);
        }
        return Standable(world, col, row) ? (col, row) : (fromX, fromY);
    }

    private static bool Standable(WorldData world, int x, int y)
        => world.InBounds(x, y) && !world.GetTile(x, y).IsWater;

    /// <summary>Chart a thin corridor along the leg just walked. Unseen to
    /// Charted only, never Explored: walking past a place is not working it.</summary>
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
                if (world.TryIndex(cx, cy, out int idx)
                    && world.Tiles[idx].Discovery == TileDiscovery.Unseen)
                {
                    world.Tiles[idx].Discovery = TileDiscovery.Charted;
                }
            }
        }
    }
}
