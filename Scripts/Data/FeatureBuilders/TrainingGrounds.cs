using System.Collections.Generic;

// ============================================================
// TrainingGrounds.cs
//
// Purpose:        The Training Grounds' doctrines (campus_building_
//                 upgrades_design_v1 §12). The old +1 damage (T2) and +4 HP
//                 (T3) are gone; the stance slots and the AP steps stay.
//                 T3 charters one of two doctrines at the Grand Hall:
//                   Levy Muster (wide)  a martial whose weapon teaches no
//                                       maneuvers drills Brace instead, and
//                                       garrisons raise militia after 2 moons
//                                       instead of 3.
//                   Proving Grounds     a martial may carry a second weapon
//                   (tall)              and change to it in a fight for 1 AP:
//                                       its class (so its maneuvers and Honed
//                                       rider) and its attack damage and
//                                       range. Stance finishers at 5 Edge
//                                       are the doctrine's second half, not
//                                       built yet.
// Layer:          Data / rules
// Collaborators:  Charters (the gate), UnitLoadout.SecondWeaponInstanceId,
//                 ArmoryData (Equip/UnequipSecondWeapon), EquipmentLoadout
//                 (resolves the carried weapon), CombatManager.Maneuvers
//                 (the tray and the swap), FieldPostings (militia),
//                 ForcesScreen (the carried-weapon card).
// ============================================================

/// <summary>Training Grounds doctrine rules. Stateless.</summary>
public static class TrainingGrounds
{
    public const string Id = "training_grounds";
    public const string LevyMuster = "levy_muster";
    public const string ProvingGrounds = "proving_grounds";

    /// <summary>The maneuver Levy Muster drills into a martial with none of its own.</summary>
    public const string LevyManeuverId = "brace";

    /// <summary>Moons a garrison holds before its militia offer, under Levy Muster.</summary>
    public const int LevyMilitiaMoons = 2;

    public static bool LevyMusterActive(GuildSaveData save) => Charters.IsActive(save, Id, LevyMuster);

    public static bool ProvingGroundsActive(GuildSaveData save) => Charters.IsActive(save, Id, ProvingGrounds);

    /// <summary>Moons of garrison before a site's militia asks to serve.</summary>
    public static int MilitiaMoons(GuildSaveData save)
        => LevyMusterActive(save) ? LevyMilitiaMoons : FieldPostings.MilitiaAfterMoons;

    /// <summary>The maneuvers a weapon class fields for this guild: the class's
    /// own, or, when it has none, Levy Muster's drilled Brace.</summary>
    public static List<ManeuverDefinition> ManeuversFor(GuildSaveData save, WeaponClass cls)
    {
        var own = ManeuverRegistry.ForWeaponClass(cls);
        if (own.Count > 0 || !LevyMusterActive(save))
        {
            return own;
        }
        var drill = ManeuverRegistry.Get(LevyManeuverId);
        return drill != null ? new List<ManeuverDefinition> { drill } : own;
    }

    /// <summary>True when this weapon class has no maneuvers of its own but the
    /// guild drills one into it (the tray says so).</summary>
    public static bool IsLevyDrill(GuildSaveData save, WeaponClass cls)
        => ManeuverRegistry.ForWeaponClass(cls).Count == 0 && LevyMusterActive(save);

    /// <summary>Why this companion cannot carry this item as a second weapon, or null.</summary>
    public static string CannotCarryReason(GuildSaveData save, Companion c, ItemInstance item)
    {
        if (!ProvingGroundsActive(save))
        {
            return "Needs the Proving Grounds chartered at the Grand Hall.";
        }
        if (c == null || (c.UnitClass != "Fighter" && c.UnitClass != "Ranger"))
        {
            return "Only martials carry a second weapon.";
        }
        if (item == null || item.Slot != "Weapon")
        {
            return "Not a weapon.";
        }
        if (item.UnitClass == "Wizard")
        {
            return "A wizard's focus.";
        }
        var armory = save?.Armory;
        if (armory != null && armory.GetLoadout(c.Id).WeaponInstanceId == item.InstanceId)
        {
            return "Already in hand.";
        }
        return null;
    }
}
