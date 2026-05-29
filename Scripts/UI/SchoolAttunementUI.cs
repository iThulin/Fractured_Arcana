using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// SchoolAttunementUI.cs
//
// Purpose:        Top-left HUD panel showing the selected unit's
//                 elemental attunement charges (per-element bars
//                 with tier labels). Currently Elementalist-only;
//                 other schools show a placeholder stub.
// Layer:          UI
// Collaborators:  Unit.cs (Attunement member),
//                 ElementalAttunement.cs (charge data source),
//                 UITheme.cs (element colours, padding)
// See:            README §6 — Elemental Attunement
// ============================================================

/// <summary>HUD panel below the selected-unit panel that renders the current unit's elemental attunement state. Binds to the unit's <see cref="ElementalAttunement"/> instance and updates per-element ProgressBars with the corresponding tier label. Style matches <c>SelectedUnitPanel</c> in CombatUI for visual continuity.</summary>
public partial class SchoolAttunementUI : PanelContainer
{
	// ── State ───────────────────────────────────────────────────────
	private Unit _currentUnit;
	private ElementalAttunement _boundAttunement;
	private CardSchool _currentSchool = CardSchool.Adept;

	// ── UI refs ─────────────────────────────────────────────────────
	private VBoxContainer _container;
	private Label _titleLabel;
	private Label _stubLabel;

	// Elementalist-specific
	private readonly Dictionary<ElementTag, ElementBar> _elementBars = new();

	// ── Colors matching your card element pips ──────────────────────
	private static Color GetElementColor(ElementTag element) => element switch
	{
		ElementTag.Fire => UITheme.ElementFire,
		ElementTag.Ice => UITheme.ElementIce,
		ElementTag.Storm => UITheme.ElementStorm,
		ElementTag.Earth => UITheme.ElementEarth,
		_ => UITheme.Neutral
	};

	private static readonly Dictionary<ElementTag, string> ElementNames = new()
	{
		{ ElementTag.Fire,  "Fire" },
		{ ElementTag.Ice,   "Ice" },
		{ ElementTag.Storm, "Storm" },
		{ ElementTag.Earth, "Earth" }
	};

	private static readonly string[] TierLabels = { "", "+1", "imbue", "enhanced", "BURST!" };

	public override void _Ready()
	{
		// Match SelectedUnitPanel: solid black, 2px expand margins
		var style = new StyleBoxFlat
		{
			BgColor = UITheme.WorldBase,
			ExpandMarginLeft = UITheme.PaddingSmall / 2,
			ExpandMarginTop = UITheme.PaddingSmall / 2,
			ExpandMarginRight = UITheme.PaddingSmall / 2,
			ExpandMarginBottom = UITheme.PaddingSmall / 2
		};
		AddThemeStyleboxOverride("panel", style);

		// Same width as SelectedUnitPanel
		CustomMinimumSize = new Vector2(UITheme.AttunementPanelWidth, 0);

		_container = new VBoxContainer();
		_container.AddThemeConstantOverride("separation", 4);

		// Add a margin container to match SelectedUnitPanel's internal padding
		var margin = new MarginContainer();
		margin.AddThemeConstantOverride("margin_left", 8);
		margin.AddThemeConstantOverride("margin_right", 8);
		margin.AddThemeConstantOverride("margin_top", 4);
		margin.AddThemeConstantOverride("margin_bottom", 4);
		AddChild(margin);
		margin.AddChild(_container);

		Visible = false;
	}

	// ════════════════════════════════════════════════════════════════
	// PUBLIC API
	// ════════════════════════════════════════════════════════════════

	public void ShowForUnit(Unit unit)
	{
		UnbindAttunement();
		_currentUnit = unit;

		if (unit == null || unit.Attunement == null)
		{
			Visible = false;
			return;
		}

		var school = unit.School;
		if (school != _currentSchool)
		{
			_currentSchool = school;
			RebuildForSchool(school);
		}

		if (school == CardSchool.Elementalist && unit.Attunement is ElementalAttunement elemAtt)
			BindElementalist(elemAtt);

		Visible = true;
	}

	public void Refresh()
	{
		if (_boundAttunement != null)
			RefreshElementalistBars();
	}

	// ════════════════════════════════════════════════════════════════
	// REBUILD
	// ════════════════════════════════════════════════════════════════

	private void RebuildForSchool(CardSchool school)
	{
		foreach (Node child in _container.GetChildren())
			child.QueueFree();
		_elementBars.Clear();
		_stubLabel = null;

		// Title — matches UnitNameLabel style (centered, default font)
		_titleLabel = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center
		};
		_container.AddChild(_titleLabel);

		switch (school)
		{
			case CardSchool.Elementalist:
				_titleLabel.Text = "Elemental Attunement";
				BuildElementalistUI();
				break;
			case CardSchool.Necromancer:
				_titleLabel.Text = "Necromantic Binding";
				BuildStubUI("Coming soon.");
				break;
			case CardSchool.Arcanist:
				_titleLabel.Text = "Arcane Focus";
				BuildStubUI("Coming soon.");
				break;
			case CardSchool.Enchanter:
				_titleLabel.Text = "Enchantment Weave";
				BuildStubUI("Coming soon.");
				break;
			case CardSchool.Tinker:
				_titleLabel.Text = "Contraption Assembly";
				BuildStubUI("Coming soon.");
				break;
			default:
				Visible = false;
				return;
		}
	}

	// ════════════════════════════════════════════════════════════════
	// ELEMENTALIST — uses ProgressBar rows like HP/Mana/Move bars
	// ════════════════════════════════════════════════════════════════

	private void BuildElementalistUI()
	{
		// Fire / Ice pair
		CreateElementRow(ElementTag.Fire);
		CreateElementRow(ElementTag.Ice);

		// Small separator
		var sep = new HSeparator();
		sep.AddThemeConstantOverride("separation", 2);
		_container.AddChild(sep);

		// Storm / Earth pair
		CreateElementRow(ElementTag.Storm);
		CreateElementRow(ElementTag.Earth);
	}

	private void CreateElementRow(ElementTag element)
	{
		var bar = new ElementBar { Element = element };

		// Row layout: Label | ProgressBar | TierLabel
		// Matches HealthRow/MoveRow/ManaRow pattern
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 4);
		_container.AddChild(row);

		// Element name label (fixed width, like "HP:" / "Mana:")
		bar.NameLabel = new Label
		{
			Text = $"{ElementNames[element]}:",
			CustomMinimumSize = new Vector2(48, 0),
			HorizontalAlignment = HorizontalAlignment.Left
		};
		row.AddChild(bar.NameLabel);

		// Progress bar — same style as HealthBar/ManaBar
		bar.Bar = new ProgressBar
		{
			CustomMinimumSize = new Vector2(80, UITheme.AttunementBarHeight),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MaxValue = UITheme.AttunementBarMax,
			Value = 0,
			Step = 1,
			ShowPercentage = false
		};

		var fillStyle = new StyleBoxFlat { BgColor = GetElementColor(element) };
		bar.Bar.AddThemeStyleboxOverride("fill", fillStyle);

		row.AddChild(bar.Bar);

		// Tier label (right-aligned, shows threshold effect)
		bar.TierLabel = new Label
		{
			Text = "",
			CustomMinimumSize = new Vector2(56, 0),
			HorizontalAlignment = HorizontalAlignment.Right
		};
		row.AddChild(bar.TierLabel);

		_elementBars[element] = bar;
	}

	// ── Binding ─────────────────────────────────────────────────────

	private void BindElementalist(ElementalAttunement att)
	{
		_boundAttunement = att;
		att.OnChargeChanged += OnElementChargeChanged;
		att.OnBurstTriggered += OnElementBurst;
		RefreshElementalistBars();
	}

	private void UnbindAttunement()
	{
		if (_boundAttunement != null)
		{
			_boundAttunement.OnChargeChanged -= OnElementChargeChanged;
			_boundAttunement.OnBurstTriggered -= OnElementBurst;
			_boundAttunement = null;
		}
	}

	// ── Events ──────────────────────────────────────────────────────

	private void OnElementChargeChanged(ElementTag element, int newValue)
	{
		if (_elementBars.TryGetValue(element, out var bar))
			UpdateElementBar(bar, newValue);
	}

	private void OnElementBurst(ElementTag element)
	{
		if (!_elementBars.TryGetValue(element, out var bar)) return;

		// Flash the bar white briefly
		var flashStyle = new StyleBoxFlat { BgColor = Colors.White };
		bar.Bar.AddThemeStyleboxOverride("fill", flashStyle);
		bar.TierLabel.Text = "BURST!";

		var tween = CreateTween();
		tween.TweenInterval(0.5f);
		tween.TweenCallback(Callable.From(() =>
		{
			// Restore normal color
			var restoreStyle = new StyleBoxFlat { BgColor = GetElementColor(element) };
			bar.Bar.AddThemeStyleboxOverride("fill", restoreStyle);
			if (_boundAttunement != null)
				UpdateElementBar(bar, _boundAttunement.Charges[element]);
		}));
	}

	// ── Rendering ───────────────────────────────────────────────────

	private void RefreshElementalistBars()
	{
		if (_boundAttunement == null) return;
		foreach (var kvp in _elementBars)
			UpdateElementBar(kvp.Value, _boundAttunement.Charges[kvp.Key]);
	}

	private void UpdateElementBar(ElementBar bar, int charges)
	{
		charges = Math.Clamp(charges, 0, UITheme.AttunementBarMax);
		bar.Bar.Value = charges;

		int tierIdx = charges >= 4 ? 4 : charges >= 3 ? 3 : charges >= 2 ? 2 : charges >= 1 ? 1 : 0;
		bar.TierLabel.Text = TierLabels[tierIdx];
	}

	// ════════════════════════════════════════════════════════════════
	// STUB — placeholder for future schools
	// ════════════════════════════════════════════════════════════════

	private void BuildStubUI(string message)
	{
		_stubLabel = new Label
		{
			Text = message,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		_container.AddChild(_stubLabel);
	}

	// ════════════════════════════════════════════════════════════════
	// Internal data
	// ════════════════════════════════════════════════════════════════

	private class ElementBar
	{
		public ElementTag Element;
		public Label NameLabel;
		public ProgressBar Bar;
		public Label TierLabel;
	}
}
