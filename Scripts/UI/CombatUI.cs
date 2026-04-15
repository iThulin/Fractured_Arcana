using Godot;
using System.Collections.Generic;

public partial class CombatUI : CanvasLayer
{
	[Signal] public delegate void ConfirmDeploymentPressedEventHandler();
	[Signal] public delegate void EndTurnPressedEventHandler();
	[Signal] public delegate void UnitButtonPressedEventHandler(int unitIndex);
	[Signal] public delegate void EnemyButtonPressedEventHandler(int unitIndex);

	// ── Selected Unit Panel (top-left) ──────────────────────────────────────
	private Label _phaseLabel;
	private Label _unitNameLabel;
	private Label _healthLabel;
	private Label _movementLabel;
	private Label _manaLabel;
	private Label _hintLabel;

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
	private ItemList   _graveList;

	// ── Deck popup ───────────────────────────────────────────────────────────
	private PopupPanel _deckPopup;
	private ItemList   _deckList;

	// ─────────────────────────────────────────────────────────────────────────

	private bool _nodesCached = false;

	public override void _Ready()
	{
		CacheNodes();
		WireButtons();
	}

	// ── Node caching ─────────────────────────────────────────────────────────

	private void CacheNodes()
	{
		if (_nodesCached)
			return;
		_nodesCached = true;

		// Phase / hint
		_phaseLabel    = GetNodeOrNull<Label>("PhasePanel/PhaseLabel");
		_hintLabel     = GetNodeOrNull<Label>("HintPanel/HintLabel");

		// Selected unit panel
		_unitNameLabel  = GetNodeOrNull<Label>    ("SelectedUnitPanel/MarginContainer/VBoxContainer/UnitNameLabel");
		_healthLabel    = GetNodeOrNull<Label>    ("SelectedUnitPanel/MarginContainer/VBoxContainer/HealthRow/HealthLabel");
		_movementLabel  = GetNodeOrNull<Label>    ("SelectedUnitPanel/MarginContainer/VBoxContainer/MoveRow/MovementLabel");
		_manaLabel      = GetNodeOrNull<Label>    ("SelectedUnitPanel/MarginContainer/VBoxContainer/ManaRow/ManaLabel");
		_healthBar      = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/HealthRow/HealthBar");
		_moveBar        = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/MoveRow/MoveBar");
		_manaBar        = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/ManaRow/ManaBar");

		// Action buttons
		_confirmDeploymentButton = GetNodeOrNull<Button>("ActionPanel/HBoxContainer/ConfirmDeploymentButton");
		_endTurnButton           = GetNodeOrNull<Button>("ActionPanel/HBoxContainer/EndTurnButton");

		// New panels (null-safe – only wired up when the .tscn nodes exist)
		_enemyRosterBox  = GetNodeOrNull<VBoxContainer>("EnemyRosterPanel/VBoxContainer");
		_playerUnitBar   = GetNodeOrNull<HBoxContainer>("PlayerUnitBar/HBoxContainer");
		_actionLogLabel  = GetNodeOrNull<Label>        ("ActionLogPanel/ActionLogLabel");
		_deckButton      = GetNodeOrNull<Button>       ("DeckGravePanel/HBoxContainer/DeckButton");
		_graveButton     = GetNodeOrNull<Button>       ("DeckGravePanel/HBoxContainer/GraveButton");
		_gravePopup      = GetNodeOrNull<PopupPanel>   ("GravePopup");
		_graveList       = GetNodeOrNull<ItemList>     ("GravePopup/ItemList");
		_deckPopup       = GetNodeOrNull<PopupPanel>   ("DeckPopup");
		_deckList        = GetNodeOrNull<ItemList>     ("DeckPopup/ItemList");

		// Warn on missing critical nodes
		if (_phaseLabel        == null) GD.PrintErr("CombatUI: PhaseLabel not found");
		if (_unitNameLabel     == null) GD.PrintErr("CombatUI: UnitNameLabel not found");
		if (_healthLabel       == null) GD.PrintErr("CombatUI: HealthLabel not found");
		if (_movementLabel     == null) GD.PrintErr("CombatUI: MovementLabel not found");
		if (_manaLabel         == null) GD.PrintErr("CombatUI: ManaLabel not found");
		if (_hintLabel         == null) GD.PrintErr("CombatUI: HintLabel not found");
		if (_confirmDeploymentButton == null) GD.PrintErr("CombatUI: ConfirmDeploymentButton not found");
		if (_endTurnButton     == null) GD.PrintErr("CombatUI: EndTurnButton not found");

		// Optional new panels – just inform, don't error
		if (_enemyRosterBox == null) GD.Print("CombatUI: EnemyRosterPanel not in scene yet – add it to enable enemy roster.");
		if (_playerUnitBar  == null) GD.Print("CombatUI: PlayerUnitBar not in scene yet – add it to enable unit bar.");
		if (_actionLogLabel == null) GD.Print("CombatUI: ActionLogPanel not in scene yet – add it to enable action log.");
		if (_deckButton     == null) GD.Print("CombatUI: DeckGravePanel not in scene yet – add it to enable deck/grave counters.");
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
	private void OnEndTurnButtonPressed()           => EmitSignal(SignalName.EndTurnPressed);

	private void OnDeckButtonPressed()
	{
		if (_deckPopup == null) return;
		_deckPopup.Popup();
	}

	private void OnGraveButtonPressed()
	{
		if (_gravePopup == null) return;
		_gravePopup.Popup();
	}

	// ── Phase / hint text ────────────────────────────────────────────────────

	public void SetPhaseText(string text)
	{
		CacheNodes();
		if (_phaseLabel != null)
			_phaseLabel.Text = text;
	}

	public void SetHintText(string text)
	{
		CacheNodes();
		if (_hintLabel != null)
			_hintLabel.Text = text;
	}

	// ── Selected unit panel ──────────────────────────────────────────────────

	/// <summary>
	/// Show stats for the selected unit (player OR enemy).
	/// Pass mana = -1 to hide the mana row (use for enemies with no mana).
	/// </summary>
	public void ShowSelectedUnit(Unit unit, int mana)
	{
		CacheNodes();

		if (_unitNameLabel == null)
			return;

		if (unit == null)
		{
			_unitNameLabel.Text = "No Unit Selected";
			if (_healthLabel   != null) _healthLabel.Text   = "";
			if (_movementLabel != null) _movementLabel.Text = "";
			if (_manaLabel     != null) _manaLabel.Text     = mana >= 0 ? $"Mana: {mana}" : "";

			SetBar(_healthBar, 1, 0);
			SetBar(_moveBar,   1, 0);
			if (mana >= 0) SetBar(_manaBar, Mathf.Max(1, mana), mana);
			return;
		}

		bool isEnemy = !unit.IsPlayerControlled;

		_unitNameLabel.Text = isEnemy ? $"[Enemy] {unit.Name}" : unit.Name;

		if (_healthLabel   != null) _healthLabel.Text   = $"HP:   {unit.Stats.Health} / {unit.Stats.MaxHealth}";
		if (_movementLabel != null) _movementLabel.Text = isEnemy
			? $"Speed: {unit.Stats.BaseSpeed}"
			: $"Move: {unit.Stats.MovePoints} / {unit.Stats.BaseSpeed}";

		// Mana row: hide for enemies unless they have mana
		bool showMana = !isEnemy || unit.Stats.MaxMana > 0;
		if (_manaLabel != null)
			_manaLabel.Text = showMana ? $"Mana: {mana}" : "";

		SetBar(_healthBar, unit.Stats.MaxHealth, unit.Stats.Health);
		SetBar(_moveBar,   unit.Stats.BaseSpeed, isEnemy ? unit.Stats.BaseSpeed : unit.Stats.MovePoints);
		if (showMana) SetBar(_manaBar, Mathf.Max(1, unit.Stats.MaxMana), mana);
	}

	// ── Deployment mode ──────────────────────────────────────────────────────

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
	/// Rebuilds the enemy roster panel. Call after any enemy HP change or at the start of each turn.
	/// </summary>
	public void RefreshEnemyRoster(List<Unit> enemies)
	{
		CacheNodes();
		if (_enemyRosterBox == null) return;

		// Clear existing rows
		foreach (Node child in _enemyRosterBox.GetChildren())
			child.QueueFree();

		for (int i = 0; i < enemies.Count; i++)
		{
			var enemy = enemies[i];
			if (enemy == null) continue;

			// Outer HBox for this enemy row
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 6);

			// Clickable name button — emits EnemyButtonPressed(index)
			var btn = new Button();
			btn.Text        = enemy.Stats.IsAlive ? enemy.Name : $"[dead] {enemy.Name}";
			btn.Disabled    = !enemy.Stats.IsAlive;
			btn.CustomMinimumSize = new Vector2(90, 0);
			int capturedIndex = i;
			btn.Pressed += () => EmitSignal(SignalName.EnemyButtonPressed, capturedIndex);
			row.AddChild(btn);

			if (enemy.Stats.IsAlive)
			{
				// HP bar
				var bar = new ProgressBar();
				bar.MaxValue          = Mathf.Max(1, enemy.Stats.MaxHealth);
				bar.Value             = enemy.Stats.Health;
				bar.ShowPercentage    = false;
				bar.CustomMinimumSize = new Vector2(80, 14);
				bar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
				// Color the bar red for enemies
				var style = new StyleBoxFlat();
				style.BgColor = new Color(0.75f, 0.15f, 0.15f);
				bar.AddThemeStyleboxOverride("fill", style);
				row.AddChild(bar);

				// HP text
				var lbl = new Label();
				lbl.Text = $"{enemy.Stats.Health}/{enemy.Stats.MaxHealth}";
				lbl.CustomMinimumSize = new Vector2(44, 0);
				row.AddChild(lbl);
			}

			_enemyRosterBox.AddChild(row);
		}
	}

	// ── Player Unit Bar ──────────────────────────────────────────────────────

	/// <summary>
	/// Rebuilds the player unit bar at the bottom. Call after HP changes or unit selection changes.
	/// Highlights the currently selected unit.
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

			// Highlight selected unit
			if (unit == selectedUnit)
			{
				var style = new StyleBoxFlat();
				style.BgColor      = new Color(0.2f, 0.5f, 0.9f, 0.6f);
				style.BorderColor  = new Color(1f, 1f, 1f, 0.9f);
				style.SetBorderWidthAll(2);
				panel.AddThemeStyleboxOverride("panel", style);
			}

			var vbox = new VBoxContainer();
			vbox.AddThemeConstantOverride("separation", 2);

			// Unit name button
			var btn = new Button();
			btn.Text = unit.Stats.IsAlive ? unit.Name : $"[dead]";
			btn.Disabled = !unit.Stats.IsAlive;
			int capturedIndex = i;
			btn.Pressed += () => EmitSignal(SignalName.UnitButtonPressed, capturedIndex);
			vbox.AddChild(btn);

			// Mini HP bar
			var hpBar = new ProgressBar();
			hpBar.MaxValue          = Mathf.Max(1, unit.Stats.MaxHealth);
			hpBar.Value             = unit.Stats.Health;
			hpBar.ShowPercentage    = false;
			hpBar.CustomMinimumSize = new Vector2(80, 8);
			hpBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			var hpStyle = new StyleBoxFlat();
			hpStyle.BgColor = new Color(0.2f, 0.75f, 0.2f);
			hpBar.AddThemeStyleboxOverride("fill", hpStyle);
			vbox.AddChild(hpBar);

			// HP text
			var lbl = new Label();
			lbl.Text = $"HP {unit.Stats.Health}/{unit.Stats.MaxHealth}  MP {unit.Stats.MovePoints}/{unit.Stats.BaseSpeed}";
			lbl.HorizontalAlignment = HorizontalAlignment.Center;
			vbox.AddChild(lbl);

			panel.AddChild(vbox);
			_playerUnitBar.AddChild(panel);
		}
	}

	// ── Action Log ───────────────────────────────────────────────────────────

	private const int MaxLogLines = 6;
	private readonly Queue<string> _logLines = new();

	/// <summary>
	/// Appends a line to the combat action log (e.g. "Enemy_1 attacks Player_1 for 5 damage").
	/// </summary>
	public void AppendActionLog(string message)
	{
		CacheNodes();

		_logLines.Enqueue(message);
		while (_logLines.Count > MaxLogLines)
			_logLines.Dequeue();

		if (_actionLogLabel != null)
			_actionLogLabel.Text = string.Join("\n", _logLines);

		GD.Print($"[ActionLog] {message}");
	}

	/// <summary>Clears the action log (e.g. at start of player turn).</summary>
	public void ClearActionLog()
	{
		_logLines.Clear();
		if (_actionLogLabel != null)
			_actionLogLabel.Text = "";
	}

	// ── Deck / Graveyard counters ────────────────────────────────────────────

	/// <summary>
	/// Updates the deck and graveyard counter buttons and refreshes popup lists.
	/// </summary>
	public void RefreshDeckCounts(List<Card> library, List<Card> graveyard)
	{
		CacheNodes();

		if (_deckButton  != null) _deckButton.Text  = $"Deck: {library?.Count ?? 0}";
		if (_graveButton != null) _graveButton.Text = $"Grave: {graveyard?.Count ?? 0}";

		// Rebuild graveyard popup list
		if (_graveList != null)
		{
			_graveList.Clear();
			if (graveyard != null)
				foreach (var card in graveyard)
					_graveList.AddItem(card.CardName ?? card.TopHalf?.Name ?? "Unknown");
		}

		// Rebuild deck popup list
		if (_deckList != null)
		{
			_deckList.Clear();
			if (library != null)
				foreach (var card in library)
					_deckList.AddItem(card.CardName ?? card.TopHalf?.Name ?? "Unknown");
		}
	}

	// ── Helpers ──────────────────────────────────────────────────────────────

	/// <summary>Safely sets a ProgressBar's max and current value.</summary>
	private static void SetBar(ProgressBar bar, int max, int value)
	{
		if (bar == null) return;
		bar.MaxValue = Mathf.Max(1, max);
		bar.Value    = Mathf.Clamp(value, 0, max);
	}
}
