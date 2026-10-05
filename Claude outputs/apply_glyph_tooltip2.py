#!/usr/bin/env python3
# Glyph tooltip pass 2 (2026-10-04): no duplicate card text, padded panel. Run from repo root.
import sys
def edit(rel, pairs):
    s = open(rel, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        if n != 1:
            sys.exit(f"{rel}: expected 1 match, found {n}:\n{old[:140]}")
        s = s.replace(old, new)
    open(rel, 'w', encoding='utf-8').write(s)
    print("patched", rel)

edit('Scripts/Systems/TooltipManager.cs', [
# Padding: the panel and the content were siblings in one MarginContainer, so the text
# sat flush against the border. Content now lives in its own padded container.
("""        _content = new VBoxContainer();
        _content.MouseFilter = MouseFilterEnum.Ignore;
        _content.AddThemeConstantOverride("separation", 3);
        margin.AddChild(_content);""",
"""        var pad = new MarginContainer();
        pad.MouseFilter = MouseFilterEnum.Ignore;
        pad.AddThemeConstantOverride("margin_left", 10);
        pad.AddThemeConstantOverride("margin_right", 10);
        pad.AddThemeConstantOverride("margin_top", 6);
        pad.AddThemeConstantOverride("margin_bottom", 6);
        margin.AddChild(pad);

        _content = new VBoxContainer();
        _content.MouseFilter = MouseFilterEnum.Ignore;
        _content.AddThemeConstantOverride("separation", 3);
        pad.AddChild(_content);"""),
("""        AddSeparator();
        AddColoredRow(name, mine ? "yours" : "enemy", mine ? UITheme.Success : UITheme.Danger);""",
"""        AddSeparator();
        AddPairRow(name, GlyphAccent, mine ? "yours" : "enemy", mine ? UITheme.Success : UITheme.Danger);"""),
("""        foreach (var line in PayloadLines(g, owner))
            AddWrapped(line, UITheme.TextPrimary);""",
"""        var payload = PayloadLines(g, owner);
        foreach (var line in payload)
            AddWrapped(line, UITheme.TextPrimary);"""),
("""        if (!string.IsNullOrEmpty(g.SourceRulesText))
            AddWrapped(g.SourceRulesText, UITheme.TextSecondary);
    }""",
"""        // The card text is a fallback only. When the glyph's own payload says what it does,
        // the card text just repeats it plus the casting instructions. When the payload is
        // empty (an effect GlyphData does not model, such as a cost aura), show what the
        // card says the glyph does, minus the placement and cast-time sentences.
        if (payload.Count == 0)
        {
            string rest = GlyphEffectText(g.SourceRulesText);
            if (!string.IsNullOrEmpty(rest))
                AddWrapped(rest, UITheme.TextPrimary);
        }
    }

    /// <summary>The rules text with the sentences about casting removed: where the glyph is
    /// prepared, how long it lasts and what the caster gains on cast. What is left is what
    /// the glyph itself does.</summary>
    private static string GlyphEffectText(string rules)
    {
        if (string.IsNullOrWhiteSpace(rules))
            return "";
        var kept = new List<string>();
        foreach (var raw in System.Text.RegularExpressions.Regex.Split(rules.Trim(), @"(?<=[.!?])\\s+"))
        {
            string s = raw.Trim();
            if (s.Length == 0)
                continue;
            string lower = s.ToLowerInvariant();
            if (lower.StartsWith("prepare ") || lower.StartsWith("lasts ")
                || lower.StartsWith("gain ") || lower.StartsWith("leave a glyph"))
                continue;
            kept.Add(s);
        }
        return string.Join(" ", kept);
    }"""),
("""    private void AddWrapped(string text, Color color)""",
"""    private void AddPairRow(string key, Color keyColor, string value, Color valueColor)
    {
        var hbox = new HBoxContainer();
        hbox.MouseFilter = MouseFilterEnum.Ignore;

        var keyLbl = new Label { Text = key };
        keyLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize + 1);
        keyLbl.AddThemeColorOverride("font_color", keyColor);
        keyLbl.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(keyLbl);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        spacer.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(spacer);

        var valLbl = new Label { Text = value };
        valLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        valLbl.AddThemeColorOverride("font_color", valueColor);
        valLbl.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(valLbl);

        _content.AddChild(hbox);
    }

    private void AddWrapped(string text, Color color)"""),
])
