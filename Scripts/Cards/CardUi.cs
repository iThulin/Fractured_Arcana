using Godot;
using System;

public partial class CardUi : Control
{
    public CardData CardData { get; private set; }
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

    public override void _Ready()
    {
        _visualNode = GetNode<Control>("CardVisual");
        originalZIndex = ZIndex;

        hoverAnimator = GetNode<AnimationPlayer>("HoverAnimator");

        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;
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

    public void SetCard(CardData data)
    {
        CardData = data;

        var nameLabel = GetNodeOrNull<Label>("CardVisual/MarginContainer/Border/VBoxContainer/NameLabel");
        var schoolLabel = GetNodeOrNull<Label>("CardVisual/MarginContainer/Border/VBoxContainer/SchoolLabel");
        var manaLabel = GetNodeOrNull<Label>("CardVisual/MarginContainer/Border/VBoxContainer/ManaLabel");
        var descLabel = GetNodeOrNull<RichTextLabel>("CardVisual/MarginContainer/Border/VBoxContainer/DescriptionLabel");

        if (nameLabel != null) nameLabel.Text = data.CardName;
        if (schoolLabel != null) schoolLabel.Text = data.School;
        if (manaLabel != null) manaLabel.Text = data.ManaCost.ToString();
        if (descLabel != null) descLabel.Text = data.Description;
    }
}
