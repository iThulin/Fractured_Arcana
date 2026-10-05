using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// EnchanterEffects.cs
//
// Purpose:        Enchanter school effects: glyphs, weave, control magic, and the
//                 Dominion / Grand Design persistent zones.
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase, core leaves),
//                 PersistentEffect.cs (PersistentEffect base),
//                 CardScriptRegistry.Enchanter.cs (registration)
// Notes:          Extracted from Effect.cs / CompositeEffects.cs /
//                 PersistentEffect.cs. Pure move, no behavior change.
// ============================================================

/// <summary>
/// Adds <see cref="Amount"/> Weave to the caster's working. If this reaches the cap the
/// attunement fires its Seventh Layer burst on its own.
/// JSON: { "type": "gain_weave", "amount": n }
/// </summary>
public sealed class GainWeaveEffect : EffectBase
{
	public int Amount;
	public GainWeaveEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.Attunement is not WeaveAttunement weave)
		{
			s.Log("[GainWeave] Caster has no Weave attunement. Ignored.");
			return;
		}
		weave.Add(Amount);
		s.Log($"[GainWeave] +{Amount} Weave (now {weave.Weave}/{WeaveAttunement.MaxWeave}).");
	}
}

/// <summary>
/// Deals <see cref="DamagePer"/> per prepared glyph the caster's team has on the board,
/// floored at <see cref="Minimum"/>, to each target. Counts the existing tile.Glyph field.
/// JSON: { "type": "damage_per_glyph", "amount": n, "min": m }
/// </summary>
public sealed class DamagePerGlyphEffect : EffectBase
{
	public int DamagePer;
	public int Minimum;

	public DamagePerGlyphEffect(int damagePer, int minimum = 0)
	{
		DamagePer = damagePer;
		Minimum = minimum;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		int count = CountFriendlyGlyphs(s, casterUnit);

		int dmg = Math.Max(Minimum, DamagePer * count);
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
		s.Log($"[DamagePerGlyph] {count} glyph(s) → {dmg} dmg to {hits} target(s) (min {Minimum}).");
	}

	/// <summary>Counts prepared glyphs owned by the caster's team across the grid.</summary>
	internal static int CountFriendlyGlyphs(GameState s, Unit casterUnit)
	{
		if (s?.Grid?.Tiles == null)
			return 0;
		int teamId = casterUnit?.TeamId ?? 0;
		int count = 0;
		foreach (var kvp in s.Grid.Tiles)
		{
			var tile = kvp.Value;
			if (tile?.Glyph == null)
				continue;
			if (casterUnit == null || tile.Glyph.OwnerTeam == teamId)
				count++;
		}
		return count;
	}
}

/// <summary>
/// Place enemy-enter glyphs on every tile within radius of the (first) target tile. 
/// JSON: { "type":"prepare_glyph_area","damage":n,"radius":n,"empty_only":bool }
/// </summary>
public sealed class PrepareGlyphAreaEffect : EffectBase
{
	public int Damage, Radius, StatusDuration; public string Status; public bool EmptyOnly, Reusable;
	public PrepareGlyphAreaEffect(int damage, int radius, string status, int statusDuration, bool emptyOnly, bool reusable)
	{ Damage = damage; Radius = radius; Status = status; StatusDuration = statusDuration; EmptyOnly = emptyOnly; Reusable = reusable; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		TileData center = null;
		if (targets?.Items != null && targets.Items.Count > 0)
			center = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
		center ??= c?.CurrentTile;
		if (center == null || s?.Grid?.Tiles == null)
			return;
		int placed = 0;
		foreach (var t in s.Grid.Tiles.Values)
		{
			if (s.Grid.Distance(center.Axial, t.Axial) > Radius)
				continue;
			if (EmptyOnly && t.Occupant != null)
				continue;
			if (InterfaceHelpers.PlaceEnterGlyph(s, c, t, Damage, Status, StatusDuration, Reusable))
				placed++;
		}
		s.Log($"[PrepareGlyphArea] placed {placed} glyph(s) in radius {Radius}.");
	}
}

/// <summary>
/// Relocate the target enemy onto the nearest friendly glyph (Unit.PlaceOnTile fires the glyph). 
/// Simplified from directional push; refine once hex-step helpers are confirmed.
/// JSON: { "type":"push_to_glyph","tiles":n } / "pull_to_glyph"
/// </summary>
public sealed class MoveToGlyphEffect : EffectBase
{
	private readonly string _label;
	public MoveToGlyphEffect(string label) { _label = label; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		int team = c?.TeamId ?? 0;
		if (targets?.Items == null)
			return;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u == null)
				continue;
			var glyph = InterfaceHelpers.NearestFriendlyGlyph(s, team, u.CurrentTile?.Axial ?? default);
			if (glyph != null && glyph.Occupant == null)
			{ u.PlaceOnTile(glyph); s.Log($"[{_label}] moved {u.Name} onto a glyph."); }
			else
				s.Log($"[{_label}] no reachable friendly glyph for {u.Name}.");
		}
	}
}

/// <summary>
/// Remove up to Count buff statuses from each target; if Steal, the caster gains them. 
/// JSON: { "type":"dispel","count":n,"steal":bool }
/// </summary>
public sealed class DispelEffect : EffectBase
{
	public int Count; public bool Steal;
	public DispelEffect(int count, bool steal) { Count = count; Steal = steal; }

	/// <summary>Strips up to <paramref name="count"/> buffs from <paramref name="victim"/>,
	/// handing them to <paramref name="thief"/> when stealing. Returns how many.</summary>
	public static int Strip(Unit victim, Unit thief, int count, bool steal)
	{
		if (victim?.Stats?.StatusEffects == null)
			return 0;
		var buffs = victim.Stats.StatusEffects.Where(kv => !InterfaceHelpers.Debuffs.Contains(kv.Key))
											 .Select(kv => (kv.Key, kv.Value)).Take(count).ToList();
		foreach (var (name, dur) in buffs)
		{
			victim.RemoveStatus(name);
			if (steal && thief != null)
				thief.ApplyStatus(name, dur);
		}
		return buffs.Count;
	}

	/// <summary>Dispel Walk step: every enemy beside <paramref name="walker"/> it has not
	/// touched this cast loses its buffs.</summary>
	public static void DispelAdjacent(GameState s, Unit walker)
	{
		if (walker?.CurrentTile == null || s?.Grid == null || walker.DispelWalkRound != Names.Round)
			return;
		foreach (var n in s.Grid.GetNeighbors(walker.CurrentTile.Axial))
		{
			var e = s.Grid.GetTile(n)?.Occupant;
			if (e == null || !GodotObject.IsInstanceValid(e) || !e.Stats.IsAlive || e.TeamId == walker.TeamId
				|| walker.DispelWalkTouched.Contains(e))
				continue;
			walker.DispelWalkTouched.Add(e);
			int k = Strip(e, walker, walker.DispelWalkCount, walker.DispelWalkSteal);
			if (k > 0)
				s.Log($"[DispelWalk] {walker.Name} {(walker.DispelWalkSteal ? "steals" : "strips")} {k} buff(s) from {e.Name}.");
		}
	}
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		if (targets?.Items == null)
			return;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u?.Stats?.StatusEffects == null)
				continue;
			var buffs = u.Stats.StatusEffects.Where(kv => !InterfaceHelpers.Debuffs.Contains(kv.Key))
											 .Select(kv => (kv.Key, kv.Value)).Take(Count).ToList();
			foreach (var (name, dur) in buffs)
			{
				u.RemoveStatus(name);
				if (Steal && c != null)
					c.ApplyStatus(name, dur);
			}
			s.Log($"[Dispel] removed {buffs.Count} buff(s) from {u.Name}" + (Steal ? " (stolen)." : "."));
		}
	}
}

/// <summary>
/// Swap the positions of two units. Two-step targeting gives [first unit, second unit's
/// tile]. Each lands on the other's tile as a teleport, so glyphs there go off. Phase
/// Shift upgrades hurt and/or Name every enemy it moved.
/// JSON: { "type":"swap_units", "with_caster": bool, "damage_enemies": n, "name_enemy": bool }
/// </summary>
public sealed class SwapUnitsEffect : EffectBase
{
	/// <summary>Bolt-Hole mode (2026-07-29): with a single targeted unit, swap it
	/// with the CASTER instead of failing.</summary>
	public bool WithCaster;
	public int DamageEnemies;
	public bool NameEnemies;

	public SwapUnitsEffect(bool withCaster = false, int damageEnemies = 0, bool nameEnemies = false)
	{ WithCaster = withCaster; DamageEnemies = damageEnemies; NameEnemies = nameEnemies; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var units = new List<Unit>();
		if (targets?.Items != null)
			foreach (var o in targets.Items)
			{ var u = ResolveTargetUnit(s, o); if (u != null && !units.Contains(u)) units.Add(u); }
		var me = s.ActiveCasterUnit;
		if (units.Count == 1 && WithCaster && me != null && me != units[0])
			units.Insert(0, me);
		if (units.Count < 2)
		{ s.Log("[SwapUnits] need two units."); return; }
		var a = units[0];
		var b = units[1];
		var ta = a.CurrentTile;
		var tb = b.CurrentTile;
		if (ta == null || tb == null)
			return;
		// Vacate both first: PlaceOnTile refuses an occupied tile, so without this
		// the first placement silently failed and nothing moved.
		ta.ClearOccupant(a);
		tb.ClearOccupant(b);
		a.PlaceOnTile(tb, MovementKind.Teleport);
		b.PlaceOnTile(ta, MovementKind.Teleport);
		s.Log($"[SwapUnits] swapped {a.Name} and {b.Name}.");

		int team = me?.TeamId ?? 0;
		foreach (var u in new[] { a, b })
		{
			if (u == null || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.TeamId == team)
				continue;
			if (DamageEnemies > 0)
				u.ApplyDamage(DamageEnemies);
			if (NameEnemies && u.Stats.IsAlive)
				u.ApplyStatus("named", 1);
		}
	}
}

/// <summary>
/// Apply a status to each target (used for geas / mana_tithe; the on-move and on-cast hooks live in the status system).
/// JSON: { "type":"geas",... } / "mana_tithe"
/// </summary>
public sealed class StatusApplyEffect : EffectBase
{
	private readonly string _status; private readonly int _duration; private readonly string _note;
	public StatusApplyEffect(string status, int duration, string note) { _status = status; _duration = duration; _note = note; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets?.Items == null)
			return;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u == null)
				continue;
			u.ApplyStatus(_status, _duration);
			s.Log($"[{_status}] applied to {u.Name} {_duration}t. {_note}");
		}
	}
}

/// <summary>
/// Prepares glyph(s). Backs three JSON types: "prepare_glyph" (one or `count` tiles),
/// "prepare_glyph_area" (every tile in `radius`), and "cascade_glyph" (an enter glyph with
/// `spread`). All glyph properties are read from JSON and written onto the GlyphData.
/// </summary>
public sealed class PrepareGlyphEffect : EffectBase
{
	public GlyphTrigger Trigger = GlyphTrigger.Enter;
	public int Damage, StatusDuration = 1, Duration = -1, Radius, Count = 1, CascadeSpread;
	public string Status;
	public bool Reusable, Invisible, EmptyOnly, AtOrigin, Area;
	public int AllyArmor, AllyShield, AllyDamage, AllyMana;
	public int OwnerDraw, OwnerMana, OwnerWeave, OwnerHeal;
	// Slice 12a
	public bool InstantCopies, HaltsMovement;
	/// <summary>Slice 13b: Seven-Layer Ward's layers; Tripwire Sentence's pairing.</summary>
	public int Layers;
	public bool Pair;
	private int _pairId;
	public int NameOnTrigger, CasterCostReduction, CasterDamage, CasterWeavePerTurn, AllyHealPerTurn, SpawnGlyphDamage;

	/// <summary>Identity of the half that owns this effect, stamped once at load time by
	/// JsonCardLoader.StampGlyphSource and copied onto every glyph this effect places.
	/// Load-time and not read from GameState during Resolve: casting pushes to the stack
	/// and the cast-context pins are cleared before the stack resolves, so a Resolve-time
	/// read always came back empty. Constant for the life of the object.</summary>
	public string SourceCardId = "", SourceHalf = "";

	/// <summary>Name and rules text of the owning half, stamped beside the id. Tooltip only.</summary>
	public string SourceName = "", SourceRulesText = "";

	private void Configure(GlyphData g)
	{
		g.SourceCardId = SourceCardId;
		g.SourceHalf = SourceHalf;
		g.SourceName = SourceName;
		g.SourceRulesText = SourceRulesText;
		g.Trigger = Trigger;
		g.Damage = Damage;
		g.Status = Status;
		g.StatusDuration = StatusDuration;
		g.DurationTurns = Duration;
		g.Reusable = Reusable;
		g.Invisible = Invisible;
		g.Radius = Radius;
		g.CascadeSpread = CascadeSpread;
		g.AllyArmor = AllyArmor;
		g.AllyShield = AllyShield;
		g.AllyDamage = AllyDamage;
		g.AllyMana = AllyMana;
		g.OwnerDraw = OwnerDraw;
		g.OwnerMana = OwnerMana;
		g.OwnerWeave = OwnerWeave;
		g.OwnerHeal = OwnerHeal;
		g.InstantCopies = InstantCopies;
		g.HaltsMovement = HaltsMovement;
		g.NameOnTrigger = NameOnTrigger;
		g.CasterCostReduction = CasterCostReduction;
		g.CasterDamage = CasterDamage;
		g.CasterWeavePerTurn = CasterWeavePerTurn;
		g.AllyHealPerTurn = AllyHealPerTurn;
		g.SpawnGlyphDamage = SpawnGlyphDamage;
		if (Layers > 0)
		{
			g.Layers = Layers;
			g.Reusable = true;   // it stays between triggers; OnGlyphFired removes it at 0
		}
		g.PairId = _pairId;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null)
		{ s?.Log("[PrepareGlyph] no GlyphManager on GameState."); return; }

		_pairId = Pair ? s.Glyphs.NewPairId() : 0;
		int placed = 0;
		if (Area)
		{
			TileData center = (targets?.Items?.Count > 0 ? InterfaceHelpers.ResolveTile(s, targets.Items[0]) : null) ?? owner?.CurrentTile;
			if (center == null || s.Grid?.Tiles == null)
				return;
			foreach (var t in s.Grid.Tiles.Values)
			{
				if (s.Grid.Distance(center.Axial, t.Axial) > Radius)
					continue;
				if (EmptyOnly && t.Occupant != null)
					continue;
				if (s.Glyphs.Prepare(t, owner, Configure) != null)
					placed++;
			}
		}
		else
		{
			if (AtOrigin && owner?.CurrentTile != null)
			{
				if (s.Glyphs.Prepare(owner.CurrentTile, owner, Configure) != null)
					placed++;
			}
			else if (targets?.Items != null)
			{
				foreach (var o in targets.Items)
				{
					if (placed >= Count)
						break;
					var tile = InterfaceHelpers.ResolveTile(s, o);
					if (tile != null && s.Glyphs.Prepare(tile, owner, Configure) != null)
						placed++;
				}
			}
		}
		s.Log($"[PrepareGlyph] placed {placed} glyph(s) [{Trigger}].");
	}
}

/// <summary>Link up to N friendly glyphs so triggering one triggers the group.
/// "persist" keeps the network open for the rest of the fight (later glyphs join it);
/// "on_trigger_weave" pays that much Weave per glyph linked.
/// { "type":"link_glyphs","count":n,"cumulative_bonus":n,"persist":bool,"on_trigger_weave":n }</summary>
public sealed class LinkGlyphsEffect : EffectBase
{
	public int Count, CumulativeBonus, WeavePerGlyph;
	public bool Persist;
	public LinkGlyphsEffect(int count, int bonus, int weavePerGlyph = 0, bool persist = false)
	{ Count = count; CumulativeBonus = bonus; WeavePerGlyph = weavePerGlyph; Persist = persist; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null)
			return;
		int linked;
		if (Persist)
		{
			linked = s.Glyphs.LinkPersistent(owner.TeamId, CumulativeBonus);
			s.Log($"[LinkGlyphs] {linked} glyph(s) linked; the network stays open for the rest of the fight.");
		}
		else
		{
			linked = Math.Min(Count, s.Glyphs.CountFriendly(owner.TeamId));
			int id = s.Glyphs.Link(owner.TeamId, Count, CumulativeBonus);
			s.Log($"[LinkGlyphs] linked {linked} glyph(s) (id {id}).");
		}
		if (WeavePerGlyph > 0 && linked > 0 && owner.Attunement is WeaveAttunement w)
			w.Add(WeavePerGlyph * linked);
	}
}

/// <summary>Re-arm consumed friendly glyphs; optional empower. { "type":"rearm_glyphs","empower":n }</summary>
public sealed class RearmGlyphsEffect : EffectBase
{
	public int Empower;
	public RearmGlyphsEffect(int empower) { Empower = empower; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null)
			return;
		int n = s.Glyphs.Rearm(owner.TeamId, Empower);
		s.Log($"[RearmGlyphs] re-armed {n} glyph(s)" + (Empower > 0 ? $" (+{Empower} dmg)." : "."));
	}
}

/// <summary>Fire all friendly glyphs at once. { "type":"trigger_all_glyphs","bonus_per_other":n,"consume":bool }</summary>
public sealed class TriggerAllGlyphsEffect : EffectBase
{
	public int BonusPerOther, Repeat = 1, WeavePerGlyph; public bool Consume;
	public TriggerAllGlyphsEffect(int bonus, bool consume, int repeat = 1, int weavePerGlyph = 0)
	{ BonusPerOther = bonus; Consume = consume; Repeat = Math.Max(1, repeat); WeavePerGlyph = weavePerGlyph; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null)
			return;
		int fired = 0;
		for (int i = 0; i < Repeat; i++)
			fired += s.Glyphs.TriggerAll(s, owner.TeamId, BonusPerOther, Consume);
		if (WeavePerGlyph > 0 && fired > 0 && owner.Attunement is WeaveAttunement w)
			w.Add(WeavePerGlyph * fired);
	}
}

/// <summary>Swap two of your glyphs (tile then tile), optionally triggering both after,
/// each with bonus damage, or every glyph you control. Weave per glyph triggered.
/// { "type":"swap_glyphs", "trigger_after": bool, "bonus": n, "trigger_all_after": bool, "on_trigger_weave": n }</summary>
public sealed class SwapGlyphsEffect : EffectBase
{
	public bool TriggerAfter, TriggerAllAfter;
	public int Bonus, WeavePerGlyph;

	public SwapGlyphsEffect(bool triggerAfter = false, int bonus = 0, bool triggerAll = false, int weavePerGlyph = 0)
	{ TriggerAfter = triggerAfter; Bonus = bonus; TriggerAllAfter = triggerAll; WeavePerGlyph = weavePerGlyph; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Glyphs == null || targets?.Items == null)
			return;
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		var tiles = targets.Items.Select(o => InterfaceHelpers.ResolveTile(s, o)).Where(t => t != null).Distinct().ToList();
		if (tiles.Count < 2)
		{ s.Log("[SwapGlyphs] need two tiles."); return; }
		s.Glyphs.Swap(tiles[0], tiles[1]);
		s.Log("[SwapGlyphs] two glyphs trade places.");

		int fired = 0;
		if (TriggerAllAfter && owner != null)
			fired = s.Glyphs.TriggerAll(s, owner.TeamId, Bonus, consume: true);
		else if (TriggerAfter)
		{
			fired += s.Glyphs.TriggerTile(s, tiles[0], Bonus) ? 1 : 0;
			fired += s.Glyphs.TriggerTile(s, tiles[1], Bonus) ? 1 : 0;
		}
		if (WeavePerGlyph > 0 && fired > 0 && owner?.Attunement is WeaveAttunement w)
			w.Add(WeavePerGlyph * fired);
	}
}

/// <summary>Teleport the caster onto one of their glyphs: the targeted tile when it holds
/// one, else the nearest. Optionally set it off on arrival; "refresh" keeps it (re-armed)
/// even if it is single use.
/// { "type":"teleport_to_glyph", "trigger_on_arrive": bool, "refresh": bool }</summary>
public sealed class TeleportToGlyphEffect : EffectBase
{
	public bool TriggerOnArrive, Refresh;
	public TeleportToGlyphEffect(bool trigger, bool refresh = false) { TriggerOnArrive = trigger; Refresh = refresh; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		if (s?.Glyphs == null || c == null)
			return;
		TileData tile = null;
		if (targets?.Items != null && targets.Items.Count > 0)
		{
			var t = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
			if (t?.Glyph != null && t.Glyph.OwnerTeam == c.TeamId)
				tile = t;
		}
		tile ??= s.Glyphs.NearestFriendly(c.TeamId, c.CurrentTile?.Axial ?? default);
		if (tile == null)
		{ s.Log("[TeleportToGlyph] no friendly glyph."); return; }
		if (tile.Occupant != null && tile.Occupant != c)
		{ s.Log("[TeleportToGlyph] that glyph is occupied."); return; }
		var glyph = tile.Glyph;
		c.PlaceOnTile(tile, MovementKind.Teleport);
		s.Log($"[TeleportToGlyph] {c.Name} steps onto a glyph.");
		if (TriggerOnArrive && glyph != null && tile.Glyph == glyph)
		{
			glyph.Fire(c, s);                 // caster is friendly: ally payload and payoffs
			bool keep = s.Glyphs.OnGlyphFired(s, tile, c);
			if (!keep && !Refresh)
				s.Glyphs.Remove(tile);
			else if (Refresh)
				glyph.Consumed = false;
		}
	}
}

/// <summary>Permanent reusable ally-buff tiles (Sovereign Pillars). Enemy-adjacent aura is logged as pending. { "type":"enchant_pillar","count":n,"ally_all_stats":n,... }</summary>
public sealed class EnchantPillarEffect : EffectBase
{
	public int Count, AllyAll, EnemyDamageReduction, AuraDamage, WeavePerTurn; public string AuraStatus;
	public EnchantPillarEffect(int count, int allyAll, int enemyDr, string aura, int auraDamage = 0, int weavePerTurn = 0)
	{ Count = count; AllyAll = allyAll; EnemyDamageReduction = enemyDr; AuraStatus = aura; AuraDamage = auraDamage; WeavePerTurn = weavePerTurn; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null || targets?.Items == null)
			return;
		int placed = 0;
		foreach (var o in targets.Items)
		{
			if (placed >= Count)
				break;
			var tile = InterfaceHelpers.ResolveTile(s, o);
			if (tile == null)
				continue;
			bool first = placed == 0;
			if (s.Glyphs.Prepare(tile, owner, g =>
			{
				g.Trigger = GlyphTrigger.AllyEnter;
				g.Reusable = true;
				g.DurationTurns = -1;
				g.AllyArmor = AllyAll;
				g.AllyDamage = AllyAll;
				g.AllyShield = AllyAll;
				g.Pillar = true;
				g.AuraDamageReduction = EnemyDamageReduction;
				g.AuraStatus = AuraStatus;
				g.AuraDamage = AuraDamage;
				g.OwnerWeavePerTurn = first ? WeavePerTurn : 0;   // once per casting, not per pillar
			}) != null)
				placed++;
		}
		s.Log($"[EnchantPillar] raised {placed} Sovereign Pillar(s).");
	}
}

/// <summary>A glyph that reflects the next spell on a unit standing on it. Placement works; reflection resolution needs a hook in the cast/targeting pipeline. { "type":"reflect_ward","triggers":n }</summary>
public sealed class ReflectWardEffect : EffectBase
{
	public int Triggers, Radius, Duration;
	public float Bonus;
	public ReflectWardEffect(int triggers, int radius, int duration = 3, float bonus = 0f)
	{ Triggers = Math.Max(1, triggers); Radius = Math.Max(0, radius); Duration = duration; Bonus = bonus; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || targets?.Items == null || targets.Items.Count == 0)
			return;
		var center = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
		if (center == null)
			return;
		int placed = 0;
		foreach (var tile in s.Grid.Tiles.Values)
		{
			if (s.Grid.Distance(center.Axial, tile.Axial) > Radius)
				continue;
			if (s.Glyphs.Prepare(tile, owner, g =>
			{
				g.Trigger = GlyphTrigger.Manual;
				g.Reusable = true;
				g.ReflectCharges = Triggers;
				g.ReflectBonus = Bonus;
				g.DurationTurns = Duration;
			}) != null)
				placed++;
		}
		s.Log($"[ReflectWard] {placed} mirror glyph(s) prepared ({(Triggers >= 99 ? "every" : Triggers.ToString())} hit(s) each).");
	}
}

/// <summary>A glyph that doubles the next spell cast while standing on it. Placement works; the cast-twice resolution needs the cast pipeline. { "type":"spell_anchor","casts":n }</summary>
public sealed class SpellAnchorEffect : EffectBase
{
	public int Casts, CostReduction, Weave, Duration = 3;
	public bool Reusable;
	public SpellAnchorEffect(int casts, int costReduction = 0, int weave = 0, int duration = 3, bool reusable = false)
	{ Casts = Math.Max(2, casts); CostReduction = costReduction; Weave = weave; Duration = duration; Reusable = reusable; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || targets?.Items == null)
			return;
		var tile = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
		if (tile == null)
			return;
		s.Glyphs.Prepare(tile, owner, g =>
		{
			g.Trigger = GlyphTrigger.SelfStand;
			g.AnchorCasts = Casts;
			g.AnchorCostReduction = CostReduction;
			g.AnchorWeave = Weave;
			g.Reusable = Reusable;
			g.DurationTurns = Duration;
		});
		s.Log($"[SpellAnchor] placed: the next spell cast standing on it resolves {Casts} times.");
	}
}

/// <summary>
/// Applies "dominated" status to each target enemy and spawns a DominateAura
/// to enforce the forced-attack each turn.
/// JSON: { "type": "dominate", "turns": n }
/// </summary>
public sealed class DominateEffect : EffectBase
{
	public int Turns;
	public DominateEffect(int turns) { Turns = Math.Max(1, turns); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (targets?.Items == null)
			return;

		bool dominated = false;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u == null || u.TeamId == casterUnit?.TeamId)
				continue;
			u.ApplyStatus("dominated", Turns);
			s.Log($"[Dominate] {u.Name} is dominated for {Turns} turn(s).");
			dominated = true;
		}

		if (dominated && !s.HasActiveEffect<DominateAura>(caster))
		{
			s.ActiveEffects ??= new List<PersistentEffect>();
			s.ActiveEffects.Add(new DominateAura(Turns, caster, casterUnit));
		}
	}
}

/// <summary>
/// Maze of Mirrors: Count mirror copies of the caster on empty tiles nearby, each with Hp
/// health and Attack damage, lasting Duration turns (99 = the rest of the fight). While
/// copies stand, an enemy that picks the caster picks the caster or a copy at random
/// (CombatManager.FindTargetOverride). A copy that is hit can turn the blow back
/// (reflect_on_hit); a destroyed copy sets off the glyph under it.
/// JSON: { "type":"summon_illusion", "count":n, "hp":n, "duration":n, "copies_attack":n,
///         "reflect_on_hit":bool, "trigger_glyph_on_death":bool }
/// </summary>
public sealed class SummonIllusionEffect : EffectBase
{
	public int Count, Hp, Duration, Attack;
	public bool ReflectOnHit, TriggerGlyphOnDeath;

	public SummonIllusionEffect(int count, int hp, int duration, int attack, bool reflect, bool triggerGlyph)
	{
		Count = Math.Max(1, count); Hp = Math.Max(1, hp); Duration = Math.Max(1, duration);
		Attack = Math.Max(0, attack); ReflectOnHit = reflect; TriggerGlyphOnDeath = triggerGlyph;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{ s.Log("[SummonIllusion] No summon handler."); return; }
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (owner?.CurrentTile == null || s.Grid == null)
			return;

		var spots = s.Grid.Tiles.Values
			.Where(t => t != null && !t.IsBlocked && !t.IsOccupied && t.IsWalkable
						&& s.Grid.Distance(owner.CurrentTile.Axial, t.Axial) <= 2)
			.OrderBy(t => s.Grid.Distance(owner.CurrentTile.Axial, t.Axial))
			.ThenBy(t => t.Axial.X).ThenBy(t => t.Axial.Y)
			.Take(Count)
			.ToList();
		int made = 0;
		foreach (var spot in spots)
		{
			var copy = s.OnSummonRequested("Illusion", spot, owner.TeamId);
			if (copy == null)
				continue;
			copy.Stats.MaxHealth = Hp;
			copy.Stats.Health = Hp;
			copy.AttackDamage = Attack;
			copy.IllusionOwner = owner;
			copy.IllusionTurnsLeft = Duration >= 99 ? -1 : Duration;
			copy.ApplyStatus("illusion", Duration >= 99 ? 999 : Duration);
			copy.RefreshHealthBar();
			if (!s.UnitsInPlay.Contains(copy))
				s.UnitsInPlay.Add(copy);

			var me = copy;
			if (ReflectOnHit)
				me.OnAttacked += (target, source, amount) =>
				{
					if (source == null || !GodotObject.IsInstanceValid(source) || source == target
						|| !source.Stats.IsAlive || source.TeamId == target.TeamId || amount <= 0)
						return;
					source.ApplyDamage(amount);
					GD.Print($"[Mirror] {target.Name} turns {amount} back on {source.Name}.");
				};
			if (TriggerGlyphOnDeath)
				me.OnDied += dead =>
				{
					var tile = dead?.CurrentTile;
					if (tile?.Glyph == null || s?.Glyphs == null)
						return;
					if (s.Glyphs.TriggerTile(s, tile, 0))
						s.Log($"[Mirror] {dead.Name} shatters and sets off the glyph beneath it.");
				};
			made++;
		}
		s.Log($"[SummonIllusion] {made} mirror cop{(made == 1 ? "y" : "ies")} of {owner.Name} ({Hp} HP).");
		// Intents are locked at the start of the turn; let enemies that chose the
		// caster pick again now that the copies stand.
		if (made > 0)
			s.OnIllusionsSummoned?.Invoke(owner);
	}
}

/// <summary>
/// The Architecture: for the rest of the fight, each glyph the caster prepares is also
/// prepared on every tile within SpreadRadius, glyph spells cost GlyphCostReduction less,
/// preparing banks WeavePerPrepare, and glyphs hit TriggerCount times as hard. Casting it
/// again keeps the strongest of each value.
/// JSON: { "type":"grand_design_passive", "spread_radius":n, "trigger_count":n,
///         "glyph_cost_reduction":n, "weave_per_prepare":n }
/// </summary>
public sealed class GrandDesignPassiveLeafEffect : EffectBase
{
	public int SpreadRadius, TriggerCount, GlyphCostReduction, WeavePerPrepare;
	public GrandDesignPassiveLeafEffect(int spread, int triggerCount, int costReduction, int weavePerPrepare)
	{ SpreadRadius = spread; TriggerCount = Math.Max(1, triggerCount); GlyphCostReduction = costReduction; WeavePerPrepare = weavePerPrepare; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		s.ActiveEffects ??= new List<PersistentEffect>();
		var gd = s.GetActiveEffect<GrandDesignPersistentEffect>(caster);
		if (gd == null)
		{
			gd = new GrandDesignPersistentEffect(GrandDesignPersistentEffect.RestOfFight, caster, unit);
			s.ActiveEffects.Add(gd);
		}
		gd.SpreadRadius = Math.Max(gd.SpreadRadius, SpreadRadius);
		gd.TriggerCount = Math.Max(gd.TriggerCount, TriggerCount);
		gd.GlyphCostReduction = Math.Max(gd.GlyphCostReduction, GlyphCostReduction);
		gd.WeavePerPrepare = Math.Max(gd.WeavePerPrepare, WeavePerPrepare);
		s.Log($"[Architecture] For the rest of the fight: glyphs spread {gd.SpreadRadius}, hit x{gd.TriggerCount}"
			  + (gd.GlyphCostReduction > 0 ? $", glyph spells cost {gd.GlyphCostReduction} less" : "")
			  + (gd.WeavePerPrepare > 0 ? $", +{gd.WeavePerPrepare} Weave per glyph prepared." : "."));
	}
}

/// <summary>
/// Absolute Territory: a zone of Radius around the caster for Turns (99 = rest of fight).
/// Enemies inside deal half damage, take DamagePerTile for every tile they walk in it, and
/// cannot teleport into or out of it; with NameEnemies they are also Named each round.
/// JSON: { "type":"absolute_territory", "radius":n, "damage_per_tile":n, "duration":n, "name_enemies":bool }
/// </summary>
public sealed class AbsoluteTerritoryLeafEffect : EffectBase
{
	public int Radius, DamagePerTile, Turns;
	public bool NameEnemies;

	public AbsoluteTerritoryLeafEffect(int r, int perTile, int turns, bool nameEnemies)
	{ Radius = r; DamagePerTile = perTile; Turns = turns; NameEnemies = nameEnemies; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		var center = unit?.CurrentTile?.Axial ?? default;
		s.ActiveEffects ??= new List<PersistentEffect>();
		var zone = new AbsoluteTerritoryZone(center, Radius, DamagePerTile, Turns >= 99 ? AbsoluteTerritoryZone.RestOfFight : Turns,
											 caster, unit) { NameEnemies = NameEnemies };
		s.ActiveEffects.Add(zone);
		if (NameEnemies)
			zone.NameInside(s);
		s.Log($"[AbsoluteTerritory] r={Radius} around {center}: enemies deal half, take {DamagePerTile} per tile walked"
			  + (NameEnemies ? ", and are Named." : "."));
	}
}

/// <summary>
/// Puppeteer: for Turns, at each round's start every enemy walks (walked rules, so glyphs
/// fire) up to Tiles toward its nearest glyph of the caster's, or toward the caster when
/// there are none; with NameControlled it is Named until the next round.
/// JSON: { "type":"puppeteer", "turns":n, "move_tiles":n, "name_controlled":bool }
/// </summary>
public sealed class PuppeteerEffect : EffectBase
{
	public int Turns, Tiles;
	public bool NameControlled;
	public PuppeteerEffect(int turns, int tiles, bool nameControlled)
	{ Turns = Math.Max(1, turns); Tiles = Math.Max(1, tiles); NameControlled = nameControlled; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new PuppeteerAura(Turns, Tiles, NameControlled, caster, unit));
		s.Log($"[Puppeteer] For {Turns} turn(s), enemies walk up to {Tiles} toward your glyphs each round.");
	}
}

/// <summary>Dispel Walk: strips buffs from every enemy beside the caster now, and from each
/// enemy the caster walks past for the rest of this round (CombatManager.Enchanter watches
/// the caster's steps). Each enemy is touched once per cast.
/// JSON: { "type":"dispel_walk", "count":n, "steal":bool }</summary>
public sealed class DispelWalkEffect : EffectBase
{
	public int Count; public bool Steal;
	public DispelWalkEffect(int count, bool steal) { Count = Math.Max(1, count); Steal = steal; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (c == null)
			return;
		c.DispelWalkRound = Names.Round;
		c.DispelWalkCount = Count;
		c.DispelWalkSteal = Steal;
		c.DispelWalkTouched.Clear();
		DispelEffect.DispelAdjacent(s, c);
	}
}

/// <summary>First Layer (§4): two enemies' locked attack tiles trade places.
/// JSON: { "type":"swap_locks" }, targeting unit_then_unit (enemies).</summary>
public sealed class SwapLocksEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var units = new List<Unit>();
		if (targets?.Items != null)
			foreach (var o in targets.Items)
			{ var u = ResolveTargetUnit(s, o); if (u != null && !units.Contains(u)) units.Add(u); }
		if (units.Count < 2)
		{ s.Log("[SwapLocks] need two enemies."); return; }
		string err = IntentTime.SwapLocks(units[0], units[1]);
		if (err != null)
			s.Log($"[SwapLocks] {err}.");
	}
}

/// <summary>
/// While active, finds all enemies with "dominated" status and forces each to
/// deal its AttackDamage to its nearest ally at start of turn.
/// Full AI control (commanding the dominated unit's actions from the player UI)
/// is a deeper engine feature; this implements the "hurts own team" half.
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
                continue; // already on our side, so skip

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
    /// <summary>Turns value used for "the rest of the fight": never ticks down.</summary>
    public const int RestOfFight = 9999;

    public Unit OwnerUnit;
    public int SpreadRadius;
    public int TriggerCount = 2;
    public int GlyphCostReduction;
    public int WeavePerPrepare;

    public GrandDesignPersistentEffect(int turns, Entity owner, Unit ownerUnit)
    {
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        if (TurnsRemaining < RestOfFight)
            TurnsRemaining--;
    }

    /// <summary>The Architecture active for <paramref name="team"/>, or null.</summary>
    public static GrandDesignPersistentEffect For(GameState s, int team) =>
        s?.ActiveEffects?.OfType<GrandDesignPersistentEffect>()
            .Where(e => !e.IsExpired && e.OwnerUnit != null && e.OwnerUnit.TeamId == team)
            .OrderByDescending(e => e.TriggerCount)
            .FirstOrDefault();
}

public sealed class AbsoluteTerritoryZone : PersistentEffect
{
    public const int RestOfFight = 9999;

    public Vector2I Center;
    public int Radius, DamagePerTile;
    public bool NameEnemies;
    public Unit OwnerUnit;

    public AbsoluteTerritoryZone(Vector2I center, int radius, int perTile, int turns,
        Entity owner, Unit ownerUnit)
    {
        Center = center;
        Radius = radius;
        DamagePerTile = perTile;
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        if (TurnsRemaining < RestOfFight)
            TurnsRemaining--;
        if (NameEnemies && !IsExpired)
            NameInside(s);
    }

    public bool Contains(GameState s, Vector2I at) => s?.Grid != null && s.Grid.Distance(Center, at) <= Radius;

    /// <summary>Is <paramref name="u"/> an enemy of this zone's owner?</summary>
    public bool IsEnemy(Unit u) => u != null && OwnerUnit != null && u.TeamId != OwnerUnit.TeamId;

    public void NameInside(GameState s)
    {
        if (s?.UnitsInPlay == null)
            return;
        foreach (var u in s.UnitsInPlay)
            if (u != null && GodotObject.IsInstanceValid(u) && u.Stats.IsAlive && u.CurrentTile != null
                && IsEnemy(u) && Contains(s, u.CurrentTile.Axial))
                u.ApplyStatus("named", 1);
    }

    /// <summary>An active zone that holds <paramref name="at"/> and opposes <paramref name="u"/>.</summary>
    public static AbsoluteTerritoryZone Holding(GameState s, Unit u, Vector2I at) =>
        s?.ActiveEffects?.OfType<AbsoluteTerritoryZone>()
            .FirstOrDefault(z => !z.IsExpired && z.IsEnemy(u) && z.Contains(s, at));

    /// <summary>True when a teleport by <paramref name="u"/> to <paramref name="dest"/> starts
    /// or ends inside an opposing zone.</summary>
    public static bool BlocksTeleport(GameState s, Unit u, Vector2I dest) =>
        u?.CurrentTile != null && (Holding(s, u, u.CurrentTile.Axial) != null || Holding(s, u, dest) != null);
}

/// <summary>Puppeteer's round-start walk. Ticks once per round at the player's turn start,
/// before the enemies act.</summary>
public sealed class PuppeteerAura : PersistentEffect
{
    public Unit OwnerUnit;
    public int Tiles;
    public bool NameControlled;

    public PuppeteerAura(int turns, int tiles, bool nameControlled, Entity owner, Unit ownerUnit)
    {
        TurnsRemaining = turns;
        Tiles = tiles;
        NameControlled = nameControlled;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (s?.Grid == null || OwnerUnit == null || !GodotObject.IsInstanceValid(OwnerUnit))
            return;
        int team = OwnerUnit.TeamId;
        foreach (var e in s.UnitsInPlay.ToList())
        {
            if (e == null || !GodotObject.IsInstanceValid(e) || !e.Stats.IsAlive || e.CurrentTile == null || e.TeamId == team)
                continue;
            var goal = s.Glyphs?.NearestFriendly(team, e.CurrentTile.Axial)?.Axial
                       ?? OwnerUnit.CurrentTile?.Axial;
            if (goal == null)
                continue;
            bool onto = s.Glyphs?.NearestFriendly(team, e.CurrentTile.Axial) != null;
            var path = CompelWalk.Plan(s.Grid, e, Tiles, goal, null, onto);
            int walked = CompelWalk.Walk(s.Grid, e, path, s.Log);
            if (walked > 0)
                s.Log($"[Puppeteer] {e.Name} is walked {walked} tile(s).");
            if (NameControlled && e.Stats.IsAlive)
                e.ApplyStatus("named", 1);
        }
    }
}
