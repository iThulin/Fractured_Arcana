using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// RegisterManager.cs
//
// Purpose:        Autoload that gives the Register (the guild's
//                 babbling book) its voice: takes trigger keys from
//                 gameplay code, applies toggles, once flags,
//                 cooldowns, priority and rate limits, and shows
//                 the result as a margin slip beside a docked book
//                 glyph. Also hosts the margin-notes history, where
//                 every explanation already shown can be reread.
//
//                 Rules (the_register_v1 §4):
//                 • explain > comment > flavor when several fire
//                   in the same frame;
//                 • at most one comment/flavor per combat round, or
//                   per screen visit outside combat;
//                 • explanations queue and show one at a time, each
//                   waiting for "Noted";
//                 • once flags live on EternalLedger.RegisterSeen,
//                   so an explanation never repeats across cycles;
//                   cooldowns are per session;
//                 • "Register commentary" mutes comment + flavor,
//                   "Register explanations" mutes explain.
//
//                 Fire() is safe from anywhere: with no instance
//                 (headless asserts, tools) it does nothing.
// Layer:          System (autoload)
// Collaborators:  RegisterBarks.cs (data), RegisterBookGlyph.cs,
//                 SettingsManager.cs (toggles), SaveManager /
//                 EternalLedger (once flags), CombatManager
//                 (NoteCombatTurn)
// See:            docs/the_register_v1.md
// ============================================================

/// <summary>Process-wide autoload that speaks the Register's barks. Gameplay code calls
/// <see cref="Fire"/> with a trigger key; everything else is decided here and in
/// Data/Register/barks.json.</summary>
public partial class RegisterManager : Node
{
    public static RegisterManager Instance { get; private set; }

    // Above the HUD (90), pause (100) and the council overlay (128): the first-council
    // note has to read over the council itself. Hidden while the tree is paused, so it
    // never sits on top of the pause menu.
    private const int RegisterLayer = 130;

    private const float SlipWidth = 400f;
    private const float FlavorSlipSeconds = 7f;
    private const float DockLeft = 16f;
    private const float DockTopFraction = 0.36f;   // of the viewport height

    private CanvasLayer _layer;
    private RegisterBookGlyph _book;
    private PanelContainer _slip;
    private RegisterBark _slipBark;
    private PanelContainer _history;

    private readonly List<RegisterBark> _explainQueue = new();
    private readonly HashSet<string> _queuedIds = new(StringComparer.Ordinal);
    private RegisterBark _pendingVoice;

    private readonly HashSet<string> _sessionSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _cooldownUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lineIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedKeys = new(StringComparer.Ordinal);

    private ulong _lastSceneId = ulong.MaxValue;
    private string _rateToken = "";
    private string _spokenToken = null;

    // ════════════════════════════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Offers the trigger <paramref name="key"/> to the Register. Whether anything is said
    /// is decided by the bark data, the toggles, once flags, cooldowns and rate limits.
    /// Unknown keys are ignored (warned once). Safe to call with no instance.
    /// </summary>
    public static void Fire(string key, IReadOnlyDictionary<string, string> args = null)
    {
        if (Instance == null || string.IsNullOrEmpty(key))
            return;
        Instance.FireInternal(key, args);
    }

    /// <summary>Opens a new combat round for the rate limit: one comment or flavor line per round.</summary>
    public static void NoteCombatTurn(int round)
    {
        if (Instance == null)
            return;
        Instance._rateToken = $"combat:{Instance._lastSceneId}:{round}";
    }

    /// <summary>True when the explanation behind <paramref name="key"/> has already been shown.</summary>
    public static bool HasSeen(string key) => Instance != null && Instance.IsSeen(key);

    // ════════════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ════════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;   // keep reading pause state while paused

        RegisterBarks.EnsureLoaded();

        _layer = new CanvasLayer { Name = "RegisterLayer", Layer = RegisterLayer };
        AddChild(_layer);

        _book = new RegisterBookGlyph { Name = "RegisterBook" };
        _book.Clicked += ToggleHistory;
        _layer.AddChild(_book);
        PlaceBook();
        GetViewport().SizeChanged += OnViewportResized;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    public override void _Process(double delta)
    {
        TrackScene();

        bool hidden = IsHiddenScene() || GetTree().Paused;
        _layer.Visible = !hidden;
        if (_book != null)
            _book.Pending = _explainQueue.Count > 0 && (_slip == null || _slipBark?.Category != RegisterCategory.Explain);
        if (hidden)
            return;

        if (_slip != null)
        {
            // An explanation outranks whatever remark is on screen: replace it.
            if (_explainQueue.Count > 0 && _slipBark != null && _slipBark.Category != RegisterCategory.Explain)
                CloseSlip();
            else
            {
                // A remark that arrives while a slip is up is stale by the time it could
                // show. Drop it rather than let it speak a turn late.
                _pendingVoice = null;
                return;
            }
        }

        if (_history != null)
            return;   // the reader has the book open; nothing interrupts it

        if (_explainQueue.Count > 0)
        {
            var next = _explainQueue[0];
            _explainQueue.RemoveAt(0);
            _queuedIds.Remove(next.SeenId);
            if (next.Once && IsSeen(next.SeenId))
                return;   // shown elsewhere meanwhile
            MarkSeen(next.SeenId);
            ShowSlip(next);
            _pendingVoice = null;
            return;
        }

        if (_pendingVoice != null)
        {
            var voice = _pendingVoice;
            _pendingVoice = null;
            if (_spokenToken == _rateToken)
                return;
            _spokenToken = _rateToken;
            if (voice.CooldownSeconds > 0f)
                _cooldownUntil[voice.SeenId] = Time.GetTicksMsec() + (ulong)(voice.CooldownSeconds * 1000f);
            if (voice.Once)
                MarkSeen(voice.SeenId);
            ShowSlip(voice);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Firing
    // ════════════════════════════════════════════════════════════════════════

    private void FireInternal(string key, IReadOnlyDictionary<string, string> args)
    {
        var candidates = RegisterBarks.For(key);
        if (candidates.Count == 0)
        {
            if (_warnedKeys.Add(key))
                GD.PushWarning($"[Register] No bark for trigger '{key}'. Add one to {RegisterBarks.DefaultPath}.");
            return;
        }

        RegisterBark bark = null;
        foreach (var c in candidates)
        {
            if (c.Matches(args))
            {
                bark = c;
                break;
            }
        }
        if (bark == null)
            return;

        string id = bark.SeenId;
        if (bark.Once && IsSeen(id))
            return;

        if (bark.Category == RegisterCategory.Explain)
        {
            if (!ExplanationsEnabled || _queuedIds.Contains(id))
                return;
            int at = _explainQueue.FindIndex(b => b.Priority < bark.Priority);
            if (at < 0)
                _explainQueue.Add(bark);
            else
                _explainQueue.Insert(at, bark);
            _queuedIds.Add(id);
            return;
        }

        if (!CommentaryEnabled)
            return;
        if (_cooldownUntil.TryGetValue(id, out var until) && Time.GetTicksMsec() < until)
            return;
        if (_spokenToken == _rateToken)
            return;
        if (_pendingVoice == null || bark.Priority > _pendingVoice.Priority)
            _pendingVoice = bark;
    }

    private static bool ExplanationsEnabled => SettingsManager.Instance?.RegisterExplanations ?? true;
    private static bool CommentaryEnabled => SettingsManager.Instance?.RegisterCommentary ?? true;

    // ════════════════════════════════════════════════════════════════════════
    //  Flags
    // ════════════════════════════════════════════════════════════════════════

    private bool IsSeen(string id)
    {
        if (_sessionSeen.Contains(id))
            return true;
        var seen = SaveManager.ActiveSave?.Ledger?.RegisterSeen;
        return seen != null && seen.Contains(id);
    }

    private void MarkSeen(string id)
    {
        _sessionSeen.Add(id);
        var ledger = SaveManager.ActiveSave?.Ledger;
        if (ledger == null)
            return;   // no save (dev scene): the session set keeps it quiet until restart
        ledger.RegisterSeen ??= new List<string>();
        if (!ledger.RegisterSeen.Contains(id))
        {
            ledger.RegisterSeen.Add(id);
            SaveManager.MarkDirty();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Scene tracking
    // ════════════════════════════════════════════════════════════════════════

    private void TrackScene()
    {
        var scene = GetTree().CurrentScene;
        ulong id = scene != null ? scene.GetInstanceId() : 0UL;
        if (id == _lastSceneId)
            return;
        _lastSceneId = id;
        _rateToken = $"scene:{id}";   // a new screen visit: one remark allowed
        if (_history != null)
            CloseHistory();
    }

    private bool IsHiddenScene()
    {
        var current = GetTree().CurrentScene;
        string lower = (current?.SceneFilePath ?? "").ToLowerInvariant();
        // Same rule as HudManager: pre-game menus only.
        return current == null || lower.Contains("mainmenu") || lower.Contains("newgame") || lower.Contains("titlescreen");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Layout
    // ════════════════════════════════════════════════════════════════════════

    private void OnViewportResized()
    {
        PlaceBook();
        if (_slip != null)
            PlaceSlip(_slip);
        if (_history != null)
            PlaceSlip(_history);
    }

    private Vector2 ViewportSize => GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);

    private void PlaceBook()
    {
        if (_book == null)
            return;
        _book.Position = new Vector2(DockLeft, Mathf.Round(ViewportSize.Y * DockTopFraction));
    }

    private void PlaceSlip(Control slip)
    {
        float x = DockLeft + RegisterBookGlyph.GlyphSize + 10f;
        float y = Mathf.Round(ViewportSize.Y * DockTopFraction);
        slip.Position = new Vector2(x, y);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The margin slip
    // ════════════════════════════════════════════════════════════════════════

    private string NextLine(RegisterBark bark)
    {
        if (bark.Lines.Count == 0)
            return "";
        _lineIndex.TryGetValue(bark.SeenId, out int i);
        _lineIndex[bark.SeenId] = i + 1;
        return bark.Lines[i % bark.Lines.Count];
    }

    private static StyleBoxFlat SlipStyle(bool explain)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(UITheme.CipherInkLight.R, UITheme.CipherInkLight.G, UITheme.CipherInkLight.B, 0.97f),
            BorderColor = explain ? UITheme.Gold : UITheme.CardInkMuted,
            BorderWidthLeft = 4,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 6,
        };
    }

    private static Label SlipLabel(string text, Color color, int size)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(SlipWidth - 28f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private void ShowSlip(RegisterBark bark)
    {
        CloseSlip();
        bool explain = bark.Category == RegisterCategory.Explain;

        var slip = new PanelContainer
        {
            Name = "RegisterSlip",
            CustomMinimumSize = new Vector2(SlipWidth, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        slip.AddThemeStyleboxOverride("panel", SlipStyle(explain));

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 6);
        slip.AddChild(vbox);

        int body = UITheme.OverworldUIFontSize - 3;
        vbox.AddChild(SlipLabel(bark.Title.ToUpperInvariant(), UITheme.CardInkMuted, body - 3));

        string line = NextLine(bark);
        if (!string.IsNullOrEmpty(line))
            vbox.AddChild(SlipLabel(line, UITheme.CipherInk, body));

        if (explain && !string.IsNullOrEmpty(bark.Note))
        {
            vbox.AddChild(new HSeparator { MouseFilter = Control.MouseFilterEnum.Ignore });
            vbox.AddChild(SlipLabel(bark.Note, UITheme.CardKeywordInk, body - 1));
        }

        if (explain)
        {
            var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            int waiting = _explainQueue.Count;
            if (waiting > 0)
            {
                var more = SlipLabel($"{waiting} more note{(waiting == 1 ? "" : "s")} waiting", UITheme.CardInkMuted, body - 3);
                more.CustomMinimumSize = Vector2.Zero;
                more.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(more);
            }
            var noted = new Button { Text = "Noted", CustomMinimumSize = new Vector2(96, 32) };
            UITheme.ApplyButtonStyle(noted, isPrimary: true);
            noted.Pressed += CloseSlip;
            row.AddChild(noted);
            vbox.AddChild(row);
        }
        else
        {
            // A remark: click to dismiss, or it fades on its own.
            slip.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    CloseSlip();
            };
            var timer = GetTree().CreateTimer(FlavorSlipSeconds, processAlways: false);
            timer.Timeout += () =>
            {
                if (_slip != slip || !GodotObject.IsInstanceValid(slip))
                    return;
                var tw = slip.CreateTween();
                tw.TweenProperty(slip, "modulate:a", 0f, 0.5f);
                tw.TweenCallback(Callable.From(() =>
                {
                    if (_slip == slip)
                        CloseSlip();
                }));
            };
        }

        _layer.AddChild(slip);
        PlaceSlip(slip);
        _slip = slip;
        _slipBark = bark;
        GD.Print($"[Register] {bark.Key}: {line}");
    }

    private void CloseSlip()
    {
        if (_slip != null && GodotObject.IsInstanceValid(_slip))
            _slip.QueueFree();
        _slip = null;
        _slipBark = null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Margin notes (history)
    // ════════════════════════════════════════════════════════════════════════

    private void ToggleHistory()
    {
        if (_history != null)
        {
            CloseHistory();
            return;
        }
        CloseSlip();
        OpenHistory();
    }

    private void CloseHistory()
    {
        if (_history != null && GodotObject.IsInstanceValid(_history))
            _history.QueueFree();
        _history = null;
    }

    private void OpenHistory()
    {
        var panel = new PanelContainer
        {
            Name = "RegisterHistory",
            CustomMinimumSize = new Vector2(SlipWidth + 40f, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        panel.AddThemeStyleboxOverride("panel", SlipStyle(true));

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        int body = UITheme.OverworldUIFontSize - 3;

        var header = new HBoxContainer();
        var title = SlipLabel("THE REGISTER: MARGIN NOTES", UITheme.CipherInk, body);
        title.CustomMinimumSize = Vector2.Zero;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(80, 30) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += CloseHistory;
        header.AddChild(close);
        vbox.AddChild(header);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(SlipWidth + 12f, Mathf.Min(520f, ViewportSize.Y * 0.5f)),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        vbox.AddChild(scroll);

        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(list);

        int shown = 0;
        foreach (var bark in RegisterBarks.All)
        {
            if (bark.Category != RegisterCategory.Explain || !IsSeen(bark.SeenId))
                continue;
            shown++;
            list.AddChild(SlipLabel(bark.Title.ToUpperInvariant(), UITheme.CardInkMuted, body - 3));
            if (!string.IsNullOrEmpty(bark.Note))
                list.AddChild(SlipLabel(bark.Note, UITheme.CardInk, body - 1));
            list.AddChild(new HSeparator());
        }

        if (shown == 0)
            list.AddChild(SlipLabel("Nothing is written here yet. The Register will make a note the first time something needs explaining.", UITheme.CardInkMuted, body - 1));

        if (!ExplanationsEnabled || !CommentaryEnabled)
        {
            string muted = !ExplanationsEnabled && !CommentaryEnabled ? "explanations and commentary are"
                         : !ExplanationsEnabled ? "explanations are" : "commentary is";
            vbox.AddChild(SlipLabel($"Register {muted} muted in Settings.", UITheme.CardInkMuted, body - 3));
        }

        _layer.AddChild(panel);
        PlaceSlip(panel);
        _history = panel;
    }
}
