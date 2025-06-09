using Godot;
using System;

public partial class CardUi : Control
{
    public CardData TopCardData { get; private set; }
    public CardData BottomCardData { get; private set; }

    private Control _visualNode;
    private Control topArea;
    private Control bottomArea;
    private string currentHoverState = "";
    private float hoverSwitchCooldown = 0.1f;
    private float hoverSwitchTimer = 0f;
    private string pendingHoverState = "";

    private AnimationPlayer hoverAnimator;

    [Signal] public delegate void CardDroppedEventHandler();
    [Signal] public delegate void TopCardSelectedEventHandler(CardData card);
    [Signal] public delegate void BottomCardSelectedEventHandler(CardData card);

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
        preview.SetCard(TopCardData, BottomCardData);
        preview.Modulate = new Color(1, 1, 1, 0.5f);
        preview.Scale = new Vector2(1.1f, 1.1f);
        return preview;
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        DragPayloadManager.DraggedCard = this;
        DragPayloadManager.IsTopHalf = _dragTopCard;
        DragPayloadManager.IsDragging = true;  // <== ✅ must be set!

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
            return dict.ContainsKey("card") && dict["card"] is CardUi;
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

    public void SetCard(CardData TopData, CardData BottomData)
    {
        TopCardData = TopData;
        BottomCardData = BottomData;

        var nameLabelTop = GetNodeOrNull<Label>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/NameLabel");
        var schoolLabelTop = GetNodeOrNull<Label>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/SchoolLabel");
        var descLabelTop = GetNodeOrNull<RichTextLabel>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/DescriptionLabel");

        if (nameLabelTop != null) nameLabelTop.Text = TopData.CardName;
        if (schoolLabelTop != null) schoolLabelTop.Text = TopData.School;
        if (descLabelTop != null) descLabelTop.Text = TopData.Description;

        var nameLabelBot = GetNodeOrNull<Label>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/NameLabel");
        var schoolLabelBot = GetNodeOrNull<Label>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/SchoolLabel");
        var descLabelBot = GetNodeOrNull<RichTextLabel>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/DescriptionLabel");

        if (nameLabelBot != null) nameLabelBot.Text = BottomData.CardName;
        if (schoolLabelBot != null) schoolLabelBot.Text = BottomData.School;
        if (descLabelBot != null) descLabelBot.Text = BottomData.Description;
    }
}
