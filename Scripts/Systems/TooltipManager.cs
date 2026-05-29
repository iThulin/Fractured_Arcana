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

        // MarginContainer is the positioned root — it sizes to content
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

        _content = new VBoxContainer();
        _content.MouseFilter = MouseFilterEnum.Ignore;
        _content.AddThemeConstantOverride("separation", 3);
        margin.AddChild(_content);

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
