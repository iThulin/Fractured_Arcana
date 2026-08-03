using Godot;

// ============================================================
// CampusUi.cs
//
// Purpose:        The shared widget factory every campus panel
//                 builds out of. Extracted verbatim from
//                 CampusScreen so the nine tab bodies can move
//                 into their own files without each one dragging
//                 a private copy of these six helpers along.
// Layer:          UI
// Collaborators:  CampusScreen.cs (via `using static CampusUi;`),
//                 UITheme.cs (every constant here comes from it)
// See:            docs/campus_tab_extraction_v1.md — Phase 1
// ============================================================

/// <summary>Stateless widget factory for the campus screens. Every method here was a
/// private helper on <c>CampusScreen</c> and is unchanged in behaviour; they were pure
/// (no <c>this</c> access) so the lift was mechanical.
///
/// Consumers pull these in with a file-scoped <c>using static CampusUi;</c> rather than
/// qualifying each call, which keeps ~200 existing call sites byte-identical while still
/// leaving exactly one implementation. Do not re-add instance copies to any panel — two
/// implementations of a widget factory is how campus layout drifts between tabs.
///
/// Note the explicit <c>Control.SizeFlags</c> qualification below: inside CampusScreen
/// (itself a Control) the bare <c>SizeFlags</c> resolved through inheritance. It does not
/// here, and the compiler error is easy to "fix" wrongly.</summary>
public static class CampusUi
{
    /// <summary>Centred section title in the campus section colour.</summary>
    public static void AddSectionHeader(VBoxContainer parent, string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
        label.AddThemeColorOverride("font_color", UITheme.CampusSectionColor);
        parent.AddChild(label);
    }

    /// <summary>VBox with an explicit child separation.</summary>
    public static VBoxContainer MakeVBox(int separation)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", separation);
        return v;
    }

    /// <summary>Margin container with symmetric horizontal/vertical insets, expanding
    /// horizontally and hugging its content vertically.</summary>
    public static MarginContainer MakeMargins(int horizontal, int vertical)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", horizontal);
        m.AddThemeConstantOverride("margin_right", horizontal);
        m.AddThemeConstantOverride("margin_top", vertical);
        m.AddThemeConstantOverride("margin_bottom", vertical);
        m.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        m.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return m;
    }

    /// <summary>Themed button at a fixed minimum size, centred in its parent.</summary>
    public static Button MakeButton(string text, float minWidth, float minHeight, int fontSize,
        bool isPrimary = true)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(minWidth, minHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        btn.AddThemeFontSizeOverride("font_size", fontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary);
        return btn;
    }

    /// <summary>Dimmed, centred, wrapping "nothing here yet" label.</summary>
    public static Label MakeStubLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeFontSizeOverride("font_size", UITheme.CampusStubFontSize);
        label.Modulate = UITheme.CampusStubText;
        return label;
    }

    /// <summary>Flat, square tab-bar button styling — active tabs carry a violet
    /// underline rather than a raised body, so the bar reads as one continuous strip.
    ///
    /// Stays here rather than moving into a panel: it styles the SELECTOR, not any
    /// panel's content. When the campus map becomes the second selector alongside the
    /// tab bar, this is the piece the map does NOT need.</summary>
    public static void ApplyTabStyle(Button btn, bool isActive)
    {
        // Flat style — no rounded corners, continuous bar appearance
        var normal = new StyleBoxFlat
        {
            BgColor = isActive ? UITheme.ButtonPrimary : UITheme.BgDeep,
            BorderColor = isActive ? UITheme.Violet : UITheme.NeutralDim,
            BorderWidthBottom = isActive ? 2 : 0,
            BorderWidthTop = 0,
            BorderWidthLeft = 0,
            BorderWidthRight = 0,
            // No corner radius — square tabs
        };
        var hover = new StyleBoxFlat
        {
            BgColor = isActive ? UITheme.ButtonPrimaryHover : UITheme.BgBase,
            BorderColor = UITheme.Violet,
            BorderWidthBottom = 2,
            BorderWidthTop = 0,
            BorderWidthLeft = 0,
            BorderWidthRight = 0,
        };

        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", normal);
        btn.AddThemeStyleboxOverride("focus", normal);
        btn.AddThemeColorOverride("font_color",
            isActive ? UITheme.TextPrimary : UITheme.TextSecondary);
    }
}
