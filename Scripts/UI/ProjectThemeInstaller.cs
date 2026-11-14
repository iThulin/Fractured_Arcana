using Godot;

// ============================================================
// ProjectThemeInstaller.cs
//
// Purpose:        Styles the stock engine controls in the ink and
//                 gold scheme: plain buttons, text fields, option
//                 buttons, popup menus, sliders, scroll bars, tabs,
//                 lists, separators, tooltips and dialog windows.
//                 Anything a screen does not style for itself falls
//                 back to these.
// Layer:          UI
// Collaborators:  UITheme (every colour and size), GameBootstrap
//                 (calls Install once, before any scene builds),
//                 Assets/UI/GameTheme.tres (the project theme this
//                 writes into at run time: fonts stay as authored)
// See:            claude/session_log_2026-11-13_daylight_glass_adoption.md
// Notes:          Written in code, not in the .tres, so the values
//                 come from UITheme and stay in one place. The
//                 editor preview does not show them; the running
//                 game does.
// ============================================================

/// <summary>Writes the ink and gold look for stock controls into the project theme.</summary>
public static class ProjectThemeInstaller
{
    private static bool _installed;

    /// <summary>Install once. Safe to call again: later calls do nothing.</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        Theme theme = ThemeDB.GetProjectTheme();
        if (theme == null)
        {
            GD.PushWarning("[ProjectThemeInstaller] No project theme is set (project.godot gui/theme/custom). Stock controls keep the engine look.");
            return;
        }
        _installed = true;

        InstallText(theme);
        InstallButtons(theme, "Button");
        InstallButtons(theme, "OptionButton");
        InstallButtons(theme, "MenuButton");
        InstallToggles(theme, "CheckBox");
        InstallToggles(theme, "CheckButton");
        InstallFields(theme);
        InstallPopups(theme);
        InstallSliders(theme);
        InstallScrollBars(theme);
        InstallTabs(theme, "TabContainer");
        InstallTabs(theme, "TabBar");
        InstallLists(theme);
        InstallPanelsAndRules(theme);
        InstallWindows(theme);
    }

    // ── Shared boxes ──────────────────────────────────────────────

    private static StyleBoxFlat Box(Color bg, Color border, float marginH = 0f, float marginV = 0f)
    {
        var s = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        s.SetBorderWidthAll(UITheme.BorderWidth);
        s.SetCornerRadiusAll(UITheme.GlassCornerRadius);
        s.ContentMarginLeft = s.ContentMarginRight = marginH;
        s.ContentMarginTop = s.ContentMarginBottom = marginV;
        return s;
    }

    /// <summary>A border with no fill, for focus rings drawn over another box.</summary>
    private static StyleBoxFlat Ring(Color border)
    {
        StyleBoxFlat s = Box(Colors.Transparent, border);
        s.DrawCenter = false;
        return s;
    }

    private static StyleBoxFlat Fill(Color bg)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetCornerRadiusAll(UITheme.GlassCornerRadius);
        return s;
    }

    private static Color Faded(Color c, float alpha) => new Color(c.R, c.G, c.B, alpha);

    // ── Text ──────────────────────────────────────────────────────

    private static void InstallText(Theme theme)
    {
        theme.SetColor("font_color", "Label", UITheme.TextPrimary);
        theme.SetColor("default_color", "RichTextLabel", UITheme.TextPrimary);
        theme.SetColor("font_color", "TooltipLabel", UITheme.TextPrimary);
    }

    // ── Buttons ───────────────────────────────────────────────────

    private static void InstallButtons(Theme theme, string type)
    {
        float h = UITheme.PaddingNormal + 4;
        float v = UITheme.PaddingSmall + 2;
        theme.SetStylebox("normal", type, Box(UITheme.GlassInk, UITheme.GlassLine, h, v));
        theme.SetStylebox("hover", type, Box(UITheme.GlassInkRaised, UITheme.GlassLine, h, v));
        theme.SetStylebox("pressed", type, Box(UITheme.GlassInkRaised, UITheme.GlassTitle, h, v));
        theme.SetStylebox("disabled", type, Box(UITheme.GlassInk, UITheme.GlassLineDim, h, v));
        theme.SetStylebox("focus", type, Ring(UITheme.GlassTitle));

        theme.SetColor("font_color", type, UITheme.TextPrimary);
        theme.SetColor("font_hover_color", type, UITheme.TextPrimary);
        theme.SetColor("font_pressed_color", type, UITheme.GlassTitle);
        theme.SetColor("font_hover_pressed_color", type, UITheme.GlassTitle);
        theme.SetColor("font_focus_color", type, UITheme.TextPrimary);
        theme.SetColor("font_disabled_color", type, UITheme.TextDim);
    }

    private static void InstallToggles(Theme theme, string type)
    {
        theme.SetColor("font_color", type, UITheme.TextPrimary);
        theme.SetColor("font_hover_color", type, UITheme.GlassTitle);
        theme.SetColor("font_pressed_color", type, UITheme.TextPrimary);
        theme.SetColor("font_hover_pressed_color", type, UITheme.GlassTitle);
        theme.SetColor("font_focus_color", type, UITheme.TextPrimary);
        theme.SetColor("font_disabled_color", type, UITheme.TextDim);
        theme.SetStylebox("focus", type, Ring(UITheme.GlassLineDim));

        // A check box is a Button to the engine, so without these it picks up the boxed
        // Button look above (border, padding) and a row of them swells.
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
        {
            var bare = new StyleBoxEmpty();
            bare.SetContentMarginAll(2f);
            theme.SetStylebox(state, type, bare);
        }
    }

    // ── Text fields ───────────────────────────────────────────────

    private static void InstallFields(Theme theme)
    {
        foreach (string type in new[] { "LineEdit", "TextEdit" })
        {
            theme.SetStylebox("normal", type, Box(UITheme.BgDeep, UITheme.Neutral, 8f, 6f));
            theme.SetStylebox("focus", type, Ring(UITheme.GlassLine));
            theme.SetStylebox("read_only", type, Box(UITheme.BgBase, UITheme.NeutralDim, 8f, 6f));

            theme.SetColor("font_color", type, UITheme.TextPrimary);
            theme.SetColor("font_placeholder_color", type, UITheme.TextDim);
            theme.SetColor("font_uneditable_color", type, UITheme.TextDim);
            theme.SetColor("font_readonly_color", type, UITheme.TextDim);
            theme.SetColor("caret_color", type, UITheme.GlassTitle);
            theme.SetColor("selection_color", type, Faded(UITheme.GlassLine, 0.35f));
        }
    }

    // ── Popups and tooltips ───────────────────────────────────────

    private static void InstallPopups(Theme theme)
    {
        theme.SetStylebox("panel", "PopupMenu", Box(UITheme.GlassInkSolid, UITheme.GlassLine, 6f, 6f));
        theme.SetStylebox("hover", "PopupMenu", Fill(UITheme.GlassInkRaised));
        theme.SetStylebox("separator", "PopupMenu", Rule(false));
        theme.SetColor("font_color", "PopupMenu", UITheme.TextPrimary);
        theme.SetColor("font_hover_color", "PopupMenu", UITheme.GlassTitle);
        theme.SetColor("font_disabled_color", "PopupMenu", UITheme.TextDim);
        theme.SetColor("font_separator_color", "PopupMenu", UITheme.TextDim);
        theme.SetColor("font_accelerator_color", "PopupMenu", UITheme.TextDim);

        theme.SetStylebox("panel", "PopupPanel", Box(UITheme.GlassInkSolid, UITheme.GlassLine, 8f, 8f));
        theme.SetStylebox("panel", "TooltipPanel", Box(UITheme.GlassInkSolid, UITheme.GlassLine, 8f, 5f));
    }

    // ── Sliders and scroll bars ───────────────────────────────────

    private static void InstallSliders(Theme theme)
    {
        foreach (string type in new[] { "HSlider", "VSlider" })
        {
            StyleBoxFlat track = Box(UITheme.BgDeep, UITheme.NeutralDim, 3f, 3f);
            theme.SetStylebox("slider", type, track);
            theme.SetStylebox("grabber_area", type, Fill(UITheme.GlassLineDim));
            theme.SetStylebox("grabber_area_highlight", type, Fill(UITheme.GlassLine));
        }
    }

    private static void InstallScrollBars(Theme theme)
    {
        foreach (string type in new[] { "HScrollBar", "VScrollBar" })
        {
            var track = new StyleBoxFlat { BgColor = UITheme.BgDeep };
            track.SetCornerRadiusAll(UITheme.GlassCornerRadius);
            track.SetContentMarginAll(4f);
            theme.SetStylebox("scroll", type, track);
            theme.SetStylebox("scroll_focus", type, track);
            theme.SetStylebox("grabber", type, Fill(UITheme.Neutral));
            theme.SetStylebox("grabber_highlight", type, Fill(UITheme.GlassLineDim));
            theme.SetStylebox("grabber_pressed", type, Fill(UITheme.GlassLine));
        }
    }

    // ── Tabs: the selected tab inverts, the rest are standard ─────

    private static void InstallTabs(Theme theme, string type)
    {
        StyleBoxFlat selected = Box(UITheme.GlassPrimary, UITheme.GlassPrimaryEdge, 12f, 6f);
        theme.SetStylebox("tab_selected", type, selected);
        theme.SetStylebox("tab_unselected", type, Box(UITheme.GlassInk, UITheme.GlassLineDim, 12f, 6f));
        theme.SetStylebox("tab_hovered", type, Box(UITheme.GlassInkRaised, UITheme.GlassLine, 12f, 6f));
        theme.SetStylebox("tab_disabled", type, Box(UITheme.GlassInk, UITheme.NeutralDim, 12f, 6f));
        theme.SetStylebox("tab_focus", type, Ring(UITheme.GlassTitle));

        theme.SetColor("font_selected_color", type, UITheme.GlassPrimaryInk);
        theme.SetColor("font_unselected_color", type, UITheme.TextPrimary);
        theme.SetColor("font_hovered_color", type, UITheme.TextPrimary);
        theme.SetColor("font_disabled_color", type, UITheme.TextDim);

        if (type == "TabContainer")
        {
            theme.SetStylebox("panel", type, Box(UITheme.BgBase, UITheme.GlassLineDim, 8f, 8f));
        }
    }

    // ── Lists ─────────────────────────────────────────────────────

    private static void InstallLists(Theme theme)
    {
        foreach (string type in new[] { "ItemList", "Tree" })
        {
            theme.SetStylebox("panel", type, Box(UITheme.BgDeep, UITheme.NeutralDim, 4f, 4f));
            theme.SetStylebox("focus", type, Ring(UITheme.GlassLineDim));
            theme.SetStylebox("selected", type, Fill(Faded(UITheme.GlassLine, 0.25f)));
            theme.SetStylebox("selected_focus", type, Fill(Faded(UITheme.GlassLine, 0.35f)));
            theme.SetStylebox("hovered", type, Fill(UITheme.GlassInkRaised));
            theme.SetColor("font_color", type, UITheme.TextSecondary);
            theme.SetColor("font_selected_color", type, UITheme.TextPrimary);
            theme.SetColor("font_hovered_color", type, UITheme.TextPrimary);
        }
    }

    // ── Panels, separators, progress bars ─────────────────────────

    private static StyleBoxLine Rule(bool vertical)
    {
        return new StyleBoxLine { Color = UITheme.GlassLineDim, Thickness = 1, Vertical = vertical };
    }

    private static void InstallPanelsAndRules(Theme theme)
    {
        theme.SetStylebox("panel", "Panel", Box(UITheme.BgBase, UITheme.GlassLineDim));
        theme.SetStylebox("panel", "PanelContainer", Box(UITheme.BgBase, UITheme.GlassLineDim));
        theme.SetStylebox("separator", "HSeparator", Rule(false));
        theme.SetStylebox("separator", "VSeparator", Rule(true));

        theme.SetStylebox("background", "ProgressBar", Box(UITheme.BgDeep, UITheme.NeutralDim));
        theme.SetStylebox("fill", "ProgressBar", Fill(UITheme.GlassLine));
        theme.SetColor("font_color", "ProgressBar", UITheme.TextPrimary);
    }

    // ── Dialog windows ────────────────────────────────────────────

    private static void InstallWindows(Theme theme)
    {
        // The engine draws the title bar in the border box's top expand margin, so the
        // margins below match the stock theme's (content 10/4/10/8, expand 8/32/8/6).
        foreach (string name in new[] { "embedded_border", "embedded_unfocused_border" })
        {
            bool focused = name == "embedded_border";
            StyleBoxFlat border = Box(UITheme.GlassInkSolid, focused ? UITheme.GlassLine : UITheme.GlassLineDim);
            border.ContentMarginLeft = 10f;
            border.ContentMarginTop = 4f;
            border.ContentMarginRight = 10f;
            border.ContentMarginBottom = 8f;
            border.ExpandMarginLeft = 8f;
            border.ExpandMarginTop = 32f;
            border.ExpandMarginRight = 8f;
            border.ExpandMarginBottom = 6f;
            theme.SetStylebox(name, "Window", border);
        }
        theme.SetColor("title_color", "Window", UITheme.GlassTitle);

        var dialogPanel = new StyleBoxFlat { BgColor = UITheme.GlassInkSolid };
        dialogPanel.SetContentMarginAll(8f);
        theme.SetStylebox("panel", "AcceptDialog", dialogPanel);
    }
}
