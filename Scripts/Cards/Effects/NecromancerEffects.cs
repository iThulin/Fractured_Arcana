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
				// Already adjacent — attack. Capture the tile first: Die() clears it.
				var victimTile = nearestEnemy.CurrentTile;
				nearestEnemy.ApplyDamage(spirit.AttackDamage);
				s.Log($"[AdvanceSpirits] {spirit.Name} attacks {nearestEnemy.Name} for {spirit.AttackDamage}.");

				// On-kill riders (Call to Purpose and upgrades) — consumed here because
				// spirits only ever attack through this effect.
				if (!nearestEnemy.Stats.IsAlive)
				{
					if (spirit.CreateMemorialOnKill && victimTile != null && s.Memorials != null)
					{
						s.Memorials.CreateMemorial(victimTile, nearestEnemy, spirit.SummonerTeamId);
						s.Log($"[AdvanceSpirits] {spirit.Name}'s kill leaves a memorial.");
					}
					if (spirit.DrawOnKillCount > 0 && casterUnit.DeckData != null)
					{
						casterUnit.DeckData.Draw(spirit.DrawOnKillCount);
						s.OnDrawCards?.Invoke(casterUnit);
						s.Log($"[AdvanceSpirits] {spirit.Name}'s kill draws {spirit.DrawOnKillCount} card(s).");
					}
				}
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

// ════════════════════════════════════════════════════════════════════════════
// Formerly-placeholder effects (implemented from card text; see
// CardScriptRegistry.Necromancer.cs for the JSON contracts)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Trade Places: the caster swaps positions with a targeted friendly spirit.
/// Both tiles are vacated before re-placement so PlaceOnTile's occupancy guard
/// passes; entering the new tiles triggers the normal on-enter hooks.
/// JSON: { "type": "swap_with_spirit" }
/// </summary>
public sealed class SwapWithSpiritEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null)
			return;

		Unit spirit = null;
		if (targets?.Items != null)
		{
			foreach (var o in targets.Items)
			{
				var u = ResolveTargetUnit(s, o);
				if (u != null && u.IsSpirit && u.Stats.IsAlive
					&& u.SummonerTeamId == casterUnit.TeamId)
				{ spirit = u; break; }
			}
		}

		if (spirit?.CurrentTile == null)
		{
			s.Log("[TradePlaces] No friendly spirit targeted.");
			return;
		}

		var casterTile = casterUnit.CurrentTile;
		var spiritTile = spirit.CurrentTile;

		casterTile.ClearOccupant(casterUnit);
		spiritTile.ClearOccupant(spirit);
		casterUnit.PlaceOnTile(spiritTile);
		spirit.PlaceOnTile(casterTile);

		s.Log($"[TradePlaces] {casterUnit.Name} swaps with {spirit.Name}.");
	}
}

/// <summary>
/// Congregation / The Flood: memorials within range step 1 tile toward the caster.
/// A memorial stepping onto another memorial's tile merges: both are spent and a
/// merge unit is summoned there (stats optionally scaled by combined strength).
/// If RemainderUnit is set (tier-4 Flood), memorials that did not merge are also
/// spent, each summoning the remainder unit on its tile.
/// JSON: { "type": "pull_memorials_and_merge", "range": 3, "merge_unit": "Revenant",
///         "merge_hp": 12, "merge_damage": 5, "merge_speed": 1,
///         "scale_with_strength": false, "remainder_unit": "Spirit", ... }
/// </summary>
public sealed class PullMemorialsAndMergeEffect : EffectBase
{
	public int Range;
	public string MergeUnit;
	public int MergeHp, MergeDamage, MergeSpeed;
	public bool ScaleWithStrength;
	public string RemainderUnit;
	public int RemainderHp, RemainderDamage, RemainderSpeed;

	public PullMemorialsAndMergeEffect(int range, string mergeUnit, int mergeHp,
		int mergeDamage, int mergeSpeed, bool scaleWithStrength,
		string remainderUnit, int remainderHp, int remainderDamage, int remainderSpeed)
	{
		Range = range;
		MergeUnit = mergeUnit;
		MergeHp = mergeHp;
		MergeDamage = mergeDamage;
		MergeSpeed = mergeSpeed;
		ScaleWithStrength = scaleWithStrength;
		RemainderUnit = remainderUnit;
		RemainderHp = remainderHp;
		RemainderDamage = remainderDamage;
		RemainderSpeed = remainderSpeed;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null || s.Memorials == null || s.Grid == null)
			return;

		var center = casterUnit.CurrentTile.Axial;
		int merges = 0, moves = 0;

		// Nearest memorials step first so later ones can land beside them.
		var tiles = s.Memorials.GetMemorialsInRange(center, Range)
			.OrderBy(t => s.Grid.Distance(center, t.Axial))
			.ToList();

		foreach (var tile in tiles)
		{
			if (!tile.HasMemorial)
				continue;   // already merged away this cast
			int dist = s.Grid.Distance(center, tile.Axial);
			if (dist <= 1)
				continue;   // already beside the wizard — nothing to pull

			// Pick the step that gets closest to the caster; prefer stepping onto
			// another memorial (that is the merge).
			TileData best = null;
			bool bestHasMemorial = false;
			int bestDist = dist;
			foreach (var n in s.Grid.GetNeighbors(tile.Axial))
			{
				var nt = s.Grid.GetTile(n);
				if (nt == null || nt.IsBlocked)
					continue;
				int nd = s.Grid.Distance(center, nt.Axial);
				if (nd >= dist)
					continue;
				bool hasMem = nt.HasMemorial;
				if (best == null || (hasMem && !bestHasMemorial) || (hasMem == bestHasMemorial && nd < bestDist))
				{
					best = nt;
					bestHasMemorial = hasMem;
					bestDist = nd;
				}
			}

			if (best == null)
				continue;

			if (bestHasMemorial)
			{
				// ── Merge ─────────────────────────────────────────────
				int combined = tile.Memorial.StrengthValue + best.Memorial.StrengthValue;
				var spawnTile = best.Occupant == null ? best
							  : tile.Occupant == null ? tile : null;
				if (spawnTile == null)
				{
					s.Log("[Congregation] Merge blocked — both tiles occupied.");
					continue;
				}

				s.Memorials.RemoveMemorial(tile);
				s.Memorials.RemoveMemorial(best);

				var merged = s.OnSummonRequested?.Invoke(
					MergeUnit.ToLowerInvariant(), spawnTile, casterUnit.TeamId);
				if (merged != null)
				{
					int hp = MergeHp + (ScaleWithStrength ? 2 * combined : 0);
					int dmg = MergeDamage + (ScaleWithStrength ? combined : 0);
					merged.Stats.MaxHealth = hp;
					merged.Stats.Health = hp;
					merged.AttackDamage = dmg;
					merged.RefreshHealthBar();
					merges++;
					s.Log($"[Congregation] Two memorials merge — {merged.Name} rises " +
						  $"({hp} HP, {dmg} DMG).");
				}
			}
			else if (s.Memorials.MoveMemorial(tile, best))
			{
				moves++;
			}
		}

		// ── The Flood: lone memorials rise too ────────────────────────
		if (!string.IsNullOrEmpty(RemainderUnit))
		{
			foreach (var tile in s.Memorials.GetMemorialsInRange(center, Range).ToList())
			{
				if (tile.Occupant != null)
					continue;
				s.Memorials.RemoveMemorial(tile);
				var lone = s.OnSummonRequested?.Invoke(
					RemainderUnit.ToLowerInvariant(), tile, casterUnit.TeamId);
				if (lone != null)
				{
					if (RemainderHp > 0)
					{
						lone.Stats.MaxHealth = RemainderHp;
						lone.Stats.Health = RemainderHp;
					}
					if (RemainderDamage > 0)
						lone.AttackDamage = RemainderDamage;
					lone.RefreshHealthBar();
					s.Log($"[Congregation] A lone memorial rises as {lone.Name}.");
				}
			}
		}

		s.Log($"[Congregation] {moves} memorial(s) drawn in, {merges} merge(s).");
	}
}

/// <summary>
/// One Last Push: friendly spirits that kill an enemy this turn draw the caster
/// cards. Consumed by the spirit attack in AdvanceAllSpiritsEffect; reset at end
/// of player turn.
/// JSON: { "type": "mark_spirits_draw_on_kill", "count": 1 }
/// </summary>
public sealed class MarkSpiritsDrawOnKillEffect : EffectBase
{
	public int Count;
	public MarkSpiritsDrawOnKillEffect(int count) { Count = count; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		int marked = 0;
		foreach (var u in s.UnitsInPlay)
		{
			if (u != null && u.IsSpirit && u.Stats.IsAlive
				&& u.SummonerTeamId == casterUnit.TeamId)
			{
				u.DrawOnKillCount = Count;
				marked++;
			}
		}
		s.Log($"[MarkSpirits] {marked} spirit(s) will draw {Count} card(s) on kill.");
	}
}

/// <summary>
/// The Weight We Carry: grants the caster shield equal to AmountPer × memorials
/// on the board. Shield variant of ArmorPerMemorialEffect.
/// JSON: { "type": "shield_per_memorial", "amount_per": 1 }
/// </summary>
public sealed class ShieldPerMemorialEffect : EffectBase
{
	public int AmountPer;
	public ShieldPerMemorialEffect(int amountPer) { AmountPer = amountPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		int count = s.Memorials.CountMemorials();
		int shield = count * AmountPer;
		if (shield > 0)
		{
			casterUnit.Stats.Shield += shield;
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[ShieldPerMemorial] {count} memorial(s) × {AmountPer} = {shield} shield.");
	}
}

/// <summary>
/// The Flood Within / Flood of Grief: forces the Grief Flood immediately —
/// OnFloodTriggered fires (refreshing all spirits via CombatManager) and Grief
/// resets to 0.
/// JSON: { "type": "trigger_flood" }
/// </summary>
public sealed class TriggerFloodEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.ActiveCasterUnit?.Attunement is not GriefAttunement grief)
		{
			s?.Log("[Flood] Caster has no Grief attunement.");
			return;
		}
		s.Log("[Flood] The grief crests — the Flood breaks.");
		grief.ForceFlood();
	}
}

// ════════════════════════════════════════════════════════════════════════════
// Upgrade-tier backlog implementations (2026-07-06). JSON contracts in
// docs/card_effect_backlog.md; registrations in CardScriptRegistry.Necromancer.cs.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Shared helpers for the memorial-movement family of effects.</summary>
internal static class NecroEffectUtil
{
	/// <summary>Nearest memorial tile to <paramref name="from"/>, optionally excluding a set of already-claimed tiles. Null when none exist.</summary>
	public static TileData NearestMemorial(GameState s, Vector2I from, HashSet<TileData> exclude = null, int maxRange = int.MaxValue)
	{
		if (s?.Memorials == null || s.Grid == null)
			return null;
		TileData best = null;
		int bestDist = int.MaxValue;
		foreach (var tile in s.Memorials.GetAllMemorials())
		{
			if (exclude != null && exclude.Contains(tile))
				continue;
			int d = s.Grid.Distance(from, tile.Axial);
			if (d <= maxRange && d < bestDist)
			{ bestDist = d; best = tile; }
		}
		return best;
	}

	/// <summary>Steps a unit up to <paramref name="steps"/> tiles toward (or away from, when <paramref name="toward"/> is false) a position, using the same greedy neighbor-walk as PushEffect/PullEffect. Returns tiles actually moved; sets <paramref name="blocked"/> when movement stopped early.</summary>
	public static int StepRelativeTo(GameState s, Unit unit, Vector2I anchor, int steps, bool toward, out bool blocked)
	{
		blocked = false;
		int moved = 0;
		for (int i = 0; i < steps; i++)
		{
			var current = unit.CurrentTile.Axial;
			if (toward && s.Grid.Distance(anchor, current) == 0)
				break;

			TileData best = null;
			int bestDist = toward ? s.Grid.Distance(anchor, current) : -1;
			foreach (var n in s.Grid.GetNeighbors(current))
			{
				var td = s.Grid.GetTile(n);
				if (td == null || !td.CanEnter(unit))
					continue;
				int d = s.Grid.Distance(anchor, n);
				if (toward ? d < bestDist : d > bestDist)
				{ bestDist = d; best = td; }
			}

			if (best == null)
			{ blocked = true; break; }

			unit.CurrentTile.ClearOccupant(unit);
			unit.PlaceOnTile(best);
			moved++;
		}
		return moved;
	}

	/// <summary>Summons and configures a spirit at (or, when occupied, adjacent to) a tile. Mirrors SummonSpiritEffect's occupancy fallback. Returns the spirit or null.</summary>
	public static Unit SpawnSpirit(GameState s, string kind, TileData tile, int team,
		int hp, int damage, int speed, string sourceName = null)
	{
		if (s?.OnSummonRequested == null || tile == null)
			return null;

		TileData spawnTile = tile;
		if (tile.IsOccupied && s.Grid != null)
		{
			spawnTile = null;
			foreach (var n in s.Grid.GetNeighbors(tile.Axial))
			{
				var nt = s.Grid.GetTile(n);
				if (nt != null && nt.IsWalkable && !nt.IsBlocked && !nt.IsOccupied)
				{ spawnTile = nt; break; }
			}
			if (spawnTile == null)
			{
				s.Log($"[SpawnSpirit] {tile.Axial} occupied and no adjacent tile free — blocked.");
				return null;
			}
		}

		var spirit = s.OnSummonRequested(kind, spawnTile, team);
		if (spirit == null)
			return null;

		spirit.IsSpirit = true;
		spirit.SummonerTeamId = team;
		spirit.Stats.MaxHealth = hp;
		spirit.Stats.Health = hp;
		spirit.Stats.BaseSpeed = speed;
		spirit.AttackDamage = damage;
		spirit.ApplySpiritAppearance();
		s.Log($"[SpawnSpirit] {sourceName ?? kind} rises at {spawnTile.Axial} ({hp}HP {damage}DMG).");
		return spirit;
	}

	/// <summary>All living enemy units of the caster's team.</summary>
	public static List<Unit> LivingEnemies(GameState s, Unit casterUnit)
		=> s.UnitsInPlay.Where(u => u != null && u.Stats.IsAlive
			&& u.CurrentTile != null && u.TeamId != casterUnit.TeamId).ToList();

	/// <summary>All living friendly spirits of the caster's team.</summary>
	public static List<Unit> FriendlySpirits(GameState s, Unit casterUnit)
		=> s.UnitsInPlay.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive
			&& u.SummonerTeamId == casterUnit.TeamId).ToList();
}

/// <summary>
/// Into the Memory / Put Them in Place: moves each target onto its nearest
/// memorial. Each memorial claims at most one target per cast, which is what
/// makes tier 3's "each pushed to a separate memorial" hold with the same node.
/// JSON: { "type": "pull_to_memorial", "range": 6 }
/// </summary>
public sealed class PullToMemorialEffect : EffectBase
{
	public int Range;
	public PullToMemorialEffect(int range) { Range = range; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null || s.Memorials == null || targets == null)
			return;

		var claimed = new HashSet<TileData>();
		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim?.CurrentTile == null)
				continue;

			var memorial = NecroEffectUtil.NearestMemorial(s, victim.CurrentTile.Axial, claimed, Range);
			if (memorial == null)
			{
				s.Log($"[PullToMemorial] No unclaimed memorial within {Range} of {victim.Name}.");
				continue;
			}
			claimed.Add(memorial);

			int moved = NecroEffectUtil.StepRelativeTo(s, victim, memorial.Axial, 32, toward: true, out _);
			bool landed = victim.CurrentTile == memorial;
			s.Log($"[PullToMemorial] {victim.Name} dragged {moved} tile(s) — " +
				  (landed ? "onto the memorial." : "toward the memorial."));
		}
	}
}

/// <summary>
/// Congregation Pull / The Summoning: every enemy within range of the caster is
/// moved N tiles toward its own nearest memorial.
/// JSON: { "type": "pull_all_to_memorial", "range": 3, "tiles": 1 }
/// </summary>
public sealed class PullAllToMemorialEffect : EffectBase
{
	public int Range, Tiles;
	public PullAllToMemorialEffect(int range, int tiles) { Range = range; Tiles = tiles; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null || s.Grid == null || s.Memorials == null)
			return;

		int affected = 0;
		foreach (var enemy in NecroEffectUtil.LivingEnemies(s, casterUnit))
		{
			if (s.Grid.Distance(casterUnit.CurrentTile.Axial, enemy.CurrentTile.Axial) > Range)
				continue;
			var memorial = NecroEffectUtil.NearestMemorial(s, enemy.CurrentTile.Axial);
			if (memorial == null)
				continue;
			NecroEffectUtil.StepRelativeTo(s, enemy, memorial.Axial, Tiles, toward: true, out _);
			affected++;
		}
		s.Log($"[PullAllToMemorial] {affected} enemy(ies) drawn toward the memorials.");
	}
}

/// <summary>
/// The Great Wave: every enemy is pushed away from its nearest memorial;
/// blocked pushes deal collision damage.
/// JSON: { "type": "push_all_from_memorial", "tiles": 2, "collision_damage": 2 }
/// </summary>
public sealed class PushAllFromMemorialEffect : EffectBase
{
	public int Tiles, CollisionDamage;
	public PushAllFromMemorialEffect(int tiles, int collisionDamage) { Tiles = tiles; CollisionDamage = collisionDamage; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null || s.Memorials == null)
			return;

		foreach (var enemy in NecroEffectUtil.LivingEnemies(s, casterUnit))
		{
			var memorial = NecroEffectUtil.NearestMemorial(s, enemy.CurrentTile.Axial);
			if (memorial == null)
				continue;
			int moved = NecroEffectUtil.StepRelativeTo(s, enemy, memorial.Axial, Tiles, toward: false, out bool blocked);
			if (blocked && CollisionDamage > 0)
			{
				enemy.ApplyDamage(CollisionDamage);
				s.Log($"[GreatWave] {enemy.Name} thrown {moved} tile(s), collides for {CollisionDamage}.");
			}
			else
				s.Log($"[GreatWave] {enemy.Name} thrown {moved} tile(s) from the memorial.");
		}
	}
}

/// <summary>
/// The Trap Is Set: damages all enemies, then drags each toward its nearest
/// memorial; any enemy ending on or beside a memorial takes the landing damage.
/// JSON: { "type": "push_all_to_memorial", "damage_before": 5, "damage_on_land": 4 }
/// </summary>
public sealed class PushAllToMemorialEffect : EffectBase
{
	public int DamageBefore, DamageOnLand;
	public PushAllToMemorialEffect(int damageBefore, int damageOnLand) { DamageBefore = damageBefore; DamageOnLand = damageOnLand; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null || s.Memorials == null)
			return;

		foreach (var enemy in NecroEffectUtil.LivingEnemies(s, casterUnit))
		{
			if (DamageBefore > 0)
				enemy.ApplyDamage(DamageBefore);
			if (!enemy.Stats.IsAlive || enemy.CurrentTile == null)
				continue;

			var memorial = NecroEffectUtil.NearestMemorial(s, enemy.CurrentTile.Axial);
			if (memorial == null)
				continue;

			NecroEffectUtil.StepRelativeTo(s, enemy, memorial.Axial, 10, toward: true, out _);
			bool landed = enemy.CurrentTile == memorial
				|| s.Grid.Distance(enemy.CurrentTile.Axial, memorial.Axial) <= 1;
			if (landed && DamageOnLand > 0)
			{
				enemy.ApplyDamage(DamageOnLand);
				s.Log($"[TheTrap] {enemy.Name} lands in the memory — {DamageOnLand} damage.");
			}
		}
	}
}

/// <summary>
/// Last Words: the target is marked — dying while marked leaves a memorial of
/// the given strength (resolved in CombatManager.HandleUnitDeath).
/// JSON: { "type": "mark_on_death_memorial", "strength": "strong" }
/// </summary>
public sealed class MarkOnDeathMemorialEffect : EffectBase
{
	public MemorialStrength Strength;
	public MarkOnDeathMemorialEffect(MemorialStrength strength) { Strength = strength; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		foreach (var obj in targets?.Items ?? new List<object>())
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim == null)
				continue;
			victim.LeaveMemorialOnDeath = Strength;
			s.Log($"[LastWords] {victim.Name} is marked — death will leave a {Strength} memorial.");
		}
	}
}

/// <summary>
/// Many Voices / The Grand Seance: communes with every memorial in range —
/// draw and gain Grief per memorial; optionally summon a spirit at each; the
/// Grand Seance leaves the memorials standing (consume: false).
/// JSON: { "type": "commune_all_memorials", "range": 3, "draw_per": 1, "grief_per": 1,
///         "summon_per": { "unit": "Spirit", "hp": 8, "damage": 4, "speed": 1 }, "consume": false }
/// </summary>
public sealed class CommuneAllMemorialsEffect : EffectBase
{
	public int Range, DrawPer, GriefPer;
	public bool Consume;
	public string SummonUnit;   // null = no summon
	public int SummonHp, SummonDamage, SummonSpeed;

	public CommuneAllMemorialsEffect(int range, int drawPer, int griefPer, bool consume,
		string summonUnit = null, int summonHp = 8, int summonDamage = 4, int summonSpeed = 1)
	{
		Range = range;
		DrawPer = drawPer;
		GriefPer = griefPer;
		Consume = consume;
		SummonUnit = summonUnit;
		SummonHp = summonHp;
		SummonDamage = summonDamage;
		SummonSpeed = summonSpeed;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null || s.Memorials == null)
			return;

		var memorials = s.Memorials.GetMemorialsInRange(casterUnit.CurrentTile.Axial, Range);
		int communed = 0;
		foreach (var tile in memorials)
		{
			if (!tile.HasMemorial)
				continue;
			communed++;

			if (DrawPer > 0 && casterUnit.DeckData != null)
			{
				casterUnit.DeckData.Draw(DrawPer);
				s.OnDrawCards?.Invoke(casterUnit);
			}
			if (GriefPer > 0 && casterUnit.Attunement is GriefAttunement grief)
				grief.GainCharges(GriefPer);
			if (!string.IsNullOrEmpty(SummonUnit))
				NecroEffectUtil.SpawnSpirit(s, SummonUnit, tile, casterUnit.TeamId,
					SummonHp, SummonDamage, SummonSpeed, tile.Memorial?.SourceName);
			if (Consume)
				s.Memorials.ConsumeMemorial(tile);
		}
		s.Log($"[Commune] {communed} memorial(s) answered — drew {communed * DrawPer}, +{communed * GriefPer} Grief.");
	}
}

/// <summary>
/// The Garden / Consecrated Battlefield: Memorial Ground over an area — radius 99
/// covers the whole board ("permanently" = duration 99, outliving any fight).
/// JSON: { "type": "create_memorial_ground_area", "radius": 1, "duration": 5, "summon_discount": 2, "spirit_regen": 2 }
/// </summary>
public sealed class CreateMemorialGroundAreaEffect : EffectBase
{
	public int Radius, Duration, SummonDiscount, SpiritRegen;

	public CreateMemorialGroundAreaEffect(int radius, int duration, int summonDiscount, int spiritRegen)
	{
		Radius = radius;
		Duration = duration;
		SummonDiscount = summonDiscount;
		SpiritRegen = spiritRegen;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null || s.Grid == null)
			return;
		var casterUnit = s.ActiveCasterUnit;

		TileData center = null;
		if (targets?.Items?.Count > 0)
			center = targets.Items[0] switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};
		center ??= casterUnit?.CurrentTile;
		if (center == null)
			return;

		int count = 0;
		foreach (var kvp in s.Grid.Tiles)
		{
			if (s.Grid.Distance(center.Axial, kvp.Key) > Radius)
				continue;
			var tile = kvp.Value;
			s.Memorials.HallowTile(tile);
			tile.SummonDiscount = SummonDiscount;
			tile.SummonDiscountTurns = Duration;
			count++;
		}
		s.Log($"[MemorialGround] {count} tile(s) consecrated (discount {SummonDiscount}, {Duration} turns).");
	}
}

/// <summary>
/// Grief Made Weapon: armor per Grief charge spent by the preceding discharge.
/// Reads GameState.LastGriefSpent (set by GriefDischargeDamageEffect).
/// JSON: { "type": "armor_per_grief_spent", "amount_per": 1 }
/// </summary>
public sealed class ArmorPerGriefSpentEffect : EffectBase
{
	public int AmountPer;
	public ArmorPerGriefSpentEffect(int amountPer) { AmountPer = amountPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null)
			return;
		int armor = s.LastGriefSpent * AmountPer;
		if (armor > 0)
		{
			casterUnit.Stats.Armor += armor;
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[GriefMadeWeapon] {s.LastGriefSpent} Grief spent × {AmountPer} = {armor} armor.");
	}
}

/// <summary>
/// Grief Drain: gain 1 Grief per N damage dealt by the preceding step.
/// JSON: { "type": "grief_per_damage", "damage_per_grief": 3 }
/// </summary>
public sealed class GriefPerDamageEffect : EffectBase
{
	public int DamagePerGrief;
	public GriefPerDamageEffect(int damagePerGrief) { DamagePerGrief = Math.Max(1, damagePerGrief); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.ActiveCasterUnit?.Attunement is not GriefAttunement grief)
			return;
		int gained = s.LastDamageDealt / DamagePerGrief;
		if (gained > 0)
			grief.GainCharges(gained);
		s.Log($"[GriefDrain] {s.LastDamageDealt} damage → +{gained} Grief.");
	}
}

/// <summary>
/// Soul Flood's rider: heals the caster for a fraction of the total damage the
/// preceding step dealt across all targets (DealDamage/AoeAll record the total).
/// JSON: { "type": "heal_fraction_of_total_damage", "fraction": 1.0 }
/// </summary>
public sealed class HealFractionOfTotalDamageEffect : EffectBase
{
	public float Fraction;
	public HealFractionOfTotalDamageEffect(float fraction) { Fraction = fraction; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null)
			return;
		int heal = (int)(s.LastDamageDealt * Fraction);
		if (heal > 0)
		{
			casterUnit.Stats.Health = Math.Min(casterUnit.Stats.MaxHealth, casterUnit.Stats.Health + heal);
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[SoulFlood] Healed {heal} ({Fraction:P0} of {s.LastDamageDealt} total damage).");
	}
}

/// <summary>
/// The Exchange: heals the friendly spirit with the most missing HP.
/// JSON: { "type": "heal_most_damaged_spirit", "amount": 4 }
/// </summary>
public sealed class HealMostDamagedSpiritEffect : EffectBase
{
	public int Amount;
	public HealMostDamagedSpiritEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		Unit worst = null;
		int worstMissing = 0;
		foreach (var spirit in NecroEffectUtil.FriendlySpirits(s, casterUnit))
		{
			int missing = spirit.Stats.MaxHealth - spirit.Stats.Health;
			if (missing > worstMissing)
			{ worstMissing = missing; worst = spirit; }
		}

		if (worst == null)
		{
			s.Log("[Exchange] No wounded spirit to heal.");
			return;
		}
		worst.Stats.Health = Math.Min(worst.Stats.MaxHealth, worst.Stats.Health + Amount);
		worst.RefreshHealthBar();
		s.Log($"[Exchange] {worst.Name} heals {Amount}.");
	}
}

/// <summary>
/// Overflowing: if Grief exceeds 4 charges, every friendly spirit's HP is
/// fully restored.
/// JSON: { "type": "grief_overflow_heal_spirits" }
/// </summary>
public sealed class GriefOverflowHealSpiritsEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.Attunement is not GriefAttunement grief)
			return;
		if (grief.Charges <= 4)
		{
			s.Log($"[Overflowing] Grief {grief.Charges} — no overflow.");
			return;
		}
		int refreshed = 0;
		foreach (var spirit in NecroEffectUtil.FriendlySpirits(s, casterUnit))
		{
			spirit.Stats.Health = spirit.Stats.MaxHealth;
			spirit.RefreshHealthBar();
			refreshed++;
		}
		s.Log($"[Overflowing] Grief overflows — {refreshed} spirit(s) fully restored.");
	}
}

/// <summary>
/// Total Communion: deals damage equal to each target's missing HP. Records the
/// total so heal_equal_to_damage_dealt can follow.
/// JSON: { "type": "damage_equal_to_missing_hp" }
/// </summary>
public sealed class DamageEqualToMissingHpEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;
		int total = 0;
		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim == null || !victim.Stats.IsAlive)
				continue;
			int missing = victim.Stats.MaxHealth - victim.Stats.Health;
			if (missing <= 0)
				continue;
			victim.ApplyDamage(missing);
			total += missing;
			s.Log($"[TotalCommunion] {victim.Name} takes {missing} (missing HP).");
		}
		if (total > 0)
			s.LastDamageDealt = total;
	}
}

/// <summary>
/// The Song That Ends All Things: the dirge hits every enemy on the board;
/// enemies adjacent to a friendly spirit take multiplied damage; all are pushed
/// away from their nearest spirit/memorial origin.
/// JSON: { "type": "dirge_pulse_global", "damage": 4, "push": 2, "collision_damage": 3, "adjacent_spirit_multiplier": 2 }
/// </summary>
public sealed class DirgePulseGlobalEffect : EffectBase
{
	public int Damage, Push, CollisionDamage, AdjacentSpiritMultiplier;

	public DirgePulseGlobalEffect(int damage, int push, int collisionDamage, int adjacentSpiritMultiplier)
	{
		Damage = damage;
		Push = push;
		CollisionDamage = collisionDamage;
		AdjacentSpiritMultiplier = Math.Max(1, adjacentSpiritMultiplier);
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null)
			return;

		// Pulse origins: friendly spirits and memorials.
		var origins = new List<Vector2I>();
		foreach (var spirit in NecroEffectUtil.FriendlySpirits(s, casterUnit))
			if (spirit.CurrentTile != null)
				origins.Add(spirit.CurrentTile.Axial);
		if (s.Memorials != null)
			foreach (var tile in s.Memorials.GetAllMemorials())
				origins.Add(tile.Axial);

		var spirits = NecroEffectUtil.FriendlySpirits(s, casterUnit);
		foreach (var enemy in NecroEffectUtil.LivingEnemies(s, casterUnit))
		{
			bool nearSpirit = spirits.Any(sp => sp.CurrentTile != null
				&& s.Grid.Distance(sp.CurrentTile.Axial, enemy.CurrentTile.Axial) <= 1);
			int dmg = nearSpirit ? Damage * AdjacentSpiritMultiplier : Damage;
			enemy.ApplyDamage(dmg);
			s.Log($"[TheSong] {enemy.Name} takes {dmg}{(nearSpirit ? " (the spirits sing close)" : "")}.");

			if (!enemy.Stats.IsAlive || enemy.CurrentTile == null || Push <= 0 || origins.Count == 0)
				continue;

			var nearest = origins.OrderBy(o => s.Grid.Distance(o, enemy.CurrentTile.Axial)).First();
			NecroEffectUtil.StepRelativeTo(s, enemy, nearest, Push, toward: false, out bool blocked);
			if (blocked && CollisionDamage > 0)
				enemy.ApplyDamage(CollisionDamage);
		}
	}
}

/// <summary>
/// The Grand Procession: every friendly spirit teleports to its nearest
/// memorial (or beside it when occupied). Memorials are not consumed.
/// JSON: { "type": "teleport_all_spirits_to_nearest_memorial" }
/// </summary>
public sealed class TeleportAllSpiritsToNearestMemorialEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null || s.Memorials == null)
			return;

		foreach (var spirit in NecroEffectUtil.FriendlySpirits(s, casterUnit))
		{
			if (spirit.CurrentTile == null)
				continue;
			var memorial = NecroEffectUtil.NearestMemorial(s, spirit.CurrentTile.Axial);
			if (memorial == null)
				continue;

			TileData dest = memorial.CanEnter(spirit) ? memorial : null;
			if (dest == null)
				foreach (var n in s.Grid.GetNeighbors(memorial.Axial))
				{
					var nt = s.Grid.GetTile(n);
					if (nt != null && nt.CanEnter(spirit))
					{ dest = nt; break; }
				}
			if (dest == null || dest == spirit.CurrentTile)
				continue;

			spirit.CurrentTile.ClearOccupant(spirit);
			spirit.PlaceOnTile(dest);
			s.Log($"[GrandProcession] {spirit.Name} steps to {dest.Axial}.");
		}
	}
}

/// <summary>
/// Weight of Loss: bonus damage to the targets equal to AmountPer × memorials
/// on the board (targeted sibling of damage_per_memorial_global).
/// JSON: { "type": "damage_per_memorial", "amount_per": 1 }
/// </summary>
public sealed class TargetedDamagePerMemorialEffect : EffectBase
{
	public int AmountPer;
	public TargetedDamagePerMemorialEffect(int amountPer) { AmountPer = amountPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null || targets == null)
			return;
		int damage = s.Memorials.CountMemorials() * AmountPer;
		if (damage <= 0)
			return;
		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim == null || !victim.Stats.IsAlive)
				continue;
			victim.ApplyDamage(damage);
			s.Log($"[WeightOfLoss] {victim.Name} takes {damage} (memorial weight).");
		}
	}
}

/// <summary>
/// Chain of Being: the targeted friendly spirit (from Trade Places) swaps
/// positions with its nearest enemy.
/// JSON: { "type": "spirit_swap_with_nearest_enemy" }
/// </summary>
public sealed class SpiritSwapWithNearestEnemyEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null)
			return;

		Unit spirit = null;
		foreach (var obj in targets?.Items ?? new List<object>())
		{
			var u = ResolveTargetUnit(s, obj);
			if (u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			{ spirit = u; break; }
		}
		if (spirit?.CurrentTile == null)
		{
			s.Log("[ChainOfBeing] No friendly spirit in the target set.");
			return;
		}

		Unit enemy = NecroEffectUtil.LivingEnemies(s, casterUnit)
			.OrderBy(e => s.Grid.Distance(spirit.CurrentTile.Axial, e.CurrentTile.Axial))
			.FirstOrDefault();
		if (enemy?.CurrentTile == null)
			return;

		var spiritTile = spirit.CurrentTile;
		var enemyTile = enemy.CurrentTile;
		spiritTile.ClearOccupant(spirit);
		enemyTile.ClearOccupant(enemy);
		spirit.PlaceOnTile(enemyTile);
		enemy.PlaceOnTile(spiritTile);
		s.Log($"[ChainOfBeing] {spirit.Name} swaps with {enemy.Name}.");
	}
}

/// <summary>
/// Mass Rites: damages all enemies; each kill performs the full rite — a
/// memorial on the victim's tile, a spirit strike on one enemy adjacent to the
/// victim, and a spirit summoned from that memorial (consuming it).
/// JSON: { "type": "last_rite_aoe", "damage": 7, "spirit_strike": 5,
///         "summon_on_kill": { "unit": "Spirit", "hp": 8, "damage": 4, "speed": 1 } }
/// </summary>
public sealed class LastRiteAoeEffect : EffectBase
{
	public int Damage, SpiritStrike;
	public string SummonUnit;
	public int SummonHp, SummonDamage, SummonSpeed;

	public LastRiteAoeEffect(int damage, int spiritStrike,
		string summonUnit, int summonHp, int summonDamage, int summonSpeed)
	{
		Damage = damage;
		SpiritStrike = spiritStrike;
		SummonUnit = summonUnit;
		SummonHp = summonHp;
		SummonDamage = summonDamage;
		SummonSpeed = summonSpeed;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null)
			return;

		foreach (var enemy in NecroEffectUtil.LivingEnemies(s, casterUnit))
		{
			var victimTile = enemy.CurrentTile;
			enemy.ApplyDamage(Damage);
			if (enemy.Stats.IsAlive || victimTile == null)
				continue;

			// The rite: memorial, adjacent strike, the spirit rises.
			s.Memorials?.CreateMemorial(victimTile, enemy.Name, wasAlly: false,
				MemorialStrength.Solid, casterUnit.TeamId);

			if (SpiritStrike > 0)
			{
				Unit adjacent = null;
				foreach (var n in s.Grid.GetNeighbors(victimTile.Axial))
				{
					var occ = s.Grid.GetTile(n)?.Occupant;
					if (occ != null && occ.Stats.IsAlive && occ.TeamId != casterUnit.TeamId)
					{ adjacent = occ; break; }
				}
				if (adjacent != null)
				{
					adjacent.ApplyDamage(SpiritStrike);
					s.Log($"[MassRites] The rite strikes {adjacent.Name} for {SpiritStrike}.");
				}
			}

			if (!string.IsNullOrEmpty(SummonUnit) && victimTile.HasMemorial)
			{
				var spirit = NecroEffectUtil.SpawnSpirit(s, SummonUnit, victimTile,
					casterUnit.TeamId, SummonHp, SummonDamage, SummonSpeed, enemy.Name);
				if (spirit != null)
					s.Memorials?.ConsumeMemorial(victimTile);
			}
		}
	}
}

/// <summary>
/// The Grand Departure: dismisses every friendly spirit; each bursts —
/// damaging and pushing adjacent enemies — and leaves a memorial of the given
/// strength on its tile.
/// JSON: { "type": "mass_departure", "damage": 7, "push": 2, "collision_damage": 2, "memorial_strength": "strong" }
/// </summary>
public sealed class MassDepartureEffect : EffectBase
{
	public int Damage, Push, CollisionDamage;
	public MemorialStrength Strength;

	public MassDepartureEffect(int damage, int push, int collisionDamage, MemorialStrength strength)
	{
		Damage = damage;
		Push = push;
		CollisionDamage = collisionDamage;
		Strength = strength;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null)
			return;

		foreach (var spirit in NecroEffectUtil.FriendlySpirits(s, casterUnit))
		{
			var origin = spirit.CurrentTile;
			if (origin == null)
				continue;

			// Burst: adjacent enemies take damage and are pushed away.
			foreach (var n in s.Grid.GetNeighbors(origin.Axial))
			{
				var occ = s.Grid.GetTile(n)?.Occupant;
				if (occ == null || !occ.Stats.IsAlive || occ.TeamId == casterUnit.TeamId)
					continue;
				occ.ApplyDamage(Damage);
				if (occ.Stats.IsAlive && Push > 0)
				{
					NecroEffectUtil.StepRelativeTo(s, occ, origin.Axial, Push, toward: false, out bool blocked);
					if (blocked && CollisionDamage > 0)
						occ.ApplyDamage(CollisionDamage);
				}
			}

			// The spirit departs; its memory remains. Die() fires the normal
			// death pipeline; the explicit memorial below strengthens if the
			// death itself already left one.
			spirit.Die();
			s.Memorials?.CreateMemorial(origin, spirit.Name, wasAlly: true, Strength, casterUnit.TeamId);
			s.Log($"[GrandDeparture] {spirit.Name} departs — the burst and the memorial remain.");
		}
	}
}

/// <summary>
/// The Flood of Memory's draw rider: draw per memorial on the board.
/// JSON: { "type": "draw_per_memorial_global", "count_per": 1 }
/// </summary>
public sealed class DrawPerMemorialGlobalEffect : EffectBase
{
	public int CountPer;
	public DrawPerMemorialGlobalEffect(int countPer) { CountPer = countPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.DeckData == null || s.Memorials == null)
			return;
		int draw = s.Memorials.CountMemorials() * CountPer;
		if (draw > 0)
		{
			casterUnit.DeckData.Draw(draw);
			s.OnDrawCards?.Invoke(casterUnit);
		}
		s.Log($"[FloodOfMemory] Drew {draw} card(s).");
	}
}

/// <summary>
/// The Flood of Memory: every memorial on the board grows one step stronger.
/// JSON: { "type": "strengthen_all_memorials" }
/// </summary>
public sealed class StrengthenAllMemorialsEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;
		int count = 0;
		foreach (var tile in s.Memorials.GetAllMemorials())
		{
			s.Memorials.StrengthenMemorial(tile);
			count++;
		}
		s.Log($"[FloodOfMemory] {count} memorial(s) strengthened.");
	}
}

/// <summary>
/// Grief Made Flesh: summons a champion at the target memorial whose stats
/// scale with the combined strength of the memorials the preceding
/// consume_memorials_for_champion step consumed.
/// JSON: { "type": "summon_spirit_scaled", "unit": "Revenant_Champion",
///         "base_hp": 28, "base_damage": 10, "hp_per_strength": 4, "damage_per_strength": 2, "speed": 1 }
/// </summary>
public sealed class SummonSpiritScaledEffect : EffectBase
{
	public string UnitKind;
	public int BaseHp, BaseDamage, HpPerStrength, DamagePerStrength, Speed;

	public SummonSpiritScaledEffect(string unitKind, int baseHp, int baseDamage,
		int hpPerStrength, int damagePerStrength, int speed)
	{
		UnitKind = unitKind;
		BaseHp = baseHp;
		BaseDamage = baseDamage;
		HpPerStrength = hpPerStrength;
		DamagePerStrength = damagePerStrength;
		Speed = speed;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		TileData tile = null;
		foreach (var obj in targets?.Items ?? new List<object>())
		{
			tile = obj switch { TileData td => td, Unit u => u.CurrentTile, _ => null };
			if (tile != null) break;
		}
		tile ??= casterUnit.CurrentTile;
		if (tile == null)
			return;

		int strength = s.LastMemorialStrengthConsumed;
		int hp = BaseHp + strength * HpPerStrength;
		int dmg = BaseDamage + strength * DamagePerStrength;

		var champion = NecroEffectUtil.SpawnSpirit(s, UnitKind, tile, casterUnit.TeamId, hp, dmg, Speed);
		if (champion != null && tile.HasMemorial)
			s.Memorials?.ConsumeMemorial(tile);
		s.Log($"[GriefMadeFlesh] Champion rises with +{strength} combined strength ({hp}HP {dmg}DMG).");
	}
}

/// <summary>
/// Revenant Champion's consume step, done properly: consumes the nearest
/// <c>count</c> memorials within <c>range</c> of the caster — excluding the
/// cast's target tile so the champion still has a memorial to rise from — and
/// records their combined strength for summon_spirit_scaled.
/// JSON: { "type": "consume_memorials_for_champion", "count": 2, "range": 3 }
/// </summary>
public sealed class ConsumeMemorialsForChampionEffect : EffectBase
{
	public int Count, Range;
	public ConsumeMemorialsForChampionEffect(int count, int range) { Count = count; Range = range; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null || s.Memorials == null || s.Grid == null)
			return;

		// The tile the champion will rise from is spared.
		TileData spared = null;
		foreach (var obj in targets?.Items ?? new List<object>())
		{
			spared = obj switch { TileData td => td, Unit u => u.CurrentTile, _ => null };
			if (spared != null) break;
		}

		var candidates = s.Memorials.GetMemorialsInRange(casterUnit.CurrentTile.Axial, Range)
			.Where(t => t != spared)
			.OrderBy(t => s.Grid.Distance(casterUnit.CurrentTile.Axial, t.Axial))
			.Take(Count)
			.ToList();

		int strength = 0;
		foreach (var tile in candidates)
		{
			strength += tile.Memorial?.StrengthValue ?? 0;
			s.Memorials.ConsumeMemorial(tile);
		}
		s.LastMemorialStrengthConsumed = strength;
		s.Log($"[Reckoning] {candidates.Count} memorial(s) consumed — combined strength {strength}.");
	}
}

/// <summary>
/// Legion of the Honored: consumes every memorial within range; one champion
/// rises per two consumed, at the consumed sites.
/// JSON: { "type": "consume_all_memorials_for_champions", "range": 3, "unit": "Revenant_Champion", "base_hp": 24, "base_damage": 8, "speed": 1 }
/// </summary>
public sealed class ConsumeAllMemorialsForChampionsEffect : EffectBase
{
	public int Range;
	public string UnitKind;
	public int BaseHp, BaseDamage, Speed;

	public ConsumeAllMemorialsForChampionsEffect(int range, string unitKind, int baseHp, int baseDamage, int speed)
	{
		Range = range;
		UnitKind = unitKind;
		BaseHp = baseHp;
		BaseDamage = baseDamage;
		Speed = speed;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null || s.Memorials == null || s.Grid == null)
			return;

		var memorials = s.Memorials.GetMemorialsInRange(casterUnit.CurrentTile.Axial, Range)
			.OrderBy(t => s.Grid.Distance(casterUnit.CurrentTile.Axial, t.Axial))
			.ToList();

		int champions = memorials.Count / 2;
		foreach (var tile in memorials)
			s.Memorials.ConsumeMemorial(tile);

		for (int i = 0; i < champions; i++)
			NecroEffectUtil.SpawnSpirit(s, UnitKind, memorials[i * 2], casterUnit.TeamId,
				BaseHp, BaseDamage, Speed, "The Honored");

		s.Log($"[Legion] {memorials.Count} memorial(s) consumed — {champions} champion(s) answer.");
	}
}

/// <summary>
/// All Rise: summons a spirit from every memorial created this turn (tracked by
/// MemorialManager.CreatedSinceLastTick), consuming each.
/// JSON: { "type": "summon_spirit_from_new_memorials", "unit": "Spirit", "hp": 8, "damage": 4, "speed": 1 }
/// </summary>
public sealed class SummonSpiritFromNewMemorialsEffect : EffectBase
{
	public string UnitKind;
	public int HP, Damage, Speed;

	public SummonSpiritFromNewMemorialsEffect(string unitKind, int hp, int damage, int speed)
	{
		UnitKind = unitKind;
		HP = hp;
		Damage = damage;
		Speed = speed;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Memorials == null)
			return;

		int risen = 0;
		foreach (var tile in s.Memorials.CreatedSinceLastTick.ToList())
		{
			if (!tile.HasMemorial)
				continue;
			var spirit = NecroEffectUtil.SpawnSpirit(s, UnitKind, tile, casterUnit.TeamId,
				HP, Damage, Speed, tile.Memorial?.SourceName);
			if (spirit != null)
			{
				s.Memorials.ConsumeMemorial(tile);
				risen++;
			}
		}
		s.Log($"[AllRise] {risen} spirit(s) rise from this turn's memorials.");
	}
}

/// <summary>
/// All of Them: spirits rise from every memorial AND from every tile a spirit
/// fell on this combat (GameState.SpiritDeathTiles). Memorial rises reuse
/// SummonSpiritFromAllMemorialsEffect; death-site rises spawn directly.
/// JSON: { "type": "summon_spirit_from_all_memorials_and_death_sites", "unit": "Spirit",
///         "hp_per_spirit": true, "base_hp": 4, "damage": 6, "speed": 1,
///         "on_arrive_advance": 1, "bonus_damage_per_strength": 2, "inherit_memorial_name": true }
/// </summary>
public sealed class SummonSpiritFromAllMemorialsAndDeathSitesEffect : EffectBase
{
	public string UnitKind;
	public int BaseHP, Damage, Speed;
	public bool HpPerSpirit, InheritMemorialName;
	public int AdvanceOnArrive, BonusDamagePerStrength;

	public SummonSpiritFromAllMemorialsAndDeathSitesEffect(string kind, int baseHp, int damage, int speed,
		bool hpPerSpirit, int advanceOnArrive, bool inheritMemorialName, int bonusDamagePerStrength)
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
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null)
			return;

		// 1) Every memorial — existing effect does this correctly.
		new SummonSpiritFromAllMemorialsEffect(UnitKind, BaseHP, Damage, Speed,
			HpPerSpirit, AdvanceOnArrive, InheritMemorialName, BonusDamagePerStrength)
			.Resolve(s, caster, targets, snap);

		// 2) Every tile a spirit fell on this combat (deduplicated, must be free).
		int risen = 0;
		foreach (var coord in s.SpiritDeathTiles.Distinct().ToList())
		{
			var tile = s.Grid.GetTile(coord);
			if (tile == null || tile.HasMemorial)
				continue;   // memorial sites already handled above

			int existing = s.UnitsInPlay.Count(u => u != null && u.IsSpirit
				&& u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId);
			int hp = HpPerSpirit ? Math.Max(1, BaseHP + existing) : BaseHP;

			var spirit = NecroEffectUtil.SpawnSpirit(s, UnitKind, tile, casterUnit.TeamId,
				hp, Damage, Speed, "The Fallen");
			if (spirit != null)
				risen++;
		}
		s.Log($"[AllOfThem] {risen} spirit(s) rise from where they fell.");
	}
}

/// <summary>
/// Spirit Trail / Ghost Road: grants movement; every tile the caster leaves
/// this turn gains a Faint memorial. phase: true additionally lets the caster's
/// movement zone traverse blocked/occupied tiles (Unit.IsPhasing, honored by
/// the pathfinding zone functions); destinations must still be free.
/// JSON: { "type": "imbue_path_memorial", "move": 3, "phase": true }
/// </summary>
public sealed class ImbuePathMemorialEffect : EffectBase
{
	public int MoveTiles;
	public bool Phase;

	public ImbuePathMemorialEffect(int moveTiles, bool phase)
	{
		MoveTiles = moveTiles;
		Phase = phase;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Memorials == null)
			return;

		Action<TileData> onLeave = (leftTile) =>
		{
			if (leftTile == null || leftTile.HasMemorial)
				return;
			string trailName = casterUnit.DisplayName?.Length > 0 ? casterUnit.DisplayName : casterUnit.Name;
			s.Memorials.CreateMemorial(leftTile, trailName,
				wasAlly: true, MemorialStrength.Faint, casterUnit.TeamId);
			s.Log($"[SpiritTrail] A memorial forms at {leftTile.Axial}.");
		};

		casterUnit.OnTileLeft += onLeave;
		casterUnit.Stats.MovePoints += MoveTiles;
		if (Phase)
			casterUnit.IsPhasing = true;

		s.Log($"[SpiritTrail] {casterUnit.Name} gains {MoveTiles} move" +
			  (Phase ? " and walks between (phasing)." : "."));

		s.OnTurnEndCleanups ??= new List<Action>();
		s.OnTurnEndCleanups.Add(() =>
		{
			casterUnit.OnTileLeft -= onLeave;
			casterUnit.IsPhasing = false;
		});
	}
}

/// <summary>
/// Procession / The Guided: counts memorials the caster passes through for the
/// rest of the turn and pays out at turn end — cards drawn or armor gained per
/// memorial. Only memorials that existed when this resolved count, so a
/// Spirit-Trail step doesn't pay for its own footprints.
/// JSON: { "type": "draw_per_memorial_passed", "count_per": 1 }
///       { "type": "armor_per_memorial_passed", "amount_per": 2 }
/// </summary>
public sealed class PerMemorialPassedEffect : EffectBase
{
	public int Per;
	public bool GrantArmor;   // false = draw cards

	public PerMemorialPassedEffect(int per, bool grantArmor)
	{
		Per = per;
		GrantArmor = grantArmor;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit == null || s.Memorials == null)
			return;

		// Snapshot: only pre-existing memorials count as "passed through".
		var preexisting = new HashSet<Vector2I>(
			s.Memorials.GetAllMemorials().Select(t => t.Axial));

		int passed = 0;
		Action<TileData> onLeave = (leftTile) =>
		{
			if (leftTile != null && preexisting.Contains(leftTile.Axial))
				passed++;
		};

		casterUnit.OnTileLeft += onLeave;
		s.OnTurnEndCleanups ??= new List<Action>();
		s.OnTurnEndCleanups.Add(() =>
		{
			casterUnit.OnTileLeft -= onLeave;
			int amount = passed * Per;
			if (amount <= 0)
				return;
			if (GrantArmor)
			{
				casterUnit.Stats.Armor += amount;
				casterUnit.RefreshHealthBar();
				s.Log($"[TheGuided] {passed} memorial(s) passed — +{amount} armor.");
			}
			else if (casterUnit.DeckData != null)
			{
				casterUnit.DeckData.Draw(amount);
				s.OnDrawCards?.Invoke(casterUnit);
				s.Log($"[Procession] {passed} memorial(s) passed — drew {amount}.");
			}
		});
	}
}

/// <summary>
/// Walk Between (Hollow Mantle tier 4): while active, every spell the caster
/// casts heals all friendly spirits. Replaces the miswired hollow-mantle
/// duplicate registration.
/// JSON: { "type": "walk_between", "turns": 2, "spirit_heal_on_cast": 3 }
/// </summary>
public sealed class WalkBetweenPersistentEffect : PersistentEffect
{
	public int SpiritHealOnCast;

	public WalkBetweenPersistentEffect(int turns, Entity owner, int spiritHealOnCast)
	{
		TurnsRemaining = turns;
		Owner = owner;
		SpiritHealOnCast = spiritHealOnCast;
	}

	public override void Tick(GameState s)
	{
		TurnsRemaining--;
		s.Log($"[WalkBetween] {TurnsRemaining} turn(s) remaining.");
	}

	public override void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets)
	{
		if (casterUnit == null || SpiritHealOnCast <= 0)
			return;
		int healed = 0;
		foreach (var spirit in NecroEffectUtil.FriendlySpirits(s, casterUnit))
		{
			spirit.Stats.Health = Math.Min(spirit.Stats.MaxHealth,
				spirit.Stats.Health + SpiritHealOnCast);
			spirit.RefreshHealthBar();
			healed++;
		}
		if (healed > 0)
			s.Log($"[WalkBetween] The casting echoes between worlds — {healed} spirit(s) heal {SpiritHealOnCast}.");
	}
}

/// <summary>Leaf that registers the WalkBetween aura.</summary>
public sealed class WalkBetweenLeafEffect : EffectBase
{
	public int Turns, SpiritHealOnCast;

	public WalkBetweenLeafEffect(int turns, int spiritHealOnCast)
	{
		Turns = turns;
		SpiritHealOnCast = spiritHealOnCast;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new WalkBetweenPersistentEffect(Turns, caster, SpiritHealOnCast));
		s.Log($"[WalkBetween] Active for {Turns} turns — spells heal spirits {SpiritHealOnCast}.");
	}
}
