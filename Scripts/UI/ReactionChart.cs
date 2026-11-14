using Godot;

// ============================================================
// ReactionChart.cs
//
// Purpose:        The element reaction reference chart
//                 (class_identity_elementalist_v1 §2a item 4): a
//                 4×4 grid of Fire / Frost / Storm / Earth showing
//                 which reaction each pair forms, plus a key with
//                 each reaction's rule text. Built entirely from
//                 ElementReactions, so the chart can never disagree
//                 with the rules.
//                 Reachable from the pause menu, the Card Library
//                 and the Register's margin notes.
// Layer:          UI
// Collaborators:  ElementReactions.cs (Resolve, DisplayName,
//                 Describe), ElementColors.cs, PauseMenu.cs,
//                 CardLibraryUi.cs, RegisterManager.cs
// See:            docs/class_identity_elementalist_v1.md §2
// ============================================================

/// <summary>Builds and shows the element reaction reference chart.</summary>
public static class ReactionChart
{
    private static readonly (TileElementType Element, string Name, string ColorTag)[] Elements =
    {
        (TileElementType.Fire, "Fire", "fire"),
        (TileElementType.Frost, "Frost", "ice"),
        (TileElementType.Lightning, "Storm", "storm"),
        (TileElementType.Earth, "Earth", "earth"),
    };

    private static readonly ElementReaction[] Reactions =
    {
        ElementReaction.Steam,
        ElementReaction.Fulgurite,
        ElementReaction.Magma,
        ElementReaction.Wildfire,
        ElementReaction.Brittle,
        ElementReaction.Glacier,
    };

    private const int CellWidth = 112;
    private const int CellHeight = 40;

    /// <summary>Opens the chart in a dialog parented to <paramref name="host"/>. Works while the tree is paused.</summary>
    public static void ShowDialog(Node host)
    {
        if (host == null || !GodotObject.IsInstanceValid(host))
            return;

        var dlg = new AcceptDialog
        {
            Title = "Element reactions",
            OkButtonText = "Close",
            ProcessMode = Node.ProcessModeEnum.Always,
            Exclusive = true,
        };
        dlg.AddChild(BuildPanel());
        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        host.AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>The chart as a standalone control: the grid, then the key.</summary>
    public static Control BuildPanel()
    {
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);

        var intro = new Label
        {
            Text = "Imbue a tile that already holds a different element and the two combine into a reaction. The new element does not replace the old one. Find the row for one element and the column for the other.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(CellWidth * 5, 0),
        };
        intro.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        root.AddChild(intro);

        var grid = new GridContainer { Columns = Elements.Length + 1 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        root.AddChild(grid);

        // Header row: a blank corner, then each element.
        grid.AddChild(Cell("", UITheme.TextSecondary, null));
        foreach (var e in Elements)
            grid.AddChild(Chip(e.Name, ElementColors.Get(e.ColorTag)));

        foreach (var row in Elements)
        {
            grid.AddChild(Chip(row.Name, ElementColors.Get(row.ColorTag)));
            foreach (var col in Elements)
            {
                var reaction = ElementReactions.Resolve(row.Element, col.Element);
                if (reaction == ElementReaction.None)
                {
                    grid.AddChild(Cell("-", UITheme.TextDim, null));
                    continue;
                }
                var cell = Chip(ElementReactions.DisplayName(reaction), ElementColors.Reaction(reaction));
                cell.TooltipText = ElementReactions.Describe(reaction);
                grid.AddChild(cell);
            }
        }

        // The key: one line per reaction, in the chart's own colours.
        var key = new VBoxContainer();
        key.AddThemeConstantOverride("separation", 6);
        root.AddChild(key);
        foreach (var r in Reactions)
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 10);

            Control name = Chip(ElementReactions.DisplayName(r), ElementColors.Reaction(r));
            name.CustomMinimumSize = new Vector2(96, 28);
            name.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            line.AddChild(name);

            var text = new Label
            {
                Text = ElementReactions.Describe(r),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(CellWidth * 5 - 106, 0),
            };
            text.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            line.AddChild(text);

            key.AddChild(line);
        }

        var tie = new Label
        {
            Text = "Ties in attunement resolve Fire, then Frost, then Storm, then Earth.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(CellWidth * 5, 0),
        };
        tie.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        root.AddChild(tie);

        return root;
    }

    /// <summary>A cell filled with its own colour, with dark or light text chosen for
    /// contrast. Coloured text on the ink panel was too faint for Earth and Fire.</summary>
    private static Control Chip(string text, Color fill)
    {
        return Cell(text, ElementColors.GetTextColor(fill), fill);
    }

    private static Control Cell(string text, Color fg, Color? bg, bool bold = false)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(CellWidth, CellHeight) };
        var style = new StyleBoxFlat
        {
            BgColor = bg ?? new Color(0f, 0f, 0f, 0f),
            CornerRadiusTopLeft = UITheme.GlassCornerRadius,
            CornerRadiusTopRight = UITheme.GlassCornerRadius,
            CornerRadiusBottomLeft = UITheme.GlassCornerRadius,
            CornerRadiusBottomRight = UITheme.GlassCornerRadius,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", fg);
        if (bold)
        {
            label.AddThemeConstantOverride("outline_size", 2);
            label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.6f));
        }
        panel.AddChild(label);
        return panel;
    }
}
