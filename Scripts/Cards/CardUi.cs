using Godot;
using System;

public partial class CardUi : Control
{
    public CardData TopCardData { get; private set; }
    public CardData BottomCardData { get; private set; }
    private bool _isHovered = false;
    private Control _visualNode;
    private Color _normalColor = new Color(1, 1, 1, 1);
    private Color _hoverColor = new Color(1.2f, 1.2f, 1.2f, 1);

    // New stuff
    private Vector2 originalPosition;
    private Vector2 hoverOffset = new Vector2(0, -40);
    private Vector2 originalScale;
    private Vector2 RectScale;
    private float hoverScale = 1.1f;
    private int originalZIndex;

    private AnimationPlayer hoverAnimator;
    [Signal]
    public delegate void CardDroppedEventHandler();
    [Signal]
    public delegate void TopCardSelectedEventHandler(CardData card);
    [Signal]
    public delegate void BottomCardSelectedEventHandler(CardData card);

    public override void _Ready()
    {
        _visualNode = GetNode<Control>("CardVisual");
        originalZIndex = ZIndex;

        hoverAnimator = GetNode<AnimationPlayer>("HoverAnimator");

        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;

        var topArea = GetNode<Control>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl");
        var BottomArea = GetNode<Control>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl");

        topArea.GuiInput += OnTopCardInput;
        BottomArea.GuiInput += OnBottomCardInput;
        
    }

    private void OnMouseEntered()
    { 
        GD.Print($"[CardUi] Mouse entered: Original Pos: {originalPosition}");

        //originalPosition = Position;
        //originalScale = RectScale;

        hoverAnimator?.Play("hover_enter");

       //ZIndex = 100;
        //Position -= new Vector2(0, 30);
        //RectScale = new Vector2(1.5f, 1.5f); // Slight zoom
        //GD.Print($"[CardUi] Moved up to: {Position}, Z index: {ZIndex}");

    }

    private void OnMouseExited()
    {

        hoverAnimator?.Play("hover_exit");

        //Position = originalPosition;
        //RectScale = originalScale;
        //ZIndex = originalZIndex;
        //GD.Print($"[CardUi] Moved up to: {Position}, Z index: {ZIndex}");
    }

    private void OnTopCardInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            EmitSignal(SignalName.TopCardSelected, TopCardData);
            GD.Print($"Top card selected: {TopCardData.CardName}");
        }
    }

    private void OnBottomCardInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            EmitSignal(SignalName.BottomCardSelected, BottomCardData);
            GD.Print($"Bottom card selected: {BottomCardData.CardName}");
        }
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        GD.Print("[CardUi] Drag started");

        var preview = Duplicate() as Control;
        preview.Modulate = new Color(1, 1, 1, 0.5f); // transparent ghost
        preview.Scale = new Vector2(1.1f, 1.1f);    // slightly bigger
        SetDragPreview(preview);

        return this;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        return data.Obj is CardUi;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        CardUi droppedCard = data.As<CardUi>();
        if (droppedCard != null)
        {
            var handUI = GetParent();
            int dropIndex = handUI.GetChildren().IndexOf(this);
            handUI.RemoveChild(droppedCard);
            handUI.AddChild(droppedCard);
            handUI.MoveChild(droppedCard, dropIndex);

            EmitSignal(nameof(CardDropped));
        }
    }

    public void SetCard(CardData TopData, CardData BottomData)
    {
        //CardData = data;

        // Top Card Data
        var nameLabelTop = GetNodeOrNull<Label>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/NameLabel");
        var schoolLabelTop = GetNodeOrNull<Label>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/SchoolLabel");
        var descLabelTop = GetNodeOrNull<RichTextLabel>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl/TopSpellContainer/DescriptionLabel");

        if (nameLabelTop != null) nameLabelTop.Text = TopData.CardName;
        if (schoolLabelTop != null) schoolLabelTop.Text = TopData.School;
        if (descLabelTop != null) descLabelTop.Text = TopData.Description;

        // Bottom Card Data
        var nameLabelBot = GetNodeOrNull<Label>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/NameLabel");
        var schoolLabelBot = GetNodeOrNull<Label>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/SchoolLabel");
        var descLabelBot = GetNodeOrNull<RichTextLabel>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl/BottomSpellContainer/DescriptionLabel");

        if (nameLabelBot != null) nameLabelBot.Text = BottomData.CardName;
        if (schoolLabelBot != null) schoolLabelBot.Text = BottomData.School;
        if (descLabelBot != null) descLabelBot.Text = BottomData.Description;

    }
}
