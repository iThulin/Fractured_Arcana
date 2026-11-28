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

    /// <summary>The Register's remarks and asides (comment + flavor barks).</summary>
    public bool RegisterCommentary { get; private set; } = true;
    /// <summary>The Register's one-time onboarding explanations.</summary>
    public bool RegisterExplanations { get; private set; } = true;

    /// <summary>How the 3D view is rendered. The UI is always drawn at full sharpness.</summary>
    public enum GraphicsQualityLevel
    {
        /// <summary>3D at two thirds resolution, fast edge smoothing. For integrated graphics.</summary>
        Performance = 0,
        /// <summary>3D at 80% resolution, 2x edge smoothing.</summary>
        Balanced = 1,
        /// <summary>3D at full resolution, 2x edge smoothing. The look the game is authored at.</summary>
        Quality = 2
    }

    public GraphicsQualityLevel GraphicsQuality { get; private set; } = GraphicsQualityLevel.Quality;

    /// <summary>Show the frame-rate readout (FpsOverlay). F3 toggles it.</summary>
    public bool ShowFps { get; private set; }

    private FpsOverlay _fpsOverlay;

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

        _fpsOverlay = new FpsOverlay { Name = "FpsOverlay" };
        AddChild(_fpsOverlay);

        ApplyAllSettings();

        // Deferred so the window system is fully ready
        if (!IsEmbedded)
            CallDeferred(nameof(ForceWindowToPrimaryScreen));

        if (IsEmbedded)
            GD.Print("[Settings] Running in editor, so window resize/move is skipped.");

        // V1 (combat_ui_v2 §4 / R21): enforce the minimum window size the
        // 1920×1080 design space is guaranteed readable at. Safe in-editor
        // (min-size on the embedded window is a no-op).
        GetWindow().MinSize = new Vector2I(UITheme.MinWindowWidth, UITheme.MinWindowHeight);
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
        ApplyGraphicsQuality();
        _fpsOverlay?.SetShown(ShowFps);
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

    public void SetGraphicsQuality(GraphicsQualityLevel level)
    {
        GraphicsQuality = level;
        ApplyGraphicsQuality();
        SaveSettings();
    }

    public void SetShowFps(bool on)
    {
        ShowFps = on;
        _fpsOverlay?.SetShown(on);
        SaveSettings();
    }

    public void SetRegisterCommentary(bool on)
    {
        RegisterCommentary = on;
        SaveSettings();
    }

    public void SetRegisterExplanations(bool on)
    {
        RegisterExplanations = on;
        SaveSettings();
    }

    // ── Apply helpers ────────────────────────────────────────────────────────

    private void ApplyResolution()
    {
        // The embedded player can't be resized, so skip silently.
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
        // WindowSetCurrentScreen alone is unreliable, and position-based targeting
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

            // Step 3: now go fullscreen. It will fullscreen on whichever
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

    /// <summary>
    /// Sets how the 3D view is rendered on the main window. Only the 3D scene is scaled:
    /// the UI stays at full resolution. Quality is the authored look (docs/performance_targets_v1.md).
    /// </summary>
    private void ApplyGraphicsQuality()
    {
        Window root = GetTree().Root;
        switch (GraphicsQuality)
        {
            case GraphicsQualityLevel.Performance:
                root.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
                root.Scaling3DScale = 0.67f;
                root.Msaa3D = Viewport.Msaa.Disabled;
                root.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa;
                break;

            case GraphicsQualityLevel.Balanced:
                root.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
                root.Scaling3DScale = 0.8f;
                root.Msaa3D = Viewport.Msaa.Msaa2X;
                root.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
                break;

            default:
                root.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
                root.Scaling3DScale = 1f;
                root.Msaa3D = Viewport.Msaa.Msaa2X;
                root.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
                break;
        }
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
            GD.Print("[Settings] No saved settings found, so using defaults.");
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
        RegisterCommentary = (bool)cfg.GetValue("register", "commentary", true);
        RegisterExplanations = (bool)cfg.GetValue("register", "explanations", true);
        GraphicsQuality = (GraphicsQualityLevel)Mathf.Clamp((int)cfg.GetValue("display", "graphics_quality", (int)GraphicsQualityLevel.Quality), 0, 2);
        ShowFps = (bool)cfg.GetValue("display", "show_fps", false);

        GD.Print($"[Settings] Loaded: {Resolution.X}x{Resolution.Y}, mode={WindowMode}, " +
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
        cfg.SetValue("register", "commentary", RegisterCommentary);
        cfg.SetValue("register", "explanations", RegisterExplanations);
        cfg.SetValue("display", "graphics_quality", (int)GraphicsQuality);
        cfg.SetValue("display", "show_fps", ShowFps);

        var err = cfg.Save(ConfigPath);
        if (err != Error.Ok)
            GD.PrintErr($"[Settings] Save failed: {err}");
    }
}
