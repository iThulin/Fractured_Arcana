using Godot;

// ============================================================
// QuestLogScreen.cs — a global quest-log overlay, openable from
// the top bar on any screen (mirrors CouncilScreen's toggle
// pattern). Renders through the shared QuestLogView.
// ============================================================

/// <summary>Full-screen quest-log overlay. Toggle() from the global top bar.</summary>
public partial class QuestLogScreen : CanvasLayer
{
    private static QuestLogScreen _instance;
    public static bool IsOpen => _instance != null && IsInstanceValid(_instance);

    public static void Toggle(Node host)
    {
        if (IsOpen) { _instance.QueueFree(); _instance = null; return; }
        if (host == null) return;
        _instance = new QuestLogScreen { Name = "QuestLogScreen", Layer = 128 };
        host.AddChild(_instance);
    }

    public static void Close()
    {
        if (IsOpen) { _instance.QueueFree(); _instance = null; }
    }

    public override void _Ready() => CallDeferred(nameof(BuildUI));

    public override void _ExitTree()
    {
        if (_instance == this) _instance = null;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUI()
    {
        var save = SaveManager.ActiveSave;
        if (save != null) QuestTracker.SyncCompletions(save);

        var backdrop = new Panel
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        backdrop.AddThemeStyleboxOverride("panel",
            new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.55f) });
        AddChild(backdrop);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -350, OffsetTop = -330, OffsetRight = 350, OffsetBottom = 330,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UITheme.BgBase,
            BorderColor = UITheme.POINarrative,
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 22, ContentMarginRight = 22,
            ContentMarginTop = 18, ContentMarginBottom = 18,
        });
        AddChild(panel);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);

        var header = new HBoxContainer();
        var title = new Label { Text = "Quests", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        title.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        header.AddChild(title);
        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(88, 0) };
        close.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += Close;
        header.AddChild(close);
        layout.AddChild(header);

        var summary = new Label();
        summary.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        summary.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        layout.AddChild(summary);

        layout.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        layout.AddChild(scroll);

        var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(content);

        var questBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        questBox.AddThemeConstantOverride("separation", 10);
        content.AddChild(questBox);
        summary.Text = QuestLogView.BuildInto(questBox, save);

        content.AddChild(new HSeparator());
        var loreHead = new Label { Text = "Hall of Lore" };
        loreHead.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        loreHead.AddThemeColorOverride("font_color", UITheme.POINarrative);
        content.AddChild(loreHead);

        var loreBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.AddChild(loreBox);
        QuestLogView.BuildLoreInto(loreBox, save);
    }
}
