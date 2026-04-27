using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// SchoolAttunementUI — Matches CombatUI visual style
//
// Uses same black StyleBoxFlat with 2px expand margins as
// SelectedUnitPanel. Sits directly below it at top-left.
// Uses ProgressBar for charge display (same as HP/mana bars).
// ============================================================

public partial class SchoolAttunementUI : PanelContainer
{
	// ── State ───────────────────────────────────────────────────────
	private Unit _currentUnit;
	private ElementalAttunement _boundAttunement;
	private CardSchool _currentSchool = CardSchool.Generic;

	// ── UI refs ─────────────────────────────────────────────────────
	private VBoxContainer _container;
	private Label _titleLabel;
	private Label _stubLabel;

	// Elementalist-specific
	private readonly Dictionary<ElementTag, ElementBar> _elementBars = new();

	// ── Colors matching your card element pips ──────────────────────
	private static readonly Dictionary<ElementTag, Color> BarFillColors = new()
	{
		{ ElementTag.Fire,  new Color(0.85f, 0.25f, 0.1f) },
		{ ElementTag.Ice,   new Color(0.3f, 0.6f, 0.9f) },
		{ ElementTag.Storm, new Color(0.8f, 0.75f, 0.15f) },
		{ ElementTag.Earth, new Color(0.5f, 0.38f, 0.2f) }
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
			BgColor = new Color(0, 0, 0, 1),
			ExpandMarginLeft = 2,
			ExpandMarginTop = 2,
			ExpandMarginRight = 2,
			ExpandMarginBottom = 2
		};
		AddThemeStyleboxOverride("panel", style);

		// Same width as SelectedUnitPanel
		CustomMinimumSize = new Vector2(252, 0);

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
			CustomMinimumSize = new Vector2(80, 12),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MaxValue = 4,
			Value = 0,
			Step = 1,
			ShowPercentage = false
		};

		// Color the fill to match the element
		var fillStyle = new StyleBoxFlat
		{
			BgColor = BarFillColors[element]
		};
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
			var restoreStyle = new StyleBoxFlat { BgColor = BarFillColors[element] };
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
		charges = Math.Clamp(charges, 0, 4);
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
