using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ElementalAttunement.cs
//
// Purpose:        The Elementalist school mechanic — 4 counters
//                 (Fire, Ice, Storm, Earth) with opposition
//                 pairs (Fire↔Ice, Storm↔Earth), per-turn decay,
//                 and four tier thresholds (1: +1 dmg; 2: auto-
//                 imbue; 3: enhanced effect; 4: burst AoE then
//                 reset to 0). Other schools will get their own
//                 ISchoolAttunement implementations later.
// Layer:          System
// Collaborators:  Unit.cs (each Elementalist unit owns one),
//                 AttunementResolver.cs (mutates this),
//                 SchoolAttunementUI.cs (renders charges),
//                 CompositeEffects.cs (ElementalConvergence
//                 sets counters directly)
// See:            README §6 — Elemental Attunement
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
	}

	// ── Called when a spell with element tags is cast ────────────────
	public List<AttunementEffect> OnSpellCast(string[] tags)
	{
		var effects = new List<AttunementEffect>();
		if (tags == null || tags.Length == 0) return effects;

		foreach (var tagStr in tags)
		{
			if (!TryParseTag(tagStr, out var element)) continue;

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

			if (newValue >= BurstThreshold)
			{
				effects.Add(new AttunementEffect
				{
					Element = element,
					Tier = AttunementTier.Burst,
					Description = GetBurstDescription(element)
				});
				OnBurstTriggered?.Invoke(element);
				Charges[element] = 0;
				OnChargeChanged?.Invoke(element, 0);
			}
			else if (newValue > oldValue && newValue >= 1)
			{
				OnThresholdReached?.Invoke(element, newValue);
			}
		}

		return effects;
	}

	// ── Turn decay ──────────────────────────────────────────────────
	public void Decay()
	{
		foreach (var key in new[] { ElementTag.Fire, ElementTag.Ice, ElementTag.Storm, ElementTag.Earth })
		{
			if (Charges[key] > 0)
			{
				Charges[key]--;
				OnChargeChanged?.Invoke(key, Charges[key]);
			}
		}
	}

	// ── Query methods ───────────────────────────────────────────────

	public int GetBonusDamage(ElementTag element)
	{
		int charges = Charges[element];
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
		ElementTag.Fire  => "FIRE BURST: Nova — Deal 6 damage to all enemies!",
		ElementTag.Ice   => "ICE BURST: Freeze Wave — Freeze all enemies for 1 turn!",
		ElementTag.Storm => "STORM BURST: Lightning Strike — Deal 8 damage to nearest enemy, chain to 1 adjacent!",
		ElementTag.Earth => "EARTH BURST: Quake — All enemies lose 2 movement, caster gains 6 armor!",
		_ => "Elemental burst!"
	};
}
