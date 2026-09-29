using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.WeaponSwap.cs  (partial)
//
// Purpose:        The Proving Grounds' carried weapon (Training Grounds
//                 doctrine, campus_building_upgrades_design_v1 §12). A
//                 martial who mustered with a second weapon sees a
//                 "Change to <weapon>" card in the technique tray; for
//                 1 AP it swaps the unit's weapon class (so its maneuvers
//                 and Honed rider) and the two weapons' printed attack
//                 damage and range. Swapping back undoes it exactly.
//                 Enchants, drawbacks and item passives stay with the
//                 weapon the unit mustered with (ResolvedLoadout notes).
//                 Edge is kept, as a stance switch keeps it.
// Layer:          Combat rules (partial of CombatManager)
// Collaborators:  ResolvedLoadout (HasSecondWeapon and deltas),
//                 ApplyEquipmentLoadout (registers the carry),
//                 CombatManager.Maneuvers (tray card + press routing),
//                 MartialAPCosts.
// ============================================================

public partial class CombatManager
{
    /// <summary>Tray card id for the swap (never a maneuver id).</summary>
    private const string SwapWeaponCardId = "swap:weapon";

    /// <summary>AP to change weapons mid-fight.</summary>
    private const int SwapWeaponAP = 1;

    /// <summary>What a unit is NOT holding right now, and the stat deltas that
    /// taking it up applies. The deltas negate on every swap.</summary>
    private sealed class CarriedWeapon
    {
        public WeaponClass OtherClass;
        public string OtherName = "";
        public string HeldName = "";
        public int AttackDamageDelta;
        public int AttackRangeDelta;
    }

    private readonly Dictionary<Unit, CarriedWeapon> _carriedWeapons = new();

    /// <summary>ApplyEquipmentLoadout hook: remember the unit's carried weapon.</summary>
    private void RegisterCarriedWeapon(Unit unit, ResolvedLoadout loadout)
    {
        if (unit == null || loadout == null || !loadout.HasSecondWeapon)
            return;
        _carriedWeapons[unit] = new CarriedWeapon
        {
            OtherClass = loadout.SecondWeaponClass,
            OtherName = loadout.SecondWeaponName,
            HeldName = loadout.PrimaryWeaponName,
            AttackDamageDelta = loadout.SecondAttackDamageDelta,
            AttackRangeDelta = loadout.SecondAttackRangeDelta,
        };
        GD.Print($"[ProvingGrounds] {unit.Name} carries {loadout.SecondWeaponName} "
                 + $"({WeaponClassLabel(loadout.SecondWeaponClass)}).");
    }

    /// <summary>The tray card for a unit that carries a second weapon, or null.</summary>
    private TechniqueCardView WeaponSwapCardFor(Unit unit)
    {
        if (unit == null || !_carriedWeapons.TryGetValue(unit, out var carried))
            return null;
        string why = WeaponSwapBlockReason(unit);
        string atk = carried.AttackDamageDelta == 0 ? "" : $" Attack {(carried.AttackDamageDelta > 0 ? "+" : "")}{carried.AttackDamageDelta}.";
        string rng = carried.AttackRangeDelta == 0 ? "" : $" Range {(carried.AttackRangeDelta > 0 ? "+" : "")}{carried.AttackRangeDelta}.";
        return new TechniqueCardView
        {
            Id = SwapWeaponCardId,
            Title = $"Change to {carried.OtherName}",
            Cost = $"{SwapWeaponAP} AP",
            Text = $"Take up {WeaponClassLabel(carried.OtherClass)}: its maneuvers and rider.{atk}{rng} Edge is kept.",
            Role = "Utility",
            Enabled = why == null,
            Tooltip = why ?? $"Put away {carried.HeldName} and take up {carried.OtherName}.",
        };
    }

    private string WeaponSwapBlockReason(Unit u)
    {
        if (currentPhase != CombatPhase.PlayerTurn || isInDeploymentPhase)
            return "Not your turn.";
        if (!u.CanAct())
            return $"{u.Name} cannot act.";
        if (u.CurrentActionPoints < SwapWeaponAP)
            return $"Needs {SwapWeaponAP} AP (has {u.CurrentActionPoints}).";
        return null;
    }

    /// <summary>Change to the carried weapon. Returns true when it happened.</summary>
    private bool TrySwapWeapon(Unit u)
    {
        if (u == null || !_carriedWeapons.TryGetValue(u, out var carried))
            return false;
        string why = WeaponSwapBlockReason(u);
        if (why != null)
        {
            combatUI?.AppendActionLog(why);
            return false;
        }
        if (!u.TrySpendAP(SwapWeaponAP))
            return false;

        DisarmManeuver(refresh: false);   // an armed card belonged to the old weapon

        var heldClass = u.WeaponClass;
        u.WeaponClass = carried.OtherClass;
        u.AttackDamage += carried.AttackDamageDelta;
        u.AttackRange += carried.AttackRangeDelta;

        // What is now in the other hand: the one just put away.
        carried.OtherClass = heldClass;
        (carried.OtherName, carried.HeldName) = (carried.HeldName, carried.OtherName);
        carried.AttackDamageDelta = -carried.AttackDamageDelta;
        carried.AttackRangeDelta = -carried.AttackRangeDelta;

        u.Stats.HasActed = true;   // it cost AP; it counts
        RecordMartial(u, "weapon", carried.HeldName, u.Edge);
        combatUI?.AppendActionLog($"{u.Name} takes up {carried.HeldName} ({WeaponClassLabel(u.WeaponClass)}).");
        GD.Print($"[ProvingGrounds] {u.Name} swaps to {carried.HeldName}: class {u.WeaponClass}, "
                 + $"ATK {u.AttackDamage}, range {u.AttackRange}.");

        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        return true;
    }
}
