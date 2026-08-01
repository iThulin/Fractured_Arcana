using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// EnchanterEffects.cs
//
// Purpose:        Enchanter school effects — glyphs, weave, control magic, and the
//                 Dominion / Grand Design persistent zones.
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase, core leaves),
//                 PersistentEffect.cs (PersistentEffect base),
//                 CardScriptRegistry.Enchanter.cs (registration)
// Notes:          Extracted from Effect.cs / CompositeEffects.cs /
//                 PersistentEffect.cs — pure move, no behavior change.
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
			s.Log("[GainWeave] Caster has no Weave attunement — ignored.");
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
/// Swap the positions of two targeted units. 
/// JSON: { "type":"swap_units" }
/// </summary>
public sealed class SwapUnitsEffect : EffectBase
{
	/// <summary>Bolt-Hole mode (2026-07-29): with a single targeted unit, swap it
	/// with the CASTER instead of failing. Lets "swap positions with a construct you
	/// control" be a one-click unit target rather than a two-unit selection.</summary>
	public bool WithCaster;

	public SwapUnitsEffect(bool withCaster = false) { WithCaster = withCaster; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var units = new List<Unit>();
		if (targets?.Items != null)
			foreach (var o in targets.Items)
			{ var u = ResolveTargetUnit(s, o); if (u != null) units.Add(u); }
		if (units.Count == 1 && WithCaster)
		{
			var me = s.ActiveCasterUnit;
			if (me != null && me != units[0])
				units.Insert(0, me);
		}
		if (units.Count < 2)
		{ s.Log("[SwapUnits] need two units."); return; }
		var a = units[0];
		var b = units[1];
		var ta = a.CurrentTile;
		var tb = b.CurrentTile;
		if (ta == null || tb == null)
			return;
		a.PlaceOnTile(tb);
		b.PlaceOnTile(ta);
		s.Log($"[SwapUnits] swapped {a.Name} and {b.Name}.");
	}
}

/// <summary>
/// Apply a status to each target (used for geas / mana_tithe — the on-move and on-cast hooks live in the status system). 
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

	/// <summary>Identity of the half that owns this effect, stamped once at load time by
	/// JsonCardLoader.StampGlyphSource and copied onto every glyph this effect places.
	/// Load-time and not read from GameState during Resolve: casting pushes to the stack
	/// and the cast-context pins are cleared before the stack resolves, so a Resolve-time
	/// read always came back empty. Constant for the life of the object.</summary>
	public string SourceCardId = "", SourceHalf = "";

	private void Configure(GlyphData g)
	{
		g.SourceCardId = SourceCardId;
		g.SourceHalf = SourceHalf;
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
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null)
		{ s?.Log("[PrepareGlyph] no GlyphManager on GameState."); return; }

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

/// <summary>Link up to N friendly glyphs so triggering one triggers the group. { "type":"link_glyphs","count":n,"cumulative_bonus":n }</summary>
public sealed class LinkGlyphsEffect : EffectBase
{
	public int Count, CumulativeBonus;
	public LinkGlyphsEffect(int count, int bonus) { Count = count; CumulativeBonus = bonus; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null)
			return;
		int id = s.Glyphs.Link(owner.TeamId, Count, CumulativeBonus);
		s.Log($"[LinkGlyphs] linked up to {Count} glyph(s) (id {id}).");
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
	public int BonusPerOther; public bool Consume;
	public TriggerAllGlyphsEffect(int bonus, bool consume) { BonusPerOther = bonus; Consume = consume; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null)
			return;
		s.Glyphs.TriggerAll(s, owner.TeamId, BonusPerOther, Consume);
	}
}

/// <summary>Swap two glyph tiles. { "type":"swap_glyphs" }</summary>
public sealed class SwapGlyphsEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Glyphs == null || targets?.Items == null)
			return;
		var tiles = targets.Items.Select(o => InterfaceHelpers.ResolveTile(s, o)).Where(t => t != null).ToList();
		if (tiles.Count < 2)
		{ s.Log("[SwapGlyphs] need two tiles."); return; }
		s.Glyphs.Swap(tiles[0], tiles[1]);
		s.Log("[SwapGlyphs] swapped two glyph tiles.");
	}
}

/// <summary>Teleport caster onto the nearest friendly glyph. { "type":"teleport_to_glyph","trigger_on_arrive":bool }</summary>
public sealed class TeleportToGlyphEffect : EffectBase
{
	public bool TriggerOnArrive;
	public TeleportToGlyphEffect(bool trigger) { TriggerOnArrive = trigger; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		if (s?.Glyphs == null || c == null)
			return;
		var tile = s.Glyphs.NearestFriendly(c.TeamId, c.CurrentTile?.Axial ?? default);
		if (tile == null)
		{ s.Log("[TeleportToGlyph] no friendly glyph."); return; }
		c.PlaceOnTile(tile);
		s.Log($"[TeleportToGlyph] {c.Name} teleported to a glyph.");
		if (TriggerOnArrive && tile.Glyph != null)
		{
			tile.Glyph.Fire(c, s);                 // caster is friendly → ally payload / payoffs
			s.Glyphs.OnGlyphFired(s, tile, c);
			if (!tile.Glyph.Reusable)
				s.Glyphs.Remove(tile);
		}
	}
}

/// <summary>Permanent reusable ally-buff tiles (Sovereign Pillars). Enemy-adjacent aura is logged as pending. { "type":"enchant_pillar","count":n,"ally_all_stats":n,... }</summary>
public sealed class EnchantPillarEffect : EffectBase
{
	public int Count, AllyAll, EnemyDamageReduction; public string AuraStatus;
	public EnchantPillarEffect(int count, int allyAll, int enemyDr, string aura)
	{ Count = count; AllyAll = allyAll; EnemyDamageReduction = enemyDr; AuraStatus = aura; }
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
			if (s.Glyphs.Prepare(tile, owner, g =>
			{
				g.Trigger = GlyphTrigger.AllyEnter;
				g.Reusable = true;
				g.DurationTurns = -1;
				g.AllyArmor = AllyAll;
				g.AllyDamage = AllyAll;
				g.AllyShield = AllyAll;
			}) != null)
				placed++;
		}
		s.Log($"[EnchantPillar] raised {placed} permanent pillar(s). (enemy-adjacent aura pending per-turn aura hook)");
	}
}

/// <summary>A glyph that reflects the next spell on a unit standing on it. Placement works; reflection resolution needs a hook in the cast/targeting pipeline. { "type":"reflect_ward","triggers":n }</summary>
public sealed class ReflectWardEffect : EffectBase
{
	public int Triggers, Radius;
	public ReflectWardEffect(int triggers, int radius) { Triggers = triggers; Radius = radius; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || targets?.Items == null)
			return;
		var tile = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
		if (tile == null)
			return;
		s.Glyphs.Prepare(tile, owner, g => { g.Trigger = GlyphTrigger.Manual; g.Status = "reflect"; g.StatusDuration = Triggers; g.DurationTurns = 3; });
		s.Log("[ReflectWard] placed. (spell-reflection resolution needs a cast-pipeline hook — see writeup)");
	}
}

/// <summary>A glyph that doubles the next spell cast while standing on it. Placement works; the cast-twice resolution needs the cast pipeline. { "type":"spell_anchor","casts":n }</summary>
public sealed class SpellAnchorEffect : EffectBase
{
	public int Casts;
	public SpellAnchorEffect(int casts) { Casts = casts; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || targets?.Items == null)
			return;
		var tile = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
		if (tile == null)
			return;
		s.Glyphs.Prepare(tile, owner, g => { g.Trigger = GlyphTrigger.SelfStand; g.Status = "anchor"; g.StatusDuration = Casts; g.DurationTurns = 3; });
		s.Log("[SpellAnchor] placed. (cast-twice resolution needs the cast pipeline — see writeup)");
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
/// Summons a phantom duplicate of the caster with HpFraction of the caster's
/// max HP. The illusion unit carries an "illusion" status; apply one-hit-break
/// behaviour in Unit.ApplyDamage by checking HasStatus("illusion") and calling
/// Die() if any damage lands — that is a unit-side hook this effect cannot set.
/// JSON: { "type": "summon_illusion", "hp_fraction": 0.5, "duration": n }
/// </summary>
public sealed class SummonIllusionEffect : EffectBase
{
	public float HpFraction;
	public int Duration;

	public SummonIllusionEffect(float hpFrac, int dur)
	{
		HpFraction = Math.Clamp(hpFrac, 0.1f, 1f);
		Duration = Math.Max(1, dur);
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{ s.Log("[SummonIllusion] No summon handler."); return; }

		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null)
			return;

		TileData spawnTile = null;
		if (casterUnit.CurrentTile != null && s.Grid != null)
			foreach (var coord in s.Grid.GetNeighbors(casterUnit.CurrentTile.Axial))
			{
				var t = s.Grid.GetTile(coord);
				if (t != null && !t.IsBlocked && !t.IsOccupied)
				{ spawnTile = t; break; }
			}

		if (spawnTile == null)
		{ s.Log("[SummonIllusion] No spawn tile."); return; }

		var illusion = s.OnSummonRequested("Illusion", spawnTile, casterUnit.TeamId);
		if (illusion == null)
			return;

		illusion.Stats.MaxHealth = Math.Max(1, (int)(casterUnit.Stats.MaxHealth * HpFraction));
		illusion.Stats.Health = illusion.Stats.MaxHealth;
		illusion.AttackDamage = casterUnit.AttackDamage;
		illusion.ApplyStatus("illusion", Duration);

		s.UnitsInPlay?.Add(illusion);
		s.Log($"[SummonIllusion] Phantom at {spawnTile.Axial} ({illusion.Stats.MaxHealth}HP). One-hit-break: add to Unit.ApplyDamage.");
	}
}

/// <summary>
/// Spawns a GrandDesignPersistentEffect. Glyph doubling is enforced in GlyphData.Fire
/// — add the 7-line check shown in the integration note above.
/// JSON: { "type": "grand_design_passive", "turns": n }
/// </summary>
public sealed class GrandDesignPassiveLeafEffect : EffectBase
{
	public int Turns;
	public GrandDesignPassiveLeafEffect(int turns) { Turns = Math.Max(1, turns); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.HasActiveEffect<GrandDesignPersistentEffect>(caster))
			return;
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new GrandDesignPersistentEffect(Turns, caster, unit));
		s.Log($"[GrandDesign] Glyphs doubled for {Turns} turn(s). (add check to GlyphData.Fire — see note)");
	}
}

/// <summary>
/// Creates a persistent damage zone centred on the caster's current position.
/// JSON: { "type": "absolute_territory", "radius": n, "damage_per_turn": n, "turns": n }
/// </summary>
public sealed class AbsoluteTerritoryLeafEffect : EffectBase
{
	public int Radius, DamagePerTurn, Turns;

	public AbsoluteTerritoryLeafEffect(int r, int dpt, int t)
	{
		Radius = r;
		DamagePerTurn = dpt;
		Turns = t;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		var center = unit?.CurrentTile?.Axial ?? default;
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new AbsoluteTerritoryZone(center, Radius, DamagePerTurn, Turns, caster, unit));
		s.Log($"[AbsoluteTerritory] Zone r={Radius} / {DamagePerTurn}dpt / {Turns} turns centred on {center}.");
	}
}

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
