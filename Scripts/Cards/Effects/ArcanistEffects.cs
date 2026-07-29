using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ArcanistEffects.cs
//
// Purpose:        Arcanist school effects — charges, spell manipulation, constructs
//                 of magic, and their persistent modifiers.
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase, core leaves),
//                 PersistentEffect.cs (PersistentEffect base),
//                 CardScriptRegistry.Arcanist.cs (registration)
// Notes:          Extracted from Effect.cs / CompositeEffects.cs /
//                 PersistentEffect.cs — pure move, no behavior change.
// ============================================================

/// <summary>
/// Adds <see cref="Amount"/> Charge to the caster's Grimoire. Overflow past the cap
/// is reported by the attunement (CombatManager turns it into card draw).
/// JSON: { "type": "gain_charge", "amount": n }
/// </summary>
public sealed class GainChargeEffect : EffectBase
{
	public int Amount;
	public GainChargeEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.Attunement is not ArcaneAttunement arc)
		{
			s.Log("[GainCharge] Caster has no Arcane attunement — ignored.");
			return;
		}

		int banked = arc.Add(Amount);
		s.Log($"[GainCharge] +{banked} charge (now {arc.Charge}/{ArcaneAttunement.MaxCharge}).");
	}
}

/// <summary>
/// Spends banked Charge as ammo, dealing <see cref="DamagePerCharge"/> per charge spent
/// to each target in the current set. Optionally costs the caster
/// <see cref="SelfDamagePerCharge"/> HP per charge spent (Overcharge). Spends between
/// <see cref="MinSpend"/> and <see cref="MaxSpend"/> charge (MaxSpend &lt;= 0 means "all").
/// JSON: { "type": "spend_charge_damage", "damage_per_charge": n,
///         "min_spend": 1, "max_spend": 0, "self_damage_per_charge": 0 }
/// NOTE: This deals the full per-charge total to every target. The "may split between
/// enemies" variant (Arcane Barrage) needs per-instance target selection and is handled
/// by the heavier barrage effect — see Arcanist_Design.md.
/// </summary>
public sealed class SpendChargeDamageEffect : EffectBase
{
	public int DamagePerCharge;
	public int MinSpend;
	public int MaxSpend;            // <= 0 means "spend all available"
	public int SelfDamagePerCharge;

	public SpendChargeDamageEffect(int damagePerCharge, int minSpend = 1, int maxSpend = 0, int selfDamagePerCharge = 0)
	{
		DamagePerCharge = damagePerCharge;
		MinSpend = Math.Max(0, minSpend);
		MaxSpend = maxSpend;
		SelfDamagePerCharge = Math.Max(0, selfDamagePerCharge);
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.Attunement is not ArcaneAttunement arc)
		{
			s.Log("[SpendChargeDamage] Caster has no Arcane attunement — ignored.");
			return;
		}

		if (arc.Charge < MinSpend)
		{
			s.Log($"[SpendChargeDamage] Not enough charge ({arc.Charge} < {MinSpend}) — nothing spent.");
			return;
		}

		int want = MaxSpend > 0 ? Math.Min(MaxSpend, arc.Charge) : arc.Charge;
		int spent = arc.Spend(want);
		if (spent <= 0)
			return;

		int dmg = spent * DamagePerCharge;
		int hits = 0;

		if (targets != null)
		{
			foreach (var obj in targets.Items)
			{
				var unit = ResolveTargetUnit(s, obj);
				if (unit == null || !unit.Stats.IsAlive)
					continue;
				unit.ApplyDamage(dmg);
				hits++;
			}
		}

		s.LastDamageDealt = dmg;

		if (SelfDamagePerCharge > 0)
		{
			int recoil = spent * SelfDamagePerCharge;
			casterUnit.ApplyDamage(recoil);
			s.Log($"[SpendChargeDamage] Spent {spent} charge → {dmg} dmg to {hits} target(s); recoil {recoil} HP.");
		}
		else
		{
			s.Log($"[SpendChargeDamage] Spent {spent} charge → {dmg} dmg to {hits} target(s).");
		}
	}
}

/// <summary>
/// Deals <see cref="DamagePerSpell"/> per spell the Arcanist has cast this turn (read from
/// the Grimoire), with a floor of <see cref="Minimum"/>, to each target in the set.
/// The triggering card counts itself because "AbilityCast" fires when the card is pushed.
/// JSON: { "type": "damage_per_spell_cast", "amount": n, "min": m }
/// </summary>
public sealed class DamagePerSpellCastEffect : EffectBase
{
	public int DamagePerSpell;
	public int Minimum;

	public DamagePerSpellCastEffect(int damagePerSpell, int minimum = 0)
	{
		DamagePerSpell = damagePerSpell;
		Minimum = minimum;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		int count = (casterUnit?.Attunement as ArcaneAttunement)?.SpellsCastThisTurn ?? 0;

		int dmg = Math.Max(Minimum, DamagePerSpell * count);
		int hits = 0;

		if (targets != null)
		{
			foreach (var obj in targets.Items)
			{
				var unit = ResolveTargetUnit(s, obj);
				if (unit == null || !unit.Stats.IsAlive)
					continue;
				unit.ApplyDamage(dmg);
				hits++;
			}
		}

		s.LastDamageDealt = dmg;
		s.Log($"[DamagePerSpellCast] {count} spell(s) → {dmg} dmg to {hits} target(s) (min {Minimum}).");
	}
}

/// <summary>
/// Drains up to <see cref="Amount"/> mana from each target and gives the total to the caster.
/// JSON: { "type": "steal_mana", "amount": n }
/// NOTE: Reads/writes Unit.Stats.Mana directly. If your Unit exposes a dedicated
/// TrySpendMana / SetMana API, route through it instead of the raw field.
/// </summary>
public sealed class StealManaEffect : EffectBase
{
	public int Amount;
	public StealManaEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null || targets == null)
			return;

		int stolen = 0;
		foreach (var obj in targets.Items)
		{
			var unit = ResolveTargetUnit(s, obj);
			if (unit == null)
				continue;

			int take = Math.Min(Amount, unit.Stats.Mana);
			if (take <= 0)
				continue;

			unit.Stats.Mana -= take;
			stolen += take;
		}

		if (stolen > 0)
		{
			casterUnit.GainMana(stolen);
			if (s.Mana.ContainsKey(caster))
				s.Mana[caster] = casterUnit.Stats.Mana;
		}

		s.Log($"[StealMana] Drained {stolen} mana to {casterUnit.Name}.");
	}
}

/// <summary>
/// Return N cards from discard to hand — the player's pick, not "most recent first".
/// (2026-07-29) "Return A CARD from your discard" was silently returning the top of
/// the pile; it now publishes a CardChoiceRequest over the discard's contents. The
/// discard is public information, so nothing is revealed — the request goes straight
/// to the pile. Then optionally draw.
/// JSON: { "type":"return_from_discard","count":n,"draw":m }
/// </summary>
public sealed class ReturnFromDiscardEffect : EffectBase
{
	public int Count, DrawN;
	public ReturnFromDiscardEffect(int count, int draw) { Count = count; DrawN = draw; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		var d = unit?.DeckData;
		if (d == null)
			return;

		// R22: the drag preview replays real effects — one that moved cards between
		// piles would corrupt the deck on hover. (This guard was MISSING here; the
		// old top-of-pile version mutated the discard during previews.)
		if (CombatSim.Active)
			return;

		if (d.DiscardPile.Count == 0)
		{
			if (DrawN > 0) d.Draw(DrawN);
			s.Log("[ReturnFromDiscard] discard is empty — nothing to return.");
			return;
		}

		// Most recent first — that is how a player thinks about their discard.
		var candidates = Enumerable.Reverse(d.DiscardPile).ToList();
		int pick = Math.Min(Count, candidates.Count);

		var req = new CardChoiceRequest
		{
			Title = "Arcane Recall",
			Prompt = $"Return {pick} card(s) from your discard to your hand.",
			Owner = unit,
			Candidates = candidates,
			PickCount = pick,
			Source = "ReturnFromDiscard",
			OnChosen = chosen =>
			{
				int returned = 0;
				if (chosen != null)
					foreach (var c in chosen)
						if (c != null && d.DiscardPile.Remove(c))
						{ d.Hand.Add(c); returned++; }
				if (DrawN > 0)
					d.Draw(DrawN);
				s.OnDrawCards?.Invoke(unit);
				s.Log($"[ReturnFromDiscard] returned {returned}, drew {DrawN}.");
			},
		};
		s.RequestCardChoice(req);
	}
}

/// <summary>
/// Deal damage to each target, then gain Charge equal to the buffs on the (first) target, floored at min. 
/// JSON: { "type":"gain_charge_per_buff","min":n }
/// </summary>
public sealed class GainChargePerBuffEffect : EffectBase
{
	public int Minimum;
	public GainChargePerBuffEffect(int minimum) { Minimum = minimum; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var caster2 = FindCasterUnit(s, caster);
		int buffs = 0;
		if (targets?.Items != null)
			foreach (var o in targets.Items)
			{
				var u = ResolveTargetUnit(s, o);
				if (u?.Stats?.StatusEffects == null)
					continue;
				buffs = Math.Max(buffs, u.Stats.StatusEffects.Keys.Count(k => !InterfaceHelpers.Debuffs.Contains(k)));
			}
		int gain = Math.Max(Minimum, buffs);
		if (caster2?.Attunement is ArcaneAttunement a)
		{ a.Add(gain); s.Log($"[GainChargePerBuff] +{gain} charge."); }
	}
}

/// <summary>
/// Gain Charge scaled by keyword count. Card-context keyword introspection is pending; grants `multiplier` (min 1) as a stable stand-in. 
/// JSON: { "type":"gain_charge_per_keyword","multiplier":n }
/// </summary>
public sealed class GainChargePerKeywordEffect : EffectBase
{
	public int Multiplier;
	public GainChargePerKeywordEffect(int multiplier) { Multiplier = Math.Max(1, multiplier); }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var u = FindCasterUnit(s, caster);
		if (u?.Attunement is ArcaneAttunement a)
		{
			a.Add(Multiplier);
			s.Log($"[GainChargePerKeyword] +{Multiplier} charge (flat stand-in until card keyword context is threaded).");
		}
	}
}

/// <summary>
/// Arcane Drift (audit #16, 2026-07-29 — the movement finally exists): grants
/// MOVEMENT, armor/shield, and optionally charge per spell cast this turn, capped
/// at Max. Movement is granted as Stats.BonusMoveRange — the same this-turn
/// movement currency other dash effects use and StartTurn resets — so the player
/// spends it through normal movement rather than an auto-walk. (The old version
/// granted only the armor and logged "movement step pending"; the card's text
/// promised movement for its entire life.)
/// JSON: { "type":"move_per_spell_cast","max":n,"armor_per":n,"shield_per":n,"charge_per":n }
/// </summary>
public sealed class MovePerSpellCastEffect : EffectBase
{
	public int Max, ArmorPer, ShieldPer, ChargePer;
	public MovePerSpellCastEffect(int max, int armorPer, int shieldPer, int chargePer = 0)
	{ Max = max; ArmorPer = armorPer; ShieldPer = shieldPer; ChargePer = chargePer; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var u = FindCasterUnit(s, caster);
		if (u == null)
			return;
		int spells = (u.Attunement is ArcaneAttunement a) ? a.SpellsCastThisTurn : 0;
		int n = Math.Min(Max, spells);
		u.Stats.BonusMoveRange += n;
		if (ArmorPer > 0)
			u.Stats.Armor += ArmorPer * n;
		if (ShieldPer > 0)
			u.Stats.Shield += ShieldPer * n;
		if (ChargePer > 0 && u.Attunement is ArcaneAttunement arc)
			arc.Add(ChargePer * n);
		u.RefreshHealthBar();
		s.Log($"[ArcaneDrift] {spells} spells → +{n} movement this turn, +{ArmorPer * n} armor" +
			  (ShieldPer > 0 ? $", +{ShieldPer * n} shield" : "") +
			  (ChargePer > 0 ? $", +{ChargePer * n} charge." : "."));
	}
}

/// <summary>
/// Spend charge, deal flat damage; if lethal, mark the target exiled.
/// JSON: { "type":"disintegrate","damage":n,"charge_cost":n,"exile_on_lethal":bool }
/// </summary>
public sealed class DisintegrateEffect : EffectBase
{
	public int Damage, ChargeCost; public bool ExileOnLethal;
	public DisintegrateEffect(int damage, int chargeCost, bool exile) { Damage = damage; ChargeCost = chargeCost; ExileOnLethal = exile; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		if (c?.Attunement is ArcaneAttunement a)
		{
			if (a.Charge < ChargeCost)
			{ s.Log($"[Disintegrate] not enough charge ({a.Charge}/{ChargeCost})."); return; }
			a.SetChargesDirectly(a.Charge - ChargeCost);
		}
		if (targets?.Items == null)
			return;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u == null)
				continue;
			if (ExileOnLethal && u.Stats.Health + u.Stats.Shield + u.Stats.Armor <= Damage)
				u.ApplyStatus("exiled", 99); // necromancer resurrect should check "exiled"
			u.ApplyDamage(Damage);
			s.Log($"[Disintegrate] {Damage} to {u.Name}" + (ExileOnLethal ? " (exile on lethal)" : ""));
		}
	}
}

/// <summary>
/// Summons an Arcane Construct adjacent to the caster or on the targeted tile.
/// Constructs are autonomous units (HP/ATK/Speed from JSON) that persist until
/// killed or their duration expires. Duration is stored as a "construct" status
/// whose countdown needs a per-turn status hook (standard status processing).
/// JSON: { "type": "create_arcane_construct", "unit": "ArcaneConstruct",
///         "hp": n, "damage": n, "speed": n, "duration": n }
/// </summary>
public sealed class CreateArcaneConstructEffect : EffectBase
{
	public string UnitKind;
	public int HP, Damage, Speed, Duration;

	public CreateArcaneConstructEffect(string kind, int hp, int damage, int speed, int duration)
	{
		UnitKind = kind;
		HP = hp;
		Damage = damage;
		Speed = speed;
		Duration = duration;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{ s.Log("[CreateConstruct] No summon handler — cannot spawn."); return; }

		var casterUnit = FindCasterUnit(s, caster);
		int team = casterUnit?.TeamId ?? 0;
		int bonusDmg = casterUnit?.BonusSpellDamage ?? 0;

		var spawnTile = FindSpawnTile(s, casterUnit, targets);
		if (spawnTile == null)
		{ s.Log("[CreateConstruct] No valid spawn tile."); return; }

		var construct = s.OnSummonRequested(UnitKind, spawnTile, team);
		if (construct == null)
		{ s.Log("[CreateConstruct] Summon handler returned null."); return; }

		construct.Stats.MaxHealth = HP;
		construct.Stats.Health = HP;
		construct.Stats.BaseSpeed = Speed;
		construct.AttackDamage = Damage + bonusDmg;

		// Duration tracked as a status; status-system per-turn processing decrements it.
		// When "construct" reaches 0, the unit AI / status handler should kill the unit.
		if (Duration > 0)
			construct.ApplyStatus("construct", Duration);

		s.UnitsInPlay?.Add(construct);
		s.Log($"[CreateConstruct] {UnitKind} at {spawnTile.Axial} — {HP}HP / {construct.AttackDamage}ATK.");
	}

	private static TileData FindSpawnTile(GameState s, Unit caster, TargetSet targets)
	{
		// Prefer explicit target tile
		if (targets?.Items != null)
			foreach (var o in targets.Items)
			{
				var t = InterfaceHelpers.ResolveTile(s, o);
				if (t != null && !t.IsBlocked && !t.IsOccupied)
					return t;
			}
		// Fall back to first empty neighbour of the caster
		if (caster?.CurrentTile != null && s.Grid != null)
			foreach (var coord in s.Grid.GetNeighbors(caster.CurrentTile.Axial))
			{
				var t = s.Grid.GetTile(coord);
				if (t != null && !t.IsBlocked && !t.IsOccupied)
					return t;
			}
		return null;
	}
}

/// <summary>
/// Summons a Living Spell — a unit that embodies a spell and auto-casts it each
/// turn against the nearest enemy. The auto-cast AI lives on the unit side (not
/// in this effect); the effect handles the exile choice, the summoning, and stats.
///
/// (2026-07-29) The printed card says "Exile a spell from your hand … HP = mana x5,
/// DMG = mana x2". Neither clause existed: nothing was exiled and the stats were
/// flat defaults (the card's own hp_per_mana / damage_per_mana JSON was not even
/// read by the loader). Now: the player chooses the card to exile via the choice
/// seam; the construct's stats scale with the exiled card's top-half mana. With an
/// empty hand it falls back to the flat stats and exiles nothing, which the log says.
/// JSON: { "type": "summon_living_spell", "unit": "LivingSpell",
///         "hp_per_mana": n, "damage_per_mana": n, "hp": n, "damage": n, "duration": n }
/// </summary>
public sealed class SummonLivingSpellEffect : EffectBase
{
	public string UnitKind;
	public int HP, Damage, Duration;
	public int HpPerMana, DamagePerMana;

	public SummonLivingSpellEffect(string kind, int hp, int damage, int duration,
								   int hpPerMana = 0, int damagePerMana = 0)
	{
		UnitKind = kind;
		HP = hp;
		Damage = damage;
		Duration = duration;
		HpPerMana = hpPerMana;
		DamagePerMana = damagePerMana;
	}

	private void Summon(GameState s, Unit casterUnit, int team, int hp, int damage, string origin)
	{
		int bonusDmg = casterUnit?.BonusSpellDamage ?? 0;

		TileData spawnTile = null;
		if (casterUnit?.CurrentTile != null && s.Grid != null)
			foreach (var coord in s.Grid.GetNeighbors(casterUnit.CurrentTile.Axial))
			{
				var t = s.Grid.GetTile(coord);
				if (t != null && !t.IsBlocked && !t.IsOccupied)
				{ spawnTile = t; break; }
			}

		if (spawnTile == null)
		{ s.Log("[SummonLivingSpell] No spawn tile."); return; }

		var spell = s.OnSummonRequested(UnitKind, spawnTile, team);
		if (spell == null)
			return;

		spell.Stats.MaxHealth = hp;
		spell.Stats.Health = hp;
		spell.AttackDamage = damage + bonusDmg;

		if (Duration > 0)
			spell.ApplyStatus("living_spell", Duration);

		s.UnitsInPlay?.Add(spell);
		s.Log($"[SummonLivingSpell] {UnitKind} ({origin}) manifested at {spawnTile.Axial} " +
			  $"({hp}HP / {spell.AttackDamage}ATK). Auto-cast AI needs unit-side integration.");
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{ s.Log("[SummonLivingSpell] No summon handler."); return; }

		if (CombatSim.Active)
			return;

		var casterUnit = FindCasterUnit(s, caster);
		int team = casterUnit?.TeamId ?? 0;

		var hand = casterUnit?.DeckData?.Hand;
		bool scales = HpPerMana > 0 || DamagePerMana > 0;
		if (hand == null || hand.Count == 0 || !scales)
		{
			Summon(s, casterUnit, team, HP, Damage,
				   hand == null || hand.Count == 0 ? "no card to exile" : "flat stats");
			return;
		}

		var deck = casterUnit.DeckData;
		var req = new CardChoiceRequest
		{
			Title = "Living Spell",
			Prompt = $"Exile a spell from your hand. The Living Spell gets HP = mana x{HpPerMana}, " +
					 $"DMG = mana x{DamagePerMana}.",
			Owner = casterUnit,
			Candidates = new List<Card>(hand),
			PickCount = 1,
			Source = "LivingSpell",
			OnChosen = chosen =>
			{
				var card = chosen != null && chosen.Count > 0 ? chosen[0] : null;
				if (card == null || !deck.ExileFromHand(card))
				{
					Summon(s, casterUnit, team, HP, Damage, "exile failed — flat stats");
					return;
				}
				int mana = Math.Max(1, card.TopHalf?.ManaCost ?? 1);
				int hp = Math.Max(1, HpPerMana * mana);
				int dmg = Math.Max(1, DamagePerMana * mana);
				s.OnDrawCards?.Invoke(casterUnit);
				Summon(s, casterUnit, team, hp, dmg, $"embodies '{card.CardName}' ({mana} mana)");
			},
		};
		s.RequestCardChoice(req);
	}
}

/// <summary>
/// Queues a spell modifier effect that will apply to the next N spells cast by the caster.
/// The modifier grants flat bonus damage, extra draw, and/or a status effect on hit.
/// </summary>
public sealed class QueueNextSpellModifierLeafEffect : EffectBase
{
	public int BonusDamage, ExtraDraw, AppliesTo, StatusDuration;
	public string GrantStatus;

	public QueueNextSpellModifierLeafEffect(int bd, int ed, int at, string gs, int sd)
	{
		BonusDamage = bd;
		ExtraDraw = ed;
		AppliesTo = Math.Max(1, at);
		GrantStatus = gs;
		StatusDuration = sd;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new QueuedSpellModifier(
			BonusDamage, ExtraDraw, AppliesTo, GrantStatus, StatusDuration, caster, unit));
		s.Log($"[QueueNextSpell] Queued +{BonusDamage} dmg / draw {ExtraDraw} on next {AppliesTo} spell(s).");
	}
}

public sealed class ChargeCostModifierLeafEffect : EffectBase
{
	public int ChargePerMana, Turns;
	public ChargeCostModifierLeafEffect(int cpm, int turns) { ChargePerMana = cpm; Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ChargeCostModifierAura(ChargePerMana, Turns, caster, unit));
		s.Log($"[ChargeCostModifier] Spells cost charge instead of mana for {Turns} turn(s).");
	}
}

public sealed class OmniscienceLeafEffect : EffectBase
{
	public int Turns, ExileOnExpire;
	public OmniscienceLeafEffect(int turns, int exile) { Turns = turns; ExileOnExpire = exile; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.HasActiveEffect<OmniscienceEffect>(caster))
			return;
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new OmniscienceEffect(Turns, ExileOnExpire, caster, unit));
		s.Log($"[Omniscience] All spells free for {Turns} turn(s). {ExileOnExpire} exiled on expire.");
	}
}

public sealed class ArcaneApotheosisLeafEffect : EffectBase
{
	public int ChargePerSpell;
	public ArcaneApotheosisLeafEffect(int cps) { ChargePerSpell = Math.Max(1, cps); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.HasActiveEffect<ArcaneApotheosisAura>(caster))
			return; // idempotent
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ArcaneApotheosisAura(ChargePerSpell, caster, unit));
		s.Log("[ArcaneApotheosis] Permanent: every spell you cast generates charge.");
	}
}

public sealed class BindCardLeafEffect : EffectBase
{
	public int Turns;
	public BindCardLeafEffect(int turns) { Turns = Math.Max(1, turns); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		if (unit?.DeckData == null)
			return;

		if (CombatSim.Active)
			return;

		var hand = unit.DeckData.Hand;
		if (hand.Count == 0)
		{ s.Log("[BindCard] Hand is empty — nothing to bind."); return; }

		// (2026-07-29) "Exile A CARD from your hand" is the player's pick — this used
		// to bind hand[0] with a "future feature" comment. Hand choices go through
		// the same request seam as pile choices; only the Candidates differ.
		var req = new CardChoiceRequest
		{
			Title = "Tome Bind",
			Prompt = "Bind a card from your hand. Its top half auto-casts each turn while bound.",
			Owner = unit,
			Candidates = new List<Card>(hand),
			PickCount = 1,
			Source = "BindCard",
			OnChosen = chosen =>
			{
				var card = chosen != null && chosen.Count > 0 ? chosen[0] : null;
				if (card == null || !hand.Remove(card))
				{ s.Log("[BindCard] the chosen card left the hand — nothing bound."); return; }

				s.ActiveEffects ??= new List<PersistentEffect>();
				s.ActiveEffects.Add(new BoundCardAura(card, Turns, caster, unit));
				s.OnDrawCards?.Invoke(unit);
				s.Log($"[BindCard] '{card.CardName}' bound for {Turns} turns; auto-casts each turn start.");
			},
		};
		s.RequestCardChoice(req);
	}
}

public sealed class ReplicateLastSpellLeafEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ReplicateSpellAura(caster, unit));
		s.Log("[ReplicateLastSpell] Your next spell will echo once.");
	}
}

/// <summary>
/// Spell Storm: reveal the top <see cref="Count"/> cards and cast their top halves
/// for free — in an order the PLAYER chooses (2026-07-29). Sequencing three free
/// spells is most of the card's skill expression, and the seam's OrderMatters flag
/// exists for exactly this: pick-all-N is degenerate, ORDER-all-N is not.
///
/// (Previously this cast only the single top card, in deck order, whatever the
/// card's `count` said — the loader never read it.)
/// Targets are inherited from the storm's own cast, as before; per-cast retargeting
/// stays future work and the card text's "targeting enemies of your choice" is
/// honest only up to that limit.
/// JSON: { "type": "cast_deck_top", "count": n, "half": "top" }
/// </summary>
public sealed class CastDeckTopEffect : EffectBase
{
	public int Count;
	public CastDeckTopEffect(int count = 1) { Count = Math.Max(1, count); }

	private void CastOne(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap,
						 UnitDeckData deck, Card card)
	{
		if (card.TopHalf?.Effects == null || card.TopHalf.Effects.Length == 0)
		{
			deck.DiscardPile.Add(card);
			s.Log($"[CastDeckTop] '{card.CardName}' has no effects.");
			return;
		}
		s.Log($"[CastDeckTop] Auto-casting '{card.CardName}'.");
		foreach (var eff in card.TopHalf.Effects)
			eff.Resolve(s, caster, targets, snap);
		deck.DiscardPile.Add(card);
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		var deck = unit?.DeckData;
		if (deck == null)
			return;

		if (CombatSim.Active)
			return;

		if (deck.DrawPile.Count < Count && deck.DiscardPile.Count > 0)
			deck.Reshuffle();

		int take = Math.Min(Count, deck.DrawPile.Count);
		if (take <= 0)
		{ s.Log("[CastDeckTop] Deck is empty."); return; }

		var revealed = deck.DrawPile.GetRange(0, take);
		deck.DrawPile.RemoveRange(0, take);          // held out until answered

		if (take == 1)
		{
			CastOne(s, caster, targets, snap, deck, revealed[0]);
			return;
		}

		var req = new CardChoiceRequest
		{
			Title = "Spell Storm",
			Prompt = $"Cast these {take} top halves for free — click them in the order they should resolve.",
			Owner = unit,
			Candidates = revealed,
			PickCount = take,
			OrderMatters = true,
			Source = "CastDeckTop",
			OnChosen = chosen =>
			{
				// Resolve in click order; anything the UI failed to hand back still
				// resolves (revealed order) — a card must not vanish because a modal
				// glitched.
				var order = new List<Card>();
				if (chosen != null)
					foreach (var c in chosen)
						if (c != null && revealed.Contains(c) && !order.Contains(c))
							order.Add(c);
				foreach (var c in revealed)
					if (c != null && !order.Contains(c))
						order.Add(c);

				foreach (var c in order)
					CastOne(s, caster, targets, snap, deck, c);
				s.OnDrawCards?.Invoke(unit);
			},
		};
		s.RequestCardChoice(req);
	}
}

public sealed class ConvergenceLeafEffect : EffectBase
{
	public int Damage, Range, Turns;
	public ConvergenceLeafEffect(int dmg, int range, int turns) { Damage = dmg; Range = range; Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ConvergenceAura(Damage, Range, Turns, caster, unit));
		s.Log($"[Convergence] Each spell pulses {Damage} dmg to nearest enemy for {Turns} turn(s).");
	}
}

/// <summary>
/// Magnum Opus (2026-07-29, rebuilt on the choice seam): CHOOSE a card in your hand;
/// it becomes Perfected for the fight — both halves cost 0 (a permanent per-card
/// discount that survives casting), it resolves with +<see cref="BonusDamage"/>
/// (pinned in Resolver.ResolveTop), and it returns to hand instead of discarding
/// (CombatManager's discard step checks GameState.PerfectedCards).
///
/// The old version granted the caster +3 BonusSpellDamage on EVERY spell forever and
/// never asked anything — a different, quietly stronger card than the one printed.
/// JSON: { "type": "perfect_card", "count": n, "bonus": n }
/// </summary>
public sealed class PerfectCardEffect : EffectBase
{
	public int BonusDamage, Count;
	public PerfectCardEffect(int bd, int count) { BonusDamage = bd; Count = Math.Max(1, count); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		if (unit?.DeckData == null)
			return;

		if (CombatSim.Active)
			return;

		var hand = unit.DeckData.Hand;
		if (hand.Count == 0)
		{ s.Log("[PerfectCard] Hand is empty — nothing to perfect."); return; }

		int pick = Math.Min(Count, hand.Count);
		var req = new CardChoiceRequest
		{
			Title = "Magnum Opus",
			Prompt = $"Perfect {pick} card(s): both halves cost 0, +{BonusDamage} on resolve, " +
					 "and it returns to your hand when cast.",
			Owner = unit,
			Candidates = new List<Card>(hand),
			PickCount = pick,
			Source = "PerfectCard",
			OnChosen = chosen =>
			{
				if (chosen == null)
					return;
				foreach (var card in chosen)
				{
					if (card == null)
						continue;
					s.PerfectedCards[card.InstanceId] = BonusDamage;
					// 99 floors both halves at 0 through ManaCost.EffectiveAmount;
					// TryCastWithTargets skips consuming deltas on Perfected cards.
					s.AddCardDiscount(card, 99);
					s.Log($"[PerfectCard] '{card.CardName}' is Perfected — cost 0, +{BonusDamage}, not consumed on cast.");
				}
				s.OnDrawCards?.Invoke(unit);
			},
		};
		s.RequestCardChoice(req);
	}
}

/// <summary>Active modifier that applies a bonus to the next N spells the owner casts.
/// Fires via OnSpellCast (sets BonusDamage) and OnSpellResolved (clears it, draws, counts down).
///
/// SYMMETRY GUARD (2026-07-29): OnSpellCast runs BEFORE the stack drain in
/// CombatManager and OnSpellResolved runs AFTER it — but this modifier is
/// ADDED during the drain (when the queueing spell resolves). So it used to
/// miss its own cast's OnSpellCast yet get caught by its own cast's
/// OnSpellResolved: it subtracted a bonus that was never added, expired on
/// the spot, and permanently drove BonusSpellDamage negative — one playtest
/// fight decayed from 5 damage to −11 across four rounds, and the buff had
/// never actually applied to anything. _armedThisCast makes the remove-side
/// fire only when the add-side actually ran, which both stops the drain and
/// lets the modifier survive to buff the genuinely-next spell.</summary>
public sealed class QueuedSpellModifier : PersistentEffect
{
    public int BonusDamage;
    public int ExtraDraw;
    public int AppliesTo;
    public string GrantStatus;
    public int GrantStatusDuration;
    public Unit OwnerUnit;

    /// <summary>True only between an OnSpellCast that applied this bonus and
    /// the matching OnSpellResolved. Guards against the self-consume bug
    /// described in the class summary.</summary>
    private bool _armedThisCast;

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
        _armedThisCast = true;
        s.Log($"[QueuedModifier] Applied +{BonusDamage} dmg to this spell.");
    }

    public override void OnSpellResolved(GameState s, Unit casterUnit, TargetSet targets)
    {
        // Only unwind what OnSpellCast actually applied. Without this guard
        // the modifier fires on the very cast that created it (see class
        // summary) and bleeds BonusSpellDamage below zero.
        if (!_armedThisCast || casterUnit != OwnerUnit || AppliesTo <= 0)
            return;
        _armedThisCast = false;
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
