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
    public int MovePoints;          // legacy pool — no longer read for gating; see EffectiveMovement
    public int BonusMoveRange;      // movespeed currency: per-turn move-range grant, reset in StartTurn
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

    // ── Combat definition (set by CombatManager at spawn time, U2) ──────────
    /// <summary>UnitRegistry id this enemy was spawned from ("" for player units).</summary>
    public string DefinitionId = "";
    /// <summary>AI routine key, dispatched by CombatManager.EnemyIntents.PlanIntent.</summary>
    public string BehaviorKey = "";
    /// <summary>Composable behavior modifiers (pack/bulwark/charge/scout/immobile) — units doc §4a.</summary>
    public List<string> BehaviorTags = new();
    /// <summary>Triggered abilities from the UnitDefinition (units doc §5, U3).
    /// Shared defs are stateless; per-unit ability STATE (stacking bonuses etc.)
    /// lives on the unit's own stats, which handlers mutate.</summary>
    public List<UnitAbilityDef> Abilities = new();
    /// <summary>V3: per-ability use counters (Requiem stacks etc.) — combat-
    /// transient state for log grammar + §8 state chips. Keyed by ability Key.</summary>
    public readonly Dictionary<string, int> AbilityUseCounts = new();
    /// <summary>V2: "line"/"elite"/"boss"/"summon" — roster markers + nameplate policy.</summary>
    public string Role = "line";
    /// <summary>V2: owning archmage id ("" = none) — faction tinting.</summary>
    public string FactionId = "";
    public int AttackRange = 1;   // 1 = melee; >1 = ranged
    public int AttackDamage = 5;   // base damage per attack

    /// <summary>Case-insensitive behavior tag test.</summary>
    public bool HasBehaviorTag(string tag)
    {
        foreach (var t in BehaviorTags)
            if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ── Martial companion fields ─────────────────────────────────────────────
    public bool IsMartial = false;
    public MartialClass MartialClass = MartialClass.None;
    public string CompanionId = "";

    // ── Stance system ─────────────────────────────────────────────────────────
    public List<StanceDefinition> AvailableStances = new();
    public StanceDefinition ActiveStance = null;
    public bool HasSwitchedStanceThisTurn = false;
    public bool HasAttackedThisCombat = false; // Ambush tracking

    // ── Intent system ────────────────────────────────────────────────
    /// <summary>This unit's locked plan for the coming enemy phase. Null for player units and unplanned enemies.</summary>
    public EnemyIntent CurrentIntent;

    /// <summary>Tile locked when a wizard begins channelling; the release lands here regardless of repositioning. Cleared on release or interrupt.</summary>
    public Vector2I? ChannelTile = null;

    /// <summary>Adept/Namer "true name" hook — once set, every future intent this unit plans starts fully revealed.</summary>
    public bool IntentPermanentlyRevealed = false;

    private Label3D _intentLabel;

    /// <summary>Shows or updates the floating intent glyph (e.g. "▲ 7", "✦ ?"). Follows the glyph-label pattern: CallDeferred add_child per README §8.</summary>
    public void SetIntentDisplay(string text, Color color)
    {
        if (_intentLabel == null)
        {
            _intentLabel = new Label3D
            {
                Name = "IntentIndicator",
                Text = text,
                FontSize = 40,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Position = new Vector3(0f, 2.75f, 0f),
                Modulate = color
            };
            CallDeferred("add_child", _intentLabel);
        }
        else
        {
            _intentLabel.Visible = true;
            _intentLabel.Text = text;
            _intentLabel.Modulate = color;
        }
    }

    public void ClearIntentDisplay()
    {
        if (_intentLabel != null)
            _intentLabel.Visible = false;
    }

    // ── Chronomancer: action delay ───────────────────────────────

    /// <summary>
    /// Number of turns this unit's action is postponed.
    /// Decremented at the start of each enemy turn before ActEnemyUnit runs.
    /// </summary>
    public int PostponedTurns = 0;

    /// <summary>
    /// Adept Counterspell: when true, this unit's next action is cancelled outright
    /// (intent cleared, no reschedule). Consumed in RunEnemyTurn before postpone.
    /// </summary>
    public bool NegateNextAction = false;

    /// <summary>
    /// Adept Riposte: damage dealt back to any attacker that hits this unit until the
    /// start of the owner's next turn. Consumed per hit in PerformAttack /
    /// PerformRangedAttack; reset in StartPlayerTurn.
    /// </summary>
    public int RetaliateDamage = 0;

    /// <summary>
    /// Tile the Chronomancer has anchored for snap-back teleport.
    /// Set by SetAnchorEffect; cleared when AnchorTurnsRemaining reaches 0.
    /// </summary>
    public Vector2I? AnchorCoord = null;

    /// <summary>
    /// Turns remaining before the anchor expires.
    /// </summary>
    public int AnchorTurnsRemaining = 0;

    /// <summary>
    /// Tile this unit has been ordered to charge to via RedirectChargeEffect.
    /// Consumed by ActSoldier/ActBrute in CombatManager; cleared after use.
    /// </summary>
    public Vector2I? RedirectedChargeTile = null;

    /// <summary>
    /// True for temporal decoy units spawned by SummonDecoyLeafEffect.
    /// RedirectAuraPersistentEffect uses this flag to identify valid decoy targets.
    /// </summary>
    public bool IsDecoy = false;

    // ── Construct identity ──────────────────────────────────────────

    /// <summary>
    /// True for Tinker constructs. Drives auto-acting in the construct phase and "do I have a construct" queries.
    /// </summary>
    public bool IsConstruct = false;

    /// <summary>
    /// Turns of setup remaining before this construct can act. Decremented (and skipped) by the construct phase. 0 = ready.
    /// </summary>
    public int SetupTurnsRemaining = 0;

    /// <summary>
    /// True for emplacements (turrets, cannons, sentinels) that cannot reposition. They simply skip the activation if no target is in range.
    /// </summary>
    public bool IsImmobileConstruct = false;

    /// <summary>
    /// Set true by Overclock for a single construct phase — the construct fires/acts twice. Consumed and cleared by the phase.
    /// </summary>
    public bool ActsTwiceThisTurn = false;

    /// <summary>Grand Turret: fires a piercing line that hits every enemy along a chosen direction instead of a single target.</summary>
    public bool LineAttack = false;

    /// <summary>Colossus: deploys a free Drone on an adjacent empty tile at the end of each of its activations.</summary>
    public bool SpawnsDronesEachTurn = false;

    /// <summary>Colossus: detonates for AoE damage when destroyed.</summary>
    public bool DeathNova = false;

    // ── Aura: as a source ───────────────────────────────────────────
    /// <summary>Sentinel: armor granted to friendly units within AuraArmorRange each round. 0 = no armor aura.</summary>
    public int AuraArmor = 0;
    public int AuraArmorRange = 1;

    /// <summary>Lattice Node / Foundry: bonus attack damage granted to friendly constructs within AuraDamageRange each round. 0 = no damage aura.</summary>
    public int AuraDamage = 0;
    public int AuraDamageRange = 2;

    // ── Aura: as a recipient (so auras can be cleanly reapplied) ─────
    /// <summary>Armor this unit currently owes to aura sources. Subtracted before auras are recomputed so they never stack across rounds.</summary>
    public int AuraArmorReceived = 0;
    /// <summary>Attack damage this unit currently owes to aura sources. Subtracted before recompute.</summary>
    public int AuraDamageReceived = 0;

    // ── Heat / burnout (push-your-luck) ─────────────────────────────
    /// <summary>
    /// Current Heat. Each point adds +1 to this construct's attack. Raised only by opt-in actions (Overclock, Heat cards) — or passively when <see cref="PassiveHeat"/> is set (corrupted Unshackled Forge).
    /// </summary>
    public int Heat = 0;

    /// <summary>
    /// Heat at which this construct burns out (is destroyed) at the end of its activation. 0 = never burns out.
    /// </summary>
    public int BurnoutThreshold = 0;

    /// <summary>
    /// Corrupted-variant flag. When true the construct gains 1 Heat each time it acts, with no opt-in required — the safety governor is gone.
    /// </summary>
    public bool PassiveHeat = false;

    /// <summary>
    /// Raises Heat. Burnout itself is resolved by the construct phase, not here, so this never kills the unit directly.
    /// </summary>
    public void AddHeat(int amount)
    {
        if (amount <= 0 || !IsConstruct)
            return;
        Heat += amount;
        GD.Print($"{Name} Heat {Heat}/{BurnoutThreshold}.");
    }

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

    /// <summary>When set, this unit leaves a memorial of the given strength on death
    /// (mark_on_death_memorial — Unfinished Business "Last Words"). Checked in
    /// CombatManager.HandleUnitDeath before the haunted/necromancer branches.</summary>
    public MemorialStrength? LeaveMemorialOnDeath = null;

    /// <summary>Ghost Road (imbue_path_memorial phase:true): this unit's movement
    /// zone ignores blocked/occupied tiles for traversal this turn. Destinations
    /// must still be enterable. Cleared by the effect's turn-end cleanup.</summary>
    public bool IsPhasing = false;

    /// <summary>
    /// Cards the owning Necromancer draws when this spirit kills an enemy this turn.
    /// Set by mark_spirits_draw_on_kill; consumed by the spirit attack in
    /// AdvanceAllSpiritsEffect; reset (with CreateMemorialOnKill) at end of player turn.
    /// </summary>
    public int DrawOnKillCount = 0;
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

        AddToGroup("units");

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
        Stats.BonusMoveRange = 0;   // movespeed grants last one turn

        Stats.Mana = Stats.MaxMana;
        _healthBar?.SetMana(Stats.Mana, Stats.MaxMana);

        // Tick statuses first so expired ones don't affect this turn
        TickStatuses();

        // Action lockouts: frozen/stunned/bound zero AP so the unit can neither
        // act nor move (movement needs AP). rooted/slowed are NOT handled here —
        // they only restrict movement, which is now enforced read-side via
        // Unit.EffectiveMovement so a rooted unit keeps its AP for casting.
        if (HasStatus("frozen") || HasStatus("stunned") || HasStatus("bound"))
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

        // Tinker: one-shot wire traps fire before link-line zaps.
        TrapSystem.OnUnitEntered(this);
        ConduitLinkSystem.OnUnitEntered(this);
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
        if (pathCost < 0 || pathCost > EffectiveMoveRange)
            return false;


        TrySpendAP(1);
        PlaceOnTile(dest);
        return true;
    }

    public void ApplyDamage(int amount)
    {
        if (amount <= 0 || IsDeathQueued)
            return;

        if (!_skipLinkRedistribution)
        {
            // Redirector Field: shunt this hit to a designated construct.
            if (RedirectNextDamageTo != null)
            {
                var redirect = RedirectNextDamageTo;
                RedirectNextDamageTo = null;
                if (redirect.Stats.IsAlive && !redirect.IsDeathQueued)
                {
                    redirect.ApplyDamageSkippingLinks(amount);
                    RefreshHealthBar();
                    return;
                }
            }

            // Conduit Link redistribution (one hop; guarded against recursion).
            amount = ConduitLinkSystem.RedistributeFor(this, amount);
            if (amount <= 0)
            {
                RefreshHealthBar();
                return;
            }
        }

        // Shrouded: incoming hits are capped at 5 ("max 5 damage per hit").
        if (HasStatus("shrouded") && amount > 5)
        {
            GD.Print($"{Name} is Shrouded — {amount} damage capped to 5.");
            amount = 5;
        }

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
        {
            // Immortal (Eternal Flame): would-be-lethal damage leaves 1 HP.
            if (HasStatus("immortal") && remaining >= Stats.Health)
            {
                remaining = Math.Max(0, Stats.Health - 1);
                GD.Print($"{Name} is Immortal — survives at 1 HP.");
            }
            Stats.Health = Math.Max(0, Stats.Health - remaining);
        }

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

        // Apply status immediately.
        // rooted and slowed are enforced READ-SIDE in EffectiveMovement (reach → 0 /
        // halved) and must NOT touch AP here: rooted has to leave AP for casting, and
        // slowed already halves reach — also halving AP would double-nerf it (this was
        // a regression once EffectiveMovement took over the slow math).
        if (status == "frozen" || status == "bound")
            // frozen: can't act or move this turn. bound: same, plus immune to cleanse
            // (see CanBeFreed). Movement is separately zeroed via EffectiveMovement.
            CurrentActionPoints = 0;
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
        else if (status == "hasted")
        {
            // AP currency: an extra action this turn. (Wizard → extra move, since
            // casting is mana-gated; martial → extra move or attack.) Movement reach
            // is a separate currency and is NOT touched here.
            CurrentActionPoints += 1;
        }
        else if (status == "temporal_drag")
        {
            // Half movement — enforced read-side in EffectiveMovement (no MovePoints
            // write). The spells-cost+1 half still awaits enemy casting (R3 follow-on).
        }

        GD.Print($"{Name} gains {status} for {duration} turn(s).");
        _healthBar?.RefreshStatuses(Stats.StatusEffects);
    }

    public bool HasStatus(string status)
    {
        return Stats.StatusEffects.ContainsKey(status) && Stats.StatusEffects[status] > 0;
    }

    /// <summary>
    /// Attacker-side status modifiers on outgoing ATTACK damage (not spells).
    /// Blinded: the attack misses entirely (deterministic, per house preference).
    /// Weakened: −2 damage, floor 0. Called by PerformAttack/PerformRangedAttack
    /// and the spirit attack in AdvanceAllSpiritsEffect.
    /// </summary>
    public int ModifyOutgoingAttackDamage(int damage)
    {
        if (HasStatus("blinded"))
        {
            GD.Print($"{Name} is Blinded — the attack goes wide.");
            return 0;
        }
        if (HasStatus("weakened"))
            damage = Math.Max(0, damage - 2);
        return damage;
    }

    /// <summary>Iron/Fortress Colossus: enemies prefer attacking this unit when it is nearly as close as their natural target. Read by FindNearestPlayerUnit.</summary>
    public bool IsTaunting = false;

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

    /// <summary>
    /// Single source of truth for how far this unit may move right now, given a
    /// caller-supplied base budget. Every movement path (reachable-tile highlight,
    /// cost map, and the TryMoveTo commit) must route its base budget through here
    /// so movement-affecting statuses are honored consistently.
    ///
    /// Historically these statuses only wrote Stats.MovePoints, which no movement
    /// code read — so rooted/slowed/temporal_drag were inert. This centralizes the
    /// rule on the read side instead. NOTE: non-status movement modifiers (Dash /
    /// TempBuff / spirit grants) still write the dead MovePoints field and are NOT
    /// yet folded in here — that needs the per-turn-pool decision.
    /// </summary>
    public int EffectiveMovement(int baseBudget)
    {
        // Hard stops: no movement at all this turn.
        if (HasStatus("frozen") || HasStatus("stunned") || HasStatus("bound")
            || HasStatus("rooted"))
            return 0;

        // Movespeed currency: per-turn move-range grants from Dash-style self-move
        // spells (Stats.BonusMoveRange, reset each turn). This is the mobility-only
        // lever — it raises reach-per-move but grants no extra actions, so it never
        // hands a martial a free attack. AP (the action-count lever, e.g. `hasted`)
        // is deliberately NOT folded in here; the two currencies stay separate.
        int budget = baseBudget + Stats.BonusMoveRange;

        if (HasStatus("slowed") || HasStatus("temporal_drag"))
            budget = Math.Max(1, budget / 2);   // "half movement" halves grants too

        return Math.Max(0, budget);
    }

    /// <summary>The unit's status-adjusted per-move reach (base `MoveRange` + movespeed
    /// grants). This is the single per-move-reach value ALL movement paths read — the
    /// highlight (`GetReachableTiles`), the cost map (`GetReachableTilesWithCost`), the
    /// commit (`TryMoveTo`), and the SPD stat. `BaseSpeed` drives the AP count, not reach.</summary>
    public int EffectiveMoveRange => EffectiveMovement(MoveRange);

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
            CardSchool.Chronomancer => new FateAttunement(),
            CardSchool.Tinker => new TinkerAttunement(),
            CardSchool.Druid => new WildingAttunement(),
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