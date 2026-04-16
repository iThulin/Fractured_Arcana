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
    public bool HasMoved;
    public bool HasActed;

    public int Armor;
    public int Shield;

    public bool IsAlive => Health > 0;
}

public partial class Unit : Node3D
{
    // Basic unit properties
    [Export] public bool IsPlayerControlled = false;
    [Export] public int TeamId = 0;
    [Export] public string DisplayName = "";
    private Label3D _nameLabel;

    // Starting stats (can be overridden in the editor for different unit types)
    [Export] public int StartMaxHealth = 10;
    [Export] public int StartHealth = 10;
    [Export] public int StartArmor = 0;
    [Export] public int StartShield = 0;
    [Export] public int StartBaseSpeed = 2;
    [Export] public int StartMaxMana = 3;
    [Export] public int StartMana = 3;

    public Stats Stats = new Stats();

    public TileData CurrentTile { get; private set; }
    private HealthBarRoot _healthBar;

    // Selection visual
    private MeshInstance3D _selectionRing;
    private StandardMaterial3D _selectionMat;
    private bool _isSelected = false;
    private MeshInstance3D _hoverRing;
    private StandardMaterial3D _hoverMat;
    private bool _isHovered = false;

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

        CreateSelectionRing();
        SetSelected(false);

        CreateHoverRing();
        CreateNameLabel();
    }

    public void StartTurn()
    {
        if (!IsInstanceValid(this)) return;

        Stats.MovePoints = Stats.BaseSpeed;
        Stats.HasMoved   = false;
        Stats.HasActed   = false;

        Stats.Mana = Stats.MaxMana;
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);
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
        Stats.HasMoved = true;
        PlaceOnTile(dest);
        return true;
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

        if (!Stats.IsAlive)
        {
            GD.Print($"{Name} has died.");
            Die();
        }

    }

    public void Die()
    {
        // Free the tile so other units can enter it
        CurrentTile?.ClearOccupant(this);
        CurrentTile = null;

        // Hide and remove from the scene
        Visible = false;
        QueueFree();
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

    // Selection visual methods
    private void CreateSelectionRing()
    {
        var ring = new MeshInstance3D();
        var mesh = new CylinderMesh
        {
            TopRadius = 0.7f,
            BottomRadius = 0.7f,
            Height = 0.05f,
            RadialSegments = 24
        };

        ring.Mesh = mesh;
        ring.Position = new Vector3(0f, 0.05f, 0f);

        _selectionMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.2f, 1.0f, 0.2f, 0.85f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true
        };

        ring.SetSurfaceOverrideMaterial(0, _selectionMat);
        AddChild(ring);

        _selectionRing = ring;
    }

    public void SetSelected(bool selected)
    {
        _isSelected = selected;

        if (_selectionRing == null) CreateSelectionRing();
        if (_selectionRing != null) _selectionRing.Visible = selected;

        // Hide hover ring while selected to avoid visual overlap
        if (_hoverRing != null && selected)
            _hoverRing.Visible = false;
    }

    private void CreateHoverRing()
    {
        var ring = new MeshInstance3D();
        var mesh = new CylinderMesh
        {
            TopRadius    = 0.75f,
            BottomRadius = 0.75f,
            Height       = 0.05f,
            RadialSegments = 24
        };
        ring.Mesh = mesh;
        ring.Position = new Vector3(0f, 0.03f, 0f);

        _hoverMat = new StandardMaterial3D
        {
            AlbedoColor  = new Color(1.0f, 0.8f, 0.1f, 0.7f), // gold
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest  = true
        };
        ring.SetSurfaceOverrideMaterial(0, _hoverMat);
        ring.Visible = false;
        AddChild(ring);
        _hoverRing = ring;
    }

    public void SetHovered(bool hovered)
    {
        _isHovered = hovered;
        if (_hoverRing != null)
            _hoverRing.Visible = hovered && !_isSelected;
    }

    private void CreateNameLabel()
    {
        _nameLabel = new Label3D
        {
            Text       = DisplayName.Length > 0 ? DisplayName : Name,
            Billboard  = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize   = 18,
            Modulate   = new Color(1f, 0.85f, 0.85f, 1f),
            Position   = new Vector3(0f, 2.4f, 0f),
            OutlineSize = 6,
            OutlineModulate = Colors.Black
        };
        AddChild(_nameLabel);
    }

    public void RefreshNameLabel()
    {
        if (_nameLabel != null)
            _nameLabel.Text = DisplayName.Length > 0 ? DisplayName : Name;
    }

    public void SetBodyColor(Color color)
    {
        var mesh = GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (mesh == null) return;
        var mat = new StandardMaterial3D { AlbedoColor = color };
        mesh.SetSurfaceOverrideMaterial(0, mat);
}
}