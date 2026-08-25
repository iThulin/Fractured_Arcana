using Godot;
using static CampusUi;

// ============================================================
// CampusCompanionsPanel.cs
//
// Purpose:        The Companions tab: recruit companions, and
//                 pick which of them field as the active party.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 CampusUi.cs, CompanionRoster.cs (all mutation),
//                 UITheme.cs
// See:            guild_campus_v2.docx §5b (the visible infirmary);
//                 docs/campus_tab_extraction_v1.md (Phase 2)
// ============================================================

/// <summary>Companion roster. All mutation goes through <see cref="CompanionRoster"/>;
/// this panel only renders and dispatches.
///
/// <para><b>Two different refresh scopes, and the difference is deliberate.</b> Preserved
/// exactly as extracted. Recruiting costs gold, so it calls
/// <see cref="CampusContext.RequestRefreshAll"/> to repaint the gold label and every other
/// panel that reads it. Adding to / removing from the party moves nobody's money, so it
/// calls this panel's own <see cref="Refresh"/>. Widening the party buttons to a full
/// refresh would work but rebuilds eight panels to restyle one button.</para>
///
/// <para>Extracted verbatim from <c>CampusScreen.BuildCompanionsTab</c> /
/// <c>RefreshCompanionList</c> on 2026-08-03.</para></summary>
public sealed class CampusCompanionsPanel : CampusPanel
{
    private VBoxContainer _container;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(10);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Companion Roster");

        var note = new Label
        {
            Text = "Manage your roster and pick the active party. Active party members " +
                           "contribute cards to your deck and tokens to negotiations. " +
                           "New people are found in the world (city hiring halls, rescues, " +
                           "and the courts), not hired from home.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(note);
        layout.AddChild(new HSeparator());

        _container = MakeVBox(8);
        layout.AddChild(_container);
    }

    public override void Refresh()
    {
        if (_container == null)
            return;
        foreach (var child in _container.GetChildren())
            child.QueueFree();

        var save = Ctx?.Save;
        if (save == null)
        {
            _container.AddChild(MakeStubLabel("Select a save slot to see companions."));
            return;
        }

        var partyHeader = new Label
        {
            Text = $"Active party: {save.ActivePartyCompanionIds.Count} / {save.MaxPartySize}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        partyHeader.AddThemeFontSizeOverride("font_size", UITheme.CampusStubFontSize);
        _container.AddChild(partyHeader);

        bool anyShown = false;
        foreach (var c in save.Companions)
        {
            if (!c.IsAvailable && !c.IsRecruited)
                continue;
            if (c.IsPermadead)
                continue;
            anyShown = true;

            var card = new PanelContainer();
            var cardStyle = new StyleBoxFlat
            {
                BgColor = UITheme.CompanionCardBg,
                BorderColor = c.IsRecruited ? UITheme.CompanionCardBorderActive : UITheme.CompanionCardBorderInactive,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                CornerRadiusTopLeft = UITheme.CornerRadius - 1,
                CornerRadiusTopRight = UITheme.CornerRadius - 1,
                CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
                CornerRadiusBottomRight = UITheme.CornerRadius - 1,
                ContentMarginLeft = UITheme.PaddingNormal + 2,
                ContentMarginRight = UITheme.PaddingNormal + 2,
                ContentMarginTop = UITheme.PaddingNormal,
                ContentMarginBottom = UITheme.PaddingNormal,
            };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            card.AddChild(row);

            var info = MakeVBox(2);
            // Control.SizeFlags: this class is not a Control, so the unqualified name
            // that resolved inside CampusScreen does not resolve here.
            info.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            bool inParty = save.ActivePartyCompanionIds.Contains(c.Id);
            // K2 (§5b): the infirmary must be VISIBLE. Injured companions
            // won't field, and the player learns that here, not from a
            // missing unit in the next fight.
            // K3 (§5a): the campus storefront is retired, so unrecruited people
            // show as ABROAD (findable in the world), never as a price tag.
            string badge = c.IsInjured
                ? $"  [INFIRMARY: {c.InjuredLunationsRemaining} lunation{(c.InjuredLunationsRemaining == 1 ? "" : "s")}]"
                : c.IsRecruited ? (inParty ? "  [PARTY]" : "  [ROSTER]") : "  [ABROAD]";

            var nameLabel = new Label { Text = $"{c.Name}{badge}" };
            nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusNameFontSize);
            nameLabel.AddThemeColorOverride("font_color",
                c.IsInjured ? UITheme.Danger : UITheme.TextPrimary);
            info.AddChild(nameLabel);

            var subLabel = new Label
            {
                Text = c.IsInjured
                    ? $"{c.School}  ·  {c.PersonalityTrait}  ·  Loyalty: {c.Loyalty}  ·  ✚ recovering, excluded from expeditions and court duty"
                    : $"{c.School}  ·  {c.PersonalityTrait}  ·  Loyalty: {c.Loyalty}"
            };
            subLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            subLabel.Modulate = UITheme.CompanionSubText;
            info.AddChild(subLabel);
            row.AddChild(info);

            string capturedId = c.Id;
            var btn = new Button { CustomMinimumSize = new Vector2(120, 32) };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);

            if (!c.IsRecruited)
            {
                // K3: campus-menu recruiting retired (v2.1 §2, "the
                // storefront dies"). They're out there: hiring halls carry
                // them at a chance per lunation, and their encounters still
                // grant them directly.
                btn.Text = "Seek them abroad";
                btn.Disabled = true;
            }
            else if (inParty)
            {
                btn.Text = "Remove";
                btn.Pressed += () => { CompanionRoster.RemoveFromParty(capturedId); Refresh(); };
            }
            else
            {
                btn.Text = "Add to Party";
                btn.Disabled = save.ActivePartyCompanionIds.Count >= save.MaxPartySize;
                btn.Pressed += () => { if (CompanionRoster.TryAddToParty(capturedId)) Refresh(); };
            }
            row.AddChild(btn);
            _container.AddChild(card);
        }

        if (!anyShown)
            _container.AddChild(MakeStubLabel("No companions available yet."));
    }
}
