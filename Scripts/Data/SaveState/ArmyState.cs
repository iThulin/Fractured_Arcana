using System.Collections.Generic;

// ============================================================
// ArmyState.cs
//
// Purpose:        The guild's army (docs/provinces_v1_12_troops.md,
//                 slice PV7, P5, T1): the troop lines it has raised,
//                 each with where it was raised, its strength, the
//                 battles it has fought and its doctrine. The army
//                 marches with the castle and dies with the timeline.
//                 Plain data with safe defaults: a save from before PV7
//                 reads as an army of no lines.
// Layer:          Data
// Collaborators:  CycleState.cs (Army), ArmyMuster.cs (raising, upkeep,
//                 the record of a battle), TroopLines.cs (what a line's
//                 numbers come to), CombatManager.Troops.cs (the lines in
//                 battle), ProvinceSaveAssert.Army.cs (round trip)
// See:            docs/provinces_v1_12_troops.md section 4
// ============================================================

/// <summary>The guild's army: one per cycle, on CycleState.Army.</summary>
public class ArmyState
{
    /// <summary>The lines, oldest first. Upkeep is paid in this order.</summary>
    public List<TroopLineState> Lines = new();

    /// <summary>The number the next line raised takes ("line:1", "line:2", ...), so a line's
    /// id is never reused within a cycle.</summary>
    public int NextNumber = 1;
}

/// <summary>One troop line. Provinces are named by Id.</summary>
public class TroopLineState
{
    /// <summary>Stable id within the cycle: "line:3".</summary>
    public string Id = "";

    /// <summary>Which line it is (TroopLines.All): "pikes", "longbows", ...</summary>
    public string LineId = "";

    /// <summary>The province it was raised in; its name is the line's surname.</summary>
    public string ProvinceId = "";

    /// <summary>0 to 100: the share of its full numbers still standing. At 0 the line is gone.</summary>
    public int Strength = 100;

    /// <summary>Battles fought and lived through. Its rank is read from this (TroopLines.RankOf).</summary>
    public int Battles = 0;

    /// <summary>Its doctrine: an index into its line's doctrines.</summary>
    public int Doctrine = 0;

    /// <summary>The lunation it was raised in.</summary>
    public int RaisedLunation = 0;
}
