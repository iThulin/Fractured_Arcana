using Godot;
using System;

// ============================================================
// CardUi.cs
//
// Purpose:        Control node that renders one Card in the hand —
//                 split-view labels, full-card hover state, mana
//                 affordability tint, lift/drag animations, and the
//                 drag-and-drop payload for play.
// Layer:          UI
// Collaborators:  DeckUiManager.cs (instantiates these into the hand
//                 and drives layout / hover propagation),
//                 CombatManager.cs (consumes CardHalfHovered /
//                 CardHalfSelected / CardDropped signals),
//                 CardDropHandler.cs, DragPayloadManager.cs (drag state)
// See:            README §3 (Architecture Overview),
//                 README §8 (Godot 4.6 compat — CallDeferred on exits)
// ============================================================

/// <summary>
/// Visual representation of a single <see cref="Card"/> in the player's hand. Owns the
/// split-view panels, the hover-triggered full-card view, mana affordability tinting,
/// the lift/breathe animations, and the drag-and-drop payload. Holds no game state of
/// its own — the underlying <see cref="Card"/> is set via <see cref="SetCard(Card)"/>
/// and any rules logic lives elsewhere (CombatManager, DeckManager).
/// </summary>
public partial class CardUi : Control
{
    /// <summary>The runtime <see cref="Card"/> this UI is currently displaying. Null until <see cref="SetCard(Card)"/> is called.</summary>
    public Card CardInstance { get; private set; }

    /// <summary>Top half of <see cref="CardInstance"/>. Cached on <see cref="SetCard(Card)"/> so the hover/drag code can read it without re-walking the Card.</summary>
    public CardHalf TopHalf { get; private set; }

    /// <summary>Bottom half of <see cref="CardInstance"/>. Cached on <see cref="SetCard(Card)"/>.</summary>
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
    private bool _isDiscardFlagged = false;
    private Tween _amberPulseTween;

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
    private bool _lastHoveredTop = true;

    // ── Split view panels (for tinting) ──────────────────────────────
    private Control _splitView;
    private Panel _topPanel;
    private Panel _bottomPanel;
    private ColorRect _splitDivider;
    private Tween _transitionTween; // crossfade between split and full view

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

    // Signals
    [Signal] public delegate void CardDroppedEventHandler();
    [Signal] public delegate void CardHalfSelectedEventHandler(CardUi cardUi, bool isTop);
    [Signal] public delegate void CardHalfHoveredEventHandler(CardUi cardUi, bool isTop, bool isEntering);

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
    private bool _isReady = false;
    private Card _pendingCard = null;

    // ── Node paths (centralized for easy maintenance) ──────────────────────

    private const string SplitTop = "CardVisual/SplitView/TopPanel/TopControl/TopContainer";
    private const string SplitBot = "CardVisual/SplitView/BottomPanel/BottomControl/BottomContainer";
    private const string Full = "CardVisual/FullCardView";

    public override void _Ready()
    {
        _visualNode = GetNode<Control>("CardVisual");
        _originalPosition = Position;
        _restRotation = Rotation;

        // ── Split view: hover zones ─────────────────────────────────
        _splitView = GetNode<Control>("CardVisual/SplitView");
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
        bottomArea.MouseEntered += () => { _lastHoveredTop = false; OnCardEnter(); EmitSignal(SignalName.CardHalfHovered, this, _lastHoveredTop, true); };
        topArea.MouseEntered += () => { _lastHoveredTop = true; OnCardEnter(); EmitSignal(SignalName.CardHalfHovered, this, _lastHoveredTop, true); };

        // Start off-screen for draw-in animation
        Position = _originalPosition + new Vector2(0, 300f);
        Modulate = new Color(1, 1, 1, 0);

        _isReady = true;

        // If SetCard was called before _Ready (common with Instantiate + SetCard + AddChild),
        // apply the pending card data now that nodes are cached.
        if (_pendingCard != null)
        {
            ApplyCardData(_pendingCard.TopHalf, _pendingCard.BottomHalf);
            _pendingCard = null;
        }
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

    // ── Card data — populate split halves and prepare full card view ───────

    /// <summary>Wire the parent <see cref="DeckUiManager"/> so this card can notify it on hover (used to slide neighbor cards aside).</summary>
    public void SetDeckUiManager(DeckUiManager manager) => _deckUiManager = manager;

    /// <summary>
    /// Bind a runtime <see cref="Card"/> to this UI. Safe to call before <c>_Ready</c> —
    /// the card data is stashed in <c>_pendingCard</c> and applied once nodes are cached.
    /// </summary>
    public void SetCard(Card card)
    {
        CardInstance = card;
        TopHalf = card.TopHalf;
        BottomHalf = card.BottomHalf;

        if (_isReady)
            ApplyCardData(card.TopHalf, card.BottomHalf);
        else
            _pendingCard = card; // Will be applied in _Ready()
    }

    /// <summary>
    /// Bind raw <see cref="CardHalf"/> halves directly without a parent <see cref="Card"/>.
    /// Used by the card library / preview screens where there is no shuffled <see cref="Card"/>
    /// instance. <see cref="CardInstance"/> stays null in this path.
    /// </summary>
    public void SetCard(CardHalf top, CardHalf bottom)
    {
        TopHalf = top;
        BottomHalf = bottom;

        if (_isReady)
            ApplyCardData(top, bottom);
        else
        {
            // Create a temporary card to hold the halves for deferred apply
            _pendingCard = new Card { TopHalf = top, BottomHalf = bottom };
        }
    }

    /// <summary>
    /// Actually populates labels, borders, and styles. Only call when nodes are cached.
    /// </summary>
    private void ApplyCardData(CardHalf top, CardHalf bottom)
    {

        // ── Populate top split half ─────────────────────────────────
        PopulateSplitHalf(top,
            _topManaLabel, _topNameLabel, _topSpeedLabel, _topRulesLabel);

        // ── Populate bottom split half ──────────────────────────────
        PopulateSplitHalf(bottom,
            _botManaLabel, _botNameLabel, _botSpeedLabel, _botRulesLabel);

        // ── Apply school-colored borders ────────────────────────────
        var school = top?.School ?? bottom?.School ?? CardSchool.Generic;
        var borderCol = SchoolColors.GetBorderColor(school);
        var darkCol = SchoolColors.GetDarkColor(school);

        ApplyPanelBorder(_topPanel, borderCol, true);
        ApplyPanelBorder(_bottomPanel, borderCol, false);

        if (_splitDivider != null)
        {
            var rarityCol = CardInstance != null
                ? UITheme.GetRarityColor(CardInstance.Rarity)
                : UITheme.RarityCommon;
            _splitDivider.Color = rarityCol;
        }


        // Style mana pips with school dark color
        StyleManaPip($"{SplitTop}/NameBar/ManaPip", darkCol);
        StyleManaPip($"{SplitBot}/NameBar/ManaPip", darkCol);

        // Channel badges — small indicator on the name bar
        SetChannelBadge($"{SplitTop}/NameBar", top?.CanChannel ?? false);
        SetChannelBadge($"{SplitBot}/NameBar", bottom?.CanChannel ?? false);
    }

    private void PopulateSplitHalf(CardHalf half,
        Label mana, Label name, Label speed, RichTextLabel rules)
    {
        if (mana != null) mana.Text = (half?.ManaCost ?? 0).ToString();
        if (name != null) name.Text = half?.Name ?? "";
        if (speed != null) speed.Text = half?.Speed.ToString() ?? "Sorcery";
        if (rules != null) rules.Text = half?.RulesText ?? "";
    }

    private void ApplyPanelBorder(Panel panel, Color borderCol, bool isTop)
    {
        if (panel == null) return;
        var style = new StyleBoxFlat();
        style.BgColor = UITheme.SurfaceLight;
        style.BorderColor = borderCol;
        style.BorderWidthLeft = UITheme.BorderWidth;
        style.BorderWidthRight = UITheme.BorderWidth;
        if (isTop)
        {
            style.BorderWidthTop = UITheme.BorderWidth;
            style.BorderWidthBottom = 2;
            style.CornerRadiusTopLeft = UITheme.CornerRadius;
            style.CornerRadiusTopRight = UITheme.CornerRadius;
        }
        else
        {
            style.BorderWidthTop = 2;
            style.BorderWidthBottom = UITheme.BorderWidth;
            style.CornerRadiusBottomLeft = UITheme.CornerRadius;
            style.CornerRadiusBottomRight = UITheme.CornerRadius;
        }
        panel.AddThemeStyleboxOverride("panel", style);
    }

    private void StyleManaPip(string pipPanelPath, Color darkCol)
    {
        var pip = GetNodeOrNull<Panel>(pipPanelPath);
        if (pip == null) return;
        var s = new StyleBoxFlat { BgColor = darkCol };
        s.SetCornerRadiusAll(UITheme.CornerRadiusLg);
        pip.AddThemeStyleboxOverride("panel", s);
    }

    private void SetChannelBadge(string nameBarPath, bool canChannel)
    {
        var nameBar = GetNodeOrNull<Control>(nameBarPath);
        if (nameBar == null) return;

        // Remove existing badge if present
        var existing = nameBar.GetNodeOrNull<Label>("ChannelBadge");
        existing?.QueueFree();

        if (!canChannel) return;

        var badge = new Label
        {
            Name = "ChannelBadge",
            Text = "⟳",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        badge.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        badge.AddThemeColorOverride("font_color", new Color(0.7f, 0.85f, 1f, 0.8f));
        nameBar.AddChild(badge);
    }

    // ── Full-card view (hover state — art on top, info on bottom) ──────────

    private void ShowFullCard(CardHalf half, bool isTop)
    {
        if (_fullCardView == null || half == null) return;

        var school = half.School;
        var borderColor = SchoolColors.GetBorderColor(school);
        var darkColor = SchoolColors.GetDarkColor(school);
        var rarityCol = UITheme.GetRarityColor(CardInstance.Rarity);

        // Art panel placeholder
        if (_artPanel != null)
        {
            var artStyle = new StyleBoxFlat();
            artStyle.BgColor = new Color(
                borderColor.R * 0.35f,
                borderColor.G * 0.35f,
                borderColor.B * 0.35f, 1f);
            artStyle.CornerRadiusTopLeft = UITheme.CornerRadius;
            artStyle.CornerRadiusTopRight = UITheme.CornerRadius;
            artStyle.BorderColor = borderColor;
            artStyle.BorderWidthLeft = UITheme.BorderWidth;
            artStyle.BorderWidthTop = UITheme.BorderWidth;
            artStyle.BorderWidthRight = UITheme.BorderWidth;
            artStyle.BorderWidthBottom = 0;
            _artPanel.AddThemeStyleboxOverride("panel", artStyle);
        }

        // School badge
        if (_schoolBadge != null)
        {
            _schoolBadge.Text = SchoolColors.GetBadgeText(school);
            var badgeStyle = new StyleBoxFlat { BgColor = borderColor };
            badgeStyle.SetCornerRadiusAll(UITheme.CornerRadiusLg);
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
                pip.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall / 2);
                pip.AddThemeColorOverride("font_color", Colors.White);
                var pipStyle = new StyleBoxFlat { BgColor = ElementColors.Get(tag) };
                pipStyle.SetCornerRadiusAll(UITheme.CornerRadius + 2);
                pip.AddThemeStyleboxOverride("normal", pipStyle);
                _elementTagContainer.AddChild(pip);
            }
        }

        // Divider
        if (_fullDivider != null)
        {
            var divStyle = new StyleBoxFlat { BgColor = rarityCol };
            _fullDivider.AddThemeStyleboxOverride("panel", divStyle);
        }

        // Info panel border
        if (_fullInfoPanel != null)
        {
            var infoStyle = new StyleBoxFlat();
            infoStyle.BgColor = UITheme.SurfaceLight;
            infoStyle.BorderColor = borderColor;
            infoStyle.BorderWidthLeft = UITheme.BorderWidth;
            infoStyle.BorderWidthRight = UITheme.BorderWidth;
            infoStyle.BorderWidthBottom = UITheme.BorderWidth;
            infoStyle.BorderWidthTop = 0;
            infoStyle.CornerRadiusBottomLeft = UITheme.CornerRadius;
            infoStyle.CornerRadiusBottomRight = UITheme.CornerRadius;
            _fullInfoPanel.AddThemeStyleboxOverride("panel", infoStyle);
        }

        // Mana pip
        var manaPipPanel = _fullManaLabel?.GetParent() as Panel;
        if (manaPipPanel != null)
        {
            var pipStyle = new StyleBoxFlat { BgColor = darkColor };
            pipStyle.SetCornerRadiusAll(UITheme.CornerRadiusLg);
            manaPipPanel.AddThemeStyleboxOverride("panel", pipStyle);
        }

        // Spell info
        if (_fullManaLabel != null) _fullManaLabel.Text = half.ManaCost.ToString();
        if (_fullNameLabel != null) _fullNameLabel.Text = half.Name ?? "";
        if (_fullSpeedLabel != null) _fullSpeedLabel.Text = half.Speed.ToString();
        if (_fullRulesLabel != null) _fullRulesLabel.Text = half.RulesText ?? "";

        // Channel strip
        bool canChannel = half?.CanChannel ?? false;
        string chText = canChannel ? $"[Shift+Drop] Channel: cast as Stage {(/* tier */ 0) + 1} (+1 mana)" : "";

        // Channel hint — single line at bottom of rules text
        if (_fullRulesLabel != null && (half?.CanChannel ?? false))
        {
            _fullRulesLabel.Text = (half?.RulesText ?? "") +
                "\n[color=#aaccff][i]Hold Shift to channel (+1 mana)[/i][/color]";
        }
        else if (_fullRulesLabel != null)
        {
            _fullRulesLabel.Text = half?.RulesText ?? "";
        }

        // Hide channel panel entirely — no longer used
        if (_fullChannelPanel != null)
            _fullChannelPanel.Visible = false;

        _fullCardView.Visible = true;
        _fullCardView.Modulate = new Color(1, 1, 1, 0);
        _fullViewHalf = isTop ? "top" : "bottom";

        // Crossfade: split out, full in
        _transitionTween?.Kill();
        _transitionTween = CreateTween().SetParallel(true);
        _transitionTween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);

        _transitionTween.TweenProperty(_splitView, "modulate",
            new Color(1, 1, 1, 0), UITheme.AnimNormal);

        _transitionTween.TweenProperty(_fullCardView, "modulate",
            Colors.White, UITheme.AnimNormal).SetDelay(0.05f);

        _fullCardView.Scale = new Vector2(0.97f, 0.97f);
        _transitionTween.TweenProperty(_fullCardView, "scale",
            Vector2.One, UITheme.AnimNormal).SetDelay(0.05f);
    }

    private void HideFullCard()
    {
        if (_fullCardView == null) return;

        _transitionTween?.Kill();
        _transitionTween = CreateTween().SetParallel(true);
        _transitionTween.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);

        _transitionTween.TweenProperty(_fullCardView, "modulate",
            new Color(1, 1, 1, 0), UITheme.AnimFast);
        _transitionTween.TweenProperty(_splitView, "modulate",
            Colors.White, UITheme.AnimNormal).SetDelay(0.03f);

        // Hide the full view node after fade completes to free up input
        _transitionTween.Chain().TweenCallback(Callable.From(() =>
        {
            if (_fullViewHalf == "none" && _fullCardView != null)
                _fullCardView.Visible = false;
        }));

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
        EmitSignal(SignalName.CardHalfHovered, this, _lastHoveredTop, true);
    }

    private void OnCardMaybeExit()
    {
        CallDeferred(nameof(CheckCardExit));
        EmitSignal(SignalName.CardHalfHovered, this, _lastHoveredTop, false);
    }

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

        // Ensure split view is fully visible (safety if transition tween was mid-fade)
        _transitionTween?.Kill();
        if (_splitView != null)
            _splitView.Modulate = Colors.White;
        if (_fullCardView != null)
        {
            _fullCardView.Visible = false;
            _fullCardView.Modulate = new Color(1, 1, 1, 0);
        }

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
            _halfTween.TweenProperty(_topPanel, "modulate", UITheme.CardTopActive, 0.1f);
            _halfTween.TweenProperty(_bottomPanel, "modulate", UITheme.CardDim, 0.1f);
            ShowFullCard(TopHalf, true);
        }
        else if (activeHalf == "bottom")
        {
            _halfTween.TweenProperty(_topPanel, "modulate", UITheme.CardDim, 0.1f);
            _halfTween.TweenProperty(_bottomPanel, "modulate", UITheme.CardBottomActive, 0.1f);
            ShowFullCard(BottomHalf, false);
        }
        else
        {
            var topBase = (TopHalf?.ManaCost ?? 0) > _lastKnownMana
                ? UITheme.DangerDim : UITheme.SurfaceLight;
            var botBase = (BottomHalf?.ManaCost ?? 0) > _lastKnownMana
                ? UITheme.DangerDim : UITheme.SurfaceLight;
            _halfTween.TweenProperty(_topPanel, "modulate", topBase, 0.1f);
            _halfTween.TweenProperty(_bottomPanel, "modulate", botBase, 0.1f);
            HideFullCard();
        }
    }

    public void RefreshAffordability(int currentMana)
    {
        _lastKnownMana = currentMana;

        // Don't override panel colors if discard flagged — amber pulse takes visual priority
        if (_isDiscardFlagged) return;

        _topPanel.Modulate = (TopHalf?.ManaCost ?? 0) > currentMana
            ? UITheme.DangerDim : UITheme.SurfaceLight;
        _bottomPanel.Modulate = (BottomHalf?.ManaCost ?? 0) > currentMana
            ? UITheme.DangerDim : UITheme.SurfaceLight;
    }

    public void SetDiscardFlagged(bool flagged)
    {
        if (_isDiscardFlagged == flagged) return;
        _isDiscardFlagged = flagged;

        _amberPulseTween?.Kill();

        if (flagged)
        {
            // Start pulsing amber on the whole card visual
            _amberPulseTween = CreateTween().SetLoops();
            _amberPulseTween.SetEase(Tween.EaseType.InOut)
                            .SetTrans(Tween.TransitionType.Sine);
            _amberPulseTween.TweenProperty(_visualNode, "modulate", UITheme.Warning, 0.5f);
            _amberPulseTween.TweenProperty(_visualNode, "modulate", UITheme.WarningDim, 0.5f);
        }
        else
        {
            // Restore normal modulate — affordability will handle panel colors
            _visualNode.Modulate = Colors.White;
            RefreshAffordability(_lastKnownMana);
        }
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

    /// <summary>
    /// Forces the card into a static visible state immediately,
    /// bypassing the draw-in animation. Use for preview/library display.
    /// </summary>
    public void SetStaticDisplay(float scale = 1f)
    {
        GD.Print($"[CardUi] SetStaticDisplay called id={GetInstanceId()} scale={scale}");
        _entryTweenComplete = true;
        HasBeenPlaced = true;
        SetProcess(false);

        // Cancel any in-flight tweens
        _cardTween?.Kill();

        Modulate = Colors.White;
        Rotation = 0f;
        Position = Vector2.Zero;
        Scale = new Vector2(scale, scale);
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
        _cardTween.TweenProperty(this, "rotation", _restRotation * 0.2f + tiltDir, UITheme.AnimFast);
        _cardTween.TweenProperty(this, "scale", new Vector2(0.92f, 0.92f), UITheme.AnimFast);
        _cardTween.TweenProperty(this, "modulate", UITheme.CardDragGhost, UITheme.AnimFast);
        ZIndex = 200;

        // Notify GameRunner to show range highlight for the dragged half
        var half = _dragTopCard ? TopHalf : BottomHalf;
        EmitSignal(SignalName.CardHalfHovered, this, _dragTopCard, true);
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
            dict.ContainsKey("card"))
        {
            var droppedCard = (CardUi)dict["card"].AsGodotObject();
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

    public void EndDrag()
    {
        SnapBack();
    }
}
