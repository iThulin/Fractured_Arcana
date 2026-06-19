using Godot;
using System;

// ============================================================
// StagingControlPanel.cs
//
// Purpose:        Runtime control surface for CharacterStagingPreview.
//                 A docked panel of dropdowns / sliders / colour
//                 pickers that drive the staged scene live: theme,
//                 seed, school tint, raw body colour, and override
//                 toggles for sun, ambient, fog, and exposure that
//                 layer ON TOP of the theme atmosphere baseline.
// Layer:          Dev tooling
// Collaborators:  CharacterStagingPreview.cs (the staged scene),
//                 HexGridManager.cs (ThemeSun / WorldEnvironment),
//                 Combat_Environment.tres (the env being overridden).
//
// Godot 4.6.1 Mac+Windows rules honoured:
//   - UI built via CallDeferred(nameof(BuildUI)) from _Ready().
//   - No ScrollContainer as a direct TabContainer child (no tabs here).
//   - Root Control anchors set in the .tscn, not in code.
// ============================================================

/// <summary>
/// Live control panel for the character staging preview. Reads the override
/// values, applies them to the grid's sun and the shared Environment, and
/// proxies theme/seed/school changes to <see cref="CharacterStagingPreview"/>.
/// Override sliders sit on top of whatever ApplyThemeAtmosphere() set, so
/// regenerating re-establishes the theme baseline and the overrides re-apply.
/// </summary>
public partial class StagingControlPanel : Control
{
    [Export] public CharacterStagingPreview Preview;

    // ── Panel palette (dev tool — intentionally utilitarian) ────────────────
    private static readonly Color PanelBg = new Color(0.10f, 0.11f, 0.13f, 0.92f);
    private static readonly Color SectionFg = new Color(0.74f, 0.80f, 0.88f);
    private static readonly Color LabelFg = new Color(0.62f, 0.66f, 0.72f);

    // ── Live control references ─────────────────────────────────────────────
    private OptionButton _themePicker;
    private OptionButton _schoolPicker;
    private SpinBox _seedField;
    private ColorPickerButton _bodyColorPicker;

    private HSlider _sunEnergy;
    private ColorPickerButton _sunColor;
    private HSlider _sunPitch;
    private HSlider _sunYaw;

    private HSlider _ambientEnergy;
    private ColorPickerButton _ambientColor;

    private CheckBox _fogEnabled;
    private HSlider _fogDensity;
    private ColorPickerButton _fogColor;

    private HSlider _exposure;

    private Label _seedReadout;

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        if (Preview == null)
            Preview = GetNodeOrNull<CharacterStagingPreview>("../../CharacterStagingPreview");

        var panel = new PanelContainer();
        var sb = new StyleBoxFlat
        {
            BgColor = PanelBg,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 12,
            ContentMarginBottom = 12,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        panel.AddThemeStyleboxOverride("panel", sb);

        // Full-height strip pinned to the left edge — mirrors the grass tuner on
        // the right. Anchored top->bottom so the inner ScrollContainer gets the
        // whole screen height and only scrolls if controls genuinely overflow.
        panel.AnchorLeft = 0f;
        panel.AnchorTop = 0f;
        panel.AnchorRight = 0f;
        panel.AnchorBottom = 1f;
        panel.OffsetLeft = 16f;
        panel.OffsetTop = 16f;
        panel.OffsetRight = 356f;   // 340px wide, matches the tuner
        panel.OffsetBottom = -16f;
        AddChild(panel);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        panel.AddChild(scroll);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(col);

        Header(col, "Character Staging");

        // ── Map section ──────────────────────────────────────────────────
        Section(col, "Map");

        _themePicker = AddDropdown(col, "Theme", () =>
        {
            Preview?.SetTheme((HexGridManager.MapTheme)_themePicker.Selected);
        });
        foreach (var name in Enum.GetNames<HexGridManager.MapTheme>())
            _themePicker.AddItem(name);

        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 6);
        col.AddChild(MiniLabel("Seed"));
        _seedField = new SpinBox
        {
            MinValue = 0,
            MaxValue = int.MaxValue,
            Step = 1,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _seedField.ValueChanged += v => Preview?.SetSeed((int)v);
        seedRow.AddChild(_seedField);

        var rollBtn = new Button { Text = "Roll" };
        rollBtn.Pressed += () => Preview?.RandomizeSeed();
        seedRow.AddChild(rollBtn);
        col.AddChild(seedRow);

        _seedReadout = MiniLabel("Active seed: —");
        col.AddChild(_seedReadout);

        var regenBtn = new Button { Text = "Regenerate (new seed)" };
        regenBtn.Pressed += () => Preview?.RandomizeSeed();
        col.AddChild(regenBtn);

        // ── Character section ────────────────────────────────────────────
        Section(col, "Character");

        _schoolPicker = AddDropdown(col, "School tint", () =>
        {
            Preview?.SetSchoolTint((CardSchool)Enum.GetValues<CardSchool>().GetValue(_schoolPicker.Selected));
        });
        foreach (var name in Enum.GetNames<CardSchool>())
            _schoolPicker.AddItem(name);

        col.AddChild(MiniLabel("Body colour (raw)"));
        _bodyColorPicker = new ColorPickerButton
        {
            Color = Colors.White,
            CustomMinimumSize = new Vector2(0, 26),
        };
        _bodyColorPicker.ColorChanged += c => Preview?.SetBodyColor(c);
        col.AddChild(_bodyColorPicker);

        // ── Sun section ──────────────────────────────────────────────────
        Section(col, "Sun (override)");

        _sunEnergy = AddSlider(col, "Energy", 0f, 3f, 1f, ApplyLighting);
        _sunColor = AddColor(col, "Colour", new Color(0.95f, 0.95f, 1f), _ => ApplyLighting());
        _sunPitch = AddSlider(col, "Pitch°", -89f, -5f, -45f, ApplyLighting);
        _sunYaw = AddSlider(col, "Yaw°", -180f, 180f, 35f, ApplyLighting);

        // ── Ambient section ──────────────────────────────────────────────
        Section(col, "Ambient (override)");

        _ambientEnergy = AddSlider(col, "Energy", 0f, 2f, 0.4f, ApplyLighting);
        _ambientColor = AddColor(col, "Colour", new Color(0.6f, 0.6f, 0.8f), _ => ApplyLighting());

        // ── Fog section ──────────────────────────────────────────────────
        Section(col, "Fog (override)");

        _fogEnabled = new CheckBox { Text = "Enabled", ButtonPressed = true };
        _fogEnabled.Toggled += _ => ApplyLighting();
        col.AddChild(_fogEnabled);

        _fogDensity = AddSlider(col, "Density", 0f, 0.1f, 0.01f, ApplyLighting);
        _fogColor = AddColor(col, "Colour", new Color(0.7f, 0.7f, 0.9f), _ => ApplyLighting());

        // ── Camera/tonemap section ───────────────────────────────────────
        Section(col, "Exposure (override)");
        _exposure = AddSlider(col, "Tonemap exposure", 0.2f, 2f, 0.9f, ApplyLighting);

        // Sync the seed readout whenever the preview regenerates.
        if (Preview != null)
        {
            Preview.OnRegenerated += OnPreviewRegenerated;
            // Initialise field/readout from whatever the first generation produced.
            CallDeferred(nameof(SyncSeedFromPreview));
        }
    }

    private void OnPreviewRegenerated(int seed)
    {
        // Update without re-triggering SetSeed (guard via SetValueNoSignal).
        _seedField?.SetValueNoSignal(seed);
        if (_seedReadout != null)
            _seedReadout.Text = $"Active seed: {seed}";
        // Re-apply lighting overrides on top of the freshly-reset theme baseline.
        ApplyLighting();
    }

    private void SyncSeedFromPreview()
    {
        if (Preview == null)
            return;
        OnPreviewRegenerated(Preview.CurrentSeed);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Lighting application — overrides layered on the theme atmosphere baseline.
    // ════════════════════════════════════════════════════════════════════════

    private void ApplyLighting()
    {
        if (Preview?.Grid == null)
            return;

        // ── Sun ──────────────────────────────────────────────────────────
        var sun = Preview.Grid.ThemeSun;
        if (sun != null)
        {
            sun.LightEnergy = (float)_sunEnergy.Value;
            sun.LightColor = _sunColor.Color;

            // Orient via pitch (X) then yaw (Y). Rebuild the basis from Euler so
            // the slider values map cleanly without accumulating drift.
            sun.RotationDegrees = new Vector3(
                (float)_sunPitch.Value,
                (float)_sunYaw.Value,
                0f);
        }

        // ── Environment (shared resource from Combat_Environment.tres) ─────
        var we = Preview.Grid.ThemeWorldEnvironment;
        if (we?.Environment is Godot.Environment env)
        {
            env.AmbientLightEnergy = (float)_ambientEnergy.Value;
            env.AmbientLightColor = _ambientColor.Color;

            env.FogEnabled = _fogEnabled.ButtonPressed;
            env.FogDensity = (float)_fogDensity.Value;
            env.FogLightColor = _fogColor.Color;

            env.TonemapExposure = (float)_exposure.Value;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Tiny UI builders
    // ════════════════════════════════════════════════════════════════════════

    private void Header(VBoxContainer parent, string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", SectionFg);
        l.AddThemeFontSizeOverride("font_size", 18);
        parent.AddChild(l);
    }

    private void Section(VBoxContainer parent, string text)
    {
        var sep = new HSeparator();
        parent.AddChild(sep);
        var l = new Label { Text = text.ToUpper() };
        l.AddThemeColorOverride("font_color", SectionFg);
        l.AddThemeFontSizeOverride("font_size", 12);
        parent.AddChild(l);
    }

    private Label MiniLabel(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", LabelFg);
        l.AddThemeFontSizeOverride("font_size", 11);
        return l;
    }

    private OptionButton AddDropdown(VBoxContainer parent, string label, Action onChanged)
    {
        parent.AddChild(MiniLabel(label));
        var opt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        opt.ItemSelected += _ => onChanged();
        parent.AddChild(opt);
        return opt;
    }

    private HSlider AddSlider(VBoxContainer parent, string label, float min, float max,
                              float value, Action onChanged)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var name = MiniLabel(label);
        name.CustomMinimumSize = new Vector2(120, 0);
        row.AddChild(name);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Value = value,
            Step = (max - min) / 200.0,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 0),
        };

        var readout = MiniLabel(value.ToString("0.###"));
        readout.CustomMinimumSize = new Vector2(40, 0);

        slider.ValueChanged += v =>
        {
            readout.Text = ((float)v).ToString("0.###");
            onChanged();
        };

        row.AddChild(slider);
        row.AddChild(readout);
        parent.AddChild(row);
        return slider;
    }

    private ColorPickerButton AddColor(VBoxContainer parent, string label, Color initial,
                                       Action<Color> onChanged)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var name = MiniLabel(label);
        name.CustomMinimumSize = new Vector2(120, 0);
        row.AddChild(name);

        var picker = new ColorPickerButton
        {
            Color = initial,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 24),
        };
        picker.ColorChanged += c => onChanged(c);
        row.AddChild(picker);
        parent.AddChild(row);
        return picker;
    }
}
