using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// Unit.cs
//
// Purpose:        The combat unit — Stats (HP, mana, AP, armor,
//                 shield, statuses), Unit Node3D (visual +
//                 tile occupancy + facing), and the public API
//                 every effect/AI/UI uses to interact with it.
//                 Each player wizard, companion, and enemy in
//                 combat is one of these.
// Layer:          System
// Collaborators:  TileData.cs (occupancy back-pointer),
//                 HexGridManager.cs (placement), HealthBarRoot.cs
//                 (visual binding), UnitDeckData.cs (wizard
//                 decks), ElementalAttunement.cs (Elementalist
//                 charges), StanceDefinition.cs (martials),
//                 every IEffect that targets units
// See:            README §3 — units are the second-most central
//                 abstraction after Card
// ============================================================

/// <summary>Combat-side stat block for one unit. Holds HP, mana, AP, armor, shield, speed/move points, and a status-effect dictionary. <see cref="IsAlive"/> is the canonical alive check. Mutated by every effect that does damage / heals / applies statuses.</summary>
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
    public bool HasPlayedCardThisTurn = false;

    public int Armor;
    public int Shield;

    public bool IsAlive => Health > 0;

    // Active status effects: name -> turns remaining
    public Dictionary<string, int> StatusEffects = new();
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
    [Export] public int MoveRange = 3;
    [Export] public int StartMaxMana = 3;
    [Export] public int StartMana = 3;
    public bool IsDeathQueued { get; private set; }

    // School-specific class mechanic. Created in _Ready based on School.
    // Null for Generic or schools without a mechanic yet.
    public ISchoolAttunement Attunement { get; private set; }
    [Export] public CardSchool School = CardSchool.Adept;

    // ── Equipment passives — set by CombatManager after applying loadout ────
    public List<(ItemPassiveTag tag, int value)> EquipmentPassives = new();
    public int BonusSpellDamage = 0;   // from wizard weapon/trinket

    // ── Combat archetype (set by CombatManager at spawn time) ───────────────
    public EnemyArchetype EnemyArchetype = EnemyArchetype.Soldier;
    public int AttackRange = 1;   // 1 = melee; >1 = ranged
    public int AttackDamage = 5;   // base damage per attack

    // ── Martial companion fields ─────────────────────────────────────────────
    public bool IsMartial = false;
    public MartialClass MartialClass = MartialClass.None;
    public string CompanionId = "";

    // ── Stance system ─────────────────────────────────────────────────────────
    public List<StanceDefinition> AvailableStances = new();
    public StanceDefinition ActiveStance = null;
    public bool HasSwitchedStanceThisTurn = false;
    public bool HasAttackedThisCombat = false; // Ambush tracking

    // ── Action Points ─────────────────────────────────────────────────────────
    public int MaxActionPoints = 0;  // set at spawn from TG tier
    public int CurrentActionPoints = 0;

    public bool CanSpendAP(int cost) => CurrentActionPoints >= cost;

    public bool TrySpendAP(int cost)
    {
        if (CurrentActionPoints < cost) return false;
        CurrentActionPoints -= cost;
        return true;
    }

    // Runtime stats
    public Stats Stats = new Stats();
    public UnitDeckData DeckData { get; set; }
    public TileData CurrentTile { get; private set; }
    private HealthBarRoot _healthBar;

    // Selection visual
    private MeshInstance3D _selectionRing;
    private StandardMaterial3D _selectionMat;
    private bool _isSelected = false;
    private MeshInstance3D _hoverRing;
    private StandardMaterial3D _hoverMat;
    private bool _isHovered = false;

    // ── Spirit fields (Necromancer summoned units) ─────────────────────────────
    public bool IsSpirit = false;
    public int SummonerTeamId = -1;
    public bool OnDeathMemorial = false;
    public bool CreateMemorialOnKill = false;
    public int SpiritDamageBuff = 0;
    public int SpiritDamageBuffTurns = 0;
    public bool IsUndying = false;
    public bool UndyingFullRestore = false;
    public int UndyingReviveHP = 8;
    public int UndyingTurns = 0;
    public bool IsInvulnerable = false;
    public int InvulnerableTurns = 0;
    public bool IsVigil = false;
    public int VigilTurns = 0;

    /// <summary>
    /// Fires when this unit moves to a new tile.
    /// Parameters: the tile the unit just LEFT (may be null on first placement).
    /// </summary>
    public event Action<TileData> OnTileLeft;
    public event Action<Unit> OnDied;

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
        _healthBar?.SetHealth(Stats.Health, Stats.MaxHealth, Stats.Armor, Stats.Shield);
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);

        //InitializeAttunement();

        CreateSelectionRing();
        SetSelected(false);

        CreateHoverRing();

        _nameLabel = GetNodeOrNull<Label3D>("NameLabel");
        if (_nameLabel != null)
            _nameLabel.Text = DisplayName.Length > 0 ? DisplayName : Name;
    }

    public void StartTurn()
    {
        if (!IsInstanceValid(this)) return;

        CurrentActionPoints = MaxActionPoints;
        Stats.HasActed = false;

        Stats.Mana = Stats.MaxMana;
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);
    }


    public void PlaceOnTile(TileData tile)
    {
        if (tile == null) return;
        if (tile.IsOccupied && tile.Occupant != this) return;

        var previousTile = CurrentTile;
        CurrentTile?.ClearOccupant(this);
        CurrentTile = tile;
        tile.TrySetOccupant(this);

        if (tile.TileView != null)
            GlobalPosition = tile.TileView.GlobalPosition;

        // Fire the callback so effects can react to movement
        if (previousTile != null && previousTile != tile)
            OnTileLeft?.Invoke(previousTile);

        // Check for glyph
        if (tile?.Glyph != null && !tile.Glyph.Consumed && tile.Glyph.OwnerTeam != this.TeamId)
        {
            var glyph = tile.Glyph;
            glyph.Consumed = true;
            tile.Glyph = null;
            tile.TileView?.ClearGlyph();
            glyph.OnTrigger?.Invoke(this, glyph.GameState);
        }
    }

    public bool TryMoveTo(HexGridManager grid, TileData dest)
    {
        if (grid == null || dest == null || CurrentTile == null) return false;
        if (!dest.CanEnter(this)) return false;
        if (dest.Axial == CurrentTile.Axial) return false;
        if (!CanSpendAP(1)) return false;

        int pathCost = grid.GetMoveCostTo(this, dest);
        if (pathCost < 0 || pathCost > MoveRange) return false;


        TrySpendAP(1);
        PlaceOnTile(dest);
        return true;
    }

    public void ApplyDamage(int amount)
    {
        if (amount <= 0 || IsDeathQueued) return;
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

        RefreshHealthBar();
        GD.Print($"{Name} HP:{Stats.Health}/{Stats.MaxHealth} Shield:{Stats.Shield} Armor:{Stats.Armor}");

        if (!Stats.IsAlive && !IsDeathQueued)
        {
            OnDied?.Invoke(this);
            Die();
        }
    }

    public void Die()
    {
        if (IsDeathQueued) return;   // idempotent — calling twice does nothing
        IsDeathQueued = true;

        // Free the tile immediately so other units can move/spawn there
        CurrentTile?.ClearOccupant(this);
        CurrentTile = null;

        // Hide visually, but DON'T QueueFree yet — leave that to GameRunner
        Visible = false;

        // Disable any input/physics so it can't be clicked or interacted with
        SetProcessInput(false);
        SetProcessUnhandledInput(false);
    }

    public void GainMana(int amount)
    {
        if (amount <= 0) return;
        Stats.Mana += amount; // no cap — overflow allowed this turn
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

    public void RefreshHealthBar()
    {
        _healthBar?.SetHealth(Stats.Health, Stats.MaxHealth, Stats.Armor, Stats.Shield);
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);
        _healthBar?.SetArmor(Stats.Armor, Stats.MaxHealth);
        _healthBar?.SetShield(Stats.Shield, Stats.MaxHealth);
        _healthBar?.SetSpeed(Stats.MovePoints);
    }

    // Status handling

    public void ApplyStatus(string status, int duration)
    {
        // If already has this status, take the longer duration
        if (Stats.StatusEffects.ContainsKey(status))
            Stats.StatusEffects[status] = Math.Max(Stats.StatusEffects[status], duration);
        else
            Stats.StatusEffects[status] = duration;

        // Apply status immediately
        if (status == "frozen" || status == "rooted")
            CurrentActionPoints = 0;
        else if (status == "slowed")
            CurrentActionPoints = Math.Max(0, CurrentActionPoints / 2);
        else if (status == "chaining")
            // no immediate effect, but checked at cast time by DealDamageEffect

            GD.Print($"{Name} gains {status} for {duration} turn(s).");
    }

    public bool HasStatus(string status)
    {
        return Stats.StatusEffects.ContainsKey(status) && Stats.StatusEffects[status] > 0;
    }

    public void RemoveStatus(string statusName)
    {
        Stats.StatusEffects?.Remove(statusName);
        RefreshHealthBar();
    }

    public void TickStatuses()
    {
        // Call this at the START of each unit's turn
        var expired = new List<string>();
        foreach (var kvp in Stats.StatusEffects)
        {
            Stats.StatusEffects[kvp.Key] = kvp.Value - 1;
            if (Stats.StatusEffects[kvp.Key] <= 0)
                expired.Add(kvp.Key);
        }

        foreach (var key in expired)
        {
            Stats.StatusEffects.Remove(key);
            GD.Print($"{Name}: {key} expired.");
        }

        // Re-apply ongoing effects for statuses that are still active
        if (HasStatus("frozen") || HasStatus("rooted"))
            Stats.MovePoints = 0;
        else if (HasStatus("slowed"))
            Stats.MovePoints = Math.Max(0, Stats.MovePoints / 2);
    }

    public bool CanAct()
    {
        // Frozen = can't do anything (move or cast)
        if (HasStatus("frozen")) return false;
        return true;
    }

    public bool CanMove() => CurrentActionPoints >= 1 && Stats.IsAlive;

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
            TopRadius = 0.75f,
            BottomRadius = 0.75f,
            Height = 0.05f,
            RadialSegments = 24
        };
        ring.Mesh = mesh;
        ring.Position = new Vector3(0f, 0.03f, 0f);

        _hoverMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.8f, 0.1f, 0.7f), // gold
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true
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

    public void ApplySpiritAppearance()
    {
        // Pale blue-white — luminous, distinct from enemy undead
        SetBodyColor(new Color(0.72f, 0.88f, 1.0f));
    }

    public void InitializeAttunement()
    {
        Attunement = School switch
        {
            CardSchool.Elementalist => new ElementalAttunement(),
            // Future schools:
            CardSchool.Necromancer => new GriefAttunement(),
            // CardSchool.Arcanist   => new ArcaneFocus(),
            _ => null
        };
    }

    // For predicates that need to check the caster's current tile properties, this tracks the element of the last cast spell for use in those checks.
    public ElementTag LastCastElement = ElementTag.Fire;
    public ElementTag HighestAttunementElement
    {
        get
        {
            if (Attunement is not ElementalAttunement att) return ElementTag.Fire;
            ElementTag best = ElementTag.Fire;
            int bestCount = -1;
            foreach (var kvp in att.Charges)
            {
                if (kvp.Value > bestCount)
                {
                    bestCount = kvp.Value;
                    best = kvp.Key;
                }
            }
            return best;
        }
    }

}