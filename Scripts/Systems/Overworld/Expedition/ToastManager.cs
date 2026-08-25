using Godot;

// ============================================================
// ToastManager.cs: stacking, auto-dismissing toast widget for
// the expedition HUD. Bottom-right, newest at the bottom.
// Toasts appear immediately (no fade-in dependency) and dismiss
// via a SceneTree timer + a short fade. Colour-coded by kind.
// ============================================================

/// <summary>Transient on-screen quest notifications. Add to the HUD CanvasLayer;
/// call Push().</summary>
public partial class ToastManager : Control
{
    private const int MaxToasts = 5;
    private VBoxContainer _stack;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _stack = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End,
            MouseFilter = MouseFilterEnum.Ignore,
            GrowHorizontal = GrowDirection.Begin,
            GrowVertical = GrowDirection.Begin,
        };
        _stack.AnchorLeft = 1f; _stack.AnchorTop = 1f;
        _stack.AnchorRight = 1f; _stack.AnchorBottom = 1f;
        _stack.OffsetLeft = -400; _stack.OffsetTop = -440;
        _stack.OffsetRight = -24; _stack.OffsetBottom = -24;
        _stack.AddThemeConstantOverride("separation", 8);
        AddChild(_stack);
    }

    public void Push(string text, QuestToastKind kind)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_stack == null) return;
        // Detached-node guard (2026-07-29): a caller can Push after a scene
        // change has removed this node from the tree (patrol-ambush dossier
        // announce raced CommitCombat's ChangeSceneToFile). GetTree() below
        // would NRE; a toast nobody can see is safe to drop instead.
        if (!IsInsideTree()) return;

        // Soft cap. Remove the oldest immediately (QueueFree alone is deferred).
        while (_stack.GetChildCount() >= MaxToasts)
        {
            var old = _stack.GetChild(0);
            _stack.RemoveChild(old);
            old.QueueFree();
        }

        Color accent = kind switch
        {
            QuestToastKind.Complete => UITheme.Gold,
            QuestToastKind.Unlock => UITheme.POINarrative,
            _ => UITheme.POINegotiation,
        };
        string prefix = kind switch
        {
            QuestToastKind.Complete => "★  ",
            QuestToastKind.Unlock => "❖  ",
            _ => "◈  ",
        };

        var panel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(340, 0),
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(UITheme.BgBase.R, UITheme.BgBase.G, UITheme.BgBase.B, 0.97f),
            BorderColor = accent,
            BorderWidthLeft = 4,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 9,
            ContentMarginBottom = 9,
            ShadowColor = new Color(0f, 0f, 0f, 0.5f),
            ShadowSize = 6,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var label = new Label
        {
            Text = prefix + text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
        label.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        panel.AddChild(label);

        _stack.AddChild(panel);

        // Visible immediately; dismiss via a reliable SceneTree timer + short fade.
        float life = kind == QuestToastKind.Complete ? 5.0f : 3.8f;
        var timer = GetTree().CreateTimer(life);
        timer.Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(panel)) return;
            var tw = panel.CreateTween();
            tw.TweenProperty(panel, "modulate:a", 0f, 0.5f);
            tw.TweenCallback(Callable.From(panel.QueueFree));
        };
    }
}
