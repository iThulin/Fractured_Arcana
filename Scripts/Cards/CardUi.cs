using Godot;
using System;

public partial class CardUi : Control
{
    public CardData TopCardData { get; private set; }
    public CardData BottomCardData { get; private set; }

    private Control _visualNode;
    private AnimationPlayer hoverAnimator;

    [Signal] public delegate void CardDroppedEventHandler();
    [Signal] public delegate void TopCardSelectedEventHandler(CardData card);
    [Signal] public delegate void BottomCardSelectedEventHandler(CardData card);

    private bool _dragPressed = false;
    private bool _dragQueued = false;
    private Vector2 _dragStartPosition;
    private Vector2 _originalPosition;
    private const float DragThreshold = 10f;

    public override void _Ready()
    {
        _visualNode = GetNode<Control>("CardVisual");
        hoverAnimator = GetNode<AnimationPlayer>("HoverAnimator");
        _originalPosition = Position;

        var topArea = GetNode<Control>("CardVisual/VBoxContainer/TopCardPanel/TopCardControl");
        var bottomArea = GetNode<Control>("CardVisual/VBoxContainer/BottomCardPanel/BottomCardControl");

        topArea.MouseEntered += () => hoverAnimator?.Play("hover_enter_top");
        bottomArea.MouseEntered += () => hoverAnimator?.Play("hover_enter_bottom");

        topArea.MouseExited += () => hoverAnimator?.Play("RESET");
        bottomArea.MouseExited += () => hoverAnimator?.Play("RESET");
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
