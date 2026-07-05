using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// NecromancerEffects.cs
//
// Purpose:        Necromancer school effects — spirits, memorials, grief, hallowed
//                 ground, and their persistent auras.
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase, core leaves),
//                 PersistentEffect.cs (PersistentEffect base),
//                 CardScriptRegistry.Necromancer.cs (registration)
// Notes:          Extracted from Effect.cs / CompositeEffects.cs /
//                 PersistentEffect.cs — pure move, no behavior change.
// ============================================================

/// <summary>
/// Summons a spirit unit on a memorial tile. Marks the unit as IsSpirit,
/// applies spirit appearance, and consumes the memorial it rises from.
/// JSON: { "type": "summon_spirit", "unit": "Spirit", "hp": 10, "damage": 5, "speed": 1 }
/// </summary>
public sealed class SummonSpiritEffect : EffectBase
{
	public string UnitKind;
	public int HP, Damage, Speed;
	public bool OnDeathMemorial;

	public SummonSpiritEffect(string kind, int hp, int damage, int speed, bool onDeathMemorial = false)
	{
		UnitKind = kind;
		HP = hp;
		Damage = damage;
		Speed = speed;
		OnDeathMemorial = onDeathMemorial;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{
			s.Log("[SummonSpirit] No summon handler registered.");
			return;
		}

		var casterUnit = s.ActiveCasterUnit;
		int ownerTeam = casterUnit?.TeamId ?? 0;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null || !tile.HasMemorial)
			{
				s.Log("[SummonSpirit] Target tile has no memorial — cannot summon here.");
				continue;
			}

			// If the memorial tile is occupied, try to find an adjacent empty tile.
			// HACK: Caster or another unit standing on the memorial would cause the
			// summon handler to place the spirit at an undefined position (observed: (1,1)).
			TileData spawnTile = tile;
			if (tile.IsOccupied && s.Grid != null)
			{
				spawnTile = null;
				foreach (var neighborCoord in s.Grid.GetNeighbors(tile.Axial))
				{
					var neighbor = s.Grid.GetTile(neighborCoord);
					if (neighbor != null && neighbor.IsWalkable && !neighbor.IsBlocked && !neighbor.IsOccupied)
					{
						spawnTile = neighbor;
						break;
					}
				}

				if (spawnTile == null)
				{
					s.Log($"[SummonSpirit] Memorial at {tile.Axial} is occupied and no adjacent tile is free — summon blocked.");
					continue;
				}

				s.Log($"[SummonSpirit] Memorial tile {tile.Axial} occupied; placing spirit at adjacent tile {spawnTile.Axial}.");
			}

			string sourceName = tile.Memorial?.SourceName ?? "Unknown";

			var spirit = s.OnSummonRequested(UnitKind, spawnTile, ownerTeam);
			if (spirit == null)
				continue;

			spirit.IsSpirit = true;
			spirit.SummonerTeamId = ownerTeam;
			spirit.Stats.MaxHealth = HP;
			spirit.Stats.Health = HP;
			spirit.Stats.BaseSpeed = Speed;
			spirit.AttackDamage = Damage;
			spirit.OnDeathMemorial = OnDeathMemorial;
			spirit.ApplySpiritAppearance();

			s.Memorials?.ConsumeMemorial(tile);
			s.Log($"[SummonSpirit] {sourceName} answers the call as {UnitKind} at {spawnTile.Axial}.");
		}
	}
}

/// <summary>
/// Summons a spirit from every memorial on the board simultaneously.
/// JSON: { "type": "summon_spirit_from_all_memorials", "unit": "Spirit", "hp": 10, "damage": 5, "speed": 1 }
/// Optional "hp_per_spirit": true — each spirit's HP equals number of other spirits controlled.
/// </summary>
public sealed class SummonSpiritFromAllMemorialsEffect : EffectBase
{
	public string UnitKind;
	public int BaseHP, Damage, Speed;
	public bool HpPerSpirit;
	public int AdvanceOnArrive;
	public bool InheritMemorialName;
	public int BonusDamagePerStrength;

	public SummonSpiritFromAllMemorialsEffect(string kind, int baseHp, int damage, int speed,
		bool hpPerSpirit = false, int advanceOnArrive = 0,
		bool inheritMemorialName = false, int bonusDamagePerStrength = 0)
	{
		UnitKind = kind;
		BaseHP = baseHp;
		Damage = damage;
		Speed = speed;
		HpPerSpirit = hpPerSpirit;
		AdvanceOnArrive = advanceOnArrive;
		InheritMemorialName = inheritMemorialName;
		BonusDamagePerStrength = bonusDamagePerStrength;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null || s.Memorials == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		int ownerTeam = casterUnit?.TeamId ?? 0;

		var memorials = s.Memorials.GetAllMemorials();
		int existingSpirits = s.UnitsInPlay.Count(u => u != null && u.IsSpirit && u.SummonerTeamId == ownerTeam);

		foreach (var tile in memorials)
		{
			if (!tile.HasMemorial)
				continue;

			int hp = HpPerSpirit ? Math.Max(1, BaseHP + existingSpirits) : BaseHP;
			int dmg = Damage + (BonusDamagePerStrength > 0
				? tile.Memorial.StrengthValue * BonusDamagePerStrength : 0);

			string sourceName = InheritMemorialName
				? (tile.Memorial?.SourceName ?? UnitKind)
				: UnitKind;

			var spirit = s.OnSummonRequested(UnitKind, tile, ownerTeam);
			if (spirit == null)
				continue;

			spirit.IsSpirit = true;
			spirit.SummonerTeamId = ownerTeam;
			spirit.Stats.MaxHealth = hp;
			spirit.Stats.Health = hp;
			spirit.Stats.BaseSpeed = Speed;
			spirit.AttackDamage = dmg;
			spirit.ApplySpiritAppearance();

			s.Memorials.ConsumeMemorial(tile);
			existingSpirits++;

			s.Log($"[SummonFromAllMemorials] {sourceName} rises at {tile.Axial} ({hp}HP {dmg}DMG).");
		}
	}
}

/// <summary>
/// Creates a memorial on target tile or caster tile.
/// JSON: { "type": "create_memorial", "strength": "solid" }
/// Strength values: "faint", "solid", "strong"
/// </summary>
public sealed class CreateMemorialEffect : EffectBase
{
	public MemorialStrength Strength;

	public CreateMemorialEffect(MemorialStrength strength = MemorialStrength.Solid)
	{
		Strength = strength;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		int ownerTeam = casterUnit?.TeamId ?? 0;
		string casterName = casterUnit?.DisplayName ?? casterUnit?.Name ?? "Unknown";

		// Use target tile if available, otherwise caster tile
		TileData tile = null;
		if (targets?.Items?.Count > 0)
		{
			tile = targets.Items[0] switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};
		}
		tile ??= casterUnit?.CurrentTile;

		if (tile == null)
			return;

		s.Memorials.CreateMemorial(tile, casterName, false, Strength, ownerTeam);
		s.Log($"[CreateMemorial] {Strength} memorial at {tile.Axial}.");
	}
}

/// <summary>
/// Consumes target memorial, marking it for removal at turn end.
/// JSON: { "type": "consume_memorial" }
/// </summary>
public sealed class ConsumeMemorialEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null || !tile.HasMemorial)
				continue;

			s.Memorials.ConsumeMemorial(tile);
			s.Log($"[ConsumeMemorial] Memorial released at {tile.Axial}.");
		}
	}
}

/// <summary>
/// Consumes a memorial on the target tile, or dismisses a spirit standing on it.
/// JSON: { "type": "consume_memorial_or_dismiss_spirit" }
/// </summary>
public sealed class ConsumeMemorialOrDismissSpiritEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s == null)
			return;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null)
				continue;

			// Prefer spirit dismissal if a spirit occupies the tile
			if (tile.Occupant is Unit occupant && occupant.IsSpirit)
			{
				occupant.Die();
				s.Log($"[DismissSpirit] {occupant.Name} dismissed from {tile.Axial}.");
				continue;
			}

			// Fall back to consuming the memorial
			if (tile.HasMemorial && s.Memorials != null)
			{
				s.Memorials.ConsumeMemorial(tile);
				s.Log($"[ConsumeMemorial] Memorial released at {tile.Axial}.");
			}
		}
	}
}

/// <summary>
/// Adds Grief charges to the active caster's GriefAttunement.
/// JSON: { "type": "gain_grief", "amount": n }
/// </summary>
public sealed class GainGriefEffect : EffectBase
{
	public int Amount;
	public GainGriefEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is GriefAttunement grief)
		{
			grief.GainCharges(Amount);
			s.Log($"[GainGrief] +{Amount} Grief (now {grief.Charges}).");
		}
	}
}

/// <summary>
/// Moves all friendly spirits toward the nearest enemy. If already adjacent, they attack.
/// JSON: { "type": "advance_all_spirits", "tiles": n, "attack_if_adjacent": true }
/// </summary>
public sealed class AdvanceAllSpiritsEffect : EffectBase
{
	public int Tiles;
	public bool AttackIfAdjacent;
	public bool GrantAttackIfReached;

	public AdvanceAllSpiritsEffect(int tiles, bool attackIfAdjacent = true, bool grantAttackIfReached = false)
	{
		Tiles = tiles;
		AttackIfAdjacent = attackIfAdjacent;
		GrantAttackIfReached = grantAttackIfReached;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null)
			return;

		var spirits = s.UnitsInPlay
			.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			.ToList();

		foreach (var spirit in spirits)
		{
			if (spirit.CurrentTile == null)
				continue;

			// Find nearest enemy
			Unit nearestEnemy = null;
			int bestDist = int.MaxValue;
			foreach (var unit in s.UnitsInPlay)
			{
				if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
					continue;
				if (unit.TeamId == spirit.TeamId)
					continue;
				int dist = s.Grid.Distance(spirit.CurrentTile.Axial, unit.CurrentTile.Axial);
				if (dist < bestDist)
				{ bestDist = dist; nearestEnemy = unit; }
			}

			if (nearestEnemy == null)
				continue;

			if (AttackIfAdjacent && bestDist <= 1)
			{
				// Already adjacent — attack
				nearestEnemy.ApplyDamage(spirit.AttackDamage);
				s.Log($"[AdvanceSpirits] {spirit.Name} attacks {nearestEnemy.Name} for {spirit.AttackDamage}.");
			}
			else
			{
				// Move toward enemy
				spirit.Stats.MovePoints = Tiles;
				s.Log($"[AdvanceSpirits] {spirit.Name} advances {Tiles} toward {nearestEnemy.Name}.");
				// Actual pathfinding movement is handled by the movement system — we set move points here
			}
		}
	}
}

/// <summary>
/// Grants a temporary stat buff to all friendly spirits.
/// JSON: { "type": "buff_all_spirits", "stat": "damage", "amount": n, "duration": 1 }
/// Supported stats: "damage", "armor", "undying"
/// </summary>
public sealed class BuffAllSpiritsEffect : EffectBase
{
	public string Stat;
	public int Amount;
	public int Duration;

	public BuffAllSpiritsEffect(string stat, int amount, int duration = 1)
	{
		Stat = stat;
		Amount = amount;
		Duration = duration;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		var spirits = s.UnitsInPlay
			.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			.ToList();

		foreach (var spirit in spirits)
		{
			switch (Stat.ToLower())
			{
				case "damage":
					spirit.AttackDamage += Amount;
					spirit.SpiritDamageBuff += Amount;
					spirit.SpiritDamageBuffTurns = Duration;
					break;
				case "armor":
					spirit.Stats.Armor += Amount;
					break;
				case "undying":
					spirit.IsUndying = true;
					spirit.UndyingTurns = Duration;
					break;
			}
		}

		s.Log($"[BuffAllSpirits] {spirits.Count} spirit(s) buffed: +{Amount} {Stat} for {Duration} turn(s).");
	}
}

/// <summary>
/// Marks all friendly spirits to create a memorial when they score a kill this turn.
/// JSON: { "type": "mark_spirits_memorial_on_kill" }
/// </summary>
public sealed class MarkSpiritsMemorialOnKillEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		var spirits = s.UnitsInPlay
			.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			.ToList();

		foreach (var spirit in spirits)
			spirit.CreateMemorialOnKill = true;

		s.Log($"[MarkSpirits] {spirits.Count} spirit(s) will leave memorials on kill.");
	}
}

/// <summary>
/// Grants the caster armor equal to AmountPer × number of memorials on the board.
/// JSON: { "type": "armor_per_memorial", "amount_per": n }
/// </summary>
public sealed class ArmorPerMemorialEffect : EffectBase
{
	public int AmountPer;
	public ArmorPerMemorialEffect(int amountPer) { AmountPer = amountPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		int count = s.Memorials.CountMemorials();
		int armor = count * AmountPer;
		if (armor > 0)
		{
			casterUnit.Stats.Armor += armor;
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[ArmorPerMemorial] {count} memorial(s) × {AmountPer} = {armor} armor.");
	}
}

/// <summary>
/// Grants the caster armor equal to AmountPer × current Grief charges.
/// JSON: { "type": "armor_per_grief", "amount_per": n }
/// </summary>
public sealed class ArmorPerGriefEffect : EffectBase
{
	public int AmountPer;
	public ArmorPerGriefEffect(int amountPer) { AmountPer = amountPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is not GriefAttunement grief)
			return;

		int armor = grief.Charges * AmountPer;
		if (armor > 0)
		{
			casterUnit.Stats.Armor += armor;
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[ArmorPerGrief] {grief.Charges} Grief × {AmountPer} = {armor} armor.");
	}
}

/// <summary>
/// Heals the caster for a fraction of the damage dealt by the previous step.
/// Reads damage from EffectResult context if available; otherwise reads last damage dealt from GameState.
/// JSON: { "type": "heal_fraction_of_damage", "fraction": 0.5 }
/// </summary>
public sealed class HealFractionOfDamageEffect : EffectBase
{
	public float Fraction;
	public HealFractionOfDamageEffect(float fraction) { Fraction = fraction; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		int damage = s.LastDamageDealt;
		int heal = (int)(damage * Fraction);
		if (heal > 0)
		{
			casterUnit.Stats.Health = Math.Min(casterUnit.Stats.MaxHealth, casterUnit.Stats.Health + heal);
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[HealFraction] Healed {heal} ({Fraction:P0} of {damage} damage).");
	}
}

/// <summary>
/// Alias registered as "gain_mana" — delegates to existing ManaGainEffect logic.
/// JSON: { "type": "gain_mana", "amount": n }
/// </summary>
public sealed class GainManaEffect : EffectBase
{
	public int Amount;
	public GainManaEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;
		casterUnit.GainMana(Amount);
		if (s.Mana.ContainsKey(caster))
			s.Mana[caster] = casterUnit.Stats.Mana;
		s.Log($"[GainMana] {casterUnit.Name} gains {Amount} mana (now {casterUnit.Stats.Mana}/{casterUnit.Stats.MaxMana}).");
	}
}

/// <summary>
/// Deals damage and pushes all enemies within range of any spirit or memorial.
/// JSON: { "type": "dirge_pulse", "damage": n, "push": n }
/// </summary>
public sealed class DirgePulseEffect : EffectBase
{
	public int Damage;
	public int Push;
	public int CollisionDamage;

	public DirgePulseEffect(int damage, int push, int collisionDamage = 0)
	{
		Damage = damage;
		Push = push;
		CollisionDamage = collisionDamage;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		// Collect pulse origins — all spirit tiles and memorial tiles
		var pulseOrigins = new HashSet<Vector2I>();

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.IsSpirit || !unit.Stats.IsAlive || unit.CurrentTile == null)
				continue;
			if (unit.SummonerTeamId != casterUnit.TeamId)
				continue;
			pulseOrigins.Add(unit.CurrentTile.Axial);
		}

		if (s.Memorials != null)
			foreach (var tile in s.Memorials.GetAllMemorials())
				pulseOrigins.Add(tile.Axial);

		if (pulseOrigins.Count == 0)
		{
			s.Log("[Dirge] No spirits or memorials on board — no effect.");
			return;
		}

		// Find all enemies within 2 of any origin
		var affected = new HashSet<Unit>();
		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
				continue;
			if (unit.TeamId == casterUnit.TeamId)
				continue;

			foreach (var origin in pulseOrigins)
			{
				if (s.Grid.Distance(origin, unit.CurrentTile.Axial) <= 2)
				{
					affected.Add(unit);
					break;
				}
			}
		}

		foreach (var unit in affected)
		{
			unit.ApplyDamage(Damage);
			s.Log($"[Dirge] {unit.Name} takes {Damage} from the dirge.");
			// Push is handled by the movement system when push tiles > 0
		}
	}
}

/// <summary>
/// Hallows target tile — creates or upgrades a memorial to Hallowed state.
/// JSON: { "type": "hallow_tile", "duration": n, "auto_rise_range": n }
/// </summary>
public sealed class HallowTileEffect : EffectBase
{
	public int Duration;
	public int AutoRiseRange;

	public HallowTileEffect(int duration = 99, int autoRiseRange = 0)
	{
		Duration = duration;
		AutoRiseRange = autoRiseRange;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null)
				continue;

			s.Memorials.HallowTile(tile);
			s.Log($"[HallowTile] Tile {tile.Axial} hallowed.");
		}
	}
}

/// <summary>
/// Hallows all tiles within radius of the caster.
/// JSON: { "type": "hallow_area", "radius": n }
/// </summary>
public sealed class HallowAreaEffect : EffectBase
{
	public int Radius;

	public HallowAreaEffect(int radius = 2)
	{
		Radius = radius;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null || s.Grid == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null)
			return;

		var center = casterUnit.CurrentTile.Axial;
		int count = 0;

		foreach (var kvp in s.Grid.Tiles)
		{
			if (s.Grid.Distance(center, kvp.Key) > Radius)
				continue;
			s.Memorials.HallowTile(kvp.Value);
			count++;
		}

		s.Log($"[HallowArea] Hallowed {count} tile(s) within radius {Radius}.");
	}
}

/// <summary>
/// Each memorial on the board strikes adjacent enemies for damage.
/// JSON: { "type": "memorial_strike_all", "damage": n }
/// Optional: "push": n, "leave_memorial": true, "strikes": n, "global": false
/// </summary>
public sealed class MemorialStrikeAllEffect : EffectBase
{
	public int Damage;
	public int Push;
	public bool LeaveMemorial;
	public int Strikes;

	public MemorialStrikeAllEffect(int damage, int push = 0, bool leaveMemorial = false, int strikes = 1)
	{
		Damage = damage;
		Push = push;
		LeaveMemorial = leaveMemorial;
		Strikes = strikes;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null || s.Grid == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		var memorials = s.Memorials.GetAllMemorials();
		int totalDamage = 0;

		foreach (var tile in memorials)
		{
			for (int strike = 0; strike < Strikes; strike++)
			{
				foreach (var neighbor in s.Grid.GetNeighbors(tile.Axial))
				{
					var neighborTile = s.Grid.GetTile(neighbor);
					if (neighborTile?.Occupant == null)
						continue;
					var unit = neighborTile.Occupant;
					if (unit.TeamId == casterUnit.TeamId)
						continue;

					unit.ApplyDamage(Damage);
					totalDamage += Damage;
					s.Log($"[MemorialStrike] Memorial at {tile.Axial} strikes {unit.Name} for {Damage}.");
				}
			}

			if (!LeaveMemorial)
				s.Memorials.ConsumeMemorial(tile);
		}

		s.Log($"[MemorialStrikeAll] {memorials.Count} memorial(s) fired. Total damage: {totalDamage}.");
	}
}

/// <summary>
/// Imbues target tile as Memorial Ground — summon spells here cost less.
/// JSON: { "type": "create_memorial_ground", "duration": n, "summon_discount": n }
/// </summary>
public sealed class CreateMemorialGroundEffect : EffectBase
{
	public int Duration;
	public int SummonDiscount;
	public int SpiritRegen;

	public CreateMemorialGroundEffect(int duration = 3, int summonDiscount = 2, int spiritRegen = 0)
	{
		Duration = duration;
		SummonDiscount = summonDiscount;
		SpiritRegen = spiritRegen;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null)
				continue;

			// Hallow the tile and track discount via persistent effect
			s.Memorials.HallowTile(tile);
			tile.SummonDiscount = SummonDiscount;
			tile.SummonDiscountTurns = Duration;
			s.Log($"[MemorialGround] Tile {tile.Axial} is Memorial Ground (discount {SummonDiscount}, {Duration} turns).");
		}
	}
}

/// <summary>
/// Spends all (or chosen amount of) Grief charges. Deals DamagePerGrief to all enemies per charge.
/// JSON: { "type": "grief_discharge_damage", "damage_per_grief": n }
/// Optional: "choose_amount": true, "min_spend": 1
/// </summary>
public sealed class GriefDischargeDamageEffect : EffectBase
{
	public int DamagePerGrief;

	public GriefDischargeDamageEffect(int damagePerGrief)
	{
		DamagePerGrief = damagePerGrief;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is not GriefAttunement grief)
			return;

		int charges = grief.Charges;
		if (charges <= 0)
		{
			s.Log("[GriefDischarge] No Grief to spend.");
			return;
		}

		int totalDamage = charges * DamagePerGrief;
		s.LastGriefSpent = charges;

		// Deal damage to all enemies
		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.TeamId == casterUnit.TeamId)
				continue;
			unit.ApplyDamage(totalDamage);
		}

		// Reset grief
		grief.SetChargesDirectly(0);

		s.Log($"[GriefDischarge] Spent {charges} Grief — dealt {totalDamage} to all enemies.");
	}
}

/// <summary>
/// Applies a status to all friendly spirits.
/// JSON: { "type": "apply_status_to_all_spirits", "status": "undying_turn", "duration": 1 }
/// </summary>
public sealed class ApplyStatusToAllSpiritsEffect : EffectBase
{
	public string Status;
	public int Duration;
	public int ReviveHP;

	public ApplyStatusToAllSpiritsEffect(string status, int duration = 1, int reviveHP = 8)
	{
		Status = status;
		Duration = duration;
		ReviveHP = reviveHP;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		var spirits = s.UnitsInPlay
			.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			.ToList();

		foreach (var spirit in spirits)
		{
			switch (Status)
			{
				case "undying_turn":
					spirit.IsUndying = true;
					spirit.UndyingReviveHP = ReviveHP;
					spirit.UndyingTurns = Duration;
					break;
				case "undying_full_restore":
					spirit.IsUndying = true;
					spirit.UndyingFullRestore = true;
					spirit.UndyingTurns = Duration;
					break;
				case "invulnerable":
					spirit.IsInvulnerable = true;
					spirit.InvulnerableTurns = Duration;
					break;
				case "vigil":
					spirit.IsVigil = true;
					spirit.VigilTurns = Duration;
					break;
				default:
					spirit.ApplyStatus(Status, Duration);
					break;
			}
		}

		s.Log($"[StatusAllSpirits] Applied '{Status}' to {spirits.Count} spirit(s).");
	}
}

/// <summary>
/// Consumes all memorials on the board. Per memorial consumed: gain mana and/or draw cards.
/// JSON: { "type": "consume_all_memorials_global", "mana_per": n, "draw_per": n }
/// </summary>
public sealed class ConsumeAllMemorialsGlobalEffect : EffectBase
{
	public int ManaPerMemorial;
	public int DrawPerMemorial;

	public ConsumeAllMemorialsGlobalEffect(int manaPerMemorial = 0, int drawPerMemorial = 0)
	{
		ManaPerMemorial = manaPerMemorial;
		DrawPerMemorial = drawPerMemorial;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		var memorials = s.Memorials.GetAllMemorials().ToList();

		foreach (var tile in memorials)
		{
			s.Memorials.ConsumeMemorial(tile);

			if (ManaPerMemorial > 0 && casterUnit != null)
			{
				casterUnit.GainMana(ManaPerMemorial);
				if (s.Mana.ContainsKey(caster))
					s.Mana[caster] = casterUnit.Stats.Mana;
			}

			if (DrawPerMemorial > 0 && casterUnit?.DeckData != null)
				casterUnit.DeckData.Draw(DrawPerMemorial);
		}

		s.Log($"[ConsumeAllMemorials] Released {memorials.Count} memorial(s). " +
			  $"+{memorials.Count * ManaPerMemorial} mana, drew {memorials.Count * DrawPerMemorial} card(s).");
	}
}

/// <summary>
/// Deals DamagePer × memorial count to all enemies.
/// JSON: { "type": "damage_per_memorial_global", "damage_per": n }
/// </summary>
public sealed class DamagePerMemorialGlobalEffect : EffectBase
{
	public int DamagePer;
	public DamagePerMemorialGlobalEffect(int damagePer) { DamagePer = damagePer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		int count = s.Memorials.CountMemorials();
		int damage = count * DamagePer;

		if (damage <= 0)
		{
			s.Log("[DamagePerMemorial] No memorials — no damage.");
			return;
		}

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.TeamId == casterUnit.TeamId)
				continue;
			unit.ApplyDamage(damage);
		}

		s.LastDamageDealt = damage;
		s.Log($"[DamagePerMemorial] {count} memorials × {DamagePer} = {damage} damage to all enemies.");
	}
}

public sealed class HollowMantleLeafEffect : EffectBase
{
    public int Turns, Armor;

    public HollowMantleLeafEffect(int turns, int armor)
    {
        Turns = turns;
        Armor = armor;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = s.ActiveCasterUnit;
        if (casterUnit == null)
            return;

        casterUnit.Stats.Armor += Armor;
        casterUnit.RefreshHealthBar();

        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new HollowMantleEffect(Turns, caster));

        s.Log($"[HollowMantle] Activated — {Armor} armor, {Turns} turns.");
    }
}

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
                if (obj is TileData td)
                { center = td.Axial; break; }
                if (obj is Unit u && u.CurrentTile != null)
                { center = u.CurrentTile.Axial; break; }
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
