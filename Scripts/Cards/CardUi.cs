using Godot;
using System;

public partial class CardUi : Control
{
    public Card CardInstance { get; private set; }
    public CardHalf TopHalf { get; private set; }
    public CardHalf BottomHalf { get; private set; }
    private DeckUiManager _deckUiManager;

    private Control _visualNode;
    private Control topArea;   // TopControl — mouse hover zone for top half
    private Control bottomArea; // BottomControl — mouse hover zone for bottom half

    // Hover animation
    private bool _cardIsLifted = false;
    private float _restRotation;
    private float _smoothBreathe = 0f;
    private Tween _cardTween;
    private Tween _halfTween;

    // Card exit safety
    private bool _entryTweenComplete = false;
    private float _notOnCardTimer = 0f;
    private const float StuckExitTimeout = 0.08f;
    private int _restTransformGeneration = 0;

    // Half-hover debounce
    private string _currentHalf = "none";
    private string _pendingHalf = "none";
    private float _halfTimer = 0f;
    private const float HalfDebounce = 0.08f;

    // ── Split view panels (for tinting) ──────────────────────────────
    private Panel _topPanel;
    private Panel _bottomPanel;
    private ColorRect _splitDivider;

    // ── Split view labels (top half) ─────────────────────────────────
    private Label _topManaLabel;
    private Label _topNameLabel;
    private Label _topSpeedLabel;
    private RichTextLabel _topRulesLabel;
    private Panel _topChannelPanel;
    private RichTextLabel _topChannelLabel;

    // ── Split view labels (bottom half) ──────────────────────────────
    private Label _botManaLabel;
    private Label _botNameLabel;
    private Label _botSpeedLabel;
    private RichTextLabel _botRulesLabel;
    private Panel _botChannelPanel;
    private RichTextLabel _botChannelLabel;

    // ── Full-card view (hover state) ─────────────────────────────────
    private Control _fullCardView;
    private Panel _artPanel;
    private Label _schoolBadge;
    private HBoxContainer _elementTagContainer;
    private Panel _fullDivider;
    private Panel _fullInfoPanel;
    private Label _fullManaLabel;
    private Label _fullNameLabel;
    private Label _fullSpeedLabel;
    private RichTextLabel _fullRulesLabel;
    private Panel _fullChannelPanel;
    private RichTextLabel _fullChannelLabel;
    private string _fullViewHalf = "none";

    // Affordability tracking
    private static readonly Color TopActiveColor = new Color(1.3f, 1.2f, 0.9f, 1f);
    private static readonly Color BottomActiveColor = new Color(1.1f, 1.0f, 1.4f, 1f);
    private static readonly Color DimColor = new Color(0.55f, 0.55f, 0.55f, 1f);

    // Signals
    [Signal] public delegate void CardDroppedEventHandler();
    [Signal] public delegate void CardHalfSelectedEventHandler(CardUi cardUi, bool isTop);

    // Drag handling
    private bool _dragPressed = false;
    private bool _dragQueued = false;
    private bool _isDragging = false;
    private Vector2 _dragStartPosition;
    private Vector2 _originalPosition;
    public Vector2 _restPosition => _originalPosition;
    private const float DragThreshold = 10f;
    private bool _dragTopCard = false;
    private int _lastKnownMana = 999;
    public bool HasBeenPlaced { get; private set; } = false;

    // ═════════════════════════════════════════════════════════════════════
    //  Node paths (centralized for easy maintenance)
    // ═════════════════════════════════════════════════════════════════════

    private const string SplitTop = "CardVisual/SplitView/TopPanel/TopControl/TopContainer";
    private const string SplitBot = "CardVisual/SplitView/BottomPanel/BottomControl/BottomContainer";
    private const string Full = "CardVisual/FullCardView";

    public override void _Ready()
    {
        _visualNode = GetNode<Control>("CardVisual");
        _originalPosition = Position;
        _restRotation = Rotation;

        // ── Split view: hover zones ─────────────────────────────────
        topArea = GetNode<Control>("CardVisual/SplitView/TopPanel/TopControl");
        bottomArea = GetNode<Control>("CardVisual/SplitView/BottomPanel/BottomControl");

        // ── Split view: panels for tinting ──────────────────────────
        _topPanel = GetNode<Panel>("CardVisual/SplitView/TopPanel");
        _bottomPanel = GetNode<Panel>("CardVisual/SplitView/BottomPanel");
        _splitDivider = GetNodeOrNull<ColorRect>("CardVisual/SplitView/SplitDivider");

        // ── Split view: top half labels ─────────────────────────────
        _topManaLabel = GetNodeOrNull<Label>($"{SplitTop}/NameBar/ManaPip/ManaLabel");
        _topNameLabel = GetNodeOrNull<Label>($"{SplitTop}/NameBar/SpellName");
        _topSpeedLabel = GetNodeOrNull<Label>($"{SplitTop}/NameBar/SpeedLabel");
        _topRulesLabel = GetNodeOrNull<RichTextLabel>($"{SplitTop}/RulesText");
        _topChannelPanel = GetNodeOrNull<Panel>($"{SplitTop}/ChannelStrip");
        _topChannelLabel = GetNodeOrNull<RichTextLabel>($"{SplitTop}/ChannelStrip/ChannelLabel");

        // ── Split view: bottom half labels ──────────────────────────
        _botManaLabel = GetNodeOrNull<Label>($"{SplitBot}/NameBar/ManaPip/ManaLabel");
        _botNameLabel = GetNodeOrNull<Label>($"{SplitBot}/NameBar/SpellName");
        _botSpeedLabel = GetNodeOrNull<Label>($"{SplitBot}/NameBar/SpeedLabel");
        _botRulesLabel = GetNodeOrNull<RichTextLabel>($"{SplitBot}/RulesText");
        _botChannelPanel = GetNodeOrNull<Panel>($"{SplitBot}/ChannelStrip");
        _botChannelLabel = GetNodeOrNull<RichTextLabel>($"{SplitBot}/ChannelStrip/ChannelLabel");

        // ── Full card view ──────────────────────────────────────────
        _fullCardView = GetNodeOrNull<Control>(Full);
        _artPanel = GetNodeOrNull<Panel>($"{Full}/ArtPanel");
        _schoolBadge = GetNodeOrNull<Label>($"{Full}/ArtPanel/SchoolBadge");
        _elementTagContainer = GetNodeOrNull<HBoxContainer>($"{Full}/ArtPanel/ElementTagContainer");
        _fullDivider = GetNodeOrNull<Panel>($"{Full}/FullDivider");
        _fullInfoPanel = GetNodeOrNull<Panel>($"{Full}/InfoPanel");
        _fullManaLabel = GetNodeOrNull<Label>($"{Full}/InfoPanel/InfoContainer/NameBar/ManaPip/ManaLabel");
        _fullNameLabel = GetNodeOrNull<Label>($"{Full}/InfoPanel/InfoContainer/NameBar/SpellName");
        _fullSpeedLabel = GetNodeOrNull<Label>($"{Full}/InfoPanel/InfoContainer/NameBar/SpeedLabel");
        _fullRulesLabel = GetNodeOrNull<RichTextLabel>($"{Full}/InfoPanel/InfoContainer/RulesText");
        _fullChannelPanel = GetNodeOrNull<Panel>($"{Full}/InfoPanel/InfoContainer/ChannelStrip");
        _fullChannelLabel = GetNodeOrNull<RichTextLabel>($"{Full}/InfoPanel/InfoContainer/ChannelStrip/ChannelLabel");

        if (_fullCardView == null)
            GD.Print("CardUi: FullCardView not found — hover art disabled");

        // ── Mouse events ────────────────────────────────────────────
        topArea.MouseEntered += OnCardEnter;
        bottomArea.MouseEntered += OnCardEnter;
        topArea.MouseExited += OnCardMaybeExit;
        bottomArea.MouseExited += OnCardMaybeExit;
        topArea.GuiInput += (e) => OnCardGuiInput(e, true);
        bottomArea.GuiInput += (e) => OnCardGuiInput(e, false);

        // Start off-screen for draw-in animation
        Position = _originalPosition + new Vector2(0, 300f);
        Modulate = new Color(1, 1, 1, 0);
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

        string detected = topArea.GetGlobalRect().HasPoint(mouse) ? "top"
                        : bottomArea.GetGlobalRect().HasPoint(mouse) ? "bottom"
                        : "none";

        if (detected == _currentHalf)
        {
            _pendingHalf = detected;
            _halfTimer = 0f;
        }
        else if (detected != _pendingHalf)
        {
            _pendingHalf = detected;
            _halfTimer = 0f;
        }
        else
        {
            _halfTimer += (float)delta;
            if (_halfTimer >= HalfDebounce)
            {
                _currentHalf = _pendingHalf;
                _halfTimer = 0f;
                ApplyHalfHighlight(_currentHalf);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Card data — populate both split halves and prepare full card view
    // ═════════════════════════════════════════════════════════════════════

    public void SetDeckUiManager(DeckUiManager manager) => _deckUiManager = manager;

    public void SetCard(Card card)
    {
        CardInstance = card;
        SetCard(card.TopHalf, card.BottomHalf);
    }

    public void SetCard(CardHalf top, CardHalf bottom)
    {
        TopHalf = top;
        BottomHalf = bottom;

        // ── Populate top split half ─────────────────────────────────
        PopulateSplitHalf(top,
            _topManaLabel, _topNameLabel, _topSpeedLabel,
            _topRulesLabel, _topChannelPanel, _topChannelLabel);

        // ── Populate bottom split half ──────────────────────────────
        PopulateSplitHalf(bottom,
            _botManaLabel, _botNameLabel, _botSpeedLabel,
            _botRulesLabel, _botChannelPanel, _botChannelLabel);

        // ── Apply school-colored borders ────────────────────────────
        var school = top?.School ?? bottom?.School ?? CardSchool.Generic;
        var borderCol = SchoolColors.GetBorderColor(school);
        var darkCol = SchoolColors.GetDarkColor(school);

        ApplyPanelBorder(_topPanel, borderCol, true);
        ApplyPanelBorder(_bottomPanel, borderCol, false);

        if (_splitDivider != null)
            _splitDivider.Color = borderCol;

        // Style mana pips with school dark color
        StyleManaPip($"{SplitTop}/NameBar/ManaPip", darkCol);
        StyleManaPip($"{SplitBot}/NameBar/ManaPip", darkCol);

        // Style channel strips with school color
        StyleChannelStrip(_topChannelPanel, borderCol, darkCol);
        StyleChannelStrip(_botChannelPanel, borderCol, darkCol);
    }

    private void PopulateSplitHalf(CardHalf half,
        Label mana, Label name, Label speed,
        RichTextLabel rules, Panel channelPanel, RichTextLabel channelLabel)
    {
        if (mana != null) mana.Text = (half?.ManaCost ?? 0).ToString();
        if (name != null) name.Text = half?.Name ?? "";
        if (speed != null) speed.Text = half?.Speed.ToString() ?? "Sorcery";
        if (rules != null) rules.Text = half?.RulesText ?? "";

        var chText = half?.ChannelVariant?.RulesText ?? "";
        if (channelPanel != null) channelPanel.Visible = !string.IsNullOrWhiteSpace(chText);
        if (channelLabel != null) channelLabel.Text = chText;
    }

    private void ApplyPanelBorder(Panel panel, Color borderCol, bool isTop)
    {
        if (panel == null) return;
        var style = new StyleBoxFlat();
        style.BgColor = Colors.White;
        style.BorderColor = borderCol;
        style.BorderWidthLeft = 3;
        style.BorderWidthRight = 3;
        if (isTop)
        {
            style.BorderWidthTop = 3;
            style.BorderWidthBottom = 2;
            style.CornerRadiusTopLeft = 5;
            style.CornerRadiusTopRight = 5;
        }
        else
        {
            style.BorderWidthTop = 2;
            style.BorderWidthBottom = 3;
            style.CornerRadiusBottomLeft = 5;
            style.CornerRadiusBottomRight = 5;
        }
        panel.AddThemeStyleboxOverride("panel", style);
    }

    private void StyleManaPip(string pipPanelPath, Color darkCol)
    {
        var pip = GetNodeOrNull<Panel>(pipPanelPath);
        if (pip == null) return;
        var s = new StyleBoxFlat { BgColor = darkCol };
        s.SetCornerRadiusAll(10);
        pip.AddThemeStyleboxOverride("panel", s);
    }

    private void StyleChannelStrip(Panel panel, Color borderCol, Color darkCol)
    {
        if (panel == null) return;
        var s = new StyleBoxFlat();
        s.BgColor = new Color(borderCol.R, borderCol.G, borderCol.B, 0.12f);
        s.SetCornerRadiusAll(3);
        panel.AddThemeStyleboxOverride("panel", s);

        var label = panel.GetNodeOrNull<RichTextLabel>("ChannelLabel");
        if (label != null)
            label.AddThemeColorOverride("default_color", darkCol);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Full-card view (hover state — art on top, info on bottom)
    // ═════════════════════════════════════════════════════════════════════

    private void ShowFullCard(CardHalf half, bool isTop)
    {
        if (_fullCardView == null || half == null) return;

        var school = half.School;
        var borderColor = SchoolColors.GetBorderColor(school);
        var darkColor = SchoolColors.GetDarkColor(school);

        // Art panel placeholder
        if (_artPanel != null)
        {
            var artStyle = new StyleBoxFlat();
            artStyle.BgColor = new Color(
                borderColor.R * 0.35f,
                borderColor.G * 0.35f,
                borderColor.B * 0.35f, 1f);
            artStyle.CornerRadiusTopLeft = 5;
            artStyle.CornerRadiusTopRight = 5;
            artStyle.BorderColor = borderColor;
            artStyle.BorderWidthLeft = 3;
            artStyle.BorderWidthTop = 3;
            artStyle.BorderWidthRight = 3;
            artStyle.BorderWidthBottom = 0;
            _artPanel.AddThemeStyleboxOverride("panel", artStyle);
        }

        // School badge
        if (_schoolBadge != null)
        {
            _schoolBadge.Text = SchoolColors.GetBadgeText(school);
            var badgeStyle = new StyleBoxFlat { BgColor = borderColor };
            badgeStyle.SetCornerRadiusAll(9);
            _schoolBadge.AddThemeStyleboxOverride("normal", badgeStyle);
        }

        // Element tags
        if (_elementTagContainer != null)
        {
            foreach (Node c in _elementTagContainer.GetChildren())
                c.QueueFree();

            foreach (var tag in half.Tags ?? Array.Empty<string>())
            {
                var pip = new Label();
                pip.Text = ElementColors.GetLabel(tag);
                pip.CustomMinimumSize = new Vector2(14, 14);
                pip.HorizontalAlignment = HorizontalAlignment.Center;
                pip.VerticalAlignment = VerticalAlignment.Center;
                pip.AddThemeFontSizeOverride("font_size", 8);
                pip.AddThemeColorOverride("font_color", Colors.White);
                var pipStyle = new StyleBoxFlat { BgColor = ElementColors.Get(tag) };
                pipStyle.SetCornerRadiusAll(7);
                pip.AddThemeStyleboxOverride("normal", pipStyle);
                _elementTagContainer.AddChild(pip);
            }
        }

        // Divider
        if (_fullDivider != null)
        {
            var divStyle = new StyleBoxFlat { BgColor = borderColor };
            _fullDivider.AddThemeStyleboxOverride("panel", divStyle);
        }

        // Info panel border
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

        // Mana pip
        var manaPipPanel = _fullManaLabel?.GetParent() as Panel;
        if (manaPipPanel != null)
        {
            var pipStyle = new StyleBoxFlat { BgColor = darkColor };
            pipStyle.SetCornerRadiusAll(10);
            manaPipPanel.AddThemeStyleboxOverride("panel", pipStyle);
        }

        // Spell info
        if (_fullManaLabel != null) _fullManaLabel.Text = half.ManaCost.ToString();
        if (_fullNameLabel != null) _fullNameLabel.Text = half.Name ?? "";
        if (_fullSpeedLabel != null) _fullSpeedLabel.Text = half.Speed.ToString();
        if (_fullRulesLabel != null) _fullRulesLabel.Text = half.RulesText ?? "";

        var chText = half.ChannelVariant?.RulesText ?? "";
        if (_fullChannelPanel != null)
        {
            _fullChannelPanel.Visible = !string.IsNullOrWhiteSpace(chText);
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

        _fullCardView.Visible = true;
        _fullViewHalf = isTop ? "top" : "bottom";
    }

    private void HideFullCard()
    {
        if (_fullCardView != null)
            _fullCardView.Visible = false;
        _fullViewHalf = "none";
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Hover / lift
    // ═════════════════════════════════════════════════════════════════════

    private void OnCardEnter()
    {
        if (_cardIsLifted || !_entryTweenComplete) return;

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

    private void OnCardMaybeExit() => CallDeferred(nameof(CheckCardExit));

    private void CheckCardExit()
    {
        Vector2 mouse = GetViewport().GetMousePosition();
        if (topArea.GetGlobalRect().HasPoint(mouse) ||
            bottomArea.GetGlobalRect().HasPoint(mouse)) return;
        DoCardExit();
    }

    private void DoCardExit()
    {
        if (!_cardIsLifted) return;

        _cardIsLifted = false;
        _notOnCardTimer = 0f;
        _currentHalf = "none";
        _pendingHalf = "none";
        _halfTimer = 0f;

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
            _halfTween.TweenProperty(_topPanel, "modulate", TopActiveColor, 0.1f);
            _halfTween.TweenProperty(_bottomPanel, "modulate", DimColor, 0.1f);
            ShowFullCard(TopHalf, true);
        }
        else if (activeHalf == "bottom")
        {
            _halfTween.TweenProperty(_topPanel, "modulate", DimColor, 0.1f);
            _halfTween.TweenProperty(_bottomPanel, "modulate", BottomActiveColor, 0.1f);
            ShowFullCard(BottomHalf, false);
        }
        else
        {
            var topBase = (TopHalf?.ManaCost ?? 0) > _lastKnownMana
                ? new Color(0.7f, 0.5f, 0.5f, 1f) : Colors.White;
            var botBase = (BottomHalf?.ManaCost ?? 0) > _lastKnownMana
                ? new Color(0.7f, 0.5f, 0.5f, 1f) : Colors.White;
            _halfTween.TweenProperty(_topPanel, "modulate", topBase, 0.1f);
            _halfTween.TweenProperty(_bottomPanel, "modulate", botBase, 0.1f);
            HideFullCard();
        }
    }

    public void RefreshAffordability(int currentMana)
    {
        _lastKnownMana = currentMana;
        _topPanel.Modulate = (TopHalf?.ManaCost ?? 0) > currentMana
            ? new Color(0.7f, 0.5f, 0.5f, 1f) : Colors.White;
        _bottomPanel.Modulate = (BottomHalf?.ManaCost ?? 0) > currentMana
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
        _restRotation = rotation;
        _entryTweenComplete = false;
        _cardIsLifted = false;
        _notOnCardTimer = 0f;
        int generation = ++_restTransformGeneration;

        if (isFirstPlacement)
        {
            Vector2 screenSize = GetViewport().GetVisibleRect().Size;
            Position = new Vector2(position.X, screenSize.Y + 50f);
            Rotation = rotation;
            Modulate = new Color(1, 1, 1, 0f);
            Scale = Vector2.One;

            float delay = GetIndex() * 0.09f;
            var tween = CreateTween().SetParallel(true);
            tween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
            tween.TweenProperty(this, "position", position, 0.45f).SetDelay(delay);
            tween.TweenProperty(this, "rotation", rotation, 0.45f).SetDelay(delay);
            tween.TweenProperty(this, "modulate", Colors.White, 0.30f).SetDelay(delay + 0.10f);

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
                _dragQueued = false;
                _dragStartPosition = mb.GlobalPosition;
                _dragTopCard = isTop;
            }
            else
            {
                _dragPressed = false;
                if (!_dragQueued) SnapBack();
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
        HideFullCard();

        float tiltDir = _dragTopCard ? -0.06f : 0.06f;
        _cardTween.TweenProperty(this, "rotation", _restRotation * 0.2f + tiltDir, 0.12f);
        _cardTween.TweenProperty(this, "scale", new Vector2(0.92f, 0.92f), 0.12f);
        _cardTween.TweenProperty(this, "modulate", new Color(1.1f, 1.1f, 1.1f, 0.85f), 0.12f);
        ZIndex = 200;
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        _isDragging = true;
        DragPayloadManager.DraggedCard = this;
        DragPayloadManager.IsTopHalf = _dragTopCard;
        DragPayloadManager.IsDragging = true;
        return new Godot.Collections.Dictionary
        {
            { "card", this },
            { "is_top", _dragTopCard }
        };
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        return data.Obj is Godot.Collections.Dictionary dict && dict.ContainsKey("card");
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (data.Obj is Godot.Collections.Dictionary dict &&
            dict.ContainsKey("card") && dict["card"] is CardUi droppedCard)
        {
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
