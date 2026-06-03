using Godot;
using System.Collections.Generic;

// ============================================================
// HealthBarRoot.cs
//
// Purpose:        World-space (3D) health/mana/armor/shield bar
//                 hovering above a unit. Auto-billboards to the
//                 camera each frame.
//
//                 Default (compact) mode: HP bar only.
//                 Detail mode (hover/selected): mana bar, armor,
//                 AP pips, and status icons also visible.
//
// Layer:          UI
// Collaborators:  Unit.cs, UITheme.cs
// ============================================================

public partial class HealthBarRoot : Node3D
{
    [Export] public NodePath HealthFillPath  = "HealthFill";
    [Export] public NodePath ManaFillPath    = "ManaFill";
    [Export] public NodePath HealthTextPath  = "HealthText";
    [Export] public NodePath ManaTextPath    = "ManaText";
    [Export] public NodePath SpeedTextPath   = "SpeedText";

    // Back plates — kept as-is from scene
    [Export] public NodePath HealthBackPath  = "HealthBack";
    [Export] public NodePath ManaBackPath    = "ManaBack";

    // Armor / shield fills still exist but we hide them — values shown as text
    [Export] public NodePath ArmorFillPath   = "ArmorFill";
    [Export] public NodePath ShieldFillPath  = "ShieldFill";

    [Export] public float FullBarWidth = 1.6f;

    // ── HP gradient colours ─────────────────────────────────────────
    // Tinted further by isPlayer flag set at init
    private static readonly Color HpHigh   = new Color(0.20f, 0.85f, 0.25f); // green
    private static readonly Color HpMid    = new Color(0.95f, 0.80f, 0.10f); // yellow
    private static readonly Color HpLow    = new Color(0.90f, 0.20f, 0.15f); // red

    // Player bars are cooler/brighter; enemy bars are warmer/redder
    private static readonly Color PlayerTint = new Color(0.85f, 1.00f, 0.90f);
    private static readonly Color EnemyTint  = new Color(1.00f, 0.80f, 0.75f);

    // ── Cached nodes ───────────────────────────────────────────────
    private MeshInstance3D _healthFill;
    private MeshInstance3D _manaFill;
    private MeshInstance3D _healthBack;
    private MeshInstance3D _manaBack;
    private MeshInstance3D _armorFill;
    private MeshInstance3D _shieldFill;
    private Label3D        _healthText;
    private Label3D        _manaText;
    private Label3D        _speedText;
    private Label3D        _detailText;   // armor / AP / shield in one line
    private Node3D         _statusRow;
    private Camera3D       _camera;

    private float _healthFillOriginX;
    private float _manaFillOriginX;

    // Duplicated materials so we can tint per-unit
    private StandardMaterial3D _hpMat;
    private StandardMaterial3D _manaMat;

    private bool _isPlayer = true;
    private bool _isDetailed = false;

    // ── Status display map ─────────────────────────────────────────
    private static readonly Dictionary<string, (string symbol, Color color)> StatusDisplay = new()
    {
        { "burn",                   ("🔥", new Color(1.0f,  0.45f, 0.1f))  },
        { "frozen",                 ("❄",  new Color(0.4f,  0.8f,  1.0f))  },
        { "poisoned",               ("☠",  new Color(0.5f,  0.9f,  0.2f))  },
        { "stunned",                ("★",  new Color(1.0f,  0.95f, 0.3f))  },
        { "rooted",                 ("⊕",  new Color(0.55f, 0.85f, 0.3f))  },
        { "slowed",                 ("↓",  new Color(0.6f,  0.6f,  0.9f))  },
        { "weakened",               ("↘",  new Color(0.7f,  0.5f,  0.8f))  },
        { "haunted",                ("✦",  new Color(0.7f,  0.4f,  1.0f))  },
        { "bound",                  ("⛓",  new Color(0.75f, 0.65f, 0.4f))  },
        { "arcane_mark",            ("◈",  new Color(0.4f,  0.7f,  1.0f))  },
        { "chaining",               ("⚡",  new Color(0.9f,  0.85f, 0.2f))  },
        { "vigil",                  ("👁",  new Color(0.85f, 0.85f, 1.0f))  },
        { "undying_turn",           ("↺",  new Color(0.9f,  0.7f,  0.3f))  },
        { "undying_full_restore",   ("✙",  new Color(0.9f,  0.7f,  0.3f))  },
    };

    // ── Init ────────────────────────────────────────────────────────
    public override void _Ready()
    {
        _healthFill  = GetNodeOrNull<MeshInstance3D>(HealthFillPath);
        _manaFill    = GetNodeOrNull<MeshInstance3D>(ManaFillPath);
        _healthBack  = GetNodeOrNull<MeshInstance3D>(HealthBackPath);
        _manaBack    = GetNodeOrNull<MeshInstance3D>(ManaBackPath);
        _armorFill   = GetNodeOrNull<MeshInstance3D>(ArmorFillPath);
        _shieldFill  = GetNodeOrNull<MeshInstance3D>(ShieldFillPath);
        _healthText  = GetNodeOrNull<Label3D>(HealthTextPath);
        _manaText    = GetNodeOrNull<Label3D>(ManaTextPath);
        _speedText   = GetNodeOrNull<Label3D>(SpeedTextPath);
        _camera      = GetViewport().GetCamera3D();

        // Duplicate HP material so we can modulate per-unit
        if (_healthFill?.GetSurfaceOverrideMaterial(0) is StandardMaterial3D srcHp)
        {
            _hpMat = (StandardMaterial3D)srcHp.Duplicate();
            _healthFill.SetSurfaceOverrideMaterial(0, _hpMat);
        }
        else if (_healthFill != null)
        {
            _hpMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            _healthFill.SetSurfaceOverrideMaterial(0, _hpMat);
        }

        // Duplicate mana material
        if (_manaFill?.GetSurfaceOverrideMaterial(0) is StandardMaterial3D srcMp)
        {
            _manaMat = (StandardMaterial3D)srcMp.Duplicate();
            _manaFill.SetSurfaceOverrideMaterial(0, _manaMat);
        }

        // Detail text label — one line below mana bar for armor/AP/shield
        _detailText = new Label3D
        {
            Name        = "DetailText",
            FontSize    = 20,
            Billboard   = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            OutlineSize = 4,
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
            Position    = new Vector3(0f, -0.08f, 0.01f),
            Visible     = false,
        };
        AddChild(_detailText);

        // Status icon row
        _statusRow = new Node3D { Name = "StatusRow" };
        _statusRow.Position = new Vector3(0f, -0.32f, 0.01f);
        _statusRow.Visible = false;
        AddChild(_statusRow);

        if (_healthFill != null)
            _healthFillOriginX = _healthFill.Position.X;
        if (_manaFill != null)
            _manaFillOriginX = _manaFill.Position.X;

        // Start in compact mode
        SetDetailed(false);
    }

    public override void _Process(double delta)
    {
        if (_camera != null)
            LookAt(_camera.GlobalPosition, Vector3.Up, true);
    }

    // ── One-time setup called by Unit._Ready ────────────────────────
    /// <summary>
    /// Call once from Unit._Ready to establish player vs enemy tinting.
    /// Affects HP bar colour baseline and name label colour.
    /// </summary>
    public void Initialize(bool isPlayerControlled)
    {
        _isPlayer = isPlayerControlled;
    }

    // ── Compact / detail toggle ─────────────────────────────────────
    public void SetDetailed(bool detailed)
    {
        _isDetailed = detailed;

        // Mana bar — only in detail mode
        if (_manaFill  != null) _manaFill.Visible  = detailed;
        if (_manaBack  != null) _manaBack.Visible  = detailed;
        if (_manaText  != null) _manaText.Visible  = detailed;

        // Armor/shield fills always hidden — shown as text instead
        if (_armorFill  != null) _armorFill.Visible  = false;
        if (_shieldFill != null) _shieldFill.Visible = false;

        // Speed text never shown in bar (it's in the side panel)
        if (_speedText != null) _speedText.Visible = false;

        // Detail line and status row
        if (_detailText != null) _detailText.Visible = detailed;
        if (_statusRow  != null) _statusRow.Visible  = detailed;

        // HP text — show in detail mode, hide in compact
        if (_healthText != null) _healthText.Visible = detailed;
    }

    // ── HP ─────────────────────────────────────────────────────────
    public void SetHealth(int current, int max, int armor, int shield)
    {
        if (!IsInstanceValid(this)) return;

        float pct = max <= 0 ? 0f : Mathf.Clamp((float)current / max, 0f, 1f);
        ResizeBar(_healthFill, _healthFillOriginX, pct);

        // Gradient: green → yellow → red
        Color hpColor;
        if (pct > 0.5f)
            hpColor = HpHigh.Lerp(HpMid, (1f - pct) * 2f);
        else
            hpColor = HpMid.Lerp(HpLow, (0.5f - pct) * 2f);

        // Apply player/enemy tint
        Color tint = _isPlayer ? PlayerTint : EnemyTint;
        if (_hpMat != null)
            _hpMat.AlbedoColor = hpColor * tint;

        // HP text (only visible in detail mode)
        if (_healthText != null)
            _healthText.Text = $"{current}/{max}";

        // Detail line: armor + shield
        if (_detailText != null && _isDetailed)
            UpdateDetailText(armor, shield, -1, -1); // AP updated separately
    }

    // ── Mana ────────────────────────────────────────────────────────
    public void SetMana(int current, int max)
    {
        if (!IsInstanceValid(this)) return;
        float pct = max <= 0 ? 0f : Mathf.Clamp((float)current / max, 0f, 1f);
        ResizeBar(_manaFill, _manaFillOriginX, pct);
        if (_manaText != null)
            _manaText.Text = current > max ? $"{current}/{max}!" : $"{current}/{max}";
    }

    // ── Armor / Shield (kept for API compat, values shown in detail text) ──
    public void SetArmor(int current, int max) { /* values shown in detail text */ }
    public void SetShield(int current, int max) { /* values shown in detail text */ }
    public void SetSpeed(int current) { /* shown in side panel, not above unit */ }

    // ── AP pips ─────────────────────────────────────────────────────
    public void SetAP(int current, int max, int armor, int shield)
    {
        if (!IsInstanceValid(this) || _detailText == null || !_isDetailed) return;
        UpdateDetailText(armor, shield, current, max);
    }

    // ── Status icons ────────────────────────────────────────────────
    public void RefreshStatuses(Dictionary<string, int> statusEffects)
    {
        if (!IsInstanceValid(this) || _statusRow == null) return;

        foreach (Node child in _statusRow.GetChildren())
            child.QueueFree();

        if (statusEffects == null || statusEffects.Count == 0) return;

        var active = new List<(string symbol, Color color)>();
        foreach (var kvp in statusEffects)
        {
            if (kvp.Value <= 0) continue;
            if (StatusDisplay.TryGetValue(kvp.Key, out var d))
                active.Add((d.symbol, d.color));
        }

        if (active.Count == 0) return;

        float spacing   = 0.28f;
        float startX    = -(active.Count - 1) * spacing * 0.5f;

        for (int i = 0; i < active.Count; i++)
        {
            var icon = new Label3D
            {
                Name            = $"SI_{i}",
                Text            = active[i].symbol,
                FontSize        = 52,          // was 18 — needs to be large to render crisply
                Billboard       = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest     = true,
                Modulate        = active[i].color,
                OutlineSize     = 6,           // was 3 — scale with font size
                OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
                Position        = new Vector3(startX + i * spacing, 0f, 0f),
                PixelSize       = 0.004f,      // shrinks the world-space size down so it doesn't loom
            };
            _statusRow.AddChild(icon);
        }
    }

    // ── Private helpers ─────────────────────────────────────────────
    private void ResizeBar(MeshInstance3D fill, float originX, float pct)
    {
        if (fill == null) return;
        float offset = -(FullBarWidth * (1f - pct)) * 0.5f;
        fill.Scale = new Vector3(pct, 1f, 1f);
        fill.Position = new Vector3(
            originX + offset,
            fill.Position.Y,
            fill.Position.Z);
    }

    private void UpdateDetailText(int armor, int shield, int apCurrent, int apMax)
    {
        if (_detailText == null) return;

        var parts = new List<string>();

        if (armor > 0)  parts.Add($"[{armor}🛡]");
        if (shield > 0) parts.Add($"({shield}◈)");
        if (apCurrent >= 0 && apMax > 0)
        {
            // Pip string: filled and empty circles
            string pips = "";
            for (int i = 0; i < apMax; i++)
                pips += i < apCurrent ? "●" : "○";
            parts.Add(pips);
        }

        _detailText.Text = string.Join("  ", parts);
    }
}