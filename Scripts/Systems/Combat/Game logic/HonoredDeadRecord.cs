using System;

// ============================================================
// HonoredDeadRecord.cs
//
// Purpose:        Data class representing a single honored dead
//                 spirit in the Ossuary. Contains all relevant 
//                 info for displaying the spirit and its 
//                 backstory, and for summoning it in combat when
//                 the player chooses to do so.
// Layer:          Combat
// Collaborators:  OssuaryUI.cs (displays the list of honored dead
//                 and their details),  HonoredDeadManager.cs 
//                 (manages the list of honored dead records and 
//                 their persistence), CombatManager.cs (summons 
//                 spirits in combat based on these records)
// See:            README §6.4 (Honored Dead and the Ossuary)
// ============================================================     


public class HonoredDeadRecord
{
    // Path to the mesh resource, e.g. "res://Assets/Player Asset/Player with hat.obj"
    // Loaded at runtime via GD.Load<Mesh>(MeshResourcePath)
    public string MeshResourcePath = "";

    // Display name shown briefly when this spirit is summoned
    public string Name = "";

    // Whether this was a player-side unit or an enemy
    public bool WasAlly = false;

    // School of the unit — used to tint the spirit's ethereal color
    public string School = "";

    // Companion id if this was a named companion — empty string otherwise
    public string CompanionId = "";

    // Run number when this unit died — used to sort chronologically
    public int RunNumber = 0;

    // Region name where they fell — flavor for the Ossuary display
    public string RegionName = "";

    // Whether this record has ever been used to summon a spirit
    // Cosmetic only — does not prevent re-use
    public bool HasBeenSummoned = false;
}