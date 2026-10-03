using Godot;
using System.Collections.Generic;

// ============================================================
// HealthBarRoot.cs
//
// Purpose:        Anchor for a unit's nameplate. The node sits
//                 above the unit in the scene (Unit.tscn and
//                 TargetDummy.tscn place it at y = 2); each frame it
//                 projects that point to the screen and hangs a
//                 UnitNameplate (screen-space Control) from it.
//
//                 v2 (2026-09-28): replaces the world-space quad bars
//                 and Label3D symbol line. That design could not show
//                 more than a handful of conditions, told armor from
//                 shield only by glyphs the shipped fonts do not
//                 contain, and only showed conditions on hover.
//
//                 Data is PULLED from the parent Unit at a fixed
//                 rate rather than trusted from the push calls, so a
//                 code path that changes armor or a status and forgets
//                 RefreshHealthBar still shows correctly within 0.1 s.
//                 The push API below is kept whole so no caller has to
//                 change: pushes just mark the plate dirty (and feed
//                 the values when the parent is not a Unit).
//
//                 The unit's own NameLabel, IntentIndicator and
//                 CoverMarker Label3Ds are mirrored into the plate and
//                 hidden from the camera by clearing their render
//                 layers. Unit.cs keeps writing their text, colour and
//                 visibility exactly as before.
//
// Layer:          UI
// Collaborators:  Unit.cs, UnitNameplate.cs, StatusCatalog.cs
// ============================================================

public partial class HealthBarRoot : Node3D
{
    // Legacy scene paths, kept so the scene's child meshes can be hidden.
    [Export] public NodePath HealthFillPath  = "HealthFill";
    [Export] public NodePath ManaFillPath    = "ManaFill";
    [Export] public NodePath HealthTextPath  = "HealthText";
    [Export] public NodePath ManaTextPath    = "ManaText";
    [Export] public NodePath SpeedTextPath   = "SpeedText";
    [Export] public NodePath HealthBackPath  = "HealthBack";
    [Export] public NodePath ManaBackPath    = "ManaBack";
    [Export] public NodePath ArmorFillPath   = "ArmorFill";
    [Export] public NodePath ShieldFillPath  = "ShieldFill";
    [Export] public float FullBarWidth = 1.6f;

    /// <summary>World-space lift above this node where the plate's bottom edge hangs.</summary>
    [Export] public float AnchorLift = 0.05f;

    /// <summary>Height above the unit's origin used to decide whether the unit
    /// itself is on screen (the plate anchor can leave the view first when the
    /// camera is close).</summary>
    [Export] public float BodyHeight = 0.6f;

    /// <summary>Seconds between pulls from the unit when nothing was pushed.</summary>
    private const float PullInterval = 0.1f;

    private Unit _unit;
    private UnitNameplate _plate;
    private bool _isPlayer = true;
    private bool _isDetailed = false;
    private bool _dirty = true;
    private float _pullTimer = 0f;

    // Mirrored Label3Ds (created by Unit, some lazily, so looked up until found).
    private Label3D _nameLabel;
    private Label3D _intentLabel;
    private Label3D _coverLabel;

    // Pushed values: the fallback source when the parent is not a Unit.
    private int _hp, _maxHp, _withered, _armor, _shield, _cover;
    private int _mana, _maxMana, _ap = -1, _maxAp, _edge, _maxEdge;
    private Dictionary<string, int> _pushedStatuses;

    // ── Init ────────────────────────────────────────────────────────
    public override void _Ready()
    {
        _unit = GetParent() as Unit;
        // Place the plate after the camera has moved this frame, or it trails by one frame.
        ProcessPriority = 100;

        // Retire the world-space bar: every mesh, label and sprite under this node.
        foreach (Node child in GetChildren())
        {
            if (child is Node3D n3)
                n3.Visible = false;
        }

        var layer = NameplateLayer.For(this);
        if (layer != null)
        {
            _plate = new UnitNameplate { Name = $"Plate_{GetParent()?.Name}", Visible = false };
            layer.AddChild(_plate);
        }
    }

    public override void _ExitTree()
    {
        if (_plate != null && IsInstanceValid(_plate))
            _plate.QueueFree();
        _plate = null;
    }

    public override void _Process(double delta)
    {
        if (_plate == null || !IsInstanceValid(_plate))
            return;

        var vp = GetViewport();
        var cam = vp?.GetCamera3D();
        Vector3 anchor = GlobalPosition + new Vector3(0f, AnchorLift, 0f);
        Vector3 body = _unit != null ? _unit.GlobalPosition + new Vector3(0f, BodyHeight, 0f) : anchor;
        bool alive = _unit == null || (_unit.Stats != null && _unit.Stats.IsAlive && !_unit.IsDeathQueued);
        bool show = cam != null && IsVisibleInTree() && alive && !cam.IsPositionBehind(body);
        Rect2 screen = vp != null ? vp.GetVisibleRect() : new Rect2();
        Vector2 bodyScreen = show ? cam.UnprojectPosition(body) : Vector2.Zero;
        // The plate follows the UNIT onto and off the screen. Its anchor above the
        // head may leave the view first at close zoom; PlaceAt then pins it to the edge.
        if (show && !screen.HasPoint(bodyScreen))
            show = false;
        if (!show)
        {
            _plate.Visible = false;
            return;
        }

        _pullTimer -= (float)delta;
        if (_dirty || _pullTimer <= 0f)
        {
            _dirty = false;
            _pullTimer = PullInterval;
            _plate.SetData(BuildData());
        }

        _plate.Visible = true;
        Vector2 anchorScreen = cam.IsPositionBehind(anchor) ? bodyScreen : cam.UnprojectPosition(anchor);
        _plate.PlaceAt(anchorScreen, screen);

        // Nearer units draw over farther ones; the inspected unit over everything.
        float dist = cam.GlobalPosition.DistanceTo(anchor);
        int z = Mathf.Clamp(2000 - Mathf.RoundToInt(dist * 20f), 0, 2000);
        _plate.BaseZ = z + (_isDetailed ? 2000 : 0);
    }

    // ── Push API (unchanged signatures) ─────────────────────────────

    /// <summary>Call once from Unit._Ready to establish player vs enemy tinting.</summary>
    public void Initialize(bool isPlayerControlled)
    {
        _isPlayer = isPlayerControlled;
        _dirty = true;
    }

    public void SetDetailed(bool detailed)
    {
        _isDetailed = detailed;
        _dirty = true;
    }

    public void SetHealth(int current, int max, int armor, int shield, int withered = 0)
    {
        _hp = current; _maxHp = max; _armor = armor; _shield = shield; _withered = withered;
        _dirty = true;
    }

    public void SetMana(int current, int max)
    {
        _mana = current; _maxMana = max;
        _dirty = true;
    }

    public void SetArmor(int current, int max) { _armor = current; _dirty = true; }
    public void SetShield(int current, int max) { _shield = current; _dirty = true; }
    public void SetSpeed(int current) { /* speed lives in the side panel */ }

    public void SetAP(int current, int max, int armor, int shield, int cover = 0)
    {
        _ap = current; _maxAp = max; _armor = armor; _shield = shield; _cover = cover;
        _dirty = true;
    }

    /// <summary>Martial Edge (spec v1 §3). max 0 means no Edge economy.</summary>
    public void SetEdge(int current, int max)
    {
        _edge = current; _maxEdge = max;
        _dirty = true;
    }

    public void RefreshStatuses(Dictionary<string, int> statusEffects)
    {
        _pushedStatuses = statusEffects;
        _dirty = true;
    }

    /// <summary>R22 damage preview: flash the span of the HP bar the predicted
    /// hit removes. Red = clean prediction, amber = could be invalidated.</summary>
    public void ShowDamagePreview(int current, int max, int withered, int hpLoss, bool warn)
    {
        if (_plate == null || !IsInstanceValid(_plate))
            return;
        if (hpLoss <= 0 || current <= 0) { _plate.ClearDamagePreview(); return; }
        _hp = current; _maxHp = max; _withered = withered;
        _dirty = true;
        _plate.SetDamagePreview(hpLoss, warn);
    }

    public void HideDamagePreview()
    {
        if (_plate != null && IsInstanceValid(_plate))
            _plate.ClearDamagePreview();
    }

    // ── Pull ────────────────────────────────────────────────────────

    private NameplateData BuildData()
    {
        var d = new NameplateData { Detailed = _isDetailed };
        var u = _unit;

        if (u != null && IsInstanceValid(u) && u.Stats != null)
        {
            var s = u.Stats;
            d.Side = u.TeamId == 0 ? 0 : (u.TeamId == 1 ? 1 : 2);
            d.Hp = s.Health;
            d.MaxHp = s.MaxHealth;
            d.Withered = s.WitheredMaxHp;
            d.Shield = s.Shield;
            d.Armor = s.Armor;
            d.BraceArmor = u.BraceArmor;
            d.Cover = s.CoverArmor;
            d.Mana = s.Mana;
            d.MaxMana = s.MaxMana;
            if (u.IsPlayerControlled && u.MaxActionPoints > 0)
            {
                d.Ap = u.CurrentActionPoints;
                d.MaxAp = u.MaxActionPoints;
            }
            d.Edge = u.Edge;
            d.MaxEdge = u.MaxEdge;
            d.Conditions = StatusCatalog.Collect(u);
        }
        else
        {
            d.Side = _isPlayer ? 0 : 1;
            d.Hp = _hp; d.MaxHp = _maxHp; d.Withered = _withered;
            d.Shield = _shield; d.Armor = _armor; d.Cover = _cover;
            d.Mana = _mana; d.MaxMana = _maxMana;
            if (_ap >= 0 && _maxAp > 0) { d.Ap = _ap; d.MaxAp = _maxAp; }
            d.Edge = _edge; d.MaxEdge = _maxEdge;
            if (_pushedStatuses != null)
            {
                foreach (var kvp in _pushedStatuses)
                {
                    if (kvp.Value <= 0) continue;
                    int count = kvp.Value >= StatusCatalog.PermanentThreshold ? 0 : kvp.Value;
                    d.Conditions.Add(new UnitCondition(kvp.Key, StatusCatalog.Get(kvp.Key), count));
                }
            }
        }

        MirrorLabels(d);
        return d;
    }

    private void MirrorLabels(NameplateData d)
    {
        Node host = GetParent();
        if (host == null)
            return;

        _nameLabel ??= Adopt(host.GetNodeOrNull<Label3D>("NameLabel"));
        _intentLabel ??= Adopt(host.GetNodeOrNull<Label3D>("IntentIndicator"));
        _coverLabel ??= Adopt(host.GetNodeOrNull<Label3D>("CoverMarker"));

        // Name, from the unit itself. Its NameLabel is written once in _Ready,
        // before the spawner renames the node, so it can still read "Unit".
        // The one deliberate label override is the spirit's death-record flash.
        string name;
        if (_unit != null)
        {
            name = !string.IsNullOrEmpty(_unit.DisplayName) ? _unit.DisplayName : _unit.Name.ToString();
            if (_unit.IsSpirit && _nameLabel != null && !string.IsNullOrEmpty(_nameLabel.Text)
                && _nameLabel.Text != "Unit")
                name = _nameLabel.Text;
        }
        else
        {
            name = _nameLabel != null ? _nameLabel.Text : host.Name.ToString();
        }
        d.Name = name ?? "";
        if (_unit != null && _unit.IsSpirit && _nameLabel != null)
            d.NameColor = _nameLabel.Modulate with { A = 1f };   // ghost tint from the death record
        else
            d.NameColor = d.Side == 0 ? UITheme.PlateNameAlly : (d.Side == 1 ? UITheme.PlateNameEnemy : UITheme.TextSecondary);

        // Intent: the label's visibility is CombatManager's show/hide decision; the
        // content comes from the intent itself, not from parsing the label glyphs.
        var intent = _unit?.CurrentIntent;
        if (intent != null && _intentLabel != null && IsInstanceValid(_intentLabel) && _intentLabel.Visible)
        {
            d.HasIntent = true;
            d.IntentKind = intent.Kind;
            d.IntentRevealed = intent.Revealed;
            d.IntentValue = intent.Value;
            d.IntentShoveTiles = intent.ShoveTiles;
            d.IntentElement = intent.ImbueElement == TileElementType.None ? "" : intent.ImbueElement.ToString();
            d.Staggered = _unit.IsStaggered;
            d.Poise = _unit.Poise;
            d.MaxPoise = _unit.MaxPoise;
            d.Openings = _unit.Openings;
            d.Postponed = _unit.PostponedTurns;
            d.IntentTiming = _unit.AttackTiming;
            d.IntentReaction = intent.PredictedReaction;
            d.IntentNext = intent.NextRevealed ? NextKindWord(intent.NextKind) : "";
            d.IntentNextNote = intent.NextRevealed ? (intent.NextNote ?? "") : "";
            string text = _intentLabel.Text ?? "";
            int nl = text.IndexOf('\n');
            d.IntentMarkers = nl >= 0 ? text.Substring(nl + 1).Trim() : "";

            // The channel is already the intent pill; its status chip would repeat it.
            d.Conditions.RemoveAll(c => c.Key == "wizard_charging");
        }

        if (_coverLabel != null && IsInstanceValid(_coverLabel) && _coverLabel.Visible)
        {
            d.CoverTag = _coverLabel.Text ?? "";
            d.CoverTagColor = _coverLabel.Modulate with { A = 1f };
        }
    }

    /// <summary>Short word for a forecast next beat (nameplate "THEN ..." tag).</summary>
    private static string NextKindWord(IntentKind kind) => kind switch
    {
        IntentKind.Attack       => "ATTACK",
        IntentKind.RangedAttack => "SHOT",
        IntentKind.Channel      => "CHANNEL",
        IntentKind.Release      => "BLAST",
        IntentKind.Guard        => "GUARD",
        IntentKind.Imbue        => "IMBUE",
        IntentKind.Shove        => "SHOVE",
        _                       => "?",
    };

    /// <summary>Takes a Label3D off every camera without touching its Visible
    /// flag, which the Unit still drives and this node reads.</summary>
    private static Label3D Adopt(Label3D label)
    {
        if (label == null || !IsInstanceValid(label))
            return null;
        label.Layers = 0;
        return label;
    }
}
