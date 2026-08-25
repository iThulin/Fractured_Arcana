using Godot;

// ============================================================
// CampusPanel.cs
//
// Purpose:        Base class for every campus tab body lifted out
//                 of CampusScreen. Fixes the build/refresh contract
//                 so the shell can host nine panels through one
//                 loop, and so the 3D campus map can later select
//                 them the same way the tab bar does.
// Layer:          UI
// Collaborators:  CampusScreen.cs (owns one instance per panel),
//                 CampusContext.cs (the only shell state a panel
//                 may reach), CampusUi.cs (widget factory)
// See:            docs/campus_tab_extraction_v1.md, Phase 2
// ============================================================

/// <summary>One campus tab body: builds its widgets into a host <see cref="ScrollContainer"/>
/// once, then re-renders on demand.
///
/// <para><b>Not a Node.</b> A panel is a plain object that holds references to the Godot
/// nodes it built into the container it was handed, exactly what the tab methods on
/// CampusScreen already did with their private container fields. Making these Nodes would
/// add scene-tree lifecycle (<c>_Ready</c> ordering, <c>_ExitTree</c> cleanup, reparenting
/// on show/hide) to an extraction whose entire value is that it changes no behaviour.</para>
///
/// <para><b>One pattern for all nine</b>, including the ones whose state is only a couple of
/// container references. An earlier draft of the extraction plan split these into "stateless
/// static <c>BuildInto</c>" (following <see cref="QuestLogView"/>) and "stateful class". That
/// split was wrong: QuestLogView is static because it has TWO hosts (the campus tab and the
/// global QuestLogScreen overlay) and must not hold either one's state. Records, Companions
/// and the rest have one host each, so the precedent does not transfer, and a uniform shape
/// is what lets the shell reduce to a single <c>Show(panelId)</c>.</para>
///
/// <para>A panel reaches the shell ONLY through <see cref="Ctx"/>. If it needs something not
/// on <see cref="CampusContext"/>, add it there rather than routing around: that field list
/// is the honest dependency count.</para></summary>
public abstract class CampusPanel
{
    /// <summary>The shell seam. Null until <see cref="Build"/> runs.</summary>
    protected CampusContext Ctx { get; private set; }

    /// <summary>Populate <paramref name="scroll"/> with this panel's widgets. Called once,
    /// from CampusScreen.BuildUI. Build-time only. Do NOT read save data here: on a cold
    /// boot the tabs are built before a slot is chosen, which is why every tab body today
    /// builds empty containers and fills them in its refresh.</summary>
    public void Build(ScrollContainer scroll, CampusContext ctx)
    {
        Ctx = ctx;
        OnBuild(scroll);
    }

    /// <summary>Subclass hook for <see cref="Build"/>.</summary>
    protected abstract void OnBuild(ScrollContainer scroll);

    /// <summary>Re-render from current save state. Must tolerate being called before
    /// <see cref="Build"/> (the shell refreshes on paths that can precede a full build) and
    /// with <c>Ctx.Save == null</c> (no slot selected yet). Every existing tab body already
    /// guards on its container being null; keep that guard when moving one in.</summary>
    public abstract void Refresh();
}
