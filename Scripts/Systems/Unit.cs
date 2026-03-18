using Godot;
using System;

public sealed class Stats
{
    public int MaxHealth;
    public int Health;

    public int MaxMana;
    public int Mana;

    public int BaseSpeed;
    public int MovePoints;

    public int Armor;
    public int Shield;

    public bool IsAlive => Health > 0;
}

public partial class Unit : Node3D
{
    [Export] public bool IsPlayerControlled = false;
    [Export] public int TeamId = 0;

    // ✅ Inspector-tweakable starting values
    [Export] public int StartMaxHealth = 10;
    [Export] public int StartHealth = 10;
    [Export] public int StartArmor = 0;
    [Export] public int StartShield = 0;
    [Export] public int StartBaseSpeed = 3;
    [Export] public int StartMaxMana = 3;
    [Export] public int StartMana = 3;

    public Stats Stats = new Stats();

    public TileData CurrentTile { get; private set; }
    private HealthBarRoot _healthBar;

    public override void _Ready()
    {
        // initialize runtime stats from exported values
        Stats.MaxHealth = StartMaxHealth;
        Stats.Health = Mathf.Clamp(StartHealth, 0, StartMaxHealth);

        Stats.Armor = StartArmor;
        Stats.Shield = StartShield;

        Stats.BaseSpeed = StartBaseSpeed;
        Stats.MovePoints = StartBaseSpeed;

        Stats.MaxMana = StartMaxMana;
        Stats.Mana = Mathf.Clamp(StartMana, 0, StartMaxMana);

        _healthBar = GetNodeOrNull<HealthBarRoot>("HealthBarRoot");
        _healthBar?.SetHealth(Stats.Health, Stats.MaxHealth);
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);
    }

    public void StartTurn()
    {
        Stats.MovePoints = Stats.BaseSpeed;
    }


    public void PlaceOnTile(TileData tile)
    {
        if (tile == null)
            return;

        if (!tile.TrySetOccupant(this))
        {
            GD.PrintErr($"Cannot place {Name} on tile {tile.Axial}; tile is blocked or occupied.");
            return;
        }

        CurrentTile?.ClearOccupant(this);
        CurrentTile = tile;

        if (tile.TileView != null)
            GlobalPosition = tile.TileView.GlobalPosition;
    }

    public bool TryMoveTo(HexGridManager grid, TileData dest)
    {
        if (grid == null || dest == null || CurrentTile == null)
            return false;

        if (!dest.CanEnter(this))
            return false;

        int dist = grid.Distance(CurrentTile, dest);
        if (dist <= 0)
            return false;
        if (dist > Stats.MovePoints)
            return false;

        Stats.MovePoints -= dist;
        PlaceOnTile(dest);
        return true;
    }

        public bool TryMoveTo(HexGridManager grid, HexTile destView)
    {
        if (grid == null || destView == null)
            return false;

        var destTile = grid.GetTile(destView.Axial);
        if (destTile == null)
            return false;

        return TryMoveTo(grid, destTile);
    }

    public void ApplyDamage(int amount)
    {
        if (amount <= 0) return;
        int remaining = amount;

        if (Stats.Shield > 0)
        {
            int used = Math.Min(Stats.Shield, remaining);
            Stats.Shield -= used;
            remaining -= used;
        }

        if (remaining > 0 && Stats.Armor > 0)
        {
            int used = Math.Min(Stats.Armor, remaining);
            Stats.Armor -= used;
            remaining -= used;
        }

        if (remaining > 0)
            Stats.Health = Math.Max(0, Stats.Health - remaining);

        _healthBar?.SetHealth(Stats.Health, Stats.MaxHealth);
        GD.Print($"{Name} HP now {Stats.Health}/{Stats.MaxHealth}");

    }

    public void GainMana(int amount)
    {
        if (amount <= 0) return;
        Stats.Mana = Math.Min(Stats.MaxMana, Stats.Mana + amount);
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);
    }

    public bool TrySpendMana(int amount)
    {
        if (amount <= 0) return true;
        if (Stats.Mana < amount) return false;
        Stats.Mana -= amount;
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);
        return true;
    }

    public void SyncManaToBar()
    {
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);
    }
}