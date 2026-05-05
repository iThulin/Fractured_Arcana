using Godot;
using System.Collections.Generic;

/// <summary>
/// Autoload singleton that loads, saves, and applies display/UI settings.
/// 
/// SETUP:
///   1. Save this script as res://Scripts/Systems/SettingsManager.cs
///   2. In Project Settings → Globals → Autoload, add this script with name "SettingsManager"
///      so it's available everywhere as `SettingsManager.Instance`.
///   3. Settings persist to user://settings.cfg (per-user save folder, survives reinstalls).
/// </summary>
public partial class SettingsManager : Node
{
    public static SettingsManager Instance { get; private set; }

    private const string ConfigPath = "user://settings.cfg";

    // ── Public state (read by the menu UI) ──────────────────────────────────
    public Vector2I Resolution { get; private set; } = new Vector2I(1920, 1080);
    public DisplayServer.WindowMode WindowMode { get; private set; } = DisplayServer.WindowMode.Windowed;
    public bool VSync { get; private set; } = true;
    public float UIScale { get; private set; } = 1.0f;        // 0.75 .. 1.5
    public float MasterVolume { get; private set; } = 1.0f;   // 0.0 .. 1.0

    // Resolutions offered in the dropdown. Tweak this list to taste.
    public static readonly List<Vector2I> SupportedResolutions = new()
    {
        new Vector2I(1280, 720),
        new Vector2I(1366, 768),
        new Vector2I(1600, 900),
        new Vector2I(1920, 1080),
        new Vector2I(2560, 1440),
        new Vector2I(3440, 1440),   // ultrawide
        new Vector2I(3840, 2160),   // 4K
    };

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        LoadSettings();
        ApplyAllSettings();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Apply settings to the running engine
    // ════════════════════════════════════════════════════════════════════════

    public void ApplyAllSettings()
    {
        ApplyWindowMode();
        ApplyResolution();
        ApplyVSync();
        ApplyUIScale();
        ApplyVolume();
    }

    public void SetResolution(Vector2I res)
    {
        Resolution = res;
        ApplyResolution();
        SaveSettings();
    }

    public void SetWindowMode(DisplayServer.WindowMode mode)
    {
        WindowMode = mode;
        ApplyWindowMode();
        // Re-apply resolution because window mode changes can reset window size.
        if (mode == DisplayServer.WindowMode.Windowed)
            ApplyResolution();
        SaveSettings();
    }

    public void SetVSync(bool on)
    {
        VSync = on;
        ApplyVSync();
        SaveSettings();
    }

    public void SetUIScale(float scale)
    {
        UIScale = Mathf.Clamp(scale, 0.75f, 1.5f);
        ApplyUIScale();
        SaveSettings();
    }

    public void SetMasterVolume(float vol)
    {
        MasterVolume = Mathf.Clamp(vol, 0f, 1f);
        ApplyVolume();
        SaveSettings();
    }

    // ── Apply helpers ───────────────────────────────────────────────────────

    private void ApplyResolution()
    {
        // Only resize the window when we're in windowed mode. Fullscreen sizes
        // itself to the monitor.
        if (WindowMode != DisplayServer.WindowMode.Windowed) return;

        DisplayServer.WindowSetSize(Resolution);
        // Re-center the window so it doesn't drift off-screen on big res changes.
        var screenSize = DisplayServer.ScreenGetSize();
        var pos = (screenSize - Resolution) / 2;
        DisplayServer.WindowSetPosition(pos);
    }

    private void ApplyWindowMode()
    {
        DisplayServer.WindowSetMode(WindowMode);
    }

    private void ApplyVSync()
    {
        DisplayServer.WindowSetVsyncMode(
            VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
    }

    private void ApplyUIScale()
    {
        // content_scale_factor multiplies all UI sizes on top of the stretch mode.
        // Works perfectly with stretch mode = canvas_items.
        GetTree().Root.ContentScaleFactor = UIScale;
    }

    private void ApplyVolume()
    {
        int masterBus = AudioServer.GetBusIndex("Master");
        if (masterBus >= 0)
        {
            // Convert linear 0..1 to dB. -80 dB is effectively silent.
            float db = MasterVolume <= 0.0001f ? -80f : Mathf.LinearToDb(MasterVolume);
            AudioServer.SetBusVolumeDb(masterBus, db);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Persistence
    // ════════════════════════════════════════════════════════════════════════

    private void LoadSettings()
    {
        var cfg = new ConfigFile();
        var err = cfg.Load(ConfigPath);
        if (err != Error.Ok)
        {
            GD.Print("[Settings] No saved settings found — using defaults.");
            return;
        }

        int resW = (int)cfg.GetValue("display", "res_w", 1920);
        int resH = (int)cfg.GetValue("display", "res_h", 1080);
        Resolution = new Vector2I(resW, resH);

        int mode = (int)cfg.GetValue("display", "window_mode", (int)DisplayServer.WindowMode.Windowed);
        WindowMode = (DisplayServer.WindowMode)mode;

        VSync         = (bool)cfg.GetValue("display", "vsync", true);
        UIScale       = (float)cfg.GetValue("ui", "scale", 1.0f);
        MasterVolume  = (float)cfg.GetValue("audio", "master_volume", 1.0f);

        GD.Print($"[Settings] Loaded — {Resolution.X}x{Resolution.Y}, mode={WindowMode}, " +
                 $"vsync={VSync}, ui_scale={UIScale}, vol={MasterVolume}");
    }

    private void SaveSettings()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("display", "res_w", Resolution.X);
        cfg.SetValue("display", "res_h", Resolution.Y);
        cfg.SetValue("display", "window_mode", (int)WindowMode);
        cfg.SetValue("display", "vsync", VSync);
        cfg.SetValue("ui", "scale", UIScale);
        cfg.SetValue("audio", "master_volume", MasterVolume);

        var err = cfg.Save(ConfigPath);
        if (err != Error.Ok)
            GD.PrintErr($"[Settings] Save failed: {err}");
    }
}
