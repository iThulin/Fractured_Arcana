using Godot;
using System;

public partial class CardUi : Control
{
    public Card CardInstance { get; private set; }
    public CardHalf TopHalf { get; private set; }
    public CardHalf BottomHalf { get; private set; }

    private Control _visualNode;
    private Control topArea;
    private Control bottomArea;
    private string currentHoverState = "";
    private float hoverSwitchCooldown = 0.1f;
    private float hoverSwitchTimer = 0f;
    private string pendingHoverState = "";

    private AnimationPlayer hoverAnimator;

    [Signal] public delegate void CardDroppedEventHandler();
    [Signal] public delegate void CardHalfSelectedEventHandler(CardUi cardUi, bool isTop);

    private bool _dragPressed = false;
    private bool _dragQueued = false;
    private Vector2 _dragStartPosition;
    private Vector2 _originalPosition;
    private const float DragThreshold = 10f;
    private bool _dragTopCard = false;

    public override void _Ready()
    {
        _visualNode = GetNode<Control>("CardVisual");
        hoverAnimator = GetNode<AnimationPlayer>("HoverAnimator");
        _originalPosition = Position;

        topArea = GetNode<Control>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl");
        bottomArea = GetNode<Control>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl");

        topArea.MouseEntered += () => hoverAnimator?.Play("hover_enter_top");
        bottomArea.MouseEntered += () => hoverAnimator?.Play("hover_enter_bottom");

        topArea.MouseExited += () => hoverAnimator?.Play("RESET");
        bottomArea.MouseExited += () => hoverAnimator?.Play("RESET");

        topArea.GuiInput += (e) => OnCardGuiInput(e, true);
        bottomArea.GuiInput += (e) => OnCardGuiInput(e, false);
    }

    public override void _Process(double delta)
    {
        Vector2 mousePos = GetViewport().GetMousePosition();

        bool isHoveringTop = topArea.GetGlobalRect().HasPoint(mousePos);
        bool isHoveringBottom = bottomArea.GetGlobalRect().HasPoint(mousePos);
        string detectedState = isHoveringTop ? "top" : isHoveringBottom ? "bottom" : "none";

        if (detectedState != currentHoverState)
        {
            if (detectedState != pendingHoverState)
            {
                pendingHoverState = detectedState;
                hoverSwitchTimer = 0f;
            }
            else
            {
                hoverSwitchTimer += (float)delta;
                if (hoverSwitchTimer >= hoverSwitchCooldown)
                {
                    currentHoverState = detectedState;
                    switch (detectedState)
                    {
                        case "top":
                            hoverAnimator?.Play("hover_enter_top");
                            break;
                        case "bottom":
                            hoverAnimator?.Play("hover_enter_bottom");
                            break;
                        case "none":
                            hoverAnimator?.Play("RESET");
                            break;
                    }
                }
            }
        }
        else
        {
            pendingHoverState = "";
            hoverSwitchTimer = 0f;
        }
    }

    public void SetCard(Card card)
    {
        CardInstance = card;
        SetCard(card.TopHalf, card.BottomHalf); // call your existing SetCard(CardHalf, CardHalf)
    }

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
                if (!_dragQueued)
                {
                    SnapBack();
                }
            }
        }
        else if (_dragPressed && @event is InputEventMouseMotion motion)
        {
            if (!_dragQueued && (motion.GlobalPosition - _dragStartPosition).Length() > DragThreshold)
            {
                _dragQueued = true;
                SetDragPreview(CreateDragPreview());
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private Control CreateDragPreview()
    {
        var preview = Duplicate() as CardUi;
        preview.SetCard(TopHalf, BottomHalf);
        preview.Modulate = new Color(1, 1, 1, 0.5f);
        preview.Scale = new Vector2(1.1f, 1.1f);
        return preview;
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
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
        if (data.Obj is Godot.Collections.Dictionary dict)
        {
            return dict.ContainsKey("card"); //&& dict["card"] is CardUi;
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
        Position = _originalPosition;
        DragPayloadManager.IsDragging = false; // Reset drag flag
        EmitSignal(SignalName.CardDropped);
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
}
}
