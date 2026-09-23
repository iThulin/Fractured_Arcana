using Godot;
using System;

// ============================================================
// NegotiationPortrait.cs
//
// Purpose:        v3 animated, voiced portrait. A layered 2D
//                 rig (head, hair, brows, eyes, mouth) driven by
//                 three inputs: expression (neutral / pleased /
//                 displeased → brow angle + resting mouth),
//                 talking (mouth flap synced to the typed bark),
//                 and a blink timer. Voice is a sample bank:
//                 short recorded syllables played on the mouth
//                 flap while the line types (Animal-Crossing /
//                 Banjo style). No samples on disk → silent. The
//                 old synthesized-tone voice is gone: raw square
//                 and saw bursts through AudioStreamGenerator
//                 overlapped into a buzz and the generator
//                 playback was not reliably live at _Ready.
//                 Every layer falls back to a procedural drawing
//                 when its PNG doesn't exist, so the rig works
//                 with zero art and lights up as files land.
// Layer:          UI
// Collaborators:  NegotiationManager.cs (owner; calls Setup /
//                 SetExpression / SetMood / Say),
//                 UITheme.cs (colors)
// Art contract:   res://Assets/Portraits/Negotiation/
//                   {archetype}_head.png     {archetype}_hair.png
//                   {archetype}_brow.png     (one brow, mirrored for the right)
//                   {archetype}_eyes.png     {archetype}_eyes_closed.png
//                   {archetype}_mouth.png    (closed, neutral)
//                   {archetype}_mouth_open.png
//                   {archetype}_mouth_smile.png
//                   {archetype}_mouth_frown.png
//                 archetype ∈ merchant, commander, scholar,
//                             opportunist, idealist, survivor
//                 Square layers, ~512×512, all drawn at the same
//                 origin (the rig positions nothing; each layer
//                 is already in place on its canvas). Missing
//                 layers fall back individually.
// ============================================================

/// <summary>Portrait widget for the negotiation table: a small rig with a
/// mood, a mouth, a blink, and a voice.</summary>
public partial class NegotiationPortrait : Control
{
    private const string ART_DIR = "res://Assets/Portraits/Negotiation/";

    private NpcArchetypeType _archetype = NpcArchetypeType.Merchant;
    private NpcExpression _expression = NpcExpression.Neutral;
    private NegotiationMood _mood = NegotiationMood.Even;
    private Posture _posture = Posture.Level;

    private Panel _ring;
    private StyleBoxFlat _ringStyle;
    private NegotiationPortraitRig _rig;

    // Talking
    private Timer _typeTimer;
    private string _sayText = "";
    private int _sayIndex = 0;
    private Action<string> _sayOnText;
    private int _flapCounter = 0;
    public bool IsTalking { get; private set; } = false;

    // Blink
    private Timer _blinkTimer;
    private bool _eyesClosed = false;

    // Voice (sample bank; see Voice contract in the header)
    private const string VOICE_DIR = "res://Assets/Audio/Voice/Negotiation/";
    private const double MinBlipGapSec = 0.07;
    private AudioStreamPlayer _voice;
    private readonly System.Collections.Generic.List<AudioStream> _bank = new();
    private NpcArchetypeType _bankFor = (NpcArchetypeType)(-1);
    private double _lastBlipAt = -1;
    private int _lastClip = -1;
    public bool VoiceEnabled { get; set; } = true;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(150, 150);

        _ringStyle = new StyleBoxFlat
        {
            BgColor = UITheme.BgRaised,
            BorderColor = UITheme.NegotiationTitleColor,
            BorderWidthTop = 3, BorderWidthBottom = 3,
            BorderWidthLeft = 3, BorderWidthRight = 3,
            CornerRadiusTopLeft = 75, CornerRadiusTopRight = 75,
            CornerRadiusBottomLeft = 75, CornerRadiusBottomRight = 75,
        };
        _ring = new Panel { AnchorRight = 1f, AnchorBottom = 1f, MouseFilter = MouseFilterEnum.Ignore };
        _ring.AddThemeStyleboxOverride("panel", _ringStyle);
        AddChild(_ring);

        _rig = new NegotiationPortraitRig
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            OffsetLeft = 6, OffsetTop = 6, OffsetRight = -6, OffsetBottom = -6,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        AddChild(_rig);

        _typeTimer = new Timer { WaitTime = 0.03, OneShot = false };
        _typeTimer.Timeout += OnTypeTick;
        AddChild(_typeTimer);

        _blinkTimer = new Timer { WaitTime = 3.4, OneShot = true };
        _blinkTimer.Timeout += OnBlink;
        AddChild(_blinkTimer);
        _blinkTimer.Start();

        _voice = new AudioStreamPlayer { VolumeDb = -4f };
        AddChild(_voice);
        LoadBank(_archetype);

        ApplyRing();
        _rig.Configure(_archetype);
        _rig.QueueRedraw();
    }

    /// <summary>Set the fixed identity for this table. Call once at open.</summary>
    public void Setup(NpcArchetypeType archetype)
    {
        _archetype = archetype;
        if (IsInsideTree())
        {
            _rig.Configure(archetype);
            _rig.QueueRedraw();
            LoadBank(archetype);
        }
    }

    /// <summary>The last reaction's read: brows and resting mouth.</summary>
    public void SetExpression(NpcExpression e)
    {
        if (_expression == e) return;
        _expression = e;
        if (_rig != null) { _rig.Expression = e; _rig.QueueRedraw(); }
    }

    /// <summary>Ring tint by mood (Warm green, Cold red, Even gold).</summary>
    public void SetMood(NegotiationMood mood)
    {
        _mood = mood;
        ApplyRing();
    }

    /// <summary>How they sit (v3.1 §5): the credit shown as a body. Eager
    /// leans in (rig up and slightly larger, ring bright); Guarded folds
    /// (rig down and smaller, ring dim); Level is the rest pose.</summary>
    public void SetPosture(Posture p, bool animate = true)
    {
        if (_rig == null) { _posture = p; return; }
        bool changed = p != _posture;
        _posture = p;
        float lean = p switch { Posture.Eager => -5f, Posture.Guarded => 4f, _ => 0f };
        float scale = p switch { Posture.Eager => 1.05f, Posture.Guarded => 0.96f, _ => 1f };
        float ringAlpha = p switch { Posture.Eager => 1f, Posture.Guarded => 0.55f, _ => 0.8f };
        _rig.PivotOffset = _rig.Size / 2f;
        var target = new Vector2(1f, 1f) * scale;
        if (!animate || !changed)
        {
            _rig.Position = new Vector2(_rig.Position.X, 6f + lean);
            _rig.Scale = target;
            _ring.Modulate = new Color(1, 1, 1, ringAlpha);
            return;
        }
        var tw = _rig.CreateTween().SetParallel(true);
        tw.TweenProperty(_rig, "position:y", 6f + lean, 0.45f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(_rig, "scale", target, 0.45f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        var tw2 = _ring.CreateTween();
        tw2.TweenProperty(_ring, "modulate", new Color(1, 1, 1, ringAlpha), 0.4f);
    }

    /// <summary>A short pulse of the ring in a colour: the FX contract's
    /// "portrait ring pulses". Returns to the mood colour.</summary>
    public void PulseRing(Color color)
    {
        if (_ring == null) return;
        _ringStyle.BorderColor = color;
        _ring.Scale = new Vector2(1.06f, 1.06f);
        _ring.PivotOffset = _ring.Size / 2f;
        var tw = _ring.CreateTween();
        tw.TweenProperty(_ring, "scale", Vector2.One, 0.5f)
          .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tw.TweenCallback(Callable.From(ApplyRing));
    }

    /// <summary>Type <paramref name="text"/> out through <paramref name="onText"/>
    /// (called with the text so far, every character), flapping the mouth
    /// and voicing each letter. Calling again mid-line cuts to the new line.</summary>
    public void Say(string text, Action<string> onText)
    {
        _sayText = text ?? "";
        _sayIndex = 0;
        _sayOnText = onText;
        _flapCounter = 0;
        IsTalking = _sayText.Length > 0;
        onText?.Invoke("");
        if (IsTalking) _typeTimer.Start();
        else StopTalking();
    }

    /// <summary>Finish the current line instantly (a click while typing).</summary>
    public void FinishLine()
    {
        if (!IsTalking) return;
        _sayIndex = _sayText.Length;
        _sayOnText?.Invoke(_sayText);
        StopTalking();
    }

    private void OnTypeTick()
    {
        if (_sayIndex >= _sayText.Length) { StopTalking(); return; }
        char ch = _sayText[_sayIndex];
        _sayIndex++;
        _sayOnText?.Invoke(_sayText.Substring(0, _sayIndex));
        if (char.IsLetterOrDigit(ch))
        {
            _flapCounter++;
            _rig.MouthOpen = (_flapCounter & 1) == 0;
            if ((_flapCounter & 1) == 0) Blip(ch);
        }
        else
        {
            _rig.MouthOpen = false;
        }
        _rig.QueueRedraw();
    }

    private void StopTalking()
    {
        _typeTimer.Stop();
        IsTalking = false;
        if (_rig != null) { _rig.MouthOpen = false; _rig.QueueRedraw(); }
        if (_voice != null && _voice.Playing) _voice.Stop();
    }

    private void OnBlink()
    {
        _eyesClosed = true;
        _rig.EyesClosed = true;
        _rig.QueueRedraw();
        var t = GetTree().CreateTimer(0.12);
        t.Timeout += () =>
        {
            _eyesClosed = false;
            if (_rig != null) { _rig.EyesClosed = false; _rig.QueueRedraw(); }
            _blinkTimer.WaitTime = 2.6 + GD.Randf() * 2.2;
            _blinkTimer.Start();
        };
    }

    private void ApplyRing()
    {
        if (_ringStyle == null) return;
        _ringStyle.BorderColor = _mood switch
        {
            NegotiationMood.Warm => UITheme.TensionCordial,
            NegotiationMood.Cold => UITheme.TensionHostile,
            _ => UITheme.NegotiationTitleColor,
        };
    }

    // ── Voice: sample bank ────────────────────────────────────────────────

    private static string VoiceFolder(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "merchant",
        NpcArchetypeType.Commander   => "commander",
        NpcArchetypeType.Scholar     => "scholar",
        NpcArchetypeType.Opportunist => "opportunist",
        NpcArchetypeType.Idealist    => "idealist",
        NpcArchetypeType.Survivor    => "survivor",
        _                            => "merchant",
    };

    /// <summary>Per-archetype pitch centre applied on top of the recorded
    /// clip, so one shared bank can still read as six voices while the
    /// per-archetype folders are empty.</summary>
    private static float PitchFor(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Commander   => 0.82f,
        NpcArchetypeType.Survivor    => 0.9f,
        NpcArchetypeType.Merchant    => 1.0f,
        NpcArchetypeType.Scholar     => 1.08f,
        NpcArchetypeType.Opportunist => 1.15f,
        NpcArchetypeType.Idealist    => 1.22f,
        _                            => 1.0f,
    };

    /// <summary>Load every clip in the archetype folder, falling back to
    /// the shared folder (VOICE_DIR/common) when the archetype has none.
    /// Silent, with one log line, when neither exists.</summary>
    private void LoadBank(NpcArchetypeType a)
    {
        if (_bankFor == a) return;
        _bankFor = a;
        _bank.Clear();
        _lastClip = -1;

        if (!LoadClipsFrom(VOICE_DIR + VoiceFolder(a) + "/"))
            LoadClipsFrom(VOICE_DIR + "common/");

        if (_bank.Count == 0)
            GD.Print($"[NegotiationPortrait] no voice clips under {VOICE_DIR}{VoiceFolder(a)}/ or {VOICE_DIR}common/. The portrait is silent.");
    }

    private bool LoadClipsFrom(string dir)
    {
        if (!DirAccess.DirExistsAbsolute(dir)) return false;
        using var d = DirAccess.Open(dir);
        if (d == null) return false;
        d.ListDirBegin();
        for (string f = d.GetNext(); !string.IsNullOrEmpty(f); f = d.GetNext())
        {
            if (d.CurrentIsDir()) continue;
            string lower = f.ToLowerInvariant();
            // Exported builds list the .import stub, not the source; strip it.
            if (lower.EndsWith(".import")) { f = f.Substring(0, f.Length - 7); lower = f.ToLowerInvariant(); }
            if (!(lower.EndsWith(".wav") || lower.EndsWith(".ogg"))) continue;
            var stream = ResourceLoader.Load<AudioStream>(dir + f);
            if (stream != null && !_bank.Contains(stream)) _bank.Add(stream);
        }
        d.ListDirEnd();
        return _bank.Count > 0;
    }

    /// <summary>One syllable: a random clip from the bank, pitched for the
    /// archetype with a little wobble, never closer than MinBlipGapSec to
    /// the previous one so fast typing cannot stack into a buzz.</summary>
    private void Blip(char ch)
    {
        if (!VoiceEnabled || _voice == null || _bank.Count == 0) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (_lastBlipAt >= 0 && now - _lastBlipAt < MinBlipGapSec) return;
        _lastBlipAt = now;

        int idx = (int)(GD.Randi() % (uint)_bank.Count);
        if (_bank.Count > 1 && idx == _lastClip) idx = (idx + 1) % _bank.Count;
        _lastClip = idx;

        bool vowel = "aeiouyAEIOUY".IndexOf(ch) >= 0;
        float wobble = 0.94f + GD.Randf() * 0.12f;
        _voice.Stream = _bank[idx];
        _voice.PitchScale = PitchFor(_archetype) * (vowel ? 1f : 1.06f) * wobble;
        _voice.Play();
    }

    // ── The rig ──────────────────────────────────────────────────────────

}

/// <summary>Draws the layered face. Each layer: PNG if present, else a
/// procedural stand-in. All layers share one square canvas. Top-level (not
/// nested) so Godot's source generator registers it without fuss.</summary>
public partial class NegotiationPortraitRig : Control
{
private const string ART_DIR = "res://Assets/Portraits/Negotiation/";

    public NpcExpression Expression = NpcExpression.Neutral;
    public bool MouthOpen = false;
    public bool EyesClosed = false;

    private Texture2D _head, _hair, _brow, _eyes, _eyesClosed, _mouth, _mouthOpen, _mouthSmile, _mouthFrown;
    private Color _skin = new Color(0.79f, 0.64f, 0.48f);
    private Color _hairColor = new Color(0.23f, 0.16f, 0.12f);
    private Color _ink = new Color(0.14f, 0.10f, 0.07f);
    private Color _lip = new Color(0.42f, 0.18f, 0.16f);

    public void Configure(NpcArchetypeType a)
    {
        string k = a.ToString().ToLowerInvariant();
        _head = Load($"{k}_head"); _hair = Load($"{k}_hair"); _brow = Load($"{k}_brow");
        _eyes = Load($"{k}_eyes"); _eyesClosed = Load($"{k}_eyes_closed");
        _mouth = Load($"{k}_mouth"); _mouthOpen = Load($"{k}_mouth_open");
        _mouthSmile = Load($"{k}_mouth_smile"); _mouthFrown = Load($"{k}_mouth_frown");
        (_skin, _hairColor) = a switch
        {
            NpcArchetypeType.Merchant    => (new Color(0.79f, 0.64f, 0.48f), new Color(0.23f, 0.16f, 0.12f)),
            NpcArchetypeType.Commander   => (new Color(0.72f, 0.58f, 0.46f), new Color(0.42f, 0.42f, 0.42f)),
            NpcArchetypeType.Scholar     => (new Color(0.86f, 0.76f, 0.66f), new Color(0.85f, 0.82f, 0.76f)),
            NpcArchetypeType.Opportunist => (new Color(0.62f, 0.48f, 0.38f), new Color(0.11f, 0.11f, 0.11f)),
            NpcArchetypeType.Idealist    => (new Color(0.84f, 0.70f, 0.58f), new Color(0.55f, 0.35f, 0.24f)),
            NpcArchetypeType.Survivor    => (new Color(0.68f, 0.56f, 0.44f), new Color(0.35f, 0.27f, 0.20f)),
            _                            => (_skin, _hairColor),
        };
    }

    private static Texture2D Load(string name)
    {
        string path = $"{ART_DIR}{name}.png";
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        float w = Size.X, h = Size.Y;
        Vector2 C(float x, float y) => new Vector2(x * w, y * h);   // normalized → px

        // Head
        if (_head != null) DrawTextureRect(_head, rect, false);
        else
        {
            DrawCircle(C(0.5f, 0.56f), w * 0.30f, _skin);
            DrawArc(C(0.5f, 0.56f), w * 0.30f, 0f, Mathf.Tau, 48, new Color(0, 0, 0, 0.18f), 2f, true);
        }
        // Hair
        if (_hair != null) DrawTextureRect(_hair, rect, false);
        else
        {
            var pts = new[] { C(0.20f, 0.46f), C(0.24f, 0.30f), C(0.36f, 0.20f), C(0.50f, 0.17f),
                              C(0.64f, 0.20f), C(0.76f, 0.30f), C(0.80f, 0.46f), C(0.72f, 0.40f),
                              C(0.50f, 0.32f), C(0.28f, 0.40f) };
            DrawColoredPolygon(pts, _hairColor);
        }
        // Brows: angle by expression.
        float browTilt = Expression switch { NpcExpression.Pleased => -0.10f, NpcExpression.Displeased => 0.16f, _ => 0f };
        float browLift = Expression switch { NpcExpression.Pleased => -0.02f, NpcExpression.Displeased => 0.015f, _ => 0f };
        if (_brow != null)
        {
            DrawSetTransform(C(0.38f, 0.40f + browLift), -browTilt, Vector2.One);
            DrawTextureRect(_brow, new Rect2(-w * 0.10f, -h * 0.03f, w * 0.20f, h * 0.06f), false);
            DrawSetTransform(C(0.62f, 0.40f + browLift), browTilt, new Vector2(-1f, 1f));
            DrawTextureRect(_brow, new Rect2(-w * 0.10f, -h * 0.03f, w * 0.20f, h * 0.06f), false);
            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }
        else
        {
            // Left brow: inner end (toward the nose) drops when displeased, lifts when pleased.
            DrawLine(C(0.30f, 0.40f + browLift - browTilt * 0.5f), C(0.46f, 0.39f + browLift + browTilt), _ink, 3f, true);
            DrawLine(C(0.54f, 0.39f + browLift + browTilt), C(0.70f, 0.40f + browLift - browTilt * 0.5f), _ink, 3f, true);
        }
        // Eyes
        if (EyesClosed && _eyesClosed != null) DrawTextureRect(_eyesClosed, rect, false);
        else if (!EyesClosed && _eyes != null) DrawTextureRect(_eyes, rect, false);
        else if (EyesClosed)
        {
            DrawLine(C(0.34f, 0.48f), C(0.42f, 0.48f), _ink, 2.5f, true);
            DrawLine(C(0.58f, 0.48f), C(0.66f, 0.48f), _ink, 2.5f, true);
        }
        else
        {
            DrawCircle(C(0.38f, 0.48f), w * 0.035f, _ink);
            DrawCircle(C(0.62f, 0.48f), w * 0.035f, _ink);
            DrawCircle(C(0.39f, 0.47f), w * 0.010f, Colors.White);
            DrawCircle(C(0.63f, 0.47f), w * 0.010f, Colors.White);
        }
        // Mouth
        Texture2D mouthTex = MouthOpen ? _mouthOpen
                           : Expression == NpcExpression.Pleased ? _mouthSmile
                           : Expression == NpcExpression.Displeased ? _mouthFrown
                           : _mouth;
        if (mouthTex != null) DrawTextureRect(mouthTex, rect, false);
        else if (MouthOpen)
        {
            var pts = new[] { C(0.42f, 0.68f), C(0.50f, 0.66f), C(0.58f, 0.68f), C(0.56f, 0.76f), C(0.50f, 0.79f), C(0.44f, 0.76f) };
            DrawColoredPolygon(pts, _lip);
        }
        else
        {
            float curve = Expression switch { NpcExpression.Pleased => 0.05f, NpcExpression.Displeased => -0.04f, _ => 0.015f };
            var pts = new Vector2[9];
            for (int i = 0; i < 9; i++)
            {
                float t = i / 8f;
                float x = 0.40f + 0.20f * t;
                float y = 0.71f + curve * (float)Math.Sin(t * Math.PI);
                pts[i] = C(x, y);
            }
            DrawPolyline(pts, _lip, 3f, true);
        }
    }
}
