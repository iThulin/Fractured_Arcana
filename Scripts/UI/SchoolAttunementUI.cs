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

	// ── Chronomancer UI ─────────────────────────────────────────────
	private FateAttunement _boundFate;
	private ProgressBar _foresightBar;
	private Label _foresightTierLabel;

	// ── Necromancer UI ──────────────────────────────────────────────
	private GriefAttunement _boundGrief;
	private ProgressBar _griefBar;
	private Label _griefTierLabel;

	// ── Arcanist UI ─────────────────────────────────────────────────
	private ArcaneAttunement _boundArcane;
	private ProgressBar _chargeBar;
	private Tween _overchargePulseTween;
	private Label _chargeTierLabel;
	private Label _grimoireLabel;

	// ── Enchanter UI ────────────────────────────────────────────────
	private WeaveAttunement _boundWeave;
	private ProgressBar _weaveBar;
	private Label _weaveTierLabel;

	// ── UI refs ─────────────────────────────────────────────────────
	private VBoxContainer _container;
	private Label _titleLabel;
	private Label _stubLabel;
	private bool _isEmbedded = false;

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
		if (_currentUnit != unit)
			UnbindAttunement();

		_currentUnit = unit;

		if (unit == null || unit.Attunement == null)
		{
			if (_container != null)
				_container.Visible = false;
			else
				Visible = false;
			return;
		}

		var school = unit.School;
		if (school != _currentSchool)
		{
			_currentSchool = school;
			RebuildForSchool(school);
		}

		switch (unit.Attunement)
		{
			case ElementalAttunement elemAtt when elemAtt != _boundAttunement:
				BindElementalist(elemAtt);
				break;
			case GriefAttunement griefAtt when griefAtt != _boundGrief:
				BindNecromancer(griefAtt);
				break;
			case FateAttunement fateAtt when fateAtt != _boundFate:
				BindChronomancer(fateAtt);
				break;
			case ArcaneAttunement arcAtt when arcAtt != _boundArcane:
				BindArcanist(arcAtt);
				break;
			case WeaveAttunement weaveAtt when weaveAtt != _boundWeave:
				BindEnchanter(weaveAtt);
				break;
		}

		if (_container != null)
			_container.Visible = true;
		else
			Visible = true;
	}

	public void Refresh()
	{
		if (_boundAttunement != null)
			RefreshElementalistBars();
		if (_boundFate != null)
			RefreshForesightBar();
		if (_boundGrief != null)
			RefreshGriefBar();
		if (_boundArcane != null)
			RefreshArcanistUI();
		if (_boundWeave != null)
			RefreshWeaveBar();
	}

	public void UseExternalContainer(VBoxContainer externalContainer)
	{
		Visible = false;
		_isEmbedded = true;
		_container = externalContainer;
		_currentSchool = CardSchool.Adept; // force full rebuild on next ShowForUnit

		// If ShowForUnit already ran before wiring, redo it now into the correct container
		if (_currentUnit != null)
			ShowForUnit(_currentUnit);

		GD.Print($"[AttunementUI] Wired to external container. Unit: {_currentUnit?.Name ?? "none"}");
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
				_titleLabel.Text = "Grief";
				BuildNecromancerUI();
				if (_currentUnit?.Attunement is GriefAttunement griefAtt)
					BindNecromancer(griefAtt);
				break;
			case CardSchool.Arcanist:
				_titleLabel.Text = "The Grimoire";
				BuildArcanistUI();
				if (_currentUnit?.Attunement is ArcaneAttunement arcAtt)
					BindArcanist(arcAtt);
				break;
			case CardSchool.Enchanter:
				_titleLabel.Text = "The Weave";
				BuildEnchanterUI();
				if (_currentUnit?.Attunement is WeaveAttunement weaveAtt)
					BindEnchanter(weaveAtt);
				break;
			case CardSchool.Tinker:
				_titleLabel.Text = "Contraption Assembly";
				BuildStubUI("Coming soon.");
				break;
			case CardSchool.Chronomancer:
				_titleLabel.Text = "Foresight";
				BuildChronomancerUI();
				if (_currentUnit?.Attunement is FateAttunement fateAtt)  // ← _currentUnit, not unit
					BindChronomancer(fateAtt);
				break;
			default:
				if (_container != null)
					_container.Visible = false;
				else
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
		UnbindChronomancer();
		UnbindNecromancer();
		UnbindArcanist();
		UnbindEnchanter();
	}

	// ── Events ──────────────────────────────────────────────────────

	private void OnElementChargeChanged(ElementTag element, int newValue)
	{
		if (_elementBars.TryGetValue(element, out var bar))
			UpdateElementBar(bar, newValue);
	}

	private void OnElementBurst(ElementTag element)
	{
		if (!_elementBars.TryGetValue(element, out var bar))
			return;

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
		if (_boundAttunement == null)
			return;
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
	// Chronomancer-specific UI — similar structure to Elementalist but with custom labels and no element pairs
	// ════════════════════════════════════════════════════════════════

	// ── Build ────────────────────────────────────────────────────────────────────
	private void BuildChronomancerUI()
	{
		// Single row: "Foresight:" | ProgressBar (0-4) | Tier label
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 4);
		_container.AddChild(row);

		var nameLabel = new Label
		{
			Text = "Foresight:",
			CustomMinimumSize = new Vector2(72, 0),
			HorizontalAlignment = HorizontalAlignment.Left
		};
		row.AddChild(nameLabel);

		_foresightBar = new ProgressBar
		{
			CustomMinimumSize = new Vector2(80, UITheme.AttunementBarHeight),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MaxValue = FateAttunement.MaxCharges,
			Value = 0,
			Step = 1,
			ShowPercentage = false
		};
		var fillStyle = new StyleBoxFlat { BgColor = SchoolColors.GetBorderColor(CardSchool.Chronomancer) };
		_foresightBar.AddThemeStyleboxOverride("fill", fillStyle);
		row.AddChild(_foresightBar);

		_foresightTierLabel = new Label
		{
			Text = "",
			CustomMinimumSize = new Vector2(72, 0),
			HorizontalAlignment = HorizontalAlignment.Right
		};
		row.AddChild(_foresightTierLabel);
	}

	// Add the bar references as fields on SchoolAttunementUI:
	// private ProgressBar _foresightBar;
	// private Label _foresightTierLabel;

	private static readonly string[] ForesightTierLabels =
		{ "", "glimpsed", "aware", "prescient", "FORESEEN!" };

	// ── Bind ─────────────────────────────────────────────────────────────────────
	private void BindChronomancer(FateAttunement fate)
	{
		_boundFate = fate;
		fate.OnChargeChanged += OnForesightChanged;
		fate.OnBurstTriggered += OnForesightBurst;
		RefreshForesightBar();
	}

	private void UnbindChronomancer()
	{
		if (_boundFate == null)
			return;
		_boundFate.OnChargeChanged -= OnForesightChanged;
		_boundFate.OnBurstTriggered -= OnForesightBurst;
		_boundFate = null;
	}

	// ── Events ───────────────────────────────────────────────────────────────────
	private void OnForesightChanged(int newValue)
	{
		if (_foresightBar == null)
			return;
		_foresightBar.Value = newValue;
		_foresightTierLabel.Text = ForesightTierLabels[Math.Clamp(newValue, 0, 4)];
	}

	private void OnForesightBurst()
	{
		if (_foresightBar == null)
			return;
		var flashStyle = new StyleBoxFlat { BgColor = Colors.White };
		_foresightBar.AddThemeStyleboxOverride("fill", flashStyle);
		_foresightTierLabel.Text = "FORESEEN!";

		var tween = CreateTween();
		tween.TweenInterval(0.5f);
		tween.TweenCallback(Callable.From(() =>
		{
			var restoreStyle = new StyleBoxFlat { BgColor = SchoolColors.GetBorderColor(CardSchool.Chronomancer) };
			_foresightBar.AddThemeStyleboxOverride("fill", restoreStyle);
			if (_boundFate != null)
				OnForesightChanged(_boundFate.Charges);
		}));
	}

	private void RefreshForesightBar()
	{
		if (_foresightBar == null || _boundFate == null)
			return;
		_foresightBar.Value = _boundFate.Charges;
		_foresightTierLabel.Text = ForesightTierLabels[Math.Clamp(_boundFate.Charges, 0, 4)];
	}

	// ════════════════════════════════════════════════════════════════
	// NECROMANCER
	// ════════════════════════════════════════════════════════════════

	private static readonly string[] GriefTierLabels =
		{ "", "kindled", "spirits act", "enhanced", "FLOOD!" };

	private void BuildNecromancerUI()
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 4);
		_container.AddChild(row);

		row.AddChild(new Label
		{
			Text = "Grief:",
			CustomMinimumSize = new Vector2(72, 0),
			HorizontalAlignment = HorizontalAlignment.Left
		});

		_griefBar = new ProgressBar
		{
			CustomMinimumSize = new Vector2(80, UITheme.AttunementBarHeight),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MaxValue = GriefAttunement.MaxCharges,
			Value = 0,
			Step = 1,
			ShowPercentage = false
		};
		_griefBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
		{
			BgColor = SchoolColors.GetBorderColor(CardSchool.Necromancer)
		});
		row.AddChild(_griefBar);

		_griefTierLabel = new Label
		{
			Text = "",
			CustomMinimumSize = new Vector2(72, 0),
			HorizontalAlignment = HorizontalAlignment.Right
		};
		row.AddChild(_griefTierLabel);
	}

	private void BindNecromancer(GriefAttunement grief)
	{
		_boundGrief = grief;
		grief.OnChargeChanged += OnGriefChanged;
		grief.OnFloodTriggered += OnGriefFlood;
		RefreshGriefBar();
	}

	private void UnbindNecromancer()
	{
		if (_boundGrief == null)
			return;
		_boundGrief.OnChargeChanged -= OnGriefChanged;
		_boundGrief.OnFloodTriggered -= OnGriefFlood;
		_boundGrief = null;
	}

	private void OnGriefChanged(int newValue)
	{
		if (_griefBar == null)
			return;
		_griefBar.Value = newValue;
		_griefTierLabel.Text = GriefTierLabels[Math.Clamp(newValue, 0, 4)];
	}

	private void OnGriefFlood()
	{
		if (_griefBar == null)
			return;
		_griefBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Colors.White });
		_griefTierLabel.Text = "FLOOD!";

		var tween = CreateTween();
		tween.TweenInterval(0.5f);
		tween.TweenCallback(Callable.From(() =>
		{
			_griefBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
			{
				BgColor = SchoolColors.GetBorderColor(CardSchool.Necromancer)
			});
			if (_boundGrief != null)
				OnGriefChanged(_boundGrief.Charges);
		}));
	}

	private void RefreshGriefBar()
	{
		if (_griefBar == null || _boundGrief == null)
			return;
		_griefBar.Value = _boundGrief.Charges;
		_griefTierLabel.Text = GriefTierLabels[Math.Clamp(_boundGrief.Charges, 0, 4)];
	}

	// ════════════════════════════════════════════════════════════════
	// ARCANIST — Charge bar (0-6) + Grimoire memory line
	// ════════════════════════════════════════════════════════════════

	// Tier labels match ChargeTier enum: Latent / Resonant / Charged / Overflowing
	private static readonly string[] ChargeTierLabels =
		{ "latent", "latent", "resonant", "resonant", "charged", "charged", "overflow" };

	private void BuildArcanistUI()
	{
		if (_container == null)
		{
			GD.Print("[AttunementUI] BuildArcanistUI: _container is null — skipping");
			return;
		}
		// ── Row 1: Charge bar ────────────────────────────────────────────
		var chargeRow = new HBoxContainer();
		chargeRow.AddThemeConstantOverride("separation", 4);
		_container.AddChild(chargeRow);

		chargeRow.AddChild(new Label
		{
			Text = "Charge:",
			CustomMinimumSize = new Vector2(72, 0),
			HorizontalAlignment = HorizontalAlignment.Left
		});

		_chargeBar = new ProgressBar
		{
			CustomMinimumSize = new Vector2(80, UITheme.AttunementBarHeight),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MaxValue = ArcaneAttunement.MaxCharge,   // 6
			Value = 0,
			Step = 1,
			ShowPercentage = false
		};
		_chargeBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
		{
			BgColor = SchoolColors.GetBorderColor(CardSchool.Arcanist)
		});
		chargeRow.AddChild(_chargeBar);

		_chargeTierLabel = new Label
		{
			Text = "",
			CustomMinimumSize = new Vector2(72, 0),
			HorizontalAlignment = HorizontalAlignment.Right
		};
		chargeRow.AddChild(_chargeTierLabel);

		// ── Row 2: Grimoire memory (last spell + cast count) ─────────────
		// Single label spanning full width — updates whenever a spell is cast.
		_grimoireLabel = new Label
		{
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		_container.AddChild(_grimoireLabel);
	}

	private void BindArcanist(ArcaneAttunement arcane)
	{
		_boundArcane = arcane;
		arcane.OnChargeChanged += OnChargeChanged;
		arcane.OnChargeOverflow += OnChargeOverflow;
		arcane.OnSpellRecorded += OnSpellRecorded;
		RefreshArcanistUI();
		GD.Print($"[AttunementUI] Arcanist bound. Charge={arcane.Charge}");
	}

	private void UnbindArcanist()
	{
		if (_boundArcane == null)
			return;
		StopOverchargePulse();
		_boundArcane.OnChargeChanged -= OnChargeChanged;
		_boundArcane.OnChargeOverflow -= OnChargeOverflow;
		_boundArcane.OnSpellRecorded -= OnSpellRecorded;
		_boundArcane = null;
	}

	// ── Events ───────────────────────────────────────────────────────────

	private void OnChargeChanged(int newValue)
	{
		if (_chargeBar == null)
			return;
		_chargeBar.Value = newValue;
		_chargeTierLabel.Text = ChargeTierLabels[Math.Clamp(newValue, 0, 6)];

		if (newValue >= ArcaneAttunement.MaxCharge)
			StartOverchargePulse();
		else
			StopOverchargePulse();
	}

	private void OnChargeOverflow(int overflowAmount)
	{
		GD.Print($"[AttunementUI] OnChargeOverflow fired: amount={overflowAmount} label={_chargeTierLabel != null} bar={_chargeBar != null}");
		if (_chargeBar == null)
			return;

		// Pulse the bar three times so the player can't miss it
		var tween = CreateTween();
		tween.SetLoops(3);
		tween.TweenCallback(Callable.From(() =>
			_chargeBar.AddThemeStyleboxOverride("fill",
				new StyleBoxFlat { BgColor = Colors.White })));
		tween.TweenInterval(0.1f);
		tween.TweenCallback(Callable.From(() =>
			_chargeBar.AddThemeStyleboxOverride("fill",
				new StyleBoxFlat { BgColor = SchoolColors.GetBorderColor(CardSchool.Arcanist) })));
		tween.TweenInterval(0.1f);

		// Label stays on "+N draw!" until next charge change clears it
		if (_chargeTierLabel != null)
			_chargeTierLabel.Text = $"+{overflowAmount} draw!";
	}

	private void StartOverchargePulse()
	{
		_overchargePulseTween?.Kill();
		if (_chargeBar == null)
			return;

		var normalColor = SchoolColors.GetBorderColor(CardSchool.Arcanist);
		var pulseColor = new Color(1f, 0.95f, 0.4f); // hot amber

		_overchargePulseTween = CreateTween().SetLoops();
		_overchargePulseTween.TweenCallback(Callable.From(() =>
			_chargeBar.AddThemeStyleboxOverride("fill",
				new StyleBoxFlat { BgColor = pulseColor })));
		_overchargePulseTween.TweenInterval(0.35f);
		_overchargePulseTween.TweenCallback(Callable.From(() =>
			_chargeBar.AddThemeStyleboxOverride("fill",
				new StyleBoxFlat { BgColor = normalColor })));
		_overchargePulseTween.TweenInterval(0.35f);
	}

	private void StopOverchargePulse()
	{
		_overchargePulseTween?.Kill();
		_overchargePulseTween = null;
		if (_chargeBar != null)
			_chargeBar.AddThemeStyleboxOverride("fill",
				new StyleBoxFlat { BgColor = SchoolColors.GetBorderColor(CardSchool.Arcanist) });
	}

	private void OnSpellRecorded(int spellCount)
	{
		if (_grimoireLabel == null || _boundArcane == null)
			return;

		if (spellCount == 0 || string.IsNullOrEmpty(_boundArcane.LastSpellName))
		{
			_grimoireLabel.Text = "";
			return;
		}

		// "Spell 2: Cascade Bolt"
		_grimoireLabel.Text = $"Spell {spellCount}: {_boundArcane.LastSpellName}";
	}

	private void RefreshArcanistUI()
	{
		if (_chargeBar == null || _boundArcane == null)
			return;
		_chargeBar.Value = _boundArcane.Charge;
		_chargeTierLabel.Text = ChargeTierLabels[Math.Clamp(_boundArcane.Charge, 0, 6)];

		if (_grimoireLabel != null)
		{
			if (_boundArcane.SpellsCastThisTurn == 0 || string.IsNullOrEmpty(_boundArcane.LastSpellName))
				_grimoireLabel.Text = "";
			else
				_grimoireLabel.Text = $"Spell {_boundArcane.SpellsCastThisTurn}: {_boundArcane.LastSpellName}";
		}
	}


	// ════════════════════════════════════════════════════════════════
	// ENCHANTER — Weave bar (0-4) with Seventh Layer burst
	// ════════════════════════════════════════════════════════════════

	// Tier labels match WeaveTier enum: Loose / Taut / Woven / Bound / SeventhLayer
	private static readonly string[] WeaveTierLabels =
		{ "", "taut", "woven", "bound", "SEVENTH!" };

	private void BuildEnchanterUI()
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 4);
		_container.AddChild(row);

		row.AddChild(new Label
		{
			Text = "Weave:",
			CustomMinimumSize = new Vector2(72, 0),
			HorizontalAlignment = HorizontalAlignment.Left
		});

		_weaveBar = new ProgressBar
		{
			CustomMinimumSize = new Vector2(80, UITheme.AttunementBarHeight),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MaxValue = WeaveAttunement.MaxWeave,   // 4
			Value = 0,
			Step = 1,
			ShowPercentage = false
		};
		_weaveBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
		{
			BgColor = SchoolColors.GetBorderColor(CardSchool.Enchanter)
		});
		row.AddChild(_weaveBar);

		_weaveTierLabel = new Label
		{
			Text = "",
			CustomMinimumSize = new Vector2(72, 0),
			HorizontalAlignment = HorizontalAlignment.Right
		};
		row.AddChild(_weaveTierLabel);
	}

	private void BindEnchanter(WeaveAttunement weave)
	{
		_boundWeave = weave;
		weave.OnWeaveChanged += OnWeaveChanged;
		weave.OnSeventhLayer += OnSeventhLayer;
		RefreshWeaveBar();
	}

	private void UnbindEnchanter()
	{
		if (_boundWeave == null)
			return;
		_boundWeave.OnWeaveChanged -= OnWeaveChanged;
		_boundWeave.OnSeventhLayer -= OnSeventhLayer;
		_boundWeave = null;
	}

	// ── Events ───────────────────────────────────────────────────────────

	private void OnWeaveChanged(int newValue)
	{
		if (_weaveBar == null)
			return;
		_weaveBar.Value = newValue;
		_weaveTierLabel.Text = WeaveTierLabels[Math.Clamp(newValue, 0, 4)];
	}

	private void OnSeventhLayer()
	{
		if (_weaveBar == null)
			return;

		// Flash rose-white on burst
		_weaveBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Colors.White });
		_weaveTierLabel.Text = "SEVENTH!";

		var tween = CreateTween();
		tween.TweenInterval(0.5f);
		tween.TweenCallback(Callable.From(() =>
		{
			_weaveBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
			{
				BgColor = SchoolColors.GetBorderColor(CardSchool.Enchanter)
			});
			if (_boundWeave != null)
				OnWeaveChanged(_boundWeave.Weave);
		}));
	}

	private void RefreshWeaveBar()
	{
		if (_weaveBar == null || _boundWeave == null)
			return;
		_weaveBar.Value = _boundWeave.Weave;
		_weaveTierLabel.Text = WeaveTierLabels[Math.Clamp(_boundWeave.Weave, 0, 4)];
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
