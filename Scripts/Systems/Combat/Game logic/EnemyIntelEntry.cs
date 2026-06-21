using Godot;

// ============================================================
// EnemyIntelEntry.cs
//
// Purpose:        Tiny DTO passed from CombatManager to
//                 CombatUI.ShowEnemyIntel during the deployment
//                 phase, before enemies actually spawn. Lives in
//                 its own file to break a would-be circular dep
//                 between CombatManager and CombatUI.
// Layer:          Data
// Collaborators:  CombatManager.cs (producer),
//                 CombatUI.cs (consumer)
// See:            (none)
// ============================================================

/// <summary>Pre-spawn intel row shown in CombatUI's deployment-mode roster. Carries archetype label + display stats, no live Unit reference (the unit doesn't exist yet).</summary>
public struct EnemyIntelEntry
{
    public string ThreatLabel; // "Soldier", "Brute", "Defender", "Skirmisher"
    public int MaxHealth;
    public int BaseSpeed;
    public int Armor;
    public Color BodyColor;
}
