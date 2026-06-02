using Godot;
using System;
using System.Collections.Generic;

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
        if (s?.Grid == null) return;

        Unit ownerUnit = null;
        if (Owner == s.PlayerA) ownerUnit = s.PlayerUnit;
        else if (Owner == s.PlayerB) ownerUnit = s.EnemyUnit;

        // Get current rotation direction
        var rotDir = HexDirs[_rotationStep % 6];

        // Imbue all tiles in radius with storm
        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(Center, kvp.Key) > Radius) continue;
            var tile = kvp.Value;
            if (tile == null) continue;
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
            if (ownerUnit != null && unit.TeamId == ownerUnit.TeamId) continue;

            int dist = s.Grid.Distance(Center, unit.CurrentTile.Axial);
            if (dist > Radius) continue;

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
    public void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (s?.Grid == null || targets == null) return;

        // Random element imbue on target tile
        var element = Elements[_rng.Next(Elements.Length)];

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td) tile = td;
            else if (obj is HexTile tv) tile = s.Grid.GetTile(tv.Axial);
            else if (obj is Unit u && u.CurrentTile != null) tile = u.CurrentTile;

            if (tile != null)
            {
                tile.ElementType = element;
                tile.ElementStrength = 1.0f;
                if (element == TileElementType.Fire) tile.IsHazardous = true;
                tile.TileView?.SetElement(element);
                s.Log($"[Avatar] Imbued {tile.Axial} with {element}.");
            }
        }

        s.Log($"[Avatar] Spell deals +{BonusDamage} bonus damage.");
    }
}

// In PersistentEffect.cs — add after AvatarAuraEffect

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
        if (target.Stats.Health <= 1) return incomingDamage;
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
        if (s == null) { TurnsRemaining--; return; }

        // Heal spirits within range
        if (SpiritRegen > 0)
        {
            foreach (var unit in s.UnitsInPlay)
            {
                if (unit == null || !unit.IsSpirit || !unit.Stats.IsAlive) continue;
                if (unit.CurrentTile == null) continue;
                if (s.Grid?.Distance(Center, unit.CurrentTile.Axial) > SpiritRegenRange) continue;

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
        if (s == null) { TurnsRemaining--; return; }

        // Regen spirits if configured
        if (SpiritRegen > 0 && SpiritRegenRange > 0)
        {
            Unit ownerUnit = s.ActiveCasterUnit ??
                s.UnitsInPlay.Find(u => u != null && u.Attunement is GriefAttunement);

            foreach (var unit in s.UnitsInPlay)
            {
                if (unit == null || !unit.IsSpirit || !unit.Stats.IsAlive) continue;
                if (ownerUnit?.CurrentTile == null || unit.CurrentTile == null) continue;
                if (s.Grid?.Distance(ownerUnit.CurrentTile.Axial,
                    unit.CurrentTile.Axial) > SpiritRegenRange) continue;

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
            if (unit == null || !unit.IsSpirit || !unit.Stats.IsAlive) continue;
            if (unit.SummonerTeamId != ownerTeam) continue;
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


// ════════════════════════════════════════════════════════════════════════
// LEAF EFFECT CLASSES — paste into CompositeEffects.cs
// ════════════════════════════════════════════════════════════════════════

// ── Open Gate Leaf ──────────────────────────────────────────────────────

/// <summary>
/// Registers an OpenGateEffect on GameState.ActiveEffects.
/// CombatManager.HandleUnitDeath checks for this effect and creates
/// a memorial + summons a spirit when it is active.
/// JSON: { "type": "open_gate", "turns": n }
/// </summary>
public sealed class OpenGateLeafEffect : EffectBase
{
    public int Turns;
    public OpenGateLeafEffect(int turns) { Turns = turns; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new OpenGateEffect(Turns, caster));
        s.Log($"[OpenGate] Gate opened for {Turns} turns.");
    }
}

// ── Ossuary Aura Leaf ────────────────────────────────────────────────────

/// <summary>
/// Reads the position of the most recently summoned spirit/ossuary unit
/// and registers an OssUaryAuraEffect centered on it.
/// JSON: { "type": "ossuary_aura", "spirit_regen": n, "spirit_regen_range": n }
/// </summary>
public sealed class OssUaryAuraLeafEffect : EffectBase
{
    public int Turns;
    public int SpiritRegen;
    public int SpiritRegenRange;
    public int MemorialOnDeathRange;
    public int AutoRiseRange;
    public int GriefPerTurn;

    public OssUaryAuraLeafEffect(int turns, int regen, int regenRange,
        int memorialOnDeathRange = 0, int autoRiseRange = 0, int griefPerTurn = 0)
    {
        Turns = turns;
        SpiritRegen = regen;
        SpiritRegenRange = regenRange;
        MemorialOnDeathRange = memorialOnDeathRange;
        AutoRiseRange = autoRiseRange;
        GriefPerTurn = griefPerTurn;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = s.ActiveCasterUnit;
        Vector2I center = casterUnit?.CurrentTile?.Axial ?? default;

        // Use the target tile if available (the ossuary was just placed there)
        if (targets?.Items?.Count > 0)
        {
            foreach (var obj in targets.Items)
            {
                if (obj is TileData td) { center = td.Axial; break; }
                if (obj is Unit u && u.CurrentTile != null) { center = u.CurrentTile.Axial; break; }
            }
        }

        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new OssUaryAuraEffect(
            Turns, caster, center,
            SpiritRegenRange, SpiritRegen,
            MemorialOnDeathRange, AutoRiseRange, GriefPerTurn));

        s.Log($"[OssUaryAura] Ossuary aura active at {center} for {Turns} turns.");
    }
}

// ── Memorial Seat Aura Leaf ──────────────────────────────────────────────

/// <summary>
/// Registers a MemorialSeatAuraEffect on GameState.ActiveEffects.
/// JSON: { "type": "memorial_seat_aura" }
/// </summary>
public sealed class MemorialSeatAuraLeafEffect : EffectBase
{
    public int Turns;
    public int SpiritDmg;
    public int SpiritArmor;
    public int RegenRange;
    public int Regen;
    public int DrawPerTurn;

    public MemorialSeatAuraLeafEffect(int turns, int spiritDmg = 2, int spiritArmor = 2,
        int regenRange = 0, int regen = 0, int drawPerTurn = 0)
    {
        Turns = turns;
        SpiritDmg = spiritDmg;
        SpiritArmor = spiritArmor;
        RegenRange = regenRange;
        Regen = regen;
        DrawPerTurn = drawPerTurn;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new MemorialSeatAuraEffect(
            Turns, caster, SpiritDmg, SpiritArmor, RegenRange, Regen, DrawPerTurn));
        s.Log($"[MemorialSeatAura] Active for {Turns} turns.");
    }
}

// ── Hallowed Double Rise Leaf ────────────────────────────────────────────

/// <summary>
/// Registers a HallowedDoubleRiseEffect on GameState.ActiveEffects.
/// JSON: { "type": "hallowed_double_rise" }
/// </summary>
public sealed class HallowedDoubleRiseLeafEffect : EffectBase
{
    public bool EmpowerOnKill;
    public HallowedDoubleRiseLeafEffect(bool empowerOnKill = false)
    {
        EmpowerOnKill = empowerOnKill;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new HallowedDoubleRiseEffect(caster, EmpowerOnKill));
        s.Log($"[HallowedDoubleRise] Active — deaths on hallowed ground summon 2 spirits.");
    }
}

// ── Elder Aura Leaf ──────────────────────────────────────────────────────

/// <summary>
/// Registers an ElderAuraEffect on GameState.ActiveEffects.
/// JSON: { "type": "elder_aura", "spirit_buff_damage": n, "spirit_buff_range": n }
/// </summary>
public sealed class ElderAuraLeafEffect : EffectBase
{
    public int Turns;
    public int SpiritDmg;
    public int SpiritRange;
    public bool ProtectMemorials;

    public ElderAuraLeafEffect(int turns, int spiritDmg = 2,
        int spiritRange = 3, bool protectMemorials = false)
    {
        Turns = turns;
        SpiritDmg = spiritDmg;
        SpiritRange = spiritRange;
        ProtectMemorials = protectMemorials;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new ElderAuraEffect(
            Turns, caster, SpiritDmg, SpiritRange, ProtectMemorials));
        s.Log($"[ElderAura] Active for {Turns} turns. Spirits +{SpiritDmg} DMG within range {SpiritRange}.");
    }
}

