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
            Text = "Imbuing a tile that already holds a different element forms a reaction instead of replacing it.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(CellWidth * 5, 0),
        };
        intro.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        root.AddChild(intro);

        var grid = new GridContainer { Columns = Elements.Length + 1 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        root.AddChild(grid);

        // Header row: a blank corner, then each element.
        grid.AddChild(Cell("", UITheme.TextSecondary, null));
        foreach (var e in Elements)
            grid.AddChild(Cell(e.Name, ElementColors.Get(e.ColorTag), null, bold: true));

        foreach (var row in Elements)
        {
            grid.AddChild(Cell(row.Name, ElementColors.Get(row.ColorTag), null, bold: true));
            foreach (var col in Elements)
            {
                var reaction = ElementReactions.Resolve(row.Element, col.Element);
                if (reaction == ElementReaction.None)
                {
                    grid.AddChild(Cell("same", UITheme.TextSecondary, null));
                    continue;
                }
                var cell = Cell(ElementReactions.DisplayName(reaction), ElementColors.Reaction(reaction),
                                new Color(UITheme.BgCard.R, UITheme.BgCard.G, UITheme.BgCard.B, 1f), bold: true);
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

            var name = new Label { Text = ElementReactions.DisplayName(r), CustomMinimumSize = new Vector2(96, 0) };
            name.AddThemeColorOverride("font_color", ElementColors.Reaction(r));
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
        tie.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        root.AddChild(tie);

        return root;
    }

    private static Control Cell(string text, Color fg, Color? bg, bool bold = false)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(CellWidth, CellHeight) };
        var style = new StyleBoxFlat
        {
            BgColor = bg ?? new Color(0f, 0f, 0f, 0f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
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
