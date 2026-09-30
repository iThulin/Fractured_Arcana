using Godot;

// ============================================================
// Administrator.cs
//
// Purpose:        The wizard as the Administrator (campus_building_upgrades_
//                 design_v1 §19; expedition handoff §4.4). The wizard stays
//                 at the campus while a FIELD PARTY dives: the party fights
//                 without them unless the wizard is summoned.
//
//                 Who fights when:
//                   castle sorties, castle defenses, portal strikes, district
//                   fights, debug fights    the wizard, as before
//                   a field party's dive     the party alone, unless
//                                            summoned, or the fight is a
//                                            scripted sortie (a shard
//                                            guardian, an archmage's
//                                            resolution), or no companion is
//                                            able to fight
//                   a posting's defense      the posted force alone
//
//                 Summon the Wizard (expedition HUD, field dives): costs a
//                 reagent (5 splinters) and 2 days added to the dive's time.
//                 The wizard then stays with that party until the dive ends.
//                 Sally Port (Teleport Sigil T3 doctrine): no days, the
//                 reagent only.
// Layer:          Data / rules
// Collaborators:  SortieState (WizardSummoned, SummonDays), ExpeditionManager
//                 (the button, ChargeSortieDays), CombatManager.SpawnTestUnits
//                 (the spawn gate), EncounterRouter (scripted fights),
//                 CompanionRoster (the able party), Charters (Sally Port).
// ============================================================

/// <summary>The Administrator's rules: when the wizard is on the board, and
/// the summons. Stateless; the summons lives on the party's SortieState.</summary>
public static class Administrator
{
    public const string SigilId = "teleport_sigil";
    public const string SallyPort = "sally_port";

    /// <summary>Days a summons adds to the dive (none under the Sally Port).</summary>
    public const int SummonDays = 2;

    /// <summary>The reagent: splinters spent on the summoning circle.</summary>
    public const int SummonSplinters = 5;

    public static bool SallyPortActive(GuildSaveData save) => Charters.IsActive(save, SigilId, SallyPort);

    public static int SummonDaysFor(GuildSaveData save) => SallyPortActive(save) ? 0 : SummonDays;

    /// <summary>The field party's sortie slot, or null.</summary>
    public static SortieState FieldSlot(CycleState cycle, string partyId)
    {
        if (cycle?.FieldParties == null || string.IsNullOrEmpty(partyId))
        {
            return null;
        }
        foreach (var p in cycle.FieldParties)
        {
            if (p != null && p.Id == partyId)
            {
                return p.Sortie;
            }
        }
        return null;
    }

    /// <summary>Is the wizard with this field party right now?</summary>
    public static bool IsSummoned(CycleState cycle, string partyId)
    {
        var slot = FieldSlot(cycle, partyId);
        return slot != null && slot.Active && slot.WizardSummoned;
    }

    /// <summary>Why the wizard cannot be summoned to this party now, or null.</summary>
    public static string CannotSummonReason(GuildSaveData save, string partyId)
    {
        var cycle = save?.Cycle;
        var slot = FieldSlot(cycle, partyId);
        if (slot == null || !slot.Active)
        {
            return "Only a field party out on a dive can summon the wizard.";
        }
        if (slot.WizardSummoned)
        {
            return "The wizard is already with the party.";
        }
        if (save.ArcaneSplinters < SummonSplinters)
        {
            return $"The circle needs {SummonSplinters} splinters (the guild has {save.ArcaneSplinters}).";
        }
        return null;
    }

    /// <summary>Summon the wizard to the party: spend the reagent, add the
    /// days. Returns the report line, or null when refused.</summary>
    public static string TrySummon(GuildSaveData save, string partyId)
    {
        if (CannotSummonReason(save, partyId) != null)
        {
            return null;
        }
        var slot = FieldSlot(save.Cycle, partyId);
        int days = SummonDaysFor(save);
        save.ArcaneSplinters -= SummonSplinters;
        slot.WizardSummoned = true;
        slot.SummonDays += days;
        FreshWizardUnlessCastleOut(save);
        SaveManager.MarkDirty();
        GD.Print($"[Administrator] Wizard summoned to {partyId}: {SummonSplinters} splinters, {days} day(s).");
        return days > 0
            ? $"The wizard steps through the circle to join the party ({SummonSplinters} splinters; the dive runs {days} days longer)."
            : $"The wizard steps through the Sally Port to join the party ({SummonSplinters} splinters, no days lost).";
    }

    /// <summary>The wizard's carried HP (PlayerSession.WizardExpeditionHP) is
    /// the castle run's. A wizard coming to a field party from the campus comes
    /// whole, unless the castle is out and the wizard with it.</summary>
    private static void FreshWizardUnlessCastleOut(GuildSaveData save)
    {
        if (save?.Cycle?.CastleSortie?.IsLive() != true)
        {
            PlayerSession.WizardExpeditionHP = -1;
        }
    }

    /// <summary>Called when the wizard IS spawned. On a field run where the
    /// wizard was not summoned (a scripted sortie, or nobody else able), they
    /// come from the campus for this one fight: whole, unless the castle is out.
    /// A summoned wizard keeps what the dive's fights have cost them.</summary>
    public static void PrepareWizardHpForField()
    {
        if (PlayerSession.DebugCombat || PlayerSession.ExpeditionRunKind != ExpeditionRunKind.Field)
        {
            return;
        }
        var save = SaveManager.ActiveSave;
        string partyId = string.IsNullOrEmpty(PlayerSession.ExpeditionFieldPartyId)
            ? ExpeditionAnchors.PrimaryFieldPartyId
            : PlayerSession.ExpeditionFieldPartyId;
        if (!IsSummoned(save?.Cycle, partyId))
        {
            FreshWizardUnlessCastleOut(save);
        }
    }

    /// <summary>Does the player's wizard take the field in the fight now being
    /// spawned? False only on a field party's run, when not summoned, not a
    /// scripted sortie, and someone else is able to fight. <paramref name="why"/>
    /// says why, for the combat log.</summary>
    public static bool WizardFieldsThisFight(out string why)
    {
        why = "";
        if (PlayerSession.DebugCombat || PlayerSession.ExpeditionRunKind != ExpeditionRunKind.Field)
        {
            return true;
        }
        var router = EncounterRouter.Instance;
        if (router != null && (!string.IsNullOrEmpty(router.SavedCombatGuardianKey)
                               || !string.IsNullOrEmpty(router.SavedResolutionArchmageId)))
        {
            why = "This sortie is the wizard's own: they fight in person.";
            return true;
        }
        var save = SaveManager.ActiveSave;
        string partyId = string.IsNullOrEmpty(PlayerSession.ExpeditionFieldPartyId)
            ? ExpeditionAnchors.PrimaryFieldPartyId
            : PlayerSession.ExpeditionFieldPartyId;
        if (IsSummoned(save?.Cycle, partyId))
        {
            why = "The wizard came through the circle and fights with the party.";
            return true;
        }
        if (CompanionRoster.GetActiveParty().Count == 0)
        {
            why = "No one in the party can fight: the wizard comes through the circle at once.";
            return true;
        }
        why = "The wizard directs from the campus (the Lens in an ally's tray casts through them). Summon them from the expedition map to fight in person.";
        return false;
    }
}
