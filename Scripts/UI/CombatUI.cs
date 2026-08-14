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
	/// <summary>U3: the player surrenders priority during an enemy-trigger window.</summary>
	[Signal] public delegate void PriorityPassPressedEventHandler();
	/// <summary>§7c: explicit Respond affordance — pulls the responder's hand up.</summary>
	[Signal] public delegate void PriorityRespondPressedEventHandler();
	/// <summary>(2026-07-29) Stance switcher: the player clicked a stance button
	/// for the selected martial unit. CombatManager routes to TrySwitchStance.</summary>
	[Signal] public delegate void StanceSwitchRequestedEventHandler(string stanceId);

	// ── Layout constants (design-space px — V1 resolution ruling) ────────
	private const int LeftPanelWidth = 280;
	private const int RightPanelWidth = 220;
	private const int PanelPadding = 10;
	private const int BarHeight = 10;
	private const int ManaBarHeight = 8;
	private const int LogLineCount = 3;          // V1: 3-line ticker (was 6-line panel section)
	private const int LogHistoryCap = 200;       // full-history popup buffer
	private const int UnitButtonWidth = 110;
	private const int EnemyBarWidth = 90;
	private const int BottomLeftWidth = 360;     // party chips + ticker block (chips may overflow right — they sit above the deck button's row, so nothing collides until 5+ chips)
	private const int EndTurnWidth = 180;
	private const int FlankButtonWidth = 96;     // deck/grave beside the fan

	// ── Left panel nodes ─────────────────────────────────────────────────
	private PanelContainer _leftPanel;
	private Label _phaseLabel;
	/// <summary>O-track: the mission line ("Survive - round 3 / 8"). Hidden
	/// entirely on an ordinary kill-fight, which is every fight authored
	/// before the objectives substrate.</summary>
	private Label _objectiveLabel;
	private Label _unitNameLabel;
	private ProgressBar _hpBar;
	private Label _hpText;
	private ProgressBar _mpBar;
	private Label _mpText;
	private Label _statLine;
	private Label _stanceLine;
	private HBoxContainer _stanceRow;   // 2026-07-29: clickable stance switcher
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
	private VBoxContainer _inspectBlock;   // V2: enemy behavior/ability/faction blocks

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
	// V1: full history behind the ticker (click to open), capped.
	private readonly List<string> _logHistory = new List<string>();
	private PopupPanel _logPopup;
	private ItemList _logHistoryList;

	// ── Pending state for calls that arrive before BuildUI fires ─────────
	private List<EnemyIntelEntry> _pendingIntel = null;

	// ── Build / pending state ─────────────────────────────────────────────
	private bool _built = false;
	private bool _pendingDeploymentMode = false;
	private bool _deploymentModePending = false;

	/// <summary>True once BuildUI has run. CombatManager's skip-deploy handoff
	/// waits on this before pushing selection/roster state (2026-07-09) — calls
	/// that land before the build either drop silently or hit empty panels.</summary>
	public bool IsBuilt => _built;

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

	/// <summary>V1: Enter / keypad-Enter ends the turn (or confirms deployment —
	/// same morph as the button). The bottom-right corner is a long mouse trip
	/// every turn; the key is the fix. Suppressed while any popup or the U3
	/// priority prompt is open so Enter can never blind-pass a stack window.</summary>
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey { Pressed: true, Echo: false } key)
			return;
		if (key.Keycode != Key.Enter && key.Keycode != Key.KpEnter)
			return;
		if (_endTurnButton == null || !_endTurnButton.Visible || _endTurnButton.Disabled)
			return;
		if (StackWindowInteractive)
			return;
		if ((_deckPopup?.Visible ?? false) || (_gravePopup?.Visible ?? false) || (_logPopup?.Visible ?? false))
			return;

		if (_endTurnButton.Text == "Confirm Deployment")
			EmitSignal(SignalName.ConfirmDeploymentPressed);
		else
			EmitSignal(SignalName.EndTurnPressed);
		GetViewport().SetInputAsHandled();
	}

	private void BuildUI()
	{
		if (_built)
			return;
		_built = true;

		// V1 layout (combat_ui_v2 §5): banner top-center, End Turn bottom-right,
		// log ticker + party chips bottom-left, hint + deck/grave flanking the
		// bottom-center hand, left panel slimmed to unit card + attunement.
		BuildTopBanner();
		BuildLeftPanel();
		BuildRightPanel();
		BuildBottomLeft();
		BuildBottomRight();
		BuildBottomCenter();
		BuildPopups();

		RedrawLog();

		if (_unitPending)
			ApplySelectedUnit(_pendingUnit, _pendingMana);

		if (_pendingIntel != null)
			BuildEnemyIntelRows(_pendingIntel);

		if (_deploymentModePending)
			ApplyDeploymentMode();

		// Roster replay (2026-07-09): RefreshEnemyRoster calls that arrived
		// before the build used to drop silently — the skip-deploy handoff's
		// roster load was lost until the next damage event re-refreshed it.
		if (_lastRosterEnemies != null)
			RefreshEnemyRoster(_lastRosterEnemies);

		// Phase banner / hint replay (2026-07-09): same pre-build drop.
		if (_lastPhaseText != null)
			SetPhaseText(_lastPhaseText);
		if (_lastHintText != null)
			SetHintText(_lastHintText);
		if (_lastObjectiveText != null)
			SetObjectiveText(_lastObjectiveText);
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
			OffsetTop = HudManager.BarHeight, // clear the global top bar
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

		// (V1: phase banner moved to top center — BuildTopBanner.)

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

		// Stance switcher (2026-07-29): one button per trained stance for the
		// selected martial unit — the control TrySwitchStance never had.
		_stanceRow = new HBoxContainer { Name = "StanceRow", Visible = false };
		_stanceRow.AddThemeConstantOverride("separation", 6);
		_stanceRow.Alignment = BoxContainer.AlignmentMode.Center;
		vbox.AddChild(_stanceRow);

		// ── Status icons ─────────────────────────────────────────────
		_statusIconRow = new HBoxContainer { Name = "StatusIcons" };
		_statusIconRow.AddThemeConstantOverride("separation", 4);
		_statusIconRow.Alignment = BoxContainer.AlignmentMode.Center;
		vbox.AddChild(_statusIconRow);

		// ── V2 inspect block (enemies only): behavior line, ability blocks,
		// role/faction line (combat_ui_v2 §7b) ───────────────────────
		_inspectBlock = new VBoxContainer { Name = "InspectBlock", Visible = false };
		_inspectBlock.AddThemeConstantOverride("separation", 4);
		vbox.AddChild(_inspectBlock);

		// Attunement slot — populated by SchoolAttunementUI.UseExternalContainer()
		vbox.AddChild(MakeDivider(UITheme.VioletDim));
		_attunementSection = new VBoxContainer { Name = "AttunementSection" };
		_attunementSection.AddThemeConstantOverride("separation", 4);
		_attunementSection.Visible = false;   // hidden until a school with an attunement is selected
		GD.Print($"[CombatUI] AttunementSection built: {_attunementSection != null}");
		vbox.AddChild(_attunementSection);

		// (V1: log, party chips, deck/grave, and End Turn all moved out — the
		// left column stops being a junk drawer. §5: log+party → bottom left,
		// deck/grave → flanking the hand, End Turn → bottom right.)
	}

	// ════════════════════════════════════════════════════════════════════
	// V1: top-center banner (phase line; V4 adds the context strip below)
	// ════════════════════════════════════════════════════════════════════

	private void BuildTopBanner()
	{
		var banner = new PanelContainer
		{
			Name = "TopBanner",
			AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
			OffsetLeft = -260, OffsetRight = 260,
			OffsetTop = HudManager.BarHeight + 6,
			GrowHorizontal = Control.GrowDirection.Both,
			GrowVertical = Control.GrowDirection.End,
		};
		banner.AddThemeStyleboxOverride("panel",
			UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Violet));
		AddChild(banner);

		var margin = new MarginContainer { Name = "Margin" };
		margin.AddThemeConstantOverride("margin_left", PanelPadding);
		margin.AddThemeConstantOverride("margin_right", PanelPadding);
		margin.AddThemeConstantOverride("margin_top", 4);
		margin.AddThemeConstantOverride("margin_bottom", 4);
		banner.AddChild(margin);

		var vbox = new VBoxContainer { Name = "VBox" };
		vbox.AddThemeConstantOverride("separation", 2);
		margin.AddChild(vbox);

		_phaseLabel = MakeLabel("", UITheme.FontSizeNormal, UITheme.Violet);
		_phaseLabel.HorizontalAlignment = HorizontalAlignment.Center;
		vbox.AddChild(_phaseLabel);

		// O-track objective line. Sits ABOVE the hint deliberately: the hint is
		// chrome ("select a unit, move, cast"), the objective is what the fight
		// is for. Gold, because the only other gold thing on screen is the
		// expedition objective marker and they mean the same kind of thing.
		_objectiveLabel = MakeLabel("", UITheme.FontSizeSmall, UITheme.Gold);
		_objectiveLabel.Name = "ObjectiveLabel";
		_objectiveLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_objectiveLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_objectiveLabel.Visible = false;
		vbox.AddChild(_objectiveLabel);

		// Hint line rides the banner (V1 fix: its first home above the fan sat
		// exactly on the card tops — unreadable and mid-hand). V4's context
		// strip takes this slot later; the hint then moves or dies.
		_hintLabel = MakeLabel("", UITheme.FontSizeSmall, UITheme.TextDim);
		_hintLabel.Name = "HintLabel";
		_hintLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_hintLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		vbox.AddChild(_hintLabel);
	}

	// ════════════════════════════════════════════════════════════════════
	// V1: bottom-left — party chips above a 3-line log ticker (click = full
	// history popup)
	// ════════════════════════════════════════════════════════════════════

	private void BuildBottomLeft()
	{
		var block = new VBoxContainer
		{
			Name = "BottomLeft",
			AnchorLeft = 0f, AnchorRight = 0f, AnchorTop = 1f, AnchorBottom = 1f,
			OffsetLeft = 12, OffsetRight = 12 + BottomLeftWidth,
			OffsetTop = -12, OffsetBottom = -12,
			GrowHorizontal = Control.GrowDirection.End,
			GrowVertical = Control.GrowDirection.Begin,
		};
		block.AddThemeConstantOverride("separation", 6);
		AddChild(block);

		// Party chips — slim row above the ticker (§5: ambient awareness).
		_playerUnitBar = new HBoxContainer { Name = "UnitBar" };
		_playerUnitBar.AddThemeConstantOverride("separation", 4);
		block.AddChild(_playerUnitBar);

		// Log ticker — 3 lines, panel-backed, clickable for full history.
		var logPanel = new PanelContainer { Name = "LogTicker" };
		logPanel.AddThemeStyleboxOverride("panel",
			UITheme.MakePanelStyle(UITheme.BgBase, UITheme.VioletDim));
		block.AddChild(logPanel);

		var logMargin = new MarginContainer { Name = "Margin" };
		logMargin.AddThemeConstantOverride("margin_left", 8);
		logMargin.AddThemeConstantOverride("margin_right", 8);
		logMargin.AddThemeConstantOverride("margin_top", 4);
		logMargin.AddThemeConstantOverride("margin_bottom", 4);
		logPanel.AddChild(logMargin);

		_logBox = new VBoxContainer { Name = "LogBox" };
		_logBox.AddThemeConstantOverride("separation", 2);
		logMargin.AddChild(_logBox);

		_logLines = new Label[LogLineCount];
		for (int i = 0; i < LogLineCount; i++)
		{
			var lbl = MakeLabel("", UITheme.FontSizeSmall,
				i == LogLineCount - 1 ? UITheme.TextPrimary : UITheme.TextDim);
			lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			lbl.CustomMinimumSize = new Vector2(BottomLeftWidth - 20, 0);
			_logLines[i] = lbl;
			_logBox.AddChild(lbl);
		}

		// Click catcher — the whole ticker opens the scrollable history.
		var clickCatcher = new Button
		{
			Name = "LogClickCatcher",
			Flat = true,
			Text = "",
			TooltipText = "Click for full combat log",
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		clickCatcher.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		clickCatcher.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
		clickCatcher.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
		clickCatcher.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
		clickCatcher.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
		clickCatcher.Pressed += OnLogTickerPressed;
		logPanel.AddChild(clickCatcher);
	}

	// ════════════════════════════════════════════════════════════════════
	// V1: bottom-right — End Turn / Confirm Deployment, standard tactics
	// corner position
	// ════════════════════════════════════════════════════════════════════

	private void BuildBottomRight()
	{
		var block = new VBoxContainer
		{
			Name = "BottomRight",
			AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 1f, AnchorBottom = 1f,
			OffsetLeft = -12 - EndTurnWidth, OffsetRight = -12,
			OffsetTop = -12, OffsetBottom = -12,
			GrowHorizontal = Control.GrowDirection.Begin,
			GrowVertical = Control.GrowDirection.Begin,
		};
		block.AddThemeConstantOverride("separation", 6);
		AddChild(block);

		// Confirm Deployment (hidden by default; End Turn also morphs as today)
		_confirmDeploymentButton = new Button
		{
			Name = "ConfirmDeployBtn",
			Text = "Confirm Deployment",
			Visible = false,
			CustomMinimumSize = new Vector2(EndTurnWidth, 40),
		};
		UITheme.ApplyButtonStyle(_confirmDeploymentButton, isPrimary: true);
		_confirmDeploymentButton.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
		_confirmDeploymentButton.Pressed += () => EmitSignal(SignalName.ConfirmDeploymentPressed);
		block.AddChild(_confirmDeploymentButton);

		_endTurnButton = new Button
		{
			Name = "EndTurnButton",
			Text = "End Turn",
			TooltipText = "Enter",
			CustomMinimumSize = new Vector2(EndTurnWidth, 48),
		};
		StyleEndTurnButton(_endTurnButton);
		_endTurnButton.Pressed += () =>
		{
			if (_endTurnButton.Text == "Confirm Deployment")
				EmitSignal(SignalName.ConfirmDeploymentPressed);
			else
				EmitSignal(SignalName.EndTurnPressed);
		};
		block.AddChild(_endTurnButton);

		// §7c: always-reachable stop toggles. Without these, a stop could only
		// be set from the stack panel — which never opens unless a stop is
		// already set or a Reflex is in hand (chicken-and-egg). Anchored above
		// the End Turn stack, wider than EndTurnWidth so the row fits; hidden
		// while the stack panel (which carries its own mirrored set) is up.
		_stopsBar = new HBoxContainer
		{
			Name = "StackStopsBar",
			AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 1f, AnchorBottom = 1f,
			OffsetLeft = -12 - 300, OffsetRight = -12,
			OffsetTop = -12 - 100 - 26, OffsetBottom = -12 - 100,
			GrowHorizontal = Control.GrowDirection.Begin,
			GrowVertical = Control.GrowDirection.Begin,
			Alignment = BoxContainer.AlignmentMode.End,
		};
		_stopsBar.AddThemeConstantOverride("separation", 6);
		AddChild(_stopsBar);
		AddStopToggleSet(_stopsBar);
	}

	// ════════════════════════════════════════════════════════════════════
	// V1: bottom-center — hint line above the fan, deck/grave flanking it
	// ════════════════════════════════════════════════════════════════════

	private void BuildBottomCenter()
	{
		// (V1 fix: the hint line moved into the top banner — above the fan it
		// sat on the card tops.)

		// Deck / Grave counters flank the fan left/right (§5) — placed just
		// INSIDE the hand reserves so they never collide with cards, the
		// bottom-left block, or the End Turn corner (see UITheme tiling note).
		_deckButton = MakeSmallButton("Deck —");
		_deckButton.Name = "DeckButton";
		_deckButton.AnchorLeft = 0f; _deckButton.AnchorRight = 0f;
		_deckButton.AnchorTop = 1f; _deckButton.AnchorBottom = 1f;
		_deckButton.OffsetLeft = UITheme.HandReserveLeft - 8 - FlankButtonWidth;
		_deckButton.OffsetRight = UITheme.HandReserveLeft - 8;
		_deckButton.OffsetTop = -52; _deckButton.OffsetBottom = -14;
		_deckButton.GrowVertical = Control.GrowDirection.Begin;
		_deckButton.Pressed += OnDeckButtonPressed;
		AddChild(_deckButton);

		_graveButton = MakeSmallButton("Grave —");
		_graveButton.Name = "GraveButton";
		_graveButton.AnchorLeft = 1f; _graveButton.AnchorRight = 1f;
		_graveButton.AnchorTop = 1f; _graveButton.AnchorBottom = 1f;
		_graveButton.OffsetLeft = -(UITheme.HandReserveRight - 8);
		_graveButton.OffsetRight = -(UITheme.HandReserveRight - 8 - FlankButtonWidth);
		_graveButton.OffsetTop = -52; _graveButton.OffsetBottom = -14;
		_graveButton.GrowHorizontal = Control.GrowDirection.Begin;
		_graveButton.GrowVertical = Control.GrowDirection.Begin;
		_graveButton.Pressed += OnGraveButtonPressed;
		AddChild(_graveButton);
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
			OffsetTop = HudManager.BarHeight, // clear the global top bar
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

		// V1: full combat-log history (ticker click).
		_logPopup = new PopupPanel { Name = "LogPopup" };
		_logHistoryList = new ItemList { Name = "LogHistoryList" };
		_logHistoryList.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_logHistoryList.CustomMinimumSize = new Vector2(520, 420);
		_logPopup.AddChild(_logHistoryList);
		AddChild(_logPopup);
	}

	// ════════════════════════════════════════════════════════════════════
	// Public API — called by CombatManager
	// ════════════════════════════════════════════════════════════════════

	// ── Phase / hint ─────────────────────────────────────────────────────

	// Pending-replay (2026-07-09): phase/hint pushed before BuildUI used to
	// drop silently — skip-deploy launches showed a blank top banner all of
	// round 1 (RefreshPhaseUI fires from the handoff, pre-build; the next
	// re-push only comes at the round-2 phase change).
	private string _lastPhaseText;
	private string _lastHintText;
	private string _lastObjectiveText;

	public void SetPhaseText(string text)
	{
		_lastPhaseText = text;
		if (_phaseLabel != null)
			_phaseLabel.Text = text.ToUpper();
	}

	/// <summary>O-track: set the mission line. Empty string hides it. Carries
	/// the same pending-replay guard as the phase and hint lines - anything
	/// pushed before the deferred BuildUI would otherwise no-op silently,
	/// and the objective is pushed from QueueEncounterFromContext, which
	/// runs well before the UI exists.</summary>
	public void SetObjectiveText(string text)
	{
		_lastObjectiveText = text ?? "";
		if (_objectiveLabel == null)
			return;
		_objectiveLabel.Text = _lastObjectiveText;
		_objectiveLabel.Visible = _lastObjectiveText.Length > 0;
	}

	public void SetHintText(string text)
	{
		_lastHintText = text;
		if (_hintLabel != null)
			_hintLabel.Text = text;
	}

	// ── V3: the stack panel (§7c) — replaces the U3 interim prompt ─────────
	// Compact vertical strip, center-right above the hand: pending stack
	// objects top-down (source · name · one-line effect), resolving object
	// highlighted. Interactive only while the player holds priority; during
	// auto-pass it plays through with ZERO input.

	private PanelContainer _stackPanel;
	private VBoxContainer _stackList;
	private Button _stackPassBtn;
	private Button _stackRespondBtn;   // §7c: explicit Respond affordance
	private HBoxContainer _stopsBar;   // §7c: always-visible stop toggles (main HUD)

	private void EnsureStackPanel()
	{
		if (_stackPanel != null)
			return;

		_stackPanel = new PanelContainer
		{
			Name = "StackPanel",
			AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 1f, AnchorBottom = 1f,
			OffsetLeft = -12 - 320, OffsetRight = -12,
			// §7c: taller than the v1 strip — stop-toggle row + Respond/Pass row.
			OffsetTop = -12 - 430, OffsetBottom = -12 - 80,
			GrowHorizontal = Control.GrowDirection.Begin,
			GrowVertical = Control.GrowDirection.Begin,
			Visible = false,
		};
		_stackPanel.AddThemeStyleboxOverride("panel",
			UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
		AddChild(_stackPanel);

		var margin = new MarginContainer();
		margin.AddThemeConstantOverride("margin_left", 8);
		margin.AddThemeConstantOverride("margin_right", 8);
		margin.AddThemeConstantOverride("margin_top", 6);
		margin.AddThemeConstantOverride("margin_bottom", 6);
		_stackPanel.AddChild(margin);

		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("separation", 4);
		margin.AddChild(vbox);

		var header = MakeLabel("─ THE STACK ─", UITheme.FontSizeSmall, UITheme.Gold);
		header.HorizontalAlignment = HorizontalAlignment.Center;
		vbox.AddChild(header);

		// §7c stops: per-trigger-type toggles in the strip header — the
		// digital-card-game full-control pattern. Persist via PlayerSession.
		var stopRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		stopRow.AddThemeConstantOverride("separation", 6);
		vbox.AddChild(stopRow);

		AddStopToggleSet(stopRow);

		_stackList = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		_stackList.AddThemeConstantOverride("separation", 3);
		vbox.AddChild(_stackList);

		// §7c: Respond + Pass. Respond is the explicit affordance — enabled only
		// when a castable Reflex is actually in a hand; it pulls that unit's
		// hand up (casting stays drag-to-cast). Pass surrenders priority.
		var btnRow = new HBoxContainer();
		btnRow.AddThemeConstantOverride("separation", 6);
		vbox.AddChild(btnRow);

		_stackRespondBtn = new Button
		{
			Text = "Respond",
			CustomMinimumSize = new Vector2(0, 32),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		UITheme.ApplyButtonStyle(_stackRespondBtn, isPrimary: false);
		_stackRespondBtn.Pressed += () => EmitSignal(SignalName.PriorityRespondPressed);
		btnRow.AddChild(_stackRespondBtn);

		_stackPassBtn = new Button
		{
			Text = "Pass",
			CustomMinimumSize = new Vector2(0, 32),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		UITheme.ApplyButtonStyle(_stackPassBtn, isPrimary: true);
		_stackPassBtn.Pressed += () => EmitSignal(SignalName.PriorityPassPressed);
		btnRow.AddChild(_stackPassBtn);
	}

	// §7c stop toggles exist in TWO places — the stack-panel header and the
	// always-visible bottom-right bar (you must be able to arm a stop BEFORE
	// a window exists; without one set and no Reflex in hand, the stack
	// auto-passes and the panel's own toggles are unreachable). All instances
	// write the same PlayerSession flags and stay mirrored via SyncStopToggles.
	private readonly List<(CheckBox cb, System.Func<bool> read)> _stopToggles = new();

	/// <summary>Adds the standard "stop: Strikes/Abilities/Items" toggle set.</summary>
	private void AddStopToggleSet(HBoxContainer parent)
	{
		parent.AddChild(MakeLabel("stop:", UITheme.FontSizeSmall - 1, UITheme.TextDim));
		MakeStopToggle(parent, "Strikes",
			"Always open a window before an enemy strike resolves",
			() => PlayerSession.StopOnStrikes, v => PlayerSession.StopOnStrikes = v);
		MakeStopToggle(parent, "Abilities",
			"Always open a window before an enemy triggered ability resolves",
			() => PlayerSession.StopOnEnemyAbilities, v => PlayerSession.StopOnEnemyAbilities = v);
		MakeStopToggle(parent, "Items",
			"Always open a window before an item proc resolves",
			() => PlayerSession.StopOnItemProcs, v => PlayerSession.StopOnItemProcs = v);
	}

	/// <summary>One §7c stop toggle: small CheckBox writing straight to its
	/// PlayerSession flag so the setting survives across fights this session.</summary>
	private void MakeStopToggle(HBoxContainer parent, string text, string tooltip,
		System.Func<bool> read, System.Action<bool> write)
	{
		var cb = new CheckBox
		{
			Text = text,
			ButtonPressed = read(),
			TooltipText = tooltip,
		};
		cb.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall - 1);
		cb.Toggled += pressed => { write(pressed); SyncStopToggles(); };
		parent.AddChild(cb);
		_stopToggles.Add((cb, read));
	}

	/// <summary>Mirrors every stop CheckBox to its PlayerSession flag without
	/// re-firing Toggled (SetPressedNoSignal), so the two sets never diverge.</summary>
	private void SyncStopToggles()
	{
		foreach (var (cb, read) in _stopToggles)
			if (IsInstanceValid(cb))
				cb.SetPressedNoSignal(read());
	}

	/// <summary>Renders the strip from top-of-stack down. <paramref name="items"/>
	/// = (source, name, effect); index 0 is the top (resolving next, highlighted).
	/// <paramref name="interactive"/> shows the Respond/Pass row (player holds
	/// priority); otherwise the strip is display-only and plays through with zero
	/// input. <paramref name="canRespond"/> enables Respond — false greys it out
	/// (window opened by a stop, no castable Reflex in hand).</summary>
	public void ShowStackStrip(List<(string source, string name, string effect)> items, bool interactive,
		bool canRespond = false)
	{
		EnsureStackPanel();

		foreach (Node child in _stackList.GetChildren())
			child.QueueFree();

		for (int i = 0; i < items.Count; i++)
		{
			var (source, name, effect) = items[i];
			bool top = i == 0;

			var line1 = MakeLabel($"{name} — {source}", UITheme.FontSizeSmall,
				top ? UITheme.Gold : UITheme.TextSecondary);
			_stackList.AddChild(line1);

			if (!string.IsNullOrEmpty(effect))
			{
				var line2 = MakeLabel(effect, UITheme.FontSizeSmall - 1,
					top ? UITheme.TextPrimary : UITheme.TextDim);
				line2.AutowrapMode = TextServer.AutowrapMode.WordSmart;
				_stackList.AddChild(line2);
			}

			if (i < items.Count - 1)
				_stackList.AddChild(MakeDivider(UITheme.VioletDim));
		}

		_stackPassBtn.Visible = interactive;
		_stackRespondBtn.Visible = interactive;
		_stackRespondBtn.Disabled = !canRespond;
		_stackRespondBtn.TooltipText = canRespond
			? "Bring up the responder's hand"
			: "No castable Reflex-speed card in hand";
		_stackPanel.Visible = items.Count > 0;

		// The panel carries its own stop toggles and overlaps the main-HUD bar —
		// keep exactly one set on screen. SyncStopToggles so the panel's set
		// reflects flags flipped on the bar since the panel last showed.
		SyncStopToggles();
		if (_stopsBar != null)
			_stopsBar.Visible = !_stackPanel.Visible;
	}

	public void HideStackStrip()
	{
		if (_stackPanel != null)
			_stackPanel.Visible = false;
		if (_stopsBar != null)
			_stopsBar.Visible = true;
	}

	/// <summary>True while the strip is interactive — the Enter-to-end-turn guard
	/// reads this so Enter can never blind-pass a stack window.</summary>
	public bool StackWindowInteractive =>
		_stackPanel != null && _stackPanel.Visible && _stackPassBtn.Visible;

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
				SetHpBarWithered(_hpBar, 0, 1, 0, UITheme.StatBarHealth, UITheme.BgDeep);
			if (_mpBar != null)
				_mpBar.Visible = false;
			if (_mpText != null)
				_mpText.Visible = false;
			if (_statLine != null)
				_statLine.Text = "";
			if (_stanceLine != null)
				_stanceLine.Visible = false;
			if (_stanceRow != null)
				_stanceRow.Visible = false;
			ClearStatusIcons();
			RefreshInspectBlock(null, false);   // V2: hide enemy blocks
			return;
		}

		bool isEnemy = !unit.IsPlayerControlled;

		_unitNameLabel.Text = isEnemy ? $"[Enemy]  {unit.Name}" : unit.Name;
		_unitNameLabel.Modulate = isEnemy ? UITheme.Danger : UITheme.TextPrimary;

		int hpWither = Mathf.Max(0, unit.Stats.WitheredMaxHp);
		int hpOrigMax = Mathf.Max(1, unit.Stats.MaxHealth + hpWither);
		float hpPct = Mathf.Clamp((float)unit.Stats.Health / hpOrigMax, 0f, 1f);
		Color hpCol = hpPct > 0.5f
			? UITheme.Success.Lerp(UITheme.Warning, (1f - hpPct) * 2f)
			: UITheme.Warning.Lerp(UITheme.Danger, (0.5f - hpPct) * 2f);
		SetHpBarWithered(_hpBar, unit.Stats.Health, unit.Stats.MaxHealth, hpWither, hpCol, UITheme.BgDeep);
		if (_hpText != null)
			_hpText.Text = hpWither > 0
				? $"{unit.Stats.Health}/{unit.Stats.MaxHealth} (−{hpWither})"
				: $"{unit.Stats.Health}/{unit.Stats.MaxHealth}";

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
			// SPD = the real per-move reach (MoveRange + movespeed grants, adjusted for
			// rooted/slowed) — the same value every movement path uses. Shows base→eff
			// when buffed/debuffed so Dash/Imbue/stance changes are visible.
			int spdEff = unit.EffectiveMoveRange;
			string spd = spdEff != unit.MoveRange
				? $"SPD {unit.MoveRange}→{spdEff}"
				: $"SPD {unit.MoveRange}";
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

		RefreshStanceRow(unit, isEnemy);

		RefreshStatusIcons(unit.Stats.StatusEffects);
		RefreshInspectBlock(unit, isEnemy);
	}

	/// <summary>Rebuilds the stance-switch buttons for the selected unit.
	/// (2026-07-29) The stance system was fully implemented end to end —
	/// registry, per-unit trained lists, TrySwitchStance with its 1-AP
	/// once-per-turn cost, passive/attack hooks — but no control ever CALLED
	/// TrySwitchStance, so martials were locked into their first stance for
	/// life. This row is that control: one button per trained stance; the
	/// active one is highlighted and disabled; the rest emit
	/// StanceSwitchRequested, which CombatManager routes to TrySwitchStance.
	/// Hidden for enemies, non-martials, and single-stance units.</summary>
	private void RefreshStanceRow(Unit unit, bool isEnemy)
	{
		if (_stanceRow == null)
			return;
		foreach (Node child in _stanceRow.GetChildren())
			child.QueueFree();

		bool show = !isEnemy && unit != null && unit.IsMartial &&
		            unit.AvailableStances != null && unit.AvailableStances.Count > 1;
		_stanceRow.Visible = show;
		if (!show)
			return;

		foreach (var stance in unit.AvailableStances)
		{
			bool isActive = stance == unit.ActiveStance;
			var btn = new Button
			{
				Text = stance.DisplayName,
				Disabled = isActive || unit.HasSwitchedStanceThisTurn ||
				           unit.CurrentActionPoints < MartialAPCosts.SwitchStance,
				TooltipText = stance.Description + "\n" +
				              (isActive
				                  ? "Active stance."
				                  : unit.HasSwitchedStanceThisTurn
				                      ? "Already switched this turn."
				                      : $"Switch: {MartialAPCosts.SwitchStance} AP, once per turn."),
			};
			btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall - 1);
			UITheme.ApplyButtonStyle(btn, isPrimary: isActive);
			string sid = stance.Id;   // capture by value for the closure
			btn.Pressed += () => EmitSignal(SignalName.StanceSwitchRequested, sid);
			_stanceRow.AddChild(btn);
		}
	}

	/// <summary>V2 (§7b): three enemy-only blocks — plain-language behavior line
	/// from BehaviorKey + tags, one block per ability (icon, name, intel), and
	/// the role/faction line. Strings live in UIContent — content, not code.</summary>
	private void RefreshInspectBlock(Unit unit, bool isEnemy)
	{
		if (_inspectBlock == null)
			return;

		foreach (Node child in _inspectBlock.GetChildren())
			child.QueueFree();

		if (!isEnemy)
		{
			_inspectBlock.Visible = false;
			return;
		}
		_inspectBlock.Visible = true;

		_inspectBlock.AddChild(MakeDivider(UITheme.VioletDim));

		// Behavior line — rules and reach, plain language.
		var behavior = MakeLabel(
			UIContent.DescribeBehavior(unit.BehaviorKey, unit.BehaviorTags),
			UITheme.FontSizeSmall, UITheme.TextSecondary);
		behavior.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_inspectBlock.AddChild(behavior);

		// One block per ability: icon + name, then the telegraph sentence.
		// §8 ability state (2026-07-17): the "current state" line — live
		// use-count for stacking/fired abilities (Requiem: "×2 this combat").
		foreach (var ab in unit.Abilities)
		{
			int uses = AbilityUseCount(unit, ab.Key);
			var nameLine = MakeLabel(
				uses > 0
					? $"{UIContent.AbilityIcon(ab.Key)} {ab.Name} ×{uses}"
					: $"{UIContent.AbilityIcon(ab.Key)} {ab.Name}",
				UITheme.FontSizeSmall, UITheme.Gold);
			_inspectBlock.AddChild(nameLine);

			// §5d: never blank. The authored line wins; UIContent carries the floor
			// so a key can no longer ship with no player-facing sentence at all.
			string abLine = UIContent.DescribeAbility(ab.Key, ab.IntelDescription);
			if (!string.IsNullOrEmpty(abLine))
			{
				var intel = MakeLabel(abLine, UITheme.FontSizeSmall, UITheme.TextDim);
				intel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
				_inspectBlock.AddChild(intel);
			}

			if (uses > 0)
			{
				var state = MakeLabel($"Fired ×{uses} this combat.",
					UITheme.FontSizeSmall, UITheme.TextSecondary);
				_inspectBlock.AddChild(state);
			}
		}

		// Role · faction line ("Elite · The Long Table"), valence-aware (V2 §6):
		// a corrupted faction shows its corrupted name; factionless = Blighted.
		var (blighted, _, _) = ResolveValence(unit);
		string factionName = "";
		if (!string.IsNullOrEmpty(unit.FactionId)
			&& ArchmageRegistry.Get(unit.FactionId) is { } arch)
			factionName = blighted && !string.IsNullOrEmpty(arch.CorruptedFactionName)
				? arch.CorruptedFactionName : arch.FactionName;
		else if (blighted)
			factionName = "Blighted";

		string roleLine = string.IsNullOrEmpty(factionName)
			? UIContent.RoleDisplay(unit.Role)
			: $"{UIContent.RoleDisplay(unit.Role)} · {factionName}";
		var role = MakeLabel(roleLine, UITheme.FontSizeSmall,
			unit.Role == "elite" ? UITheme.RoleElite
			: unit.Role == "boss" ? UITheme.RoleBoss
			: UITheme.TextDim);
		role.HorizontalAlignment = HorizontalAlignment.Center;
		_inspectBlock.AddChild(role);
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

		_logHistory.Add(message);
		if (_logHistory.Count > LogHistoryCap)
			_logHistory.RemoveAt(0);

		RedrawLog();
	}

	public void ClearActionLog()
	{
		_logQueue.Clear();
		_logHistory.Clear();
		if (_logLines == null)
			return;
		foreach (var lbl in _logLines)
			if (lbl != null)
				lbl.Text = "";
	}

	/// <summary>V1: ticker click — full scrollable history (§5).</summary>
	private void OnLogTickerPressed()
	{
		if (_logPopup == null)
			return;
		_logHistoryList.Clear();
		foreach (var line in _logHistory)
			_logHistoryList.AddItem(line);
		_logPopup.PopupCentered();
		if (_logHistory.Count > 0)
			_logHistoryList.EnsureCurrentIsVisible();
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

	// ── Enemy roster v2 (combat_ui_v2 §6) ────────────────────────────────

	/// <summary>V2: roster hover — index + entering. CombatManager wires this to
	/// the threat-range overlay (hovering a row = hovering the unit in-world).</summary>
	[Signal] public delegate void EnemyRowHoveredEventHandler(int unitIndex, bool entering);

	private List<Unit> _lastRosterEnemies;
	private Unit _activeRosterEnemy;

	/// <summary>V2: highlights the currently-acting enemy's row during the enemy
	/// phase — the roster doubles as the phase's progress bar. Null clears.</summary>
	public void SetActiveEnemy(Unit enemy)
	{
		_activeRosterEnemy = enemy;
		if (_lastRosterEnemies != null)
			RefreshEnemyRoster(_lastRosterEnemies);
	}

	public void RefreshEnemyRoster(List<Unit> enemies)
	{
		// Store BEFORE the built-check (2026-07-09): a call arriving before
		// BuildUI is remembered and replayed at the end of the build instead
		// of dropping silently.
		_lastRosterEnemies = enemies;

		if (_enemyRosterBox == null)
			return;

		foreach (Node child in _enemyRosterBox.GetChildren())
			child.QueueFree();

		for (int i = 0; i < enemies.Count; i++)
		{
			var enemy = enemies[i];
			if (enemy == null)
				continue;

			bool isActive = enemy == _activeRosterEnemy && enemy.Stats.IsAlive;

			var row = new HBoxContainer { Name = $"Enemy_{i}" };
			row.AddThemeConstantOverride("separation", 4);

			// Role marker (§6): Line dot, Elite chevron, Boss crest. The active
			// marker overrides it with ▶ during that unit's activation.
			Color roleCol = enemy.Role == "elite" ? UITheme.RoleElite
						 : enemy.Role == "boss" ? UITheme.RoleBoss
						 : UITheme.RoleLine;
			var marker = MakeLabel(
				isActive ? "▶" : UIContent.RoleMarker(enemy.Role),
				UITheme.FontSizeSmall,
				isActive ? UITheme.Gold : roleCol);
			marker.CustomMinimumSize = new Vector2(14, 0);
			marker.HorizontalAlignment = HorizontalAlignment.Center;
			row.AddChild(marker);

			// Nameplate policy (§6/§11): Line units carry ThreatLabel + spawn
			// index (already baked into unit.Name); Elite/Boss read as names.
			var btn = new Button
			{
				Text = enemy.Stats.IsAlive ? enemy.Name : $"✕ {enemy.Name}",
				Disabled = !enemy.Stats.IsAlive,
				CustomMinimumSize = new Vector2(80, 0),
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				TooltipText = enemy.Stats.IsAlive
					? UIContent.DescribeBehavior(enemy.BehaviorKey, enemy.BehaviorTags)
					: "",
			};
			btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
			if (!enemy.Stats.IsAlive)
				btn.Modulate = UITheme.TextDim;
			else if (enemy.Role == "elite" || enemy.Role == "boss")
				btn.Modulate = roleCol;

			int capturedIndex = i;
			btn.Pressed += () => EmitSignal(SignalName.EnemyButtonPressed, capturedIndex);
			// V2: row hover = world hover (threat-range overlay).
			btn.MouseEntered += () => EmitSignal(SignalName.EnemyRowHovered, capturedIndex, true);
			btn.MouseExited += () => EmitSignal(SignalName.EnemyRowHovered, capturedIndex, false);
			row.AddChild(btn);

			if (enemy.Stats.IsAlive)
			{
				// V3 charge dot (§8): ranged_charge units telegraph their cycle —
				// hollow ○ = will begin channelling, filled ✸ = releases next
				// activation. Replaces the implicit every-other-turn counting.
				if (enemy.BehaviorKey == "ranged_charge")
				{
					bool charged = enemy.HasStatus("wizard_charging");
					var chargeDot = MakeLabel(charged ? "✸" : "○", UITheme.FontSizeSmall,
						charged ? UITheme.ChargeReady : UITheme.ChargeSpent);
					chargeDot.TooltipText = charged
						? "Charged — releases its blast next activation"
						: "Will begin channelling";
					chargeDot.CustomMinimumSize = new Vector2(14, 0);
					row.AddChild(chargeDot);
				}

				// Ability chips (§6): one icon per ability, tooltip = telegraph.
				// §8 ability state (2026-07-17): stacking/fired abilities carry a
				// live use-count from Unit.AbilityUseCounts ("✦2" = Requiem ×2).
				foreach (var ab in enemy.Abilities)
				{
					int uses = AbilityUseCount(enemy, ab.Key);
					string icon = UIContent.AbilityIcon(ab.Key);
					var chip = MakeLabel(uses > 0 ? $"{icon}{uses}" : icon,
						UITheme.FontSizeSmall, UITheme.Gold);
					string chipLine = UIContent.DescribeAbility(ab.Key, ab.IntelDescription);
					chip.TooltipText = uses > 0
						? $"{ab.Name} ×{uses}: {chipLine}"
						: $"{ab.Name}: {chipLine}";
					chip.CustomMinimumSize = new Vector2(14, 0);
					row.AddChild(chip);
				}

				// Behavior-tag chips (v2.2 §7b): pack/charge/bulwark telegraph.
				// Only tags with an authored chip render — a chip is a promise
				// the mechanic is wired, so inert tags (flock/flying) stay off.
				if (enemy.BehaviorTags != null)
				{
					foreach (var tag in enemy.BehaviorTags)
					{
						string letter = UIContent.TagChipLetter(tag);
						if (letter == null)
							continue;
						Color tagCol = tag.ToLowerInvariant() switch
						{
							"pack"    => UITheme.TagPack,
							"charge"  => UITheme.TagCharge,
							"bulwark" => UITheme.TagBulwark,
							_         => UITheme.TagNeutral,
						};
						var tagChip = MakeLabel(letter, UITheme.FontSizeSmall, tagCol);
						tagChip.TooltipText = UIContent.TagChipTooltip(tag);
						tagChip.CustomMinimumSize = new Vector2(12, 0);
						tagChip.HorizontalAlignment = HorizontalAlignment.Center;
						row.AddChild(tagChip);
					}
				}

				// HP bar (§6): faction-tinted at low saturation so the roster
				// doubles as a faction read; generics keep the health gradient.
				float pct = (float)enemy.Stats.Health / enemy.Stats.MaxHealth;
				Color barCol;
				if (!string.IsNullOrEmpty(enemy.FactionId)
					&& ArchmageRegistry.Get(enemy.FactionId) is { } arch)
				{
					barCol = new Color(arch.FactionColorHex).Lerp(UITheme.TextPrimary, 0.25f);
				}
				else
				{
					barCol = pct > 0.5f
						? UITheme.Success.Lerp(UITheme.Warning, (1f - pct) * 2f)
						: UITheme.Warning.Lerp(UITheme.Danger, (0.5f - pct) * 2f);
				}

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

				// Valence tag (V2 §6): kingdom chip vs blight chip — who claims
				// this unit is echo-relevant everywhere, not only in settlements.
				var (_, valenceTip, valenceCol) = ResolveValence(enemy);
				var valChip = MakeLabel("■", UITheme.FontSizeSmall, valenceCol);
				valChip.TooltipText = valenceTip;
				valChip.CustomMinimumSize = new Vector2(12, 0);
				valChip.HorizontalAlignment = HorizontalAlignment.Center;
				row.AddChild(valChip);
			}

			_enemyRosterBox.AddChild(row);
		}
	}

	// R22 damage preview note: the preview renders as a flashing span of the
	// victim's in-world HP bar (HealthBarRoot.ShowDamagePreview), driven by
	// CombatManager.UpdateDamagePreview — no CombatUI surface involved.

	/// <summary>§8 ability state: case-insensitive read of a unit's live
	/// use-count for an ability key (RequiemEffect writes lowercase keys).</summary>
	private static int AbilityUseCount(Unit unit, string abilityKey)
	{
		if (unit?.AbilityUseCounts == null || string.IsNullOrEmpty(abilityKey))
			return 0;
		if (unit.AbilityUseCounts.TryGetValue(abilityKey, out var n))
			return n;
		return unit.AbilityUseCounts.TryGetValue(abilityKey.ToLowerInvariant(), out n) ? n : 0;
	}

	/// <summary>V2 §6 valence: a unit whose archmage faction is uncorrupted is
	/// kingdom-aligned (chip in the faction color); a corrupted faction's unit
	/// or a factionless monster is blighted (spec accent #1A3A5C). Debug fights
	/// with no active save read as uncorrupted.</summary>
	private static (bool blighted, string tooltip, Color color) ResolveValence(Unit unit)
	{
		if (!string.IsNullOrEmpty(unit.FactionId)
			&& ArchmageRegistry.Get(unit.FactionId) is { } arch)
		{
			bool corrupted = SaveManager.ActiveSave?.Campaign?
				.GetDisposition(unit.FactionId) == ArchmageDisposition.Corrupted;
			if (!corrupted)
				return (false, $"{arch.FactionName} — kingdom-aligned",
					new Color(arch.FactionColorHex));

			string cname = string.IsNullOrEmpty(arch.CorruptedFactionName)
				? arch.FactionName : arch.CorruptedFactionName;
			return (true, $"{cname} — corrupted", UITheme.ValenceBlight);
		}
		return (true, "Blighted — claimed by no kingdom", UITheme.ValenceBlight);
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

			// V2: role marker on deployment intel too.
			var marker = MakeLabel(UIContent.RoleMarker(entry.Role),
				UITheme.FontSizeSmall,
				entry.Role == "elite" ? UITheme.RoleElite
				: entry.Role == "boss" ? UITheme.RoleBoss
				: UITheme.RoleLine);
			marker.CustomMinimumSize = new Vector2(12, 0);
			row.AddChild(marker);

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
			if (!string.IsNullOrEmpty(entry.Intel))
				lbl.TooltipText = entry.Intel;
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
			// O3: the ward is a mission element, not a squad member — its
			// health reads on the board, not in the party bar.
			if (unit.IsObjectiveWard)
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
				int chipWither = Mathf.Max(0, unit.Stats.WitheredMaxHp);
				int chipOrigMax = Mathf.Max(1, unit.Stats.MaxHealth + chipWither);
				var hpLbl = MakeLabel(
					chipWither > 0
						? $"{unit.Stats.Health}/{unit.Stats.MaxHealth} (−{chipWither})"
						: $"{unit.Stats.Health}/{unit.Stats.MaxHealth}",
					UITheme.FontSizeSmall - 1, UITheme.TextSecondary);
				hpLbl.HorizontalAlignment = HorizontalAlignment.Center;
				vbox.AddChild(hpLbl);

				// HP strip (V2.2: original max = full width, withered span painted)
				float pct = Mathf.Clamp((float)unit.Stats.Health / chipOrigMax, 0f, 1f);
				Color hpCol = pct > 0.5f
					? UITheme.Success.Lerp(UITheme.Warning, (1f - pct) * 2f)
					: UITheme.Warning.Lerp(UITheme.Danger, (0.5f - pct) * 2f);

				var hpStrip = new ProgressBar
				{
					ShowPercentage = false,
					CustomMinimumSize = new Vector2(0, 4),
					SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				};
				vbox.AddChild(hpStrip);
				SetHpBarWithered(hpStrip, unit.Stats.Health, unit.Stats.MaxHealth, chipWither, hpCol, UITheme.BgDeep);
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
	/// <summary>V2.2 (combat_ui §8): HP bar with the withered-max span, matching the
	/// in-world HealthBarRoot. Full width = original max (curMax + withered); current HP
	/// fills against that; the withered span is painted bruised-violet, right-anchored,
	/// via a lazily-created ColorRect so the effective max visibly shrinks.</summary>
	private static void SetHpBarWithered(ProgressBar bar, int health, int curMax,
		int withered, Color fillCol, Color backCol)
	{
		if (bar == null)
			return;
		withered = Mathf.Max(0, withered);
		int originalMax = Mathf.Max(1, curMax + withered);
		bar.MaxValue = originalMax;
		bar.Value = Mathf.Clamp(health, 0, originalMax);
		bar.AddThemeStyleboxOverride("fill", MakeFillStyle(fillCol));
		bar.AddThemeStyleboxOverride("background", MakeFillStyle(backCol));

		var overlay = bar.GetNodeOrNull<ColorRect>("WitherOverlay");
		if (withered > 0)
		{
			if (overlay == null)
			{
				overlay = new ColorRect { Name = "WitherOverlay", MouseFilter = Control.MouseFilterEnum.Ignore };
				bar.AddChild(overlay);
			}
			float frac = (float)withered / originalMax;
			overlay.AnchorLeft = 1f - frac;
			overlay.AnchorRight = 1f;
			overlay.AnchorTop = 0f;
			overlay.AnchorBottom = 1f;
			overlay.OffsetLeft = 0f;
			overlay.OffsetRight = 0f;
			overlay.OffsetTop = 0f;
			overlay.OffsetBottom = 0f;
			overlay.Color = UITheme.WitherFill;
			overlay.Visible = true;
			GD.Print($"[WitherHUD] {health}/{curMax} (−{withered}) origMax={originalMax} frac={frac:F2}");
		}
		else if (overlay != null)
		{
			overlay.Visible = false;
		}
	}

}
