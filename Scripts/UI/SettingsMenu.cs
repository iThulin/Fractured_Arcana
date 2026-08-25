using Godot;

// ============================================================
// SettingsMenu.cs
//
// Purpose:        Settings overlay controller: resolution, window
//                 mode, vsync, UI scale, master volume. Reads
//                 current values from SettingsManager and pushes
//                 changes back to it. Uses FindChild() instead of
//                 hardcoded NodePath strings so scene-tree nesting
//                 changes don't break it.
// Layer:          UI
// Collaborators:  SettingsManager.cs (state + persistence),
//                 PauseMenu.cs (hosts this inline when opened
//                 from pause)
// See:            (none)
// ============================================================

/// <summary>Settings overlay controller. Two-way binds the visible widgets to <see cref="SettingsManager"/>. <see cref="ReturnScenePath"/> controls back-button behaviour: empty string or the inline sentinel means "QueueFree self", otherwise change scene.</summary>
public partial class SettingsMenu : Control
{
    /// <summary>Optional: scene to return to when Back is pressed. If empty, hides the menu.</summary>
    [Export] public string ReturnScenePath = "";

    private OptionButton _resDropdown;
    private OptionButton _modeDropdown;
    private CheckBox     _vsyncCheck;
    private HSlider      _uiScaleSlider;
    private Label        _uiScaleValue;
    private HSlider      _volumeSlider;
    private Label        _volumeValue;
    private Button       _backButton;

    public override void _Ready()
    {
        // Find children by name (recursive). This is robust to scene-tree nesting
        // changes, much better than hardcoded NodePath strings.
        _resDropdown    = FindChild("ResolutionDropdown",  true) as OptionButton;
        _modeDropdown   = FindChild("WindowModeDropdown",  true) as OptionButton;
        _vsyncCheck     = FindChild("VSyncCheck",          true) as CheckBox;
        _uiScaleSlider  = FindChild("UIScaleSlider",       true) as HSlider;
        _uiScaleValue   = FindChild("UIScaleValue",        true) as Label;
        _volumeSlider   = FindChild("VolumeSlider",        true) as HSlider;
        _volumeValue    = FindChild("VolumeValue",         true) as Label;
        _backButton     = FindChild("BackButton",          true) as Button;

        if (_resDropdown   == null) GD.PrintErr("[SettingsMenu] ResolutionDropdown not found");
        if (_modeDropdown  == null) GD.PrintErr("[SettingsMenu] WindowModeDropdown not found");
        if (_vsyncCheck    == null) GD.PrintErr("[SettingsMenu] VSyncCheck not found");
        if (_uiScaleSlider == null) GD.PrintErr("[SettingsMenu] UIScaleSlider not found");
        if (_volumeSlider  == null) GD.PrintErr("[SettingsMenu] VolumeSlider not found");
        if (_backButton    == null) GD.PrintErr("[SettingsMenu] BackButton not found");

        PopulateResolutionDropdown();
        PopulateWindowModeDropdown();
        ReadCurrentValues();
        WireUpSignals();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Populate dropdowns
    // ════════════════════════════════════════════════════════════════════════

    private void PopulateResolutionDropdown()
    {
        if (_resDropdown == null) return;
        _resDropdown.Clear();
        for (int i = 0; i < SettingsManager.SupportedResolutions.Count; i++)
        {
            var r = SettingsManager.SupportedResolutions[i];
            _resDropdown.AddItem($"{r.X} × {r.Y}", i);
        }
    }

    private void PopulateWindowModeDropdown()
    {
        if (_modeDropdown == null) return;
        _modeDropdown.Clear();
        _modeDropdown.AddItem("Windowed",   (int)DisplayServer.WindowMode.Windowed);
        _modeDropdown.AddItem("Borderless", (int)DisplayServer.WindowMode.Maximized);
        _modeDropdown.AddItem("Fullscreen", (int)DisplayServer.WindowMode.Fullscreen);
        _modeDropdown.AddItem("Exclusive",  (int)DisplayServer.WindowMode.ExclusiveFullscreen);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Sync UI to current settings
    // ════════════════════════════════════════════════════════════════════════

    private void ReadCurrentValues()
    {
        var sm = SettingsManager.Instance;
        if (sm == null)
        {
            GD.PrintErr("[SettingsMenu] SettingsManager.Instance is null. " +
                        "Did you add SettingsManager to Project Settings → Globals → Autoload?");
            return;
        }

        if (_resDropdown != null)
        {
            int resIdx = SettingsManager.SupportedResolutions.IndexOf(sm.Resolution);
            if (resIdx < 0) resIdx = 3;
            _resDropdown.Selected = resIdx;
        }

        if (_modeDropdown != null)
        {
            for (int i = 0; i < _modeDropdown.ItemCount; i++)
            {
                if (_modeDropdown.GetItemId(i) == (int)sm.WindowMode)
                {
                    _modeDropdown.Selected = i;
                    break;
                }
            }
        }

        if (_vsyncCheck    != null) _vsyncCheck.ButtonPressed = sm.VSync;
        if (_uiScaleSlider != null) _uiScaleSlider.Value      = sm.UIScale;
        if (_volumeSlider  != null) _volumeSlider.Value       = sm.MasterVolume;

        UpdateUIScaleLabel(sm.UIScale);
        UpdateVolumeLabel(sm.MasterVolume);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Signal wiring
    // ════════════════════════════════════════════════════════════════════════

    private void WireUpSignals()
    {
        if (_resDropdown   != null) _resDropdown.ItemSelected   += OnResolutionSelected;
        if (_modeDropdown  != null) _modeDropdown.ItemSelected  += OnWindowModeSelected;
        if (_vsyncCheck    != null) _vsyncCheck.Toggled         += OnVSyncToggled;
        if (_uiScaleSlider != null) _uiScaleSlider.ValueChanged += OnUIScaleChanged;
        if (_volumeSlider  != null) _volumeSlider.ValueChanged  += OnVolumeChanged;
        if (_backButton    != null) _backButton.Pressed         += OnBackPressed;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Signal handlers
    // ════════════════════════════════════════════════════════════════════════

    private void OnResolutionSelected(long index)
    {
        var res = SettingsManager.SupportedResolutions[(int)index];
        SettingsManager.Instance?.SetResolution(res);
    }

    private void OnWindowModeSelected(long index)
    {
        int id = _modeDropdown.GetItemId((int)index);
        SettingsManager.Instance?.SetWindowMode((DisplayServer.WindowMode)id);
    }

    private void OnVSyncToggled(bool pressed)
    {
        SettingsManager.Instance?.SetVSync(pressed);
    }

    private void OnUIScaleChanged(double value)
    {
        SettingsManager.Instance?.SetUIScale((float)value);
        UpdateUIScaleLabel((float)value);
    }

    private void OnVolumeChanged(double value)
    {
        SettingsManager.Instance?.SetMasterVolume((float)value);
        UpdateVolumeLabel((float)value);
    }

    private void OnBackPressed()
    {
        if (!string.IsNullOrEmpty(ReturnScenePath))
            GetTree().ChangeSceneToFile(ReturnScenePath);
        else
            QueueFree();
    }

    private void UpdateUIScaleLabel(float v)
    {
        if (_uiScaleValue != null) _uiScaleValue.Text = $"{v * 100f:0}%";
    }

    private void UpdateVolumeLabel(float v)
    {
        if (_volumeValue != null) _volumeValue.Text = $"{v * 100f:0}%";
    }
}
