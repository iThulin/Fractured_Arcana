using Godot;

// ============================================================
// StrategicView.Tutorial.cs
//
// Purpose:        The hub's half of the first lunation (S5): where the
//                 top bar's Next line goes for each step. FirstLunation.Go
//                 hands the step here when the hub is the scene; a step
//                 asked for from another screen arrives at the hub's first
//                 quiet moment (PollChronicle, TakePendingGo).
//
//                 Each step goes to the verb that does it, through the
//                 hub's own controls: the Grand Hall's and the Armory's
//                 panels open over the city, as a click on the building
//                 opens them; the Forces screen; the Sanctum; up to the
//                 world; the castle's sortie drawer; Pass time; the quest
//                 log. Where a step cannot be done from here, the castle
//                 is picked and a notice says why.
// Layer:          UI (strategic view)
// Collaborators:  FirstLunation (the steps), StrategicView (its verbs),
//                 HudManager (the Next line), QuestLogScreen
// ============================================================

public partial class StrategicView
{
    /// <summary>The hub is showing the world map, live: not a city, and no march or panel holding the map
    /// (FirstLunation.Settle counts the world step from it, as the founding opens the map there).</summary>
    public bool OnWorldMap => _world != null && _atlas3D != null && !_atlas3D.CityMode && _atlas3D.AcceptInput;

    /// <summary>The Next line was clicked on the hub, or a step arrived from another screen: go to it.</summary>
    public void FirstLunationGo(string step)
    {
        if (_atlas3D == null || _world == null)
        {
            return;
        }
        var cycle = SaveManager.ActiveSave?.Cycle;
        switch (step)
        {
            case FirstLunation.Ledger:
                OpenHomeRoom(Charters.GrandHallId, CampusPanelId.Guild);
                break;
            case FirstLunation.Forces:
                OpenForcesScreen();
                break;
            case FirstLunation.Equip:
                OpenHomeRoom("armory", CampusPanelId.Armory);
                break;
            case FirstLunation.Deck:
                // As the Sanctum's own door does: the deck editor's way back lands in the city.
                PlayerSession.StartInCityOnOpen = true;
                GetTree().ChangeSceneToFile(FirstLunation.DeckEditorScenePath);
                break;
            case FirstLunation.World:
                if (_atlas3D.CityMode)
                {
                    _atlas3D.LeaveCityMode();
                }
                else
                {
                    _atlas3D.FlyToOverview();
                }
                break;
            case FirstLunation.Sortie:
            case FirstLunation.Fight:
                GoSortie(cycle);
                break;
            case FirstLunation.Home:
                GoHome(cycle);
                break;
            case FirstLunation.Moon:
                if (_atlas3D.CityMode)
                {
                    _atlas3D.LeaveCityMode();
                }
                OnPassTimePressed();
                break;
            case FirstLunation.Quests:
                if (!QuestLogScreen.IsOpen)
                {
                    QuestLogScreen.Toggle(GetTree().Root);
                }
                break;
        }
    }

    /// <summary>Down into the home city if the map is not there, then the building's panel over it, as
    /// a click on the building opens it (OnHomeBuildingPicked), with the camera on the building.</summary>
    private void OpenHomeRoom(string buildingId, CampusPanelId panel)
    {
        if (!HomeBuildingPanelHost.CanFloat(panel))
        {
            return;
        }
        if (_atlas3D.CityMode && !_atlas3D.ActiveCityIsHome)
        {
            _atlas3D.LeaveCityMode();   // another city first: the guild's rooms are at home
        }
        if (!_atlas3D.CityMode)
        {
            if (!_atlas3D.HasCityGrounds || !_world.InBounds(_world.HomeX, _world.HomeY))
            {
                return;
            }
            _atlas3D.FlyToTile(_world.HomeX, _world.HomeY, 30f, 0f);
            _atlas3D.EnterCityMode();
        }
        ShowFloatingPanel(panel, buildingId);
        var entry = SaveManager.ActiveSave?.Buildings?.Find(b => b != null && b.Id == buildingId);
        if (entry != null && entry.IsPlaced)
        {
            _atlas3D.FocusHomeBuilding(new Vector2I(entry.Q, entry.R));
        }
    }

    /// <summary>Up to the world and the castle's sortie drawer, as the Sortie verb opens it; or, when the
    /// castle cannot go, the castle picked and the reason.</summary>
    private void GoSortie(CycleState cycle)
    {
        if (cycle == null)
        {
            return;
        }
        if (_atlas3D.CityMode)
        {
            _atlas3D.LeaveCityMode();
        }
        if (CanSortie(cycle, out string why))
        {
            OnSortiePressed();
            return;
        }
        FocusForce("");
        ShowStrategicNotice("The castle cannot sortie yet", why);
    }

    /// <summary>A sortie ends in the field, so the way home is not a verb here. Pick whichever force is out
    /// and say where its way home is.</summary>
    private void GoHome(CycleState cycle)
    {
        if (cycle == null)
        {
            return;
        }
        if (_atlas3D.CityMode)
        {
            _atlas3D.LeaveCityMode();
        }
        if (cycle.CastleSortie != null && cycle.CastleSortie.Active)
        {
            FocusForce("");
            ShowStrategicNotice("The castle is still in the field",
                "End the sortie in the field: extract it from a supply anchor. Or press Bank the furnace here, "
                + "which ends the sortie and makes camp where the castle stands.");
            return;
        }
        var party = cycle.FieldParties?.Find(p => p?.Sortie != null && p.Sortie.Active);
        if (party != null)
        {
            FocusForce(party.Id);
            ShowStrategicNotice($"{party.Name} are still in the field",
                "End the dive in the field: extract from a supply anchor.");
            return;
        }
        GoSortie(cycle);   // nothing is out: the way home starts with a sortie
    }
}
