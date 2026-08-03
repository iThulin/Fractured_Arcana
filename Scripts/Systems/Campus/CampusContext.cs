using Godot;
using System;

// ============================================================
// CampusContext.cs
//
// Purpose:        The seam between CampusScreen and the campus
//                 panels being extracted out of it. Carries the
//                 four things a panel legitimately needs from
//                 the shell — and nothing else — so panels stop
//                 being methods on a 3,400-line god object and
//                 start being units that can be moved, tested,
//                 and hosted by something other than a tab.
// Layer:          UI
// Collaborators:  CampusScreen.cs (constructs exactly one),
//                 every extracted Campus*Panel / *View
// See:            docs/campus_tab_extraction_v1.md — Phase 1
// ============================================================

/// <summary>Everything a campus panel is allowed to reach for. One instance, built by
/// <c>CampusScreen.BuildUI</c> before the panels are, handed to each panel at build time.
///
/// The point is subtractive: a panel that takes a CampusContext cannot call
/// <c>RefreshSlotButtons</c>, cannot read <c>_selectedArmoryUnitId</c>, cannot touch
/// another tab's containers. Whatever it needs has to be on this class, which makes the
/// coupling countable instead of implicit. Adding a field here should feel expensive.
///
/// This exists so that when the 3D campus map replaces the tab bar, panels are already
/// independent of WHAT selected them. The tab bar and the map both end up calling the
/// same <c>Show(panelId)</c>; neither is baked into the panels.</summary>
public sealed class CampusContext
{
    /// <summary>The active save, read live rather than captured. Deliberately a property
    /// over <see cref="SaveManager.ActiveSave"/> and not a stored reference: the save
    /// object is REPLACED on slot switch and on <see cref="SaveManager.BeginNewCycle"/>,
    /// so a panel holding a captured reference would silently edit the previous
    /// timeline's data. Null before a slot is chosen — every consumer must null-check,
    /// exactly as the tab bodies already do.</summary>
    public GuildSaveData Save => SaveManager.ActiveSave;

    /// <summary>The campus-local toast host. Campus has its own rather than the
    /// expedition's (see session log 2026-07-21) — panels push quest/unlock toasts here.</summary>
    public ToastManager Toasts { get; }

    /// <summary>Open an encounter on the shared narrative overlay. The only genuinely
    /// cross-panel dependency measured in CampusScreen: Quests (companion arc stages) and
    /// Council (archmage resolutions) both drive it, as do landmark clicks on the campus
    /// map. It belongs to the shell, not to any one panel.
    ///
    /// Deliberately an action rather than the <c>NarrativeEncounterPanel</c> itself. Showing
    /// an encounter also means wiring its completion back to the shell's
    /// <c>OnCampusNarrativeCompleted</c> — the Snapshot-Mutate-Diff-Toast pass that writes
    /// flags, gold and meta-progression. A panel handed the raw panel would have to
    /// remember to attach that handler, and the failure mode is silent: the encounter
    /// renders, the player picks a choice, and nothing is persisted. Exposing only the
    /// verb makes that unrepresentable.
    ///
    /// Safe to call with a null encounter or before the overlay exists — the shell's
    /// implementation no-ops.</summary>
    public Action<NarrativeEncounterData> ShowNarrative { get; }

    /// <summary>Ask the shell to re-run its full refresh. Panels need this because
    /// building, recruiting, equipping and training all move gold and unlock state that
    /// OTHER panels display — <c>RefreshAll</c> already fans out to slots, companions,
    /// buildings, training, armory and gold for exactly that reason.
    ///
    /// A panel should call this only after a mutation it has already persisted. It is not
    /// a redraw-me hook; a panel redrawing itself calls its own Refresh.</summary>
    public Action RequestRefreshAll { get; }

    /// <summary>The CampusScreen node itself — the escape hatch, and the one member here
    /// that is a smell. Present because some panels legitimately leave the screen
    /// (<c>GetTree().ChangeSceneToFile</c> to the deck editor, card library, strategic
    /// map) or parent a full-screen picker over everything.
    ///
    /// Anything else reached through this is coupling that dodged the count above. If a
    /// panel needs shell state, add a named member to this class instead — the whole
    /// value of CampusContext is that its field list is the honest dependency list.</summary>
    public Node Host { get; }

    /// <summary>Repaint only the gold/materials readout. Narrower than
    /// <see cref="RequestRefreshAll"/> on purpose: the Scriptorium's scribe button changes
    /// gold and its own list and nothing else, and a full refresh there would rebuild eight
    /// panels and drop the player's scroll position mid-purchase.</summary>
    public Action RefreshGold { get; }

    /// <summary>Leave campus for the strategic map — generates the cycle's world first if
    /// it does not exist. The generation half (world, kingdoms, campaign, council, sim
    /// resets, echo seeding, roster rotation) is cycle LIFECYCLE and stays on the shell;
    /// a panel gets the verb, not the machinery.</summary>
    public Action EnterStrategicMap { get; }

    /// <summary>End the current timeline and start the next one on the given school:
    /// archive a LoopRecord, replace CycleState, reseed the starter deck, generate the new
    /// world, then open it. The single most consequential call available to a panel, which
    /// is exactly why it is one named verb rather than a panel reaching into SaveManager.
    ///
    /// Takes the school only — the caller frees its own picker UI.</summary>
    public Action<string> BeginNextCycle { get; }

    /// <summary>Seed everything a freshly loaded save is expected to already contain —
    /// companion roster, building list, starter armory. Idempotent. The Guild panel calls it
    /// after switching slots; the shell calls it on boot. Kept as one verb because splitting
    /// it is exactly how the empty-armory-on-new-guild bug happened.</summary>
    public Action EnsureSaveSeeded { get; }

    public CampusContext(Node host, ToastManager toasts,
                         Action<NarrativeEncounterData> showNarrative, Action requestRefreshAll,
                         Action refreshGold, Action enterStrategicMap, Action<string> beginNextCycle,
                         Action ensureSaveSeeded)
    {
        EnsureSaveSeeded = ensureSaveSeeded;
        Host = host;
        Toasts = toasts;
        ShowNarrative = showNarrative;
        RequestRefreshAll = requestRefreshAll;
        RefreshGold = refreshGold;
        EnterStrategicMap = enterStrategicMap;
        BeginNextCycle = beginNextCycle;
    }
}
