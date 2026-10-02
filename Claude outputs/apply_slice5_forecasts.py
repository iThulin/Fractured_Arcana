import sys, os
root = sys.argv[1]

def edit(rel, pairs):
    p = os.path.join(root, rel)
    s = open(p, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        assert n == 1, f"{rel}: expected 1 match, got {n} for:\n{old}"
        s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print("ok", rel)

# 1. Growth forecast (pure)
edit('Scripts/Systems/Legacy/GrowthManager.cs', [(
'''    // -- Riot burst (subscribe per Druid unit's OnRiotTriggered) ------
''',
'''    /// <summary>
    /// Pure forecast of the next <see cref="TickEndOfEnemyTurn"/> (class_identity_druid_v1
    /// §2a, "Preview"): fills <paramref name="sprouts"/> with the tiles that will seed a
    /// Sapling and <paramref name="advances"/> with the living tiles that will grow a
    /// stage. Mirrors the tick's own rules (age threshold, Cold regression, Hostile
    /// recession, Saplings spreading at Stirring, deterministic CollectSpread) and
    /// mutates nothing, so the preview is exactly what the tick will do if the board
    /// does not change first.
    /// </summary>
    public void PreviewNextTick(List<TileData> sprouts, List<TileData> advances)
    {
        sprouts.Clear();
        advances.Clear();
        if (_grid == null)
            return;

        var claimed = new List<(TileData, Unit)>();
        foreach (TileData tile in _grid.Tiles.Values)
        {
            if (tile.GrowthStage <= StageNone)
                continue;

            GrowthProfile p = GetProfile(tile);
            int stage = tile.GrowthStage;

            switch (p.Affinity)
            {
                case "Hostile":
                    continue;   // it burns or recedes; it never spreads

                case "Cold":
                    if (stage > p.MaxStage)
                        stage = Mathf.Max(p.MaxStage, StageNone);
                    if (stage > StageNone && tile.GrowthAge - 1 < -_config.AdvanceAgeThreshold)
                        stage--;
                    break;

                default:
                    if (tile.GrowthAge + 1 >= _config.AdvanceAgeThreshold && stage < p.MaxStage)
                    {
                        stage++;
                        advances.Add(tile);
                    }
                    break;
            }

            if (stage <= StageNone)
                continue;
            if (stage >= StageThicket || (stage == StageSapling && SaplingsSpread(tile.GrowthOwner)))
                CollectSpread(tile, claimed);
        }

        foreach (var (t, _) in claimed)
            sprouts.Add(t);
    }

    // -- Riot burst (subscribe per Druid unit's OnRiotTriggered) ------
''')])

# 2. Tile markers for the growth forecast
edit('Scripts/Systems/Combat/Terrain/HexTile.cs', [
(
'''    private Label3D _growthLabel;
''',
'''    private Label3D _growthLabel;
    private Label3D _growthPreviewLabel;     // next growth tick forecast (druid §2a)
'''),
(
'''    private void UpdateGrowthLabel(int stage)
''',
'''    /// <summary>
    /// Next-tick growth forecast (class_identity_druid_v1 §2a): 1 = a faint hollow
    /// sprout where a Sapling will seed, 2 = a faint "+" above the pip where living
    /// ground will grow a stage, 0 = clear. Same green as the growth pip, at low alpha,
    /// so it reads as "about to be" rather than "is".
    /// </summary>
    public void SetGrowthPreview(int mode)
    {
        if (mode <= 0)
        {
            if (_growthPreviewLabel != null)
                _growthPreviewLabel.Visible = false;
            return;
        }

        if (_growthPreviewLabel == null)
        {
            _growthPreviewLabel = new Label3D
            {
                Name = "GrowthPreview",
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
            };
            CallDeferred("add_child", _growthPreviewLabel);
        }

        Color tint = UITheme.GrowthPip;
        tint.A = 0.45f;
        _growthPreviewLabel.Text = mode == 1 ? "○" : "+";
        _growthPreviewLabel.FontSize = mode == 1 ? 30 : 28;
        _growthPreviewLabel.Position = new Vector3(0f, mode == 1 ? 0.7f : 1.0f, 0f);
        _growthPreviewLabel.Modulate = tint;
        _growthPreviewLabel.Visible = true;
    }

    private void UpdateGrowthLabel(int stage)
'''),
])

# 3. Combat wiring
edit('Scripts/Systems/Combat/Core/CombatManager.cs', [(
'''        State.Growth.OnGrowthChanged += tile => tile.TileView?.SetGrowth(tile.GrowthStage);
''',
'''        State.Growth.OnGrowthChanged += tile =>
        {
            tile.TileView?.SetGrowth(tile.GrowthStage);
            QueueGrowthPreview();   // BoardForecast: one repaint per frame, however many tiles changed
        };
''')])

edit('Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs', [
(
'''        string markers = ShowDebugIntentMarkers ? BuildIntentMarkers(enemy) : "";
        string body = string.IsNullOrEmpty(markers)
            ? $"{glyph} {value}{suffix}"
            : $"{glyph} {value}{suffix}\\n{markers}";

        // The marker line is reference text, not a glyph, so shrink it and two lines
        // don't swallow the board.
        enemy.SetIntentDisplay(body, color, string.IsNullOrEmpty(markers) ? 40 : 24);
''',
'''        string markers = ShowDebugIntentMarkers ? BuildIntentMarkers(enemy) : "";
        string body = string.IsNullOrEmpty(markers)
            ? $"{glyph} {value}{suffix}"
            : $"{glyph} {value}{suffix}\\n{markers}";

        // class_identity_elementalist_v1 §2a item 7: an intent that will set off a
        // reaction names it under the glyph (plain letters: the Label3D font has no
        // symbol glyphs for reactions).
        var reaction = PredictIntentReaction(intent);
        if (reaction != ElementReaction.None)
            body += $"\\nFORMS {ElementReactions.DisplayName(reaction).ToUpperInvariant()}";

        // The marker line is reference text, not a glyph, so shrink it and two lines
        // don't swallow the board.
        int size = !string.IsNullOrEmpty(markers) ? 24 : reaction != ElementReaction.None ? 30 : 40;
        enemy.SetIntentDisplay(body, color, size);
'''),
(
'''                SpawnThreatMarker(view, kvp.Key, kvp.Value);
                _paintedThreatTiles.Add(kvp.Key);
            }
        }
    }
''',
'''                SpawnThreatMarker(view, kvp.Key, kvp.Value);
                _paintedThreatTiles.Add(kvp.Key);
            }
        }

        // BoardForecast: the board just changed, so both forecasts may have too.
        RefreshIntentReactions();
        QueueGrowthPreview();
    }
'''),
])

# 4. Reaction chart entry points
edit('Scripts/UI/PauseMenu.cs', [(
'''        _resumeButton.Pressed += OnResumePressed;
''',
'''        // Reaction chart (class_identity_elementalist_v1 §2a item 4). Duplicated from
        // the Card Library button BEFORE any signal is wired, so it carries the
        // scene's styling and none of that button's handlers.
        var chartButton = (Button)_cardLibraryButton.Duplicate();
        chartButton.Name = "ReactionChartButton";
        chartButton.Text = "Reaction Chart";
        var menuColumn = _cardLibraryButton.GetParent();
        menuColumn.AddChild(chartButton);
        menuColumn.MoveChild(chartButton, _cardLibraryButton.GetIndex() + 1);
        chartButton.Pressed += () => ReactionChart.ShowDialog(this);

        _resumeButton.Pressed += OnResumePressed;
''')])

edit('Scripts/UI/CardLibraryUi.cs', [(
'''        if (_detailContent == null) GD.PrintErr("[CardLibrary] DetailContent not found");
    }
''',
'''        if (_detailContent == null) GD.PrintErr("[CardLibrary] DetailContent not found");

        // Reaction chart (class_identity_elementalist_v1 §2a item 4), at the end of the top bar.
        if (_backButton?.GetParent() is HBoxContainer topBar)
        {
            var chart = new Button { Text = "Reaction Chart", FocusMode = FocusModeEnum.None };
            UITheme.ApplyButtonStyle(chart, isPrimary: false);
            chart.Pressed += () => ReactionChart.ShowDialog(this);
            topBar.AddChild(chart);
        }
    }
''')])
