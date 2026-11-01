using Godot;
using System;

// ============================================================
// DirectionCues.cs
//
// Purpose:        The cue sheet of a direction scene (stage direction,
//                 docs/stage_direction_v1.md section 4): the node a
//                 sequence's method keys call. A key calls Cue(id) or
//                 CueFor(id, seconds), and the stage that loaded the
//                 direction performs the cue (StageDirection.Cued).
//
//                 Cues are for what only the code can do: the set's own
//                 beats, which depend on the save or are built at
//                 runtime, and the clips of a figure's model. Godot never
//                 runs method keys while a sequence is scrubbed in the
//                 editor, so cues show there as marks on the timeline and
//                 happen when the game plays them.
//
//                 The founding's cues (FoundingStage):
//                   cut_name        the guild's name over the doors, over its seconds
//                   register_down   the Register set down on the lectern, over its seconds
//                   roll_line       the Roll of Cycles' next line, over its seconds
//                   candidate:CLIP  the candidate's model plays CLIP, when it has it
// Layer:          Prologue / presentation
// Collaborators:  StageDirection (passes the cues on), FoundingStage
// ============================================================

/// <summary>The cue sheet a direction's method keys call (see the file header).</summary>
public partial class DirectionCues : Node
{
    /// <summary>A cue: its id, and its seconds (0 for Cue).</summary>
    public event Action<string, float> Cued;

    /// <summary>A cue with no length.</summary>
    public void Cue(string id) => Cued?.Invoke(id ?? "", 0f);

    /// <summary>A cue that takes <paramref name="seconds"/>.</summary>
    public void CueFor(string id, float seconds) => Cued?.Invoke(id ?? "", seconds);
}
