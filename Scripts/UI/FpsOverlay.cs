using Godot;

// ============================================================
// FpsOverlay.cs
//
// Purpose:        A small frame-rate readout in the top-left corner, on
//                 every screen. Shows frames per second and the time one
//                 frame takes. Off by default.
// Layer:          UI (diagnostic)
// Collaborators:  SettingsManager.cs (creates this node, owns the ShowFps
//                 setting and persists it), SettingsMenu.cs (the checkbox)
// See:            docs/performance_targets_v1.md
//
// F3 toggles it anywhere in the game. The readout refreshes four times a
// second, so reading it does not itself cost frames.
// ============================================================

public partial class FpsOverlay : CanvasLayer
{
    private const int OverlayLayer = 127;          // above the HUD (90) and pause (100), below the council (128)
    private const double RefreshSeconds = 0.25;

    private Label _label;
    private double _sinceRefresh;
    private int _frames;

    public override void _Ready()
    {
        Layer = OverlayLayer;
        ProcessMode = ProcessModeEnum.Always;   // keeps counting while the game is paused

        _label = new Label
        {
            Name = "FpsLabel",
            Position = new Vector2(6f, 2f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _label.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        _label.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        _label.AddThemeColorOverride("font_outline_color", UITheme.TextOnLight);
        _label.AddThemeConstantOverride("outline_size", 4);
        AddChild(_label);

        SetShown(SettingsManager.Instance?.ShowFps ?? false);
    }

    /// <summary>Shows or hides the readout. Hidden, it does no work.</summary>
    public void SetShown(bool shown)
    {
        Visible = shown;
        SetProcess(shown);
        _sinceRefresh = 0.0;
        _frames = 0;
        if (shown && _label != null)
            _label.Text = "FPS";
    }

    public override void _Process(double delta)
    {
        _frames++;
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshSeconds)
            return;

        // Measured over the refresh window, so it follows the frame rate you are
        // getting now. The engine's own counter averages over a full second.
        double fps = _frames / _sinceRefresh;
        double ms = _sinceRefresh / _frames * 1000.0;
        _label.Text = $"FPS {fps:0}   {ms:0.0} ms";

        _sinceRefresh = 0.0;
        _frames = 0;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F3 })
        {
            SettingsManager.Instance?.SetShowFps(!(SettingsManager.Instance?.ShowFps ?? false));
            GetViewport().SetInputAsHandled();
        }
    }
}
