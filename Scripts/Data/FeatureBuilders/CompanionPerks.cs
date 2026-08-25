using Godot;

// ============================================================
// CompanionPerks.cs
//
// Purpose:        K4 Trusted personality perks: one small passive
//                 per PersonalityTrait, active at LoyaltyTier
//                 Trusted and above. Trait-keyed (not class-keyed),
//                 so the §5c procedural matrix covers hirelings for
//                 free. FRESH-AUTHORED K4 STARTING VALUES. The v1
//                 perk matrix could not be located; these are
//                 starting values, not recovered canon.
// Layer:          Data (FeatureBuilders)
// Collaborators:  CombatManager (unit spawn), ExpeditionManager
//                 (party pool + extraction gold), UI dossiers.
//
// The five perks:
//   Stoic:    Unshakeable,   +1 Armor in combat
//   Reckless: First Blood,   +1 AttackDamage in combat
//   Curious:  Pathfinder's Eye, +1 MoveRange in combat
//   Loyal:    Shoulder to Shoulder, +2 party pool contribution
//   Cunning:  Finder's Fee,  +10 gold at successful extraction
// ============================================================

/// <summary>Trusted-tier personality perks. All appliers are no-ops below
/// Trusted, for the dead, and for empty traits; callers never pre-check.</summary>
public static class CompanionPerks
{
    // ── Tuning (K4 starting values) ──────────────────────────────────────
    public const int StoicArmor = 1;
    public const int RecklessDamage = 1;
    public const int CuriousMove = 1;
    public const int LoyalPoolBonus = 2;
    public const int CunningExtractionGold = 10;

    /// <summary>True when the companion's perk is live (Trusted+, alive).</summary>
    public static bool PerkActive(Companion c) =>
        c != null && !c.IsPermadead && c.GetLoyaltyTier() >= LoyaltyTier.Trusted;

    /// <summary>Combat-side perks, applied at unit spawn (both martial and
    /// arcane branches: armor and stride mean the same thing to both;
    /// Reckless damage only matters where attacks do).</summary>
    public static void ApplyToUnit(Unit unit, Companion c)
    {
        if (unit == null || !PerkActive(c)) return;
        switch (c.PersonalityTrait)
        {
            case "Stoic":
                unit.Stats.Armor += StoicArmor;
                GD.Print($"[Perk] {c.Name} Unshakeable: +{StoicArmor} armor (Trusted Stoic).");
                break;
            case "Reckless":
                unit.AttackDamage += RecklessDamage;
                GD.Print($"[Perk] {c.Name} First Blood: +{RecklessDamage} damage (Trusted Reckless).");
                break;
            case "Curious":
                unit.MoveRange += CuriousMove;
                GD.Print($"[Perk] {c.Name} Pathfinder's Eye: +{CuriousMove} move (Trusted Curious).");
                break;
        }
    }

    /// <summary>Loyal pool perk, added beside LoyaltyPoolBonus in
    /// ComputePartyBaseHP. Returns 0 unless a live Trusted+ Loyal.</summary>
    public static int PoolBonus(Companion c) =>
        PerkActive(c) && c.PersonalityTrait == "Loyal" ? LoyalPoolBonus : 0;

    /// <summary>Cunning extraction perk: flat gold per fielded live Trusted+
    /// Cunning companion, added to GoldEarned before banking.</summary>
    public static int ExtractionGold(GuildSaveData save)
    {
        if (save == null) return 0;
        int gold = 0;
        foreach (var id in save.ActivePartyCompanionIds)
        {
            var c = save.Companions.Find(x => x.Id == id && x.IsRecruited && !x.IsPermadead);
            if (PerkActive(c) && c.PersonalityTrait == "Cunning")
            {
                gold += CunningExtractionGold;
                GD.Print($"[Perk] {c.Name} Finder's Fee: +{CunningExtractionGold}g (Trusted Cunning).");
            }
        }
        return gold;
    }
}
