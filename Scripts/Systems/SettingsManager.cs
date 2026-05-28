using Godot;
using System.Collections.Generic;

// ============================================================
// SettingsManager.cs
//
// Purpose:        Autoload singleton owning display, audio, and
//                 UI scale settings. Persists to user://settings.
//                 cfg; applies values to DisplayServer /
//                 AudioServer / Tree on change. Embedded-player
//                 (editor F5) detection skips window resize ops
//                 the editor can't honour.
// Layer:          System
// Collaborators:  SettingsMenu.cs (UI binding), every viewport
//                 (indirectly via ContentScaleFactor)
// See:            (none)
// ============================================================

/// <summary>Autoload that owns and persists user-facing settings (resolution, window mode, vsync, UI scale, master volume). Skips window-touching operations when running inside the editor's embedded player.</summary>
public partial class SettingsManager : Node
{
    public static SettingsManager Instance { get; private set; }

    private const string ConfigPath = "user://settings.cfg";

    // ── Whether we're inside the Godot editor's embedded player ────────────
    // True when running via F5 inside the editor. False in exported builds.
    private static bool IsEmbedded => OS.HasFeature("editor");

    // ── Public state ────────────────────────────────────────────────────────
    public Vector2I Resolution { get; private set; } = new Vector2I(1920, 1080);
    public DisplayServer.WindowMode WindowMode { get; private set; } = DisplayServer.WindowMode.Fullscreen;
    public bool VSync { get; private set; } = true;
    public float UIScale { get; private set; } = 1.0f;
    public float MasterVolume { get; private set; } = 1.0f;

    public static readonly List<Vector2I> SupportedResolutions = new()
    {
        new Vector2I(1280, 720),
        new Vector2I(1366, 768),
        new Vector2I(1600, 900),
        new Vector2I(1920, 1080),
        new Vector2I(2560, 1440),
        new Vector2I(3440, 1440),
        new Vector2I(3840, 2160),
    };

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        LoadSettings();
        ApplyAllSettings();

        // Deferred so the window system is fully ready
        if (!IsEmbedded)
            CallDeferred(nameof(ForceWindowToPrimaryScreen));

        if (IsEmbedded)
            GD.Print("[Settings] Running in editor — window resize/move skipped.");
    }

    private void ForceWindowToPrimaryScreen()
    {
        int primary = DisplayServer.GetPrimaryScreen();
        var screenPos = DisplayServer.ScreenGetPosition(primary);

        // Must be windowed to move, then go fullscreen
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetPosition(screenPos + new Vector2I(10, 10));
        DisplayServer.WindowSetCurrentScreen(primary);
        DisplayServer.WindowSetMode(WindowMode);

        GD.Print($"[Settings] Forced to screen {primary} at {screenPos}, mode={WindowMode}");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Apply
    // ════════════════════════════════════════════════════════════════════════

    public void ApplyAllSettings()
    {
        // ── Always start on the primary screen ──────────────────────────────
        if (!IsEmbedded)
        {
            int primary = DisplayServer.GetPrimaryScreen();
            var screenPos = DisplayServer.ScreenGetPosition(primary);
            DisplayServer.WindowSetPosition(screenPos + new Vector2I(10, 10));
        }

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

    // ── Apply helpers ────────────────────────────────────────────────────────

    private void ApplyResolution()
    {
        // The embedded player can't be resized — skip silently.
        if (IsEmbedded) return;
        if (WindowMode != DisplayServer.WindowMode.Windowed) return;

        DisplayServer.WindowSetSize(Resolution);
        var screenSize = DisplayServer.ScreenGetSize();
        var pos = (screenSize - Resolution) / 2;
        DisplayServer.WindowSetPosition(pos);
    }

    private void ApplyWindowMode()
    {
        if (IsEmbedded) return;

        // For fullscreen modes, explicitly move to the primary monitor first.
        // WindowSetCurrentScreen alone is unreliable — position-based targeting
        // is more consistent across drivers.
        if (WindowMode == DisplayServer.WindowMode.Fullscreen ||
            WindowMode == DisplayServer.WindowMode.ExclusiveFullscreen)
        {
            int primary = DisplayServer.GetPrimaryScreen();

            // Get the top-left position of the primary monitor in desktop space
            var screenPos = DisplayServer.ScreenGetPosition(primary);

            // Step 1: ensure we're windowed so we can be moved
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);

            // Step 2: move the window onto that monitor
            DisplayServer.WindowSetPosition(screenPos + new Vector2I(1, 1));

            // Step 3: now go fullscreen — it will fullscreen on whichever
            // monitor the window is currently sitting on
            DisplayServer.WindowSetMode(WindowMode);
        }
        else
        {
            DisplayServer.WindowSetMode(WindowMode);
        }
    }

    private void ApplyVSync()
    {
        DisplayServer.WindowSetVsyncMode(
            VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
    }

    private void ApplyUIScale()
    {
        GetTree().Root.ContentScaleFactor = UIScale;
    }

    private void ApplyVolume()
    {
        int masterBus = AudioServer.GetBusIndex("Master");
        if (masterBus >= 0)
        {
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

        VSync = (bool)cfg.GetValue("display", "vsync", true);
        UIScale = (float)cfg.GetValue("ui", "scale", 1.0f);
        MasterVolume = (float)cfg.GetValue("audio", "master_volume", 1.0f);

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
