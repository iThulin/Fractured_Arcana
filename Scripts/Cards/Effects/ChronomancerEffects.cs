using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ChronomancerEffects.cs
//
// Purpose:        Chronomancer school effects — foresight, tempo, anchors, phase
//                 tiles, redirects, and temporal persistent effects.
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase, core leaves),
//                 PersistentEffect.cs (PersistentEffect base),
//                 CardScriptRegistry.Chronomancer.cs (registration)
// Notes:          Extracted from Effect.cs / CompositeEffects.cs /
//                 PersistentEffect.cs — pure move, no behavior change.
// ============================================================

/// <summary>
/// Adds Foresight charges to the active caster's FateAttunement.
/// JSON: { "type": "gain_foresight", "amount": n }
/// </summary>
public sealed class GainForesightEffect : EffectBase
{
	public int Amount;
	public GainForesightEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is FateAttunement fate)
		{
			fate.GainCharges(Amount);
			s.Log($"[GainForesight] +{Amount} Foresight (now {fate.Charges}).");
		}
		else
		{
			s.Log("[GainForesight] No FateAttunement on caster — no-op.");
		}
	}
}

/// <summary>
/// Reveals the top <see cref="Look"/> cards of the caster's draw pile and asks the
/// player to keep <see cref="Keep"/> of them. Kept cards go to hand; the rest go to
/// the BOTTOM of the draw pile in the order they were revealed.
///
/// (2026-07-28) This used to draw the top <see cref="Keep"/> cards and log
/// "pending UI" — i.e. the card's whole point, the choice, did not exist. It now
/// publishes a <see cref="CardChoiceRequest"/> and finishes in a continuation; see
/// CardChoice.cs for why that is a continuation rather than an await.
///
/// <see cref="Discount"/> is still NOT implemented, and says so in the log rather
/// than silently doing nothing: a per-card cost reduction needs a per-card modifier
/// field, which does not exist yet. GameState.NextSpellCostReduction is single-use
/// and global, so it cannot express "this specific card costs 1 less".
///
/// JSON: { "type": "scry", "look": n, "keep": n, "discount": n }
/// </summary>
public sealed class ScryEffect : EffectBase
{
	public int Look;
	public int Keep;
	public int Discount;

	/// <summary>True = picked cards go to HAND ("draw 1, bottom the rest" — the
	/// look/keep/draw cards). False = they go back on TOP of the draw pile ("scry N
	/// and reorder" — the count cards). Unpicked cards always go to the bottom, so the
	/// two modes differ only in where the winners land.</summary>
	public bool ToHand;

	public ScryEffect(int look, int keep, int discount, bool toHand = true)
	{
		Look = look;
		Keep = keep;
		Discount = discount;
		ToHand = toHand;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
		{ s.Log("[Scry] No active caster — no-op."); return; }

		var deck = casterUnit.DeckData;
		if (deck == null)
		{ s.Log($"[Scry] {casterUnit.Name} has no deck — no-op."); return; }

		// R22: the drag preview replays real effects. A preview that popped a modal
		// would be unusable, and one that MOVED CARDS would corrupt the deck on hover.
		if (CombatSim.Active)
			return;

		// Reveal off the top, reshuffling once if the pile runs dry — same courtesy
		// Draw() extends, so scrying near the end of a deck is not a dead card.
		if (deck.DrawPile.Count < Look && deck.DiscardPile.Count > 0)
			deck.Reshuffle();

		int look = Math.Min(Look, deck.DrawPile.Count);
		if (look <= 0)
		{ s.Log($"[Scry] {casterUnit.Name}'s deck is empty — nothing to look at."); return; }

		var revealed = deck.DrawPile.GetRange(0, look);
		deck.DrawPile.RemoveRange(0, look);      // held OUT of the deck until answered

		int keep = Math.Clamp(Keep, 0, look);

		if (Discount > 0)
			s.Log($"[Scry] discount {Discount} is not implemented — kept cards cost full price.");

		var req = new CardChoiceRequest
		{
			Title = "Scry",
			Prompt = ToHand
				? $"Keep {keep} of {look}. The rest go to the bottom of your deck."
				: $"Put {keep} of {look} back on top. The rest go to the bottom.",
			Owner = casterUnit,
			Candidates = revealed,
			PickCount = keep,
			Source = "Scry",
			OnChosen = chosen =>
			{
				// Unpicked first, to the BOTTOM. Then the picked ones — to hand, or
				// back on TOP in the order the player chose them. Both lists come from
				// `revealed`, so a card can be neither lost nor duplicated whatever
				// the UI hands back.
				foreach (var c in revealed)
					if (c != null && (chosen == null || !chosen.Contains(c)))
						deck.DrawPile.Add(c);

				if (chosen != null)
				{
					if (ToHand)
					{
						foreach (var c in chosen)
							if (c != null) deck.Hand.Add(c);
					}
					else
					{
						for (int i = chosen.Count - 1; i >= 0; i--)
							if (chosen[i] != null) deck.DrawPile.Insert(0, chosen[i]);
					}
				}

				s.OnDrawCards?.Invoke(casterUnit);
				string names = chosen == null || chosen.Count == 0
					? "nothing"
					: string.Join(", ", chosen.ConvertAll(c => c?.CardName ?? "a card"));
				s.Log($"[Scry] {casterUnit.Name} {(ToHand ? "keeps" : "puts back on top")} {names} " +
					  $"({look - (chosen?.Count ?? 0)} to the bottom).");
			},
		};

		// Two effects could each want a choice in one resolution. The slot holds one,
		// so queue behind whatever is already there — dropping a request would
		// silently eat the cards this one just held out of the deck.
		//
		// The chained request DISPATCHES rather than re-filling the slot: by the time
		// that continuation runs, Resolver.ResolveTop has already cleared PendingChoice
		// and stopped looking at it, so a second assignment would sit there unread
		// until some unrelated later resolution happened to publish it.
		if (s.PendingChoice != null)
			s.PendingChoice.Then(_ => s.DispatchCardChoice(req));
		else
			s.PendingChoice = req;
	}
}

/// <summary>
/// Leaf that spawns a DelayedDamageEffect on GameState.ActiveEffects.
/// JSON: { "type": "delayed_damage", "amount": n, "turns": n }
/// </summary>
public sealed class DelayedDamageLeafEffect : EffectBase
{
	public int DamagePerTick;
	public int Ticks;

	public DelayedDamageLeafEffect(int damagePerTick, int ticks)
	{
		DamagePerTick = damagePerTick;
		Ticks = ticks;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null || s?.Grid == null)
		{ s?.Log("[DelayedDamage] No targets or grid."); return; }

		s.ActiveEffects ??= new List<PersistentEffect>();

		foreach (var obj in targets.Items)
		{
			Vector2I coord;
			if (obj is TileData td)
				coord = td.Axial;
			else if (obj is Unit u && u.CurrentTile != null)
				coord = u.CurrentTile.Axial;
			else
				continue;

			s.ActiveEffects.Add(new DelayedDamageEffect(coord, DamagePerTick, Ticks, caster));
			s.Log($"[DelayedDamage] Scheduled {DamagePerTick}×{Ticks} ticks at {coord}.");
		}
	}
}

/// <summary>
/// Ticks once per turn. Deals DamagePerTick to whatever enemy stands on
/// TargetCoord. Stored in GameState.ActiveEffects.
/// </summary>
public class DelayedDamageEffect : PersistentEffect
{
	public Vector2I TargetCoord;
	public int DamagePerTick;

	public DelayedDamageEffect(Vector2I coord, int damagePerTick, int ticks, Entity owner)
	{
		TargetCoord = coord;
		DamagePerTick = damagePerTick;
		TurnsRemaining = ticks;
		Owner = owner;
		// Entity can't be cast to Unit directly — match by name instead
		// _ownerTeamId is set externally via SetOwnerTeam if needed,
		// or resolved at tick time:
	}

	// Remove _ownerTeamId entirely and resolve team at tick time:
	public override void Tick(GameState s)
	{
		if (s?.Grid != null)
		{
			var tile = s.Grid.GetTile(TargetCoord);
			if (tile?.Occupant is Unit target && target.Stats.IsAlive)
			{
				// Find owner's team by name-matching against UnitsInPlay
				var ownerUnit = s.UnitsInPlay?.Find(u => u != null && u.Name == Owner?.Name);
				if (ownerUnit == null || target.TeamId != ownerUnit.TeamId)
				{
					target.ApplyDamage(DamagePerTick);
					s.Log($"[DelayedDamage] {target.Name} takes {DamagePerTick} at {TargetCoord}. " +
						  $"{TurnsRemaining - 1} tick(s) left.");
				}
			}
			else
			{
				s.Log($"[DelayedDamage] No valid target at {TargetCoord} — tick skipped.");
			}
		}
		TurnsRemaining--;
	}
}

/// <summary>
/// Reveals enemy intent (currently logs HP/status to console and grants
/// Foresight). Full HUD reveal is future UI work.
/// JSON: { "type": "peek_intent" }
/// </summary>
public sealed class PeekIntentEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;

		if (s?.UnitsInPlay == null)
			return;

		s.Log("[PeekIntent] Enemy intel:");
		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive)
				continue;
			if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
				continue;

			var statuses = unit.Stats.StatusEffects.Count > 0
				? string.Join(", ", unit.Stats.StatusEffects.Keys)
				: "none";
			s.Log($"  {unit.Name}: {unit.Stats.Health}/{unit.Stats.MaxHealth}HP " +
				  $"| Unit={unit.DefinitionId} | Status=[{statuses}]");
		}
	}
}

/// <summary>
/// Grants a named stat boost for <see cref="Turns"/> turns.
/// Movement: adds directly to MovePoints this turn; registers a cleanup
///   callback on GameState.OnTurnEndCleanups for multi-turn restoration.
/// Action: adds to CurrentActionPoints immediately.
/// Damage: adds to BonusSpellDamage; registers cleanup.
/// JSON: { "type": "temp_buff", "stat": "movement"|"action"|"damage", "amount": n, "turns": n }
/// </summary>
public sealed class TempBuffEffect : EffectBase
{
	public string Stat;
	public int Amount;
	public int Turns;

	public TempBuffEffect(string stat, int amount, int turns)
	{
		Stat = stat;
		Amount = amount;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;

		foreach (var obj in targets.Items)
		{
			var unit = ResolveTargetUnit(s, obj);
			if (unit == null)
				continue;

			switch (Stat)
			{
				case "movement":
					// Movespeed currency (BonusMoveRange, per-turn, auto-resets in StartTurn).
					// The immediate grant covers this turn; multi-turn buffs re-apply via
					// MovementBuffEffect. No end-of-turn cleanup needed — the reset handles it.
					unit.Stats.BonusMoveRange += Amount;
					unit.RefreshHealthBar();
					s.Log($"[TempBuff] {unit.Name} +{Amount} move range for {Turns} turn(s).");

					if (Turns > 1)
					{
						s.ActiveEffects ??= new List<PersistentEffect>();
						s.ActiveEffects.Add(new MovementBuffEffect(unit, Amount, Turns));
					}
					break;

				case "action":
					unit.CurrentActionPoints += Amount;
					s.Log($"[TempBuff] {unit.Name} +{Amount} action(s) (now {unit.CurrentActionPoints}).");
					// Actions don't roll over — no cleanup needed (they're consumed or lost at turn end)
					break;

				case "damage":
					unit.BonusSpellDamage += Amount;
					s.Log($"[TempBuff] {unit.Name} +{Amount} spell damage (now {unit.BonusSpellDamage}).");

					int capturedDmg = Amount;
					var capturedDmgUnit = unit;
					s.OnTurnEndCleanups ??= new List<Action>();
					for (int i = 0; i < Turns; i++)
					{
						s.OnTurnEndCleanups.Add(() =>
						{
							capturedDmgUnit.BonusSpellDamage =
								Math.Max(0, capturedDmgUnit.BonusSpellDamage - capturedDmg);
						});
					}
					break;

				default:
					s.Log($"[TempBuff] Unknown stat '{Stat}' — no-op.");
					break;
			}
		}
	}
}

/// <summary>
/// Modifies mana costs.
///   "self_next"  — your next spell costs Amount less (consumed after one spell).
///   "enemy"      — enemy spells cost Amount more this round.
///
/// Reads/writes fields added to GameState:
///   GameState.NextSpellCostReduction (int)
///   GameState.EnemySpellCostIncrease (int)
///
/// TryCastWithTargets applies NextSpellCostReduction as an additional
/// manaDiscount. See ChronomancerWiringComplete.md §3.
/// JSON: { "type": "cost_modify", "amount": n, "scope": "self_next"|"enemy" }
/// </summary>
public sealed class CostModifyEffect : EffectBase
{
	public int Amount;
	public string Scope;

	public CostModifyEffect(int amount, string scope)
	{
		Amount = amount;
		Scope = scope;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		switch (Scope)
		{
			case "self_next":
				s.NextSpellCostReduction = Math.Max(s.NextSpellCostReduction, Amount);
				s.Log($"[CostModify] Next spell costs {Amount} less.");
				break;

			case "enemy":
				s.EnemySpellCostIncrease += Amount;
				s.Log($"[CostModify] Enemy spells cost +{Amount} this round.");
				// Reset in StartEnemyTurn — see wiring doc §7.
				break;

			default:
				s.Log($"[CostModify] Unknown scope '{Scope}' — no-op.");
				break;
		}
	}
}

/// <summary>
/// Adds <see cref="Turns"/> postponed-turn tokens to each target enemy.
/// In RunEnemyTurn, before ActEnemyUnit: if PostponedTurns > 0, skip
/// the unit and decrement. See wiring doc §5.
/// JSON: { "type": "postpone", "turns": n }
/// </summary>
public sealed class PostponeEffect : EffectBase
{
	public int Turns;
	public PostponeEffect(int turns) { Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;

		foreach (var obj in targets.Items)
		{
			var unit = ResolveTargetUnit(s, obj);
			if (unit == null || unit.IsPlayerControlled)
				continue;

			unit.PostponedTurns += Turns;
			unit.ApplyStatus("delayed", Turns);
			s.Log($"[Postpone] {unit.Name} postponed {Turns} turn(s) (total: {unit.PostponedTurns}).");
		}
	}
}

/// <summary>
/// All living enemies skip their next <see cref="Turns"/> turns.
/// Uses the same PostponedTurns mechanism as PostponeEffect but applies
/// to every enemy simultaneously.
/// JSON: { "type": "skip_enemy_turn", "turns": n }
/// </summary>
public sealed class SkipEnemyTurnEffect : EffectBase
{
	public int Turns;
	public SkipEnemyTurnEffect(int turns) { Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.IsPlayerControlled)
				continue;
			if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
				continue;

			unit.PostponedTurns += Turns;
			unit.ApplyStatus("delayed", Turns);
			s.Log($"[SkipEnemyTurn] {unit.Name} skips {Turns} turn(s).");
		}
	}
}

/// <summary>
/// Schedules a child effect to resolve at the end of the player's turn
/// <see cref="Turns"/> rounds from now. Stored in GameState.Almanac.
/// The Almanac is ticked in StartPlayerTurn — see wiring doc §6.
/// JSON: { "type": "schedule", "turns": n, "do": { ...effect... } }
/// </summary>
public sealed class ScheduleLeafEffect : EffectBase
{
	public int Turns;
	public IEffect Child;

	public ScheduleLeafEffect(int turns, IEffect child)
	{
		Turns = turns;
		Child = child;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.Almanac ??= new List<AlmanacEntry>();
		s.Almanac.Add(new AlmanacEntry
		{
			TurnsRemaining = Math.Max(0, Turns),
			Child = Child,
			Caster = caster,
			Targets = targets,
			Snapshot = snap,
			Label = Child?.GetType().Name ?? "Scheduled"
		});
		s.Log($"[Schedule] Effect scheduled for {Turns} turn(s) from now.");
	}
}

/// <summary>
/// Moves the soonest Almanac entry one turn closer to firing.
/// If it was already at 1 (fires next turn), it fires immediately.
/// JSON: { "type": "advance" }
/// </summary>
public sealed class AdvanceEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.Almanac == null || s.Almanac.Count == 0)
		{
			s.Log("[Advance] No scheduled entries to advance.");
			return;
		}

		// Find the soonest entry belonging to the caster
		var entry = s.Almanac
			.Where(e => e.Caster == caster)
			.OrderBy(e => e.TurnsRemaining)
			.FirstOrDefault()
			?? s.Almanac.OrderBy(e => e.TurnsRemaining).First();

		entry.TurnsRemaining = Math.Max(0, entry.TurnsRemaining - 1);

		if (entry.IsReady)
		{
			s.Log($"[Advance] Entry reached 0 — firing immediately.");
			entry.Child?.Resolve(s, entry.Caster, entry.Targets, entry.Snapshot);
			s.Almanac.Remove(entry);
		}
		else
		{
			s.Log($"[Advance] Entry moved to {entry.TurnsRemaining} turn(s) away.");
		}
	}
}

/// <summary>
/// Resolves the soonest caster Almanac entry immediately.
/// Spends 1 Foresight if the caster has a FateAttunement.
/// JSON: { "type": "fast_forward" }
/// </summary>
public sealed class FastForwardEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.Almanac == null || s.Almanac.Count == 0)
		{
			s.Log("[FastForward] No scheduled entries.");
			return;
		}

		var entry = s.Almanac
			.Where(e => e.Caster == caster)
			.OrderBy(e => e.TurnsRemaining)
			.FirstOrDefault()
			?? s.Almanac.OrderBy(e => e.TurnsRemaining).First();

		s.Log($"[FastForward] Firing scheduled entry immediately.");
		entry.Child?.Resolve(s, entry.Caster, entry.Targets, entry.Snapshot);
		s.Almanac.Remove(entry);

		// Spend 1 Foresight
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is FateAttunement fate)
			fate.SpendCharges(1);
	}
}

/// <summary>
/// Marks the caster's current tile as an anchor for <see cref="Turns"/> turns.
/// Stores the coord on <c>Unit.AnchorCoord</c> and <c>Unit.AnchorTurnsRemaining</c>.
/// The anchor expires in <c>StartPlayerTurn</c> — see wiring doc §8.
/// JSON: { "type": "set_anchor", "turns": n }
/// </summary>
public sealed class SetAnchorEffect : EffectBase
{
	public int Turns;
	public SetAnchorEffect(int turns) { Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null)
		{
			s.Log("[SetAnchor] Caster has no tile — no-op.");
			return;
		}

		casterUnit.AnchorCoord = casterUnit.CurrentTile.Axial;
		casterUnit.AnchorTurnsRemaining = Turns;
		s.Log($"[SetAnchor] Anchor set at {casterUnit.AnchorCoord} for {Turns} turn(s).");
	}
}

/// <summary>
/// Teleports the caster to their stored anchor tile.
/// No-ops if no anchor is set or the anchor tile is occupied.
/// JSON: { "type": "teleport_to_anchor" }
/// </summary>
public sealed class TeleportToAnchorEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null || casterUnit.AnchorCoord == null)
		{
			s.Log("[TeleportToAnchor] No anchor set — no-op.");
			return;
		}

		var tile = s.Grid?.GetTile(casterUnit.AnchorCoord.Value);
		if (tile == null || !tile.CanEnter(casterUnit))
		{
			s.Log("[TeleportToAnchor] Anchor tile unavailable.");
			return;
		}

		casterUnit.CurrentTile?.ClearOccupant(casterUnit);
		casterUnit.PlaceOnTile(tile);
		s.Log($"[TeleportToAnchor] {casterUnit.Name} snapped back to {tile.Axial}.");
	}
}

/// <summary>
/// Registers up to <see cref="Count"/> tiles near the target as Phase tiles on
/// GameState.PhaseTiles. The caster may teleport between them for free once per
/// turn. The actual teleport is handled by <see cref="TeleportToPhaseTileEffect"/>.
/// JSON: { "type": "create_phase_tiles", "count": n, "turns": n }
/// </summary>
public sealed class CreatePhaseTilesEffect : EffectBase
{
	public int Count;
	public int Turns;

	public CreatePhaseTilesEffect(int count, int turns)
	{
		Count = count;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null)
			return;

		s.PhaseTiles ??= new List<Vector2I>();
		s.PhaseTileTurnsRemaining = Turns;

		// Use the target tiles, or fall back to nearby empty tiles
		int added = 0;
		if (targets?.Items != null)
		{
			foreach (var obj in targets.Items)
			{
				if (added >= Count)
					break;
				Vector2I coord;
				if (obj is TileData td)
					coord = td.Axial;
				else if (obj is Unit u && u.CurrentTile != null)
					coord = u.CurrentTile.Axial;
				else
					continue;

				if (!s.PhaseTiles.Contains(coord))
				{
					s.PhaseTiles.Add(coord);
					added++;
					s.Log($"[PhaseTiles] Added tile {coord}.");
				}
			}
		}

		s.Log($"[PhaseTiles] Network of {s.PhaseTiles.Count} tile(s) active for {Turns} turn(s).");
	}
}

/// <summary>
/// Teleports the caster to a phase tile.
/// Used by the Temporal Anchor and Phase Anchor snap-back mechanics.
/// JSON: { "type": "teleport_to_phase_tile" }
/// </summary>
public sealed class TeleportToPhaseTileEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null || s.PhaseTiles == null || s.PhaseTiles.Count == 0)
		{
			s.Log("[PhaseTile] No phase tiles registered — no-op.");
			return;
		}

		// Find the closest phase tile that isn't the caster's current position
		Vector2I? best = null;
		int bestDist = int.MaxValue;
		foreach (var coord in s.PhaseTiles)
		{
			if (casterUnit.CurrentTile != null && coord == casterUnit.CurrentTile.Axial)
				continue;
			int dist = s.Grid?.Distance(casterUnit.CurrentTile?.Axial ?? coord, coord) ?? 0;
			if (dist < bestDist)
			{ bestDist = dist; best = coord; }
		}

		if (best == null)
		{ s.Log("[PhaseTile] No valid destination."); return; }

		var tile = s.Grid.GetTile(best.Value);
		if (tile == null || !tile.CanEnter(casterUnit))
		{ s.Log("[PhaseTile] Destination blocked."); return; }

		casterUnit.CurrentTile?.ClearOccupant(casterUnit);
		casterUnit.PlaceOnTile(tile);
		s.Log($"[PhaseTile] {casterUnit.Name} teleported to {tile.Axial}.");
	}
}

/// <summary>
/// Directly re-resolves all effects of <c>GameState.LastResolvedItem</c>
/// using the original targets. <see cref="ValueMult"/> is stored on the
/// snapshot — DealDamageEffect reads <c>EffectSnapshot.DamageMultiplier</c>
/// and applies it. See wiring doc §9 for the DealDamageEffect hook.
/// JSON: { "type": "echo_last", "value_mult": f }
/// </summary>
public sealed class EchoLastEffect : EffectBase
{
	public float ValueMult;
	public EchoLastEffect(float valueMult) { ValueMult = valueMult; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var last = s.LastResolvedItem;
		if (last == null)
		{
			s.Log("[EchoLast] No last resolved item — no-op.");
			return;
		}

		// Build a modified snapshot with the value multiplier
		var echoSnap = new EffectSnapshot
		{
			DamageMultiplier = ValueMult,
			// Copy any other fields from the original snapshot as needed
		};

		s.Log($"[EchoLast] Echoing '{last.Ability?.Name}' at {ValueMult * 100f}% value.");
		foreach (var eff in last.Ability.Effects)
			eff.Resolve(s, last.Caster, last.Targets, echoSnap);
	}
}

/// <summary>
/// Re-resolves <c>GameState.LastResolvedItem</c>. When <see cref="Retarget"/>
/// is true, uses the new targets passed into this effect instead of the
/// original. Spends 1 Foresight.
/// JSON: { "type": "rewind_last", "retarget": bool }
/// </summary>
public sealed class RewindLastEffect : EffectBase
{
	public bool Retarget;
	public RewindLastEffect(bool retarget) { Retarget = retarget; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var last = s.LastResolvedItem;
		if (last == null)
		{
			s.Log("[RewindLast] Nothing to rewind — no-op.");
			return;
		}

		if (last.Ability?.Effects != null && last.Ability.Effects.OfType<RewindLastEffect>().Any())
		{
			s.Log("[RewindLast] Last spell contains a rewind — cannot rewind a rewind. No-op.");
			return;
		}

		var resolveTargets = Retarget ? targets : last.Targets;
		s.Log($"[RewindLast] Rewinding '{last.Ability?.Name}'" +
			  (Retarget ? " with new targets." : " with original targets."));

		foreach (var eff in last.Ability.Effects)
			eff.Resolve(s, last.Caster, resolveTargets, last.Snapshot);

		// Spend 1 Foresight
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is FateAttunement fate)
			fate.SpendCharges(1);
	}
}

/// <summary>
/// Reverses the order of all items on GameStack. The item that would have
/// resolved last now resolves first. Requires GameStack.Reverse() — see wiring doc §10.
/// JSON: { "type": "reverse_stack" }
/// </summary>
public sealed class ReverseStackEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.StackCount() == 0)
		{
			s.Log("[ReverseStack] Stack is empty — no-op.");
			return;
		}

		s.Stack.Reverse();
		s.Log($"[ReverseStack] Stack reversed ({s.StackCount()} item(s)).");
	}
}

/// <summary>
/// Retargets the top enemy spell on the stack.
///   "random_enemy" — picks a random enemy other than the original target.
///   "chosen"       — uses the targets passed into this effect (costs 1 Foresight).
///   "decoy"        — redirects to the nearest live decoy.
///
/// Requires GameStack.PeekTop() — see wiring doc §11.
/// JSON: { "type": "redirect", "to": "random_enemy"|"chosen"|"decoy" }
/// </summary>
public sealed class RedirectEffect : EffectBase
{
	public string To;
	private static readonly Random _rng = new();

	public RedirectEffect(string to) { To = to; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var top = s.Stack.PeekTop();
		if (top == null)
		{
			s.Log("[Redirect] Stack is empty — no-op.");
			return;
		}

		var casterUnit = s.ActiveCasterUnit;

		Unit newTarget = null;

		switch (To)
		{
			case "random_enemy":
				{
					var originalItems = top.Targets?.Items ?? new List<object>();
					var candidates = s.UnitsInPlay
						.Where(u => u != null && u.Stats.IsAlive
								 && (casterUnit == null || u.TeamId != casterUnit.TeamId)
								 && !originalItems.Contains(u))
						.ToList();
					if (candidates.Count == 0)
						candidates = s.UnitsInPlay.Where(u => u != null && u.Stats.IsAlive
								 && (casterUnit == null || u.TeamId != casterUnit.TeamId)).ToList();
					if (candidates.Count > 0)
						newTarget = candidates[_rng.Next(candidates.Count)];
					break;
				}

			case "chosen":
				{
					newTarget = targets?.Items?.OfType<Unit>().FirstOrDefault();
					// Spend 1 Foresight for chosen redirect
					if (casterUnit?.Attunement is FateAttunement fate)
						fate.SpendCharges(1);
					break;
				}

			case "decoy":
				{
					newTarget = s.UnitsInPlay.FirstOrDefault(u => u != null && u.IsDecoy && u.Stats.IsAlive);
					break;
				}
		}

		if (newTarget == null)
		{
			s.Log($"[Redirect] No valid redirect target found (to={To}).");
			return;
		}

		top.Targets = new TargetSet { Items = new List<object> { newTarget } };
		s.Log($"[Redirect] Spell '{top.Ability?.Name}' redirected to {newTarget.Name}.");
	}
}

/// <summary>
/// Redirects an enemy's current movement/charge target.
/// Sets <c>Unit.RedirectedChargeTile</c> on the target unit.
/// CombatManager's ActSoldier/ActBrute reads this field and moves there
/// instead of toward the player. See wiring doc §12.
/// JSON: { "type": "redirect_charge", "to": "chosen" }
/// </summary>
public sealed class RedirectChargeEffect : EffectBase
{
	public string To;
	public RedirectChargeEffect(string to) { To = to; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		// Find the target enemy unit
		var enemy = targets?.Items?.OfType<Unit>()
			.FirstOrDefault(u => u != null && !u.IsPlayerControlled);

		if (enemy == null)
		{ s.Log("[RedirectCharge] No enemy target — no-op."); return; }

		// Find redirect destination: target tile or random walkable tile
		TileData dest = null;
		switch (To)
		{
			case "chosen":
				dest = targets?.Items?.OfType<TileData>().FirstOrDefault()
					?? targets?.Items?.Skip(1).OfType<Unit>().FirstOrDefault()?.CurrentTile;
				break;
		}

		if (dest != null && dest.CanEnter(enemy))
		{
			enemy.RedirectedChargeTile = dest.Axial;
			s.Log($"[RedirectCharge] {enemy.Name} charge redirected to {dest.Axial}.");
		}
		else
		{
			s.Log("[RedirectCharge] No valid charge destination — no-op.");
		}
	}
}

/// <summary>
/// Sets <c>GameState.RedirectAllTurnsRemaining</c> to <see cref="Turns"/>.
/// CombatManager.FindNearestPlayerUnit checks this flag and returns a random
/// other enemy unit instead of the nearest player unit. See wiring doc §13.
/// JSON: { "type": "redirect_all", "to": "random_enemy"|"chosen", "turns": n }
/// </summary>
public sealed class RedirectAllEffect : EffectBase
{
	public string To;
	public int Turns;

	public RedirectAllEffect(string to, int turns)
	{
		To = to;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.RedirectAllTurnsRemaining = Math.Max(s.RedirectAllTurnsRemaining, Turns);
		s.Log($"[RedirectAll] All enemy actions redirected for {Turns} turn(s).");
	}
}

/// <summary>
/// Adds an <see cref="ExtraTurnPersistentEffect"/> to GameState.ActiveEffects.
/// The Chronomancer gets a second turn each round with <see cref="Mana"/> mana
/// and draws <see cref="Draw"/> cards. See wiring doc §14 for the CombatManager hook.
/// JSON: { "type": "extra_turn", "mana": n, "draw": n }
/// </summary>
public sealed class ExtraTurnLeafEffect : EffectBase
{
	public int Mana;
	public int Draw;

	public ExtraTurnLeafEffect(int mana, int draw)
	{
		Mana = mana;
		Draw = draw;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		// Only one extra-turn effect at a time
		if (s.HasActiveEffect<ExtraTurnPersistentEffect>(caster))
		{
			s.Log("[ExtraTurn] Already active — no-op.");
			return;
		}

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ExtraTurnPersistentEffect(Mana, Draw, caster));
		s.Log($"[ExtraTurn] Granted: {Mana} mana, draw {Draw} per extra turn.");
	}
}

/// <summary>
/// Spawns a decoy unit on the target tile using <c>GameState.OnSummonRequested</c>.
/// The decoy has <see cref="HP"/> health and expires after <see cref="Turns"/> turns
/// (a DelayedDamage effect kills it). Enemy units within a Redirect Aura's radius
/// must target the decoy instead of player units.
/// JSON: { "type": "summon_decoy", "hp": n, "turns": n }
/// </summary>
public sealed class SummonDecoyLeafEffect : EffectBase
{
	public int HP;
	public int Turns;

	public SummonDecoyLeafEffect(int hp, int turns)
	{
		HP = hp;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{
			s.Log("[SummonDecoy] OnSummonRequested not registered — no-op.");
			return;
		}

		TileData tile = null;
		if (targets?.Items != null)
		{
			foreach (var obj in targets.Items)
			{
				if (obj is TileData td)
				{ tile = td; break; }
				if (obj is Unit u && u.CurrentTile != null)
				{ tile = u.CurrentTile; break; }
			}
		}

		// Fall back to an empty tile adjacent to the caster
		if (tile == null && s.ActiveCasterUnit?.CurrentTile != null && s.Grid != null)
		{
			var casterTile = s.ActiveCasterUnit.CurrentTile;
			foreach (var kvp in s.Grid.Tiles)
			{
				if (s.Grid.Distance(casterTile.Axial, kvp.Key) == 1
					&& kvp.Value != null
					&& !kvp.Value.IsOccupied
					&& kvp.Value.IsWalkable)
				{
					tile = kvp.Value;
					break;
				}
			}
		}

		if (tile == null)
		{ s.Log("[SummonDecoy] No valid tile — no-op."); return; }

		var casterUnit = s.ActiveCasterUnit;
		int teamId = casterUnit?.TeamId ?? 0;

		// Pass HP via a special summon kind; CombatManager.RegisterSummonHandler
		// must handle "decoy" — see wiring doc §15.
		var decoy = s.OnSummonRequested("decoy", tile, teamId);
		if (decoy != null)
		{
			decoy.Stats.MaxHealth = HP;
			decoy.Stats.Health = HP;
			decoy.IsDecoy = true;

			// Schedule the decoy's death after Turns turns
			var killEntry = new AlmanacEntry
			{
				TurnsRemaining = Turns,
				Child = new LethalDamageEffect(decoy),
				Caster = caster,
				Targets = new TargetSet { Items = new List<object> { decoy } },
				Label = "Decoy Expire"
			};
			s.Almanac ??= new List<AlmanacEntry>();
			s.Almanac.Add(killEntry);

			s.Log($"[SummonDecoy] Spawned {HP}HP decoy at {tile.Axial}. Expires in {Turns} turns.");
		}
	}
}

/// <summary>
/// Kills a specific unit — used internally to expire decoys.
/// Not registered in the JSON registry.
/// </summary>
public sealed class LethalDamageEffect : EffectBase
{
	private readonly Unit _target;
	public LethalDamageEffect(Unit target) { _target = target; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (_target != null && Godot.GodotObject.IsInstanceValid(_target) && _target.Stats.IsAlive)
		{
			_target.ApplyDamage(_target.Stats.Health);
			s.Log($"[LethalDamage] Decoy {_target.Name} expired.");
		}
	}
}

/// <summary>
/// Spawns a <see cref="RedirectAuraPersistentEffect"/> on GameState.ActiveEffects.
/// While active, enemy single-target actions within Radius must target the
/// nearest live decoy unit instead of player units.
/// JSON: { "type": "redirect_aura", "radius": n, "turns": n }
/// </summary>
public sealed class RedirectAuraLeafEffect : EffectBase
{
	public int Radius;
	public int Turns;

	public RedirectAuraLeafEffect(int radius, int turns)
	{
		Radius = radius;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		var center = casterUnit?.CurrentTile?.Axial ?? new Vector2I(0, 0);

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new RedirectAuraPersistentEffect(Radius, Turns, caster, center));
		s.Log($"[RedirectAura] Active within {Radius} for {Turns} turns.");
	}
}

/// <summary>
/// Spawns a <see cref="TemporalDecayFieldPersistentEffect"/> on GameState.ActiveEffects.
/// JSON: { "type": "temporal_decay_field", "damage": n, "scaling": n }
/// </summary>
public sealed class TemporalDecayFieldLeafEffect : EffectBase
{
	public int Damage;
	public int Scaling;

	public TemporalDecayFieldLeafEffect(int damage, int scaling)
	{
		Damage = damage;
		Scaling = scaling;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		// Only one decay field at a time
		if (s.GetActiveEffect<TemporalDecayFieldPersistentEffect>(caster) != null)
		{
			s.Log("[TemporalDecayField] Already active — no-op.");
			return;
		}

		var casterUnit = s.ActiveCasterUnit;
		bool enemiesOnly = false; // default: everyone decays. Upgrade to enemies-only handled by card upgrade.

		var effect = new TemporalDecayFieldPersistentEffect(Damage, Scaling, caster, enemiesOnly);
		effect.SetOwnerTeamId(casterUnit?.TeamId ?? 0);

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(effect);
		s.Log($"[TemporalDecayField] Board-wide decay: {Damage}/turn, scaling +{Scaling}/turn.");
	}
}

/// <summary>
/// Spawns an <see cref="EventControlPersistentEffect"/> that at the start of each
/// player turn automatically fast-forwards the nearest Almanac entry (or rewinds
/// the last resolved spell if no Almanac entries exist).
///
/// The "player chooses which event" UI will be a future upgrade. For now,
/// the logic auto-resolves the most beneficial action deterministically.
/// JSON: { "type": "event_control" }
/// </summary>
public sealed class EventControlLeafEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.HasActiveEffect<EventControlPersistentEffect>(caster))
		{
			s.Log("[EventControl] Already active — no-op.");
			return;
		}

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new EventControlPersistentEffect(caster));
		s.Log("[EventControl] Permanent event control granted.");
	}
}

/// <summary>
/// Permanent PersistentEffect. Each player turn: if the Almanac has entries,
/// fast-forward the soonest one. Otherwise, echo the last resolved spell.
/// </summary>
public class EventControlPersistentEffect : PersistentEffect
{
	public int EventsPerTurn = 1;

	public EventControlPersistentEffect(Entity owner)
	{
		TurnsRemaining = 999;
		Owner = owner;
	}

	public override void Tick(GameState s)
	{
		for (int i = 0; i < EventsPerTurn; i++)
		{
			if (s.Almanac != null && s.Almanac.Count > 0)
			{
				var entry = s.Almanac.OrderBy(e => e.TurnsRemaining).First();
				s.Log($"[EventControl] Fast-forwarding '{entry.Label}'.");
				entry.Child?.Resolve(s, entry.Caster, entry.Targets, entry.Snapshot);
				s.Almanac.Remove(entry);
			}
			else if (s.LastResolvedItem != null)
			{
				s.Log($"[EventControl] Echoing last spell '{s.LastResolvedItem.Ability?.Name}'.");
				foreach (var eff in s.LastResolvedItem.Ability.Effects)
					eff.Resolve(s, s.LastResolvedItem.Caster,
								s.LastResolvedItem.Targets, s.LastResolvedItem.Snapshot);
			}
		}
		// Never expire — permanent for the fight
	}
}

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
