using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatSummaryPanel.cs
//
// Purpose:        Post-combat spoils card (2026-08-13, Magos
//                 request): one compact centered panel on the
//                 expedition map summarizing what a VICTORY paid:
//                 gold, splinters, item drops (rarity-colored,
//                 blight-flagged), and relic/guardian beats, instead
//                 of the rewards scattering into toasts. Defeat
//                 deliberately has NO card: it routes into
//                 FailExpedition's banner with the casualty note.
// Layer:          UI (expedition)
// Collaborators:  ExpeditionManager.RestoreFromCombat (collects
//                 the lines, shows the card), UITheme.
// ============================================================

/// <summary>A modal spoils card. The host collects (text, color) lines while
/// applying rewards, then calls <see cref="Show"/>; Continue dismisses and
/// runs the callback (the host re-enables map input there).</summary>
public sealed partial class CombatSummaryPanel : CanvasLayer
{
    private List<(string text, Color color)> _lines;
    private Action _onClosed;

    public static CombatSummaryPanel Show(Node host, List<(string, Color)> lines, Action onClosed)
    {
        var p = new CombatSummaryPanel
        {
            Name = "CombatSummaryPanel",
            Layer = 60,
            _lines = lines ?? new List<(string, Color)>(),
            _onClosed = onClosed,
        };
        host.AddChild(p);
        return p;
    }

    public override void _Ready() => CallDeferred(nameof(BuildOverlay));

    private void BuildOverlay()
    {
        // Dimmed full-rect catcher: the map stays visible but muted, and no
        // click reaches it until Continue (the CampusScreen dimmer lesson).
        var catcher = new ColorRect
        {
            Name = "Backdrop",
            Color = UITheme.BgOverlay,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        catcher.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(catcher);

        var card = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -240, OffsetRight = 240,
        };
        card.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        catcher.AddChild(card);

        var margins = new MarginContainer();
        margins.AddThemeConstantOverride("margin_left", 20);
        margins.AddThemeConstantOverride("margin_right", 20);
        margins.AddThemeConstantOverride("margin_top", 16);
        margins.AddThemeConstantOverride("margin_bottom", 16);
        card.AddChild(margins);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margins.AddChild(vbox);

        var title = new Label
        {
            Text = "Victory: the Spoils",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(title);

        vbox.AddChild(new HSeparator());

        if (_lines.Count == 0)
        {
            var none = new Label
            {
                Text = "The field yields nothing but the win itself.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            none.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            none.AddThemeColorOverride("font_color", UITheme.TextDim);
            vbox.AddChild(none);
        }

        foreach (var (text, color) in _lines)
        {
            var line = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            line.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            line.AddThemeColorOverride("font_color", color);
            vbox.AddChild(line);
        }

        var btn = new Button
        {
            Text = "Continue",
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(160, 40),
        };
        UITheme.ApplyButtonStyle(btn, isPrimary: true);
        btn.Pressed += Close;
        vbox.AddChild(btn);
    }

    private void Close()
    {
        var cb = _onClosed;
        _onClosed = null;
        cb?.Invoke();
        QueueFree();
    }
}
