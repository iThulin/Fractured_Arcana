using Godot;
using System.Collections.Generic;

// ============================================================
// WorldMapScreen.cs
//
// Purpose:        Campus sub-screen showing the world map.
//                 Displays region tiles on a grid, colour-codes
//                 lock/unlock/explored state, shows per-region
//                 memory stats, and lets the player deploy to
//                 an accessible region.
// Layer:          UI
// Collaborators:  WorldMapDefinition.cs / WorldMapLoader.cs,
//                 RegionMemoryService.cs (stats per region),
//                 GuildSaveData.cs (UnlockedRegionIds, Gold),
//                 SaveManager.cs (writes CurrentRegionId),
//                 UITheme.cs, SchoolColors.cs
// ============================================================

/// <summary>
/// World map screen. Built in code, follows the same patterns as
/// CampusScreen tabs. Attach to a Control node that fills the campus
/// content area, or load as a standalone scene from the Expedition Hall.
/// </summary>
public partial class WorldMapScreen : Control
{
    // ── Layout constants ─────────────────────────────────────────────────
    private const int TileW = 180;
    private const int TileH = 110;
    private const int TileGapX = 20;
    private const int TileGapY = 20;
    private const int GridOriginX = 40;
    private const int GridOriginY = 60;

    // ── State ────────────────────────────────────────────────────────────
    private WorldMapDefinition _worldMap;
    private string _selectedRegionId = null;

    // ── UI nodes ─────────────────────────────────────────────────────────
    private Label _titleLabel;
    private Control _tileLayer;
    private Panel _detailPanel;
    private Label _detailName;
    private Label _detailDesc;
    private Label _detailStats;
    private Label _detailLock;
    private Button _deployButton;

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        _worldMap = WorldMapLoader.Load();

        AnchorRight = 1f;
        AnchorBottom = 1f;

        // ── Title ────────────────────────────────────────────────────────
        _titleLabel = new Label
        {
            Text = "World Map — Select a Region to Deploy",
            Position = new Vector2(GridOriginX, 16),
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
        _titleLabel.AddThemeColorOverride("font_color", UITheme.NarrativeTitleColor);
        AddChild(_titleLabel);

        // ── Tile layer ───────────────────────────────────────────────────
        _tileLayer = new Control
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
        };
        AddChild(_tileLayer);

        foreach (var node in _worldMap.Nodes)
            BuildRegionTile(node);

        // ── Detail panel (right side) ────────────────────────────────────
        _detailPanel = new Panel
        {
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Begin,
            OffsetLeft = -280,
            OffsetRight = 0,
            OffsetTop = 50,
            OffsetBottom = -20,
        };
        var detailStyle = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.45f),
            BorderColor = new Color(1f, 1f, 1f, 0.1f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        _detailPanel.AddThemeStyleboxOverride("panel", detailStyle);
        AddChild(_detailPanel);

        var detailMargin = new MarginContainer
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
        };
        detailMargin.AddThemeConstantOverride("margin_left", 16);
        detailMargin.AddThemeConstantOverride("margin_right", 16);
        detailMargin.AddThemeConstantOverride("margin_top", 16);
        detailMargin.AddThemeConstantOverride("margin_bottom", 16);
        _detailPanel.AddChild(detailMargin);

        var detailVBox = new VBoxContainer();
        detailVBox.AddThemeConstantOverride("separation", 10);
        detailMargin.AddChild(detailVBox);

        _detailName = MakeDetailLabel("", UITheme.NarrativeTitleColor, UITheme.NarrativeTitleFontSize);
        detailVBox.AddChild(_detailName);

        detailVBox.AddChild(new HSeparator());

        _detailDesc = MakeDetailLabel("", UITheme.NarrativeBodyColor, UITheme.NarrativeBodyFontSize);
        _detailDesc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        detailVBox.AddChild(_detailDesc);

        detailVBox.AddChild(new HSeparator());

        _detailStats = MakeDetailLabel("", UITheme.NarrativeBodyColor, UITheme.NarrativeBodyFontSize);
        _detailStats.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        detailVBox.AddChild(_detailStats);

        _detailLock = MakeDetailLabel("", new Color(1f, 0.4f, 0.4f), UITheme.NarrativeBodyFontSize);
        _detailLock.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        detailVBox.AddChild(_detailLock);

        // Push deploy button to the bottom
        detailVBox.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        _deployButton = new Button
        {
            Text = "Deploy",
            Visible = false,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _deployButton.AddThemeFontSizeOverride("font_size", UITheme.NarrativeBodyFontSize);
        _deployButton.AddThemeColorOverride("font_color", UITheme.POICombat);
        _deployButton.Pressed += OnDeployPressed;
        detailVBox.AddChild(_deployButton);

        // Start with no selection
        ClearDetail();
    }

    // ════════════════════════════════════════════════════════════════════
    // Tile construction
    // ════════════════════════════════════════════════════════════════════

    private void BuildRegionTile(RegionNode node)
    {
        bool unlocked = IsUnlocked(node);
        bool visited = RegionMemoryService.HasMemory(node.RegionId);
        bool isCurrent = SaveManager.ActiveSave?.CurrentRegionId == node.RegionId;

        float x = GridOriginX + node.Col * (TileW + TileGapX);
        float y = GridOriginY + node.Row * (TileH + TileGapY);

        // ── Tile panel ───────────────────────────────────────────────────
        var tile = new Panel
        {
            Position = new Vector2(x, y),
            CustomMinimumSize = new Vector2(TileW, TileH),
        };

        Color bgColor = unlocked
            ? (visited ? new Color(0.1f, 0.2f, 0.1f, 0.85f) : new Color(0.05f, 0.1f, 0.2f, 0.85f))
            : new Color(0.08f, 0.08f, 0.08f, 0.85f);
        Color borderColor = isCurrent
            ? UITheme.POIObjective
            : (unlocked ? new Color(0.4f, 0.4f, 0.5f) : new Color(0.2f, 0.2f, 0.2f));

        var tileStyle = new StyleBoxFlat
        {
            BgColor = bgColor,
            BorderColor = borderColor,
            BorderWidthTop = isCurrent ? 2 : 1,
            BorderWidthBottom = isCurrent ? 2 : 1,
            BorderWidthLeft = isCurrent ? 2 : 1,
            BorderWidthRight = isCurrent ? 2 : 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
        };
        tile.AddThemeStyleboxOverride("panel", tileStyle);

        // ── Tile content ─────────────────────────────────────────────────
        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        tile.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        margin.AddChild(vbox);

        // Region name
        var nameLabel = new Label
        {
            Text = unlocked ? node.DisplayName : "???",
            AutowrapMode = TextServer.AutowrapMode.Off,
        };
        nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize + 1);
        nameLabel.AddThemeColorOverride("font_color",
            unlocked ? Colors.White : new Color(0.4f, 0.4f, 0.4f));
        vbox.AddChild(nameLabel);

        // Atmosphere / flavor
        if (unlocked)
        {
            var flavorLabel = new Label
            {
                Text = node.TerrainFlavor,
                AutowrapMode = TextServer.AutowrapMode.Off,
            };
            flavorLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize - 1);
            flavorLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
            vbox.AddChild(flavorLabel);
        }

        // Exploration bar
        if (visited)
        {
            var (_, explored, cleared) = RegionMemoryService.GetStats(node.RegionId);

            var bar = new ProgressBar
            {
                Value = explored,
                CustomMinimumSize = new Vector2(0, 10),
                ShowPercentage = false,
            };
            vbox.AddChild(bar);

            if (cleared)
            {
                var clearedLabel = new Label { Text = "✓ Objective cleared" };
                clearedLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize - 1);
                clearedLabel.AddThemeColorOverride("font_color", UITheme.POIObjective);
                vbox.AddChild(clearedLabel);
            }
        }
        else if (unlocked)
        {
            var unvisitedLabel = new Label { Text = "Not yet explored" };
            unvisitedLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize - 1);
            unvisitedLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            vbox.AddChild(unvisitedLabel);
        }
        else
        {
            var lockLabel = new Label { Text = "🔒 Locked" };
            lockLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize - 1);
            lockLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.4f));
            vbox.AddChild(lockLabel);
        }

        // ── Click area ───────────────────────────────────────────────────
        var btn = new Button
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            Flat = true, // invisible — tile panel provides the visuals
        };
        string capturedId = node.RegionId;
        btn.Pressed += () => OnTileClicked(capturedId);
        tile.AddChild(btn);

        _tileLayer.AddChild(tile);
    }

    // ════════════════════════════════════════════════════════════════════
    // Interaction
    // ════════════════════════════════════════════════════════════════════

    private void OnTileClicked(string regionId)
    {
        _selectedRegionId = regionId;

        var node = _worldMap.Nodes.Find(n => n.RegionId == regionId);
        if (node == null) return;

        bool unlocked = IsUnlocked(node);
        var (visits, explored, cleared) = RegionMemoryService.GetStats(regionId);

        _detailName.Text = unlocked ? node.DisplayName : "Unknown Region";
        _detailDesc.Text = unlocked ? node.Description : "Explore adjacent regions to unlock this area.";

        if (visits > 0)
        {
            _detailStats.Text =
                $"Visits: {visits}\n" +
                $"Explored: {explored:F0}%\n" +
                $"Objective: {(cleared ? "Completed" : "Not yet reached")}";
        }
        else if (unlocked)
        {
            _detailStats.Text = "No expeditions recorded yet.";
        }
        else
        {
            _detailStats.Text = "";
        }

        if (!unlocked)
        {
            string reason = GetLockReason(node);
            _detailLock.Text = reason;
            _detailLock.Visible = true;
            _deployButton.Visible = false;
        }
        else
        {
            _detailLock.Visible = false;
            _deployButton.Visible = true;
            _deployButton.Text = node.DeploymentCost > 0
                ? $"Deploy  ({node.DeploymentCost}g)"
                : "Deploy";

            int gold = SaveManager.ActiveSave?.Gold ?? 0;
            _deployButton.Disabled = node.DeploymentCost > gold;
        }
    }

    private void OnDeployPressed()
    {
        if (_selectedRegionId == null) return;

        var node = _worldMap.Nodes.Find(n => n.RegionId == _selectedRegionId);
        if (node == null || !IsUnlocked(node)) return;

        var save = SaveManager.ActiveSave;
        if (save == null) return;

        // Deduct deployment cost
        if (node.DeploymentCost > 0)
        {
            if (save.Gold < node.DeploymentCost)
            {
                GD.Print("[WorldMap] Not enough gold to deploy.");
                return;
            }
            save.Gold -= node.DeploymentCost;
        }

        save.CurrentRegionId = _selectedRegionId;
        SaveManager.Save();

        GD.Print($"[WorldMap] Deploying to '{_selectedRegionId}'");
        GetTree().ChangeSceneToFile("res://Scenes/Overworld/OverworldScene.tscn");
    }

    private void ClearDetail()
    {
        _detailName.Text = "Select a region";
        _detailDesc.Text = "Click a tile on the map to see details and deploy.";
        _detailStats.Text = "";
        _detailLock.Visible = false;
        _deployButton.Visible = false;
    }

    // ════════════════════════════════════════════════════════════════════
    // Access logic
    // ════════════════════════════════════════════════════════════════════

    private bool IsUnlocked(RegionNode node)
    {
        if (node.UnlockedByDefault) return true;

        var save = SaveManager.ActiveSave;
        if (save == null) return false;

        // Check explicit unlock list on save (Phase 2: add UnlockedRegionIds field)
        // For now: unlock by checking if the required region has been cleared
        if (!string.IsNullOrEmpty(node.RequiresRegionCleared))
        {
            if (!save.RegionMemory.TryGetValue(node.RequiresRegionCleared, out var mem))
                return false;
            if (!mem.ObjectiveReached) return false;
        }

        return true;
    }

    private string GetLockReason(RegionNode node)
    {
        if (!string.IsNullOrEmpty(node.RequiresRegionCleared))
        {
            var req = _worldMap.Nodes.Find(n => n.RegionId == node.RequiresRegionCleared);
            string reqName = req?.DisplayName ?? node.RequiresRegionCleared;
            return $"Requires: complete objective in {reqName}";
        }
        return "Requirements not met.";
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    private static Label MakeDetailLabel(string text, Color color, int fontSize)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }
}
