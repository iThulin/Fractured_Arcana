using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.Exam.cs
//
// Purpose:        E4 (ruled 2026-10-26): a declaration exam's apparatus
//                 fitted to the wizard. The practicals were tuned for the
//                 candidate of the prologue (20 health, 3 mana, no armor,
//                 no shield, no spell damage); a wizard who has grown since
//                 would otherwise break them without the school's verb.
//                 DisciplineExam works the fitting out once, from the
//                 equipment the wizard fields (ExamScaling), and the
//                 router carries it here; this applies it to every enemy
//                 the encounter placed, once they stand: health, the
//                 plate (chitin), the mend threshold (regrowth) and the
//                 blows. Practice dummies (role "summon") stay as they
//                 are: they are the rack's material, not its armor.
// Layer:          Combat / runtime
// Collaborators:  ExamScaling (DisciplineExam.cs), EncounterRouter
//                 (ExamFitting), CombatManager.cs (SpawnAndPlaceEnemies
//                 calls it), Unit (Stats, AttackDamage, Abilities,
//                 RecacheSelfAuras)
// ============================================================

public partial class CombatManager
{
    /// <summary>From SpawnAndPlaceEnemies, once the enemies stand and before the roster draws:
    /// a declaration exam fits its apparatus to the wizard. A no-op for every other fight, and
    /// for a wizard who fields nothing the candidate did not.</summary>
    private void ScaleExamToWizard()
    {
        var fitting = EncounterRouter.Instance?.ExamFitting;
        if (fitting == null || !fitting.Fitted)
            return;

        int fitted = 0;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || e.TeamId == 0 || e.IsPlayerControlled)
                continue;
            if (string.Equals(e.Role, "summon", StringComparison.OrdinalIgnoreCase))
                continue;

            bool plated = HasAbilityKey(e, "chitin");
            int baseHealth = e.Stats.MaxHealth;
            int health = fitting.Health(baseHealth, plated);
            e.Stats.MaxHealth = health;
            e.Stats.Health = health;
            int baseDamage = e.AttackDamage;
            if (baseDamage > 0)
                e.AttackDamage = fitting.Damage(baseDamage);
            e.Abilities = FittedAbilities(e.Abilities, fitting);
            e.RecacheSelfAuras();
            e.RefreshHealthBar();
            fitted++;
            GD.Print($"[Exam] {e.Name}: health {baseHealth} to {health}, blows {baseDamage} to {e.AttackDamage}, "
                     + $"plate {e.ChitinAmount}.");
        }
        if (fitted > 0)
            combatUI?.AppendActionLog("── The apparatus is fitted to you. ──");
        GD.Print($"[Exam] {fitted} enemy unit(s) {fitting}.");
    }

    private static bool HasAbilityKey(Unit unit, string key)
    {
        if (unit?.Abilities == null)
            return false;
        foreach (var ab in unit.Abilities)
            if (ab != null && string.Equals(ab.Key, key, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>A unit's own copy of its abilities with the fitted numbers: chitin's amount and
    /// regrowth's threshold. The definition's list is shared by every unit of the kind, so it is
    /// copied, never edited. Each copy's description gets its new number where the old one
    /// stood, so the tooltip tells the truth.</summary>
    private static List<UnitAbilityDef> FittedAbilities(List<UnitAbilityDef> abilities, ExamScaling fitting)
    {
        var copies = new List<UnitAbilityDef>();
        if (abilities == null)
            return copies;
        foreach (var ab in abilities)
        {
            if (ab == null)
                continue;
            var copy = new UnitAbilityDef
            {
                Key = ab.Key,
                Trigger = ab.Trigger,
                Name = ab.Name,
                IntelDescription = ab.IntelDescription,
                Params = new Dictionary<string, string>(ab.Params ?? new Dictionary<string, string>()),
            };
            if (string.Equals(ab.Key, "chitin", StringComparison.OrdinalIgnoreCase))
                Refit(copy, "amount", fitting.Plate(ab.GetIntParam("amount", 2)), ab.GetIntParam("amount", 2));
            else if (string.Equals(ab.Key, "regrowth", StringComparison.OrdinalIgnoreCase))
                Refit(copy, "threshold", fitting.Mend(ab.GetIntParam("threshold", 20)), ab.GetIntParam("threshold", 20));
            copies.Add(copy);
        }
        return copies;
    }

    private static void Refit(UnitAbilityDef copy, string param, int value, int was)
    {
        copy.Params[param] = value.ToString();
        if (!string.IsNullOrEmpty(copy.IntelDescription) && value != was)
            copy.IntelDescription = copy.IntelDescription.Replace(was.ToString(), value.ToString());
    }
}
