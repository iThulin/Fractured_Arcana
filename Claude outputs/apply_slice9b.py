#!/usr/bin/env python3
# Slice 9b: crash fix (threat reticles pooled, never freed between a deferred add and
# the add itself) and the debug launcher round trip (deck/upgrade/library screens and
# finished debug fights come back to the launcher with its settings; Launch from the
# deck editor).
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

# ── 1. Crash: pooled threat reticles ──────────────────────────────────
edit('Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs', [
('''        foreach (var marker in _threatMarkers.Values)
        {
            if (marker != null && IsInstanceValid(marker))
                marker.QueueFree();
        }
        _threatMarkers.Clear();''',
'''        // 2026-10-04 crash fix: markers used to be QueueFree'd here and re-created
        // with a deferred add_child. Two refreshes in one frame (Grand Design and
        // The Hour Comes Due defer or advance several enemies at once, each calling
        // RefreshThreatTiles) freed a marker whose deferred add_child was still
        // queued, and the queued call then read the freed node: EXC_BAD_ACCESS in
        // Node::add_child from CallQueue::flush. Markers are now pooled per tile:
        // created once, hidden and re-shown, never freed during the fight.
        foreach (var marker in _threatMarkers.Values)
        {
            if (marker != null && IsInstanceValid(marker))
                marker.Visible = false;
        }'''),
('''    private void SpawnThreatMarker(HexTile view, Vector2I coord, bool revealed)
    {
        var marker = new Label3D
        {''',
'''    private void SpawnThreatMarker(HexTile view, Vector2I coord, bool revealed)
    {
        if (_threatMarkers.TryGetValue(coord, out var pooled) && pooled != null && IsInstanceValid(pooled))
        {
            pooled.Modulate = revealed ? UITheme.TileThreatReticle : UITheme.TileThreatReticleDim;
            pooled.Visible = true;
            return;
        }
        var marker = new Label3D
        {'''),
])

# ── 2. Launcher round trip ────────────────────────────────────────────
L = 'Scripts/Dev/CombatDebugLauncher.cs'
edit(L, [
('''    private static CombatDebugLauncher _instance;
    public static bool IsOpen => _instance != null && IsInstanceValid(_instance);
''',
'''    private static CombatDebugLauncher _instance;
    public static bool IsOpen => _instance != null && IsInstanceValid(_instance);

    // ── Round trip (2026-10-04) ─────────────────────────────────────────
    // The deck editor, upgrade screen and card library used to dump the player at
    // the campus, and a finished debug fight did too, so every iteration meant
    // re-entering the guild hall, reopening the launcher and re-entering every
    // setting. Now the form is snapshotted when the launcher hands off, those
    // screens' Back buttons (and the end of a debug fight) come back to the campus
    // with the launcher reopened and the snapshot restored, and the deck editor
    // gets a Launch button that goes straight back into the fight.
    /// <summary>A launcher sub-screen is open: its Back returns here, not to campus.</summary>
    public static bool ReturnPending;
    private static List<(char kind, double value)> _savedForm;
    private static bool _autoLaunch;
    private VBoxContainer _form;
'''),
('''        var form = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        form.AddThemeConstantOverride("separation", 8);
        sm.AddChild(form);''',
'''        var form = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        form.AddThemeConstantOverride("separation", 8);
        sm.AddChild(form);
        _form = form;'''),
('''        var close = new Button { Text = "Close  [Esc]", CustomMinimumSize = new Vector2(120, 38) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += Close;
        btnRow.AddChild(close);
    }
''',
'''        var close = new Button { Text = "Close  [Esc]", CustomMinimumSize = new Vector2(120, 38) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += Close;
        btnRow.AddChild(close);

        RestoreForm();
        if (_autoLaunch)
        {
            _autoLaunch = false;
            CallDeferred(nameof(OnLaunch));
        }
    }

    // ── Form snapshot ───────────────────────────────────────────────────
    /// <summary>Every dropdown, spin box and checkbox under the form, in build order.
    /// The form is rebuilt from the same data each time, so order is a stable key.</summary>
    private static void CollectInputs(Node n, List<Control> into)
    {
        foreach (var child in n.GetChildren())
        {
            switch (child)
            {
                case OptionButton ob: into.Add(ob); break;
                case SpinBox sb: into.Add(sb); break;
                case CheckBox cb: into.Add(cb); break;
                default: CollectInputs(child, into); break;
            }
        }
    }

    private void SaveForm()
    {
        if (_form == null || !IsInstanceValid(_form))
            return;
        var inputs = new List<Control>();
        CollectInputs(_form, inputs);
        _savedForm = new List<(char, double)>();
        foreach (var c in inputs)
        {
            _savedForm.Add(c switch
            {
                OptionButton ob => ('o', ob.Selected),
                SpinBox sb => ('s', sb.Value),
                CheckBox cb => ('c', cb.ButtonPressed ? 1 : 0),
                _ => ('?', 0),
            });
        }
    }

    private void RestoreForm()
    {
        if (_savedForm == null || _form == null)
            return;
        var inputs = new List<Control>();
        CollectInputs(_form, inputs);
        int n = Math.Min(inputs.Count, _savedForm.Count);
        for (int i = 0; i < n; i++)
        {
            var (kind, value) = _savedForm[i];
            switch (inputs[i])
            {
                case OptionButton ob when kind == 'o':
                    if (value >= 0 && value < ob.ItemCount)
                    {
                        ob.Selected = (int)value;
                        if (ob == _patternRegionOpt)
                            RebuildPatternDropdown((int)value);   // its pattern list follows the region
                    }
                    break;
                case SpinBox sb when kind == 's':
                    sb.Value = value;
                    break;
                case CheckBox cb when kind == 'c':
                    cb.ButtonPressed = value > 0.5;
                    break;
                default:
                    GD.Print($"[DebugLauncher] Saved settings no longer match the form at input {i}; the rest keep their defaults.");
                    return;
            }
        }
    }

    /// <summary>Leave for a launcher sub-screen (deck editor, upgrades, library).</summary>
    private void OpenSubscreen(string scenePath)
    {
        SaveForm();
        ReturnPending = true;
        GetTree().ChangeSceneToFile(scenePath);
    }

    /// <summary>Called by a sub-screen's Back (and the deck editor's Launch). True when
    /// the screen was opened from the launcher and the return is handled here.</summary>
    public static bool TryReturnToLauncher(SceneTree tree, bool launch = false)
    {
        if (!ReturnPending || tree == null)
            return false;
        ReturnPending = false;
        _autoLaunch = launch;
        tree.ChangeSceneToFile(CampusScene);
        ReopenWhenCampusReady(tree);
        return true;
    }

    /// <summary>Waits for the campus scene to be current and ready (a SceneTransition
    /// fade takes a while), then reopens the launcher over it.</summary>
    private static async void ReopenWhenCampusReady(SceneTree tree)
    {
        for (int frame = 0; frame < 900; frame++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            var scene = tree.CurrentScene;
            if (scene != null && IsInstanceValid(scene) && scene.SceneFilePath == CampusScene && scene.IsNodeReady())
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                if (!IsOpen && IsInstanceValid(scene))
                    Toggle(scene);
                return;
            }
        }
        GD.Print("[DebugLauncher] The campus never became current; the launcher was not reopened.");
    }
'''),
('''    private void OnLaunch()
    {''',
'''    private void OnLaunch()
    {
        SaveForm();   // a finished debug fight reopens the launcher with these settings'''),
('''        b.Pressed += () => GetTree().ChangeSceneToFile(scenePath);
        return b;''',
'''        b.Pressed += () => OpenSubscreen(scenePath);
        return b;'''),
('''            PlayerDeckSave.UseDebugDeck = true;
            GetTree().ChangeSceneToFile("res://Scenes/UI/DeckEditor.tscn");''',
'''            PlayerDeckSave.UseDebugDeck = true;
            OpenSubscreen("res://Scenes/UI/DeckEditor.tscn");'''),
('''        SceneTransition.Go(ctx?.GetTree(), CampusScene, "Debug fight over", "Returning to the hub.");
    }''',
'''        var tree = ctx?.GetTree();
        SceneTransition.Go(tree, CampusScene, "Debug fight over", "Returning to the hub.");
        if (tree != null && _savedForm != null)
            ReopenWhenCampusReady(tree);   // straight back to the launcher, settings kept
    }'''),
])

edit('Scripts/UI/DeckEditorUi.cs', [
('''        backBtn.Pressed += () => GetTree().ChangeSceneToFile(ReturnScenePath);
        topBar.AddChild(backBtn);''',
'''        backBtn.Pressed += () =>
        {
            if (!CombatDebugLauncher.TryReturnToLauncher(GetTree()))
                GetTree().ChangeSceneToFile(ReturnScenePath);
        };
        topBar.AddChild(backBtn);

        // Opened from the combat debug launcher: go straight back into the fight.
        if (CombatDebugLauncher.ReturnPending)
        {
            backBtn.Text = "← Launcher";
            var launchBtn = new Button { Text = "Launch Fight ▶", CustomMinimumSize = new Vector2(150, 36) };
            launchBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            launchBtn.SetAnchorsPreset(LayoutPreset.CenterRight);
            launchBtn.OffsetLeft = -166; launchBtn.OffsetRight = -16;
            launchBtn.OffsetTop = -18; launchBtn.OffsetBottom = 18;
            UITheme.ApplyButtonStyle(launchBtn, isPrimary: true);
            launchBtn.Pressed += () => CombatDebugLauncher.TryReturnToLauncher(GetTree(), launch: true);
            topBar.AddChild(launchBtn);
        }'''),
])

edit('Scripts/Systems/Campus/CardUpgradeScreen.cs', [
('''        back.Pressed += () => GetTree().ChangeSceneToFile(ReturnScenePath);''',
'''        back.Pressed += () =>
        {
            if (!CombatDebugLauncher.TryReturnToLauncher(GetTree()))
                GetTree().ChangeSceneToFile(ReturnScenePath);
        };'''),
])

edit('Scripts/UI/CardLibraryUi.cs', [
('''        GetTree().ChangeSceneToFile(ReturnScenePath);
    }
}''',
'''        if (CombatDebugLauncher.TryReturnToLauncher(GetTree()))
            return;
        GetTree().ChangeSceneToFile(ReturnScenePath);
    }
}'''),
])
print("slice 9b applied")
