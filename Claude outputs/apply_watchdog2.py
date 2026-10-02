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

# CombatManager: bracket the frame and input handlers, the threat zone, inspect.
edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
(
'''    public override void _Process(double delta)
    {
        SyncMoveZoneDim();   // derived every frame, so no hover-event ordering can strand it
''',
'''    public override void _Process(double delta)
    {
        HangWatchdog.Crumb("CM._Process");
        ProcessFrame(delta);
        HangWatchdog.Crumb("CM._Process done");
    }

    private void ProcessFrame(double delta)
    {
        SyncMoveZoneDim();   // derived every frame, so no hover-event ordering can strand it
'''),
(
'''    public override void _UnhandledInput(InputEvent e)
    {
        if (isInDeploymentPhase)
''',
'''    public override void _UnhandledInput(InputEvent e)
    {
        HangWatchdog.Crumb($"CM.input {e.GetType().Name}");
        UnhandledInputBody(e);
        HangWatchdog.Crumb("CM.input done");
    }

    private void UnhandledInputBody(InputEvent e)
    {
        if (isInDeploymentPhase)
'''),
(
'''    private void ShowEnemyThreatZone(Unit enemy)
    {
''',
'''    private void ShowEnemyThreatZone(Unit enemy)
    {
        HangWatchdog.Crumb($"ThreatZone {enemy?.Name}");
'''),
])

edit('Scripts/Cards/CardDropHandler.cs', [(
'''    public override void _Process(double delta)
    {
        bool isDragging = DragPayloadManager.IsDragging;
''',
'''    public override void _Process(double delta)
    {
        HangWatchdog.Crumb("CardDrop._Process");
        bool isDragging = DragPayloadManager.IsDragging;
''')])

edit('Scripts/Systems/Combat/Presentation/CombatPresenter.cs', [(
'''        if (!_playing && _queue.Count > 0)
            _ = PlayQueueAsync();
    }
''',
'''        if (!_playing && _queue.Count > 0)
        {
            HangWatchdog.Crumb($"Presenter.play queue={_queue.Count}");
            _ = PlayQueueAsync();
        }
    }
''')])

edit('Scripts/Systems/Combat/Terrain/HexTile.cs', [(
'''        // ── Tooltip ──────────────────────────────────────────────
        if (Data != null)
            TooltipManager.Instance?.ShowTileTooltip(Data);
''',
'''        // ── Tooltip ──────────────────────────────────────────────
        HangWatchdog.Crumb($"tile hover {Axial}");
        if (Data != null)
            TooltipManager.Instance?.ShowTileTooltip(Data);
        HangWatchdog.Crumb("tile hover done");
''')])
