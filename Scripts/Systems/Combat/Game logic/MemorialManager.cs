using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public class MemorialManager
{
    // Fires when a memorial is created — HexTile listens to refresh visuals
    public event Action<TileData> OnMemorialCreated;
    // Fires when a memorial's state changes (Fresh→Established, Hallowed)
    public event Action<TileData> OnMemorialChanged;
    // Fires when a memorial is removed
    public event Action<TileData> OnMemorialRemoved;

    private readonly HexGridManager _grid;

    public MemorialManager(HexGridManager grid)
    {
        _grid = grid;
    }

    // ── Called by unit death handler ─────────────────────────────────
    public void CreateMemorial(TileData tile, Unit deceased, int ownerTeamId)
    {
        if (tile == null) return;

        if (tile.HasMemorial)
        {
            // Overlap — strengthen the existing memorial
            Strengthen(tile);
            return;
        }

        bool wasAlly = deceased.TeamId == ownerTeamId;
        string name = deceased.DisplayName.Length > 0 ? deceased.DisplayName : deceased.Name;
        var strength = DetermineStrength(deceased);

        tile.Memorial = new MemorialData(name, wasAlly, strength, ownerTeamId);
        OnMemorialCreated?.Invoke(tile);
    }

    // ── Overload for non-unit deaths (constructs, summons, etc.) ────
    public void CreateMemorial(TileData tile, string sourceName, bool wasAlly,
                               MemorialStrength strength, int ownerTeamId)
    {
        if (tile == null) return;

        if (tile.HasMemorial)
        {
            Strengthen(tile);
            return;
        }

        tile.Memorial = new MemorialData(sourceName, wasAlly, strength, ownerTeamId);
        OnMemorialCreated?.Invoke(tile);
    }

    // ── Called by Hallowed Ground / Memorial Garden card effects ────
    public void HallowTile(TileData tile)
    {
        if (tile == null) return;

        if (!tile.HasMemorial)
        {
            // Create a faint memorial as the base for hallowing
            tile.Memorial = new MemorialData("Hallowed Ground", false,
                                             MemorialStrength.Faint, -1);
        }

        tile.Memorial.Hallow();
        OnMemorialChanged?.Invoke(tile);
    }

    // ── Called by cards that consume memorials (Release, Revenant, etc.) ──
    public bool ConsumeMemorial(TileData tile)
    {
        if (!tile.HasMemorial) return false;

        tile.Memorial.ConsumedThisTurn = true;
        return true;
    }

    // ── Called at start of each turn ────────────────────────────────
    public void Tick()
    {
        // Grid may not be assigned yet at first tick — skip silently
        if (_grid?.Tiles == null) return;

        var allTiles = _grid.Tiles.Values.ToList();

        foreach (var tile in allTiles)
        {
            if (tile.Memorial == null) continue;

            if (tile.Memorial.ConsumedThisTurn)
            {
                tile.Memorial = null;
                OnMemorialRemoved?.Invoke(tile);
                continue;
            }

            bool wasChanged = tile.Memorial.State == MemorialState.Fresh;
            tile.Memorial.Tick();

            if (wasChanged)
                OnMemorialChanged?.Invoke(tile);
        }
    }

    // ── Queries used by card effects and targeter validation ─────────
    public List<TileData> GetAllMemorials()
        => _grid.Tiles.Values.Where(t => t.HasMemorial).ToList();

    public List<TileData> GetMemorialsInRange(Vector2I origin, int range)
        => _grid.Tiles.Values
                .Where(t => t.HasMemorial && _grid.Distance(origin, t.Axial) <= range)
                .ToList();

    public int CountMemorials()
        => _grid.Tiles.Values.Count(t => t.HasMemorial);

    // ── Private helpers ──────────────────────────────────────────────
    private void Strengthen(TileData tile)
    {
        if (tile.Memorial.Strength < MemorialStrength.Strong)
        {
            tile.Memorial.Strength++;
            OnMemorialChanged?.Invoke(tile);
        }
    }

    private static MemorialStrength DetermineStrength(Unit deceased)
    {
        // Placeholder logic — expand when unit rarity/tier exists
        if (deceased.StartMaxHealth >= 20) return MemorialStrength.Strong;
        if (deceased.StartMaxHealth >= 10) return MemorialStrength.Solid;
        return MemorialStrength.Faint;
    }
}