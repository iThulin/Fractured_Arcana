using Godot;
using System.Collections.Generic;

// ============================================================
// TooltipManager.cs
//
// Purpose:        Floating 2D tooltip panel. Autoload singleton.
//                 Any system calls Show/Hide to surface contextual
//                 info near the mouse. Currently used by HexTile
//                 (tile info) and CardUi (requires badges).
// Layer:          UI
// Collaborators:  HexTile.cs (tile hover), TileData.cs (data source),
//                 UITheme.cs (all colors/styles)
// ============================================================

/// <summary>
/// Autoload singleton tooltip panel. Follows the mouse and displays
/// contextual information. Call <see cref="ShowTileTooltip"/> from
/// HexTile hover events; call <see cref="HideTileTooltip"/> on mouse exit.
/// </summary>
public partial class TooltipManager : Control
{
    public static TooltipManager Instance { get; private set; }

    private const int OffsetX = 16;
    private const int OffsetY = -8;

    private Panel _panel;
    private VBoxContainer _content;
    private MarginContainer _tooltipRoot;

    public override void _Ready()
    {
        Instance = this;
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorRight = 1f;
        AnchorBottom = 1f;

        GD.Print($"[TooltipManager] Ready. Instance set: {Instance != null}");

        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        var canvas = new CanvasLayer();
        canvas.Layer = 100;
        AddChild(canvas);

        // Full-rect root so Position math is in screen space
        var root = new Control();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = MouseFilterEnum.Ignore;
        canvas.AddChild(root);

        // MarginContainer is the positioned root; it sizes to content
        var margin = new MarginContainer();
        margin.MouseFilter = MouseFilterEnum.Ignore;
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        root.AddChild(margin);
        _tooltipRoot = margin;

        // Panel paints the background behind the content
        _panel = new Panel();
        _panel.MouseFilter = MouseFilterEnum.Ignore;
        _panel.ShowBehindParent = true;
        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.06f, 0.06f, 0.10f, 0.97f);
        style.BorderColor = UITheme.Violet;
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(UITheme.CornerRadius);
        style.ShadowColor = new Color(0f, 0f, 0f, 0.8f);
        style.ShadowSize = 4;
        _panel.AddThemeStyleboxOverride("panel", style);
        margin.AddChild(_panel);

        var pad = new MarginContainer();
        pad.MouseFilter = MouseFilterEnum.Ignore;
        pad.AddThemeConstantOverride("margin_left", 10);
        pad.AddThemeConstantOverride("margin_right", 10);
        pad.AddThemeConstantOverride("margin_top", 6);
        pad.AddThemeConstantOverride("margin_bottom", 6);
        margin.AddChild(pad);

        _content = new VBoxContainer();
        _content.MouseFilter = MouseFilterEnum.Ignore;
        _content.AddThemeConstantOverride("separation", 3);
        pad.AddChild(_content);

        _tooltipRoot.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_tooltipRoot == null || !_tooltipRoot.Visible) return;
        if (DragPayloadManager.IsDragging) { _tooltipRoot.Visible = false; return; }

        _tooltipRoot.ResetSize();

        var mouse = GetViewport().GetMousePosition();
        var vp = GetViewport().GetVisibleRect().Size;
        var size = _tooltipRoot.Size;

        float x = mouse.X + OffsetX;
        float y = mouse.Y + OffsetY - size.Y;

        if (x + size.X > vp.X) x = mouse.X - size.X - OffsetX;
        if (y < 0) y = mouse.Y + OffsetX;

        _tooltipRoot.Position = new Vector2(x, y);
    }

    // ── Public API ───────────────────────────────────────────────

    public void ShowTileTooltip(TileData tile)
    {
        if (_tooltipRoot == null || tile == null) return;

        RebuildContent();

        // Title: terrain type
        AddTitle(TerrainDisplayName(tile.TerrainType));

        // Element imbuement
        if (tile.ElementType != TileElementType.None)
            AddColoredRow("Imbued:", tile.ElementType.ToString(), ElementColorForType(tile.ElementType));

        // Element reaction (class_identity_elementalist_v1 §2a item 3): name, rounds, rule.
        if (tile.Reaction != ElementReaction.None)
        {
            string rounds = tile.ReactionRounds < 0 ? "permanent"
                          : tile.ReactionRounds == 1 ? "1 round left"
                          : $"{tile.ReactionRounds} rounds left";
            AddColoredRow($"{ElementReactions.DisplayName(tile.Reaction)}:", rounds, ElementColors.Reaction(tile.Reaction));
            AddRow(ElementReactions.Describe(tile.Reaction), "");
        }

        // A prepared glyph. The sigil itself is decoration (an easter egg cipher), so this
        // is the player's real read of what is on the tile and how it goes off.
        AddGlyphSection(tile.Glyph);

        // Height
        if (tile.Height != 0)
            AddRow("Height:", tile.Height > 0 ? $"+{tile.Height}" : tile.Height.ToString());

        // Walkable / blocked
        if (tile.IsBlocked)
            AddColoredRow("Blocked", "", UITheme.Danger);
        else if (!tile.IsWalkable)
            AddColoredRow("Impassable", "", UITheme.Warning);

        // Hazard
        if (tile.IsHazardous)
            AddColoredRow("Hazardous:", "damages units", UITheme.Warning);

        // Modifier tag (rubble, scorched, etc.)
        if (!string.IsNullOrEmpty(tile.TerrainModifier))
            AddRow("State:", tile.TerrainModifier);

        // Card requires hints
        var satisfiedRequires = GetSatisfiedRequires(tile);
        if (satisfiedRequires.Count > 0)
        {
            AddSeparator();
            AddSubtitle("Satisfies:");
            foreach (var req in satisfiedRequires)
                AddColoredRow("✓", RequiresDisplayName(req), UITheme.Success);
        }

        _tooltipRoot.Visible = true;
    }

    public void HideTileTooltip()
    {
        if (_tooltipRoot != null) _tooltipRoot.Visible = false;
    }

    // ── Glyph section ────────────────────────────────────────────

    private const int PlayerTeam = 0;
    private const float GlyphTextWidth = 260f;
    private static readonly Color GlyphAccent = new Color(0.62f, 0.82f, 1.00f, 1f);
    private static readonly Color GlyphHostileAccent = new Color(1.00f, 0.56f, 0.84f, 1f);

    private void AddGlyphSection(GlyphData g)
    {
        if (g == null || g.Consumed)
            return;
        bool mine = g.OwnerTeam == PlayerTeam;
        // Team 1 is the enemy side. Anything else (map events plant team 2) belongs to
        // nobody and goes off under everyone.
        bool hazard = g.OwnerTeam != PlayerTeam && g.OwnerTeam != EnemyTeam;
        if (g.Invisible && !mine)
            return;                              // a hidden enemy glyph stays hidden

        string owner = g.Owner?.Name ?? g.OwnerId ?? "";
        string name = string.IsNullOrEmpty(g.SourceName) ? "Glyph" : g.SourceName;

        AddSeparator();
        AddPairRow(name, g.AffectsEnemies ? GlyphHostileAccent : GlyphAccent,
                   mine ? "yours" : hazard ? "hazard" : "enemy",
                   mine ? UITheme.Success : hazard ? UITheme.Warning : UITheme.Danger);
        if (!string.IsNullOrEmpty(owner) && !mine && !hazard)
            AddRow("Set by:", owner);

        AddColoredRow("Triggers:", "", GlyphAccent);
        string trig = !string.IsNullOrEmpty(g.GlyphTriggerText) ? g.GlyphTriggerText
                    : hazard ? HazardTriggerText(g)
                    : TriggerText(g, mine, owner);
        AddWrapped(trig, UITheme.TextPrimary);

        // Authored glyph text (CardHalf.GlyphText) is the primary read. Without it, fall
        // back to lines built from the glyph's fields, then to the filtered card text.
        if (!string.IsNullOrEmpty(g.GlyphText))
        {
            AddWrapped(FillGlyphTokens(g.GlyphText, g, owner), UITheme.TextPrimary);
            foreach (var line in DynamicLines(g))
                AddWrapped(line, UITheme.TextPrimary);
        }
        else
        {
            var payload = PayloadLines(g, owner);
            foreach (var line in payload)
                AddWrapped(line, UITheme.TextPrimary);
            if (payload.Count == 0)
            {
                string rest = GlyphEffectText(g.SourceRulesText);
                if (!string.IsNullOrEmpty(rest))
                    AddWrapped(rest, UITheme.TextPrimary);
            }
        }

        string life = g.DurationTurns < 0 ? "Until triggered"
                    : g.DurationTurns >= PermanentTurns ? "Permanent"
                    : g.DurationTurns == 1 ? "1 turn left"
                    : $"{g.DurationTurns} turns left";
        AddRow(g.Reusable ? "Reusable" : "Single use", life);
    }

    private const int EnemyTeam = 1;

    /// <summary>Card data writes "duration": 99 for a glyph that lasts the fight.</summary>
    private const int PermanentTurns = 90;

    private static string HazardTriggerText(GlyphData g) => g.Trigger switch
    {
        GlyphTrigger.StartOfTurn => "When any unit starts its turn on this tile.",
        _ => "When any unit steps onto this tile.",
    };

    /// <summary>State a glyph picks up after it is cast (Glyph Network links, cascade
    /// copies), which no authored text can know about.</summary>
    private static List<string> DynamicLines(GlyphData g)
    {
        var lines = new List<string>();
        if (g.CascadeSpread > 0)
            lines.Add($"Then copies itself onto {g.CascadeSpread} adjacent tile{(g.CascadeSpread == 1 ? "" : "s")}.");
        if (g.LinkId != 0)
            lines.Add(g.CumulativeBonus > 0
                ? $"Linked: fires with its network, +{g.CumulativeBonus} damage per glyph that fires."
                : "Linked: fires with its network.");
        return lines;
    }

    /// <summary>
    /// Fills the authored glyph text from the live glyph, so the tooltip number is the one
    /// that will land (spell damage bonuses, Grand Design) and a number-only upgrade needs
    /// no new text. Unknown tokens are left as written, so a typo shows up in play.
    /// </summary>
    private static string FillGlyphTokens(string text, GlyphData g, string owner)
    {
        static string Plural(int n, string one, string many) => n == 1 ? $"{n} {one}" : $"{n} {many}";
        int sd = g.StatusDuration;
        int rc = g.ReflectCharges > 0 ? g.ReflectCharges : sd;
        string nextHits = rc >= 99 ? "every attack or spell"
                        : rc == 1 ? "the next attack or spell"
                        : $"the next {rc} attacks or spells";
        return text
            .Replace("{damage}", g.EffectiveDamage(g.GameState).ToString())
            .Replace("{status_turns}", Plural(sd, "turn", "turns"))
            .Replace("{armor}", g.AllyArmor.ToString())
            .Replace("{shield}", g.AllyShield.ToString())
            .Replace("{ally_damage}", g.AllyDamage.ToString())
            .Replace("{ally_mana}", g.AllyMana.ToString())
            .Replace("{draw_cards}", Plural(g.OwnerDraw, "card", "cards"))
            .Replace("{owner_mana}", g.OwnerMana.ToString())
            .Replace("{weave}", g.OwnerWeave.ToString())
            .Replace("{heal}", g.OwnerHeal.ToString())
            .Replace("{radius}", g.Radius.ToString())
            .Replace("{casts}", (g.AnchorCasts > 0 ? g.AnchorCasts : sd).ToString())
            .Replace("{next_spells}", rc == 1 ? "the next spell" : $"the next {rc} spells")
            .Replace("{next_hits}", nextHits)
            .Replace("{heal_turn}", g.AllyHealPerTurn.ToString())
            .Replace("{owner}", string.IsNullOrEmpty(owner) ? "its caster" : owner);
    }

    /// <summary>The rules text with the sentences about casting removed: where the glyph is
    /// prepared, how long it lasts and what the caster gains on cast. What is left is what
    /// the glyph itself does.</summary>
    private static string GlyphEffectText(string rules)
    {
        if (string.IsNullOrWhiteSpace(rules))
            return "";
        var kept = new List<string>();
        foreach (var raw in System.Text.RegularExpressions.Regex.Split(rules.Trim(), @"(?<=[.!?])\s+"))
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
    }

    private static string TriggerText(GlyphData g, bool mine, string owner)
    {
        string who = string.IsNullOrEmpty(owner) ? "its caster" : owner;
        switch (g.Trigger)
        {
            case GlyphTrigger.Enter:
                return mine ? "When an enemy steps onto this tile." : "When one of your units steps onto this tile.";
            case GlyphTrigger.StartOfTurn:
                return mine ? "When an enemy starts its turn on this tile." : "When one of your units starts its turn on this tile.";
            case GlyphTrigger.AllyEnter:
                return mine ? "When one of your units steps onto this tile." : "When an enemy steps onto this tile.";
            case GlyphTrigger.SpellCastNear:
                return g.Radius <= 0 ? "When a spell is cast on this tile."
                     : $"When any spell is cast within {g.Radius} tile{(g.Radius == 1 ? "" : "s")}.";
            case GlyphTrigger.SelfStand:
                return $"Works while {who} stands on it.";
            default:
                return "Only when a linked glyph fires.";
        }
    }

    private static List<string> PayloadLines(GlyphData g, string owner)
    {
        var lines = new List<string>();

        int dmg = g.EffectiveDamage(g.GameState);
        if (dmg > 0 && !string.IsNullOrEmpty(g.Status))
            lines.Add($"Deals {dmg} damage and applies {g.Status} for {g.StatusDuration} turn{(g.StatusDuration == 1 ? "" : "s")}.");
        else if (dmg > 0)
            lines.Add($"Deals {dmg} damage.");
        else if (!string.IsNullOrEmpty(g.Status))
            lines.Add($"Applies {g.Status} for {g.StatusDuration} turn{(g.StatusDuration == 1 ? "" : "s")}.");

        var ally = new List<string>();
        if (g.AllyArmor > 0) ally.Add($"+{g.AllyArmor} armor");
        if (g.AllyShield > 0) ally.Add($"+{g.AllyShield} shield");
        if (g.AllyDamage > 0) ally.Add($"+{g.AllyDamage} spell damage");
        if (g.AllyMana > 0) ally.Add($"+{g.AllyMana} mana");
        if (ally.Count > 0)
            lines.Add((g.Trigger == GlyphTrigger.SelfStand ? "Grants " : "Allies gain ") + string.Join(", ", ally) + ".");

        var pay = new List<string>();
        if (g.OwnerDraw > 0) pay.Add($"draw {g.OwnerDraw}");
        if (g.OwnerMana > 0) pay.Add($"+{g.OwnerMana} mana");
        if (g.OwnerWeave > 0) pay.Add($"+{g.OwnerWeave} Weave");
        if (g.OwnerHeal > 0) pay.Add($"heal {g.OwnerHeal}");
        if (pay.Count > 0)
            lines.Add($"{(string.IsNullOrEmpty(owner) ? "Caster" : owner)}: " + string.Join(", ", pay) + ".");

        if (g.CascadeSpread > 0)
            lines.Add($"Spreads a copy to {g.CascadeSpread} adjacent tile{(g.CascadeSpread == 1 ? "" : "s")} when it fires.");
        if (g.LinkId != 0)
            lines.Add(g.CumulativeBonus > 0
                ? $"Linked: fires with its network, +{g.CumulativeBonus} damage per glyph that fires."
                : "Linked: fires with its network.");

        if (lines.Count == 0 && g.OnTrigger != null)
            lines.Add("Effect: see the spell that placed it.");
        return lines;
    }

    private void AddPairRow(string key, Color keyColor, string value, Color valueColor)
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

    private void AddWrapped(string text, Color color)
    {
        var lbl = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(GlyphTextWidth, 0),
        };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        lbl.MouseFilter = MouseFilterEnum.Ignore;
        _content.AddChild(lbl);
    }

    // ── Content builders ─────────────────────────────────────────

    private void RebuildContent()
    {
        foreach (Node child in _content.GetChildren())
            child.QueueFree();
    }

    private void AddTitle(string text)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize + 1);
        lbl.AddThemeColorOverride("font_color", UITheme.Gold);
        lbl.MouseFilter = MouseFilterEnum.Ignore;
        _content.AddChild(lbl);
    }

    private void AddSubtitle(string text)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        lbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        lbl.MouseFilter = MouseFilterEnum.Ignore;
        _content.AddChild(lbl);
    }

    private void AddRow(string key, string value)
    {
        var hbox = new HBoxContainer();
        hbox.MouseFilter = MouseFilterEnum.Ignore;

        var keyLbl = new Label { Text = key };
        keyLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        keyLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        keyLbl.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(keyLbl);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        spacer.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(spacer);

        var valLbl = new Label { Text = value };
        valLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        valLbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        valLbl.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(valLbl);

        _content.AddChild(hbox);
    }

    private void AddColoredRow(string key, string value, Color valueColor)
    {
        var hbox = new HBoxContainer();
        hbox.MouseFilter = MouseFilterEnum.Ignore;

        var keyLbl = new Label { Text = key };
        keyLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        keyLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        keyLbl.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(keyLbl);

        if (!string.IsNullOrEmpty(value))
        {
            var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            spacer.MouseFilter = MouseFilterEnum.Ignore;
            hbox.AddChild(spacer);

            var valLbl = new Label { Text = value };
            valLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            valLbl.AddThemeColorOverride("font_color", valueColor);
            valLbl.MouseFilter = MouseFilterEnum.Ignore;
            hbox.AddChild(valLbl);
        }
        else
        {
            keyLbl.AddThemeColorOverride("font_color", valueColor);
        }

        _content.AddChild(hbox);
    }

    private void AddSeparator()
    {
        var sep = new HSeparator();
        sep.MouseFilter = MouseFilterEnum.Ignore;
        var sepStyle = new StyleBoxFlat { BgColor = UITheme.NeutralDim };
        sep.AddThemeStyleboxOverride("separator", sepStyle);
        _content.AddChild(sep);
    }

    // ── Data helpers ─────────────────────────────────────────────

    private static string TerrainDisplayName(TileTerrainType t) => t switch
    {
        TileTerrainType.Grass  => "Grassland",
        TileTerrainType.Water  => "Water",
        TileTerrainType.Lava   => "Lava",
        TileTerrainType.Forest => "Forest",
        TileTerrainType.Stone  => "Stone",
        TileTerrainType.Arcane => "Arcane Ground",
        TileTerrainType.Ice    => "Ice",
        TileTerrainType.Sand   => "Sand",
        _                      => t.ToString()
    };

    private static List<string> GetSatisfiedRequires(TileData tile)
    {
        var result = new List<string>();

        if (tile.TerrainType == TileTerrainType.Stone || tile.ElementType == TileElementType.Earth)
            result.Add("stone_tile");
        if (tile.ElementType == TileElementType.Fire)
            result.Add("fire_tile");
        if (tile.ElementType == TileElementType.Frost)
            result.Add("ice_tile");
        if (tile.ElementType == TileElementType.Lightning)
            result.Add("storm_tile");
        if (tile.Occupant == null && !tile.IsBlocked)
            result.Add("empty_tile");

        return result;
    }

    private static string RequiresDisplayName(string req) => req switch
    {
        "fire_tile"  => "fire_tile cards",
        "ice_tile"   => "ice_tile cards",
        "storm_tile" => "storm_tile cards",
        "stone_tile" => "stone_tile cards",
        "empty_tile" => "empty_tile cards",
        _            => req
    };

    private static Color ElementColorForType(TileElementType e) => e switch
    {
        TileElementType.Fire      => UITheme.ElementFire,
        TileElementType.Frost     => UITheme.ElementIce,
        TileElementType.Lightning => UITheme.ElementStorm,
        TileElementType.Earth     => UITheme.ElementEarth,
        TileElementType.Arcane    => UITheme.ArcaneBlue,
        TileElementType.Water     => UITheme.ArcaneBlue,
        TileElementType.Shadow    => UITheme.VioletDim,
        _                         => UITheme.Neutral
    };
}
