using Godot;
using System.Collections.Generic;
// ============================================================
// StrategicView.GuildScreen.cs: what the map took over from the Guild screen
//
// Purpose:        Three pieces of the retired Guild screen that live on the
//                 strategic map since 2026-11-05 (part B of the spec below):
//                 the last expedition's result as one line when the map
//                 comes up (B5); one line when a school
//                 first turns declarable, since the Grand Hall is no longer a
//                 screen a guild lands on (B3); and the rule that sends a door
//                 building (a screen of its own, or the full campus overlay)
//                 to its card first when it has tiers to buy or doctrines to
//                 charter (B2), so the city can raise it and charter it.
// Layer:          UI (strategic view)
// Collaborators:  StrategicView.cs (_Ready, OnHomeBuildingPicked),
//                 RunResultData (the result), DeclarationService (who is
//                 declarable), Charters and BuildingDatabase (doctrines and
//                 tiers), HomeBuildingPanelHost (the card and its Enter)
// See:            docs/guild_screen_retirement_v1.md
// ============================================================
public partial class StrategicView
{
    /// <summary>Ledger flag prefix: this school's turn to declarable has been said (B3).</summary>
    private const string DeclarableNotedPrefix = "declarable_noted_";

    /// <summary>The last expedition's line, waiting for the map to come up (B5).</summary>
    private string _expeditionResultToast = "";

    /// <summary>The last expedition in one line (B5): home with its spoils, or failed with them
    /// lost. Read from RunResultData before anything clears it.</summary>
    private static string ExpeditionResultLine()
    {
        int fights = RunResultData.EncountersWon;
        string spoils = $"{RunResultData.GoldEarned} gold and {RunResultData.ArcaneSplinters} splinters";
        string won = $"{fights} fight{(fights == 1 ? "" : "s")} won";
        return RunResultData.ReachedObjective
            ? $"The expedition is home with {spoils} ({won})."
            : $"The expedition failed: {spoils} lost ({won}). Its discoveries are kept.";
    }

    /// <summary>Show the last expedition's line once (B5). Deferred from _Ready, so the map's
    /// toast layer is built first.</summary>
    private void PushExpeditionResult()
    {
        if (string.IsNullOrEmpty(_expeditionResultToast))
        {
            return;
        }
        EnsureCityExploreToasts();
        _cityExploreToasts?.Push(_expeditionResultToast, QuestToastKind.Progress);
        _expeditionResultToast = "";
    }

    /// <summary>B3: a school that has turned declarable is said once, on the map. One ledger
    /// flag per school, so it is said once per guild; a school that falls out of reach and
    /// comes back is not said again (the Grand Hall's roll still shows it).</summary>
    private void NoteDeclarableSchools()
    {
        var save = SaveManager.ActiveSave;
        var flags = save?.Ledger?.MetaNarrativeFlags;
        if (flags == null)
        {
            return;
        }
        var named = new List<string>();
        foreach (CardSchool school in System.Enum.GetValues(typeof(CardSchool)))
        {
            string name = school.ToString();
            if (name == DeclarationService.StartingSchool)
            {
                continue;
            }
            string flag = DeclarableNotedPrefix + name.ToLowerInvariant();
            if (flags.Contains(flag))
            {
                continue;
            }
            var status = DeclarationService.Evaluate(save, name);
            if (status.Declared || !status.Eligible)
            {
                continue;
            }
            flags.Add(flag);
            named.Add(name);
        }
        if (named.Count == 0)
        {
            return;
        }
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        EnsureCityExploreToasts();
        _cityExploreToasts?.Push($"{string.Join(", ", named)} can be declared now, in the Grand Hall.",
                                 QuestToastKind.Unlock);
    }

    /// <summary>B2: does this building's card have business of its own, tiers to buy or
    /// doctrines to charter? A door building with neither (the Sanctum) still opens straight
    /// through to its screen.</summary>
    private static bool BuildingCardHasBusiness(string buildingId)
    {
        var template = BuildingDatabase.GetTemplate(buildingId);
        return template != null && (template.MaxTier > 1 || Charters.DoctrineTier(template) != null);
    }
}
