using Godot;
using System.Collections.Generic;

// ============================================================
// CouncilPanel.cs
//
// Purpose:        The council screen (Court & Council phase C2),
//                 opened from the strategic view. Shows every
//                 contacted-or-not court: seat, standing band,
//                 courtier roster, active envoy mission, and the
//                 dispatch flow (companion -> mission -> target
//                 courtier -> confirm). Also displays the recent
//                 Herald's Report lines. Built entirely in code
//                 on the CampusScreen pattern (manual buttons +
//                 Visible, no TabContainer/ScrollContainer
//                 nesting hazards; MarginContainer inside
//                 ScrollContainer gets ShrinkBegin).
// Layer:          UI (strategic view)
// Collaborators:  CouncilTick.cs (missions, queries, report),
//                 CouncilState.cs (data), StrategicView.cs
//                 (opens via a HUD button), CompanionRoster.cs
//                 (party removal on dispatch), UITheme.cs,
//                 SaveManager.cs
// See:            court_council_system_v1_1.docx §5, §6 (Tier A)
// ============================================================

/// <summary>The council screen. Create/close via <see cref="Toggle"/>.</summary>
public partial class CouncilPanel : CanvasLayer
{
    private static CouncilPanel _instance;

    // Dispatch selection state (one dispatch flow open at a time).
    private string _dispatchKingdomId = null;
    private string _selCompanionId = null;
    private string _selMissionId = null;
    private string _selTargetCourtierId = null;

    private Label _headerStatus;
    private VBoxContainer _reportBox;
    private VBoxContainer _courtList;

    /// <summary>Open the panel if closed; close it if open.</summary>
    public static void Toggle(Node host)
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            _instance.QueueFree();
            _instance = null;
            return;
        }
        _instance = new CouncilPanel { Name = "CouncilPanel" };
        host.AddChild(_instance);
    }

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUI));
    }

    public override void _ExitTree()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void BuildUI()
    {
        var backdrop = new ColorRect { Color = UITheme.BgOverlay };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -470,
            OffsetRight = 470,
            OffsetTop = -310,
            OffsetBottom = 310,
        };
        panel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        // ── Header: title, envoy cap, gold, close ────────────────────────
        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 14);
        vbox.AddChild(headerRow);

        var title = new Label { Text = "The Council" };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(title);

        _headerStatus = new Label();
        _headerStatus.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _headerStatus.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        _headerStatus.VerticalAlignment = VerticalAlignment.Center;
        headerRow.AddChild(_headerStatus);

        var closeBtn = new Button { Text = "Close", CustomMinimumSize = new Vector2(90, 34) };
        closeBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(closeBtn, isPrimary: false);
        closeBtn.Pressed += () => Toggle(GetParent());
        headerRow.AddChild(closeBtn);

        vbox.AddChild(new HSeparator());

        // ── Herald's Report (recent dispatches) ──────────────────────────
        var reportHeader = new Label { Text = "The Herald's Report" };
        reportHeader.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        reportHeader.AddThemeColorOverride("font_color", UITheme.Violet);
        vbox.AddChild(reportHeader);

        _reportBox = new VBoxContainer();
        _reportBox.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(_reportBox);

        vbox.AddChild(new HSeparator());

        // ── Court list ───────────────────────────────────────────────────
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(scroll);

        var scrollMargin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin, // Compatibility rule
        };
        scrollMargin.AddThemeConstantOverride("margin_right", 8);
        scroll.AddChild(scrollMargin);

        _courtList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _courtList.AddThemeConstantOverride("separation", 10);
        scrollMargin.AddChild(_courtList);

        RefreshAll();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Refresh
    // ══════════════════════════════════════════════════════════════════════

    private void RefreshAll()
    {
        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle?.Council == null)
        {
            return;
        }

        int cap = CouncilQueries.EnvoyCap(save);
        int active = cycle.Council.ActiveMissions.Count;
        _headerStatus.Text = $"Envoys afield: {active} / {cap}    ·    Gold: {save.Gold}";

        // Report lines (newest last; show the tail).
        foreach (var child in _reportBox.GetChildren())
        {
            child.QueueFree();
        }
        if (CouncilTick.RecentReports.Count == 0)
        {
            var none = new Label { Text = "No dispatches yet. Send an envoy; word returns when the moon turns." };
            none.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            none.AddThemeColorOverride("font_color", UITheme.TextDim);
            _reportBox.AddChild(none);
        }
        else
        {
            int start = Mathf.Max(0, CouncilTick.RecentReports.Count - 8);
            for (int i = start; i < CouncilTick.RecentReports.Count; i++)
            {
                var line = new Label
                {
                    Text = CouncilTick.RecentReports[i],
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                bool isHeader = CouncilTick.RecentReports[i].StartsWith("—");
                line.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                line.AddThemeColorOverride("font_color",
                    isHeader ? UITheme.Gold : UITheme.TextSecondary);
                _reportBox.AddChild(line);
            }
        }

        // Court cards.
        foreach (var child in _courtList.GetChildren())
        {
            child.QueueFree();
        }
        foreach (var kingdomId in SortedCourtIds(cycle))
        {
            _courtList.AddChild(BuildCourtCard(save, cycle, cycle.Council.Courts[kingdomId]));
        }
    }

    private static List<string> SortedCourtIds(CycleState cycle)
    {
        var ids = new List<string>(cycle.Council.Courts.Keys);
        ids.Sort(System.StringComparer.Ordinal);
        return ids;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Court card
    // ══════════════════════════════════════════════════════════════════════

    private Control BuildCourtCard(GuildSaveData save, CycleState cycle, CourtState court)
    {
        var band = court.Band();

        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, BandColor(band)));

        var cardMargin = new MarginContainer();
        cardMargin.AddThemeConstantOverride("margin_left", 14);
        cardMargin.AddThemeConstantOverride("margin_right", 14);
        cardMargin.AddThemeConstantOverride("margin_top", 10);
        cardMargin.AddThemeConstantOverride("margin_bottom", 10);
        card.AddChild(cardMargin);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        cardMargin.AddChild(v);

        // Header: kingdom, seat, band.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        v.AddChild(head);

        var nameLbl = new Label
        {
            Text = CouncilTick.CourtDisplayName(cycle, court.KingdomId),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        nameLbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        head.AddChild(nameLbl);

        var seatLbl = new Label { Text = SeatDisplay(cycle, court) };
        seatLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        seatLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        seatLbl.VerticalAlignment = VerticalAlignment.Center;
        head.AddChild(seatLbl);

        var bandLbl = new Label { Text = band.ToString() };
        bandLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        bandLbl.AddThemeColorOverride("font_color", BandColor(band));
        bandLbl.VerticalAlignment = VerticalAlignment.Center;
        head.AddChild(bandLbl);

        // Courtiers. Unknown courts show silhouettes — you learn the court
        // by attending it, not by opening a menu.
        foreach (var c in court.Courtiers)
        {
            string text;
            if (!court.HasContact)
            {
                text = $"  •  A figure of the court — {c.Office}";
            }
            else
            {
                string secret = c.SecretKnown ? "   [secret known]" : "";
                text = $"  •  {c.DisplayName} — {c.Office}, {c.Archetype}   " +
                       $"Regard {(c.Regard > 0 ? "+" : "")}{c.Regard}  ·  Influence {c.Influence}{secret}";
            }
            var row = new Label { Text = text };
            row.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            row.AddThemeColorOverride("font_color",
                court.HasContact ? UITheme.TextSecondary : UITheme.TextDim);
            v.AddChild(row);
        }

        if (court.Exposure > 0)
        {
            var exp = new Label { Text = $"  Exposure: {court.Exposure}/10" };
            exp.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            exp.AddThemeColorOverride("font_color",
                court.Exposure >= 3 ? UITheme.Danger : UITheme.TextDim);
            v.AddChild(exp);
        }

        // Mission status or dispatch flow.
        var mission = CouncilQueries.MissionAt(court.KingdomId);
        if (mission != null)
        {
            v.AddChild(BuildMissionStatusRow(cycle, mission));
        }
        else if (_dispatchKingdomId == court.KingdomId)
        {
            BuildDispatchFlow(v, save, cycle, court);
        }
        else
        {
            var btnRow = new HBoxContainer();
            v.AddChild(btnRow);
            bool capFull = cycle.Council.ActiveMissions.Count >= CouncilQueries.EnvoyCap(save);
            var dispatchBtn = new Button
            {
                Text = capFull ? "No envoys free" : "Dispatch Envoy",
                CustomMinimumSize = new Vector2(150, 30),
                Disabled = capFull,
            };
            dispatchBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            UITheme.ApplyButtonStyle(dispatchBtn, isPrimary: !capFull);
            string captured = court.KingdomId;
            dispatchBtn.Pressed += () =>
            {
                _dispatchKingdomId = captured;
                _selCompanionId = null;
                _selMissionId = null;
                _selTargetCourtierId = null;
                RefreshAll();
            };
            btnRow.AddChild(dispatchBtn);
        }

        return card;
    }

    private Control BuildMissionStatusRow(CycleState cycle, EnvoyMission mission)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);

        var envoy = cycle.Companions.Find(c => c.Id == mission.CompanionId);
        var def = CouncilMissions.Get(mission.MissionType);
        string status = mission.Recalled
            ? $"{envoy?.Name ?? mission.CompanionId} is travelling home ({mission.LunationsRemaining} lunation)"
            : $"{envoy?.Name ?? mission.CompanionId} — {def?.DisplayName ?? mission.MissionType}, " +
              $"{mission.LunationsRemaining} lunation(s) remaining";

        var lbl = new Label { Text = status, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        lbl.AddThemeColorOverride("font_color", UITheme.Success);
        lbl.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(lbl);

        if (!mission.Recalled)
        {
            var recallBtn = new Button { Text = "Recall", CustomMinimumSize = new Vector2(80, 26) };
            recallBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            UITheme.ApplyButtonStyle(recallBtn, isPrimary: false);
            recallBtn.Pressed += () =>
            {
                mission.Recalled = true;
                mission.LunationsRemaining = 1; // travel home
                SaveManager.Save();
                RefreshAll();
            };
            row.AddChild(recallBtn);
        }
        return row;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Dispatch flow (companion -> mission -> target -> confirm)
    // ══════════════════════════════════════════════════════════════════════

    private void BuildDispatchFlow(VBoxContainer v, GuildSaveData save,
                                   CycleState cycle, CourtState court)
    {
        v.AddChild(new HSeparator());

        // 1. Envoy selection.
        AddFlowLabel(v, "Envoy:");
        var compRow = new HBoxContainer();
        compRow.AddThemeConstantOverride("separation", 6);
        v.AddChild(compRow);

        bool anyCompanion = false;
        foreach (var c in cycle.Companions)
        {
            if (!c.IsRecruited || c.IsPermadead || CouncilQueries.IsOnMission(c.Id))
            {
                continue;
            }
            anyCompanion = true;
            bool inParty = save.ActivePartyCompanionIds.Contains(c.Id);
            AddSelectButton(compRow, c.Name + (inParty ? " (in party)" : ""),
                _selCompanionId == c.Id, () => { _selCompanionId = c.Id; RefreshAll(); });
        }
        if (!anyCompanion)
        {
            AddFlowLabel(v, "  No companions free to send.");
        }

        // 2. Mission selection.
        AddFlowLabel(v, "Mission:");
        var missionRow = new HBoxContainer();
        missionRow.AddThemeConstantOverride("separation", 6);
        v.AddChild(missionRow);

        foreach (var def in CouncilMissions.All)
        {
            bool locked = def.RequiresContact && !court.HasContact;
            string label = $"{def.DisplayName} ({def.Lunations}◐, {def.GoldCost}g)" +
                           (locked ? " — requires contact" : "");
            AddSelectButton(missionRow, label, _selMissionId == def.Id,
                () => { _selMissionId = def.Id; _selTargetCourtierId = null; RefreshAll(); },
                disabled: locked);
        }

        var selDef = _selMissionId != null ? CouncilMissions.Get(_selMissionId) : null;
        if (selDef != null)
        {
            var blurb = new Label { Text = "  " + selDef.Blurb, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            blurb.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            blurb.AddThemeColorOverride("font_color", UITheme.TextDim);
            v.AddChild(blurb);
        }

        // 3. Target courtier (gifts).
        if (selDef != null && selDef.NeedsTargetCourtier)
        {
            AddFlowLabel(v, "Recipient:");
            var targetRow = new HBoxContainer();
            targetRow.AddThemeConstantOverride("separation", 6);
            v.AddChild(targetRow);
            foreach (var c in court.Courtiers)
            {
                string cid = c.Id;
                AddSelectButton(targetRow, c.DisplayName, _selTargetCourtierId == cid,
                    () => { _selTargetCourtierId = cid; RefreshAll(); });
            }
        }

        // 4. Confirm / cancel.
        var confirmRow = new HBoxContainer();
        confirmRow.AddThemeConstantOverride("separation", 10);
        v.AddChild(confirmRow);

        bool ready = _selCompanionId != null && selDef != null &&
                     (!selDef.NeedsTargetCourtier || _selTargetCourtierId != null);
        bool affordable = selDef == null || save.Gold >= selDef.GoldCost;

        var confirmBtn = new Button
        {
            Text = selDef == null ? "Send" :
                (affordable ? $"Send ({selDef.GoldCost}g)" : $"Need {selDef.GoldCost}g"),
            CustomMinimumSize = new Vector2(140, 30),
            Disabled = !ready || !affordable,
        };
        confirmBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(confirmBtn, isPrimary: ready && affordable);
        confirmBtn.Pressed += () => ConfirmDispatch(save, cycle, court);
        confirmRow.AddChild(confirmBtn);

        var cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(90, 30) };
        cancelBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(cancelBtn, isPrimary: false);
        cancelBtn.Pressed += () => { _dispatchKingdomId = null; RefreshAll(); };
        confirmRow.AddChild(cancelBtn);
    }

    private void ConfirmDispatch(GuildSaveData save, CycleState cycle, CourtState court)
    {
        var def = CouncilMissions.Get(_selMissionId);
        if (def == null || _selCompanionId == null)
        {
            return;
        }
        if (save.Gold < def.GoldCost)
        {
            return;
        }
        if (cycle.Council.ActiveMissions.Count >= CouncilQueries.EnvoyCap(save))
        {
            return;
        }
        if (def.RequiresContact && !court.HasContact)
        {
            return;
        }

        save.Gold -= def.GoldCost;

        // Envoys leave the expedition pool: instant dispatch (v1.1 ruling).
        CompanionRoster.RemoveFromParty(_selCompanionId);

        cycle.Council.ActiveMissions.Add(new EnvoyMission
        {
            CompanionId = _selCompanionId,
            KingdomId = court.KingdomId,
            MissionType = def.Id,
            LunationsRemaining = def.Lunations,
            TargetCourtierId = _selTargetCourtierId ?? "",
            Recalled = false,
        });

        GD.Print($"[Council] Dispatched {_selCompanionId} to {court.KingdomId} " +
                 $"({def.Id}, {def.Lunations} lunation(s), {def.GoldCost}g).");

        _dispatchKingdomId = null;
        _selCompanionId = null;
        _selMissionId = null;
        _selTargetCourtierId = null;

        SaveManager.Save();
        RefreshAll();
    }

    // ── UI helpers ───────────────────────────────────────────────────────

    private void AddFlowLabel(VBoxContainer parent, string text)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        lbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        parent.AddChild(lbl);
    }

    private void AddSelectButton(HBoxContainer row, string text, bool selected,
                                 System.Action onPress, bool disabled = false)
    {
        var btn = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonPressed = selected,
            Disabled = disabled,
            CustomMinimumSize = new Vector2(0, 28),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: selected);
        btn.Pressed += () => onPress();
        row.AddChild(btn);
    }

    private static string SeatDisplay(CycleState cycle, CourtState court)
    {
        if (court.IsRegentCourt)
        {
            return court.RegentName;
        }
        if (cycle.Kingdoms.TryGetValue(court.KingdomId, out var ks) &&
            !string.IsNullOrEmpty(ks.ArchmageId))
        {
            var def = ArchmageRegistry.Get(ks.ArchmageId);
            return "Seat: " + (def?.DisplayName ?? ks.ArchmageId);
        }
        return "Seat: unknown";
    }

    private static Color BandColor(CourtStandingBand band) => band switch
    {
        CourtStandingBand.Trusted => UITheme.Gold,
        CourtStandingBand.Favored => UITheme.Success,
        CourtStandingBand.Welcome => UITheme.Violet,
        CourtStandingBand.Received => UITheme.TextSecondary,
        CourtStandingBand.Hostile => UITheme.Danger,
        _ => UITheme.TextDim, // Unknown
    };
}
