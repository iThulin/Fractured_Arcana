using Godot;

// ============================================================
// StrategicView.PortalStrike.cs  (partial of StrategicView)
//
// Purpose:        The portal strike's owed-fight flow (TeleportSigil.cs):
//                 offer it, launch the home-city defense compiled around
//                 the Sigil, pick up the return, or seal the circle
//                 instead. The same shape as the castle defense
//                 (OfferPendingCastleDefense / LaunchCastleDefense /
//                 ConsumeCastleDefenseReturn), so the three owed fights
//                 read alike.
// Layer:          UI + orchestration (strategic)
// Collaborators:  TeleportSigil (rules, Resolve), CycleState
//                 (PendingPortalStrikeKingdomId, PortalStrikeLaunched),
//                 HomeCityCombatSource + CityBattlemapCompiler
//                 (CompilePortalStrike), MapRecipeRegistry,
//                 EncounterPoolLoader, EncounterRouter, SceneTransition.
// ============================================================

public partial class StrategicView
{
    /// <summary>True while the strike dialog is up, so it can never open twice.</summary>
    private bool _portalOfferOpen;

    /// <summary>A strike is owed: fight at the rift, or break the circle.</summary>
    private void OfferPendingPortalStrike()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (_portalOfferOpen || cycle == null || string.IsNullOrEmpty(cycle.PendingPortalStrikeKingdomId)
            || cycle.PortalStrikeLaunched)
        {
            return;
        }
        _portalOfferOpen = true;
        string who = FactionDisplay(cycle.PendingPortalStrikeKingdomId);

        var dialog = new ConfirmationDialog
        {
            Title = "The Sigil is open",
            OkButtonText = "Seal the rift",
            CancelButtonText = "Break the circle",
            DialogText =
                $"The Teleport Sigil is burning open from the far side. Soldiers of {who} are coming " +
                "through into the campus.\n\n" +
                $"Seal the rift: the wizard and the home party hold the circle for {TeleportSigil.StrikeRounds} rounds. " +
                "They have come to foul the guild's land: they will raze the foundations of the buildings " +
                "near the rift, and their ritualists will channel blight into the ground. Strike a ritualist to " +
                "break the channel. Lose, and they loot the campus too: the Sigil and another building are damaged and " +
                $"{TeleportSigil.GoldTakenPercent}% of the gold goes with them.\n\n" +
                "Break the circle: no fight. The Sigil takes the damage alone and their pattern is useless.",
        };
        dialog.Confirmed += () => { _portalOfferOpen = false; dialog.QueueFree(); LaunchPortalStrike(); };
        // Breaking the circle costs the Sigil, so it hangs off the BUTTON; Escape
        // and the close box only defer (the strike stays owed), as the castle's does.
        dialog.GetCancelButton().Pressed += SealPortalStrike;
        dialog.Canceled += () => { _portalOfferOpen = false; dialog.QueueFree(); };

        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>Refuse the fight: the circle is broken from our side.</summary>
    private void SealPortalStrike()
    {
        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle == null || string.IsNullOrEmpty(cycle.PendingPortalStrikeKingdomId))
        {
            return;
        }
        string who = FactionDisplay(cycle.PendingPortalStrikeKingdomId);
        string line = TeleportSigil.Resolve(cycle, save, "sealed", who);
        cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
        cycle.PendingSiegeReports.Add(line);
        SaveManager.SaveIfDirty();
    }

    /// <summary>Send the home party into the campus, compiled around the Sigil.</summary>
    private void LaunchPortalStrike()
    {
        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle == null || save.Ledger == null
            || string.IsNullOrEmpty(cycle.PendingPortalStrikeKingdomId) || cycle.PortalStrikeLaunched)
        {
            return;   // nothing owed any more (a stale dialog), or already under way
        }
        string kid = cycle.PendingPortalStrikeKingdomId;
        string who = FactionDisplay(kid);

        string regionId = "";
        if (_kingdoms != null && !string.IsNullOrEmpty(kid) && _kingdoms.TryGetValue(kid, out var ks))
        {
            regionId = string.IsNullOrEmpty(ks.TemplateRegionId) ? ks.RegionId : ks.TemplateRegionId;
        }
        var def = EncounterPoolLoader.Pick(regionId, EncounterTier.Siege, "Grassland",
                                           CampaignEscalation.CombatDifficultyMult(cycle));

        // The map: the home city around the Sigil. No placed Sigil (or no room
        // around it) means no rift to fight at; the strike is sealed instead of
        // being fought on a wrong map.
        string recipeId = null;
        if (def != null && def.Enemies.Count > 0)
        {
            try
            {
                ulong seed = (ulong)(cycle.WorldSeed * 31 + (cycle.Calendar?.CurrentLunation ?? 1) * 7919);
                var win = CityBattlemapCompiler.CompilePortalStrike(
                    new HomeCityCombatSource(save.Ledger), seed, defending: true);
                var dict = Json.ParseString(win.RecipeJson).AsGodotDictionary();
                if (dict != null && dict.Count > 0)
                {
                    MapRecipeRegistry.Register(MapRecipe.FromDict(dict));
                    recipeId = win.RecipeId;
                }
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"[PortalStrike] Could not compile the home city: {e.Message}");
            }
        }
        if (recipeId == null)
        {
            // No roster or no rift to fight at: they think better of it. Nothing
            // is damaged, because the player chose to fight (as a posting's does).
            cycle.PendingPortalStrikeKingdomId = "";
            cycle.PortalStrikeLaunched = false;
            if (cycle.PortalKnowledge != null && !string.IsNullOrEmpty(kid))
                cycle.PortalKnowledge[kid] = 0;
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add($"The rift gutters and closes before {who} can come through.");
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
            return;
        }

        def.MapRecipe = recipeId;
        def.Objective = new CombatObjectiveDef
        {
            Kind = CombatObjectiveDef.KindSurvive,
            Rounds = TeleportSigil.StrikeRounds,
            Description = "Seal the rift. Survive.",
        };

        if (EncounterRouter.Instance == null)
            GetTree().Root.AddChild(new EncounterRouter { Name = "EncounterRouter" });
        var router = EncounterRouter.Instance;
        if (router == null)
        {
            return;
        }

        cycle.PortalStrikeLaunched = true;
        SaveManager.MarkDirty();
        // The fight fills this afresh at setup; nothing from an earlier one may leak in.
        SiegeBlightReport.Clear();

        // The home party: the wizard and whoever stands at the guild, not a
        // field party (RunRosterIds reads ActivePartyCompanionIds for Castle).
        PlayerSession.ExpeditionRunKind = ExpeditionRunKind.Castle;
        PlayerSession.ExpeditionFieldPartyId = "";

        router.HasPendingReturn = false;
        router.SavedCombatWasPatrolAmbush = false;
        router.SavedCombatPatrolArchmageId = "";
        router.SavedCombatGuardianKey = "";
        router.SavedCombatArchmageId = "";
        router.SavedResolutionArchmageId = "";
        router.ReturnSceneOverride = StrategicScenePath;
        router.SetCurrentTier(def.Tier);

        SaveManager.SaveIfDirty();
        EncounterContextCarrier.Set(def);
        EncounterContextCarrier.SetContext(def.TerrainType, def.Tier);
        SceneTransition.Go(GetTree(), router.CombatScenePath, "The Rift",
            $"{who} is coming through the Sigil.");
    }

    // ── Recall home (Sigil T2) ──────────────────────────────────────────

    /// <summary>Can this marching party abandon the road and step home to the dock?</summary>
    private static bool CanSigilRecallHome(CycleState cycle, FieldParty party)
    {
        if (party == null || party.State != FieldPartyState.Travelling)
        {
            return false;
        }
        var dock = TeleportSigil.DockTile(cycle);
        return dock.HasValue && FieldMarch.CanTeleportTo(cycle, party, dock.Value.x, dock.Value.y, out _);
    }

    /// <summary>The Move verb on a marching party: every other order is refused on
    /// the road, but a Sigil at T2 can call them home. Returns false when it cannot,
    /// so the caller shows its usual refusal.</summary>
    private bool OfferSigilRecallHome(CycleState cycle, FieldParty party)
    {
        if (!CanSigilRecallHome(cycle, party))
        {
            return false;
        }
        var dock = TeleportSigil.DockTile(cycle).Value;
        var dialog = new ConfirmationDialog
        {
            Title = "Recall through the Sigil",
            OkButtonText = "Step home",
            CancelButtonText = "Keep marching",
            DialogText = $"{party.Name} is on the road, {party.TravelPhasesRemaining} tile(s) from where they are going. "
                       + "The Teleport Sigil can open a way under their feet and bring them home to the dock now. "
                       + "The march is abandoned.",
        };
        string partyId = party.Id;
        dialog.Confirmed += () =>
        {
            dialog.QueueFree();
            // The same selection the offer was made for: TryMovePartyTo reads it.
            SelectPiece(partyId);
            TryMovePartyTo(dock.x, dock.y);
        };
        dialog.Canceled += () => dialog.QueueFree();
        AddChild(dialog);
        dialog.PopupCentered();
        return true;
    }

    /// <summary>Pick up a returning portal strike. Keyed on the launched marker AND
    /// the router's return being this scene, so a mid-combat reload leaves it owed.</summary>
    private void ConsumePortalStrikeReturn(CycleState cycle)
    {
        if (cycle == null || !cycle.PortalStrikeLaunched)
        {
            return;
        }
        var router = EncounterRouter.Instance;
        if (router == null || !router.HasPendingReturn || router.ReturnSceneOverride != StrategicScenePath)
        {
            return;
        }
        bool won = router.CombatWon;
        router.HasPendingReturn = false;
        router.ReturnSceneOverride = "";

        var save = SaveManager.ActiveSave;
        string who = FactionDisplay(cycle.PendingPortalStrikeKingdomId);
        if (won && save != null)
        {
            save.Gold += router.GoldReward;
            save.ArcaneSplinters += router.SplinterReward;
            save.BuildMaterials += router.MaterialReward;
        }
        string line = TeleportSigil.Resolve(cycle, save, won ? "won" : "lost", who);
        // What the attackers did to the ground stands either way (campus corruption).
        string blight = TeleportSigil.ApplySiegeBlight(save);
        if (!string.IsNullOrEmpty(blight))
        {
            line = $"{line} {blight}";
        }
        cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
        cycle.PendingSiegeReports.Add(line);
        ScryInbox.Post(cycle, won ? ScryChannel.Note : ScryChannel.Messenger,
            won ? "The rift is sealed" : "The campus was looted", line, "", "portal");
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
    }
}
