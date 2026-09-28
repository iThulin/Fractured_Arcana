using Godot;
using System.Collections.Generic;

// ============================================================
// TechniqueTray.cs
//
// Purpose:        The martial half of the action tray (Martial
//                 Maneuvers & Edge spec v1 §11d): pinned technique
//                 cards for the selected martial unit, never drawn or
//                 discarded. Sits on the DeckUI layer where the card
//                 hand strip lives for wizards, in the space that strip
//                 leaves empty when a martial is selected. Click a card
//                 to arm it; CombatManager owns targeting and resolution.
// Layer:          UI
// Collaborators:  CombatManager.Maneuvers.cs (fills and reads it),
//                 UITheme.cs (every colour), ManeuverDefinition.cs
//                 (the data behind each card)
//
// Built in code, so it follows the Mac deferred-build rules: the root
// is added to the tree with CallDeferred by its owner and builds its
// own children from _Ready via CallDeferred.
// ============================================================

/// <summary>What one technique card shows. A view, not the definition: costs
/// and enabled state are already resolved against the selected unit.</summary>
public sealed class TechniqueCardView
{
    public string Id = "";
    public string Title = "";
    public string Cost = "";
    public string Text = "";
    public string Role = "";
    public string Tooltip = "";
    public bool Enabled = true;
    public bool Armed = false;
    public bool IsFinisher = false;
    /// <summary>Spec §7: a reaction card has its own frame and flips to Armed.</summary>
    public bool IsReaction = false;
    public bool ArmedNow = false;
    /// <summary>Spec §11d: a pinned item (belt or wand), shown right of the techniques.</summary>
    public bool IsItem = false;
}

public partial class TechniqueTray : Control
{
    [Signal] public delegate void TechniquePressedEventHandler(string maneuverId);
    /// <summary>A stance button in the tray's stance row (moved here from the unit
    /// panel, 2026-09-28). CombatManager routes it to TrySwitchStance.</summary>
    [Signal] public delegate void StancePressedEventHandler(string stanceId);

    private const float CardWidth = 172f;
    private const float CardHeight = 196f;
    private const float TrayHeight = 300f;   // header (up to 2 lines) + stance row + cards

    private VBoxContainer _column;
    private Label _header;
    private HBoxContainer _stanceRow;
    private HBoxContainer _row;
    private bool _built;

    private List<TechniqueCardView> _pendingCards;
    private string _pendingHeader = "";
    private List<TechniqueCardView> _pendingStances;

    public override void _Ready()
    {
        // The whole strip: clicks pass through to the board except on the cards.
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        OffsetTop = -TrayHeight;
        OffsetBottom = 0;
        Visible = false;
        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        var center = new CenterContainer
        {
            Name = "Center",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        _column = new VBoxContainer { Name = "Column", MouseFilter = MouseFilterEnum.Ignore };
        _column.AddThemeConstantOverride("separation", 6);
        _column.Alignment = BoxContainer.AlignmentMode.End;
        center.AddChild(_column);

        _header = new Label
        {
            Name = "Header",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _header.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        _header.AddThemeColorOverride("font_color", UITheme.Gold);
        _column.AddChild(_header);

        // Stance row: a mode, not an action (spec §11d), so it sits above the
        // cards as a strip of small buttons rather than as cards.
        _stanceRow = new HBoxContainer { Name = "Stances", MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _stanceRow.AddThemeConstantOverride("separation", 6);
        _stanceRow.Alignment = BoxContainer.AlignmentMode.Center;
        _column.AddChild(_stanceRow);

        _row = new HBoxContainer { Name = "Cards", MouseFilter = MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 10);
        _row.Alignment = BoxContainer.AlignmentMode.Center;
        _column.AddChild(_row);

        _built = true;
        if (_pendingCards != null || !string.IsNullOrEmpty(_pendingHeader) || _pendingStances != null)
            ShowCards(_pendingCards, _pendingHeader, _pendingStances);
    }

    /// <summary>Replace the tray's contents. Null or empty <paramref name="cards"/>
    /// with an empty header and no stances hides the tray. <paramref name="stances"/>
    /// uses Title for the label, Armed for "the active stance", Enabled and Tooltip
    /// as for cards.</summary>
    public void ShowCards(List<TechniqueCardView> cards, string header, List<TechniqueCardView> stances = null)
    {
        if (!_built)
        {
            _pendingCards = cards;
            _pendingHeader = header ?? "";
            _pendingStances = stances;
            return;
        }
        _pendingCards = null;
        _pendingHeader = "";
        _pendingStances = null;

        foreach (Node child in _row.GetChildren())
            child.QueueFree();
        foreach (Node child in _stanceRow.GetChildren())
            child.QueueFree();

        bool any = cards != null && cards.Count > 0;
        bool anyStance = stances != null && stances.Count > 0;
        bool show = any || anyStance || !string.IsNullOrEmpty(header);
        Visible = show;
        if (!show)
            return;

        _header.Text = header ?? "";
        _header.Visible = !string.IsNullOrEmpty(header);

        _stanceRow.Visible = anyStance;
        if (anyStance)
        {
            var lbl = new Label { Text = "Stance:", MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall - 1);
            lbl.AddThemeColorOverride("font_color", UITheme.GoldDim);
            _stanceRow.AddChild(lbl);
            foreach (var st in stances)
                _stanceRow.AddChild(MakeStanceButton(st));
        }

        if (!any)
            return;

        foreach (var view in cards)
            _row.AddChild(MakeCard(view));
    }

    private Control MakeStanceButton(TechniqueCardView v)
    {
        var btn = new Button
        {
            Name = $"Stance_{v.Id}",
            Text = v.Title,
            Disabled = !v.Enabled,
            TooltipText = v.Tooltip,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 28),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall - 1);
        UITheme.ApplyButtonStyle(btn, isPrimary: v.Armed);
        string id = v.Id;   // capture by value for the closure
        btn.Pressed += () => EmitSignal(SignalName.StancePressed, id);
        return btn;
    }

    private Control MakeCard(TechniqueCardView v)
    {
        var btn = new Button
        {
            Name = $"Tech_{v.Id}",
            CustomMinimumSize = new Vector2(CardWidth, CardHeight),
            Disabled = !v.Enabled,
            TooltipText = v.Tooltip,
            Text = "",
            FocusMode = FocusModeEnum.None,
        };
        UITheme.ApplyButtonStyle(btn, isPrimary: v.Armed || v.ArmedNow);
        string id = v.Id;   // capture by value for the closure
        btn.Pressed += () => EmitSignal(SignalName.TechniquePressed, id);

        // Children draw over the button and ignore the mouse, so every click is the button's.
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        btn.AddChild(margin);

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 4);
        margin.AddChild(box);

        var title = new Label
        {
            Text = v.IsFinisher ? $"{v.Title}  ✦" : v.IsReaction ? $"{v.Title}  »" : v.IsItem ? $"{v.Title}  ◇" : v.Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall + 2);
        title.AddThemeColorOverride("font_color", v.Enabled ? UITheme.TextPrimary : UITheme.GoldDim);
        box.AddChild(title);

        var cost = new Label
        {
            Text = v.Cost,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        cost.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        cost.AddThemeColorOverride("font_color", v.Enabled ? UITheme.Gold : UITheme.GoldDim);
        box.AddChild(cost);

        var text = new Label
        {
            Text = v.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        text.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall - 1);
        text.AddThemeColorOverride("font_color", v.Enabled ? UITheme.TextPrimary : UITheme.GoldDim);
        box.AddChild(text);

        var foot = new Label
        {
            Text = v.Armed ? "ARMED: click a target"
                 : v.ArmedNow ? "ARMED: fires in the enemy phase"
                 : v.IsReaction ? $"Reaction · {v.Role}"
                 : v.Role,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        foot.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall - 2);
        foot.AddThemeColorOverride("font_color", v.Armed || v.ArmedNow ? UITheme.Warning : UITheme.GoldDim);
        box.AddChild(foot);

        return btn;
    }
}
