using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ElementalAttunement.cs
//
// Purpose:        The Elementalist school mechanic. Four counters
//                 (Fire, Ice, Storm, Earth) with opposition
//                 pairs (Fire↔Ice, Storm↔Earth) and four tier
//                 thresholds (1: +1 dmg; 2: auto-imbue; 3:
//                 enhanced effect; 4: burst AoE then reset to 0).
//                 Charges persist across turns. Per-turn decay
//                 was removed 2026-07-06 (Decay() is a no-op);
//                 charges fall only via opposition or a burst.
//                 Other schools will get their own
//                 ISchoolAttunement implementations later.
// Layer:          System
// Collaborators:  Unit.cs (each Elementalist unit owns one),
//                 AttunementResolver.cs (mutates this),
//                 SchoolAttunementUI.cs (renders charges),
//                 CompositeEffects.cs (ElementalConvergence
//                 sets counters directly)
// See:            README §6, Elemental Attunement
// ============================================================

/// <summary>Element identity tags for the Elementalist attunement system. Mapped to JSON tag strings on cards via aliasing in the predicate/effect code.</summary>
public enum ElementTag
{
	Fire,
	Ice,
	Storm,
	Earth
}

public enum AttunementTier
{
	None,     // 0 charges
	Minor,    // 1 charge:  +1 bonus damage
	Imbue,    // 2 charges: auto-imbue target tile
	Enhanced, // 3 charges: enhanced effect (burn/slow/chain/armor)
	Burst     // 4 charges: big AoE, then reset to 0
}

public struct AttunementEffect
{
	public ElementTag Element;
	public AttunementTier Tier;
	public string Description;
}

/// <summary>
/// Interface for any school's class mechanic tracker.
/// Lets GameRunner and UI work with any school without knowing the details.
/// </summary>
public interface ISchoolAttunement
{
	CardSchool School { get; }
	void Decay();
	void OnCombatStart();
}

public class ElementalAttunement : ISchoolAttunement
{
	public CardSchool School => CardSchool.Elementalist;

	// ── Core state ──────────────────────────────────────────────────
	public Dictionary<ElementTag, int> Charges { get; private set; } = new()
	{
		{ ElementTag.Fire,  0 },
		{ ElementTag.Ice,   0 },
		{ ElementTag.Storm, 0 },
		{ ElementTag.Earth, 0 }
	};

	public const int MaxCharges = 4;
	public const int BurstThreshold = 4;

	/// <summary>Fixed tie order for every "your highest element" read (shown in
	/// tooltips): Fire, then Ice, then Storm, then Earth.</summary>
	public static readonly ElementTag[] TieOrder =
		{ ElementTag.Fire, ElementTag.Ice, ElementTag.Storm, ElementTag.Earth };

	/// <summary>Element of the most recent element-tagged cast; null before any.
	/// Read by Avatar of Elements (class_identity_elementalist_v1 §4: no random
	/// element picks).</summary>
	public ElementTag? LastCastElement { get; private set; }

	/// <summary>The element holding the most attunement, ties broken by
	/// <see cref="TieOrder"/>. With every counter at 0 it falls back to the last
	/// cast element, then Fire.</summary>
	public ElementTag HighestElement()
	{
		ElementTag best = ElementTag.Fire;
		int bestValue = -1;
		foreach (var e in TieOrder)
		{
			if (Charges[e] > bestValue)
			{
				best = e;
				bestValue = Charges[e];
			}
		}
		if (bestValue <= 0 && LastCastElement.HasValue)
			return LastCastElement.Value;
		return best;
	}

	// ── Crucible of Storms doctrines (campus_building_upgrades_design_v1 §8d).
	// Set at combat start by SchoolSeats.ApplyCombatStart; defaults are the
	// school as it has always played. ──────────────────────────────────────
	/// <summary>Charges at which an element bursts. 4 normally; 3 under the
	/// Monoelement doctrine.</summary>
	public int BurstAt = BurstThreshold;

	/// <summary>Monoelement: only one element may hold attunement. Charging an
	/// element empties every other.</summary>
	public bool Monoelement = false;

	/// <summary>Confluence: the third DIFFERENT element cast in one turn triggers
	/// that element's burst for free (its charges are kept). Once per turn.</summary>
	public bool Confluence = false;

	private readonly HashSet<ElementTag> _elementsThisTurn = new();
	private bool _confluenceFiredThisTurn = false;

	// ── Opposition pairs ────────────────────────────────────────────
	private static readonly Dictionary<ElementTag, ElementTag> Opposition = new()
	{
		{ ElementTag.Fire,  ElementTag.Ice },
		{ ElementTag.Ice,   ElementTag.Fire },
		{ ElementTag.Storm, ElementTag.Earth },
		{ ElementTag.Earth, ElementTag.Storm }
	};

	// ── Events for UI ───────────────────────────────────────────────
	public event Action<ElementTag, int> OnChargeChanged;    // element, new value
	public event Action<ElementTag, int> OnThresholdReached; // element, threshold level
	public event Action<ElementTag> OnBurstTriggered;        // element that burst

	public void OnCombatStart()
	{
		foreach (var key in new[] { ElementTag.Fire, ElementTag.Ice, ElementTag.Storm, ElementTag.Earth })
			Charges[key] = 0;
		LastCastElement = null;
	}

	/// <summary>
	/// Direct charge gain outside the cast pipeline (attunement_per_nearby_element:
	/// reading the land, not casting). No opposition reduction; fires the same UI
	/// events as cast-driven gains, including threshold events.
	/// </summary>
	public void GainCharge(ElementTag element, int amount = 1)
	{
		if (Monoelement)
			ClearOthers(element);
		int oldValue = Charges[element];
		Charges[element] = Math.Min(oldValue + amount, MaxCharges);
		if (Charges[element] == oldValue)
			return;
		OnChargeChanged?.Invoke(element, Charges[element]);
		for (int i = oldValue + 1; i <= Charges[element]; i++)
			OnThresholdReached?.Invoke(element, i);
	}

	// ── Called when a spell with element tags is cast ────────────────
	public List<AttunementEffect> OnSpellCast(string[] tags)
	{
		var effects = new List<AttunementEffect>();
		if (tags == null || tags.Length == 0) return effects;

		foreach (var tagStr in tags)
		{
			if (!TryParseTag(tagStr, out var element)) continue;

			LastCastElement = element;

			if (Monoelement)
				ClearOthers(element);

			int oldValue = Charges[element];
			Charges[element] = Math.Min(Charges[element] + 1, MaxCharges);

			// Reduce opposition
			if (Opposition.TryGetValue(element, out var opposite))
			{
				int oldOpp = Charges[opposite];
				Charges[opposite] = Math.Max(0, Charges[opposite] - 1);
				if (Charges[opposite] != oldOpp)
					OnChargeChanged?.Invoke(opposite, Charges[opposite]);
			}

			int newValue = Charges[element];
			OnChargeChanged?.Invoke(element, newValue);

			// Confluence: the third different element this turn bursts for free.
			bool confluence = false;
			if (Confluence)
			{
				_elementsThisTurn.Add(element);
				if (!_confluenceFiredThisTurn && _elementsThisTurn.Count >= 3)
				{
					confluence = true;
					_confluenceFiredThisTurn = true;
				}
			}

			if (newValue >= BurstAt)
			{
				effects.Add(new AttunementEffect
				{
					Element = element,
					Tier = AttunementTier.Burst,
					Description = GetBurstDescription(element)
				});
				OnBurstTriggered?.Invoke(element);
				if (!CombatSim.Active) RegisterManager.Fire("elementalist.burst");
				Charges[element] = 0;
				OnChargeChanged?.Invoke(element, 0);
			}
			else if (newValue > oldValue && newValue >= 1)
			{
				OnThresholdReached?.Invoke(element, newValue);
			}

			if (confluence && !effects.Exists(e => e.Element == element && e.Tier == AttunementTier.Burst))
			{
				effects.Add(new AttunementEffect
				{
					Element = element,
					Tier = AttunementTier.Burst,
					Description = "CONFLUENCE: " + GetBurstDescription(element)
				});
				OnBurstTriggered?.Invoke(element);
				if (!CombatSim.Active) RegisterManager.Fire("elementalist.burst");
			}
		}

		return effects;
	}

	/// <summary>Monoelement: empty every element but <paramref name="keep"/>.</summary>
	private void ClearOthers(ElementTag keep)
	{
		foreach (var key in new[] { ElementTag.Fire, ElementTag.Ice, ElementTag.Storm, ElementTag.Earth })
		{
			if (key == keep || Charges[key] == 0)
				continue;
			Charges[key] = 0;
			OnChargeChanged?.Invoke(key, 0);
		}
	}

	// ── Turn decay ──────────────────────────────────────────────────
	/// <summary>
	/// No-op by design (2026-07-06). Per-turn decay was removed: with charges also
	/// dropping via opposition on every paired-element cast, the double erosion made
	/// building to the tier-2/3/4 thresholds unreliable in play. Charges now persist
	/// across turns and only fall via opposition (<see cref="OnSpellCast"/>) or a
	/// tier-4 burst reset. Decay() is kept to satisfy ISchoolAttunement and the
	/// shared per-turn Attunement.Decay() call; other schools still decay normally.
	/// </summary>
	public void Decay()
	{
		// Charges do not decay (see summary). The Confluence count is per turn,
		// and Decay is the per-turn call, so it resets here.
		_elementsThisTurn.Clear();
		_confluenceFiredThisTurn = false;
	}

	// ── Query methods ───────────────────────────────────────────────

	public int GetBonusDamage(ElementTag element)
	{
		int charges = Charges[element];
		if (charges >= 3) return 2;
		if (charges >= 1) return 1;
		return 0;
	}

	/// <summary>R22 damage preview: the attunement bonus a cast carrying this
	/// element WOULD deal, accounting for THIS cast's own charge increment.
	/// At cast time OnSpellCast advances the meter (+1, cap, burst-reset)
	/// BEFORE ApplyThresholdEffects reads GetBonusDamage, so a live read is one
	/// step behind. This mirrors that increment purely (no mutation, no events).
	/// KEEP IN SYNC with OnSpellCast's charge math + GetBonusDamage's thresholds.</summary>
	public int PreviewBonusDamageAfterCast(ElementTag element)
	{
		int charges = System.Math.Min(Charges[element] + 1, MaxCharges);
		if (charges >= BurstAt) charges = 0;   // burst resets before the bonus is read
		if (charges >= 3) return 2;
		if (charges >= 1) return 1;
		return 0;
	}

	public bool ShouldAutoImbue(ElementTag element) => Charges[element] >= 2;
	public bool ShouldEnhance(ElementTag element) => Charges[element] >= 3;

	public AttunementTier GetTier(ElementTag element)
	{
		int charges = Charges[element];
		if (charges >= 4) return AttunementTier.Burst;
		if (charges >= 3) return AttunementTier.Enhanced;
		if (charges >= 2) return AttunementTier.Imbue;
		if (charges >= 1) return AttunementTier.Minor;
		return AttunementTier.None;
	}

	// ── Helpers ─────────────────────────────────────────────────────

	public static bool TryParseTag(string tag, out ElementTag element)
	{
		element = ElementTag.Fire;
		if (string.IsNullOrEmpty(tag)) return false;
		return tag.ToLowerInvariant() switch
		{
			"fire"  => Assign(out element, ElementTag.Fire),
			"ice"   => Assign(out element, ElementTag.Ice),
			"frost" => Assign(out element, ElementTag.Ice),
			"storm" => Assign(out element, ElementTag.Storm),
			"stone" => Assign(out element, ElementTag.Earth),
			"earth" => Assign(out element, ElementTag.Earth),
			_ => false
		};
	}

	private static bool Assign(out ElementTag element, ElementTag value)
	{
		element = value;
		return true;
	}

	private string GetBurstDescription(ElementTag element) => element switch
	{
		ElementTag.Fire  => "FIRE BURST: Nova. Deal 6 damage to all enemies!",
		ElementTag.Ice   => "ICE BURST: Freeze Wave. Freeze all enemies for 1 turn!",
		ElementTag.Storm => "STORM BURST: Lightning Strike. Deal 8 damage to nearest enemy, chain to 1 adjacent!",
		ElementTag.Earth => "EARTH BURST: Quake. All enemies lose 2 movement, caster gains 6 armor!",
		_ => "Elemental burst!"
	};
}
