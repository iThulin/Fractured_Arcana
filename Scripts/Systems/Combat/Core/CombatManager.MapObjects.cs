using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.MapObjects.cs  (partial of CombatManager)
//
// Purpose:   Battlefield E3 — neutral map objects (pillars, crystals,
//            braziers, boulders, ward stones, powder casks). They are Units
//            on a neutral "field" team (TeamId 2), spawned from the recipe's
//            map_object ops after generation, kept OUT of playerUnits/
//            enemyUnits so they never touch turn order, the unit bar, or the
//            win/loss scans. Damage reaches them via tile.Occupant; death
//            routes here from HandleUnitDeath's IsMapObject branch.
// Collaborators: MapObjectCatalog (specs), HexGridManager.PendingMapObjects
//            (placement list), TileEntryReactions (imbue), PushEffect
//            (immovable guard + brazier fire-on-push).
// See:       docs/battlefield_tactics_spec_v1.md §3
// ============================================================
public partial class CombatManager : Node3D
{
    /// <summary>Neutral field objects in play. Separate from playerUnits/enemyUnits
    /// on purpose (see file header). Pruned by HandleMapObjectDeath.</summary>
    private readonly List<Unit> fieldObjects = new();

    /// <summary>Materialise every map_object the recipe recorded during generation.
    /// Called from ConfigureAndGenerateMap right after grid.GenerateMap(), before
    /// enemies deploy — so object tiles read as occupied and spawns avoid them.</summary>
    private void SpawnMapObjects()
    {
        if (grid?.PendingMapObjects != null)
        foreach (var (coord, kind, count) in grid.PendingMapObjects)
        {
            int want = count < 1 ? 1 : count;
            int placed = 0;
            // Primary tile first, then nearest neighbours for count > 1.
            var candidates = new List<Vector2I> { coord };
            candidates.AddRange(grid.GetNeighbors(coord));
            foreach (var c in candidates)
            {
                if (placed >= want)
                    break;
                if (SpawnMapObject(kind, grid.GetTile(c)) != null)
                    placed++;
            }
            if (placed < want)
                GD.Print($"[MapObject] placed {placed}/{want} '{kind}' near {coord} (no free tiles).");
        }

        SpawnDebugMapObjects();
    }

    /// <summary>CombatDebugLauncher hook: spawn the launcher's requested test objects on
    /// the free tiles nearest the arena centre, so E3 objects can be exercised on any map.</summary>
    private void SpawnDebugMapObjects()
    {
        var wanted = PlayerSession.DebugMapObjects;
        if (wanted == null || wanted.Count == 0 || grid == null)
            return;
        var center = grid.RecipeMidpoint;
        var spots = new List<TileData>();
        foreach (var t in grid.Tiles.Values)
            if (t != null && t.IsWalkable && !t.IsBlocked && !t.IsOccupied)
                spots.Add(t);
        spots.Sort((a, b) => grid.Distance(center, a.Axial).CompareTo(grid.Distance(center, b.Axial)));
        int idx = 0;
        foreach (var kind in wanted)
        {
            Unit obj = null;
            while (idx < spots.Count && obj == null)
            {
                obj = SpawnMapObject(kind, spots[idx]);
                idx++;
            }
            if (obj == null)
                GD.Print($"[MapObject] debug '{kind}': no free tile near centre.");
        }
    }

    /// <summary>Spawn one map object on a tile. Skips occupied / blocked / non-walkable
    /// tiles so an object never lands on a spawn zone or in a wall. Returns null on skip
    /// or unknown kind.</summary>
    private Unit SpawnMapObject(string kind, TileData tile)
    {
        if (tile == null || tile.IsOccupied || !tile.IsWalkable || tile.IsBlocked)
            return null;
        if (!MapObjectCatalog.TryGet(kind, out var spec))
        {
            GD.PushWarning($"[MapObject] unknown kind '{kind}' — skipped.");
            return null;
        }

        var unit = DummyUnitScene.Instantiate<Unit>();
        unit.TeamId = 2;                       // neutral "field" team
        unit.IsPlayerControlled = false;
        unit.IsMapObject = true;
        unit.MapObjectKind = kind.ToLowerInvariant();
        unit.Pushable = spec.Pushable;
        unit.StartMaxHealth = spec.Hp;
        unit.StartHealth = spec.Hp;
        unit.StartBaseSpeed = 0;
        unit.StartMaxMana = 0;
        unit.StartMana = 0;
        unit.StartArmor = 0;
        unit.StartShield = 0;

        AddChild(unit);
        unit.OnDied += HandleUnitDeath;        // routes to HandleMapObjectDeath (IsMapObject branch)
        unit.PlaceOnTile(tile);
        unit.MaxActionPoints = 0;
        unit.CurrentActionPoints = 0;
        unit.Name = spec.Label;
        unit.DisplayName = spec.Label;
        unit.DefinitionId = "mapobj_" + unit.MapObjectKind;
        unit.SetBodyColor(spec.BodyColor);
        unit.RefreshNameLabel();

        if (spec.BlocksLoS)
            tile.BlocksLineOfSight = true;

        fieldObjects.Add(unit);
        GD.Print($"[MapObject] {spec.Label} at {tile.Axial} (HP {spec.Hp}, LoS {spec.BlocksLoS}, push {spec.Pushable}).");
        return unit;
    }

    /// <summary>Death path for a neutral object. OnDied fires while CurrentTile is still
    /// valid (before Die() clears it), so the on-death effect and LoS clear read the
    /// right tile. Then Die() frees the tile and hides the node.</summary>
    private void HandleMapObjectDeath(Unit unit)
    {
        var tile = unit.CurrentTile;
        if (tile != null)
        {
            tile.BlocksLineOfSight = false;    // demolished cover stops blocking sight
            ResolveMapObjectDeathEffect(unit.MapObjectKind, tile);
        }
        fieldObjects.Remove(unit);
        if (!unit.IsDeathQueued)
            unit.Die();
        RefreshThreatTiles();
        combatUI?.AppendActionLog($"{unit.DisplayName} is destroyed.");
        GD.Print($"[MapObject] {unit.DisplayName} destroyed.");
    }

    private void ResolveMapObjectDeathEffect(string kind, TileData tile)
    {
        switch (kind)
        {
            case "cracked_pillar":
                MakeRubble(tile);
                break;
            case "resonant_crystal":
                MapObjectBurst(tile, 4);
                break;
            case "powder_cask":
                MapObjectBurst(tile, 6);
                MakeRubble(tile);
                break;
            case "ember_brazier":
                ImbueAround(tile, TileElementType.Fire);
                break;
            // boulder, ward_stone: no on-death effect
        }
    }

    /// <summary>Turn a tile into difficult terrain (move cost 2). Not blocking, not LoS.</summary>
    private void MakeRubble(TileData tile)
    {
        if (tile == null)
            return;
        tile.MoveCost = 2;
        tile.BaseMoveCost = 2;
        tile.ObstacleKind = "rubble";
        grid.GetTileView(tile.Axial)?.SetTerrainScar("rubble");
    }

    /// <summary>Radius-1 burst — damages living occupants of the tile and its neighbours,
    /// excluding other map objects so casks/crystals don't chain-detonate.</summary>
    private void MapObjectBurst(TileData center, int dmg)
    {
        HitTileOccupant(center, dmg);
        foreach (var nb in grid.GetNeighbors(center.Axial))
            HitTileOccupant(grid.GetTile(nb), dmg);
    }

    private static void HitTileOccupant(TileData t, int dmg)
    {
        var occ = t?.Occupant;
        if (occ != null && occ.Stats.IsAlive && !occ.IsMapObject)
            occ.ApplyDamage(dmg);
    }

    /// <summary>Imbue a tile and its walkable neighbours with an element (brazier coals).</summary>
    private void ImbueAround(TileData tile, TileElementType el)
    {
        TileEntryReactions.ImbueTile(tile, el);
        foreach (var nb in grid.GetNeighbors(tile.Axial))
        {
            var t = grid.GetTile(nb);
            if (t != null && t.IsWalkable && !t.IsBlocked)
                TileEntryReactions.ImbueTile(t, el);
        }
    }

    /// <summary>Ward Stone aura (round start): any living combatant within 2 of a living
    /// ward stone gains +1 armor. Neutral ground — either side benefits from holding it.
    /// Armor is a consumable damage pool, so this rewards standing near it without
    /// unbounded stacking. Called from the round boundary.</summary>
    private void ApplyWardStoneAuras()
    {
        if (fieldObjects.Count == 0 || grid == null)
            return;
        foreach (var obj in fieldObjects)
        {
            if (obj == null || !IsInstanceValid(obj) || !obj.Stats.IsAlive)
                continue;
            if (obj.MapObjectKind != "ward_stone" || obj.CurrentTile == null)
                continue;
            var center = obj.CurrentTile.Axial;
            GrantWardArmor(center, playerUnits);
            GrantWardArmor(center, enemyUnits);
        }
    }

    private void GrantWardArmor(Vector2I center, List<Unit> units)
    {
        foreach (var u in units)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (grid.Distance(center, u.CurrentTile.Axial) <= 2)
                u.Stats.Armor += 1;
        }
    }
}
