using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// PersistentEffect.cs
//
// Purpose:        Persistent effects — zones, auras, and other
//                 state-machines that live across turns and tick
//                 at the start of each player turn. Spawned by
//                 leaf effects (CreateMaelstromEffect,
//                 AvatarTransformEffect) and tracked on
//                 GameState.ActiveEffects.
// Layer:          Effects
// Collaborators:  GameState.cs (ActiveEffects list, Tick driver),
//                 CompositeEffects.cs (CreateMaelstromEffect and
//                 AvatarTransformEffect spawn instances here),
//                 Effect.cs (DealDamageEffect queries
//                 AvatarAuraEffect for the bonus damage stack),
//                 ElementalAttunement.cs (ElementTag mapping)
// See:            README §6 — Persistent Effects,
//                 README §6 — Elemental Attunement
// ============================================================

/// <summary>
/// Abstract base for any effect that ticks across turns. <see cref="Tick"/> is invoked
/// once per player turn by the combat loop; the implementation is responsible for
/// decrementing <see cref="TurnsRemaining"/>. The combat loop garbage-collects entries
/// where <see cref="IsExpired"/> is true.
/// </summary>
public abstract class PersistentEffect
{
    /// <summary>Turns this effect has left before it should be culled. Implementations decrement this in <see cref="Tick"/>.</summary>
    public int TurnsRemaining;

    /// <summary>The casting Entity. Used to determine team affiliation for friendly-fire filtering.</summary>
    public Entity Owner;

    /// <summary>Called once per player turn at start-of-turn. Implementation must decrement <see cref="TurnsRemaining"/>.</summary>
    public abstract void Tick(GameState s);

    /// <summary>Called after a spell is pushed to the stack but before its effects resolve. Use to set BonusDamage etc. Override in subclasses.</summary>
    public virtual void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets) { }
    /// <summary>Called after a spell's effects have fully resolved. Use for echoes, mana refunds, charge spending. Override in subclasses.</summary>
    public virtual void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets) { }

    /// <summary>True once <see cref="TurnsRemaining"/> reaches 0. The combat loop garbage-collects expired entries.</summary>
    public bool IsExpired => TurnsRemaining <= 0;
}

// ── Maelstrom Zone ──────────────────────────────────────────

/// <summary>
/// Rotating storm zone. Each tick: imbues every tile in radius with Lightning, deals
/// <see cref="Damage"/> to every enemy in radius, and pushes each surviving enemy one
/// tile in the current rotation direction (advances through the 6 hex directions over
/// successive ticks). When <see cref="Freezes"/> is set, also applies the frozen status.
/// </summary>
public class MaelstromEffect : PersistentEffect
{
    public Vector2I Center;
    public int Radius;
    public int Damage;
    public bool Freezes;

    // Track rotation direction (0-5, one of the 6 hex directions)
    private int _rotationStep = 0;

    private static readonly Vector2I[] HexDirs =
    {
        new Vector2I(1, 0),  new Vector2I(1, -1), new Vector2I(0, -1),
        new Vector2I(-1, 0), new Vector2I(-1, 1), new Vector2I(0, 1)
    };

    public MaelstromEffect(Vector2I center, int radius, int damage,
        int turns, Entity owner, bool freezes = false)
    {
        Center = center;
        Radius = radius;
        Damage = damage;
        TurnsRemaining = turns;
        Owner = owner;
        Freezes = freezes;
    }

    public override void Tick(GameState s)
    {
        if (s?.Grid == null)
            return;

        Unit ownerUnit = null;
        if (Owner == s.PlayerA)
            ownerUnit = s.PlayerUnit;
        else if (Owner == s.PlayerB)
            ownerUnit = s.EnemyUnit;

        // Get current rotation direction
        var rotDir = HexDirs[_rotationStep % 6];

        // Imbue all tiles in radius with storm
        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(Center, kvp.Key) > Radius)
                continue;
            var tile = kvp.Value;
            if (tile == null)
                continue;
            tile.ElementType = TileElementType.Lightning;
            tile.ElementStrength = 1.0f;
            s.Grid.ApplyVisualToTile(tile);
        }

        // Deal damage and push enemies clockwise
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !Godot.GodotObject.IsInstanceValid(unit))
                continue;
            if (!unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (ownerUnit != null && unit.TeamId == ownerUnit.TeamId)
                continue;

            int dist = s.Grid.Distance(Center, unit.CurrentTile.Axial);
            if (dist > Radius)
                continue;

            // Deal damage
            unit.ApplyDamage(Damage);
            s.Log($"[Maelstrom] {unit.Name} takes {Damage} damage.");

            // Re-check after damage — unit may have died and CurrentTile nulled
            if (!Godot.GodotObject.IsInstanceValid(unit) || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;

            // Push clockwise — find the neighbor in rotation direction
            var current = unit.CurrentTile.Axial;
            var pushTarget = current + rotDir;
            var pushTile = s.Grid.GetTile(pushTarget);

            if (pushTile != null && pushTile.CanEnter(unit))
            {
                unit.CurrentTile.ClearOccupant(unit);
                unit.PlaceOnTile(pushTile);
                s.Log($"[Maelstrom] {unit.Name} pushed clockwise.");
            }

            if (Freezes)
            {
                unit.ApplyStatus("frozen", 1);
                s.Log($"[Maelstrom] {unit.Name} frozen.");
            }
        }

        // Advance rotation
        _rotationStep = (_rotationStep + 1) % 6;
        TurnsRemaining--;

        s.Log($"[Maelstrom] Ticked. {TurnsRemaining} turns remaining.");
    }
}

// ── Avatar Aura ─────────────────────────────────────────────

/// <summary>
/// Spell-cast aura created by <c>AvatarTransformEffect</c>. While active, every spell cast by
/// the owner gets +<see cref="BonusDamage"/> (queried by <c>DealDamageEffect</c> via
/// <c>GameState.GetActiveEffect&lt;AvatarAuraEffect&gt;</c>), and <see cref="OnSpellCast"/>
/// random-imbues each spell's target tile.
/// </summary>
public class AvatarAuraEffect : PersistentEffect
{
    /// <summary>Bonus damage added to every spell cast while this aura is active.</summary>
    public int BonusDamage;

    private static readonly TileElementType[] Elements =
    {
        TileElementType.Fire, TileElementType.Frost,
        TileElementType.Lightning, TileElementType.Earth
    };
    private Random _rng = new();

    public AvatarAuraEffect(int turns, int bonusDamage, Entity owner)
    {
        TurnsRemaining = turns;
        BonusDamage = bonusDamage;
        Owner = owner;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        s.Log($"[Avatar] Aura ticking. {TurnsRemaining} turns remaining.");
    }

    /// <summary>Hook invoked by the combat runner after every successful spell resolution by the owner. Random-imbues each target tile and logs the bonus damage application.</summary>
    public override void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (s?.Grid == null || targets == null)
            return;

        // Random element imbue on target tile
        var element = Elements[_rng.Next(Elements.Length)];

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td)
                tile = td;
            else if (obj is HexTile tv)
                tile = s.Grid.GetTile(tv.Axial);
            else if (obj is Unit u && u.CurrentTile != null)
                tile = u.CurrentTile;

            if (tile != null)
            {
                tile.ElementType = element;
                tile.ElementStrength = 1.0f;
                if (element == TileElementType.Fire)
                    tile.IsHazardous = true;
                tile.TileView?.SetElement(element);
                s.Log($"[Avatar] Imbued {tile.Axial} with {element}.");
            }
        }

        s.Log($"[Avatar] Spell deals +{BonusDamage} bonus damage.");
    }
}

// ── Hollow Mantle ────────────────────────────────────────────

/// <summary>
/// Necromancer legendary aura. While active: spells cost 1 less mana
/// and the caster cannot be reduced below 1HP by any single hit.
/// Ticks down each turn; expired by the combat loop automatically.
/// </summary>
public class HollowMantleEffect : PersistentEffect
{
    public HollowMantleEffect(int turns, Entity owner)
    {
        TurnsRemaining = turns;
        Owner = owner;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        s.Log($"[HollowMantle] {TurnsRemaining} turn(s) remaining.");
    }

    /// <summary>
    /// Called by DealDamageEffect (or wherever damage is applied) to clamp
    /// incoming damage so the caster cannot drop below 1HP in a single hit.
    /// Wire this into Unit.ApplyDamage the same way AvatarAuraEffect.BonusDamage
    /// is queried by DealDamageEffect.
    /// </summary>
    public int ClampDamage(Unit target, int incomingDamage)
    {
        if (target.Stats.Health <= 1)
            return incomingDamage;
        return Math.Min(incomingDamage, target.Stats.Health - 1);
    }
}

// ── Open Gate ───────────────────────────────────────────────────────────

/// <summary>
/// Necromancer aura. While active: all enemies that die create a memorial
/// AND immediately summon a spirit on their death tile.
/// Wired into HandleUnitDeath in CombatManager — check for this effect
/// the same way DealDamageEffect checks for AvatarAuraEffect.
/// </summary>
public class OpenGateEffect : PersistentEffect
{
    public OpenGateEffect(int turns, Entity owner)
    {
        TurnsRemaining = turns;
        Owner = owner;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        s.Log($"[OpenGate] {TurnsRemaining} turn(s) remaining.");
    }
}

// ── Ossuary Aura ────────────────────────────────────────────────────────

/// <summary>
/// Necromancer structure aura. Each turn: heals all friendly spirits
/// within SpiritRegenRange by SpiritRegen HP.
/// Optionally: spirits that fall within range leave a memorial.
/// Optionally: adjacent memorials auto-rise a spirit each turn.
/// </summary>
public class OssUaryAuraEffect : PersistentEffect
{
    public int SpiritRegenRange;
    public int SpiritRegen;
    public int MemorialOnSpiritDeathRange; // 0 = disabled
    public int AutoRiseRange;              // 0 = disabled
    public int GriefPerTurn;              // 0 = disabled
    public Vector2I Center;               // set to ossuary tile position

    public OssUaryAuraEffect(int turns, Entity owner, Vector2I center,
        int regenRange = 2, int regen = 2,
        int memorialOnDeathRange = 0, int autoRiseRange = 0, int griefPerTurn = 0)
    {
        TurnsRemaining = turns;
        Owner = owner;
        Center = center;
        SpiritRegenRange = regenRange;
        SpiritRegen = regen;
        MemorialOnSpiritDeathRange = memorialOnDeathRange;
        AutoRiseRange = autoRiseRange;
        GriefPerTurn = griefPerTurn;
    }

    public override void Tick(GameState s)
    {
        if (s == null)
        { TurnsRemaining--; return; }

        // Heal spirits within range
        if (SpiritRegen > 0)
        {
            foreach (var unit in s.UnitsInPlay)
            {
                if (unit == null || !unit.IsSpirit || !unit.Stats.IsAlive)
                    continue;
                if (unit.CurrentTile == null)
                    continue;
                if (s.Grid?.Distance(Center, unit.CurrentTile.Axial) > SpiritRegenRange)
                    continue;

                unit.Stats.Health = Math.Min(unit.Stats.MaxHealth,
                    unit.Stats.Health + SpiritRegen);
                unit.RefreshHealthBar();
                s.Log($"[OssUaryAura] {unit.Name} heals {SpiritRegen}HP.");
            }
        }

        // Gain grief per turn (Soul Well variant)
        if (GriefPerTurn > 0)
        {
            Unit ownerUnit = s.UnitsInPlay.Find(u =>
                u != null && u.Attunement is GriefAttunement);
            if (ownerUnit?.Attunement is GriefAttunement grief)
                grief.GainCharges(GriefPerTurn);
        }

        TurnsRemaining--;
        s.Log($"[OssUaryAura] Ticked. {TurnsRemaining} turn(s) remaining.");
    }
}

// ── Memorial Seat Aura ──────────────────────────────────────────────────

/// <summary>
/// Necromancer structure aura. While active:
/// - All friendly spirits gain +SpritDmgBonus damage and +SpiritArmorBonus armor.
/// - Healing and release effects trigger twice (flag checked by relevant effects).
/// - Optionally: spirits within range regen HP each turn.
/// - Optionally: draw DrawPerTurn cards at start of each turn.
/// If the seat is destroyed, the caster takes DestroyDamage.
/// </summary>
public class MemorialSeatAuraEffect : PersistentEffect
{
    public int SpiritDmgBonus;
    public int SpiritArmorBonus;
    public int SpiritRegenRange;
    public int SpiritRegen;
    public int DrawPerTurn;
    public int DestroyDamage;

    public MemorialSeatAuraEffect(int turns, Entity owner,
        int spiritDmg = 2, int spiritArmor = 2,
        int regenRange = 0, int regen = 0,
        int drawPerTurn = 0, int destroyDamage = 8)
    {
        TurnsRemaining = turns;
        Owner = owner;
        SpiritDmgBonus = spiritDmg;
        SpiritArmorBonus = spiritArmor;
        SpiritRegenRange = regenRange;
        SpiritRegen = regen;
        DrawPerTurn = drawPerTurn;
        DestroyDamage = destroyDamage;
    }

    public override void Tick(GameState s)
    {
        if (s == null)
        { TurnsRemaining--; return; }

        // Regen spirits if configured
        if (SpiritRegen > 0 && SpiritRegenRange > 0)
        {
            Unit ownerUnit = s.ActiveCasterUnit ??
                s.UnitsInPlay.Find(u => u != null && u.Attunement is GriefAttunement);

            foreach (var unit in s.UnitsInPlay)
            {
                if (unit == null || !unit.IsSpirit || !unit.Stats.IsAlive)
                    continue;
                if (ownerUnit?.CurrentTile == null || unit.CurrentTile == null)
                    continue;
                if (s.Grid?.Distance(ownerUnit.CurrentTile.Axial,
                    unit.CurrentTile.Axial) > SpiritRegenRange)
                    continue;

                unit.Stats.Health = Math.Min(unit.Stats.MaxHealth,
                    unit.Stats.Health + SpiritRegen);
                unit.RefreshHealthBar();
            }
        }

        // Draw cards if configured
        if (DrawPerTurn > 0)
        {
            Unit ownerUnit = s.UnitsInPlay.Find(u =>
                u != null && u.Attunement is GriefAttunement);
            if (ownerUnit?.DeckData != null)
            {
                ownerUnit.DeckData.Draw(DrawPerTurn);
                s.OnDrawCards?.Invoke(ownerUnit);
                s.Log($"[MemorialSeatAura] Drew {DrawPerTurn} card(s).");
            }
        }

        TurnsRemaining--;
        s.Log($"[MemorialSeatAura] Ticked. {TurnsRemaining} turn(s) remaining.");
    }

    // Called by buff system each turn to apply spirit bonuses
    public void ApplySpiritBuffs(GameState s, int ownerTeam)
    {
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.IsSpirit || !unit.Stats.IsAlive)
                continue;
            if (unit.SummonerTeamId != ownerTeam)
                continue;
            unit.AttackDamage += SpiritDmgBonus;
            unit.Stats.Armor += SpiritArmorBonus;
        }
    }
}

// ── Hallowed Double Rise ─────────────────────────────────────────────────

/// <summary>
/// Necromancer permanent aura. While active: any unit that dies on a
/// hallowed tile summons 2 spirits instead of 1.
/// Optionally: each kill on hallowed ground grants all spirits +1 DMG.
/// Lasts for the rest of combat (TurnsRemaining = 999).
/// Wired into HandleUnitDeath in CombatManager.
/// </summary>
public class HallowedDoubleRiseEffect : PersistentEffect
{
    public bool EmpowerOnKill; // grants spirits +1 DMG per kill on hallowed ground

    public HallowedDoubleRiseEffect(Entity owner, bool empowerOnKill = false)
    {
        TurnsRemaining = 999; // permanent for this combat
        Owner = owner;
        EmpowerOnKill = empowerOnKill;
    }

    public override void Tick(GameState s)
    {
        // Permanent — don't decrement
        s.Log("[HallowedDoubleRise] Active.");
    }
}

// ── Elder Aura ──────────────────────────────────────────────────────────

/// <summary>
/// Necromancer revenant aura. While the Elder stands:
/// - All friendly spirits within SpiritBuffRange gain +SpiritDmgBonus damage.
/// - Optionally: memorials cannot be consumed by enemy effects.
/// Ticks down each turn.
/// </summary>
public class ElderAuraEffect : PersistentEffect
{
    public int SpiritDmgBonus;
    public int SpiritBuffRange;
    public bool ProtectMemorials;

    public ElderAuraEffect(int turns, Entity owner,
        int spiritDmg = 2, int spiritRange = 3, bool protectMemorials = false)
    {
        TurnsRemaining = turns;
        Owner = owner;
        SpiritDmgBonus = spiritDmg;
        SpiritBuffRange = spiritRange;
        ProtectMemorials = protectMemorials;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        s.Log($"[ElderAura] {TurnsRemaining} turn(s) remaining.");
    }
}

/// <summary>Active modifier that applies a bonus to the next N spells the owner casts.
/// Fires via OnSpellCast (sets BonusDamage) and OnSpellResolved (clears it, draws, counts down).</summary>
public sealed class QueuedSpellModifier : PersistentEffect
{
    public int BonusDamage;
    public int ExtraDraw;
    public int AppliesTo;
    public string GrantStatus;
    public int GrantStatusDuration;
    public Unit OwnerUnit;

    public QueuedSpellModifier(int bonusDmg, int extraDraw, int appliesTo,
        string grantStatus, int statusDur, Entity owner, Unit ownerUnit)
    {
        BonusDamage = bonusDmg;
        ExtraDraw = extraDraw;
        AppliesTo = Math.Max(1, appliesTo);
        GrantStatus = grantStatus;
        GrantStatusDuration = statusDur;
        Owner = owner;
        OwnerUnit = ownerUnit;
        TurnsRemaining = appliesTo + 4; // safety expiry even if never triggered
    }

    public override void Tick(GameState s) { TurnsRemaining--; }

    public override void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (casterUnit != OwnerUnit || AppliesTo <= 0)
            return;
        if (BonusDamage > 0)
            casterUnit.BonusSpellDamage += BonusDamage;
        if (!string.IsNullOrEmpty(GrantStatus))
            casterUnit.ApplyStatus(GrantStatus, GrantStatusDuration);
        s.Log($"[QueuedModifier] Applied +{BonusDamage} dmg to this spell.");
    }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (casterUnit != OwnerUnit || AppliesTo <= 0)
            return;
        // Remove the bonus so it does not carry to the next spell
        if (BonusDamage > 0)
            casterUnit.BonusSpellDamage -= BonusDamage;
        if (ExtraDraw > 0)
            casterUnit.DeckData?.Draw(ExtraDraw);
        AppliesTo--;
        if (AppliesTo <= 0)
            TurnsRemaining = 0;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  CHARGE COST MODIFIER
//  While active: after each spell resolves, refund its mana cost and spend
//  an equivalent amount of Arcane Charge instead.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ChargeCostModifierAura : PersistentEffect
{
    public int ChargePerMana;
    public Unit OwnerUnit;

    public ChargeCostModifierAura(int chargePerMana, int turns, Entity owner, Unit ownerUnit)
    {
        ChargePerMana = Math.Max(1, chargePerMana);
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s) { TurnsRemaining--; }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (casterUnit != OwnerUnit)
            return;
        if (casterUnit.Attunement is not ArcaneAttunement arc)
            return;

        int manaCost = CastModifierHelpers.ReadManaCost(s.LastResolvedItem);
        if (manaCost <= 0)
            return;

        int chargesNeeded = manaCost * ChargePerMana;
        if (arc.Charge < chargesNeeded)
        {
            s.Log($"[ChargeCostModifier] Not enough charge ({arc.Charge} < {chargesNeeded}) — mana stays spent.");
            return;
        }

        casterUnit.GainMana(manaCost);
        if (s.Mana.ContainsKey(Owner))
            s.Mana[Owner] = casterUnit.Stats.Mana;
        arc.Spend(chargesNeeded);
        s.Log($"[ChargeCostModifier] Refunded {manaCost} mana; spent {chargesNeeded} charge.");
    }
}


// ─────────────────────────────────────────────────────────────────────────────
//  OMNISCIENCE
//  All your spells are free for N turns. When it expires, exile cards from hand.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class OmniscienceEffect : PersistentEffect
{
    public int ExileOnExpire;
    public Unit OwnerUnit;

    public OmniscienceEffect(int turns, int exileOnExpire, Entity owner, Unit ownerUnit)
    {
        TurnsRemaining = turns;
        ExileOnExpire = exileOnExpire;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (TurnsRemaining <= 0)
            ExileHand(s);
    }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (casterUnit != OwnerUnit)
            return;
        int manaCost = CastModifierHelpers.ReadManaCost(s.LastResolvedItem);
        if (manaCost <= 0)
            return;
        casterUnit.GainMana(manaCost);
        if (s.Mana.ContainsKey(Owner))
            s.Mana[Owner] = casterUnit.Stats.Mana;
        s.Log($"[Omniscience] Refunded {manaCost} mana — spell was free.");
    }

    private void ExileHand(GameState s)
    {
        if (OwnerUnit?.DeckData == null || ExileOnExpire <= 0)
            return;
        int n = Math.Min(ExileOnExpire, OwnerUnit.DeckData.Hand.Count);
        if (n > 0)
            OwnerUnit.DeckData.Hand.RemoveRange(0, n);
        s.Log($"[Omniscience] Expired — {n} card(s) exiled as the price of godhood.");
    }
}


// ─────────────────────────────────────────────────────────────────────────────
//  ARCANE APOTHEOSIS
//  Permanent passive — every spell cast generates Arcane Charge.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ArcaneApotheosisAura : PersistentEffect
{
    public int ChargePerSpell;
    public Unit OwnerUnit;

    public ArcaneApotheosisAura(int chargePerSpell, Entity owner, Unit ownerUnit)
    {
        ChargePerSpell = Math.Max(1, chargePerSpell);
        Owner = owner;
        OwnerUnit = ownerUnit;
        TurnsRemaining = int.MaxValue; // never expires — legendary passive
    }

    public override void Tick(GameState s) { /* permanent — intentionally no decrement */ }

    public override void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (casterUnit != OwnerUnit)
            return;
        if (casterUnit.Attunement is ArcaneAttunement arc)
        {
            arc.Add(ChargePerSpell);
            s.Log($"[ArcaneApotheosis] +{ChargePerSpell} charge from apotheosis (now {arc.Charge}).");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  BIND CARD
//  Exile a card from hand — it auto-casts its top half at the start of each turn.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class BoundCardAura : PersistentEffect
{
    public Card BoundCard;
    public Unit OwnerUnit;

    public BoundCardAura(Card card, int turns, Entity owner, Unit ownerUnit)
    {
        BoundCard = card;
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (BoundCard?.TopHalf?.Effects == null || OwnerUnit == null)
            return;

        // Auto-cast the bound card's top half against the owner (self-cast; no targeting)
        var selfTargets = new TargetSet();
        if (selfTargets.Items == null)
            selfTargets.Items = new List<object>();
        selfTargets.Items.Add(OwnerUnit);

        foreach (var eff in BoundCard.TopHalf.Effects)
            eff.Resolve(s, Owner, selfTargets, null);

        s.Log($"[BindCard] Bound '{BoundCard.CardName}' auto-casts. ({TurnsRemaining} turn(s) remaining)");
    }
}


// ─────────────────────────────────────────────────────────────────────────────
//  REPLICATE LAST SPELL
//  After your next spell fully resolves, replay all its effects once more.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ReplicateSpellAura : PersistentEffect
{
    public Unit OwnerUnit;
    private bool _triggered;

    public ReplicateSpellAura(Entity owner, Unit ownerUnit)
    {
        Owner = owner;
        OwnerUnit = ownerUnit;
        TurnsRemaining = 4; // safety — expires even if never triggered
    }

    public override void Tick(GameState s) { TurnsRemaining--; }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (_triggered || casterUnit != OwnerUnit)
            return;

        var item = s.LastResolvedItem;
        if (item == null || item.Caster != Owner)
            return;

        _triggered = true;
        TurnsRemaining = 0; // consume

        s.Log("[ReplicateSpell] Echoing last spell...");
        foreach (var eff in item.Ability.Effects)
            eff.Resolve(s, item.Caster, item.Targets, item.Snapshot);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  CONVERGENCE
//  Each spell cast pulses bonus damage to the nearest enemy within range.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ConvergenceAura : PersistentEffect
{
    public int Damage, Range;
    public Unit OwnerUnit;

    public ConvergenceAura(int damage, int range, int turns, Entity owner, Unit ownerUnit)
    {
        Damage = damage;
        Range = range;
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s) { TurnsRemaining--; }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (casterUnit != OwnerUnit || s?.Grid == null || casterUnit.CurrentTile == null)
            return;

        Unit nearest = null;
        int bestD = int.MaxValue;
        foreach (var u in s.UnitsInPlay)
        {
            if (u == null || !u.Stats.IsAlive || u.TeamId == casterUnit.TeamId || u.CurrentTile == null)
                continue;
            int d = s.Grid.Distance(casterUnit.CurrentTile.Axial, u.CurrentTile.Axial);
            if (d <= Range && d < bestD)
            { bestD = d; nearest = u; }
        }

        if (nearest != null)
        {
            nearest.ApplyDamage(Damage);
            s.Log($"[Convergence] {nearest.Name} takes {Damage} from convergence pulse.");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  DOMINATE
//  Dominated enemies attack their own allies each turn.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// While active, finds all enemies with "dominated" status and forces each to
/// deal its AttackDamage to its nearest ally at start of turn.
/// Full AI control (commanding the dominated unit's actions from the player UI)
/// is a deeper engine feature — this implements the "hurts own team" half.
/// </summary>
public sealed class DominateAura : PersistentEffect
{
    public Unit OwnerUnit;

    public DominateAura(int turns, Entity owner, Unit ownerUnit)
    {
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (s?.Grid == null || OwnerUnit == null)
            return;

        foreach (var unit in s.UnitsInPlay.ToList())
        {
            if (unit == null || !unit.Stats.IsAlive || !unit.HasStatus("dominated"))
                continue;
            if (unit.TeamId == OwnerUnit.TeamId)
                continue; // already on our side — skip

            // Find the nearest unit on the dominated unit's OWN team to attack
            Unit target = null;
            int bestD = int.MaxValue;
            foreach (var ally in s.UnitsInPlay)
            {
                if (ally == null || !ally.Stats.IsAlive || ally.CurrentTile == null)
                    continue;
                if (ally.TeamId != unit.TeamId || ally == unit)
                    continue;
                int d = s.Grid.Distance(unit.CurrentTile?.Axial ?? default, ally.CurrentTile.Axial);
                if (d < bestD)
                { bestD = d; target = ally; }
            }

            if (target != null)
            {
                target.ApplyDamage(unit.AttackDamage);
                s.Log($"[Dominate] {unit.Name} attacks own ally {target.Name} for {unit.AttackDamage}.");
            }
        }
    }
}

public sealed class GrandDesignPersistentEffect : PersistentEffect
{
    public Unit OwnerUnit;

    public GrandDesignPersistentEffect(int turns, Entity owner, Unit ownerUnit)
    {
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s) { TurnsRemaining--; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  ABSOLUTE TERRITORY
//  A persistent zone centred on the caster's position at cast time.
//  Each turn: all enemies in the zone take damage.
//  Movement gating (enemies cannot exit) needs a CanMove hook in the movement
//  system — that is a separate feature noted here.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AbsoluteTerritoryZone : PersistentEffect
{
    public Vector2I Center;
    public int Radius, DamagePerTurn;
    public Unit OwnerUnit;

    public AbsoluteTerritoryZone(Vector2I center, int radius, int dpt, int turns,
        Entity owner, Unit ownerUnit)
    {
        Center = center;
        Radius = radius;
        DamagePerTurn = dpt;
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (s?.Grid == null || OwnerUnit == null)
            return;

        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (unit.TeamId == OwnerUnit.TeamId)
                continue; // spare allies
            if (s.Grid.Distance(Center, unit.CurrentTile.Axial) > Radius)
                continue;

            unit.ApplyDamage(DamagePerTurn);
            s.Log($"[AbsoluteTerritory] {unit.Name} takes {DamagePerTurn} inside the zone.");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Temporal Decay Field
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Board-wide persistent effect. Each tick: deals <see cref="DamagePerTick"/>
/// to every unit on the board (or enemies only when <see cref="EnemiesOnly"/> is set).
/// Also increments <see cref="CurrentScalingBonus"/> by <see cref="ScalingPerTick"/>
/// each turn — the caster's DealDamageEffect reads this via
/// <c>GameState.GetActiveEffect&lt;TemporalDecayFieldPersistentEffect&gt;</c>
/// and adds it to all spell damage.
///
/// Permanent for the fight (TurnsRemaining = 999).
/// Spawned by <see cref="TemporalDecayFieldLeafEffect"/>.
/// </summary>
public class TemporalDecayFieldPersistentEffect : PersistentEffect
{
    public int DamagePerTick;
    public int ScalingPerTick;
    public bool EnemiesOnly;

    /// <summary>Running total added to all caster spell damage.</summary>
    public int CurrentScalingBonus = 0;

    private int _ownerTeamId = -1;

    public TemporalDecayFieldPersistentEffect(int damage, int scaling,
        Entity owner, bool enemiesOnly = false)
    {
        DamagePerTick = damage;
        ScalingPerTick = scaling;
        Owner = owner;
        EnemiesOnly = enemiesOnly;
        TurnsRemaining = 999; // permanent for this combat
    }

    public void SetOwnerTeamId(int teamId) => _ownerTeamId = teamId;

    public override void Tick(GameState s)
    {
        if (s?.UnitsInPlay == null)
            return;

        foreach (var unit in s.UnitsInPlay.ToList())
        {
            if (unit == null || !unit.Stats.IsAlive)
                continue;
            if (EnemiesOnly && _ownerTeamId >= 0 && unit.TeamId == _ownerTeamId)
                continue;

            unit.ApplyDamage(DamagePerTick);
            s.Log($"[TemporalDecay] {unit.Name} takes {DamagePerTick}.");
        }

        CurrentScalingBonus += ScalingPerTick;
        s.Log($"[TemporalDecay] Spell scaling now +{CurrentScalingBonus}.");

        // TurnsRemaining never reaches 0 — effect persists until combat ends.
        // CombatManager prunes dead-unit effects; this one only expires via
        // combat end or an explicit removal effect.
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  EXTRA TURN
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Grants the Chronomancer a second turn each round with limited resources.
/// Stored on <c>GameState.ActiveEffects</c>.
///
/// CombatManager reads <see cref="HasExtraTurn"/> in <c>EndPlayerTurn</c>:
/// if true and the extra turn hasn't fired this round, it runs a second
/// <c>StartPlayerTurn</c> before the enemy acts.
///
/// Set <see cref="ExtraTurnFiredThisRound"/> = true at the top of
/// <c>StartPlayerTurn</c> when entering the extra turn, and reset it
/// at the top of each new round (i.e. before every non-extra StartPlayerTurn).
/// </summary>
public class ExtraTurnPersistentEffect : PersistentEffect
{
    public int ExtraMana;
    public int ExtraDraw;

    /// <summary>True during the extra-turn window so CombatManager doesn't loop.</summary>
    public bool ExtraTurnFiredThisRound = false;

    public ExtraTurnPersistentEffect(int mana, int draw, Entity owner)
    {
        ExtraMana = mana;
        ExtraDraw = draw;
        TurnsRemaining = 999;
        Owner = owner;
    }

    public override void Tick(GameState s)
    {
        // Permanent — never expire.
        // ExtraTurnFiredThisRound is reset by CombatManager each new round.
    }

    /// <summary>True when the extra turn should fire this round.</summary>
    public bool HasExtraTurn => !ExtraTurnFiredThisRound;
}

// ─────────────────────────────────────────────────────────────────────────────
//  REDIRECT AURA
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Aura that forces enemy single-target actions within <see cref="Radius"/>
/// to target the nearest friendly decoy unit instead of their chosen target.
///
/// CombatManager reads this in <c>FindNearestPlayerUnit</c>: if a redirect
/// aura is active and a live decoy exists within <see cref="Radius"/> of the
/// attacker, return the decoy instead of the actual nearest player unit.
///
/// Spawned by <see cref="RedirectAuraLeafEffect"/> alongside a decoy unit.
/// </summary>
public class RedirectAuraPersistentEffect : PersistentEffect
{
    public int Radius;
    public Vector2I AuraCenter; // updated each turn to the owning unit's position

    public RedirectAuraPersistentEffect(int radius, int turns, Entity owner, Vector2I center)
    {
        Radius = radius;
        TurnsRemaining = turns;
        Owner = owner;
        AuraCenter = center;
    }

    public override void Tick(GameState s)
    {
        // Update center to the owner unit's current position (if it moved)
        var ownerUnit = s.UnitsInPlay.Find(u => u != null && u.Name == Owner?.Name);
        if (ownerUnit?.CurrentTile != null)
            AuraCenter = ownerUnit.CurrentTile.Axial;

        TurnsRemaining--;
        s.Log($"[RedirectAura] Active. {TurnsRemaining} turn(s) remaining.");
    }

    /// <summary>
    /// Returns the closest live decoy unit within range of the attacker's position,
    /// or null if none qualifies. Call from CombatManager.FindNearestPlayerUnit.
    /// </summary>
    public Unit FindDecoyTarget(GameState s, Vector2I attackerCoord)
    {
        if (s?.Grid == null)
            return null;

        Unit best = null;
        int bestDist = int.MaxValue;

        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || !unit.IsDecoy)
                continue;
            if (unit.CurrentTile == null)
                continue;

            int dist = s.Grid.Distance(attackerCoord, unit.CurrentTile.Axial);
            if (dist <= Radius && dist < bestDist)
            {
                bestDist = dist;
                best = unit;
            }
        }

        return best;
    }
}

/// <summary>Multi-turn movement buff, tracked as a PersistentEffect.</summary>
public class MovementBuffEffect : PersistentEffect
{
    private readonly Unit _unit;
    private readonly int _amount;

    public MovementBuffEffect(Unit unit, int amount, int turns)
    {
        _unit = unit;
        _amount = amount;
        TurnsRemaining = turns;
        Owner = null; // not owner-keyed
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (TurnsRemaining <= 0 && _unit != null && Godot.GodotObject.IsInstanceValid(_unit))
        {
            _unit.Stats.MovePoints = Math.Max(0, _unit.Stats.MovePoints - _amount);
            _unit.RefreshHealthBar();
            s.Log($"[MovementBuff] Expired on {_unit.Name}.");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  SHARED HELPER
// ─────────────────────────────────────────────────────────────────────────────

internal static class CastModifierHelpers
{
    /// <summary>Reads the mana cost from a resolved StackItem's cost list. Returns 0 on failure.</summary>
    internal static int ReadManaCost(StackItem item)
    {
        if (item?.Ability?.Costs == null)
            return 0;
        int total = 0;
        foreach (var c in item.Ability.Costs)
            if (c is ManaCost mc)
                total += mc.Amount;
        return total;
    }
}