using Godot;
using System;

public partial class CardUi : Control
{
    public Card CardInstance { get; private set; }
    public CardHalf TopHalf { get; private set; }
    public CardHalf BottomHalf { get; private set; }
    private DeckUiManager _deckUiManager;

    private Control _visualNode;
    private Control topArea;
    private Control bottomArea;

    // Hover animation
    private bool _cardIsLifted = false;
    private float _restRotation;
    private float _smoothBreathe = 0f;
    private Tween _cardTween;       // handles card-level lift/tilt/scale
    private Tween _halfTween;       // handles per-half highlight modulate

    // Card exit safety (prevents getting stuck in lifted state if mouse leaves card unexpectedly)
    private bool _entryTweenComplete = false;
    private float _notOnCardTimer = 0f;
    private const float StuckExitTimeout = 0.08f; // force exit if mouse gone for 80ms
    private int _restTransformGeneration = 0;

    // Half-hover debounce (prevents flickering at the boundary)
    private string _currentHalf  = "none";   // "top" | "bottom" | "none"
    private string _pendingHalf  = "none";
    private float  _halfTimer    = 0f;
    private const float HalfDebounce = 0.08f;   // seconds before a half-switch commits

    // Panels to tint
    private Panel topPanel;
    private Panel bottomPanel;

    // ── Full-card view (hover state) ─────────────────────────────────
    private Control _fullCardView;
    private Panel   _artPanel;
    private Label   _schoolBadge;
    private HBoxContainer _elementTagContainer;
    private Panel   _fullDivider;
    private Panel   _fullInfoPanel;
    private Label   _fullManaLabel;
    private Label   _fullNameLabel;
    private Label   _fullSpeedLabel;
    private RichTextLabel _fullRulesLabel;
    private Panel   _fullChannelPanel;
    private RichTextLabel _fullChannelLabel;
    private string  _fullViewHalf = "none"; // "top" | "bottom" | "none"

    // Affordability tracking
    private static readonly Color TopActiveColor   = new Color(1.3f, 1.2f, 0.9f, 1f); // warm gold glow
    private static readonly Color BottomActiveColor= new Color(1.1f, 1.0f, 1.4f, 1f); // cool purple glow
    private static readonly Color DimColor         = new Color(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Color NeutralColor     = new Color(1f, 1f, 1f, 1f);

    // Signals
    [Signal] public delegate void CardDroppedEventHandler();
    [Signal] public delegate void CardHalfSelectedEventHandler(CardUi cardUi, bool isTop);

    // Drag handling
    private bool _dragPressed = false;
    private bool _dragQueued = false;
    private bool _isDragging = false;
    private Vector2 _dragStartPosition;
    private Vector2 _originalPosition;
    public Vector2 _restPosition => _originalPosition; // Expose original position for external use (e.g., DeckUiManager)
    private const float DragThreshold = 10f;
    private bool _dragTopCard = false;
    private int _lastKnownMana = 999; // 999 = everything affordable until told otherwise
    public bool HasBeenPlaced { get; private set; } = false;

    public override void _Ready()
    {
        _visualNode  = GetNode<Control>("CardVisual");
        _originalPosition = Position;
        _restRotation = Rotation;

        topArea    = GetNode<Control>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl");
        bottomArea = GetNode<Control>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl");
        topPanel   = GetNode<Panel>("CardVisual/VBoxContainer/TopCardPanel");
        bottomPanel= GetNode<Panel>("CardVisual/VBoxContainer/BottomCardPanel");

        if (topPanel == null)    GD.PrintErr("CardUi: topPanel not found");
        if (bottomPanel == null) GD.PrintErr("CardUi: bottomPanel not found");

        // ── Cache full-card view nodes ──────────────────────────────
        _fullCardView        = GetNodeOrNull<Control>("CardVisual/FullCardView");
        _artPanel            = GetNodeOrNull<Panel>("CardVisual/FullCardView/ArtPanel");
        _schoolBadge         = GetNodeOrNull<Label>("CardVisual/FullCardView/ArtPanel/SchoolBadge");
        _elementTagContainer = GetNodeOrNull<HBoxContainer>("CardVisual/FullCardView/ArtPanel/ElementTagContainer");
        _fullDivider         = GetNodeOrNull<Panel>("CardVisual/FullCardView/FullDivider");
        _fullInfoPanel       = GetNodeOrNull<Panel>("CardVisual/FullCardView/InfoPanel");
        _fullManaLabel       = GetNodeOrNull<Label>("CardVisual/FullCardView/InfoPanel/InfoContainer/NameBar/ManaPip/ManaLabel");
        _fullNameLabel       = GetNodeOrNull<Label>("CardVisual/FullCardView/InfoPanel/InfoContainer/NameBar/SpellName");
        _fullSpeedLabel      = GetNodeOrNull<Label>("CardVisual/FullCardView/InfoPanel/InfoContainer/NameBar/SpeedLabel");
        _fullRulesLabel      = GetNodeOrNull<RichTextLabel>("CardVisual/FullCardView/InfoPanel/InfoContainer/RulesText");
        _fullChannelPanel    = GetNodeOrNull<Panel>("CardVisual/FullCardView/InfoPanel/InfoContainer/ChannelStrip");
        _fullChannelLabel    = GetNodeOrNull<RichTextLabel>("CardVisual/FullCardView/InfoPanel/InfoContainer/ChannelStrip/ChannelLabel");

        if (_fullCardView == null)
            GD.Print("CardUi: FullCardView not found — hover art disabled (add FullCardView to CardUI.tscn)");

        // Card-level: entering/leaving the whole card lifts or drops it
        topArea.MouseEntered    += OnCardEnter;
        bottomArea.MouseEntered += OnCardEnter;
        topArea.MouseExited     += OnCardMaybeExit;
        bottomArea.MouseExited  += OnCardMaybeExit;

        // CardUi.cs — at end of _Ready(), before rest transform is known
        Position = _originalPosition + new Vector2(0, 300f); // start below
        Modulate = new Color(1, 1, 1, 0);                    // start transparent

        topArea.GuiInput    += (e) => OnCardGuiInput(e, true);
        bottomArea.GuiInput += (e) => OnCardGuiInput(e, false);
    }

    public override void _Process(double delta)
    {
        if (_entryTweenComplete && !_isDragging)
        {
            float targetBreathe = Mathf.Sin(
                (float)Time.GetTicksMsec() / 1000f * 1.2f + GetIndex() * 0.8f) * 2.5f;
            _smoothBreathe = Mathf.Lerp(_smoothBreathe, targetBreathe, (float)delta * 12f);

            float liftOffset = _cardIsLifted ? -35f : 0f;
            Position = _originalPosition + new Vector2(0, liftOffset + _smoothBreathe);
        }

        if (!_cardIsLifted || _isDragging) return;

        Vector2 mouse = GetViewport().GetMousePosition();

        // Check if mouse is within the card's overall global rect expanded slightly
        Rect2 cardRect = GetGlobalRect().GrowIndividual(10, 40, 10, 10);
        bool onCard = cardRect.HasPoint(mouse);

        if (!onCard)
        {
            _notOnCardTimer += (float)delta;
            if (_notOnCardTimer >= StuckExitTimeout)
            {
                DoCardExit();
                return;
            }
        }
        else
        {
            _notOnCardTimer = 0f;
        }

        // Half highlight — use the actual current rects since we're confirmed on card
        string detected = topArea.GetGlobalRect().HasPoint(mouse)    ? "top"
                        : bottomArea.GetGlobalRect().HasPoint(mouse) ? "bottom"
                        : "none";

        if (detected == _currentHalf)
        {
            _pendingHalf = detected;
            _halfTimer   = 0f;
        }
        else if (detected != _pendingHalf)
        {
            _pendingHalf = detected;
            _halfTimer   = 0f;
        }
        else
        {
            _halfTimer += (float)delta;
            if (_halfTimer >= HalfDebounce)
            {
                _currentHalf = _pendingHalf;
                _halfTimer   = 0f;
                ApplyHalfHighlight(_currentHalf);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Full-card view (split-to-full hover animation)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Populates and shows the full-card overlay for the given half.
    /// Art on top, spell info on bottom. Completely covers the split layout.
    /// </summary>
    private void ShowFullCard(CardHalf half, bool isTop)
    {
        if (_fullCardView == null || half == null) return;

        var school = half.School;
        var borderColor = SchoolColors.GetBorderColor(school);
        var darkColor   = SchoolColors.GetDarkColor(school);

        // ── Art panel (placeholder color — swap for TextureRect when art is ready) ──
        if (_artPanel != null)
        {
            var artStyle = new StyleBoxFlat();
            // Dark version of school color as art placeholder
            artStyle.BgColor = new Color(
                borderColor.R * 0.35f,
                borderColor.G * 0.35f,
                borderColor.B * 0.35f,
                1f);
            artStyle.CornerRadiusTopLeft = 5;
            artStyle.CornerRadiusTopRight = 5;
            artStyle.BorderColor = borderColor;
            artStyle.BorderWidthLeft = 3;
            artStyle.BorderWidthTop = 3;
            artStyle.BorderWidthRight = 3;
            artStyle.BorderWidthBottom = 0;
            _artPanel.AddThemeStyleboxOverride("panel", artStyle);
        }

        // ── School badge ──
        if (_schoolBadge != null)
        {
            _schoolBadge.Text = SchoolColors.GetBadgeText(school);
            _schoolBadge.AddThemeColorOverride("font_color", Colors.White);
            _schoolBadge.AddThemeFontSizeOverride("font_size", 9);
            _schoolBadge.HorizontalAlignment = HorizontalAlignment.Center;
            _schoolBadge.VerticalAlignment = VerticalAlignment.Center;

            var badgeStyle = new StyleBoxFlat();
            badgeStyle.BgColor = borderColor;
            badgeStyle.SetCornerRadiusAll(9);
            _schoolBadge.AddThemeStyleboxOverride("normal", badgeStyle);
        }

        // ── Element tags ──
        if (_elementTagContainer != null)
        {
            // Clear existing pips
            foreach (Node c in _elementTagContainer.GetChildren())
                c.QueueFree();

            var tags = half.Tags ?? System.Array.Empty<string>();
            foreach (var tag in tags)
            {
                var pip = new Label();
                pip.Text = ElementColors.GetLabel(tag);
                pip.CustomMinimumSize = new Vector2(14, 14);
                pip.HorizontalAlignment = HorizontalAlignment.Center;
                pip.VerticalAlignment = VerticalAlignment.Center;
                pip.AddThemeFontSizeOverride("font_size", 8);
                pip.AddThemeColorOverride("font_color", Colors.White);

                var pipStyle = new StyleBoxFlat();
                pipStyle.BgColor = ElementColors.Get(tag);
                pipStyle.SetCornerRadiusAll(7);
                pipStyle.SetContentMarginAll(0);
                pip.AddThemeStyleboxOverride("normal", pipStyle);

                _elementTagContainer.AddChild(pip);
            }
        }

        // ── Divider color ──
        if (_fullDivider != null)
        {
            var divStyle = new StyleBoxFlat { BgColor = borderColor };
            _fullDivider.AddThemeStyleboxOverride("panel", divStyle);
        }

        // ── Info panel border ──
        if (_fullInfoPanel != null)
        {
            var infoStyle = new StyleBoxFlat();
            infoStyle.BgColor = Colors.White;
            infoStyle.BorderColor = borderColor;
            infoStyle.BorderWidthLeft = 3;
            infoStyle.BorderWidthRight = 3;
            infoStyle.BorderWidthBottom = 3;
            infoStyle.BorderWidthTop = 0;
            infoStyle.CornerRadiusBottomLeft = 5;
            infoStyle.CornerRadiusBottomRight = 5;
            _fullInfoPanel.AddThemeStyleboxOverride("panel", infoStyle);
        }

        // ── Mana pip styling ──
        var manaPipPanel = _fullManaLabel?.GetParent() as Panel;
        if (manaPipPanel != null)
        {
            var pipPanelStyle = new StyleBoxFlat();
            pipPanelStyle.BgColor = darkColor;
            pipPanelStyle.SetCornerRadiusAll(10);
            manaPipPanel.AddThemeStyleboxOverride("panel", pipPanelStyle);
        }

        // ── Spell info ──
        if (_fullManaLabel != null)
        {
            _fullManaLabel.Text = half.ManaCost.ToString();
            _fullManaLabel.AddThemeColorOverride("font_color", Colors.White);
        }
        if (_fullNameLabel != null)
        {
            _fullNameLabel.Text = half.Name ?? "";
            _fullNameLabel.AddThemeColorOverride("font_color", Colors.Black);
        }
        if (_fullSpeedLabel != null)
        {
            _fullSpeedLabel.Text = half.Speed.ToString();
            _fullSpeedLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.4f));
            _fullSpeedLabel.AddThemeFontSizeOverride("font_size", 10);
        }
        if (_fullRulesLabel != null)
        {
            _fullRulesLabel.Text = half.RulesText ?? "";
        }

        var chText = half.ChannelVariant?.RulesText ?? "";
        if (_fullChannelPanel != null)
        {
            _fullChannelPanel.Visible = !string.IsNullOrWhiteSpace(chText);

            // Tint channel strip with school color
            var chStyle = new StyleBoxFlat();
            chStyle.BgColor = new Color(borderColor.R, borderColor.G, borderColor.B, 0.12f);
            chStyle.SetCornerRadiusAll(3);
            _fullChannelPanel.AddThemeStyleboxOverride("panel", chStyle);
        }
        if (_fullChannelLabel != null)
        {
            _fullChannelLabel.Text = chText;
            _fullChannelLabel.AddThemeColorOverride("default_color", darkColor);
        }

        // ── Show the full view, occluding the split layout ──
        _fullCardView.Visible = true;
        _fullViewHalf = isTop ? "top" : "bottom";
    }

    /// <summary>
    /// Hides the full-card overlay, revealing the split layout underneath.
    /// </summary>
    private void HideFullCard()
    {
        if (_fullCardView != null)
            _fullCardView.Visible = false;
        _fullViewHalf = "none";
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Card data
    // ═════════════════════════════════════════════════════════════════════

    public void SetDeckUiManager(DeckUiManager manager)
    {
        _deckUiManager = manager;
    }

    public void SetCard(Card card)
    {
        CardInstance = card;
        SetCard(card.TopHalf, card.BottomHalf);
    }

    public void SetCard(CardHalf top, CardHalf bottom)
    {
        TopHalf = top;
        BottomHalf = bottom;

        // --- TOP ---
        var nameLabelTop = GetNodeOrNull<Label>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/HBoxContainer/NameLabel");
        var manaLabelTop = GetNodeOrNull<Label>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/HBoxContainer/ManaPanel/ManaLabel");
        var descLabelTop = GetNodeOrNull<RichTextLabel>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/DescriptionLabel");
        var channelPanelTop = GetNodeOrNull<Panel>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/ChannelPanel");
        var channelLabelTop = GetNodeOrNull<RichTextLabel>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/ChannelPanel/ChannelLabel");

        if (nameLabelTop != null) nameLabelTop.Text = top?.Name ?? "";
        if (manaLabelTop != null) manaLabelTop.Text = (top?.ManaCost ?? 0).ToString();
        if (descLabelTop != null) descLabelTop.Text = top?.RulesText ?? "";

        var topChannelText = top?.ChannelVariant?.RulesText ?? "";
        if (channelPanelTop != null) channelPanelTop.Visible = !string.IsNullOrWhiteSpace(topChannelText);
        if (channelLabelTop != null) channelLabelTop.Text = topChannelText;

        // --- BOTTOM ---
        var nameLabelBot = GetNodeOrNull<Label>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/NameHBoxContainer/NameLabel");
        var manaLabelBot = GetNodeOrNull<Label>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/NameHBoxContainer/ManaPanel/ManaLabel");
        var descLabelBot = GetNodeOrNull<RichTextLabel>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/DescriptionLabel");
        var channelPanelBot = GetNodeOrNull<Panel>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/ChannelPanel");
        var channelLabelBot = GetNodeOrNull<RichTextLabel>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/ChannelPanel/ChannelLabel");

        if (nameLabelBot != null) nameLabelBot.Text = bottom?.Name ?? "";
        if (manaLabelBot != null) manaLabelBot.Text = (bottom?.ManaCost ?? 0).ToString();
        if (descLabelBot != null) descLabelBot.Text = bottom?.RulesText ?? "";

        var botChannelText = bottom?.ChannelVariant?.RulesText ?? "";
        if (channelPanelBot != null) channelPanelBot.Visible = !string.IsNullOrWhiteSpace(botChannelText);
        if (channelLabelBot != null) channelLabelBot.Text = botChannelText;

        // ── Apply school-colored borders to split panels ────────────
        var school = top?.School ?? bottom?.School ?? CardSchool.Generic;
        var borderCol = SchoolColors.GetBorderColor(school);

        if (topPanel != null)
        {
            var topStyle = new StyleBoxFlat();
            topStyle.BgColor = Colors.White;
            topStyle.BorderColor = borderCol;
            topStyle.BorderWidthLeft = 5;
            topStyle.BorderWidthTop = 5;
            topStyle.BorderWidthRight = 5;
            topStyle.BorderWidthBottom = 2;
            topStyle.CornerRadiusTopLeft = 5;
            topStyle.CornerRadiusTopRight = 5;
            topStyle.CornerRadiusBottomLeft = 0;
            topStyle.CornerRadiusBottomRight = 0;
            topPanel.AddThemeStyleboxOverride("panel", topStyle);
        }

        if (bottomPanel != null)
        {
            var botStyle = new StyleBoxFlat();
            botStyle.BgColor = Colors.White;
            botStyle.BorderColor = borderCol;
            botStyle.BorderWidthLeft = 5;
            botStyle.BorderWidthTop = 2;
            botStyle.BorderWidthRight = 5;
            botStyle.BorderWidthBottom = 5;
            botStyle.CornerRadiusTopLeft = 0;
            botStyle.CornerRadiusTopRight = 0;
            botStyle.CornerRadiusBottomLeft = 5;
            botStyle.CornerRadiusBottomRight = 5;
            bottomPanel.AddThemeStyleboxOverride("panel", botStyle);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Hover / lift
    // ═════════════════════════════════════════════════════════════════════

    private void OnCardEnter()
    {
        if (_cardIsLifted) return;
        if (!_entryTweenComplete) return;

        _cardIsLifted = true;
        _notOnCardTimer = 0f;

        _cardTween?.Kill();
        _cardTween = CreateTween().SetParallel(true);
        _cardTween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        _cardTween.TweenProperty(this, "rotation", _restRotation * 0.2f, 0.15f);
        _cardTween.TweenProperty(this, "scale", new Vector2(1.05f, 1.05f), 0.15f);
        ZIndex = 100;

        _deckUiManager?.OnCardHoverChanged(this, true);
    }

    private void OnCardMaybeExit()
    {
        CallDeferred(nameof(CheckCardExit));
    }

    private void CheckCardExit()
    {
        Vector2 mouse = GetViewport().GetMousePosition();
        bool stillOnCard = topArea.GetGlobalRect().HasPoint(mouse) ||
                        bottomArea.GetGlobalRect().HasPoint(mouse);
        if (stillOnCard) return;

        DoCardExit();
    }

    private void DoCardExit()
    {
        if (!_cardIsLifted) return;

        _cardIsLifted = false;
        _notOnCardTimer = 0f;
        _currentHalf  = "none";
        _pendingHalf  = "none";
        _halfTimer    = 0f;

        // Hide the full-card overlay, revealing the split layout
        HideFullCard();

        _cardTween?.Kill();
        _cardTween = CreateTween().SetParallel(true);
        _cardTween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        _cardTween.TweenProperty(this, "rotation", _restRotation, 0.12f);
        _cardTween.TweenProperty(this, "scale", Vector2.One, 0.12f);
        ZIndex = 0;

        _deckUiManager?.OnCardHoverChanged(this, false);
        RefreshAffordability(_lastKnownMana);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Half highlight + full-card toggle
    // ═════════════════════════════════════════════════════════════════════

    private void ApplyHalfHighlight(string activeHalf)
    {
        _halfTween?.Kill();
        _halfTween = CreateTween().SetParallel(true);
        _halfTween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);

        if (activeHalf == "top")
        {
            _halfTween.TweenProperty(topPanel,    "modulate", TopActiveColor,    0.1f);
            _halfTween.TweenProperty(bottomPanel, "modulate", DimColor,          0.1f);
            ShowFullCard(TopHalf, true);
        }
        else if (activeHalf == "bottom")
        {
            _halfTween.TweenProperty(topPanel,    "modulate", DimColor,          0.1f);
            _halfTween.TweenProperty(bottomPanel, "modulate", BottomActiveColor, 0.1f);
            ShowFullCard(BottomHalf, false);
        }
        else
        {
            // Restore to affordability colors
            var topBase    = (TopHalf?.ManaCost    ?? 0) > _lastKnownMana
                ? new Color(0.7f, 0.5f, 0.5f, 1f) : Colors.White;
            var bottomBase = (BottomHalf?.ManaCost ?? 0) > _lastKnownMana
                ? new Color(0.7f, 0.5f, 0.5f, 1f) : Colors.White;

            _halfTween.TweenProperty(topPanel,    "modulate", topBase,    0.1f);
            _halfTween.TweenProperty(bottomPanel, "modulate", bottomBase, 0.1f);
            HideFullCard();
        }
    }

    public void RefreshAffordability(int currentMana)
    {
        _lastKnownMana = currentMana;

        topPanel.Modulate    = (TopHalf?.ManaCost    ?? 0) > currentMana
            ? new Color(0.7f, 0.5f, 0.5f, 1f) : Colors.White;
        bottomPanel.Modulate = (BottomHalf?.ManaCost ?? 0) > currentMana
            ? new Color(0.7f, 0.5f, 0.5f, 1f) : Colors.White;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Rest transform / draw-in animation
    // ═════════════════════════════════════════════════════════════════════

    public void SetRestTransform(Vector2 position, float rotation)
    {
        bool isFirstPlacement = !HasBeenPlaced;
        HasBeenPlaced = true;

        _originalPosition = position;
        _restRotation     = rotation;
        _entryTweenComplete = false;
        _cardIsLifted = false;
        _notOnCardTimer = 0f;

        int generation = ++_restTransformGeneration;

        if (isFirstPlacement)
        {
            Vector2 screenSize = GetViewport().GetVisibleRect().Size;
            Position  = new Vector2(position.X, screenSize.Y + 50f);
            Rotation  = rotation;
            Modulate  = new Color(1, 1, 1, 0f);
            Scale     = Vector2.One;

            float delay = GetIndex() * 0.09f;

            var tween = CreateTween().SetParallel(true);
            tween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
            tween.TweenProperty(this, "position", position, 0.45f).SetDelay(delay);
            tween.TweenProperty(this, "rotation", rotation, 0.45f).SetDelay(delay);
            tween.TweenProperty(this, "modulate", Colors.White, 0.30f)
                .SetDelay(delay + 0.10f);

            var timer = GetTree().CreateTimer(delay + 0.45f);
            timer.Timeout += () =>
            {
                if (generation == _restTransformGeneration)
                    _entryTweenComplete = true;
            };
        }
        else
        {
            _entryTweenComplete = true;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Drag handling
    // ═════════════════════════════════════════════════════════════════════

    private void OnCardGuiInput(InputEvent @event, bool isTop)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _dragPressed = true;
                _dragQueued  = false;
                _dragStartPosition = mb.GlobalPosition;
                _dragTopCard = isTop;
            }
            else
            {
                _dragPressed = false;
                if (!_dragQueued)
                    SnapBack();
            }
        }
        else if (_dragPressed && @event is InputEventMouseMotion motion)
        {
            if (!_dragQueued &&
                (motion.GlobalPosition - _dragStartPosition).Length() > DragThreshold)
            {
                _dragQueued = true;
                PlayGrabAnimation();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private void PlayGrabAnimation()
    {
        _cardTween?.Kill();
        _cardTween = CreateTween().SetParallel(true);
        _cardTween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);

        _isDragging = true;

        // Hide full card view during drag
        HideFullCard();

        float tiltDir = _dragTopCard ? -0.06f : 0.06f;
        _cardTween.TweenProperty(this, "rotation",
            _restRotation * 0.2f + tiltDir, 0.12f);
        _cardTween.TweenProperty(this, "scale",
            new Vector2(0.92f, 0.92f), 0.12f);
        _cardTween.TweenProperty(this, "modulate",
            new Color(1.1f, 1.1f, 1.1f, 0.85f), 0.12f);
        ZIndex = 200;
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        _isDragging = true;
        DragPayloadManager.DraggedCard = this;
        DragPayloadManager.IsTopHalf   = _dragTopCard;
        DragPayloadManager.IsDragging  = true;

        return new Godot.Collections.Dictionary
        {
            { "card", this },
            { "is_top", _dragTopCard }
        };
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (data.Obj is Godot.Collections.Dictionary dict)
        {
            return dict.ContainsKey("card");
        }
        return false;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (data.Obj is Godot.Collections.Dictionary dict)
        {
            if (dict.ContainsKey("card") && dict["card"] is object cardObj && cardObj is CardUi)
            {
                CardUi droppedCard = (CardUi)cardObj;

                droppedCard._isDragging = false;
                droppedCard.Modulate = Colors.White;

                var container = GetParent();
                if (container is Control control)
                {
                    int dropIndex = control.GetChildren().IndexOf(this);
                    control.RemoveChild(droppedCard);
                    control.AddChild(droppedCard);
                    control.MoveChild(droppedCard, dropIndex);
                    droppedCard.SnapBack();
                    EmitSignal(SignalName.CardDropped);
                }
            }
        }
        DragPayloadManager.IsDragging = false;
    }

    private void SnapBack()
    {
        _isDragging = false;
        DoCardExit();
        DragPayloadManager.IsDragging = false;
        EmitSignal(SignalName.CardDropped);
    }
}
