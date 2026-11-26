using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.Reach.cs
//
// Purpose:        Combat overhaul stage C2: the "reach" objective,
//                 reworked in C6 into an extraction. The marked ground
//                 is a far exit. Units leave by it one at a time with
//                 the Withdraw action. The fight is won when the leader
//                 and half the party are out, whatever is still alive.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.Objectives.cs (the zone, the round
//                   boundary, the banner, the victory latch),
//                 CombatManager.Outcome.cs (WithdrawLeader),
//                 CombatManager.cs (ConfigureAndGenerateMap sets
//                   the board up),
//                 HexGridManager.Reach.cs (board size, enemy seat,
//                   the path guarantee),
//                 CombatObjectiveDef.cs (KindReach)
// See:            docs/combat_c2_reach_and_field_board_v1.md,
//                 docs/combat_c6_extraction_v1.md
// ============================================================

public partial class CombatManager
{
    /// <summary>Size steps a reach fight adds to its board (HexGridManager.BoardGrow).
    /// The radius 5 hexagon becomes radius 8 (217 tiles). Was 2 until 2026-11-25,
    /// when the far side was being reached on turn 2 or 3.</summary>
    private const int ReachBoardGrow = 3;

    /// <summary>The barricade stands this many tiles outside the far exit's edge.</summary>
    private const int ReachBarricadeGap = 1;

    /// <summary>Every Nth piece of the barricade is a Powder Cask, the weak point.</summary>
    private const int ReachBarricadeCaskEvery = 4;

    private const string ReachBarricadeKind = "cracked_pillar";
    private const string ReachBarricadeWeakKind = "powder_cask";

    /// <summary>Where the marked ground sits when the objective names nowhere: the
    /// enemy's side of the board, with the enemy moved to the middle.</summary>
    private const string ReachDefaultAnchor = "enemy_anchor";

    private bool ReachActive =>
        _objective != null && _objective.Kind == CombatObjectiveDef.KindReach;

    /// <summary>Fills a reach objective's defaults and sets the grid up for it. Called
    /// from ConfigureAndGenerateMap before GenerateMap, for every fight, so a fight
    /// that is not a reach fight has the three knobs put back to off.</summary>
    private void ConfigureGridForReach(CombatObjectiveDef obj)
    {
        if (grid == null)
            return;

        bool reach = obj != null && obj.Kind == CombatObjectiveDef.KindReach;
        if (!reach)
        {
            grid.BoardGrow = 0;
            grid.EnemySpawnOverride = "";
            grid.MustReachToken = "";
            return;
        }

        if (string.IsNullOrEmpty(obj.ZoneAnchor) || obj.ZoneAnchor == "player_spawn")
            obj.ZoneAnchor = ReachDefaultAnchor;
        if (obj.ZoneRadius < 1)
            obj.ZoneRadius = 2;

        grid.BoardGrow = ReachBoardGrow;
        grid.EnemySpawnOverride = "midpoint";   // they hold the middle; the way out is past them
        grid.MustReachToken = obj.ZoneAnchor;
        grid.ForcedDeploymentId = "line";       // every recipe with deployments has it
        GD.Print($"[Objective] reach: marked ground at '{obj.ZoneAnchor}' radius {obj.ZoneRadius}, " +
                 $"board grown by {ReachBoardGrow}, enemy seeded at the midpoint.");
    }

    /// <summary>From ConfigureAndGenerateMap, after the map's own objects and before
    /// enemies deploy. Rings the far exit with a barricade: one object on every free
    /// tile at a fixed distance from the exit's centre, so the way in has to be
    /// broken open. Most pieces are pillars. Every fourth is a Powder Cask, which
    /// breaks to a light hit and bursts on whoever stands next to it.</summary>
    private void SpawnReachBarricade()
    {
        // The map is generated before InitObjectiveState runs, so _objective is not
        // set yet. Read the objective where ConfigureGridForReach read it, which has
        // already filled in its anchor and radius.
        var obj = EncounterContextCarrier.Current?.Objective;
        if (grid == null || obj == null || obj.Kind != CombatObjectiveDef.KindReach)
            return;
        Vector2I centre = grid.ResolveRecipeCoord(obj.ZoneAnchor);
        int ringAt = Mathf.Max(1, obj.ZoneRadius) + ReachBarricadeGap;

        var ring = new List<Vector2I>();
        foreach (var coord in grid.Tiles.Keys)
            if (grid.Distance(coord, centre) == ringAt)
                ring.Add(coord);
        // A stable order round the ring, so the casks are spread and not bunched.
        ring.Sort((a, b) =>
        {
            float aa = Mathf.Atan2(a.Y - centre.Y, a.X - centre.X);
            float bb = Mathf.Atan2(b.Y - centre.Y, b.X - centre.X);
            return aa.CompareTo(bb);
        });

        // The gate: the ring tile nearest the middle of the board, where the party
        // comes from, and the next tile round the ring. The winch opens both.
        Vector2I mid = grid.RecipeMidpoint;
        int gateIndex = -1;
        for (int i = 0; i < ring.Count; i++)
        {
            var td = grid.GetTile(ring[i]);
            if (td == null || td.IsOccupied || !td.IsWalkable || td.IsBlocked)
                continue;
            if (gateIndex < 0 || grid.Distance(ring[i], mid) < grid.Distance(ring[gateIndex], mid))
                gateIndex = i;
        }
        var gateCoords = new HashSet<Vector2I>();
        if (gateIndex >= 0)
        {
            gateCoords.Add(ring[gateIndex]);
            if (ring.Count > 1)
                gateCoords.Add(ring[(gateIndex + 1) % ring.Count]);
        }

        int placed = 0;
        int casks = 0;
        int nth = 0;
        int gatePlaced = 0;
        foreach (var coord in ring)
        {
            if (gateCoords.Contains(coord))
            {
                // The gateway is left open. Its keeper closes it (CombatManager.GateKeeper.cs).
                _reachGateTiles.Add(coord);
                gatePlaced++;
                continue;
            }
            bool weak = nth % ReachBarricadeCaskEvery == ReachBarricadeCaskEvery / 2;
            var piece = SpawnMapObject(weak ? ReachBarricadeWeakKind : ReachBarricadeKind, grid.GetTile(coord));
            if (piece == null)
                continue;
            placed++;
            nth++;
            if (weak)
                casks++;
        }

        // The winch stands inside the ring, beside the gate: the defender's side.
        _reachExitCentre = centre;
        if (gatePlaced > 0)
        {
            Vector2I gateAt = ring[gateIndex];
            // Inside the ring, against it, two or three tiles round from the gateway:
            // never beside a gateway tile, so the opening and the way in stay clear.
            var spots = new List<TileData>();
            foreach (var td in grid.Tiles.Values)
            {
                if (td == null || td.IsOccupied || !td.IsWalkable || td.IsBlocked)
                    continue;
                if (grid.Distance(td.Axial, centre) != ringAt - 1)
                    continue;
                int toGateway = int.MaxValue;
                foreach (var g in gateCoords)
                    toGateway = Mathf.Min(toGateway, grid.Distance(td.Axial, g));
                if (toGateway < 2 || toGateway > 3)
                    continue;
                spots.Add(td);
            }
            spots.Sort((a, b) => grid.Distance(a.Axial, gateAt).CompareTo(grid.Distance(b.Axial, gateAt)));
            for (int i = 0; i < spots.Count && _reachWinch == null; i++)
                _reachWinch = SpawnMapObject(WinchKind, spots[i]);
            _reachBarricadeHasWinch = _reachWinch != null;
            if (_reachWinch == null)
                GD.Print("[Objective] reach: no free tile for the Gate Winch.");
        }

        _reachBarricadePieces = placed;
        GD.Print($"[Objective] reach: barricade of {placed} piece(s), {casks} cask(s), {ringAt} tiles from '{obj.ZoneAnchor}'.");
    }

    /// <summary>Pieces the barricade was built with. Announced when the enemies
    /// are placed (CombatManager.Posts.cs), once the log is in use.</summary>
    private int _reachBarricadePieces;
    private bool _reachBarricadeHasWinch;

    /// <summary>The barricade's gateway tiles, its winch and the centre of the far
    /// exit. The gateway starts open (CombatManager.GateKeeper.cs).</summary>
    private readonly List<Vector2I> _reachGateTiles = new();
    private Unit _reachWinch;
    private Vector2I _reachExitCentre;

    /// <summary>True when <paramref name="unit"/> stands on the far exit of a reach fight.</summary>
    private bool OnFarExit(Unit unit)
    {
        if (!ReachActive || unit?.CurrentTile == null)
            return false;
        EnsureObjectiveZone();
        return _objectiveZone != null && _objectiveZone.Contains(unit.CurrentTile.Axial);
    }

    /// <summary>An enemy standing on the far exit holds it: nobody leaves by it
    /// until that enemy is killed or moved off. Set false to let units leave
    /// past it (the adjacency rule still applies).</summary>
    private static readonly bool ReachExitIsHeld = true;

    /// <summary>The first living enemy standing on the far exit, or null.</summary>
    private Unit EnemyHoldingFarExit()
    {
        if (!ReachExitIsHeld || !ReachActive)
            return null;
        EnsureObjectiveZone();
        if (_objectiveZone == null)
            return null;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || e.Stats == null || !e.Stats.IsAlive)
                continue;
            if (e.IsMapObject || e.CurrentTile == null)
                continue;
            if (_objectiveZone.Contains(e.CurrentTile.Axial))
                return e;
        }
        return null;
    }

    /// <summary>Party units that would still be on the board if
    /// <paramref name="leaving"/> left now.</summary>
    private int ReachLeftBehindCount(Unit leaving)
    {
        int n = 0;
        foreach (var u in playerUnits)
            if (u != leaving && IsPartyUnit(u))
                n++;
        return n;
    }

    /// <summary>The objective is met with units still on the board. They get out,
    /// but not cleanly: each keeps half its current health, rounded up. Run when
    /// the win is latched, before the victory tail carries health out.</summary>
    private void PenalizeLeftBehind()
    {
        foreach (var u in playerUnits)
        {
            if (!IsPartyUnit(u))
                continue;
            int kept = Mathf.Max(1, Mathf.CeilToInt(u.Stats.Health * 0.5f));
            GD.Print($"[Objective] reach: {u.Name} was left behind: {u.Stats.Health} to {kept} health.");
            combatUI?.AppendActionLog(
                $"{UnitLabel(u)} was not out by the far exit and loses half its health: {u.Stats.Health} to {kept}.");
            u.Stats.Health = kept;
            u.RefreshHealthBar();
        }
    }

    private bool WizardOnBoard()
    {
        foreach (var u in playerUnits)
            if (IsPartyUnit(u) && u.CompanionId == "wizard")
                return true;
        return false;
    }

    /// <summary>The count the objective is judged on. The party is every party unit
    /// still standing on the board plus every one that left, by either exit, so
    /// sending units back the way they came does not lower what is needed. Half
    /// of that, rounded up, must leave by the far exit. The wizard must be one of
    /// them, unless the wizard was never fielded or has fallen.</summary>
    private void CountReach(out int needed, out int through, out bool leaderOut)
    {
        int party = PartyOnBoardCount() + _leftFight.Count;
        needed = Mathf.Max(1, (party + 1) / 2);
        through = _leftThrough.Count;
        if (WizardOnBoard())
            leaderOut = false;
        else if (_leftFight.Contains("wizard"))
            leaderOut = _leftThrough.Contains("wizard");
        else
            leaderOut = true;   // no wizard in this fight, or the wizard fell
    }

    /// <summary>Called when a unit has just left the board in a reach fight. Latches
    /// the objective victory when the count is met. Returns true when it is latched.</summary>
    private bool NoteReachExit()
    {
        if (!ReachActive || _objectiveDefeat)
            return false;
        if (_objectiveVictory)
            return true;
        CountReach(out int needed, out int through, out bool leaderOut);
        GD.Print($"[Objective] reach: {through}/{needed} out by the far exit, leader out: {leaderOut}.");
        if (through < needed || !leaderOut)
            return false;
        _objectiveVictory = true;
        combatUI?.AppendActionLog("Your party is out by the far exit. The objective is met.");
        PenalizeLeftBehind();
        return true;
    }

    /// <summary>Would <paramref name="unit"/> leaving by the far exit now meet the
    /// objective? For the line above the Withdraw button.</summary>
    private bool ReachMetIfLeaves(Unit unit)
    {
        if (!ReachActive || unit == null)
            return false;
        CountReach(out int needed, out int through, out bool leaderOut);
        bool isWizard = unit.CompanionId == "wizard";
        return through + 1 >= needed && (leaderOut || isWizard);
    }

    /// <summary>The reach objective's banner line.</summary>
    private string ReachBannerLabel()
    {
        if (grid == null)
            return "Leave by the far exit (gold outline) with your leader and half your party.";
        CountReach(out int needed, out int through, out _);
        string leaderLine;
        var leader = WithdrawLeader();
        if (WizardOnBoard())
            leaderLine = $" {UnitLabel(leader)} must be one of them.";
        else if (_leftThrough.Contains("wizard"))
            leaderLine = " Your leader is out.";
        else if (_leftFight.Contains("wizard"))
            leaderLine = " Your leader left the way you came in, so this cannot be met.";
        else
            leaderLine = "";
        var holder = EnemyHoldingFarExit();
        string heldLine = holder == null
            ? ""
            : $" {UnitLabel(holder)} is standing on it: nobody can leave until it is killed or moved off.";
        return $"Leave by the far exit (gold outline): {through} / {needed} of your party out.{leaderLine}{heldLine} " +
               "The fight is won when that many are out. Units still on the board then lose half their health.";
    }
}
