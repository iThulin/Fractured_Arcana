using Godot;
using System;

public static class CardRowHelpers
{
    public static Label MakePip(string text, Color color)
    {
        var pip = new Label { Text = text };
        pip.CustomMinimumSize = new Vector2(24, 24);
        pip.HorizontalAlignment = HorizontalAlignment.Center;
        pip.VerticalAlignment = VerticalAlignment.Center;
        pip.MouseFilter = Control.MouseFilterEnum.Ignore;
        pip.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize + 1);
        pip.AddThemeColorOverride("font_color", Colors.White);
        var style = new StyleBoxFlat { BgColor = new Color(color.R, color.G, color.B) };
        style.SetCornerRadiusAll(12);
        style.ContentMarginLeft = 4;
        style.ContentMarginRight = 4;
        pip.AddThemeStyleboxOverride("normal", style);
        return pip;
    }

    public static void AddElementTags(HBoxContainer parent, CardHalf half)
    {
        foreach (var tag in half?.Tags ?? Array.Empty<string>())
        {
            var pip = new Label
            {
                Text = ElementColors.GetLabel(tag),
                CustomMinimumSize = new Vector2(0, 14),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.Off,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            pip.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize - 1);
            pip.AddThemeColorOverride("font_color", Colors.White);
            var style = new StyleBoxFlat { BgColor = ElementColors.Get(tag) };
            style.SetCornerRadiusAll(3);
            style.ContentMarginLeft = 4;
            style.ContentMarginRight = 4;
            style.ContentMarginTop = 1;
            style.ContentMarginBottom = 1;
            pip.AddThemeStyleboxOverride("normal", style);
            parent.AddChild(pip);
        }
    }
}