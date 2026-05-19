using Godot;
using System.Collections.Generic;

// ============================================================
// CombatUI.cs
//
// Purpose:        CanvasLayer hosting all in-combat HUD widgets —
//                 selected-unit panel, enemy roster, action log,
//                 deck/grave counters, and the bottom unit bar.
// Layer:          UI
// Collaborators:  CombatScene.cs (instantiates this and wires signals),
//                 Unit.cs, GameState.cs, UITheme.cs,
//                 CardRuntime.cs (Card list passed to RefreshDeckCounts)
// See:            README §3 (Architecture Overview),
//                 README §8 (Godot 4.6 compat — CallDeferred rules apply
//                 to children built in code here)
// ============================================================

/// <summary>
/// CanvasLayer for the combat HUD. Holds all the bound nodes for the in-combat panels,
/// rebuilds the dynamic enemy roster and player unit bar each tick, and surfaces button
/// presses as Godot signals. Holds no game state — purely a view layer driven by
/// <c>CombatScene</c> and friends.
/// </summary>
public partial class CombatUI : CanvasLayer
{
	/// <summary>Emitted when the player clicks the "Confirm Deployment" button during the deployment phase.</summary>
	[Signal] public delegate void ConfirmDeploymentPressedEventHandler();

	/// <summary>Emitted when the player clicks "End Turn" during their turn.</summary>
	[Signal] public delegate void EndTurnPressedEventHandler();

	/// <summary>Emitted when the player clicks a unit button in the bottom unit bar. <paramref name="unitIndex"/> is the index into the player unit list passed to <see cref="RefreshPlayerUnitBar"/>.</summary>
	[Signal] public delegate void UnitButtonPressedEventHandler(int unitIndex);

	/// <summary>Emitted when the player clicks an enemy button in the enemy roster. <paramref name="unitIndex"/> is the index into the enemy list passed to <see cref="RefreshEnemyRoster"/>.</summary>
	[Signal] public delegate void EnemyButtonPressedEventHandler(int unitIndex);

	// ── Selected Unit Panel (top-left) ──────────────────────────────────────
	private Label _phaseLabel;
	private Label _unitNameLabel;
	private Label _healthLabel;
	private Label _movementLabel;
	private Label _manaLabel;
	private Label _hintLabel;
	private Label _stanceLabel;

	private ProgressBar _healthBar;
	private ProgressBar _moveBar;
	private ProgressBar _manaBar;

	private Button _confirmDeploymentButton;
	private Button _endTurnButton;

	// ── Enemy Roster Panel (top-right) ──────────────────────────────────────
	private VBoxContainer _enemyRosterBox;

	// ── Player Unit Bar (bottom-left) ───────────────────────────────────────
	private HBoxContainer _playerUnitBar;

	// ── Action Log (bottom-center) ──────────────────────────────────────────
	private Label _actionLogLabel;

	// ── Deck / Graveyard counters (bottom-right) ────────────────────────────
	private Button _deckButton;
	private Button _graveButton;

	// ── Graveyard popup ─────────────────────────────────────────────────────
	private PopupPanel _gravePopup;
	private ItemList _graveList;

	// ── Deck popup ───────────────────────────────────────────────────────────
	private PopupPanel _deckPopup;
	private ItemList _deckList;

	// ── Internal state ───────────────────────────────────────────────────────
	private bool _nodesCached = false;
	private readonly Queue<string> _logLines = new();

	public override void _Ready()
	{
		CacheNodes();

		GD.Print($"ActionLogLabel found: {_actionLogLabel}");
		GD.Print($"DeckButton found: {_deckButton != null}");
		GD.Print($"GraveButton found: {_graveButton != null}");

		WireButtons();
	}

	// ── Node caching ─────────────────────────────────────────────────────────

	private void CacheNodes()
	{
		if (_nodesCached)
			return;
		_nodesCached = true;

		// Phase / hint
		_phaseLabel = GetNodeOrNull<Label>("PhasePanel/PhaseLabel");
		_hintLabel = GetNodeOrNull<Label>("HintPanel/HintLabel");

		// Selected unit panel
		_unitNameLabel = GetNodeOrNull<Label>("SelectedUnitPanel/MarginContainer/VBoxContainer/UnitNameLabel");
		_healthLabel = GetNodeOrNull<Label>("SelectedUnitPanel/MarginContainer/VBoxContainer/HealthRow/HealthLabel");
		_movementLabel = GetNodeOrNull<Label>("SelectedUnitPanel/MarginContainer/VBoxContainer/MoveRow/MovementLabel");
		_manaLabel = GetNodeOrNull<Label>("SelectedUnitPanel/MarginContainer/VBoxContainer/ManaRow/ManaLabel");
		_healthBar = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/HealthRow/HealthBar");
		_moveBar = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/MoveRow/MoveBar");
		_manaBar = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/ManaRow/ManaBar");

		// Action buttons
		_confirmDeploymentButton = GetNodeOrNull<Button>("ActionPanel/HBoxContainer/ConfirmDeploymentButton");
		_endTurnButton = GetNodeOrNull<Button>("ActionPanel/HBoxContainer/EndTurnButton");

		// New panels (null-safe — only wired up when the .tscn nodes exist)
		_enemyRosterBox = GetNodeOrNull<VBoxContainer>("EnemyRosterPanel/VBoxContainer");
		_playerUnitBar = GetNodeOrNull<HBoxContainer>("PlayerUnitBar/HBoxContainer");
		_actionLogLabel = GetNodeOrNull<Label>("ActionLogPanel/ActionLogLabel");
		_deckButton = GetNodeOrNull<Button>("DeckGravePanel/HBoxContainer/DeckButton");
		_graveButton = GetNodeOrNull<Button>("DeckGravePanel/HBoxContainer/GraveButton");
		_gravePopup = GetNodeOrNull<PopupPanel>("GravePopup");
		_graveList = GetNodeOrNull<ItemList>("GravePopup/ItemList");
		_deckPopup = GetNodeOrNull<PopupPanel>("DeckPopup");
		_deckList = GetNodeOrNull<ItemList>("DeckPopup/ItemList");

		// Warn on missing critical nodes
		if (_phaseLabel == null) GD.PrintErr("CombatUI: PhaseLabel not found");
		if (_unitNameLabel == null) GD.PrintErr("CombatUI: UnitNameLabel not found");
		if (_healthLabel == null) GD.PrintErr("CombatUI: HealthLabel not found");
		if (_movementLabel == null) GD.PrintErr("CombatUI: MovementLabel not found");
		if (_manaLabel == null) GD.PrintErr("CombatUI: ManaLabel not found");
		if (_hintLabel == null) GD.PrintErr("CombatUI: HintLabel not found");
		if (_confirmDeploymentButton == null) GD.PrintErr("CombatUI: ConfirmDeploymentButton not found");
		if (_endTurnButton == null) GD.PrintErr("CombatUI: EndTurnButton not found");

		// Suppress the built-in % text — labels already show the values
		if (_healthBar != null) _healthBar.ShowPercentage = false;
		if (_moveBar != null) _moveBar.ShowPercentage = false;
		if (_manaBar != null) _manaBar.ShowPercentage = false;

		// Build stance label once if it doesn't exist
		if (_stanceLabel == null && _unitNameLabel != null)
		{
			_stanceLabel = new Label();
			_stanceLabel.AddThemeFontSizeOverride("font_size", 10);
			_stanceLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			_stanceLabel.CustomMinimumSize = new Vector2(120, 0);
			// Insert after the mana bar's parent container
			_unitNameLabel.GetParent()?.AddChild(_stanceLabel);
		}
	}

	private void WireButtons()
	{
		if (_confirmDeploymentButton != null)
			_confirmDeploymentButton.Pressed += OnConfirmDeploymentButtonPressed;

		if (_endTurnButton != null)
			_endTurnButton.Pressed += OnEndTurnButtonPressed;

		if (_deckButton != null)
			_deckButton.Pressed += OnDeckButtonPressed;

		if (_graveButton != null)
			_graveButton.Pressed += OnGraveButtonPressed;
	}

	// ── Button callbacks ─────────────────────────────────────────────────────

	private void OnConfirmDeploymentButtonPressed() => EmitSignal(SignalName.ConfirmDeploymentPressed);
	private void OnEndTurnButtonPressed() => EmitSignal(SignalName.EndTurnPressed);

	private void OnDeckButtonPressed()
	{
		if (_deckPopup == null) return;
		_deckPopup.PopupCentered();
	}

	private void OnGraveButtonPressed()
	{
		if (_gravePopup == null) return;
		_gravePopup.Popup();
	}

	// ── Phase / hint text ────────────────────────────────────────────────────

	/// <summary>Sets the phase indicator label in the top-left (e.g. "Deployment", "Player Turn"). No-op if the label node is missing.</summary>
	public void SetPhaseText(string text)
	{
		CacheNodes();
		if (_phaseLabel != null)
			_phaseLabel.Text = text;
	}

	/// <summary>Sets the contextual hint label (e.g. "Click a tile to deploy", "Select a target"). No-op if the label node is missing.</summary>
	public void SetHintText(string text)
	{
		CacheNodes();
		if (_hintLabel != null)
			_hintLabel.Text = text;
	}

	// ── Selected unit panel ──────────────────────────────────────────────────

	/// <summary>
	/// Populates the top-left selected-unit panel. Pass <paramref name="unit"/> as null
	/// to show the "No Unit Selected" empty state. <paramref name="mana"/> is the current
	/// mana pool; pass -1 to hide the mana row entirely.
	/// </summary>
	public void ShowSelectedUnit(Unit unit, int mana)
	{
		CacheNodes();

		if (_unitNameLabel == null)
			return;

		if (unit == null)
		{
			_unitNameLabel.Text = "No Unit Selected";
			if (_healthLabel != null) _healthLabel.Text = "";
			if (_movementLabel != null) _movementLabel.Text = "";
			if (_manaLabel != null) { _manaLabel.Text = ""; _manaLabel.Visible = false; }
			if (_manaBar != null) _manaBar.Visible = false;
			SetBar(_healthBar, 1, 0);
			SetBar(_moveBar, 1, 0);
			return;
		}

		bool isEnemy = !unit.IsPlayerControlled;

		_unitNameLabel.Text = isEnemy ? $"[Enemy] {unit.Name}" : unit.Name;

		if (_healthLabel != null) _healthLabel.Text = $"HP:   {unit.Stats.Health} / {unit.Stats.MaxHealth}";
		if (_movementLabel != null) _movementLabel.Text = isEnemy
			? $"Speed: {unit.Stats.BaseSpeed}"
			: $"AP: {unit.CurrentActionPoints} / {unit.MaxActionPoints}";

		bool showMana = unit.Stats.MaxMana > 0;

		if (_manaLabel != null)
		{
			_manaLabel.Text = showMana ? $"Mana: {mana}" : "";
			_manaLabel.Visible = showMana;
		}

		if (_manaBar != null)
			_manaBar.Visible = showMana;

		SetBar(_healthBar, unit.Stats.MaxHealth, unit.Stats.Health);
		SetBar(_moveBar,
			isEnemy ? 1 : unit.MaxActionPoints,
			isEnemy ? 1 : unit.CurrentActionPoints);

		if (showMana)
			SetBar(_manaBar, Mathf.Max(1, unit.Stats.MaxMana), mana);
		// ── Stance display ────────────────────────────────────────────────────
		if (_stanceLabel != null)
		{
			if (!isEnemy && unit.IsMartial && unit.ActiveStance != null)
			{
				_stanceLabel.Text = $"[{unit.ActiveStance.DisplayName}]\n{unit.ActiveStance.Description}";
				_stanceLabel.Modulate = new Color(0.9f, 0.85f, 0.5f); // warm gold
				_stanceLabel.Visible = true;
			}
			else if (!isEnemy && unit.IsMartial && unit.ActiveStance == null)
			{
				_stanceLabel.Text = "No stance active";
				_stanceLabel.Modulate = new Color(0.6f, 0.6f, 0.6f);
				_stanceLabel.Visible = true;
			}
			else
			{
				_stanceLabel.Visible = false; // wizards and enemies show nothing
			}
		}
	}

	// ── Deployment mode ──────────────────────────────────────────────────────

	/// <summary>Toggles the action-button panel between deployment mode (Confirm Deployment visible) and combat mode (End Turn visible). The two buttons are mutually exclusive.</summary>
	public void SetDeploymentMode(bool isDeployment)
	{
		CacheNodes();
		if (_confirmDeploymentButton != null)
			_confirmDeploymentButton.Visible = isDeployment;
		if (_endTurnButton != null)
			_endTurnButton.Visible = !isDeployment;
	}

	// ── Enemy Roster ─────────────────────────────────────────────────────────

	/// <summary>
	/// Rebuilds the top-right enemy roster from the live enemy list. Dead enemies render as
	/// disabled "[dead]" rows. Index in <paramref name="enemies"/> is the value emitted by
	/// <see cref="EnemyButtonPressed"/>. Call after any enemy death or HP change.
	/// </summary>
	public void RefreshEnemyRoster(List<Unit> enemies)
	{
		CacheNodes();
		if (_enemyRosterBox == null) return;

		foreach (Node child in _enemyRosterBox.GetChildren())
			child.QueueFree();

		for (int i = 0; i < enemies.Count; i++)
		{
			var enemy = enemies[i];
			if (enemy == null) continue;

			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 6);

			var btn = new Button();
			btn.Text = enemy.Stats.IsAlive ? enemy.Name : $"[dead] {enemy.Name}";
			btn.Disabled = !enemy.Stats.IsAlive;
			btn.CustomMinimumSize = new Vector2(UITheme.EnemyRosterButtonWidth, 0);
			int capturedIndex = i;
			btn.Pressed += () => EmitSignal(SignalName.EnemyButtonPressed, capturedIndex);
			row.AddChild(btn);

			if (enemy.Stats.IsAlive)
			{
				var bar = new ProgressBar();
				bar.MaxValue = Mathf.Max(1, enemy.Stats.MaxHealth);
				bar.Value = enemy.Stats.Health;
				bar.ShowPercentage = false;
				bar.CustomMinimumSize = new Vector2(UITheme.EnemyRosterBarWidth, UITheme.EnemyRosterBarHeight);
				bar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
				var style = new StyleBoxFlat { BgColor = UITheme.EnemyHealthBar };
				bar.AddThemeStyleboxOverride("fill", style);
				row.AddChild(bar);

				var lbl = new Label();
				lbl.Text = $"{enemy.Stats.Health}/{enemy.Stats.MaxHealth}";
				lbl.CustomMinimumSize = new Vector2(44, 0);
				row.AddChild(lbl);
			}

			_enemyRosterBox.AddChild(row);
		}
	}

	// ── Enemy Intel (shown during deployment instead of live roster) ──────────

	/// <summary>
	/// Replaces the enemy roster with a pre-combat intel summary built from
	/// pending spawn data. No live Unit references needed.
	/// </summary>
	public void ShowEnemyIntel(List<EnemyIntelEntry> entries)
	{
		CacheNodes();
		if (_enemyRosterBox == null) return;

		foreach (Node child in _enemyRosterBox.GetChildren())
			child.QueueFree();

		// Header
		var header = new Label();
		header.Text = "─ ENEMY INTEL ─";
		header.HorizontalAlignment = HorizontalAlignment.Center;
		header.AddThemeColorOverride("font_color", new Color(0.9f, 0.7f, 0.2f));
		_enemyRosterBox.AddChild(header);

		foreach (var entry in entries)
		{
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 6);

			// Colour swatch matching the enemy's body colour
			var swatch = new ColorRect();
			swatch.Color = entry.BodyColor;
			swatch.CustomMinimumSize = new Vector2(10, 20);
			row.AddChild(swatch);

			// Threat label + stats
			var lbl = new Label();
			lbl.Text = $"{entry.ThreatLabel}  HP:{entry.MaxHealth}  SPD:{entry.BaseSpeed}";
			if (entry.Armor > 0)
				lbl.Text += $"  ARM:{entry.Armor}";
			lbl.CustomMinimumSize = new Vector2(160, 0);
			row.AddChild(lbl);

			_enemyRosterBox.AddChild(row);
		}

		// Footer hint
		var hint = new Label();
		hint.Text = "Formation unknown until deployment ends.";
		hint.AutowrapMode = TextServer.AutowrapMode.Word;
		hint.CustomMinimumSize = new Vector2(180, 0);
		hint.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
		_enemyRosterBox.AddChild(hint);
	}

	// ── Player Unit Bar ──────────────────────────────────────────────────────

	/// <summary>
	/// Rebuilds the bottom-left player unit bar from the player's roster. The
	/// <paramref name="selectedUnit"/> argument (which must be a reference held in
	/// <paramref name="playerUnits"/>) receives a highlighted border. Dead units render
	/// as disabled "[dead]" buttons. Index in <paramref name="playerUnits"/> is the
	/// value emitted by <see cref="UnitButtonPressed"/>.
	/// </summary>
	public void RefreshPlayerUnitBar(List<Unit> playerUnits, Unit selectedUnit)
	{
		CacheNodes();
		if (_playerUnitBar == null) return;

		foreach (Node child in _playerUnitBar.GetChildren())
			child.QueueFree();

		for (int i = 0; i < playerUnits.Count; i++)
		{
			var unit = playerUnits[i];
			if (unit == null) continue;

			var panel = new PanelContainer();

			if (unit == selectedUnit)
			{
				var style = new StyleBoxFlat();
				style.BgColor = UITheme.UnitBarSelected;
				style.BorderColor = UITheme.UnitBarBorder;
				style.SetBorderWidthAll(UITheme.BorderWidth);
				panel.AddThemeStyleboxOverride("panel", style);
			}

			var vbox = new VBoxContainer();
			vbox.AddThemeConstantOverride("separation", 1);

			var btn = new Button();
			btn.Text = unit.Stats.IsAlive ? unit.Name : "[dead]";
			btn.Disabled = !unit.Stats.IsAlive;
			int capturedIndex = i;
			btn.Pressed += () => EmitSignal(SignalName.UnitButtonPressed, capturedIndex);
			vbox.AddChild(btn);

			AddStatRow(vbox, $"HP {unit.Stats.Health}/{unit.Stats.MaxHealth}",
				unit.Stats.MaxHealth, unit.Stats.Health, UITheme.StatBarHealth);

			// Replace the movement row with AP pips:
			var pipRow = new HBoxContainer();
			pipRow.AddThemeConstantOverride("separation", 3);
			for (int p = 0; p < unit.MaxActionPoints; p++)
			{
				var pip = new ColorRect();
				pip.CustomMinimumSize = new Vector2(12, 12);
				pip.Color = p < unit.CurrentActionPoints
					? UITheme.APPipFull    // bright, e.g. new Color(0.9f, 0.8f, 0.2f)
					: UITheme.APPipEmpty;  // dim, e.g. new Color(0.25f, 0.25f, 0.25f)
				pipRow.AddChild(pip);
			}
			// AP Label
			var apLabel = new Label { Text = "AP" };
			apLabel.HorizontalAlignment = HorizontalAlignment.Center;
			apLabel.AddThemeFontSizeOverride("font_size", 10);
			vbox.AddChild(apLabel);
			vbox.AddChild(pipRow);

			if (unit.Stats.MaxMana > 0)
			{
				AddStatRow(vbox, $"MANA {unit.Stats.Mana}/{unit.Stats.MaxMana}",
					unit.Stats.MaxMana, unit.Stats.Mana, UITheme.StatBarMana);
			}

			panel.AddChild(vbox);
			_playerUnitBar.AddChild(panel);
		}
	}

	private static void AddStatRow(VBoxContainer parent, string text, int max, int value, Color fillColor)
	{
		var lbl = new Label();
		lbl.Text = text;
		lbl.HorizontalAlignment = HorizontalAlignment.Center;
		lbl.AddThemeFontSizeOverride("font_size", UITheme.CombatStatLabelFontSize);
		parent.AddChild(lbl);

		var bar = new ProgressBar();
		bar.MaxValue = Mathf.Max(1, max);
		bar.Value = Mathf.Clamp(value, 0, max);
		bar.ShowPercentage = false;
		bar.CustomMinimumSize = new Vector2(UITheme.UnitBarStatBarWidth, UITheme.UnitBarStatBarHeight);
		bar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		var style = new StyleBoxFlat { BgColor = fillColor };
		bar.AddThemeStyleboxOverride("fill", style);
		parent.AddChild(bar);
	}

	// ── Action Log ───────────────────────────────────────────────────────────

	/// <summary>
	/// Appends a single line to the action log. Lines older than
	/// <see cref="UITheme.MaxActionLogLines"/> are dropped from the visible buffer.
	/// Every appended line is also echoed to the Godot console with an `[ActionLog]` prefix.
	/// </summary>
	public void AppendActionLog(string message)
	{
		CacheNodes();

		_logLines.Enqueue(message);
		while (_logLines.Count > UITheme.MaxActionLogLines)
			_logLines.Dequeue();

		if (_actionLogLabel != null)
			_actionLogLabel.Text = string.Join("\n", _logLines);

		GD.Print($"[ActionLog] {message}");
	}

	/// <summary>Clears the action log buffer and blanks the visible label. Use between combat encounters.</summary>
	public void ClearActionLog()
	{
		_logLines.Clear();
		if (_actionLogLabel != null)
			_actionLogLabel.Text = "";
	}

	// ── Deck / Graveyard counters ────────────────────────────────────────────

	/// <summary>
	/// Refreshes the deck/grave counter buttons and rebuilds the popup lists shown when
	/// either is clicked. Null lists are treated as empty.
	/// </summary>
	public void RefreshDeckCounts(List<Card> library, List<Card> graveyard)
	{
		CacheNodes();

		if (_deckButton != null) _deckButton.Text = $"Deck: {library?.Count ?? 0}";
		if (_graveButton != null) _graveButton.Text = $"Grave: {graveyard?.Count ?? 0}";

		if (_graveList != null)
		{
			_graveList.Clear();
			if (graveyard != null)
				foreach (var card in graveyard)
					_graveList.AddItem(card.CardName ?? card.TopHalf?.Name ?? "Unknown");
		}

		if (_deckList != null)
		{
			_deckList.Clear();
			if (library != null)
				foreach (var card in library)
					_deckList.AddItem(card.CardName ?? card.TopHalf?.Name ?? "Unknown");
		}
	}

	// ── Helpers ──────────────────────────────────────────────────────────────

	private static void SetBar(ProgressBar bar, int max, int value)
	{
		if (bar == null) return;
		bar.MaxValue = Mathf.Max(1, max);
		bar.Value = Mathf.Clamp(value, 0, max);
	}
}
