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

    // Poison tracks drain rate separately because the status dict only
    // stores duration. Set when poisoned is applied, persists until combat ends.
    public int PoisonDrainPerTurn = 0;

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
        if (CurrentActionPoints < cost)
            return false;
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

    /// <summary>False when the unit has the 'bound' status, which prevents 
    /// cleanse/dispel effects from removing it.
    /// </summary>
    public bool CanBeFreed => !HasStatus("bound");

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
        _healthBar?.Initialize(IsPlayerControlled);
        _healthBar?.SetHealth(Stats.Health, Stats.MaxHealth, Stats.Armor, Stats.Shield);
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);

        InitializeAttunement();

        CreateSelectionRing();
        SetSelected(false);

        CreateHoverRing();

        _nameLabel = GetNodeOrNull<Label3D>("NameLabel");
        if (_nameLabel != null)
            _nameLabel.Text = DisplayName.Length > 0 ? DisplayName : Name;
    }

    public void StartTurn()
    {
        if (!IsInstanceValid(this))
            return;

        CurrentActionPoints = MaxActionPoints;
        Stats.HasActed = false;
        Stats.MovePoints = Stats.BaseSpeed;

        Stats.Mana = Stats.MaxMana;
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);

        // Tick statuses first so expired ones don't affect this turn
        TickStatuses();

        // Now apply movement/action restrictions from still-active statuses
        if (HasStatus("frozen") || HasStatus("stunned"))
        {
            CurrentActionPoints = 0;
            Stats.MovePoints = 0;
        }
        else if (HasStatus("rooted"))
        {
            Stats.MovePoints = 0;
            // AP unchanged — rooted unit can still cast
        }
        else if (HasStatus("slowed"))
        {
            Stats.MovePoints = Math.Max(0, Stats.MovePoints / 2);
            // AP unchanged — slowed unit can still act, just moves less
        }

        if (HasStatus("bound"))
        {
            CurrentActionPoints = 0;
            Stats.MovePoints = 0;
        }

        RefreshHealthBar();
    }


    public void PlaceOnTile(TileData tile)
    {
        if (tile == null)
            return;
        if (tile.IsOccupied && tile.Occupant != this)
            return;

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
        if (tile?.Glyph != null && !tile.Glyph.Consumed)
        {
            var glyph = tile.Glyph;
            var state = glyph.GameState;
            bool enemyOfOwner = glyph.OwnerTeam != this.TeamId;

            bool shouldFire =
                (enemyOfOwner && glyph.Trigger == GlyphTrigger.Enter) ||
                (!enemyOfOwner && glyph.Trigger == GlyphTrigger.AllyEnter);

            if (shouldFire)
            {
                glyph.Fire(this, state);
                bool keep = state?.Glyphs?.OnGlyphFired(state, tile, this)
                            ?? glyph.Reusable;
                if (!keep)
                {
                    glyph.Consumed = true;
                    tile.Glyph = null;
                    tile.TileView?.ClearGlyph();
                }
            }
        }

        // Colossus tile absorption
        if (HasStatus("colossus_absorb") && CurrentTile?.ElementType != TileElementType.None)
        {
            var element = CurrentTile.ElementType;
            switch (element)
            {
                case TileElementType.Fire:
                    AttackDamage += 2;
                    GD.Print($"[Colossus] {Name} absorbs fire — +2 DMG (now {AttackDamage}).");
                    break;
                case TileElementType.Earth:
                    Stats.Armor += 2;
                    RefreshHealthBar();
                    GD.Print($"[Colossus] {Name} absorbs earth — +2 Armor (now {Stats.Armor}).");
                    break;
                case TileElementType.Lightning:
                    Stats.BaseSpeed = Math.Min(Stats.BaseSpeed + 1, 4);
                    GD.Print($"[Colossus] {Name} absorbs storm — +1 Speed (now {Stats.BaseSpeed}).");
                    break;
                case TileElementType.Frost:
                    Stats.Shield += 4;
                    RefreshHealthBar();
                    GD.Print($"[Colossus] {Name} absorbs frost — +4 Shield.");
                    break;
            }
            CurrentTile.ElementType = TileElementType.None;
            CurrentTile.ElementStrength = 0f;
            CurrentTile.TileView?.SetElement(TileElementType.None);
        }
    }

    public bool TryMoveTo(HexGridManager grid, TileData dest)
    {
        if (grid == null || dest == null || CurrentTile == null)
            return false;
        if (!dest.CanEnter(this))
            return false;
        if (dest.Axial == CurrentTile.Axial)
            return false;
        if (!CanSpendAP(1))
            return false;

        int pathCost = grid.GetMoveCostTo(this, dest);
        if (pathCost < 0 || pathCost > MoveRange)
            return false;


        TrySpendAP(1);
        PlaceOnTile(dest);
        return true;
    }

    public void ApplyDamage(int amount)
    {
        if (amount <= 0 || IsDeathQueued)
            return;
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
        if (IsDeathQueued)
            return;   // idempotent — calling twice does nothing
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

    /// <summary>
    /// Forces this unit to die from a non-damage source (e.g. poison max HP drain).
    /// Fires OnDied and delegates to Die() for cleanup.
    /// </summary>
    public void KillFromEffect()
    {
        if (IsDeathQueued || !Stats.IsAlive)
            return;
        Stats.Health = 0;
        OnDied?.Invoke(this);
        Die();
    }

    public void GainMana(int amount)
    {
        if (amount <= 0)
            return;
        Stats.Mana += amount; // no cap — overflow allowed this turn
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);
    }

    public bool TrySpendMana(int amount)
    {
        if (amount <= 0)
            return true;
        if (Stats.Mana < amount)
            return false;
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
        _healthBar?.SetAP(CurrentActionPoints, MaxActionPoints, Stats.Armor, Stats.Shield);
        _healthBar?.RefreshStatuses(Stats.StatusEffects);
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
        if (status == "frozen")
            CurrentActionPoints = 0;
        else if (status == "rooted")
            Stats.MovePoints = 0;  // can still cast, can't move
        else if (status == "slowed")
            CurrentActionPoints = Math.Max(0, CurrentActionPoints / 2);
        else if (status == "bound")
        {
            // Cannot act or be freed until next player turn — zero AP, immune to cleanse
            CurrentActionPoints = 0;
            Stats.MovePoints = 0;
        }
        else if (status == "poisoned")
        {
            Stats.PoisonDrainPerTurn = Math.Max(Stats.PoisonDrainPerTurn, duration);
            // Override duration to a large number so TickStatuses doesn't
            // accidentally expire it — poison is permanent until combat ends.
            if (Stats.StatusEffects.ContainsKey("poisoned"))
                Stats.StatusEffects["poisoned"] = 999;
            else
                Stats.StatusEffects["poisoned"] = 999;
            // Don't fall through to the normal duration assignment below.
            GD.Print($"{Name} is poisoned ({Stats.PoisonDrainPerTurn} max HP/turn).");
            return;
        }
        else if (status == "chaining")
        {
            // no immediate effect, but checked at cast time by DealDamageEffect
        }

        GD.Print($"{Name} gains {status} for {duration} turn(s).");
        _healthBar?.RefreshStatuses(Stats.StatusEffects);
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
        var expired = new List<string>();
        foreach (var kvp in Stats.StatusEffects)
        {
            if (kvp.Key == "poisoned")
                continue;

            Stats.StatusEffects[kvp.Key] = kvp.Value - 1;
            if (Stats.StatusEffects[kvp.Key] <= 0)
                expired.Add(kvp.Key);
        }

        foreach (var key in expired)
        {
            Stats.StatusEffects.Remove(key);
            GD.Print($"{Name}: {key} expired.");
        }

        _healthBar?.RefreshStatuses(Stats.StatusEffects);
    }

    /// <summary>
    /// Applies poison, reducing max HP by <paramref name="drainPerTurn"/> each turn,
    /// clamping current HP to the new max. Stacks by taking the highest drain rate.
    /// Permanent until combat ends — does not tick down via TickStatuses.
    /// </summary>
    public void ApplyPoison(int drainPerTurn)
    {
        ApplyStatus("poisoned", drainPerTurn);
    }

    /// <summary>
    /// Clears the poison status and resets the drain rate.
    /// Call this at combat end to avoid carrying poison state into the next fight.
    /// </summary>
    public void ClearPoison()
    {
        Stats.StatusEffects.Remove("poisoned");
        Stats.PoisonDrainPerTurn = 0;
    }

    public bool CanAct()
    {
        // Frozen = can't do anything (move or cast)
        if (HasStatus("frozen"))
            return false;
        if (HasStatus("bound"))
            return false; // can't act or be freed until next player turn
        if (HasStatus("stunned"))
            return false; // can't act but can still move
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

        if (_selectionRing == null)
            CreateSelectionRing();
        if (_selectionRing != null)
            _selectionRing.Visible = selected;

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

    public void SetDetailedBar(bool detailed)
    {
        _healthBar?.SetDetailed(detailed);
        // Also push AP into the bar whenever detail opens
        if (detailed)
            _healthBar?.SetAP(CurrentActionPoints, MaxActionPoints,
                            Stats.Armor, Stats.Shield);
    }

    public void SetBodyColor(Color color)
    {
        var mesh = GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (mesh == null)
            return;
        var mat = new StandardMaterial3D { AlbedoColor = color };
        mesh.SetSurfaceOverrideMaterial(0, mat);
    }

    public void ApplySpiritAppearance()
    {
        var meshNode = GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (meshNode == null)
            return;

        // ── Inherit mesh from guild death records ────────────────────────
        var record = HonoredDeadService.Claim();

        if (record != null && !string.IsNullOrEmpty(record.MeshResourcePath))
        {
            var inheritedMesh = GD.Load<Mesh>(record.MeshResourcePath);
            if (inheritedMesh != null)
            {
                meshNode.Mesh = inheritedMesh;
                GD.Print($"[Spirit] Inherited mesh from {record.Name}.");
            }

            // Show the source name for 2 seconds then revert to spirit name
            if (_nameLabel != null)
            {
                _nameLabel.Text = record.Name;
                var timer = GetTree().CreateTimer(2.0);
                timer.Timeout += () =>
                {
                    if (IsInstanceValid(this) && _nameLabel != null)
                        _nameLabel.Text = DisplayName?.Length > 0 ? DisplayName : Name;
                };
            }
        }

        // ── Tint: warm gold for allies, cool blue for enemies ────────────
        Color baseAlbedo = record?.WasAlly == true
            ? new Color(1.0f, 0.92f, 0.72f, 0.45f)   // ally — warm gold-white
            : new Color(0.72f, 0.88f, 1.0f, 0.45f);  // enemy — cool blue-white

        Color emission = record?.WasAlly == true
            ? new Color(1.0f, 0.85f, 0.55f)
            : new Color(0.55f, 0.78f, 1.0f);

        // ── Ethereal material ────────────────────────────────────────────
        var etherealMat = new StandardMaterial3D
        {
            AlbedoColor = baseAlbedo,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            EmissionEnabled = true,
            Emission = emission,
            EmissionEnergyMultiplier = 0.8f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            RimEnabled = true,
            Rim = 0.6f,
            RimTint = 0.3f,
        };

        meshNode.SetSurfaceOverrideMaterial(0, etherealMat);

        meshNode.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

        // ── Name label tint ──────────────────────────────────────────────
        if (_nameLabel != null)
            _nameLabel.Modulate = record?.WasAlly == true
                ? new Color(1.0f, 0.92f, 0.72f, 0.9f)   // warm gold
                : new Color(0.75f, 0.90f, 1.0f, 0.85f);  // cool blue

        // ── Slightly larger scale — spirits feel weightless ──────────────
        meshNode.Scale = new Vector3(1.05f, 1.12f, 1.05f);
    }

    public void InitializeAttunement()
    {
        Attunement = School switch
        {
            CardSchool.Elementalist => new ElementalAttunement(),
            CardSchool.Necromancer => new GriefAttunement(),
            CardSchool.Arcanist => new ArcaneAttunement(),
            CardSchool.Enchanter => new WeaveAttunement(),
            _ => null
        };
    }

    // For predicates that need to check the caster's current tile properties, this tracks the element of the last cast spell for use in those checks.
    public ElementTag LastCastElement = ElementTag.Fire;
    public ElementTag HighestAttunementElement
    {
        get
        {
            if (Attunement is not ElementalAttunement att)
                return ElementTag.Fire;
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