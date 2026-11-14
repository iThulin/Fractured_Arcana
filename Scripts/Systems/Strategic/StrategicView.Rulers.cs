using Godot;

// ============================================================
// StrategicView.Rulers.cs  (partial of StrategicView)
//
// Purpose:        Who rules a province, in the strategic view's words
//                 (docs/provinces_v1_8_fronts_per_province.md, slice
//                 PV3a, PR57): the ruler line on the province card,
//                 the note the readout and the place card add when a
//                 province is not ruled by its own realm, a ruler's
//                 name as the readout names realms, and the warfront
//                 drawer's province and Advance. Slice PV3b adds who
//                 moves to annex land no one rules (PR65).
// Layer:          UI (strategic view)
// Collaborators:  ProvinceControl.cs (RulerOf, RealmName, Name),
//                 StrategicView.ProvinceHover.cs (the readout,
//                 FillProvinceStance, ProvinceRealm),
//                 StrategicView.ProvinceCard.cs (the card),
//                 StrategicView.cs (the place card, the warfront
//                 drawer, FactionDisplay)
// See:            docs/provinces_v1_8_fronts_per_province.md
//                 sections 3.6 and 5
// ============================================================

public partial class StrategicView
{
    /// <summary>A ruler's name as the readout names realms: a kingdom by the faction that rules
    /// it (as ProvinceRealm names a province's own realm), "the guild", "no one".</summary>
    private string RulerDisplay(string ruler)
    {
        if (ruler == ProvinceControl.Guild)
            return "the guild";
        if (ruler == ProvinceControl.Neutral)
            return "no one";
        if (string.IsNullOrEmpty(ruler))
            return "no kingdom";
        if (_kingdoms != null && _kingdoms.TryGetValue(ruler, out var ks) && ks != null
            && !string.IsNullOrEmpty(ks.ControllingFactionId))
            return FactionDisplay(ks.ControllingFactionId);
        return ruler;
    }

    /// <summary>The note for a province its own realm does not rule, or "": "Taken by House
    /// Veyra in lunation 7.", "The guild's own land since lunation 9.", "No one rules it since
    /// Ostmark fell in lunation 9." with who moves to annex it (AnnexNote, PV3b). A realm the
    /// guild seized whole before fronts walked gets none here: its stance line already says
    /// the guild rules it.</summary>
    private string ProvinceRulerNote(CycleState cycle, Province province)
    {
        if (province == null || string.IsNullOrEmpty(province.ControllerId))
            return "";
        string ruler = ProvinceControl.RulerOf(cycle, province);
        if (ruler == (province.KingdomId ?? ""))
            return "";
        int since = province.ControlSinceLunation;
        if (ruler == ProvinceControl.Guild)
            return since >= 0 ? $"The guild's own land since moon {since}." : "The guild's own land.";
        if (ruler == ProvinceControl.Neutral)
        {
            string realm = ProvinceControl.RealmName(cycle, province.KingdomId);
            return (since >= 0 ? $"No one rules it since {realm} fell in moon {since}." : $"No one rules it since {realm} fell.")
                   + AnnexNote(cycle, province);
        }
        return since >= 0 ? $"Taken by {RulerDisplay(ruler)} in moon {since}." : $"Taken by {RulerDisplay(ruler)}.";
    }

    /// <summary>Provinces PV3b (PR65): who moves to annex a province no one rules, and when
    /// the first would if nothing changes: " House Veyra will annex it in about 3 lunations
    /// unless the guild holds it first (bars of 100: House Veyra 25, Lostmere 20)." It says
    /// so when the guild's influence there slows them, and when no realm borders it.</summary>
    private string AnnexNote(CycleState cycle, Province province)
    {
        var bids = KingdomTickSimulation.AnnexBids(cycle, province.Index);
        if (bids.Count == 0)
            return " No realm borders it to annex it.";
        string bars = "";
        foreach (var bid in bids)
            bars += (bars.Length > 0 ? ", " : "") + RulerDisplay(bid.KingdomId) + " " + bid.Bar;
        string lead = KingdomTickSimulation.AnnexForecast(bids, out int lunations);
        string slowed = province.Influence >= KingdomTickSimulation.AnnexReliefAt
            ? " The guild's influence here slows them."
            : "";
        if (lead == null)
            return $" Bars to annex it, of 100: {bars}.{slowed}";
        string when = lunations <= 1 ? "next moon" : $"in about {lunations} moons";
        return $" {RulerDisplay(lead)} will annex it {when} unless the guild holds it first (bars of 100: {bars}).{slowed}";
    }

    /// <summary>The province card's ruler line: the note, or who rules it when its own realm
    /// does.</summary>
    private string ProvinceRulerLine(CycleState cycle, Province province)
    {
        string note = ProvinceRulerNote(cycle, province);
        if (note.Length > 0)
            return note;
        if (province == null || string.IsNullOrEmpty(province.KingdomId))
            return "No realm rules it.";
        return ProvinceControl.RulerOf(cycle, province) == ProvinceControl.Guild
            ? "The guild rules its whole realm."
            : "Ruled by its own realm.";
    }

    /// <summary>The readout's kind line ends with the note, when there is one.</summary>
    private string ProvinceRulerSuffix(CycleState cycle, Province province)
    {
        string note = ProvinceRulerNote(cycle, province);
        return note.Length > 0 ? " " + note : "";
    }

    /// <summary>The warfront drawer's front: "Southeast Fields, Westmarch  (23, 22)". A front
    /// opened before fronts walked, or a cache siege, shows its tile alone.</summary>
    private static string WarfrontPlace(CycleState cycle, Warfront wf)
    {
        string at = $"({wf.FocusCol}, {wf.FocusRow})";
        var province = ProvinceControl.ProvinceById(cycle?.World, wf.ProvinceId);
        return province == null ? at : $"{ProvinceControl.Name(province)}, {wf.DefenderName}  {at}";
    }

    /// <summary>The warfront drawer's Advance, and what each end of the bar does.</summary>
    private static string WarfrontAdvanceText(Warfront wf)
        => string.IsNullOrEmpty(wf.ProvinceId)
            ? $"{wf.Advance}/100  (falls at 100, repelled at 0)"
            : $"{wf.Advance}/100  (the province is taken at 100, the invasion thrown back at 0)";
}
