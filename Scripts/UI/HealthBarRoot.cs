using Godot;

public partial class HealthBarRoot : Node3D
{
    [Export] public NodePath HealthFillPath = "HealthFill";
    [Export] public NodePath ManaFillPath = "ManaFill";
    [Export] public NodePath ArmorFillPath = "ArmorFill";
    [Export] public NodePath ShieldFillPath = "ShieldFill";
    [Export] public NodePath HealthTextPath = "HealthText";
    [Export] public NodePath ManaTextPath = "ManaText";
    [Export] public NodePath SpeedTextPath = "SpeedText";

    [Export] public float FullBarWidth = 1.6f;

    private Node3D _healthFill;
    private Node3D _manaFill;
    private Node3D _armorFill;
    private Node3D _shieldFill;
    private Label3D _healthText;
    private Label3D _manaText;
    private Label3D _speedText;
    private Camera3D _camera;

    private float _healthFillOriginX;
    private float _manaFillOriginX;
    private float _armorFillOriginX;
    private float _shieldFillOriginX;

    public override void _Ready()
    {
        _healthFill = GetNodeOrNull<Node3D>(HealthFillPath);
        _manaFill = GetNodeOrNull<Node3D>(ManaFillPath);
        _armorFill = GetNodeOrNull<Node3D>(ArmorFillPath);
        _shieldFill = GetNodeOrNull<Node3D>(ShieldFillPath);
        _healthText = GetNodeOrNull<Label3D>(HealthTextPath);
        _manaText = GetNodeOrNull<Label3D>(ManaTextPath);
        _speedText = GetNodeOrNull<Label3D>(SpeedTextPath);
        _camera = GetViewport().GetCamera3D();

        if (_healthFill != null) _healthFillOriginX = _healthFill.Position.X;
        if (_manaFill != null) _manaFillOriginX = _manaFill.Position.X;
        if (_armorFill != null) _armorFillOriginX = _armorFill.Position.X;
        if (_shieldFill != null) _shieldFillOriginX = _shieldFill.Position.X;
    }

    public override void _Process(double delta)
    {
        if (_camera != null)
            LookAt(_camera.GlobalPosition, Vector3.Up, true);
    }

    // ── Shared bar resize helper ────────────────────────────────
    private void ResizeBar(Node3D fill, float originX, float pct)
    {
        if (fill == null) return;
        float offset = -(FullBarWidth * (1f - pct)) * 0.5f;
        fill.Scale = new Vector3(pct, 1f, 1f);
        fill.Position = new Vector3(
            originX + offset,
            fill.Position.Y,
            fill.Position.Z
        );
    }

    // ── Public setters ──────────────────────────────────────────
    public void SetHealth(int current, int max, int armor, int shield)
    {
        if (!IsInstanceValid(this)) return;
        float pct = max <= 0 ? 0f : Mathf.Clamp((float)current / max, 0f, 1f);
        ResizeBar(_healthFill, _healthFillOriginX, pct);

        if (_healthText != null)
        {
            string text = $"{current}/{max}";
            if (armor > 0) text += $" [{armor}]";
            if (shield > 0) text += $" ({shield})";
            _healthText.Text = text;
        }
    }

    public void SetMana(int current, int max)
    {
        if (!IsInstanceValid(this)) return;
        float pct = max <= 0 ? 0f : Mathf.Clamp((float)current / max, 0f, 1f);
        ResizeBar(_manaFill, _manaFillOriginX, pct);
        if (_manaText != null)
            _manaText.Text = $"MP {current}/{max}";
    }

    public void SetArmor(int current, int max)
    {
        if (!IsInstanceValid(this) || _armorFill == null) return;
        bool hasArmor = current > 0;
        _armorFill.Visible = hasArmor;
        if (hasArmor)
        {
            float pct = max <= 0 ? 0f : Mathf.Clamp((float)current / max, 0f, 1f);
            ResizeBar(_armorFill, _armorFillOriginX, pct);
        }
    }

    public void SetShield(int current, int max)
    {
        if (!IsInstanceValid(this) || _shieldFill == null) return;
        bool hasShield = current > 0;
        _shieldFill.Visible = hasShield;
        if (hasShield)
        {
            float pct = max <= 0 ? 0f : Mathf.Clamp((float)current / max, 0f, 1f);
            ResizeBar(_shieldFill, _shieldFillOriginX, pct);
        }
    }

    public void SetSpeed(int current)
    {
        if (!IsInstanceValid(this) || _speedText == null) return;
        _speedText.Visible = current > 0;
        _speedText.Text = current > 0 ? $"Spd {current}" : "";
    }
}