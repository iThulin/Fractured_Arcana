#!/usr/bin/env python3
# Nameplates v2 (2026-09-28): surgical find/replace patches for the two LIVE files
# (UITheme.cs, CombatUI.cs). Every find must match exactly once or nothing is written.
# Run from the repo root:  python3 "Claude outputs/nameplates_v2_apply.py"
import sys

PATCHES = {
"Scripts/UI/UITheme.cs": [
# 1. Nameplate and condition tokens.
("""    public const int MaxActionLogLines = 6;
""",
"""    public const int MaxActionLogLines = 6;

    // Unit nameplates (UnitNameplate.cs, screen-space, one per unit).
    // Condition families: the chip colour says what KIND of condition it is
    // before the text is read (see StatusTone in StatusCatalog.cs).
    public static readonly Color ConditionDebuff = new Color(0.86f, 0.30f, 0.34f, 1f); // crimson: lockouts, weakens
    public static readonly Color ConditionDamage = new Color(0.95f, 0.52f, 0.18f, 1f); // ember: damage over time
    public static readonly Color ConditionMark   = new Color(0.72f, 0.46f, 0.95f, 1f); // violet: pays off when hit
    public static readonly Color ConditionBuff   = new Color(0.36f, 0.80f, 0.52f, 1f); // green: helps the bearer
    public static readonly Color ConditionTrait  = new Color(0.62f, 0.64f, 0.72f, 1f); // slate: standing traits
    // Defense badges: three shapes AND three hues, in the order damage spends them.
    public static readonly Color PlateShield = new Color(0.36f, 0.82f, 0.98f, 1f); // cyan kite: temporary, spent first
    public static readonly Color PlateArmor  = new Color(0.74f, 0.78f, 0.86f, 1f); // steel plate: lasting, spent second
    public static readonly Color PlateCover  = new Color(0.64f, 0.76f, 0.42f, 1f); // moss wall: bolts from cover only
    public static readonly Color PlateAllyFill    = new Color(0.30f, 0.80f, 0.42f, 1f);
    public static readonly Color PlateEnemyFill   = new Color(0.86f, 0.26f, 0.24f, 1f);
    public static readonly Color PlateNeutralFill = new Color(0.62f, 0.60f, 0.56f, 1f);
    public static readonly Color PlateBarBack     = new Color(0.06f, 0.05f, 0.08f, 0.92f);
    public static readonly Color PlatePreview     = new Color(1.00f, 0.92f, 0.85f, 1f); // near-white flash on the red/green fill
    public static readonly Color PlateAp   = Gold;
    public static readonly Color PlateEdge = new Color(1.00f, 0.60f, 0.20f, 1f);
    public static readonly Color PlateNameAlly  = new Color(0.92f, 0.96f, 0.92f, 1f);
    public static readonly Color PlateNameEnemy = new Color(1.00f, 0.76f, 0.72f, 1f);
"""),
# 2. Stale cross-reference: HealthBarRoot no longer owns a wither colour.
("""    // V2.2 (combat_ui §8): 2D HP-bar withered-max span; mirrors HealthBarRoot.WitherColor.
""",
"""    // V2.2 (combat_ui §8): withered-max span, on the 2D HP bar and the unit nameplate.
"""),
],
"Scripts/UI/CombatUI.cs": [
# 1. The glyph map goes: StatusCatalog is the one table now.
("""	// ── Status display map ───────────────────────────────────────────────
	private static readonly Dictionary<string, (string symbol, Color color)> StatusDisplay = new()
	{
		{ "burn",                 ("🔥", new Color(1.0f,  0.45f, 0.1f))  },
		{ "frozen",               ("❄",  new Color(0.4f,  0.8f,  1.0f))  },
		{ "poisoned",             ("☠",  new Color(0.5f,  0.9f,  0.2f))  },
		{ "stunned",              ("★",  new Color(1.0f,  0.95f, 0.3f))  },
		{ "rooted",               ("⊕",  new Color(0.55f, 0.85f, 0.3f))  },
		{ "slowed",               ("↓",  new Color(0.6f,  0.6f,  0.9f))  },
		{ "haunted",              ("✦",  new Color(0.7f,  0.4f,  1.0f))  },
		{ "bound",                ("⛓",  new Color(0.75f, 0.65f, 0.4f))  },
		{ "arcane_mark",          ("◈",  new Color(0.4f,  0.7f,  1.0f))  },
		{ "chaining",             ("⚡",  new Color(0.9f,  0.85f, 0.2f))  },
		{ "vigil",                ("👁",  new Color(0.85f, 0.85f, 1.0f))  },
		{ "undying_turn",         ("↺",  new Color(0.9f,  0.7f,  0.3f))  },
		{ "undying_full_restore", ("✙",  new Color(0.9f,  0.7f,  0.3f))  },
	};

""",
""),
# 2. Chips wrap, so the field becomes a flow container.
("""	private HBoxContainer _statusIconRow;
""",
"""	private HFlowContainer _statusIconRow;
"""),
("""		_statusIconRow = new HBoxContainer { Name = "StatusIcons" };
		_statusIconRow.AddThemeConstantOverride("separation", 4);
		_statusIconRow.Alignment = BoxContainer.AlignmentMode.Center;
""",
"""		_statusIconRow = new HFlowContainer { Name = "StatusIcons" };
		_statusIconRow.AddThemeConstantOverride("h_separation", 4);
		_statusIconRow.AddThemeConstantOverride("v_separation", 3);
		_statusIconRow.Alignment = FlowContainer.AlignmentMode.Center;
"""),
# 3. Pass the unit, so unit-level conditions (stagger, reaction, veil...) show too.
("""		RefreshStatusIcons(unit.Stats.StatusEffects);
""",
"""		RefreshStatusIcons(unit);
"""),
# 4. Labelled, coloured, tooltipped chips for every condition.
("""	private void RefreshStatusIcons(Dictionary<string, int> statuses)
	{
		ClearStatusIcons();
		if (statuses == null || statuses.Count == 0)
			return;

		foreach (var kvp in statuses)
		{
			if (kvp.Value <= 0)
				continue;
			if (!StatusDisplay.TryGetValue(kvp.Key, out var d))
				continue;

			var lbl = new Label { Name = $"SI_{kvp.Key}", Text = d.symbol, Modulate = d.color };
			lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSizeNormal);
			lbl.TooltipText = kvp.Key;
			_statusIconRow.AddChild(lbl);
		}
	}
""",
"""	/// <summary>One chip per condition (StatusCatalog.Collect): full name and
	/// count, family colour, and the rules text as the tooltip. Replaces the
	/// symbol row, which only knew 13 of the game's statuses.</summary>
	private void RefreshStatusIcons(Unit unit)
	{
		ClearStatusIcons();
		if (_statusIconRow == null || unit == null)
			return;

		foreach (var c in StatusCatalog.Collect(unit))
		{
			Color tone = StatusCatalog.ToneColor(c.Tone);
			var box = new StyleBoxFlat { BgColor = tone.Darkened(0.62f), BorderColor = tone };
			box.SetBorderWidthAll(1);
			box.SetCornerRadiusAll(4);
			box.ContentMarginLeft = box.ContentMarginRight = 5;
			box.ContentMarginTop = box.ContentMarginBottom = 1;

			var chip = new PanelContainer
			{
				Name = $"SI_{c.Key}",
				TooltipText = $"{c.TooltipTitle()}\\n{c.Description}",
				MouseFilter = Control.MouseFilterEnum.Stop,
			};
			chip.AddThemeStyleboxOverride("panel", box);

			var lbl = new Label { Text = c.ChipText(true), MouseFilter = Control.MouseFilterEnum.Ignore };
			lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
			lbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
			chip.AddChild(lbl);
			_statusIconRow.AddChild(chip);
		}
	}
"""),
],
}

def main():
    out = {}
    for path, edits in PATCHES.items():
        with open(path, encoding="utf-8", newline="") as f:
            src = f.read()
        crlf = "\r\n" in src
        text = src.replace("\r\n", "\n")
        for i, (find, repl) in enumerate(edits, 1):
            n = text.count(find)
            if n != 1:
                print(f"ABORT: {path} hunk {i} matched {n} times (expected 1). Nothing written.")
                sys.exit(1)
            text = text.replace(find, repl)
        out[path] = text.replace("\n", "\r\n") if crlf else text
    for path, text in out.items():
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        print(f"patched {path}")

if __name__ == "__main__":
    main()
