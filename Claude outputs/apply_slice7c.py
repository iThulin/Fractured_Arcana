#!/usr/bin/env python3
# Slice 7c: aim arcs and Almanac view never touch the scene during a drag; arcs built once,
# keyed by tile (not by per-frame world position), so they redraw only on real changes.
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

A = 'Scripts/Systems/Combat/Core/CombatManager.AimLines.cs'
edit(A, [
('''//                 One persistent MeshInstance3D (ImmediateMesh), rebuilt
//                 only when a cheap per-frame signature changes; nothing
//                 is created or freed per change (RegisterManager's
//                 lifetime rule).''',
'''//                 One persistent MeshInstance3D (ImmediateMesh), created
//                 once at combat start (never from _Process), rebuilt only
//                 when an aimed intent, its enemy's TILE, or its visibility
//                 changes (not its animated world position), and never
//                 while a card or anything else is being dragged: the
//                 drag hang of 2026-10-02 came back with these arcs
//                 (2026-10-03, slice 7c), so the scene is left alone for
//                 the whole drag and catches up when it ends.'''),
('''    private void EnsureAimLines()
    {
        if (_aimLines != null && IsInstanceValid(_aimLines))
            return;''',
'''    /// <summary>Builds the arc node. Called once from _Ready (InstallAnchorHooks'
    /// neighbour), never from _Process.</summary>
    private void EnsureAimLines()
    {
        if (_aimLines != null && IsInstanceValid(_aimLines))
            return;'''),
('''        if (grid == null || enemyUnits == null)
            return;
''',
'''        if (grid == null || enemyUnits == null || _aimLines == null || !IsInstanceValid(_aimLines))
            return;
        if (DragPayloadManager.IsDragging || GetViewport().GuiIsDragging())
            return;   // never touch the scene mid-drag; the signature catches up after
'''),
('''            Vector3 from = enemy.GlobalPosition + new Vector3(0f, AimStartLift, 0f);''',
'''            var fromView = grid.GetTileView(enemy.CurrentTile.Axial);
            if (fromView == null)
                continue;
            Vector3 from = fromView.GlobalPosition + new Vector3(0f, AimStartLift, 0f);'''),
('''            sig.Append(enemy.GetInstanceId()).Append(':').Append(from.ToString("F2")).Append('>')''',
'''            sig.Append(enemy.GetInstanceId()).Append(':').Append(enemy.CurrentTile.Axial).Append('>')'''),
('''        EnsureAimLines();
        _aimMesh.ClearSurfaces();''',
'''        _aimMesh.ClearSurfaces();'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.AlmanacView.cs', [
('''        var entries = State?.Almanac;
        var sig = new StringBuilder();''',
'''        if (DragPayloadManager.IsDragging || GetViewport().GuiIsDragging())
            return;   // lifetime rule: no scene edits during a drag (see AimLines)
        var entries = State?.Almanac;
        var sig = new StringBuilder();'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
('''        InstallAnchorHooks(); // Temporal Anchor lethal save
''',
'''        InstallAnchorHooks(); // Temporal Anchor lethal save
        EnsureAimLines();     // ranged-intent arcs: the node is built here, once
'''),
])
print("slice 7c applied")
