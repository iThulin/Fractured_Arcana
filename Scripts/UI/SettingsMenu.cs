using Godot;

/// <summary>
/// Controller for the settings menu UI.
/// 
/// Hooks up all the controls to SettingsManager. Reads current values on _Ready,
/// pushes changes back through SettingsManager so they apply + persist.
/// 
/// Expects a scene structure matching SettingsMenu.tscn (paths below).
/// </summary>
public partial class SettingsMenu : Control
{
    [Export] public NodePath ResolutionDropdownPath = "Margin/VBox/Settings/ResolutionRow/ResolutionDropdown";
    [Export] public NodePath WindowModeDropdownPath  = "Margin/VBox/Settings/WindowModeRow/WindowModeDropdown";
    [Export] public NodePath VSyncCheckPath          = "Margin/VBox/Settings/VSyncRow/VSyncCheck";
    [Export] public NodePath UIScaleSliderPath       = "Margin/VBox/Settings/UIScaleRow/UIScaleSlider";
    [Export] public NodePath UIScaleValueLabelPath   = "Margin/VBox/Settings/UIScaleRow/UIScaleValue";
    [Export] public NodePath VolumeSliderPath        = "Margin/VBox/Settings/VolumeRow/VolumeSlider";
    [Export] public NodePath VolumeValueLabelPath    = "Margin/VBox/Settings/VolumeRow/VolumeValue";
    [Export] public NodePath BackButtonPath          = "Margin/VBox/TopBar/BackButton";

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
        _resDropdown    = GetNode<OptionButton>(ResolutionDropdownPath);
        _modeDropdown   = GetNode<OptionButton>(WindowModeDropdownPath);
        _vsyncCheck     = GetNode<CheckBox>(VSyncCheckPath);
        _uiScaleSlider  = GetNode<HSlider>(UIScaleSliderPath);
        _uiScaleValue   = GetNode<Label>(UIScaleValueLabelPath);
        _volumeSlider   = GetNode<HSlider>(VolumeSliderPath);
        _volumeValue    = GetNode<Label>(VolumeValueLabelPath);
        _backButton     = GetNode<Button>(BackButtonPath);

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
        _resDropdown.Clear();
        for (int i = 0; i < SettingsManager.SupportedResolutions.Count; i++)
        {
            var r = SettingsManager.SupportedResolutions[i];
            _resDropdown.AddItem($"{r.X} × {r.Y}", i);
        }
    }

    private void PopulateWindowModeDropdown()
    {
        _modeDropdown.Clear();
        _modeDropdown.AddItem("Windowed",   (int)DisplayServer.WindowMode.Windowed);
        _modeDropdown.AddItem("Borderless", (int)DisplayServer.WindowMode.Maximized); // borderless-ish; see note
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

        // Resolution
        int resIdx = SettingsManager.SupportedResolutions.IndexOf(sm.Resolution);
        if (resIdx < 0) resIdx = 3; // default to 1920x1080 if current res isn't in the list
        _resDropdown.Selected = resIdx;

        // Window mode
        for (int i = 0; i < _modeDropdown.ItemCount; i++)
        {
            if (_modeDropdown.GetItemId(i) == (int)sm.WindowMode)
            {
                _modeDropdown.Selected = i;
                break;
            }
        }

        _vsyncCheck.ButtonPressed = sm.VSync;
        _uiScaleSlider.Value      = sm.UIScale;
        _volumeSlider.Value       = sm.MasterVolume;

        UpdateUIScaleLabel(sm.UIScale);
        UpdateVolumeLabel(sm.MasterVolume);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Signal wiring
    // ════════════════════════════════════════════════════════════════════════

    private void WireUpSignals()
    {
        _resDropdown.ItemSelected   += OnResolutionSelected;
        _modeDropdown.ItemSelected  += OnWindowModeSelected;
        _vsyncCheck.Toggled         += OnVSyncToggled;
        _uiScaleSlider.ValueChanged += OnUIScaleChanged;
        _volumeSlider.ValueChanged  += OnVolumeChanged;
        _backButton.Pressed         += OnBackPressed;
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

    private void UpdateUIScaleLabel(float v) => _uiScaleValue.Text = $"{v * 100f:0}%";
    private void UpdateVolumeLabel(float v)  => _volumeValue.Text  = $"{v * 100f:0}%";
}
