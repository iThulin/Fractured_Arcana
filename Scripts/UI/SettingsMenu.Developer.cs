using Godot;
// ============================================================
// SettingsMenu.Developer.cs: the developer tools' door (2026-11-05)
//
// Purpose:        In a debug build (running from the editor or a debug
//                 export), Settings gains a Developer section with one
//                 button. It opens the developer tools (DebugToolsPanel:
//                 Debug Mode and the debug sections that used to sit at the
//                 foot of the Guild tab) over Settings, with a Back button.
//                 A release export shows no such section. Settings opens
//                 from the title screen and from the in-game Menu, so the
//                 tools are reachable from both.
// Layer:          UI (SettingsMenu partial)
// Collaborators:  SettingsMenu.cs (calls BuildDeveloperRow from _Ready),
//                 DebugToolsPanel.cs (the tools)
// See:            docs/guild_screen_retirement_v1.md
// ============================================================
public partial class SettingsMenu
{
    private const string DeveloperToolsName = "DeveloperTools";

    /// <summary>The Developer header and its button, after the Register's rows, built from
    /// the scene's own Audio header so it matches. Nothing at all in a release build.</summary>
    private void BuildDeveloperRow()
    {
        if (!DebugToolsPanel.Available)
            return;
        var vsyncRow = _vsyncCheck?.GetParent() as Control;
        var settings = vsyncRow?.GetParent() as Control;
        if (settings == null)
        {
            GD.PrintErr("[SettingsMenu] Settings list not found, so the developer tools are not shown.");
            return;
        }
        if (FindChild("AudioSpacer", true) is Control spacer)
            settings.AddChild(spacer.Duplicate());
        if (FindChild("AudioHeader", true) is Label headerTemplate)
        {
            var header = (Label)headerTemplate.Duplicate();
            header.Name = "DeveloperHeader";
            header.Text = "Developer";
            settings.AddChild(header);
        }
        var open = new Button
        {
            Name = "DeveloperToolsButton",
            Text = "Developer tools",
            TooltipText = "Debug Mode and the debug tools. Only a debug build shows this "
                        + "(the editor, or a debug export).",
            CustomMinimumSize = new Vector2(240, 40),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        UITheme.ApplyButtonStyle(open, isPrimary: false);
        open.Pressed += OpenDeveloperTools;
        settings.AddChild(open);
    }

    /// <summary>The tools over Settings: a title, Back, and the tools in a scroll.</summary>
    private void OpenDeveloperTools()
    {
        if (FindChild(DeveloperToolsName, false, false) != null)
            return;
        var overlay = new Control
        {
            Name = DeveloperToolsName,
            MouseFilter = MouseFilterEnum.Stop,
        };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);

        var bg = new ColorRect { Color = UITheme.WorldDeep, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 32);
        margin.AddThemeConstantOverride("margin_right", 32);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        overlay.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        margin.AddChild(column);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 16);
        column.AddChild(top);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(120, 40) };
        UITheme.ApplyButtonStyle(back, isPrimary: false);
        back.Pressed += () => overlay.QueueFree();
        top.AddChild(back);
        var title = new Label { Text = "Developer tools", VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        top.AddChild(title);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        column.AddChild(scroll);
        scroll.AddChild(new DebugToolsPanel { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        AddChild(overlay);
    }
}
