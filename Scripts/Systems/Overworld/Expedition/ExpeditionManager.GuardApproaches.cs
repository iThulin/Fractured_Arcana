using Godot;

// ============================================================
// ExpeditionManager.GuardApproaches.cs
//
// Purpose:        Combat overhaul stage C2b. A guard held by an
//                 archmage's own soldiers offers two approaches on
//                 the scout report. Force it: the fight as it was,
//                 the guard removed, the spoils paid, and standing
//                 lost with that archmage. Slip past: a reach fight,
//                 no spoils, nothing lost, and the guard stays on its
//                 tile but stops holding the ground around it.
//                 Every other guard is fought exactly as before.
// Layer:          Overworld / expedition
// Collaborators:  ExpeditionManager.cs (OpenScoutReport,
//                   CommitCombat, RestoreFromCombat),
//                 ScoutReportPanel.cs (OfferApproaches),
//                 EncounterRouter.cs (GuardApproach),
//                 ProvinceField.cs (GuardAt, GuardShaken),
//                 BattlefieldRoster.cs (RollObjective),
//                 CombatObjectiveDef.cs (KindReach)
// See:            docs/combat_c2_reach_and_field_board_v1.md, part B
// ============================================================

public partial class ExpeditionManager
{
    /// <summary>EncounterRouter.GuardApproach values. Empty is every other fight.</summary>
    public const string GuardApproachForceRealm = "force_realm";
    public const string GuardApproachSlip = "slip";

    /// <summary>Sentiment lost with an archmage for cutting down their guard post.
    /// A slain patrol costs 10 (EmitCombatDeed). A post is a little less. Starting value.</summary>
    private const int ForcedRealmGuardSentiment = -8;

    /// <summary>From OpenScoutReport, after the panel is shown. Adds the second approach
    /// when the guard on this tile is an archmage's own soldiers. A pinned (identified)
    /// fight carries no attribution, and a vault garrison has to be beaten, so neither
    /// gets the choice.</summary>
    private void OfferGuardApproaches(Vector2I coord)
    {
        if (_scoutPanel == null || !IsGuardTile(coord))
            return;
        string archId = _pendingCombatArchmageId;
        if (string.IsNullOrEmpty(archId) || archId == "wilds")
            return;
        if (_window.TryLocalToWorld(coord, out int col, out int row)
            && !string.IsNullOrEmpty(_world.PoiAt(col, row)?.VaultKey))
            return;

        var archDef = ArchmageDefById(archId);
        string who = archDef != null && !string.IsNullOrEmpty(archDef.DisplayName)
            ? archDef.DisplayName
            : archId;

        var engage = _scoutPanel.OnEngage;

        _scoutPanel.OnEngage = () =>
        {
            PinForcedObjective(_pendingEncounter);
            engage?.Invoke();
            // After CommitCombat, which clears the flag for every ordinary fight.
            if (EncounterRouter.Instance != null)
                EncounterRouter.Instance.GuardApproach = GuardApproachForceRealm;
        };

        _scoutPanel.OfferApproaches(
            $"These are {who}'s own soldiers.\n"
            + $"Force it: fight them. The guard is removed and you are paid. You lose {-ForcedRealmGuardSentiment} standing with {who} and gain no influence here.\n"
            + "Slip past: get your leader and half your party across. No pay and no standing lost. The guard stays on its tile but no longer holds the ground around it.",
            "Force it",
            "Slip past",
            () =>
            {
                if (_pendingEncounter != null)
                    _pendingEncounter.Objective = new CombatObjectiveDef { Kind = CombatObjectiveDef.KindReach };
                engage?.Invoke();
                if (EncounterRouter.Instance != null)
                    EncounterRouter.Instance.GuardApproach = GuardApproachSlip;
            });
    }

    /// <summary>A forced guard is never a reach fight: that is the other button. Roll the
    /// objective here and pin it, so the battlefield does not roll again.</summary>
    private static void PinForcedObjective(EncounterDefinition def)
    {
        if (def == null || def.Objective != null)
            return;
        var rolled = BattlefieldRoster.RollObjective(def.RegionId, def.Tier);
        if (rolled == null || rolled.Kind == CombatObjectiveDef.KindReach)
            rolled = new CombatObjectiveDef { Kind = CombatObjectiveDef.KindAnnihilate };
        def.Objective = rolled;
    }

    /// <summary>From RestoreFromCombat: a slip-past fight was won. The guard is shaken
    /// (SP3: it holds only its own tile), nothing is paid, and the site is not spent.</summary>
    private void ResolveSlipPast(EncounterRouter router, Vector2I resultHex)
    {
        CastMasteryTracker.CommitInterval();

        if (_world != null && _window != null
            && _window.TryLocalToWorld(resultHex, out int col, out int row))
        {
            var guard = ProvinceField.GuardAt(_world, col, row);
            if (guard != null)
            {
                guard.GuardShaken = true;
                SaveManager.MarkDirty();
            }
        }

        LogRun("combat_end", "SLIPPED PAST the guard", at: resultHex);
        ShowInfo("You got past the guard. It still stands on its tile, but the ground around it is open.");
        router.GuardApproach = "";
        router.SavedCombatArchmageId = "";
    }

    /// <summary>From RestoreFromCombat's win branch, in place of the +5 a cleared threat
    /// earns: the soldiers were the archmage's own.</summary>
    private void ApplyForcedRealmGuardCost(CampaignState campaign, string archmageId)
    {
        if (campaign == null || string.IsNullOrEmpty(archmageId) || archmageId == "wilds")
            return;
        campaign.ShiftSentiment(archmageId, ForcedRealmGuardSentiment);
        var def = ArchmageDefById(archmageId);
        string who = def != null && !string.IsNullOrEmpty(def.DisplayName) ? def.DisplayName : archmageId;
        _toasts?.Push($"You cut down {who}'s guard post. Standing with {who} {ForcedRealmGuardSentiment}.",
                      QuestToastKind.Progress);
    }
}
