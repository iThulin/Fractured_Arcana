using Godot;

public partial class HealthBarRoot : Node3D
{
    [Export] public NodePath SubViewportPath = "SubViewport";
    [Export] public NodePath BarPath = "HealthBarRoot/SubViewport/UI/HealthBar";
    [Export] public NodePath SpritePath = "Sprite3D";
    [Export] public NodePath ManaBarPath = "SubViewport/UI/ManaBar";
private Range _manaBar;

    private SubViewport _vp;
    private Range _bar; // ProgressBar/TextureProgressBar derive from Range
    private Sprite3D _sprite;

public override void _Ready()
{
    _vp = GetNodeOrNull<SubViewport>(SubViewportPath);
    _bar = GetNodeOrNull<Range>(BarPath);
    _sprite = GetNodeOrNull<Sprite3D>(SpritePath);
    _manaBar = GetNodeOrNull<Range>(ManaBarPath);

    if (_vp == null || _bar == null || _sprite == null || _manaBar == null)
    {
        GD.PrintErr($"HealthBarRoot: Missing nodes. vp={_vp!=null}, bar={_bar!=null}, sprite={_sprite!=null}, manaBar={_manaBar!=null}");
        return;
    }

    var ui = _vp.GetNodeOrNull<Control>("UI");
    if (ui != null) ui.MouseFilter = Control.MouseFilterEnum.Ignore;

    if (_bar is Control barCtrl)
        barCtrl.MouseFilter = Control.MouseFilterEnum.Ignore;

    // Make sure the viewport is actually producing pixels
    _vp.TransparentBg = true;
    _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
    if (_vp.Size == Vector2I.Zero) _vp.Size = new Vector2I(256, 32);

    _sprite.Texture = _vp.GetTexture();

    // Make sure the sprite is visible and faces the camera
    _sprite.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
    _sprite.PixelSize = 0.01f;         // tune: smaller = bigger in world
    _sprite.Centered = true;

    // Debug: force it to show something
    SetHealth(10, 10);
}

    public void SetHealth(int current, int max)
    {
        if (_bar == null) return;
        _bar.MaxValue = max <= 0 ? 1 : max;
        _bar.Value = Mathf.Clamp(current, 0, (int)_bar.MaxValue);
    }

    public void SetMana(int current, int max)
    {
        if (_manaBar == null) return;
        _manaBar.MaxValue = max <= 0 ? 1 : max;
        _manaBar.Value = Mathf.Clamp(current, 0, (int)_manaBar.MaxValue);
    }
}
