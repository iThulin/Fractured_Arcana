using Godot;
// ============================================================
// ExpeditionManager.FavorDeeds.cs: one call-in, two uses (2026-11-05)
//
// Purpose:        Slice PV2b-2 of the provinces spec, ruling PR44
//                 (docs/provinces_v1_7g_deeds_and_courts.md, sections 14
//                 and 17). Calling in a favor whose type has a deed opens a
//                 choice: its field effect, as before, or the favor spent as
//                 its deed in the court's realm (FavorDeeds). Each use says
//                 what it does, or why it cannot be had here and now. A
//                 favor without a deed (Arcane, Passage) goes straight to its
//                 field effect, as it always did. Either use spends the one
//                 favor, and both are had only inside the creditor's
//                 territory: a favor is called in where its court can hear.
// Layer:          Expedition (ExpeditionManager partial)
// Collaborators:  FavorDeeds (the deed), ExpeditionManager.cs (the field
//                 effect: CallInIneligibility, ExecuteCallIn; the ledger's
//                 wiring), LedgerPanel (its Call In button), DialogFit
// See:            docs/provinces_v1_7g_deeds_and_courts.md
// ============================================================
public partial class ExpeditionManager
{
    /// <summary>The ledger row's test: the Call In button shows while either use can be had.
    /// When neither can, the field effect's reason, and the deed's when it differs.</summary>
    private string CallInRowIneligibility(Favor f)
    {
        string field = CallInIneligibility(f);
        if (field == null || !FavorDeeds.HasDeed(f))
            return field;
        string deed = DeedIneligibility(f);
        if (deed == null)
            return null;
        return deed == field ? field : $"{field} As a deed: {deed}";
    }

    /// <summary>Why the favor cannot be spent as its deed here and now, or null.</summary>
    private string DeedIneligibility(Favor f)
    {
        if (f == null)
            return "No favor.";
        if (ExpeditionComplete)
            return "The expedition is over.";
        if (KingdomIdAt(_party.CurrentCoord) != f.KingdomId)
            return "Must be inside the creditor's territory.";
        if (!_window.TryLocalToWorld(_party.CurrentCoord, out int col, out int row))
            return "The party is off the map.";
        return FavorDeeds.Ineligibility(SaveManager.ActiveSave?.Cycle, f, col, row);
    }

    /// <summary>The choice of use: the field effect or the deed, each with what it does and,
    /// when it cannot be had, why.</summary>
    private void OpenCallInChoice(Favor f)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || f == null)
            return;
        var dlg = new AcceptDialog
        {
            Title = "Call in the favor",
            OkButtonText = "Not now",
            MinSize = new Vector2I(560, 280),
        };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        dlg.AddChild(box);

        string weight = f.IsMajor ? "major" : "minor";
        var intro = DialogFit.Wrap(new Label
        {
            Text = $"{CreditorLine(cycle, f)} owes the guild a {weight} {f.Type.ToLowerInvariant()} favor. "
                 + "It is spent one way or the other, once.",
        });
        intro.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        intro.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        box.AddChild(intro);
        box.AddChild(new HSeparator());

        AddCallInUse(box, dlg, "In the field", FieldUseLine(f), CallInIneligibility(f), () => CallInFieldEffect(f));

        string deedWhy = DeedIneligibility(f);
        string deedLine = FavorDeeds.Describe(f);
        if (deedWhy == null && _window.TryLocalToWorld(_party.CurrentCoord, out int col, out int row))
            deedLine = FavorDeeds.Preview(cycle, f, col, row);
        AddCallInUse(box, dlg, "As a deed", deedLine, deedWhy, () => SpendFavorAsDeed(f));

        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>One use in the choice: its name, what it does, why not (when it cannot be had),
    /// and the button that spends the favor on it.</summary>
    private static void AddCallInUse(VBoxContainer box, AcceptDialog dlg, string title, string line,
                                     string why, System.Action spend)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        box.AddChild(row);

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 2);
        row.AddChild(col);

        var head = new Label { Text = title };
        head.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        head.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        col.AddChild(head);

        var body = DialogFit.Wrap(new Label { Text = line }, DialogFit.RowTextWidth);
        body.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        body.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        col.AddChild(body);

        if (why != null)
        {
            var refusal = DialogFit.Wrap(new Label { Text = why }, DialogFit.RowTextWidth);
            refusal.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            refusal.AddThemeColorOverride("font_color", UITheme.Warning);
            col.AddChild(refusal);
        }

        var btn = new Button
        {
            Text = "Spend it",
            Disabled = why != null,
            CustomMinimumSize = new Vector2(110, 36),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        UITheme.ApplyButtonStyle(btn, isPrimary: why == null);
        btn.Pressed += () =>
        {
            dlg.QueueFree();
            spend?.Invoke();
        };
        row.AddChild(btn);
    }

    /// <summary>"Ilse Varn the Marshal of Westmarch", or the court when the courtier is gone.</summary>
    private static string CreditorLine(CycleState cycle, Favor f)
    {
        CourtierState courtier = null;
        if (cycle?.Council?.Courts != null && cycle.Council.Courts.TryGetValue(f.KingdomId, out var court) && court != null)
            courtier = court.GetCourtier(f.CourtierId);
        string realm = CouncilTick.CourtDisplayName(cycle, f.KingdomId);
        return courtier != null ? $"{ProvinceCourts.Who(courtier)} of {realm}" : $"The court of {realm}";
    }

    /// <summary>The field effect in one line, as ExecuteCallIn has it.</summary>
    private string FieldUseLine(Favor f)
    {
        if (f.IsMajor && f.Type != "Arcane")
            return "The courtier's own gift, sent under seal: an item with a story, and a watcher.";
        return f.Type switch
        {
            "Military" => "A patrol stands down for the rest of this expedition.",
            "Economic" => "The Steward's supply train refills a quarter of your stores.",
            "Intelligence" => "A chart packet: one unfound site in the realm revealed, the ground around it charted.",
            "Passage" => $"Papers of safe conduct: patrols leave you alone for {SafeConductSteps} steps.",
            "Political" => "An ill word travelling toward the court is buried before it arrives.",
            "Arcane" => "The Court Wizard's own service.",
            _ => "No field effect.",
        };
    }

    /// <summary>The deed: spend, chart what it revealed, consume, checkpoint.</summary>
    private void SpendFavorAsDeed(Favor f)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null)
            return;
        string reason = DeedIneligibility(f);
        if (reason != null)
        {
            ShowInfo(reason);
            return;
        }
        _window.TryLocalToWorld(_party.CurrentCoord, out int col, out int row);
        var outcome = FavorDeeds.Spend(cycle, f, col, row);
        // A site brought to light is charted around, as the Spymaster's chart packet does,
        // and a settlement that grants a staging point grants it.
        foreach (var poi in outcome.Revealed)
        {
            ChartRadius(poi.X, poi.Y, 1);
            if (poi.Kind == PoiKind.Settlement && poi.GrantsStaging)
                GrantStagingPointAtWorld(poi.X, poi.Y);
        }
        if (outcome.Revealed.Count > 0)
            RefreshWindowSilhouettes();
        ShowInfo(outcome.Message);
        CouncilLedger.Consume(council, f);
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty(); // favor consumption is a checkpoint
        _ledgerPanel.RefreshRows();
        UpdateUI();
    }
}
