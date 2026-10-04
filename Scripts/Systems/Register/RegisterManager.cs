using Godot;
using System;
using System.Collections.Generic;
using System.Text;

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
//                 Node lifetime rule (2026-10-02 crash fix): every
//                 node this autoload owns is built ONCE in _Ready
//                 and only ever shown, hidden or retexted after
//                 that. Nothing is created per note and nothing is
//                 freed. The first version built and QueueFree'd a
//                 slip per note, and a playtest crashed inside
//                 Viewport::_propagate_drag_notification on a
//                 deleted node still listed in the tree (or froze,
//                 when the dangling memory looped instead). Turning
//                 the Register off removed the crash, so the per-note
//                 slips are the prime suspect. A persistent slip has
//                 no lifetime to get wrong.
//
//                 Fire() is safe from anywhere: with no instance
//                 (headless asserts, tools) it does nothing.
// Layer:          System (autoload)
// Collaborators:  RegisterBarks.cs (data), RegisterBookGlyph.cs,
//                 SettingsManager.cs (toggles), SaveManager /
//                 EternalLedger (once flags), CombatManager
//                 (NoteCombatTurn), ReactionChart.cs
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
    private const float FadeSeconds = 0.5f;
    private const float DockLeft = 16f;
    private const float DockTopFraction = 0.36f;   // of the viewport height

    // ── Persistent UI (built once; see the lifetime rule above) ─────────
    private CanvasLayer _layer;
    private RegisterBookGlyph _book;

    private PanelContainer _slip;
    private StyleBoxFlat _slipStyleExplain;
    private StyleBoxFlat _slipStyleVoice;
    private Label _slipTitle;
    private Label _slipLine;
    private HSeparator _slipRule;
    private Label _slipNote;
    private HBoxContainer _slipRow;
    private Label _slipMore;
    private Button _slipNoted;

    private PanelContainer _history;
    private RichTextLabel _historyText;
    private Label _historyMuted;

    // ── Slip state ──────────────────────────────────────────────────────
    private RegisterBark _slipBark;           // null when no slip is showing
    private ulong _voiceExpireAtMs;           // 0 when the slip is not a timed remark
    private Tween _fade;

    // ── Queue and flags ─────────────────────────────────────────────────
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

    private bool SlipShowing => _slipBark != null;
    private bool HistoryShowing => _history != null && _history.Visible;

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
        LoadSeenFile();

        _layer = new CanvasLayer { Name = "RegisterLayer", Layer = RegisterLayer };
        AddChild(_layer);

        _book = new RegisterBookGlyph { Name = "RegisterBook" };
        _book.Clicked += ToggleHistory;
        _layer.AddChild(_book);

        BuildSlip();
        BuildHistory();

        PlaceAll();
        GetViewport().SizeChanged += PlaceAll;
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
        _book.Pending = _explainQueue.Count > 0 && (_slipBark == null || _slipBark.Category != RegisterCategory.Explain);
        if (hidden)
            return;

        // A timed remark runs out: fade, then hide (FinishFade).
        if (SlipShowing && _voiceExpireAtMs > 0 && Time.GetTicksMsec() >= _voiceExpireAtMs)
        {
            _voiceExpireAtMs = 0;
            StartFade();
        }

        if (SlipShowing)
        {
            // An explanation outranks whatever remark is on screen: replace it.
            if (_explainQueue.Count > 0 && _slipBark.Category != RegisterCategory.Explain)
                HideSlip();
            else
            {
                // A remark that arrives while a slip is up is stale by the time it could
                // show. Drop it rather than let it speak a turn late.
                _pendingVoice = null;
                return;
            }
        }

        if (HistoryShowing)
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

    // 2026-10-04: once flags were only written to the save's ledger and marked dirty;
    // nothing flushes a dirty save during a fight (and debug fights never save), so
    // quitting before a campus save brought every tip back. They are now also written
    // straight to a small file of their own the moment they are marked.
    private const string SeenFilePath = "user://register_seen.txt";

    private void LoadSeenFile()
    {
        if (!FileAccess.FileExists(SeenFilePath))
            return;
        using var f = FileAccess.Open(SeenFilePath, FileAccess.ModeFlags.Read);
        if (f == null)
            return;
        while (!f.EofReached())
        {
            string line = f.GetLine().Trim();
            if (line.Length > 0)
                _sessionSeen.Add(line);
        }
    }

    private static void AppendSeenFile(string id)
    {
        using var f = FileAccess.FileExists(SeenFilePath)
            ? FileAccess.Open(SeenFilePath, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(SeenFilePath, FileAccess.ModeFlags.Write);
        if (f == null)
            return;
        f.SeekEnd();
        f.StoreLine(id);
    }

    private void MarkSeen(string id)
    {
        if (_sessionSeen.Add(id))
            AppendSeenFile(id);
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
        HideHistory();
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

    private Vector2 ViewportSize => GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);

    private void PlaceAll()
    {
        float top = Mathf.Round(ViewportSize.Y * DockTopFraction);
        _book.Position = new Vector2(DockLeft, top);
        var beside = new Vector2(DockLeft + RegisterBookGlyph.GlyphSize + 10f, top);
        _slip.Position = beside;
        _history.Position = beside;
        _historyText.CustomMinimumSize = new Vector2(SlipWidth + 12f, Mathf.Min(520f, ViewportSize.Y * 0.5f));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Building (once)
    // ════════════════════════════════════════════════════════════════════════

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

    private static Label SlipLabel(Color color, int size, bool wrap = true)
    {
        var label = new Label
        {
            AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
            CustomMinimumSize = wrap ? new Vector2(SlipWidth - 28f, 0f) : Vector2.Zero,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static int BodySize => UITheme.OverworldUIFontSize - 3;

    private void BuildSlip()
    {
        _slipStyleExplain = SlipStyle(true);
        _slipStyleVoice = SlipStyle(false);

        _slip = new PanelContainer
        {
            Name = "RegisterSlip",
            CustomMinimumSize = new Vector2(SlipWidth, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        _slip.AddThemeStyleboxOverride("panel", _slipStyleVoice);
        _slip.GuiInput += OnSlipGuiInput;

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 6);
        _slip.AddChild(vbox);

        _slipTitle = SlipLabel(UITheme.CardInkMuted, BodySize - 3);
        _slipLine = SlipLabel(UITheme.CipherInk, BodySize);
        _slipRule = new HSeparator { MouseFilter = Control.MouseFilterEnum.Ignore };
        _slipNote = SlipLabel(UITheme.CardKeywordInk, BodySize - 1);
        vbox.AddChild(_slipTitle);
        vbox.AddChild(_slipLine);
        vbox.AddChild(_slipRule);
        vbox.AddChild(_slipNote);

        _slipRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _slipMore = SlipLabel(UITheme.CardInkMuted, BodySize - 3, wrap: false);
        _slipMore.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _slipRow.AddChild(_slipMore);
        _slipNoted = new Button { Text = "Noted", CustomMinimumSize = new Vector2(96, 32) };
        UITheme.ApplyButtonStyle(_slipNoted, isPrimary: true);
        _slipNoted.Pressed += HideSlip;
        _slipRow.AddChild(_slipNoted);
        vbox.AddChild(_slipRow);

        _layer.AddChild(_slip);
    }

    private void BuildHistory()
    {
        _history = new PanelContainer
        {
            Name = "RegisterHistory",
            CustomMinimumSize = new Vector2(SlipWidth + 40f, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        _history.AddThemeStyleboxOverride("panel", _slipStyleExplain);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        _history.AddChild(vbox);

        var header = new HBoxContainer();
        var title = SlipLabel(UITheme.CipherInk, BodySize, wrap: false);
        title.Text = "THE REGISTER: MARGIN NOTES";
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);

        var chart = new Button { Text = "Reaction chart", CustomMinimumSize = new Vector2(130, 30) };
        UITheme.ApplyButtonStyle(chart, isPrimary: false);
        chart.Pressed += OpenReactionChart;
        header.AddChild(chart);

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(80, 30) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += HideHistory;
        header.AddChild(close);
        vbox.AddChild(header);

        // One persistent RichTextLabel with its own scrolling holds every note, so
        // reopening the book rewrites text instead of building and freeing rows.
        _historyText = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            FitContent = false,
            SelectionEnabled = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _historyText.AddThemeFontSizeOverride("normal_font_size", BodySize - 1);
        _historyText.AddThemeFontSizeOverride("bold_font_size", BodySize - 1);
        _historyText.AddThemeColorOverride("default_color", UITheme.CardInk);
        vbox.AddChild(_historyText);

        _historyMuted = SlipLabel(UITheme.CardInkMuted, BodySize - 3);
        vbox.AddChild(_historyMuted);

        _layer.AddChild(_history);
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

    private void ShowSlip(RegisterBark bark)
    {
        StopFade();
        bool explain = bark.Category == RegisterCategory.Explain;

        _slip.AddThemeStyleboxOverride("panel", explain ? _slipStyleExplain : _slipStyleVoice);
        _slipTitle.Text = bark.Title.ToUpperInvariant();

        string line = NextLine(bark);
        _slipLine.Text = line;
        _slipLine.Visible = !string.IsNullOrEmpty(line);

        bool hasNote = explain && !string.IsNullOrEmpty(bark.Note);
        _slipNote.Text = hasNote ? bark.Note : "";
        _slipNote.Visible = hasNote;
        _slipRule.Visible = hasNote;

        int waiting = _explainQueue.Count;
        _slipMore.Text = waiting > 0 ? $"{waiting} more note{(waiting == 1 ? "" : "s")} waiting" : "";
        _slipRow.Visible = explain;

        _voiceExpireAtMs = explain ? 0 : Time.GetTicksMsec() + (ulong)(FlavorSlipSeconds * 1000f);

        _slip.Modulate = Colors.White;
        _slip.Visible = true;
        _slip.ResetSize();   // shrink to the new text rather than keep the last note's height
        _slipBark = bark;
        GD.Print($"[Register] {bark.Key}: {line}");
    }

    private void HideSlip()
    {
        StopFade();
        _voiceExpireAtMs = 0;
        _slip.Visible = false;
        _slip.Modulate = Colors.White;
        _slipBark = null;
    }

    /// <summary>A remark is dismissed by a click anywhere on it. Explanations wait for Noted.</summary>
    private void OnSlipGuiInput(InputEvent e)
    {
        if (_slipBark == null || _slipBark.Category == RegisterCategory.Explain)
            return;
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            HideSlip();
    }

    private void StartFade()
    {
        StopFade();
        _fade = CreateTween();   // owned by this autoload, never by the slip
        _fade.TweenProperty(_slip, "modulate:a", 0f, FadeSeconds);
        _fade.TweenCallback(Callable.From(HideSlip));
    }

    private void StopFade()
    {
        if (_fade != null && _fade.IsValid())
            _fade.Kill();
        _fade = null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Margin notes (history)
    // ════════════════════════════════════════════════════════════════════════

    private void ToggleHistory()
    {
        if (HistoryShowing)
        {
            HideHistory();
            return;
        }
        HideSlip();
        ShowHistory();
    }

    private void HideHistory()
    {
        if (_history != null)
            _history.Visible = false;
    }

    private void ShowHistory()
    {
        string muted = ColorHex(UITheme.CardInkMuted);
        var sb = new StringBuilder();
        int shown = 0;
        foreach (var bark in RegisterBarks.All)
        {
            if (bark.Category != RegisterCategory.Explain || !IsSeen(bark.SeenId))
                continue;
            if (shown > 0)
                sb.Append("\n\n");
            shown++;
            sb.Append($"[color={muted}]{Escape(bark.Title.ToUpperInvariant())}[/color]\n");
            sb.Append(Escape(bark.Note));
        }
        if (shown == 0)
            sb.Append($"[color={muted}]Nothing is written here yet. The Register will make a note the first time something needs explaining.[/color]");

        _historyText.Text = sb.ToString();
        _historyText.ScrollToLine(0);

        bool explainOff = !ExplanationsEnabled, commentOff = !CommentaryEnabled;
        if (explainOff || commentOff)
        {
            string which = explainOff && commentOff ? "explanations and commentary are"
                         : explainOff ? "explanations are" : "commentary is";
            _historyMuted.Text = $"Register {which} muted in Settings.";
            _historyMuted.Visible = true;
        }
        else
        {
            _historyMuted.Text = "";
            _historyMuted.Visible = false;
        }

        _history.Visible = true;
        _history.ResetSize();
    }

    private void OpenReactionChart() => ReactionChart.ShowDialog(this);   // not under the layer: nothing transient lives there

    private static string ColorHex(Color c) => "#" + c.ToHtml(false);

    /// <summary>Neutralises BBCode brackets in authored text.</summary>
    private static string Escape(string s) => (s ?? "").Replace("[", "[lb]");
}
