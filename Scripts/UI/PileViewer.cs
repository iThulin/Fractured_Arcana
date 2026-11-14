using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// PileViewer.cs
//
// Purpose:        Shows a pile of cards (the deck or the grave in a
//                 fight) as card faces in a scrolling grid, in place
//                 of a list of names.
// Layer:          UI
// Collaborators:  CombatUI (opens it from the Deck and Grave
//                 buttons), CardUi (draws each face, static display),
//                 GlassPanel and UITheme (the frame and colours),
//                 CameraController (leaves the wheel to the grid)
// See:            claude/session_log_2026-11-13_daylight_glass_pass_3.md
// Notes:          Ruled 2026-11-13 (Magos): the deck shows sorted,
//                 so the draw order stays hidden; the grave shows in
//                 the order the cards arrived.
// ============================================================

/// <summary>A full-screen overlay listing one pile as card faces. One at a time.</summary>
public partial class PileViewer : Control
{
    private const string CardScenePath = "res://Scenes/Cards/CardUI.tscn";
    private const float CardScale = 0.8f;
    private const int Columns = 5;
    private const int CellGap = 14;
    private const int PanelHalfWidth = 480;
    private const int PanelHalfHeight = 330;
    private const int AboveHandZ = 200;   // hand cards sit at z 100

    private static PileViewer _open;

    private string _title = "";
    private string _note = "";
    private List<Card> _cards = new List<Card>();
    private bool _groupByName;

    /// <summary>True while a pile is on screen.</summary>
    public static bool IsOpen => _open != null && IsInstanceValid(_open);

    /// <summary>
    /// Open the viewer over <paramref name="host"/>. groupByName sorts the pile by name
    /// and stacks copies under one face with a count; otherwise the given order is kept
    /// and each face is numbered.
    /// </summary>
    public static void Open(Node host, string title, string note, IReadOnlyList<Card> cards, bool groupByName)
    {
        if (host == null || !IsInstanceValid(host))
        {
            return;
        }
        if (IsOpen)
        {
            _open.Close();
        }

        var viewer = new PileViewer
        {
            Name = "PileViewer",
            _title = title ?? "",
            _note = note ?? "",
            _groupByName = groupByName,
        };
        if (cards != null)
        {
            viewer._cards.AddRange(cards);
        }
        _open = viewer;
        host.AddChild(viewer);
        viewer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready()
    {
        ZIndex = AboveHandZ;
        MouseFilter = MouseFilterEnum.Stop;
        CallDeferred(nameof(BuildUI));
    }

    public override void _ExitTree()
    {
        if (_open == this)
        {
            _open = null;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }

    private void Close()
    {
        if (_open == this)
        {
            _open = null;
        }
        QueueFree();
    }

    private void BuildUI()
    {
        // A click on the dimmed field around the panel closes the viewer.
        var backdrop = new ColorRect { Color = UITheme.BgOverlay, MouseFilter = MouseFilterEnum.Stop };
        AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        backdrop.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                Close();
            }
        };

        var panel = new GlassPanel
        {
            Name = "Panel",
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -PanelHalfWidth, OffsetRight = PanelHalfWidth,
            OffsetTop = -PanelHalfHeight, OffsetBottom = PanelHalfHeight,
            MouseFilter = MouseFilterEnum.Stop,
        };
        StyleBoxFlat style = UITheme.MakeGlassPanelStyle();
        style.BgColor = UITheme.GlassInkSolid;
        style.ContentMarginLeft = style.ContentMarginRight = 20;
        style.ContentMarginTop = style.ContentMarginBottom = 16;
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 8);
        panel.AddChild(layout);

        var header = new HBoxContainer();
        layout.AddChild(header);

        string countText = _cards.Count == 1 ? "1 card" : $"{_cards.Count} cards";
        var title = new Label
        {
            Text = $"{_title}: {countText}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        title.AddThemeFontOverride("font", UITheme.GlassDisplayFont);
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        title.AddThemeColorOverride("font_color", UITheme.GlassTitle);
        header.AddChild(title);

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(96, 36) };
        UITheme.ApplyGlassButtonStyle(close);
        close.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        close.Pressed += Close;
        header.AddChild(close);

        if (!string.IsNullOrEmpty(_note))
        {
            var note = new Label { Text = _note, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            note.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            note.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            layout.AddChild(note);
        }

        layout.AddChild(new HSeparator());

        if (_cards.Count == 0)
        {
            var empty = new Label
            {
                Text = "No cards here.",
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
            };
            empty.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            layout.AddChild(empty);
            return;
        }

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        layout.AddChild(scroll);

        var grid = new GridContainer { Columns = Columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", CellGap);
        grid.AddThemeConstantOverride("v_separation", CellGap);
        scroll.AddChild(grid);

        var scene = GD.Load<PackedScene>(CardScenePath);
        foreach ((Card card, string caption) in Entries())
        {
            grid.AddChild(BuildCell(scene, card, caption));
        }
    }

    /// <summary>The faces to show, each with the line under it.</summary>
    private List<(Card Card, string Caption)> Entries()
    {
        var entries = new List<(Card Card, string Caption)>();
        if (!_groupByName)
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                entries.Add((_cards[i], $"{i + 1}"));
            }
            return entries;
        }

        var sorted = new List<Card>(_cards);
        sorted.Sort((a, b) => string.Compare(a?.CardName, b?.CardName, StringComparison.OrdinalIgnoreCase));

        int index = 0;
        while (index < sorted.Count)
        {
            Card first = sorted[index];
            int copies = 1;
            while (index + copies < sorted.Count
                   && string.Equals(sorted[index + copies]?.CardName, first?.CardName, StringComparison.OrdinalIgnoreCase))
            {
                copies++;
            }
            entries.Add((first, copies > 1 ? $"{copies} copies" : ""));
            index += copies;
        }
        return entries;
    }

    private Control BuildCell(PackedScene scene, Card card, string caption)
    {
        var cell = new VBoxContainer();
        cell.AddThemeConstantOverride("separation", 4);

        // CardUi lays itself out at full size and is scaled down, so the wrapper holds
        // the scaled footprint for the grid.
        var wrapper = new Control
        {
            CustomMinimumSize = new Vector2(UITheme.HandCardWidth * CardScale, UITheme.HandCardHeight * CardScale),
        };
        cell.AddChild(wrapper);

        if (scene != null && card != null)
        {
            var cardUi = scene.Instantiate<CardUi>();
            wrapper.AddChild(cardUi);
            cardUi.SetCard(card);
            cardUi.SetProcess(false);

            // CardUi parks itself off-screen and transparent for its draw-in tween in
            // _Ready. A short timer lets that finish, then the face is pinned in place.
            SceneTreeTimer timer = GetTree().CreateTimer(0.05f);
            timer.Timeout += () =>
            {
                if (IsInstanceValid(cardUi))
                {
                    cardUi.SetStaticDisplay(CardScale);
                }
            };
        }

        var label = new Label { Text = caption, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        label.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        cell.AddChild(label);

        return cell;
    }
}
