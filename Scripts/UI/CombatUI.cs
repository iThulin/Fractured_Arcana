using Godot;

public partial class CombatUI : CanvasLayer
{
	[Signal] public delegate void ConfirmDeploymentPressedEventHandler();
	[Signal] public delegate void EndTurnPressedEventHandler();

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

	public override void _Ready()
	{
		CacheNodes();
		WireButtons();
	}

	private void CacheNodes()
	{
		if (_phaseLabel != null)
			return;

		_phaseLabel = GetNodeOrNull<Label>("PhasePanel/PhaseLabel");

		_unitNameLabel = GetNodeOrNull<Label>("SelectedUnitPanel/MarginContainer/VBoxContainer/UnitNameLabel");
		_healthLabel = GetNodeOrNull<Label>("SelectedUnitPanel/MarginContainer/VBoxContainer/HealthRow/HealthLabel");
		_movementLabel = GetNodeOrNull<Label>("SelectedUnitPanel/MarginContainer/VBoxContainer/MoveRow/MovementLabel");
		_manaLabel = GetNodeOrNull<Label>("SelectedUnitPanel/MarginContainer/VBoxContainer/ManaRow/ManaLabel");

		_healthBar = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/HealthRow/HealthBar");
		_moveBar = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/MoveRow/MoveBar");
		_manaBar = GetNodeOrNull<ProgressBar>("SelectedUnitPanel/MarginContainer/VBoxContainer/ManaRow/ManaBar");

		_hintLabel = GetNodeOrNull<Label>("HintPanel/HintLabel");

		_confirmDeploymentButton = GetNodeOrNull<Button>("ActionPanel/HBoxContainer/ConfirmDeploymentButton");
		_endTurnButton = GetNodeOrNull<Button>("ActionPanel/HBoxContainer/EndTurnButton");

		if (_phaseLabel == null) GD.PrintErr("CombatUI: PhaseLabel not found");
		if (_unitNameLabel == null) GD.PrintErr("CombatUI: UnitNameLabel not found");
		if (_healthLabel == null) GD.PrintErr("CombatUI: HealthLabel not found");
		if (_movementLabel == null) GD.PrintErr("CombatUI: MovementLabel not found");
		if (_manaLabel == null) GD.PrintErr("CombatUI: ManaLabel not found");
		if (_hintLabel == null) GD.PrintErr("CombatUI: HintLabel not found");
		if (_confirmDeploymentButton == null) GD.PrintErr("CombatUI: ConfirmDeploymentButton not found");
		if (_endTurnButton == null) GD.PrintErr("CombatUI: EndTurnButton not found");
	}

	private void WireButtons()
	{
		CacheNodes();

		if (_confirmDeploymentButton != null)
			_confirmDeploymentButton.Pressed += OnConfirmDeploymentButtonPressed;

		if (_endTurnButton != null)
			_endTurnButton.Pressed += OnEndTurnButtonPressed;
	}

	private void OnConfirmDeploymentButtonPressed()
	{
		EmitSignal(SignalName.ConfirmDeploymentPressed);
	}

	private void OnEndTurnButtonPressed()
	{
		EmitSignal(SignalName.EndTurnPressed);
	}

	public void SetPhaseText(string text)
	{
		CacheNodes();
		if (_phaseLabel == null)
			return;

		_phaseLabel.Text = text;
	}

	public void SetHintText(string text)
	{
		CacheNodes();
		if (_hintLabel == null)
			return;

		_hintLabel.Text = text;
	}

	public void ShowSelectedUnit(Unit unit, int mana)
	{
		CacheNodes();

		if (_unitNameLabel == null || _healthLabel == null || _movementLabel == null || _manaLabel == null)
			return;

		if (unit == null)
		{
			_unitNameLabel.Text = "No Unit Selected";
			_healthLabel.Text = "";
			_movementLabel.Text = "";
			_manaLabel.Text = $"Mana: {mana}";

			if (_healthBar != null)
			{
				_healthBar.MaxValue = 1;
				_healthBar.Value = 0;
			}

			if (_moveBar != null)
			{
				_moveBar.MaxValue = 1;
				_moveBar.Value = 0;
			}

			if (_manaBar != null)
			{
				_manaBar.MaxValue = Mathf.Max(1, mana);
				_manaBar.Value = mana;
			}

			return;
		}

		_unitNameLabel.Text = unit.Name;
		_healthLabel.Text = $"HP: {unit.Stats.Health} / {unit.Stats.MaxHealth}";
		_movementLabel.Text = $"Move: {unit.Stats.MovePoints} / {unit.Stats.BaseSpeed}";
		_manaLabel.Text = $"Mana: {mana}";

		if (_healthBar != null)
		{
			_healthBar.MaxValue = Mathf.Max(1, unit.Stats.MaxHealth);
			_healthBar.Value = unit.Stats.Health;
		}

		if (_moveBar != null)
		{
			_moveBar.MaxValue = Mathf.Max(1, unit.Stats.BaseSpeed);
			_moveBar.Value = unit.Stats.MovePoints;
		}

		if (_manaBar != null)
		{
			_manaBar.MaxValue = Mathf.Max(1, unit.Stats.MaxMana);
			_manaBar.Value = mana;
		}
	}

	public void SetDeploymentMode(bool isDeployment)
	{
		CacheNodes();

		if (_confirmDeploymentButton != null)
			_confirmDeploymentButton.Visible = isDeployment;

		if (_endTurnButton != null)
			_endTurnButton.Visible = !isDeployment;
	}
}
