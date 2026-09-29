using Godot;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusUndercroftPanel.cs
//
// Purpose:        The Undercroft room: the first player-facing surface
//                 for espionage (campus_building_upgrades_design_v1 §14).
//                 Until now every espionage verb (informants, the Veiled
//                 Concord's market, selling secrets, contracts) was reached
//                 only from debug buttons, while the monthly tick ran on
//                 its own. This panel is a thin front on the existing
//                 rules; it adds no rule of its own except the Whisper
//                 Market sale (Networks).
//
//                 Sections:
//                   Network     tier, informant and contract caps, Marked,
//                               the Concord's favour and standing.
//                   Informants  each asset with its cover and access, and
//                               its verbs: Exfiltrate; a Saboteur strikes;
//                               a Cutout forges a good word.
//                   Secrets     every courtier secret the guild holds: turn
//                               it into an informant, fence it to the
//                               Concord, or (Whisper Market) sell it to a
//                               rival court.
//                   The Concord contracts in flight, the Astrologer's bid to
//                               outbid, and the contract market by court
//                               and courtier.
// Layer:          UI (campus)
// Collaborators:  ShadowTick (TryTurnSecret), ShadowOps (Exfiltrate,
//                 SaboteurStrike, ForgeEcho), ShadowMarket (SellSecret,
//                 Commission*, Outbid), Networks (caps, doctrines, Whisper
//                 Market), CouncilTick.CourtDisplayName,
//                 CampusScreen / HomeBuildingPanelHost (hosting),
//                 CampusLocationRegistry ("undercroft" door).
// ============================================================

/// <summary>The Undercroft tab. Standard panel contract.</summary>
public sealed class CampusUndercroftPanel : CampusPanel
{
    private VBoxContainer _container;
    private Label _status;

    // Pickers survive a Refresh so a choice is not lost when the panel redraws.
    private string _pickedCourt = "";
    private string _pickedCourtier = "";
    private readonly Dictionary<string, string> _turnRole = new();
    private readonly Dictionary<string, string> _sellTo = new();

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(10);
        margins.AddChild(layout);

        AddSectionHeader(layout, "The Undercroft");
        var note = new Label
        {
            Text = "The guild's hidden network: informants embedded in the courts, the secrets they bring "
                 + "home, and the Veiled Concord, who sell what the guild cannot do itself. Every "
                 + "dealing leaves a shadow (Marked); too long a shadow and the Concord sells you out.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(note);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _status.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        layout.AddChild(_status);
        layout.AddChild(new HSeparator());

        _container = MakeVBox(10);
        layout.AddChild(_container);
    }

    public override void Refresh()
    {
        if (_container == null)
        {
            return;
        }
        foreach (var child in _container.GetChildren())
        {
            child.QueueFree();
        }
        var save = Ctx?.Save;
        var cycle = save?.Cycle;
        var council = cycle?.Council;
        if (council == null)
        {
            _container.AddChild(MakeStubLabel("No timeline in play."));
            return;
        }

        BuildNetwork(save, cycle, council);
        BuildInformants(cycle, council);
        BuildSecrets(save, cycle, council);
        BuildConcord(save, cycle, council);
    }

    // ── Network ──────────────────────────────────────────────────────────

    private void BuildNetwork(GuildSaveData save, CycleState cycle, CouncilState council)
    {
        AddSubheader("Network");
        int tier = Networks.UndercroftTier(save);
        int guildContracts = 0;
        foreach (var c in council.ConcordContracts)
        {
            if (!c.AgainstPlayer)
            {
                guildContracts++;
            }
        }
        AddLine(tier > 0 ? $"Undercroft tier {tier}." : "The Undercroft is not built: a minimal network is all the guild can run.",
            UITheme.TextPrimary);
        AddLine($"Informants {council.Informants.Count}/{Networks.InformantCap(save)}"
              + (Networks.SpyNetworkActive(save) ? " (the Spy Network runs two more)" : "")
              + $"   ·   Concord contracts {guildContracts}/{ShadowVocab.ContractCap(tier)}"
              + $"   ·   Marked {council.Marked}/{ShadowVocab.MarkedMax}", UITheme.TextSecondary);
        if (Networks.SilentFloorActive(save))
        {
            AddLine("The Silent Floor: Assassination is on the Concord's list, and every contract leaves 1 less Marked.", UITheme.Gold);
        }
        if (Networks.WhisperMarketActive(save))
        {
            AddLine("The Whisper Market: held secrets may be sold to rival courts.", UITheme.Gold);
        }
    }

    // ── Informants ───────────────────────────────────────────────────────

    private void BuildInformants(CycleState cycle, CouncilState council)
    {
        AddSubheader("Informants");
        if (council.Informants.Count == 0)
        {
            AddLine("No informants run yet. How to begin: turn a held secret below (pick a role, press Turn), "
                  + "or, once the Concord deals with you, buy \"Plant an asset\" in a court. An informant grows "
                  + "access the longer it stays; Saboteur strikes need access "
                  + $"{ShadowVocab.SaboteurStrikeMinAccess}, a Cutout's forgery access {ShadowVocab.ForgeEchoMinAccess}.",
                UITheme.TextDim);
            return;
        }
        foreach (var inf in new List<InformantState>(council.Informants))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            _container.AddChild(row);

            string on = "";
            if (!string.IsNullOrEmpty(inf.CourtierId) && council.Courts.TryGetValue(inf.KingdomId, out var court))
            {
                var courtier = court.GetCourtier(inf.CourtierId);
                if (courtier != null)
                {
                    on = $", on {courtier.DisplayName}";
                }
            }
            var text = new Label
            {
                Text = $"{inf.Role} in {CouncilTick.CourtDisplayName(cycle, inf.KingdomId)}{on}   ·   "
                     + $"cover {inf.Cover}/{ShadowVocab.CoverMax}   ·   access {inf.Access}/{ShadowVocab.AccessMax}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            text.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            row.AddChild(text);

            string id = inf.Id;
            string kid = inf.KingdomId;
            if (inf.Role == ShadowVocab.RoleSaboteur)
            {
                row.AddChild(Verb("Break the siege",
                    $"Spend {ShadowVocab.SaboteurStrikeCoverCost} cover to wreck a siege pressing this kingdom. "
                    + $"Needs access {ShadowVocab.SaboteurStrikeMinAccess} and a siege to break.",
                    () => ShadowOps.SaboteurStrike(Ctx.Save.Cycle, id, ShadowVocab.SabotageSiege)));
                row.AddChild(Verb("Stall the blight",
                    $"Spend {ShadowVocab.SaboteurStrikeCoverCost} cover to stall this kingdom's next corruption tick. "
                    + $"Needs access {ShadowVocab.SaboteurStrikeMinAccess}.",
                    () => ShadowOps.SaboteurStrike(Ctx.Save.Cycle, id, ShadowVocab.SabotageCorruption)));
            }
            else if (inf.Role == ShadowVocab.RoleCutout)
            {
                row.AddChild(Verb("Forge a good word",
                    $"Spend {ShadowVocab.ForgeEchoCoverCost} cover to fabricate a flattering report toward this court. "
                    + $"Needs access {ShadowVocab.ForgeEchoMinAccess}. If traced, it becomes a scandal.",
                    () => ShadowOps.ForgeEcho(Ctx.Save.Cycle, id, kid, positive: true)));
            }
            row.AddChild(Verb("Exfiltrate", "Pull the asset out before it burns; its access is banked as renown.",
                () => ShadowOps.Exfiltrate(Ctx.Save.Cycle, id)));
        }
    }

    // ── Secrets ──────────────────────────────────────────────────────────

    private void BuildSecrets(GuildSaveData save, CycleState cycle, CouncilState council)
    {
        AddSubheader("Held secrets");
        var contacted = new List<string>();
        foreach (var kv in council.Courts)
        {
            if (kv.Value != null && kv.Value.HasContact)
            {
                contacted.Add(kv.Key);
            }
        }
        contacted.Sort(string.CompareOrdinal);

        bool any = false;
        var kingdoms = new List<string>(council.Courts.Keys);
        kingdoms.Sort(string.CompareOrdinal);
        foreach (var kid in kingdoms)
        {
            var court = council.Courts[kid];
            if (court?.Courtiers == null)
            {
                continue;
            }
            foreach (var courtier in court.Courtiers)
            {
                if (courtier == null || !courtier.SecretKnown)
                {
                    continue;
                }
                any = true;
                string key = kid + "/" + courtier.Id;
                var row = new HFlowContainer();
                row.AddThemeConstantOverride("h_separation", 8);
                row.AddThemeConstantOverride("v_separation", 4);
                _container.AddChild(row);

                var text = new Label { Text = $"{courtier.DisplayName}, {courtier.Office} of {CouncilTick.CourtDisplayName(cycle, kid)}" };
                text.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                row.AddChild(text);

                // Turn into an informant, in the chosen role.
                var roles = new[] { ShadowVocab.RoleCutout, ShadowVocab.RoleWatcher, ShadowVocab.RoleSaboteur };
                var rolePick = new OptionButton { CustomMinimumSize = new Vector2(120, 28) };
                rolePick.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                _turnRole.TryGetValue(key, out string chosenRole);
                for (int i = 0; i < roles.Length; i++)
                {
                    rolePick.AddItem(roles[i], i);
                    if (roles[i] == chosenRole)
                    {
                        rolePick.Select(i);
                    }
                }
                rolePick.ItemSelected += idx => _turnRole[key] = roles[(int)idx];
                row.AddChild(rolePick);
                string cid = courtier.Id;
                string k = kid;
                row.AddChild(Verb("Turn", "Use the secret as leverage: the courtier becomes the guild's informant.", () =>
                {
                    _turnRole.TryGetValue(key, out string role);
                    var inf = ShadowTick.TryTurnSecret(Ctx.Save.Cycle, k, cid, role ?? ShadowVocab.RoleCutout);
                    return inf != null
                        ? ShadowMarketResult.Pass($"{courtier.DisplayName} now reports to the guild as a {inf.Role}.")
                        : ShadowMarketResult.Fail($"Could not turn them (the network is full at {Networks.InformantCap(Ctx.Save)}, or they already report).");
                }));

                if (council.ConcordContacted)
                {
                    row.AddChild(Verb($"Fence to the Concord (+{ShadowVocab.FavorSellSecret} favour)",
                        "Sell the secret to the Veiled Concord. The sale may be traced.",
                        () => ShadowMarket.SellSecret(Ctx.Save.Cycle, k, cid)));
                }

                if (Networks.WhisperMarketActive(save))
                {
                    var buyers = contacted.FindAll(b => b != kid);
                    if (buyers.Count > 0)
                    {
                        var buyerPick = new OptionButton { CustomMinimumSize = new Vector2(160, 28) };
                        buyerPick.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                        _sellTo.TryGetValue(key, out string chosenBuyer);
                        for (int i = 0; i < buyers.Count; i++)
                        {
                            buyerPick.AddItem(CouncilTick.CourtDisplayName(cycle, buyers[i]), i);
                            if (buyers[i] == chosenBuyer)
                            {
                                buyerPick.Select(i);
                            }
                        }
                        if (string.IsNullOrEmpty(chosenBuyer) || !buyers.Contains(chosenBuyer))
                        {
                            _sellTo[key] = buyers[0];
                        }
                        buyerPick.ItemSelected += idx => _sellTo[key] = buyers[(int)idx];
                        row.AddChild(buyerPick);
                        row.AddChild(Verb($"Sell ({Networks.WhisperPrice(courtier)}g)",
                            "The Whisper Market: sell to a rival court for gold and favour. The court it came from grows warier.",
                            () => Networks.SellSecretToCourt(Ctx.Save.Cycle, Ctx.Save, k, cid, _sellTo.GetValueOrDefault(key, ""))));
                    }
                }
            }
        }
        if (!any)
        {
            AddLine("The guild holds no courtier's secret yet. How to begin: open the Council, choose a court, "
                  + "and send an envoy on Gather Intelligence. On the mission's second moon it usually uncovers "
                  + "one courtier's secret, which appears here. Cutout informants and a Concord theft bring more.",
                UITheme.TextDim);
        }
    }

    // ── The Concord ──────────────────────────────────────────────────────

    private void BuildConcord(GuildSaveData save, CycleState cycle, CouncilState council)
    {
        AddSubheader("The Veiled Concord");
        if (!council.ConcordContacted)
        {
            AddLine("The Concord has not made contact yet. How to begin: the Veiled Concord keeps a few unmarked "
                  + "doors across the kingdoms. Gather Intelligence at a court can reveal hidden sites; walk a "
                  + "field party onto a Concord door and they will deal with you. Their market then opens here, "
                  + "paid in favour (fence a secret to earn it).",
                UITheme.TextDim);
            return;
        }
        var band = ShadowVocab.EffectiveBand(council.ConcordContacted, council.ConcordDealings);
        AddLine($"Favour {council.ConcordFavor}   ·   standing {band}   ·   dealings {council.ConcordDealings}", UITheme.TextSecondary);

        foreach (var c in council.ConcordContracts)
        {
            string target = CouncilTick.CourtDisplayName(cycle, c.TargetKingdomId);
            AddLine(c.AgainstPlayer
                    ? $"Against the guild: the Astrologer's {c.ContractType} lands in {c.LunationsRemaining} moon(s)."
                    : $"In flight: {c.ContractType} in {target}, {c.LunationsRemaining} moon(s).",
                c.AgainstPlayer ? UITheme.Danger : UITheme.TextSecondary);
            if (c.AgainstPlayer)
            {
                _container.AddChild(Verb($"Outbid the Astrologer (more than {ShadowVocab.AstrologerBidFavor} favour)",
                    "Buy the contract back with hoarded favour.",
                    () => ShadowMarket.Outbid(Ctx.Save.Cycle)));
            }
        }

        // Pick a court, then (for the named work) a courtier.
        var kingdoms = new List<string>(council.Courts.Keys);
        kingdoms.Sort(string.CompareOrdinal);
        if (kingdoms.Count == 0)
        {
            return;
        }
        if (!kingdoms.Contains(_pickedCourt))
        {
            _pickedCourt = kingdoms[0];
            _pickedCourtier = "";
        }
        var pickRow = new HBoxContainer();
        pickRow.AddThemeConstantOverride("separation", 8);
        _container.AddChild(pickRow);
        var courtPick = new OptionButton { CustomMinimumSize = new Vector2(200, 30) };
        courtPick.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        for (int i = 0; i < kingdoms.Count; i++)
        {
            courtPick.AddItem(CouncilTick.CourtDisplayName(cycle, kingdoms[i]), i);
            if (kingdoms[i] == _pickedCourt)
            {
                courtPick.Select(i);
            }
        }
        courtPick.ItemSelected += idx =>
        {
            _pickedCourt = kingdoms[(int)idx];
            _pickedCourtier = "";
            Refresh();
        };
        pickRow.AddChild(courtPick);

        var courtiers = council.Courts[_pickedCourt]?.Courtiers ?? new List<CourtierState>();
        var courtierPick = new OptionButton { CustomMinimumSize = new Vector2(220, 30) };
        courtierPick.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        for (int i = 0; i < courtiers.Count; i++)
        {
            courtierPick.AddItem($"{courtiers[i].DisplayName} ({courtiers[i].Office})", i);
            if (courtiers[i].Id == _pickedCourtier)
            {
                courtierPick.Select(i);
            }
        }
        if (courtiers.Count > 0 && courtiers.Find(x => x.Id == _pickedCourtier) == null)
        {
            _pickedCourtier = courtiers[0].Id;
        }
        courtierPick.ItemSelected += idx => _pickedCourtier = courtiers[(int)idx].Id;
        pickRow.AddChild(courtierPick);

        var market = new HFlowContainer();
        market.AddThemeConstantOverride("h_separation", 8);
        market.AddThemeConstantOverride("v_separation", 6);
        _container.AddChild(market);
        market.AddChild(Verb($"Plant an asset ({ShadowVocab.FavorCostPlantAsset})", "An informant planted in the chosen court.",
            () => ShadowMarket.CommissionPlantAsset(Ctx.Save.Cycle, _pickedCourt)));
        market.AddChild(Verb($"Buy intelligence ({ShadowVocab.FavorCostPurchaseIntel})", "The court's ground and sites, charted for you.",
            () => ShadowMarket.CommissionPurchaseIntel(Ctx.Save.Cycle, _pickedCourt)));
        market.AddChild(Verb($"Sabotage a siege ({ShadowVocab.FavorCostSabotage})", "Wreck a siege pressing the chosen kingdom.",
            () => ShadowMarket.CommissionSabotage(Ctx.Save.Cycle, _pickedCourt, ShadowVocab.SabotageSiege)));
        market.AddChild(Verb($"Stall the blight ({ShadowVocab.FavorCostSabotage})", "Delay the chosen kingdom's corruption.",
            () => ShadowMarket.CommissionSabotage(Ctx.Save.Cycle, _pickedCourt, ShadowVocab.SabotageCorruption)));
        market.AddChild(Verb($"Extraction ({ShadowVocab.FavorCostExtraction})", "Get an imprisoned envoy out.",
            () => ShadowMarket.CommissionExtraction(Ctx.Save.Cycle, _pickedCourt)));
        market.AddChild(Verb($"Steal a secret ({ShadowVocab.FavorCostTheft})", "Take the chosen courtier's secret.",
            () => ShadowMarket.CommissionTheft(Ctx.Save.Cycle, _pickedCourt, _pickedCourtier)));
        if (Networks.SilentFloorActive(save))
        {
            market.AddChild(Verb($"Assassination ({ShadowVocab.FavorCostAssassination})", "The Silent Floor: remove the chosen courtier. The court will investigate its dead.",
                () => ShadowMarket.CommissionAssassination(Ctx.Save.Cycle, _pickedCourt, _pickedCourtier)));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>A button that runs an espionage verb and shows its result line.</summary>
    private Button Verb(string text, string tooltip, System.Func<ShadowMarketResult> act)
    {
        var btn = new Button { Text = text, TooltipText = tooltip, CustomMinimumSize = new Vector2(0, 28) };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        btn.Pressed += () =>
        {
            var result = act();   // ShadowMarketResult is a struct: never null
            ShowStatus(result.Message, result.Ok);
            if (result.Ok)
            {
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
                Ctx?.RefreshGold?.Invoke();
            }
            Refresh();
        };
        return btn;
    }

    private void ShowStatus(string message, bool ok)
    {
        if (_status == null)
        {
            return;
        }
        _status.Text = message ?? "";
        _status.Visible = !string.IsNullOrEmpty(message);
        _status.AddThemeColorOverride("font_color", ok ? UITheme.Success : UITheme.Danger);
    }

    private void AddSubheader(string text)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        lbl.AddThemeColorOverride("font_color", UITheme.Gold);
        _container.AddChild(lbl);
    }

    private void AddLine(string text, Color color)
    {
        var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        _container.AddChild(lbl);
    }
}
