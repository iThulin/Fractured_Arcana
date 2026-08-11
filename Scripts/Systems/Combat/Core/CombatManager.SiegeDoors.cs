using Godot;

// ============================================================
// CombatManager.SiegeDoors.cs  (partial)
//
// Purpose:        The gate DOOR — a destructible structure unit
//                 spawned on the siege recipe's gate-gap tiles in
//                 defense fights. No open/close verb exists anywhere:
//                 the door blocks by OCCUPANCY (existing rules), takes
//                 damage from existing attacks, and enemy targeting
//                 needs no changes because a team-0 unit in the
//                 doorway IS the nearest player unit
//                 (FindNearestPlayerUnit). Sally-port open/close via a
//                 winch tile is a possible later pass — deliberately
//                 not built (no card-verb integration exists for it).
// Layer:          Combat / runtime
// Collaborators:  Data/Units/gate_door.json (def), SpawnRegistryUnit
//                 (spawner — the Necromancer-risen team-0 path),
//                 Unit.IsStructure (exclusion flag),
//                 HexGridManager.SiegeGateGap + MapRecipe.SiegeSpec
// ============================================================

public partial class CombatManager
{
    private const string GateDoorUnitId = "gate_door";

    /// <summary>Spawns one door panel per gate-gap tile. Defense fights only —
    /// when attacking, the door belongs to the OTHER side, and enemy-team
    /// structures need AI exclusions this build doesn't have yet (attackers
    /// currently find the gap open; fiction: the breach was forced before the
    /// fight). No-op unless the active recipe is a defending siege.</summary>
    private void SpawnGateDoors()
    {
        var siege = grid?.ActiveSiege;
        if (siege == null || !siege.Defending || siege.GateGap.Count == 0)
            return;
        if (siege.Entry != "gate")
            return;   // a BREACH has no doors — the opening is rubble-choked

        int spawned = 0;
        foreach (var coord in siege.GateGap)
        {
            var tile = grid.GetTile(coord);
            if (tile == null || tile.IsBlocked || !tile.IsWalkable)
                continue;
            if (tile.IsOccupied)
            {
                // The spawn-zone flood treats the doorway as impassable, so
                // nothing should ever stand here at door time. If this fires,
                // a placer leaked through the gap again — fix THAT, the door
                // spanning the full gate is a design requirement.
                GD.PrintErr($"[SiegeDoors] gap tile {coord} occupied at door spawn — " +
                            "a spawn placer leaked through the doorway.");
                continue;
            }

            var door = SpawnRegistryUnit(GateDoorUnitId, tile, teamId: 0);
            if (door == null)
                continue;
            door.IsStructure = true;
            door.IsPlayerControlled = false;   // commandable by nobody
            ApplyDoorPanelVisual(door, coord, siege);
            spawned++;
        }

        if (spawned > 0)
            GD.Print($"[SiegeDoors] {spawned} gate door panel(s) barred. " +
                     "They do not fight back; they simply refuse.");
    }

    /// <summary>Swaps the unit capsule for a tall thin door slab, rotated so
    /// its wide face spans the doorway (long axis toward the ADJACENT gap
    /// tile — the gap is contiguous by compiler assert, so every panel has a
    /// neighbor to align to; an isolated panel keeps default orientation).
    /// Only the BODY mesh rotates — the unit node, labels, and HP bar keep
    /// their orientation. Placeholder; real door model in the art pass.</summary>
    private void ApplyDoorPanelVisual(Unit door, Vector2I coord, SiegeSpec siege)
    {
        var body = door.GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (body == null)
            return;

        body.Mesh = new BoxMesh { Size = new Vector3(1.7f, 2.6f, 0.45f) };
        body.Position = new Vector3(0f, 1.3f, 0f);   // slab base at the feet;
        // shorter than the 3.2 wall — a door fills the arch, not the parapet

        foreach (var g in siege.GateGap)
        {
            if (g == coord || grid.Distance(coord, g) != 1)
                continue;
            var dir = grid.AxialToWorld(g) - grid.AxialToWorld(coord);
            if (dir.LengthSquared() < 0.001f)
                break;
            body.Rotation = new Vector3(0f, Mathf.Atan2(-dir.Z, dir.X), 0f);
            break;
        }
    }
}
