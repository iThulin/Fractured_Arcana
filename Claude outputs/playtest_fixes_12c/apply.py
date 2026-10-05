import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text()
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:70], s.count(o))
        s = s.replace(o, n)
    p.write_text(s)

# 1. Swap: PlaceOnTile refuses an occupied tile, so vacate both first.
edit('Scripts/Cards/Effects/EnchanterEffects.cs', [
 ('''		// Clear both first so neither placement finds its destination occupied.
		a.PlaceOnTile(tb, MovementKind.Teleport);''',
  '''		// Vacate both first: PlaceOnTile refuses an occupied tile, so without this
		// the first placement silently failed and nothing moved.
		ta.ClearOccupant(a);
		tb.ClearOccupant(b);
		a.PlaceOnTile(tb, MovementKind.Teleport);'''),
 ('''		s.Log($"[SummonIllusion] {made} mirror cop{(made == 1 ? "y" : "ies")} of {owner.Name} ({Hp} HP).");''',
  '''		s.Log($"[SummonIllusion] {made} mirror cop{(made == 1 ? "y" : "ies")} of {owner.Name} ({Hp} HP).");
		// Intents are locked at the start of the turn; let enemies that chose the
		// caster pick again now that the copies stand.
		if (made > 0)
			s.OnIllusionsSummoned?.Invoke(owner);'''),
])

edit('Scripts/Systems/GameStateManager.cs', [
 ('''    public Func<string, TileData, int, Unit> OnSummonRequested;''',
  '''    public Func<string, TileData, int, Unit> OnSummonRequested;
    /// <summary>Maze of Mirrors: copies of this unit were just summoned. CombatManager
    /// re-plans the intents of enemies that had chosen it.</summary>
    public Action<Unit> OnIllusionsSummoned;'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.Enchanter.cs', [
 ('''        Names.NameBroken += OnNameBroken;
        _enchanterHooksInstalled = true;''',
  '''        Names.NameBroken += OnNameBroken;
        if (State != null)
            State.OnIllusionsSummoned = ReplanIntentsAgainst;
        _enchanterHooksInstalled = true;'''),
 ('''        Names.NameBroken -= OnNameBroken;
        _enchanterHooksInstalled = false;''',
  '''        Names.NameBroken -= OnNameBroken;
        if (State != null)
            State.OnIllusionsSummoned = null;
        _enchanterHooksInstalled = false;'''),
 ('''    /// <summary>Maze of Mirrors: while copies of <paramref name="picked"/> stand, an enemy''',
  '''    /// <summary>Maze of Mirrors: every enemy whose locked intent chose <paramref name="owner"/>
    /// plans again, so it can pick a copy this turn instead of next. Planning is safe to
    /// repeat (PlanAllEnemyIntents runs redundantly by design); the Defer/Advance timing
    /// and the reveal state are kept, because only the target changed.</summary>
    private void ReplanIntentsAgainst(Unit owner)
    {
        if (owner == null)
            return;
        bool any = false;
        foreach (var enemy in enemyUnits)
        {
            if (!IsValidActor(enemy) || enemy.CurrentIntent?.TargetUnit != owner)
                continue;
            bool revealed = enemy.CurrentIntent.Revealed;
            var plan = PlanIntent(enemy);
            if (plan == null)
                continue;
            plan.Revealed = revealed || enemy.IntentPermanentlyRevealed;
            enemy.CurrentIntent = plan;
            UpdateIntentDisplay(enemy);
            any = true;
            GD.Print($"[Mirror] {enemy.Name} re-aims at {plan.TargetUnit?.Name ?? "a tile"}.");
        }
        if (any)
            RefreshThreatTiles();
    }

    /// <summary>Maze of Mirrors: while copies of <paramref name="picked"/> stand, an enemy'''),
])

# 3. Trackpad: pinch and two-finger scroll zoom (a Mac trackpad sends gestures, not wheel events).
edit('Scripts/UI/CameraController.cs', [
 ('''        if (@event is InputEventMouseMotion motion)
            _mouseDelta = motion.Relative;''',
  '''        // macOS trackpad: pinch zooms, and a two-finger vertical scroll zooms like the
        // wheel. A Mac trackpad sends gesture events, never WheelUp/WheelDown.
        if (@event is InputEventMagnifyGesture mag && mag.Factor > 0.01f)
            _zoomTarget = Mathf.Clamp(_zoomTarget / mag.Factor, MinZoom, _maxZoomDynamic);
        else if (@event is InputEventPanGesture pan && !pan.AltPressed)
            _zoomTarget = Mathf.Clamp(_zoomTarget + pan.Delta.Y * ZoomSpeed * TrackpadScrollZoom,
                                      MinZoom, _maxZoomDynamic);

        if (@event is InputEventMouseMotion motion)
            _mouseDelta = motion.Relative;'''),
 ('''    [Export] public float ZoomLerpSpeed = 8f;''',
  '''    [Export] public float ZoomLerpSpeed = 8f;
    /// <summary>Zoom per unit of two-finger scroll, as a fraction of ZoomSpeed. Negate to flip the direction.</summary>
    [Export] public float TrackpadScrollZoom = 0.35f;'''),
])

# 4. Deck editor: one text size, and a right column that does not jump.
edit('Scripts/UI/DeckEditorUi.cs', [
 ('    private const float RightColumnWidth = 300f;', '    private const float RightColumnWidth = 580f;'),
 ('        name.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);',
  '        name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);'),
 ('''        var classLbl = new Label { Text = school.ToString() };
        classLbl.CustomMinimumSize = new Vector2(150, 0);''',
  '''        // Stash rows are compact: the right column has a fixed width, and the full-width
        // active-deck layout (150 + 340 + 340 px of minimums) used to force it to ~950 px
        // the moment any card sat in the stash. The school still shows as the border.
        bool compact = !isActive;
        var classLbl = new Label { Text = school.ToString(), Visible = !compact };
        classLbl.CustomMinimumSize = new Vector2(compact ? 0 : 150, 0);'''),
 ('''        topBlock.CustomMinimumSize = new Vector2(340, 0);
        topBlock.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;''',
  '''        topBlock.CustomMinimumSize = new Vector2(compact ? 0 : 340, 0);
        topBlock.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;'''),
 ('''        topLbl.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        topLbl.CustomMinimumSize = new Vector2(150, 0);''',
  '''        topLbl.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;
        topLbl.CustomMinimumSize = new Vector2(compact ? 60 : 150, 0);'''),
 ('''            botBlock.CustomMinimumSize = new Vector2(340, 0);
            botBlock.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;''',
  '''            botBlock.CustomMinimumSize = new Vector2(compact ? 0 : 340, 0);
            botBlock.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;'''),
 ('''            botLbl.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            botLbl.CustomMinimumSize = new Vector2(110, 0);''',
  '''            botLbl.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;
            botLbl.CustomMinimumSize = new Vector2(compact ? 60 : 110, 0);'''),
])
print("ok")
