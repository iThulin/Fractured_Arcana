using Godot;

// ============================================================
// StrategicView.Leads.cs  (partial of StrategicView)
//
// Purpose:        The province card's rumors (docs/provinces_v1_13_
//                 orders_and_leads.md, slice PV8a, PR98): under the
//                 places in the wild, how many rumors point into the
//                 province, the first of them in their own words with
//                 their source's record, and that the castle standing
//                 there runs them down.
// Layer:          UI (strategic view)
// Collaborators:  ProvinceLeads.cs (the line), StrategicView.ProvinceCard.cs
//                 (CardLine, SetCardLine; builds and fills the line)
// See:            docs/provinces_v1_13_orders_and_leads.md section 3
// ============================================================

public partial class StrategicView
{
    private Label _cardLeads;

    /// <summary>Built once with the card, under its places line; hidden while no rumor points
    /// into the province.</summary>
    private void BuildLeadCardLine(VBoxContainer stack)
    {
        _cardLeads = CardLine(stack, UITheme.TextSecondary);
        _cardLeads.Visible = false;
    }

    private void RefreshLeadCardLine(CycleState cycle, int index)
    {
        if (_cardLeads == null)
            return;
        SetCardLine(_cardLeads, ProvinceLeads.CardLine(cycle, index));
    }
}
