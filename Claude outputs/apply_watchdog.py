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

edit('Scripts/Systems/Register/RegisterManager.cs', [
(
'''        RegisterBarks.EnsureLoaded();
''',
'''        RegisterBarks.EnsureLoaded();
        HangWatchdog.Start();   // dev: freeze diagnostics (Scripts/Dev/HangWatchdog.cs)
'''),
(
'''    public override void _Process(double delta)
    {
        TrackScene();
''',
'''    public override void _Process(double delta)
    {
        HangWatchdog.Beat();
        TrackScene();
'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
(
'''        _isCardBeingDragged = true;
        var half = isTop ? cardUi.TopHalf : cardUi.BottomHalf;
        _draggedHalf = half;
        ShowTargetHighlight(half);
''',
'''        _isCardBeingDragged = true;
        var half = isTop ? cardUi.TopHalf : cardUi.BottomHalf;
        _draggedHalf = half;
        HangWatchdog.Crumb($"drag start {half?.Name} -> ShowTargetHighlight");
        ShowTargetHighlight(half);
        HangWatchdog.Crumb("drag start: ShowTargetHighlight done");
'''),
(
'''        var map = ComputePreviewDamage(_draggedHalf, tileData);
        ShowReactionPreview();
''',
'''        HangWatchdog.Crumb($"preview {_draggedHalf.Name} at {tile.Axial} occupant={(victim != null ? victim.Name : "none")}");
        var map = ComputePreviewDamage(_draggedHalf, tileData);
        HangWatchdog.Crumb("preview: sim done");
        ShowReactionPreview();
        HangWatchdog.Crumb("preview: reaction markers done");
'''),
(
'''    private bool RunPreviewEffect(IEffect effect, PredicateContext ctx)
    {
        switch (effect)
''',
'''    private bool RunPreviewEffect(IEffect effect, PredicateContext ctx)
    {
        HangWatchdog.Crumb($"  sim effect {effect?.GetType().Name ?? "null"}");
        switch (effect)
'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs', [
(
'''    public void RefreshThreatTiles()
    {
''',
'''    public void RefreshThreatTiles()
    {
        HangWatchdog.Crumb("RefreshThreatTiles");
'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.BoardForecast.cs', [
(
'''    public void RefreshGrowthPreview()
    {
        _growthPreviewQueued = false;
''',
'''    public void RefreshGrowthPreview()
    {
        _growthPreviewQueued = false;
        HangWatchdog.Crumb("RefreshGrowthPreview");
'''),
])
