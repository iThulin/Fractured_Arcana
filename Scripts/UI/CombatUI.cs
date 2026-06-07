using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatUI.cs
//
// Purpose:        CanvasLayer hosting all in-combat HUD panels.
//                 Fully procedural — no .tscn layout dependencies.
//                 All children built via CallDeferred(nameof(BuildUI))
//                 to satisfy Mac + Metal Compatibility mode rules.
//
// Layout:
//   Top-left  (280px wide, shrinks to content)
//             ┌─ Left Panel ──────────────────────────┐
//             │  ROUND 1 - PLAYER TURN                │
//             │  ─────────────────────────────────── │
//             │  Wizard_1  (large name)               │
//             │  HP ████████░░  12/20                 │
//             │  MP ████░░░░░░   4/10                 │
//             │  ARM 3  AP ●●○  SPD 4                 │
//             │  🔥 ❄ (status icons)                  │
//             │  ── LOG ─────────────────────────── │
//             │  Ranger_1 repositions.                │
//             │  Wizard_3 begins channelling...       │
//             │  ── PARTY ───────────────────────── │
//             │  [Wiz1 ●●●][Wiz2 ●○○]                │
//             │  [Deck 5]  [Grave 2]                  │
//             │  [     End Turn     ]                 │
//             └───────────────────────────────────────┘
//
//   Top-right (220px wide, shrinks to content)
//             ┌─ Enemy Roster ────────────────────────┐
//             │  ─ ENEMIES ─                          │
//             │  Ranger_1   ████████░░  18/20         │
//             │  Wizard_3   ████░░░░░░  10/20         │
//             └───────────────────────────────────────┘
//
//   Cards sit at the bottom of the screen with no bar below them.
//
// Layer:          UI
// Collaborators:  CombatManager.cs, Unit.cs, UITheme.cs
// See:            README §8 (Godot 4.6 compat — CallDeferred rules)
// ============================================================

public partial class CombatUI : CanvasLayer
{
	// ── Signals ──────────────────────────────────────────────────────────
	[Signal] public delegate void ConfirmDeploymentPressedEventHandler();
	[Signal] public delegate void EndTurnPressedEventHandler();
	[Signal] public delegate void UnitButtonPressedEventHandler(int unitIndex);
	[Signal] public delegate void EnemyButtonPressedEventHandler(int unitIndex);

	// ── Layout constants ─────────────────────────────────────────────────
	private const int LeftPanelWidth = 280;
	private const int RightPanelWidth = 220;
	private const int PanelPadding = 10;
	private const int BarHeight = 10;
	private const int ManaBarHeight = 8;
	private const int LogLineCount = 6;
	private const int UnitButtonWidth = 110;
	private const int EnemyBarWidth = 90;

	// ── Left panel nodes ─────────────────────────────────────────────────
	private PanelContainer _leftPanel;
	private Label _phaseLabel;
	private Label _unitNameLabel;
	private ProgressBar _hpBar;
	private Label _hpText;
	private ProgressBar _mpBar;
	private Label _mpText;
	private Label _statLine;
	private Label _stanceLine;
	private HBoxContainer _statusIconRow;
	private VBoxContainer _logBox;
	private Label[] _logLines;
	private Label _hintLabel;
	private HBoxContainer _playerUnitBar;
	private Button _deckButton;
	private Button _graveButton;
	private Button _endTurnButton;
	private Button _confirmDeploymentButton;

	// Pending selected unit state for when ShowSelectedUnit arrives before BuildUI
	private Unit _pendingUnit = null;
	private int _pendingMana = 0;
	private bool _unitPending = false;

	private VBoxContainer _attunementSection;

	// ── Right panel nodes ────────────────────────────────────────────────
	private PanelContainer _rightPanel;
	private VBoxContainer _enemyRosterBox;

	// ── Popups ───────────────────────────────────────────────────────────
	private PopupPanel _gravePopup;
	private ItemList _graveList;
	private PopupPanel _deckPopup;
	private ItemList _deckList;

	// ── Log ring buffer ──────────────────────────────────────────────────
	private readonly Queue<string> _logQueue = new Queue<string>();

	// ── Pending state for calls that arrive before BuildUI fires ─────────
	private List<EnemyIntelEntry> _pendingIntel = null;

	// ── Build / pending state ─────────────────────────────────────────────
	private bool _built = false;
	private bool _pendingDeploymentMode = false;
	private bool _deploymentModePending = false;

	// ── Status display map ───────────────────────────────────────────────
	private static readonly Dictionary<string, (string symbol, Color color)> StatusDisplay = new()
	{
		{ "burn",                 ("🔥", new Color(1.0f,  0.45f, 0.1f))  },
		{ "frozen",               ("❄",  new Color(0.4f,  0.8f,  1.0f))  },
		{ "poisoned",             ("☠",  new Color(0.5f,  0.9f,  0.2f))  },
		{ "stunned",              ("★",  new Color(1.0f,  0.95f, 0.3f))  },
		{ "rooted",               ("⊕",  new Color(0.55f, 0.85f, 0.3f))  },
		{ "slowed",               ("↓",  new Color(0.6f,  0.6f,  0.9f))  },
		{ "haunted",              ("✦",  new Color(0.7f,  0.4f,  1.0f))  },
		{ "bound",                ("⛓",  new Color(0.75f, 0.65f, 0.4f))  },
		{ "arcane_mark",          ("◈",  new Color(0.4f,  0.7f,  1.0f))  },
		{ "chaining",             ("⚡",  new Color(0.9f,  0.85f, 0.2f))  },
		{ "vigil",                ("👁",  new Color(0.85f, 0.85f, 1.0f))  },
		{ "undying_turn",         ("↺",  new Color(0.9f,  0.7f,  0.3f))  },
		{ "undying_full_restore", ("✙",  new Color(0.9f,  0.7f,  0.3f))  },
	};

	// ════════════════════════════════════════════════════════════════════
	// Lifecycle
	// ════════════════════════════════════════════════════════════════════

	public override void _Ready()
	{
		CallDeferred(nameof(BuildUI));
	}

	private void BuildUI()
	{
		if (_built)
			return;
		_built = true;

		BuildLeftPanel();
		BuildRightPanel();
		BuildPopups();

		RedrawLog();

		if (_unitPending)
			ApplySelectedUnit(_pendingUnit, _pendingMana);

		if (_pendingIntel != null)
			BuildEnemyIntelRows(_pendingIntel);

		if (_deploymentModePending)
			ApplyDeploymentMode();
	}
	// ════════════════════════════════════════════════════════════════════
	// Left panel
	// ════════════════════════════════════════════════════════════════════

	private void BuildLeftPanel()
	{
		_leftPanel = new PanelContainer
		{
			Name = "LeftPanel",
			AnchorLeft = 0f,
			AnchorTop = 0f,
			AnchorRight = 0f,
			AnchorBottom = 0f,
			OffsetRight = LeftPanelWidth,
			GrowHorizontal = Control.GrowDirection.End,
			GrowVertical = Control.GrowDirection.End,
		};
		_leftPanel.AddThemeStyleboxOverride("panel",
			UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Violet));
		AddChild(_leftPanel);

		var margin = new MarginContainer { Name = "Margin" };
		margin.AddThemeConstantOverride("margin_left", PanelPadding);
		margin.AddThemeConstantOverride("margin_right", PanelPadding);
		margin.AddThemeConstantOverride("margin_top", PanelPadding);
		margin.AddThemeConstantOverride("margin_bottom", PanelPadding);
		margin.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
		_leftPanel.AddChild(margin);

		var vbox = new VBoxContainer { Name = "VBox" };
		vbox.AddThemeConstantOverride("separation", 6);
		margin.AddChild(vbox);

		// ── Phase ───────────────────────────────────────────────────
		_phaseLabel = MakeLabel("", UITheme.FontSizeSmall, UITheme.Violet);
		_phaseLabel.HorizontalAlignment = HorizontalAlignment.Center;
		vbox.AddChild(_phaseLabel);

		vbox.AddChild(MakeDivider(UITheme.Violet));

		// ── Unit name ────────────────────────────────────────────────
		_unitNameLabel = MakeLabel("—", UITheme.FontSizeLarge, UITheme.TextPrimary);
		_unitNameLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_unitNameLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		vbox.AddChild(_unitNameLabel);

		// ── HP bar ───────────────────────────────────────────────────
		vbox.AddChild(MakeBarRow("HP", BarHeight,
			out _hpBar, out _hpText,
			UITheme.StatBarHealth, UITheme.BgDeep));

		// ── MP bar ───────────────────────────────────────────────────
		vbox.AddChild(MakeBarRow("MP", ManaBarHeight,
			out _mpBar, out _mpText,
			UITheme.StatBarMana, UITheme.BgDeep));

		// ── Stat line ────────────────────────────────────────────────
		_statLine = MakeLabel("", UITheme.FontSizeSmall, UITheme.TextSecondary);
		_statLine.HorizontalAlignment = HorizontalAlignment.Center;
		vbox.AddChild(_statLine);

		// ── Stance (martial only) ────────────────────────────────────
		_stanceLine = MakeLabel("", UITheme.FontSizeSmall, UITheme.Gold);
		_stanceLine.HorizontalAlignment = HorizontalAlignment.Center;
		_stanceLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_stanceLine.Visible = false;
		vbox.AddChild(_stanceLine);

		// ── Status icons ─────────────────────────────────────────────
		_statusIconRow = new HBoxContainer { Name = "StatusIcons" };
		_statusIconRow.AddThemeConstantOverride("separation", 4);
		_statusIconRow.Alignment = BoxContainer.AlignmentMode.Center;
		vbox.AddChild(_statusIconRow);

		// Attunement slot — populated by SchoolAttunementUI.UseExternalContainer()
		vbox.AddChild(MakeDivider(UITheme.VioletDim));
		_attunementSection = new VBoxContainer { Name = "AttunementSection" };
		_attunementSection.AddThemeConstantOverride("separation", 4);
		_attunementSection.Visible = false;   // hidden until a school with an attunement is selected
		GD.Print($"[CombatUI] AttunementSection built: {_attunementSection != null}");
		vbox.AddChild(_attunementSection);

		// ── Action log ───────────────────────────────────────────────
		vbox.AddChild(MakeDivider());

		var logHeader = MakeLabel("─ LOG ─", UITheme.FontSizeSmall, UITheme.TextDim);
		logHeader.HorizontalAlignment = HorizontalAlignment.Center;
		vbox.AddChild(logHeader);

		_logBox = new VBoxContainer { Name = "LogBox" };
		_logBox.AddThemeConstantOverride("separation", 2);
		_logBox.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;

		_logLines = new Label[LogLineCount];
		for (int i = 0; i < LogLineCount; i++)
		{
			var lbl = MakeLabel("", UITheme.FontSizeSmall,
				i == LogLineCount - 1 ? UITheme.TextPrimary : UITheme.TextDim);
			lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			_logLines[i] = lbl;
			_logBox.AddChild(lbl);
		}
		vbox.AddChild(_logBox);

		// ── Party section ────────────────────────────────────────────
		vbox.AddChild(MakeDivider());

		var partyHeader = MakeLabel("─ PARTY ─", UITheme.FontSizeSmall, UITheme.TextDim);
		partyHeader.HorizontalAlignment = HorizontalAlignment.Center;
		vbox.AddChild(partyHeader);

		_playerUnitBar = new HBoxContainer { Name = "UnitBar" };
		_playerUnitBar.AddThemeConstantOverride("separation", 4);
		_playerUnitBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		vbox.AddChild(_playerUnitBar);

		// ── Deck / Grave row ─────────────────────────────────────────
		var deckRow = new HBoxContainer { Name = "DeckRow" };
		deckRow.AddThemeConstantOverride("separation", 6);
		vbox.AddChild(deckRow);

		_deckButton = MakeSmallButton("Deck —");
		_deckButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_deckButton.Pressed += OnDeckButtonPressed;
		deckRow.AddChild(_deckButton);

		_graveButton = MakeSmallButton("Grave —");
		_graveButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_graveButton.Pressed += OnGraveButtonPressed;
		deckRow.AddChild(_graveButton);

		// ── Confirm Deployment (hidden by default) ───────────────────
		_confirmDeploymentButton = new Button
		{
			Name = "ConfirmDeployBtn",
			Text = "Confirm Deployment",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Visible = false,
		};
		UITheme.ApplyButtonStyle(_confirmDeploymentButton, isPrimary: true);
		_confirmDeploymentButton.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
		_confirmDeploymentButton.Pressed += () => EmitSignal(SignalName.ConfirmDeploymentPressed);
		vbox.AddChild(_confirmDeploymentButton);

		// ── End Turn ─────────────────────────────────────────────────
		_endTurnButton = new Button
		{
			Name = "EndTurnButton",
			Text = "End Turn",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		StyleEndTurnButton(_endTurnButton);
		_endTurnButton.Pressed += () =>
		{
			if (_endTurnButton.Text == "Confirm Deployment")
				EmitSignal(SignalName.ConfirmDeploymentPressed);
			else
				EmitSignal(SignalName.EndTurnPressed);
		};
		vbox.AddChild(_endTurnButton);
	}

	// ════════════════════════════════════════════════════════════════════
	// Right panel — enemy roster
	// ════════════════════════════════════════════════════════════════════

	private void BuildRightPanel()
	{
		_rightPanel = new PanelContainer
		{
			Name = "RightPanel",
			AnchorLeft = 1f,
			AnchorTop = 0f,
			AnchorRight = 1f,
			AnchorBottom = 0f,
			OffsetLeft = -RightPanelWidth,
			GrowHorizontal = Control.GrowDirection.Begin,
			GrowVertical = Control.GrowDirection.End,
		};
		_rightPanel.AddThemeStyleboxOverride("panel",
			UITheme.MakePanelStyle(UITheme.BgBase, UITheme.VioletDim));
		AddChild(_rightPanel);

		var margin = new MarginContainer { Name = "Margin" };
		margin.AddThemeConstantOverride("margin_left", PanelPadding);
		margin.AddThemeConstantOverride("margin_right", PanelPadding);
		margin.AddThemeConstantOverride("margin_top", PanelPadding);
		margin.AddThemeConstantOverride("margin_bottom", PanelPadding);
		_rightPanel.AddChild(margin);

		var vbox = new VBoxContainer { Name = "VBox" };
		vbox.AddThemeConstantOverride("separation", 4);
		margin.AddChild(vbox);

		var header = MakeLabel("─ ENEMIES ─", UITheme.FontSizeSmall, UITheme.Violet);
		header.HorizontalAlignment = HorizontalAlignment.Center;
		vbox.AddChild(header);

		vbox.AddChild(MakeDivider(UITheme.VioletDim));

		_enemyRosterBox = new VBoxContainer { Name = "EnemyRoster" };
		_enemyRosterBox.AddThemeConstantOverride("separation", 5);
		vbox.AddChild(_enemyRosterBox);
	}

	// ════════════════════════════════════════════════════════════════════
	// Popups
	// ════════════════════════════════════════════════════════════════════

	private void BuildPopups()
	{
		_deckPopup = new PopupPanel { Name = "DeckPopup" };
		_deckList = new ItemList { Name = "DeckList" };
		_deckList.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_deckList.CustomMinimumSize = new Vector2(220, 300);
		_deckPopup.AddChild(_deckList);
		AddChild(_deckPopup);

		_gravePopup = new PopupPanel { Name = "GravePopup" };
		_graveList = new ItemList { Name = "GraveList" };
		_graveList.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_graveList.CustomMinimumSize = new Vector2(220, 300);
		_gravePopup.AddChild(_graveList);
		AddChild(_gravePopup);
	}

	// ════════════════════════════════════════════════════════════════════
	// Public API — called by CombatManager
	// ════════════════════════════════════════════════════════════════════

	// ── Phase / hint ─────────────────────────────────────────────────────

	public void SetPhaseText(string text)
	{
		if (_phaseLabel != null)
			_phaseLabel.Text = text.ToUpper();
	}

	public void SetHintText(string text)
	{
		if (_hintLabel != null)
			_hintLabel.Text = text;
	}

	// ── Deployment mode ──────────────────────────────────────────────────

	public void SetDeploymentMode(bool isDeployment)
	{
		_pendingDeploymentMode = isDeployment;
		_deploymentModePending = true;
		if (_endTurnButton != null)
			ApplyDeploymentMode();
	}

	private void ApplyDeploymentMode()
	{
		_endTurnButton.Text = _pendingDeploymentMode
			? "Confirm Deployment"
			: "End Turn";
		_deploymentModePending = false;
	}

	// ── Selected unit panel ──────────────────────────────────────────────

	public void ShowSelectedUnit(Unit unit, int mana)
	{
		if (_unitNameLabel == null)
		{
			_pendingUnit = unit;
			_pendingMana = mana;
			_unitPending = true;
			return;
		}
		_unitPending = false;
		ApplySelectedUnit(unit, mana);
	}

	private void ApplySelectedUnit(Unit unit, int mana)
	{
		if (_unitNameLabel == null)
			return;

		if (unit == null)
		{
			_unitNameLabel.Text = "—";
			_unitNameLabel.Modulate = UITheme.TextPrimary;
			if (_hpBar != null)
				SetBar(_hpBar, 1, 0, UITheme.StatBarHealth, UITheme.BgDeep);
			if (_mpBar != null)
				_mpBar.Visible = false;
			if (_mpText != null)
				_mpText.Visible = false;
			if (_statLine != null)
				_statLine.Text = "";
			if (_stanceLine != null)
				_stanceLine.Visible = false;
			ClearStatusIcons();
			return;
		}

		bool isEnemy = !unit.IsPlayerControlled;

		_unitNameLabel.Text = isEnemy ? $"[Enemy]  {unit.Name}" : unit.Name;
		_unitNameLabel.Modulate = isEnemy ? UITheme.Danger : UITheme.TextPrimary;

		float hpPct = unit.Stats.MaxHealth <= 0 ? 0f
			: Mathf.Clamp((float)unit.Stats.Health / unit.Stats.MaxHealth, 0f, 1f);
		Color hpCol = hpPct > 0.5f
			? UITheme.Success.Lerp(UITheme.Warning, (1f - hpPct) * 2f)
			: UITheme.Warning.Lerp(UITheme.Danger, (0.5f - hpPct) * 2f);
		SetBar(_hpBar, unit.Stats.MaxHealth, unit.Stats.Health, hpCol, UITheme.BgDeep);
		if (_hpText != null)
			_hpText.Text = $"{unit.Stats.Health}/{unit.Stats.MaxHealth}";

		bool hasMana = unit.Stats.MaxMana > 0;
		if (_mpBar != null)
			_mpBar.Visible = hasMana;
		if (_mpText != null)
			_mpText.Visible = hasMana;
		if (hasMana)
		{
			SetBar(_mpBar, unit.Stats.MaxMana, mana, UITheme.ArcaneBlue, UITheme.BgDeep);
			if (_mpText != null)
				_mpText.Text = $"{mana}/{unit.Stats.MaxMana}";
		}

		if (_statLine != null)
		{
			string apPips = "";
			if (!isEnemy)
				for (int i = 0; i < unit.MaxActionPoints; i++)
					apPips += i < unit.CurrentActionPoints ? "●" : "○";

			string armor = unit.Stats.Armor > 0 ? $"ARM {unit.Stats.Armor}  " : "";
			string shield = unit.Stats.Shield > 0 ? $"SHD {unit.Stats.Shield}  " : "";
			string ap = !isEnemy && unit.MaxActionPoints > 0 ? $"AP {apPips}  " : "";
			string spd = $"SPD {unit.Stats.BaseSpeed}";
			_statLine.Text = $"{armor}{shield}{ap}{spd}";
		}

		if (_stanceLine != null)
		{
			if (!isEnemy && unit.IsMartial && unit.ActiveStance != null)
			{
				_stanceLine.Text = $"[{unit.ActiveStance.DisplayName}]";
				_stanceLine.Visible = true;
			}
			else
			{
				_stanceLine.Visible = false;
			}
		}

		RefreshStatusIcons(unit.Stats.StatusEffects);
	}

	// ── Status icons ─────────────────────────────────────────────────────

	private void RefreshStatusIcons(Dictionary<string, int> statuses)
	{
		ClearStatusIcons();
		if (statuses == null || statuses.Count == 0)
			return;

		foreach (var kvp in statuses)
		{
			if (kvp.Value <= 0)
				continue;
			if (!StatusDisplay.TryGetValue(kvp.Key, out var d))
				continue;

			var lbl = new Label { Name = $"SI_{kvp.Key}", Text = d.symbol, Modulate = d.color };
			lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSizeNormal);
			lbl.TooltipText = kvp.Key;
			_statusIconRow.AddChild(lbl);
		}
	}

	private void ClearStatusIcons()
	{
		if (_statusIconRow == null)
			return;
		foreach (Node child in _statusIconRow.GetChildren())
			child.QueueFree();
	}

	// ── Action log ───────────────────────────────────────────────────────

	public void AppendActionLog(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return;
		_logQueue.Enqueue(message);
		while (_logQueue.Count > LogLineCount)
			_logQueue.Dequeue();
		RedrawLog();
	}

	public void ClearActionLog()
	{
		_logQueue.Clear();
		if (_logLines == null)
			return;
		foreach (var lbl in _logLines)
			if (lbl != null)
				lbl.Text = "";
	}

	private void RedrawLog()
	{
		if (_logLines == null || _logLines.Length == 0)
			return;
		var lines = new List<string>(_logQueue);
		int padCount = _logLines.Length - lines.Count;

		for (int i = 0; i < _logLines.Length; i++)
		{
			int lineIndex = i - padCount;
			if (_logLines[i] == null)
				continue;

			if (lineIndex < 0)
			{
				_logLines[i].Text = "";
				_logLines[i].Modulate = UITheme.TextDim;
			}
			else
			{
				_logLines[i].Text = lines[lineIndex];
				float age = _logLines.Length <= 1 ? 0f
					: (float)(_logLines.Length - 1 - lineIndex) / (_logLines.Length - 1);
				_logLines[i].Modulate = UITheme.TextDim.Lerp(UITheme.TextPrimary, 1f - age * 0.8f);
			}
		}
	}

	// ── Enemy roster ─────────────────────────────────────────────────────

	public void RefreshEnemyRoster(List<Unit> enemies)
	{
		if (_enemyRosterBox == null)
			return;

		foreach (Node child in _enemyRosterBox.GetChildren())
			child.QueueFree();

		for (int i = 0; i < enemies.Count; i++)
		{
			var enemy = enemies[i];
			if (enemy == null)
				continue;

			var row = new HBoxContainer { Name = $"Enemy_{i}" };
			row.AddThemeConstantOverride("separation", 5);

			var btn = new Button
			{
				Text = enemy.Stats.IsAlive ? enemy.Name : $"✕ {enemy.Name}",
				Disabled = !enemy.Stats.IsAlive,
				CustomMinimumSize = new Vector2(80, 0),
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			};
			btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
			if (!enemy.Stats.IsAlive)
				btn.Modulate = UITheme.TextDim;

			int capturedIndex = i;
			btn.Pressed += () => EmitSignal(SignalName.EnemyButtonPressed, capturedIndex);
			row.AddChild(btn);

			if (enemy.Stats.IsAlive)
			{
				float pct = (float)enemy.Stats.Health / enemy.Stats.MaxHealth;
				Color barCol = pct > 0.5f
					? UITheme.Success.Lerp(UITheme.Warning, (1f - pct) * 2f)
					: UITheme.Warning.Lerp(UITheme.Danger, (0.5f - pct) * 2f);

				var bar = new ProgressBar
				{
					MaxValue = Mathf.Max(1, enemy.Stats.MaxHealth),
					Value = enemy.Stats.Health,
					ShowPercentage = false,
					CustomMinimumSize = new Vector2(EnemyBarWidth, BarHeight),
					SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
					SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
				};
				bar.AddThemeStyleboxOverride("fill", MakeFillStyle(barCol));
				bar.AddThemeStyleboxOverride("background", MakeFillStyle(UITheme.BgDeep));
				row.AddChild(bar);

				var lbl = MakeLabel(
					$"{enemy.Stats.Health}/{enemy.Stats.MaxHealth}",
					UITheme.FontSizeSmall, UITheme.TextSecondary);
				lbl.CustomMinimumSize = new Vector2(44, 0);
				lbl.HorizontalAlignment = HorizontalAlignment.Right;
				row.AddChild(lbl);
			}

			_enemyRosterBox.AddChild(row);
		}
	}

	public void ShowEnemyIntel(List<EnemyIntelEntry> entries)
	{
		if (_enemyRosterBox == null)
		{
			// UI not built yet — cache and apply once built
			_pendingIntel = entries;
			return;
		}
		_pendingIntel = null;
		BuildEnemyIntelRows(entries);
	}

	private void BuildEnemyIntelRows(List<EnemyIntelEntry> entries)
	{
		foreach (Node child in _enemyRosterBox.GetChildren())
			child.QueueFree();

		var header = MakeLabel("─ ENEMY INTEL ─", UITheme.FontSizeSmall, UITheme.Gold);
		header.HorizontalAlignment = HorizontalAlignment.Center;
		_enemyRosterBox.AddChild(header);

		foreach (var entry in entries)
		{
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 5);

			var swatch = new ColorRect
			{
				Color = entry.BodyColor,
				CustomMinimumSize = new Vector2(8, 18),
			};
			row.AddChild(swatch);

			string txt = $"{entry.ThreatLabel}  HP:{entry.MaxHealth}  SPD:{entry.BaseSpeed}";
			if (entry.Armor > 0)
				txt += $"  ARM:{entry.Armor}";
			var lbl = MakeLabel(txt, UITheme.FontSizeSmall, UITheme.TextSecondary);
			lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			row.AddChild(lbl);

			_enemyRosterBox.AddChild(row);
		}

		var hint = MakeLabel("Formation unknown until deployment ends.",
			UITheme.FontSizeSmall, UITheme.TextDim);
		hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_enemyRosterBox.AddChild(hint);
	}

	// ── Player unit bar ──────────────────────────────────────────────────

	public void RefreshPlayerUnitBar(List<Unit> playerUnits, Unit selectedUnit)
	{
		if (_playerUnitBar == null)
			return;

		foreach (Node child in _playerUnitBar.GetChildren())
			child.QueueFree();

		for (int i = 0; i < playerUnits.Count; i++)
		{
			var unit = playerUnits[i];
			if (unit == null)
				continue;

			bool isSelected = unit == selectedUnit;
			bool isAlive = unit.Stats.IsAlive;

			var panel = new PanelContainer { Name = $"UnitPanel_{i}" };
			panel.CustomMinimumSize = new Vector2(UnitButtonWidth, 0);

			var style = new StyleBoxFlat
			{
				BgColor = isSelected ? UITheme.UnitBarSelected : UITheme.BgRaised,
				BorderColor = isSelected ? UITheme.UnitBarBorder : UITheme.Neutral,
			};
			style.SetBorderWidthAll(isSelected ? 2 : 1);
			style.SetCornerRadiusAll(UITheme.CornerRadius);
			panel.AddThemeStyleboxOverride("panel", style);
			panel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

			var vbox = new VBoxContainer { Name = "VBox" };
			vbox.AddThemeConstantOverride("separation", 2);
			panel.AddChild(vbox);

			// Name
			var nameLbl = MakeLabel(
				isAlive ? unit.DisplayName : $"✕ {unit.DisplayName}",
				UITheme.FontSizeSmall,
				isAlive ? (isSelected ? UITheme.TextPrimary : UITheme.TextSecondary)
						: UITheme.TextDim);
			nameLbl.HorizontalAlignment = HorizontalAlignment.Center;
			nameLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			vbox.AddChild(nameLbl);

			if (isAlive)
			{
				// AP pips
				string pips = "";
				for (int p = 0; p < unit.MaxActionPoints; p++)
					pips += p < unit.CurrentActionPoints ? "●" : "○";

				var pipLbl = MakeLabel(pips, UITheme.FontSizeSmall,
					isSelected ? UITheme.Violet : UITheme.NeutralDim);
				pipLbl.HorizontalAlignment = HorizontalAlignment.Center;
				vbox.AddChild(pipLbl);

				// HP text
				var hpLbl = MakeLabel(
					$"{unit.Stats.Health}/{unit.Stats.MaxHealth}",
					UITheme.FontSizeSmall - 1, UITheme.TextSecondary);
				hpLbl.HorizontalAlignment = HorizontalAlignment.Center;
				vbox.AddChild(hpLbl);

				// HP strip
				float pct = (float)unit.Stats.Health / unit.Stats.MaxHealth;
				Color hpCol = pct > 0.5f
					? UITheme.Success.Lerp(UITheme.Warning, (1f - pct) * 2f)
					: UITheme.Warning.Lerp(UITheme.Danger, (0.5f - pct) * 2f);

				var hpStrip = new ProgressBar
				{
					MaxValue = Mathf.Max(1, unit.Stats.MaxHealth),
					Value = unit.Stats.Health,
					ShowPercentage = false,
					CustomMinimumSize = new Vector2(0, 4),
					SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				};
				hpStrip.AddThemeStyleboxOverride("fill", MakeFillStyle(hpCol));
				hpStrip.AddThemeStyleboxOverride("background", MakeFillStyle(UITheme.BgDeep));
				vbox.AddChild(hpStrip);
			}

			// Invisible click catcher
			var clickCatcher = new Button
			{
				Name = "ClickCatcher",
				Flat = true,
				Text = "",
				MouseFilter = Control.MouseFilterEnum.Stop,
			};
			clickCatcher.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			clickCatcher.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
			clickCatcher.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
			clickCatcher.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
			clickCatcher.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
			int capturedIndex = i;
			clickCatcher.Pressed += () => EmitSignal(SignalName.UnitButtonPressed, capturedIndex);
			panel.AddChild(clickCatcher);

			_playerUnitBar.AddChild(panel);
		}
	}

	// ── Deck / grave counts ──────────────────────────────────────────────

	public void RefreshDeckCounts(List<Card> drawPile, List<Card> discardPile)
	{
		if (_deckButton != null)
			_deckButton.Text = $"Deck  {drawPile?.Count ?? 0}";
		if (_graveButton != null)
			_graveButton.Text = $"Grave {discardPile?.Count ?? 0}";

		if (_deckList != null)
		{
			_deckList.Clear();
			if (drawPile != null)
				foreach (var c in drawPile)
					_deckList.AddItem(c.CardName);
		}
		if (_graveList != null)
		{
			_graveList.Clear();
			if (discardPile != null)
				foreach (var c in discardPile)
					_graveList.AddItem(c.CardName);
		}
	}

	// ════════════════════════════════════════════════════════════════════
	// Private helpers
	// ════════════════════════════════════════════════════════════════════

	private void OnDeckButtonPressed() => _deckPopup?.PopupCentered();
	private void OnGraveButtonPressed() => _gravePopup?.PopupCentered();
	public VBoxContainer AttunementSection => _attunementSection;

	private static StyleBoxFlat MakeFillStyle(Color col)
	{
		var s = new StyleBoxFlat { BgColor = col };
		s.SetCornerRadiusAll(2);
		return s;
	}

	private static Label MakeLabel(string text, int fontSize, Color color)
	{
		var lbl = new Label { Text = text, Modulate = color };
		lbl.AddThemeFontSizeOverride("font_size", fontSize);
		return lbl;
	}

	private static HSeparator MakeDivider(Color? col = null)
	{
		var sep = new HSeparator { Name = "Divider" };
		sep.AddThemeColorOverride("separator_color",
			col ?? new Color(UITheme.Neutral.R, UITheme.Neutral.G, UITheme.Neutral.B, 0.5f));
		return sep;
	}

	private static Button MakeSmallButton(string text)
	{
		var btn = new Button { Text = text };
		btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
		UITheme.ApplyButtonStyle(btn, isPrimary: false);
		return btn;
	}

	private static void StyleEndTurnButton(Button btn)
	{
		var style = new StyleBoxFlat { BgColor = UITheme.Success };
		style.SetBorderWidthAll(1);
		style.SetCornerRadiusAll(UITheme.CornerRadius);
		style.BorderColor = UITheme.SuccessDim;
		btn.AddThemeStyleboxOverride("normal", style);
		btn.AddThemeColorOverride("font_color", UITheme.TextPrimary);
		btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeNormal);
	}

	private static HBoxContainer MakeBarRow(
		string labelText, int barHeight,
		out ProgressBar bar, out Label valueText,
		Color fillCol, Color backCol)
	{
		var row = new HBoxContainer { Name = $"{labelText}Row" };
		row.AddThemeConstantOverride("separation", 5);

		var prefix = MakeLabel(labelText, UITheme.FontSizeSmall, UITheme.TextSecondary);
		prefix.CustomMinimumSize = new Vector2(18, 0);
		row.AddChild(prefix);

		bar = new ProgressBar
		{
			ShowPercentage = false,
			CustomMinimumSize = new Vector2(0, barHeight),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
		};
		bar.AddThemeStyleboxOverride("fill", MakeFillStyle(fillCol));
		bar.AddThemeStyleboxOverride("background", MakeFillStyle(backCol));
		row.AddChild(bar);

		valueText = MakeLabel("", UITheme.FontSizeSmall, UITheme.TextSecondary);
		valueText.CustomMinimumSize = new Vector2(48, 0);
		valueText.HorizontalAlignment = HorizontalAlignment.Right;
		row.AddChild(valueText);

		return row;
	}

	private static void SetBar(ProgressBar bar, int max, int current,
		Color fillCol, Color backCol)
	{
		if (bar == null)
			return;
		bar.MaxValue = Mathf.Max(1, max);
		bar.Value = Mathf.Clamp(current, 0, max);
		bar.AddThemeStyleboxOverride("fill", MakeFillStyle(fillCol));
		bar.AddThemeStyleboxOverride("background", MakeFillStyle(backCol));
	}
}
