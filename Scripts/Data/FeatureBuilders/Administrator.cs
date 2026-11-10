using Godot;

// ============================================================
// Administrator.cs
//
// Purpose:        Where the player's wizard stands in a fight (A1,
//                 docs/field_party_addendum_a1_wizard_leads.md; ruled
//                 2026-10-12, built 2026-11-10). The wizard leads the
//                 Wizard's Party: the primary field party, never a
//                 detachment (FieldParty.CarriesWizard).
//
//                 Who fights when:
//                   the Wizard's Party in the field   the wizard, always
//                   castle sorties, castle defenses,
//                   portal strikes, district fights,
//                   debug fights                      the wizard, as before
//                   another party's dive              the party alone, unless
//                                                     the fight is a beat that
//                                                     is the wizard's own (a
//                                                     shard guardian, an
//                                                     archmage's resolution)
//                                                     or no companion is able
//                                                     to fight
//                   a posting's defense               the posted force alone
//
//                 The summons is retired (A4.2): nothing calls the wizard to
//                 a party any more. The Sally Port (Teleport Sigil T3
//                 doctrine) brings the wizard to a castle defense with no
//                 rounds lost instead (CombatManager.CastleDefense).
// Layer:          Data / rules
// Collaborators:  FieldParty (CarriesWizard), ExpeditionManager
//                 (WizardWithThisForce, the fresh sortie),
//                 CombatManager.SpawnTestUnits (the spawn gate),
//                 EncounterRouter (the wizard's own beats),
//                 CompanionRoster (the able party), Charters (Sally Port).
// ============================================================

/// <summary>The rules for when the wizard is on the board. Stateless: the
/// Wizard's Party is derived from the party's id.</summary>
public static class Administrator
{
    public const string SigilId = "teleport_sigil";
    public const string SallyPort = "sally_port";

    public static bool SallyPortActive(GuildSaveData save) => Charters.IsActive(save, SigilId, SallyPort);

    /// <summary>Does this field party carry the wizard? An empty id means the
    /// primary party. A party the cycle does not list is judged by its id.</summary>
    public static bool WizardWith(CycleState cycle, string partyId)
    {
        if (string.IsNullOrEmpty(partyId))
        {
            partyId = ExpeditionAnchors.PrimaryFieldPartyId;
        }
        if (cycle?.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p != null && p.Id == partyId)
                {
                    return p.CarriesWizard;
                }
            }
        }
        return partyId == ExpeditionAnchors.PrimaryFieldPartyId;
    }

    /// <summary>The wizard's carried HP (PlayerSession.WizardExpeditionHP) is one
    /// pool. A wizard setting out with a party sets out whole, unless the castle
    /// is out and that HP is the castle run's.</summary>
    public static void WizardSetsOut(GuildSaveData save)
    {
        if (save?.Cycle?.CastleSortie?.IsLive() != true)
        {
            PlayerSession.WizardExpeditionHP = -1;
        }
    }

    /// <summary>Called when the wizard IS spawned. With the Wizard's Party they
    /// keep what the dive's fights have cost them. Joining another party's fight
    /// (their own beat, or nobody else able), they come for that one fight:
    /// whole, unless the castle is out.</summary>
    public static void PrepareWizardHpForField()
    {
        if (PlayerSession.DebugCombat || PlayerSession.ExpeditionRunKind != ExpeditionRunKind.Field)
        {
            return;
        }
        var save = SaveManager.ActiveSave;
        if (!WizardWith(save?.Cycle, PlayerSession.ExpeditionFieldPartyId))
        {
            WizardSetsOut(save);
        }
    }

    /// <summary>Does the player's wizard take the field in the fight now being
    /// spawned? False only on the dive of a party that does not carry them, when
    /// the fight is not their own beat and someone else is able to fight.
    /// <paramref name="why"/> says why, for the combat log; empty when the wizard
    /// is simply where they belong.</summary>
    public static bool WizardFieldsThisFight(out string why)
    {
        why = "";
        if (PlayerSession.DebugCombat || PlayerSession.ExpeditionRunKind != ExpeditionRunKind.Field)
        {
            return true;
        }
        var save = SaveManager.ActiveSave;
        if (WizardWith(save?.Cycle, PlayerSession.ExpeditionFieldPartyId))
        {
            return true;
        }
        var router = EncounterRouter.Instance;
        if (router != null && (!string.IsNullOrEmpty(router.SavedCombatGuardianKey)
                               || !string.IsNullOrEmpty(router.SavedResolutionArchmageId)))
        {
            why = "This fight is the wizard's own: they come to it in person.";
            return true;
        }
        if (CompanionRoster.GetActiveParty().Count == 0)
        {
            why = "No one in the party can fight: the wizard comes to them at once.";
            return true;
        }
        why = "This party fights without the wizard (the Lens in an ally's tray casts through them).";
        return false;
    }
}
