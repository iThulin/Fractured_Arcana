using System.Collections.Generic;

// ============================================================
// CampusLandmarkData.cs
//
// Purpose:        Static registry of campus landmark locations
//                 and their narrative vignette encounters.
//                 Landmarks are authored locations on the campus
//                 hex grid (belfry, refectory, infirmary, etc.)
//                 that host narrative beats from the §1 campus
//                 restoration quest hooks. Each landmark has a
//                 flag-derived state (ruined/restored) and
//                 carries a NarrativeEncounterData for its
//                 current beat, resolved at display time from
//                 the player's flag state.
//
//                 Landmarks are NOT buildings — they occupy fixed
//                 hex positions and do not participate in the
//                 build/place/upgrade flow. They are the campus's
//                 story layer; buildings are the mechanical layer.
// Layer:          Data
// Collaborators:  CampusHexGrid.cs (rendering + click events),
//                 CampusScreen.cs (hosts NarrativeEncounterPanel),
//                 NarrativeEncounterData.cs (encounter schema),
//                 GuildSaveData.cs (flag reads for state)
// See:            quest_hooks_compendium_v1.md §1 (campus hooks),
//                 quest_hooks_compendium_v1.md §7 step 2
// ============================================================

/// <summary>
/// One campus landmark: a fixed hex-grid location with narrative content.
/// The <see cref="State"/> method derives the landmark's current phase from
/// the player's flags, and <see cref="GetEncounter"/> returns the matching
/// vignette encounter (or null if all beats are exhausted / restored).
/// </summary>
public class CampusLandmarkData
{
    /// <summary>Unique landmark id (e.g. "belfry", "refectory").</summary>
    public string Id = "";

    /// <summary>Display name shown on the hex grid and in the panel title.</summary>
    public string DisplayName = "";

    /// <summary>Short label drawn on the hex (2-3 chars, like building abbreviations).</summary>
    public string HexLabel = "";

    /// <summary>Axial Q coordinate on the campus hex grid.</summary>
    public int Q;

    /// <summary>Axial R coordinate on the campus hex grid.</summary>
    public int R;

    /// <summary>
    /// Ordered list of vignette beats. Each beat has a gate flag (empty = always
    /// available) and a completion flag (set by the encounter's SetFlags/SetMetaFlags).
    /// The first beat whose gate is met and whose completion flag is NOT set is the
    /// active beat. If all beats are completed, the landmark is in its restored state.
    /// </summary>
    public List<LandmarkBeat> Beats = new();

    /// <summary>Flag that, when set, means the landmark is fully restored. Typically
    /// the last beat's completion flag or a building-completion flag.</summary>
    public string RestoredFlag = "";

    // ── State resolution ────────────────────────────────────────────────

    public enum LandmarkState { Ruined, Active, Restored }

    /// <summary>Derive the landmark's current state from the player's flags.</summary>
    public LandmarkState State(System.Func<string, bool> hasFlag)
    {
        if (hasFlag != null && !string.IsNullOrEmpty(RestoredFlag) && hasFlag(RestoredFlag))
            return LandmarkState.Restored;

        // Find the first uncompleted beat whose gate is met.
        foreach (var beat in Beats)
        {
            bool gateOk = string.IsNullOrEmpty(beat.GateFlag) ||
                           (hasFlag != null && hasFlag(beat.GateFlag));
            bool done = !string.IsNullOrEmpty(beat.CompletionFlag) &&
                         hasFlag != null && hasFlag(beat.CompletionFlag);

            if (gateOk && !done)
                return LandmarkState.Active;
        }

        // All beats done but no restored flag? Still treat as restored.
        return LandmarkState.Restored;
    }

    /// <summary>Get the narrative encounter for the landmark's current active beat,
    /// or null if restored / no eligible beat.</summary>
    public NarrativeEncounterData GetEncounter(System.Func<string, bool> hasFlag)
    {
        foreach (var beat in Beats)
        {
            bool gateOk = string.IsNullOrEmpty(beat.GateFlag) ||
                           (hasFlag != null && hasFlag(beat.GateFlag));
            bool done = !string.IsNullOrEmpty(beat.CompletionFlag) &&
                         hasFlag != null && hasFlag(beat.CompletionFlag);

            if (gateOk && !done)
                return beat.Encounter;
        }
        return null;
    }
}

/// <summary>One narrative beat within a campus landmark's restoration arc.</summary>
public class LandmarkBeat
{
    /// <summary>Flag that must be set for this beat to be available (empty = always).
    /// Checked via GuildSaveData.HasFlag, so both WorldFlags and MetaNarrativeFlags
    /// are visible.</summary>
    public string GateFlag = "";

    /// <summary>Flag set by this beat's encounter choices on completion. When set,
    /// the beat is done and the next one becomes active.</summary>
    public string CompletionFlag = "";

    /// <summary>The narrative encounter shown when this beat is active.</summary>
    public NarrativeEncounterData Encounter;
}

/// <summary>
/// Static registry of all campus landmarks. Hardcoded for v1 — migrate to
/// JSON loading when the campus content stabilizes. Landmarks are keyed by
/// id and placed at authored axial coordinates on the radius-5 campus disc.
///
/// Coordinates are chosen to ring the campus grounds at hex-ring 3-4,
/// leaving the center (rings 0-2) for player-placed buildings. The six
/// hooks from §1 of the quest compendium each get a landmark; the
/// Remembrancer's Hall (1.7) is gated behind the Moment Eternal fragment.
/// </summary>
public static class CampusLandmarkRegistry
{
    private static List<CampusLandmarkData> _landmarks;

    public static List<CampusLandmarkData> All
    {
        get
        {
            if (_landmarks == null)
                _landmarks = Build();
            return _landmarks;
        }
    }

    public static CampusLandmarkData Get(string id)
    {
        foreach (var lm in All)
            if (lm.Id == id)
                return lm;
        return null;
    }

    // ════════════════════════════════════════════════════════════════════
    // Authored landmarks (quest hooks compendium §1)
    // ════════════════════════════════════════════════════════════════════

    private static List<CampusLandmarkData> Build()
    {
        return new List<CampusLandmarkData>
        {
            // ── 1.2 The Half-Rung Bell ──────────────────────────────────
            new CampusLandmarkData
            {
                Id = "belfry",
                DisplayName = "The Belfry",
                HexLabel = "BL",
                Q = 0, R = -4,  // top of campus
                RestoredFlag = "campus_belfry_restored",
                Beats = new List<LandmarkBeat>
                {
                    new LandmarkBeat
                    {
                        GateFlag = "",
                        CompletionFlag = "campus_belfry_b1",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_belfry_beat1",
                            Title = "The Half-Rung Bell",
                            Body = "The commencement bell hangs frozen mid-swing above you. " +
                                   "Its note stretches thin through the still air, more felt " +
                                   "than heard, a single sound that has lasted every timeline " +
                                   "you have lived. The belfry stairs are choked with leaked " +
                                   "dream-stuff that eats sound — your footsteps die on the " +
                                   "first step.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Climb the silent stair.",
                                    ResultText = "You climb in silence so complete it feels " +
                                                 "like drowning. The dream-stuff parts reluctantly, " +
                                                 "filling back in behind you. At the top, the bell " +
                                                 "is enormous and very still. Its clapper rests a " +
                                                 "finger's width from the rim, frozen in the moment " +
                                                 "before the note would have ended.",
                                    HPDelta = -8,
                                    SetFlags = new List<string> { "campus_belfry_b1" },
                                },
                                new EncounterChoice
                                {
                                    Label = "Not yet. Leave the belfry be.",
                                    ResultText = "You step back. The note hangs on, patient " +
                                                 "and tired.",
                                },
                            },
                        },
                    },
                    new LandmarkBeat
                    {
                        GateFlag = "campus_belfry_b1",
                        CompletionFlag = "campus_belfry_b2",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_belfry_beat2",
                            Title = "The Belfry — The Tuning Fork",
                            Body = "The bell waits at the top of the cleared stair. Its note " +
                                   "has changed since you climbed — thinner, as if your " +
                                   "passage drained something from the dream-stuff that was " +
                                   "feeding it. The founder's tuning fork, if you can find it " +
                                   "in the world, remembers the bell's true pitch.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Present the tuning fork.",
                                    ResultText = "The fork hums. The bell does not ring — not " +
                                                 "yet — but its clapper trembles, a motion so " +
                                                 "small it might be a wish. The pitch is right. " +
                                                 "When the belfry is restored, the bell will know " +
                                                 "its own voice.",
                                    RequiredFlag = "tuning_fork_recovered",
                                    SetFlags = new List<string> { "campus_belfry_b2" },
                                },
                                new EncounterChoice
                                {
                                    Label = "You haven't found the tuning fork yet.",
                                    ResultText = "The bell waits. It has been waiting a long time. " +
                                                 "It can wait longer.",
                                },
                            },
                        },
                    },
                },
            },

            // ── 1.1 The Refectory Lights ────────────────────────────────
            new CampusLandmarkData
            {
                Id = "refectory",
                DisplayName = "The Refectory",
                HexLabel = "RF",
                Q = 3, R = -4,  // northeast
                RestoredFlag = "campus_refectory_restored",
                Beats = new List<LandmarkBeat>
                {
                    new LandmarkBeat
                    {
                        GateFlag = "",
                        CompletionFlag = "campus_refectory_b1",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_refectory_beat1",
                            Title = "The Refectory Lights",
                            Body = "The graduation feast is still on the tables, still warm, " +
                                   "frozen mid-steam. Twenty-seven place settings. Three have " +
                                   "chairs pushed back — someone stood up quickly. The kitchen " +
                                   "door is ajar, and through it you can see the ovens, cold " +
                                   "now but with bread halfway risen inside them, the dough " +
                                   "as fresh as the moment it stopped being time.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Clear the frost from the hall.",
                                    ResultText = "You work for what feels like an hour, though " +
                                                 "nothing here marks time. The frost retreats " +
                                                 "from the surfaces you touch, and behind it the " +
                                                 "wood is warm — warm from a fire that went out " +
                                                 "in a timeline that never ended. The feast " +
                                                 "steams on, patient.",
                                    SetFlags = new List<string> { "campus_refectory_b1" },
                                },
                                new EncounterChoice
                                {
                                    Label = "Leave the feast undisturbed.",
                                    ResultText = "The steam rises and does not disperse.",
                                },
                            },
                        },
                    },
                    new LandmarkBeat
                    {
                        GateFlag = "campus_refectory_b1",
                        CompletionFlag = "campus_refectory_b2",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_refectory_beat2",
                            Title = "The Refectory — The Kitchen-Master's Ledger",
                            Body = "The cleared hall waits for its fire. The kitchen-master's " +
                                   "last entry is a supply order, dated the morning of the " +
                                   "Sundering: flour, salt, three barrels of wine for the " +
                                   "commencement. It was never delivered.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Deliver the ledger's last order.",
                                    ResultText = "You set the supplies in the kitchen as the " +
                                                 "ledger describes. The ovens do not light " +
                                                 "themselves — you light them, with your hands, " +
                                                 "in a room that has been waiting for hands " +
                                                 "since before you were born. The bread begins " +
                                                 "to rise again.",
                                    RequiredFlag = "kitchen_ledger_recovered",
                                    GoldDelta = -15,
                                    SetFlags = new List<string> { "campus_refectory_b2" },
                                },
                                new EncounterChoice
                                {
                                    Label = "You haven't found the supplies yet.",
                                    ResultText = "The ledger's ink is as fresh as the day it " +
                                                 "was written. It can wait for you.",
                                },
                            },
                        },
                    },
                },
            },

            // ── 1.4 The Interrupted Mending ─────────────────────────────
            new CampusLandmarkData
            {
                Id = "infirmary",
                DisplayName = "The Infirmary",
                HexLabel = "IF",
                Q = -3, R = 1,  // west
                RestoredFlag = "campus_infirmary_restored",
                Beats = new List<LandmarkBeat>
                {
                    new LandmarkBeat
                    {
                        GateFlag = "",
                        CompletionFlag = "campus_infirmary_b1",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_infirmary_beat1",
                            Title = "The Interrupted Mending",
                            Body = "In the infirmary, a healer stands frozen over a student, " +
                                   "hands mid-gesture, the mending spell half-woven between " +
                                   "them. The spell is still running — the only active magic " +
                                   "in the frozen campus — and it has been running for every " +
                                   "timeline you have lived. It is very tired.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Study the standing spell.",
                                    ResultText = "The weave is fraying at its edges, thin as " +
                                                 "breath. You trace its structure: a standard " +
                                                 "mending, nothing remarkable, cast by a " +
                                                 "competent healer on an ordinary wound. But " +
                                                 "ordinary magic was never meant to hold for " +
                                                 "this long. The spell has rewritten itself a " +
                                                 "thousand times to keep running, each revision " +
                                                 "a little more desperate, a little more precise. " +
                                                 "It is the most stubborn piece of magic you " +
                                                 "have ever seen.",
                                    SetFlags = new List<string> { "campus_infirmary_b1" },
                                    LoreId = "the_standing_spell",
                                },
                                new EncounterChoice
                                {
                                    Label = "Leave the spell to its work.",
                                    ResultText = "The weave pulses once, faintly, as if in " +
                                                 "acknowledgment. It has been alone a long time.",
                                },
                            },
                        },
                    },
                    new LandmarkBeat
                    {
                        GateFlag = "campus_infirmary_b1",
                        CompletionFlag = "campus_infirmary_b2",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_infirmary_beat2",
                            Title = "The Infirmary — What It Needs",
                            Body = "The mending spell is holding, but barely. Its structure " +
                                   "needs reagents it exhausted long ago — heartleaf and " +
                                   "stillwater moss, the kind that grows in the Witness's " +
                                   "territory.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Shore the mending with fresh reagents.",
                                    ResultText = "You feed the spell what it needs, and it " +
                                                 "drinks deep. The fraying edges tighten. The " +
                                                 "healer's frozen hands steady, as if the spell " +
                                                 "is remembering what confidence felt like. " +
                                                 "Through the weave, the student's wound is " +
                                                 "one stitch more closed than it was. One stitch.",
                                    RequiredFlag = "infirmary_reagents_recovered",
                                    SetFlags = new List<string> { "campus_infirmary_b2" },
                                },
                                new EncounterChoice
                                {
                                    Label = "You haven't gathered the reagents yet.",
                                    ResultText = "The spell holds on, patient and tired.",
                                },
                            },
                        },
                    },
                },
            },

            // ── 1.5 The Counter-Reading ─────────────────────────────────
            new CampusLandmarkData
            {
                Id = "observatory",
                DisplayName = "The Observatory",
                HexLabel = "OB",
                Q = 3, R = 1,   // east
                RestoredFlag = "campus_observatory_restored",
                Beats = new List<LandmarkBeat>
                {
                    new LandmarkBeat
                    {
                        GateFlag = "",
                        CompletionFlag = "campus_observatory_b1",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_observatory_beat1",
                            Title = "The Counter-Reading",
                            Body = "The observatory's great lens is aimed at the sky the " +
                                   "Astrologer reads. From inside the Long Second, the sky " +
                                   "doesn't move — which means, for the first time in " +
                                   "history, someone could read it slowly. The dome is " +
                                   "sealed shut, and the leaked dream-stuff here mimics " +
                                   "constellations, star-shaped and cold.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Unshutter the dome.",
                                    ResultText = "The dome resists. The dream-stuff constellations " +
                                                 "flare as you force the mechanism, casting cold " +
                                                 "light across the floor in patterns that almost " +
                                                 "mean something. The shutter groans open and the " +
                                                 "frozen sky fills the lens — the same sky " +
                                                 "Kassian reads, held still for you to study at " +
                                                 "your own pace. It is both beautiful and " +
                                                 "deeply unsettling.",
                                    HPDelta = -6,
                                    SetFlags = new List<string> { "campus_observatory_b1" },
                                    LoreId = "the_frozen_sky",
                                },
                                new EncounterChoice
                                {
                                    Label = "Leave the dome sealed.",
                                    ResultText = "The false constellations pulse on the floor, " +
                                                 "cold and patient.",
                                },
                            },
                        },
                    },
                    new LandmarkBeat
                    {
                        GateFlag = "campus_observatory_b1",
                        CompletionFlag = "campus_observatory_b2",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_observatory_beat2",
                            Title = "The Observatory — The Night-Ledgers",
                            Body = "The lens shows the frozen sky in exquisite detail, but " +
                                   "without the night-ledgers from the week of the Sundering, " +
                                   "you cannot bracket what Kassian saw. The records are in an " +
                                   "abandoned station somewhere in the field.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Install the night-ledgers and grind the counter-lens.",
                                    ResultText = "The ledgers slot into the observatory's " +
                                                 "calculation frames. Before and after — what " +
                                                 "the sky showed the week of the Sundering, and " +
                                                 "what it shows now, frozen in the same instant. " +
                                                 "The difference is the counter-reading: what " +
                                                 "Kassian sees that you don't, and what you see " +
                                                 "that he can't. The observatory hums to life.",
                                    RequiredFlag = "night_ledgers_recovered",
                                    SetFlags = new List<string> { "campus_observatory_b2" },
                                },
                                new EncounterChoice
                                {
                                    Label = "You haven't found the night-ledgers yet.",
                                    ResultText = "The lens waits, aimed at a sky that will " +
                                                 "not move until you understand it.",
                                },
                            },
                        },
                    },
                },
            },

            // ── 1.6 The Threshold Wards ─────────────────────────────────
            new CampusLandmarkData
            {
                Id = "gatehouse",
                DisplayName = "The Gatehouse",
                HexLabel = "GH",
                Q = 0, R = 4,   // bottom of campus
                RestoredFlag = "campus_gatehouse_restored",
                Beats = new List<LandmarkBeat>
                {
                    new LandmarkBeat
                    {
                        GateFlag = "",
                        CompletionFlag = "campus_gatehouse_b1",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_gatehouse_beat1",
                            Title = "The Threshold Wards",
                            Body = "The gatehouse wards were the first thing the " +
                                   "co-conspirator broke, and they broke them from inside " +
                                   "— the sigil-work is shattered in a pattern that only " +
                                   "makes sense read in reverse. The arch replays its " +
                                   "half-second of shattering in a slow, silent loop, " +
                                   "over and over, the only motion on campus besides " +
                                   "the mending spell.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Trace the breaking, stroke by stroke.",
                                    ResultText = "Read in reverse, the sigil-work tells a story. " +
                                                 "The breaker knew exactly where each ward " +
                                                 "anchored, exactly how much force each seal " +
                                                 "could hold. This was not destruction — it was " +
                                                 "surgery, performed with a school-signature " +
                                                 "you almost recognize. Someone trained here. " +
                                                 "Someone who sat at those twenty-seven places " +
                                                 "at the feast.",
                                    SetFlags = new List<string> { "campus_gatehouse_b1" },
                                    LoreId = "the_broken_wards",
                                },
                                new EncounterChoice
                                {
                                    Label = "Leave the wards for now.",
                                    ResultText = "The arch shatters again, silently. And again. " +
                                                 "And again.",
                                },
                            },
                        },
                    },
                    new LandmarkBeat
                    {
                        GateFlag = "campus_gatehouse_b1",
                        CompletionFlag = "campus_gatehouse_b2",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_gatehouse_beat2",
                            Title = "The Gatehouse — The Warding Primer",
                            Body = "You know how the wards were broken. To rebuild them, " +
                                   "you need a primer — the foundational text the breaker " +
                                   "studied from. It would be in their kingdom, in a library " +
                                   "or vault they once had access to.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Raise new wards from the primer.",
                                    ResultText = "The new wards settle into the gatehouse arch " +
                                                 "like a sentence finding its period. The loop " +
                                                 "of shattering stops. For the first time, the " +
                                                 "arch is simply an arch, still and whole, and " +
                                                 "the silence it holds is not the silence of " +
                                                 "something broken but the silence of something " +
                                                 "that no longer needs to scream.",
                                    RequiredFlag = "warding_primer_recovered",
                                    SetFlags = new List<string> { "campus_gatehouse_b2" },
                                },
                                new EncounterChoice
                                {
                                    Label = "You haven't recovered the primer yet.",
                                    ResultText = "The arch shatters. Again.",
                                },
                            },
                        },
                    },
                },
            },

            // ── 1.3 The Uncatalogued Wing ───────────────────────────────
            new CampusLandmarkData
            {
                Id = "library_wing",
                DisplayName = "The Uncatalogued Wing",
                HexLabel = "UW",
                Q = -3, R = -1, // northwest
                RestoredFlag = "campus_library_restored",
                Beats = new List<LandmarkBeat>
                {
                    new LandmarkBeat
                    {
                        GateFlag = "",
                        CompletionFlag = "campus_library_b1",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_library_beat1",
                            Title = "The Uncatalogued Wing",
                            Body = "The library froze during reshelving. Ten thousand books " +
                                   "hang in the air, mid-flight between hands and shelves, " +
                                   "suspended in the last breath of a morning that never " +
                                   "ended. The wing's index was being rewritten — nothing " +
                                   "frozen here is anywhere twice. Whatever is in this room " +
                                   "exists nowhere else in any timeline.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Walk the hanging stacks, carefully.",
                                    ResultText = "You move through the suspended library like " +
                                                 "a diver in still water. One book, brushed by " +
                                                 "your sleeve, drops from its frozen arc and " +
                                                 "ages to dust before it hits the floor — a " +
                                                 "text that existed for every timeline, gone in " +
                                                 "a breath of real time. You are more careful " +
                                                 "after that.",
                                    HPDelta = -4,
                                    SetFlags = new List<string> { "campus_library_b1" },
                                },
                                new EncounterChoice
                                {
                                    Label = "Too fragile. Leave the stacks alone.",
                                    ResultText = "The books hang on, patient as prayers.",
                                },
                            },
                        },
                    },
                    new LandmarkBeat
                    {
                        GateFlag = "campus_library_b1",
                        CompletionFlag = "campus_library_b2",
                        Encounter = new NarrativeEncounterData
                        {
                            Id = "campus_library_beat2",
                            Title = "The Uncatalogued Wing — The Accession List",
                            Body = "Deep in the hanging stacks, you found the under-librarian's " +
                                   "cart. On it, a day's accession list — three titles flagged " +
                                   "for priority retrieval. The request came from outside the " +
                                   "Academy, from someone with enough standing to pull books " +
                                   "from a university collection. The signature is Kassian " +
                                   "Vor-Aleth's.",
                            Choices = new List<EncounterChoice>
                            {
                                new EncounterChoice
                                {
                                    Label = "Read the three titles he requested.",
                                    ResultText = "Three books, pulled the week before the " +
                                                 "Sundering: a treatise on sympathetic anchoring, " +
                                                 "a primer on temporal paradox in sealed systems, " +
                                                 "and an unnamed manuscript described only as " +
                                                 "\"the one about endings.\" He was researching " +
                                                 "something. These three titles are the seed of " +
                                                 "that research, and they are now yours to follow.",
                                    SetFlags = new List<string> { "campus_library_b2" },
                                    LoreId = "the_accession_list",
                                },
                                new EncounterChoice
                                {
                                    Label = "Leave the list for now.",
                                    ResultText = "The cart stands where the under-librarian " +
                                                 "left it, halfway between two futures.",
                                },
                            },
                        },
                    },
                },
            },
        };
    }
}
