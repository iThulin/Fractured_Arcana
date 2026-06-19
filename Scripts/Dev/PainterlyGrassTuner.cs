using Godot;
using System.Collections.Generic;

// ============================================================
// PainterlyGrassTuner.cs   (dev-only)  — v5 params
//
// Live tuner for painterly_grass.gdshader. Drop it into the Character
// Staging UI. On Refresh() it finds the generated grass field (group
// "painterly_grass"), reads its MaterialOverride ShaderMaterial, and
// exposes sliders / colour pickers / toggles that write shader params
// live. Drag a slider and the board updates instantly.
//
// Bind by dragging painterly_grass.tres into the Material export slot.
// "Print values to Output" dumps current settings to bake into the .tres.
// ============================================================

public partial class PainterlyGrassTuner : PanelContainer
{
    [Export] public NodePath GrassFieldPath;
    [Export] public ShaderMaterial Material;

    private const string PainterlyGrassGroup = "painterly_grass";

    private ShaderMaterial _mat;
    private readonly List<System.Action> _syncers = new();

    private static readonly (string Name, string Label, float Min, float Max, float Def)[] Floats =
    {
        ("wind_bend",              "Wind Bend",          0f,    1f,    0.15f),
        ("wind_amplitude",         "Wind Amplitude",     0f,    1f,    0.40f),
        ("wave_scale",             "Wave Scale",         0.01f, 1f,    0.12f),
        ("wave_speed",             "Wave Speed",         0f,    3f,    0.50f),
        ("wave_strength",          "Wave Strength",      0f,    2f,    0.70f),
        ("wave_stretch",           "Wave Stretch",       0.05f, 1f,    0.35f),
        ("detail_scale",           "Detail Scale",       0.1f,  6f,    1.20f),
        ("detail_speed",           "Detail Speed",       0f,    3f,    0.25f),
        ("detail_strength",        "Detail Strength",    0f,    1f,    0.25f),
        ("tip_bias",               "Tip Bias",           0.5f,  4f,    1.50f),
        ("mid_position",           "Mid Position",       0.05f, 0.95f, 0.50f),
        ("highlight_start",        "Highlight Start",    0f,    1f,    0.78f),
        ("color_variation",        "Colour Variation",   0f,    0.3f,  0.07f),
        ("ao_strength",            "AO Strength",        0f,    1f,    0.45f),
        ("ao_height",              "AO Height",          0f,    0.6f,  0.28f),
        ("toon_bands",             "Toon Bands",         1f,    6f,    3.00f),
        ("toon_softness",          "Toon Softness",      0f,    1f,    0.12f),
        ("toon_wrap",              "Toon Wrap",          0f,    1f,    0.25f),
        ("mass_strength",          "Mass Strength",      0f,    1f,    0.55f),
        ("mass_strength2",         "Mass Strength 2",    0f,    1f,    0.40f),
        ("mass_scale",             "Mass Scale",         0.005f,0.4f,  0.06f),
        ("mass_wind_follow",       "Mass Wind Follow",   0f,    1f,    0.00f),
        ("normal_strength",        "Normal Strength",    0f,    2f,    1.00f),
        // --- v5: distance fade ---
        ("fade_start",             "Fade Start",         1f,    80f,   18.0f),
        ("fade_end",               "Fade End",           1f,    120f,  40.0f),
        // --- v5: atmospheric depth tint ---
        ("depth_start",            "Depth Start",        0f,    80f,   8.0f),
        ("depth_end",              "Depth End",          0f,    120f,  30.0f),
        ("depth_strength",         "Depth Strength",     0f,    1f,    0.50f),
        // --- v5: tip translucency ---
        ("translucency_strength",  "Translucency",       0f,    3f,    1.00f),
        ("translucency_power",     "Translucency Power", 1f,    12f,   4.00f),
        ("translucency_distortion","Transl. Distortion", 0f,    1f,    0.30f),
    };

    // Shader *data* defaults (not UI theming) -> UITheme rule does not apply.
    private static readonly (string Name, string Label, Color Def)[] Colors =
    {
        ("base_color",          "Base Colour",          new Color(0.10f, 0.34f, 0.09f)),
        ("mid_color",           "Mid Colour",           new Color(0.32f, 0.62f, 0.18f)),
        ("tip_color",           "Tip Colour",           new Color(0.65f, 0.90f, 0.35f)),
        ("highlight_color",     "Highlight Colour",     new Color(0.96f, 1.00f, 0.78f)),
        ("mass_tint_color",     "Mass Tint",            new Color(0.50f, 0.62f, 0.42f)),
        ("mass_tint_color2",    "Mass Tint 2",          new Color(0.50f, 0.46f, 0.26f)),
        ("depth_color",         "Depth Haze",           new Color(0.62f, 0.68f, 0.72f)),
        ("translucency_color",  "Backlight Colour",     new Color(0.75f, 0.95f, 0.40f)),
    };

    private static readonly (string Name, string Label, bool Def)[] Bools =
    {
        ("use_mass_tint",       "Mass Clumping",     false),
        ("use_mass_tint2",      "Mass Clumping 2",   false),
        ("use_normal_map",      "Normal Map",        false),
        ("use_albedo_tex",      "Albedo Texture",    false),
        ("use_distance_fade",   "Distance Fade",     true),
        ("use_dither_fade",     "Dither Fade (compat)", false),
        ("use_depth_tint",      "Atmospheric Depth", true),
        ("use_translucency",    "Tip Backlight",     true),
    };

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        CustomMinimumSize = new Vector2(330, 0);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        AddChild(scroll);

        var vb = new VBoxContainer();
        vb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(vb);

        vb.AddChild(new Label { Text = "Painterly Grass — live tuner" });

        var rescan = new Button { Text = "Rescan / Sync" };
        rescan.Pressed += Refresh;
        vb.AddChild(rescan);

        var dump = new Button { Text = "Print values to Output" };
        dump.Pressed += PrintValues;
        vb.AddChild(dump);

        vb.AddChild(new HSeparator());

        foreach (var b in Bools)
            AddBool(vb, b.Name, b.Label, b.Def);

        vb.AddChild(new HSeparator());

        foreach (var f in Floats)
            AddFloat(vb, f.Name, f.Label, f.Min, f.Max, f.Def);

        vb.AddChild(new HSeparator());

        foreach (var c in Colors)
            AddColor(vb, c.Name, c.Label, c.Def);

        Refresh();
    }

    private void AddFloat(VBoxContainer parent, string name, string label, float min, float max, float def)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);

        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(130, 0) });

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = (max - min) / 200.0,
            Value = def,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 0)
        };
        row.AddChild(slider);

        var valLabel = new Label
        {
            Text = def.ToString("0.###"),
            CustomMinimumSize = new Vector2(46, 0)
        };
        row.AddChild(valLabel);

        slider.ValueChanged += (double v) =>
        {
            valLabel.Text = v.ToString("0.###");
            _mat?.SetShaderParameter(name, (float)v);
        };

        _syncers.Add(() =>
        {
            if (_mat == null)
                return;
            Variant cur = _mat.GetShaderParameter(name);
            float fv = cur.VariantType != Variant.Type.Nil ? cur.AsSingle() : def;
            slider.SetValueNoSignal(fv);
            valLabel.Text = fv.ToString("0.###");
        });
    }

    private void AddColor(VBoxContainer parent, string name, string label, Color def)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);

        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(130, 0) });

        var picker = new ColorPickerButton
        {
            Color = def,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(160, 24)
        };
        row.AddChild(picker);

        picker.ColorChanged += (Color c) => _mat?.SetShaderParameter(name, c);

        _syncers.Add(() =>
        {
            if (_mat == null)
                return;
            Variant cur = _mat.GetShaderParameter(name);
            picker.Color = cur.VariantType != Variant.Type.Nil ? cur.AsColor() : def;
        });
    }

    private void AddBool(VBoxContainer parent, string name, string label, bool def)
    {
        var cb = new CheckButton { Text = label, ButtonPressed = def };
        parent.AddChild(cb);

        cb.Toggled += (bool on) => _mat?.SetShaderParameter(name, on);

        _syncers.Add(() =>
        {
            if (_mat == null)
                return;
            Variant cur = _mat.GetShaderParameter(name);
            cb.SetPressedNoSignal(cur.VariantType != Variant.Type.Nil ? cur.AsBool() : def);
        });
    }

    /// <summary>Re-find the grass material and sync every control to it. Call after the stage regenerates.</summary>
    public void Refresh()
    {
        _mat = ResolveMaterial();
        if (_mat == null)
            GD.Print("[PainterlyGrassTuner] No grass material yet — generate the stage, then press Rescan / Sync.");

        foreach (var s in _syncers)
            s();
    }

    private ShaderMaterial ResolveMaterial()
    {
        if (Material != null)
            return Material;

        Node field = null;

        if (GrassFieldPath != null && !GrassFieldPath.IsEmpty)
            field = GetNodeOrNull(GrassFieldPath);

        if (field == null)
        {
            var nodes = GetTree().GetNodesInGroup(PainterlyGrassGroup);
            if (nodes.Count > 0)
                field = nodes[0];
        }

        if (field is GeometryInstance3D gi && gi.MaterialOverride is ShaderMaterial sm)
            return sm;

        return null;
    }

    private void PrintValues()
    {
        if (_mat == null)
        {
            GD.Print("[PainterlyGrassTuner] No material bound.");
            return;
        }

        GD.Print("--- painterly grass values ---");
        foreach (var f in Floats)
        {
            Variant c = _mat.GetShaderParameter(f.Name);
            float v = c.VariantType != Variant.Type.Nil ? c.AsSingle() : f.Def;
            GD.Print($"{f.Name} = {v:0.####}");
        }
        foreach (var c in Colors)
        {
            Variant v = _mat.GetShaderParameter(c.Name);
            Color col = v.VariantType != Variant.Type.Nil ? v.AsColor() : c.Def;
            GD.Print($"{c.Name} = Color({col.R:0.###}, {col.G:0.###}, {col.B:0.###})");
        }
        foreach (var b in Bools)
        {
            Variant v = _mat.GetShaderParameter(b.Name);
            bool on = v.VariantType != Variant.Type.Nil ? v.AsBool() : b.Def;
            GD.Print($"{b.Name} = {on}");
        }
    }
}
