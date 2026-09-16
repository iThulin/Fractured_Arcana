using Godot;
using System;
using System.Threading.Tasks;

// ============================================================
// SceneTransition.cs
//
// Purpose:        One interstitial for every heavy scene swap around
//                 combat. A persistent overlay (CanvasLayer on the tree
//                 root, like EncounterRouter) fades in over the outgoing
//                 scene, the tree is switched to an EMPTY hold scene so
//                 the outgoing scene is fully freed, then the target is
//                 loaded, and the overlay fades out once the target has
//                 drawn. Two things this buys:
//                   1. The outgoing scene's teardown and the incoming
//                      scene's build never share a frame (the direct
//                      Battlefield -> ExpeditionScene swap tripped an
//                      0xC0000374 heap-corruption check at exactly that
//                      point, 2026-09-15).
//                   2. No grey frame: the overlay outlives the swap, so
//                      the clear colour never reaches the screen.
// Layer:          UI
// Collaborators:  EncounterRouter, ExpeditionManager.CommitCombat,
//                 StrategicView / CampusScreen combat launchers,
//                 CardRewardScreen.ReturnToOverworld, CombatDebugLauncher
// ============================================================

/// <summary>Persistent scene-swap interstitial. Call <see cref="Go"/> in place of
/// <c>GetTree().ChangeSceneToFile(path)</c>; the swap still happens, just behind a
/// themed curtain and with the outgoing scene freed first.</summary>
public partial class SceneTransition : Node
{
    public static SceneTransition Instance { get; private set; }

    /// <summary>Curtain fade-in / fade-out, seconds.</summary>
    private const float FadeIn = 0.25f;
    private const float FadeOut = 0.35f;

    /// <summary>The empty hold scene. A real .tscn with a real resource path: a
    /// PackedScene packed at runtime has no path, and the editor's remote scene
    /// tree tries to resolve the current scene's file path while it is current
    /// ("Resource file not found: res://", 2026-09-16).</summary>
    private const string HoldScenePath = "res://Scenes/UI/TransitionHold.tscn";

    /// <summary>Frames to sit on the empty hold scene before loading the target.
    /// ChangeSceneToPacked is deferred, so frame 1 performs the swap and frees the
    /// outgoing scene; frame 2 flushes anything that swap queued.</summary>
    private const int HoldFrames = 2;

    /// <summary>Frames to let the target scene settle before the curtain lifts:
    /// its _Ready, the deferred camera make_current, and one full draw.</summary>
    private const int SettleFrames = 2;

    private CanvasLayer _layer;
    private ColorRect _curtain;
    private Label _title;
    private Label _subtitle;
    private PackedScene _holdScene;
    private bool _built;
    private bool _busy;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Swap to <paramref name="targetScenePath"/> behind the curtain.
    /// <paramref name="title"/> / <paramref name="subtitle"/> are what the player
    /// reads while the swap happens. <paramref name="minHoldSeconds"/> is the
    /// least time the curtain stays fully opaque (so a fast swap still reads as a
    /// beat, not a flicker). A second call while a transition is running is
    /// ignored with a warning: the first swap wins.</summary>
    public static void Go(SceneTree tree, string targetScenePath, string title,
                          string subtitle = "", float minHoldSeconds = 0.6f)
    {
        if (tree == null || string.IsNullOrEmpty(targetScenePath))
        {
            GD.PushError("[SceneTransition] Go called with no tree or no target.");
            return;
        }
        var t = Ensure(tree);
        _ = t.Run(targetScenePath, title ?? "", subtitle ?? "", Mathf.Max(0f, minHoldSeconds));
    }

    /// <summary>True while a transition owns the screen. Launchers can use it to
    /// ignore input that arrives during the fade.</summary>
    public static bool IsTransitioning => Instance != null && Instance._busy;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private static SceneTransition Ensure(SceneTree tree)
    {
        if (Instance != null && GodotObject.IsInstanceValid(Instance))
            return Instance;
        var node = new SceneTransition { Name = "SceneTransition" };
        Instance = node;
        // Tree-root adds go through CallDeferred (standing Mac build rule).
        tree.Root.CallDeferred(Node.MethodName.AddChild, node);
        return node;
    }

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;   // works under pause, like the router
        CallDeferred(nameof(BuildUI));
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    private void BuildUI()
    {
        if (_built)
            return;
        _built = true;

        // Above every in-scene CanvasLayer (HUDs sit at 0..10; toasts higher).
        _layer = new CanvasLayer { Name = "TransitionLayer", Layer = 120, Visible = false };
        AddChild(_layer);

        _curtain = new ColorRect
        {
            Name = "Curtain",
            Color = UITheme.WorldDeep,
            MouseFilter = Control.MouseFilterEnum.Stop,   // swallow clicks during the fade
            Modulate = new Color(1f, 1f, 1f, 0f),
        };
        _curtain.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(_curtain);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _curtain.AddChild(center);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        center.AddChild(column);

        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeTitle);
        _title.AddThemeColorOverride("font_color", UITheme.Gold);
        column.AddChild(_title);

        _subtitle = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(720f, 0f),
        };
        _subtitle.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _subtitle.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        column.AddChild(_subtitle);

        // The hold scene: being current for two frames is its entire job.
        _holdScene = GD.Load<PackedScene>(HoldScenePath);
        if (_holdScene == null)
            GD.PushError($"[SceneTransition] Hold scene missing at {HoldScenePath}; transitions will swap directly.");
    }

    // ── The transition ───────────────────────────────────────────────────────

    private async Task Run(string target, string title, string subtitle, float minHold)
    {
        if (_busy)
        {
            GD.PushWarning($"[SceneTransition] Ignored '{target}': a transition is already running.");
            return;
        }
        _busy = true;

        try
        {
            // Ensure() adds us deferred; the first Go can arrive in the same frame.
            if (!IsInsideTree())
                await ToSignal(this, Node.SignalName.Ready);
            BuildUI();   // no-op if the deferred build already ran

            var tree = GetTree();
            _title.Text = title;
            _subtitle.Text = subtitle;
            _subtitle.Visible = !string.IsNullOrEmpty(subtitle);
            _layer.Visible = true;

            // 1. Curtain down over the outgoing scene.
            await Fade(1f, FadeIn);
            ulong opaqueAt = Time.GetTicksMsec();

            // 2. Park on the empty hold scene. The outgoing scene is freed by the
            //    deferred swap on the next frame; the frame after flushes anything
            //    that teardown queued. Nothing heavy is alive after this.
            if (_holdScene != null)
            {
                Error holdErr = tree.ChangeSceneToPacked(_holdScene);
                if (holdErr != Error.Ok)
                    GD.PushError($"[SceneTransition] Hold scene swap failed: {holdErr}. Continuing to target.");
                await Frames(tree, HoldFrames);
            }

            // 3. Respect the minimum beat, measured from full opacity.
            float elapsed = (Time.GetTicksMsec() - opaqueAt) / 1000f;
            if (elapsed < minHold)
                await ToSignal(tree.CreateTimer(minHold - elapsed, processAlways: true), SceneTreeTimer.SignalName.Timeout);

            // 4. Load the target. ChangeSceneToFile blocks while the scene is
            //    instantiated and its _Ready runs; the curtain is what stays on
            //    screen for that stretch, which is the whole point.
            GD.Print($"[SceneTransition] {title} → {target}");
            Error err = tree.ChangeSceneToFile(target);
            if (err != Error.Ok)
                GD.PushError($"[SceneTransition] ChangeSceneToFile('{target}') failed: {err}. Lifting the curtain on the hold scene.");

            // 5. Let the target settle (deferred camera make_current, first draw).
            await Frames(tree, SettleFrames);

            // 6. Curtain up.
            await Fade(0f, FadeOut);
            _layer.Visible = false;
        }
        catch (Exception ex)
        {
            GD.PushError($"[SceneTransition] Transition to '{target}' threw: {ex.Message}");
            if (_layer != null && GodotObject.IsInstanceValid(_layer))
                _layer.Visible = false;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task Fade(float toAlpha, float seconds)
    {
        var tween = CreateTween();
        tween.SetProcessMode(Tween.TweenProcessMode.Idle);
        tween.SetPauseMode(Tween.TweenPauseMode.Process);   // keep fading if the game is paused
        tween.TweenProperty(_curtain, "modulate:a", toAlpha, seconds)
             .SetTrans(Tween.TransitionType.Sine)
             .SetEase(toAlpha > 0.5f ? Tween.EaseType.Out : Tween.EaseType.In);
        await ToSignal(tween, Tween.SignalName.Finished);
    }

    private static async Task Frames(SceneTree tree, int count)
    {
        for (int i = 0; i < count; i++)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }
}
