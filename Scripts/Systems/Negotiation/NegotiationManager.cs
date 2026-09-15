using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// NegotiationManager.cs
//
// Purpose:        Full-screen negotiation scene controller (v2).
//                 Reads NegotiationContext input, drives the v2
//                 NegotiationState (term board, two-sided pools,
//                 stances, squeeze), renders the verb band (Sway /
//                 Force / Offer / Read / Bide chip rack with
//                 timing glyphs and a shared context line, per
//                 negotiation_narrative_spec_v1 addendum), and
//                 writes results back to the context for
//                 EncounterRouter to pick up post-scene.
// Layer:          UI
// Collaborators:  NegotiationContext.cs (I/O),
//                 NegotiationState.cs (state machine),
//                 NegotiationBarks.cs (spoken-move content),
//                 NegotiationEncounterLoader.cs (data source),
//                 UITheme.cs (negotiation panel styling)
// See:            README §6 (Negotiation);
//                 negotiation_redesign_v1.md (approved package:
//                 Core + Modules A/B/D)
// ============================================================

/// <summary>Full-screen negotiation scene controller. Owns the on-screen
/// widgets, delegates rules to <see cref="NegotiationState"/>, and writes the
/// outcome back to <see cref="NegotiationContext"/> for the run manager.</summary>
public partial class NegotiationManager : Control
{
    private NegotiationState _state;
    private NegotiationEncounterData _data;

    // ── UI references ────────────────────────────────────────────────────
    private Label _titleLabel;
    private NegotiationPortrait _portrait;   // Phase 4: stance-keyed portrait
    private Label _npcNameLabel;
    private Label _stanceLabel;          // Module A: the tell, under the portrait

    private HBoxContainer _tensionBar;
    private Label _tensionLabel;
    private HBoxContainer _npcPoolRow;   // v2: their leverage as mini token chips

    private ScrollContainer _termsScroll;
    private HBoxContainer _termsRow;           // the clause cards (prototype style)
    private string _selectedTermId = "";       // click-to-target selection
    private string _unrollTermId = "";         // scroll-open animation, one build
    private VBoxContainer _actionsContainer;   // the verb band + context line
    private Label _contextSpoken;              // hovered chip's spoken line
    private Label _contextFx;                  // and its mechanical read
    private VBoxContainer _schoolMoveContainer; // Phase 5: signature move row
    private Label _intentLabel;                // Embassy tier-2 intent briefing
    private int _embassyTier = 0;
    private RichTextLabel _logLabel;

    private Button _shakeButton;
    private Button _walkAwayButton;
    private Label _dealPreviewLabel;           // live "a handshake signs for…" readout
    private Label _unreadRiskLabel;            // §4b: "N clauses unread" beside it

    private Panel _squeezePanel;               // Module B modal
    private Label _squeezeLabel;
    private Button _squeezeConcedeBtn;
    private Button _squeezeHoldBtn;
    private Button _squeezeWithdrawBtn;
    private NegotiationState.SqueezeOffer _pendingSqueeze;

    private Panel _resultPanel;
    private VBoxContainer _resultContent;      // the receipt, rebuilt at resolution
    private Button _continueButton;

    private ColorRect[] _tensionSteps = new ColorRect[10];
    private HBoxContainer _patienceBar;        // pip bar: their remaining patience
    private Label _patienceCaption;

    // Log aging: everything before your latest action renders dim, so the
    // results of the last exchange pop. Entries carry their reading tier:
    // Dialogue is the reading layer, Scene is stage direction, Detail is the
    // sim readout (hidden unless _showDetails).
    private ScrollContainer _logScroll;
    private readonly List<(string Text, NegotiationLogKind Kind)> _logHistory = new();
    private readonly List<(string Text, NegotiationLogKind Kind)> _logRecent = new();
    private CheckButton _detailsToggle;        // "Table details": sim readout in the log
    private bool _showDetails = false;

    public override void _Ready()
    {
        BuildUI();
        InitializeNegotiation();
    }

    private void InitializeNegotiation()
    {
        string encounterId = NegotiationContext.EncounterId;
        _data = NegotiationEncounterLoader.Load(encounterId);

        if (_data == null)
        {
            GD.PrintErr($"NegotiationScene: Could not load encounter '{encounterId}'");
            ReturnToOverworld();
            return;
        }

        var party = CompanionRoster.GetActiveParty();
        var school = PlayerSession.SelectedSchool;
        // Starting reputation: kingdom NPCs derive it from court standing
        // (single source of truth per the standing ruling); non-kingdom
        // factions keep the FactionReputation ledger. OriginKingdomId is
        // set at trigger time; the negotiation scene can't resolve the
        // tile's kingdom on its own.
        int factionRep = 0;
        var cycle = SaveManager.ActiveSave?.Cycle;
        string originKingdom = NegotiationContext.OriginKingdomId;
        if (cycle != null && !string.IsNullOrEmpty(originKingdom) &&
            cycle.Council != null && cycle.Council.Courts.ContainsKey(originKingdom))
        {
            factionRep = CouncilQueries.NegotiationReputationFor(cycle, originKingdom);
        }
        else if (SaveManager.ActiveSave != null &&
                 !string.IsNullOrEmpty(_data.FactionId) &&
                 SaveManager.ActiveSave.FactionReputation.TryGetValue(_data.FactionId, out int rep))
        {
            factionRep = rep;
        }

        GD.Print($"[Negotiation] origin='{NegotiationContext.OriginKingdomId}', " +
                 $"factionRep={factionRep}, startTension will reflect it.");

        // S3 (Beguile): an armed charm opens the table a band more favorable.
        // Applied to the encounter's StartingTension before state init so every
        // downstream read (zone, log) sees the shifted opening. Consumed here.
        if (NegotiationContext.TensionShift != 0)
        {
            _data.StartingTension = Mathf.Max(0, _data.StartingTension - NegotiationContext.TensionShift);
            GD.Print($"[Negotiation] Beguile: starting tension eased by {NegotiationContext.TensionShift}.");
            NegotiationContext.TensionShift = 0;
        }

        // ── The chronicle read-back (spec §6b/§6c) ──────────────────────
        // The counterpart is the same person every visit: within a cycle
        // they remember the last table; across an unmake only the player's
        // eternal DealRecords do. One pass over the ledger sorts history
        // into this-life continuity and other-life familiarity.
        DealRecord lastThisCycle = null;
        int priorLifeTables = 0;
        bool priorLifeCollapse = false;
        var chronCycle = SaveManager.ActiveSave?.Cycle;
        var chronLedger = SaveManager.ActiveSave?.Ledger;
        if (chronCycle != null && chronLedger != null)
        {
            foreach (var r in chronLedger.DealRecords)
            {
                if (r.EncounterId != _data.Id)
                    continue;
                if (r.CycleNumber == chronCycle.CycleNumber)
                    lastThisCycle = r;   // records append in order; last wins
                else if (r.CycleNumber < chronCycle.CycleNumber)
                {
                    priorLifeTables++;
                    if (r.Outcome == "Collapsed")
                        priorLifeCollapse = true;
                }
            }
        }

        // §6b: how the last table this life ended shifts how this one opens.
        // Applied pre-init like Beguile; clamped short of instant collapse.
        if (lastThisCycle != null)
        {
            int shift = lastThisCycle.Outcome switch
            {
                "Signed" => lastThisCycle.Stars >= 4 ? -1 : 0,
                "Collapsed" => 2,
                _ => 1,   // WalkedAway, TheyLeft
            };
            if (shift != 0)
            {
                _data.StartingTension = Mathf.Clamp(_data.StartingTension + shift, 1, 9);
                GD.Print($"[Negotiation] Continuity: last outcome " +
                         $"{lastThisCycle.Outcome} ({lastThisCycle.Stars}★) " +
                         $"shifts opening tension by {shift:+0;-0}.");
            }
        }

        // §6d: a continued campaign reaches the table. Year 2+ swaps in the
        // late-war variants where a table authored them.
        if (chronCycle != null && chronCycle.CampaignYear >= 2)
        {
            if (!string.IsNullOrEmpty(_data.OpeningTextLate))
                _data.OpeningText = _data.OpeningTextLate;
            if (!string.IsNullOrEmpty(_data.DialogueWalkawayLate))
                _data.DialogueWalkaway = _data.DialogueWalkawayLate;
        }

        // S4 (overworld_spell_system §11): the social route to spells. The
        // loader now hands out PER-TABLE CLONES (cache stays pristine), so
        // injected terms can't leak across tables; the strip below stays as
        // a guard against authored stale tuition. Then maybe inject a fresh
        // offer for a learnable the guild lacks.
        // Granted only if the deal closes in the Cordial zone. The term's
        // text says so up front (G5), and NegotiationState enforces it.
        _data.Terms.RemoveAll(t => t.Id == "spell_tuition");

        // Supply-cost clauses the guild can't cover come off the table before
        // the board is built. Settlement floors the treasury at 0, so without
        // this strip a "sell 15 supplies" term pays its gold in full while
        // delivering supplies that don't exist: free gold on an empty larder.
        // (Conservative: pending expedition SuppliesEarned deliberately don't
        // count; they're unbanked and may be forfeited.)
        int suppliesOnHand = SaveManager.ActiveSave?.Supplies ?? 0;
        int strippedSupply = _data.Terms.RemoveAll(
            t => t.SuppliesDelta < 0 && suppliesOnHand < -t.SuppliesDelta);
        if (strippedSupply > 0)
            GD.Print($"[Negotiation] Stripped {strippedSupply} supply-cost term(s): " +
                     $"stores too low ({suppliesOnHand}).");

        // Supply-lines intel (supply_cache spec v1.1): kingdom NPCs can sell
        // the locations of their homeland's caches, diplomacy as a discovery
        // channel. Offered only while the kingdom still has undiscovered ones.
        _data.Terms.RemoveAll(t => t.Id == "supply_lines_intel");
        var intelCycle = SaveManager.ActiveSave?.Cycle;
        string intelKingdom = NegotiationContext.OriginKingdomId;
        if (intelCycle != null && !string.IsNullOrEmpty(intelKingdom) &&
            intelCycle.Kingdoms != null && intelCycle.Kingdoms.ContainsKey(intelKingdom) &&
            SupplyCacheSystem.HasUndiscoveredCache(intelCycle, intelKingdom) &&
            GD.Randf() < 0.4f)
        {
            _data.Terms.Add(new DealTerm
            {
                Id = "supply_lines_intel",
                ShortName = "supply charts",
                Description = "They mark the region's supply caches on your map: " +
                              "every depot their people draw from.",
                FavorPlayer = true,
                RevealsSupplyCaches = true,
                Weight = 2,
            });
            GD.Print("[Negotiation] Supply-lines intel on the table.");
        }

        var grimoire = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (grimoire != null)
        {
            float offerChance = _data.Archetype is NpcArchetypeType.Merchant or NpcArchetypeType.Scholar
                ? SpellAcquisition.DealOfferChanceKeen
                : SpellAcquisition.DealOfferChanceOther;
            if (GD.Randf() < offerChance)
            {
                string offerId = SpellAcquisition.PickNegotiationSpell(grimoire);
                var offerDef = OverworldSpellRegistry.Get(offerId);
                if (offerDef != null)
                {
                    _data.Terms.Add(new DealTerm
                    {
                        Id = "spell_tuition",
                        ShortName = "tuition",
                        Description = $"They offer to teach {offerDef.Name}: " +
                                      "theirs if the deal closes cordially.",
                        FavorPlayer = true,
                        SpellId = offerDef.Id,
                    });
                    GD.Print($"[Negotiation] Tuition on the table: '{offerDef.Id}'.");
                }
            }
        }

        _state = new NegotiationState();
        _state.OnTensionChanged += OnTensionChanged;
        _state.OnLogEntry += AppendLog;
        _state.OnResolved += OnNegotiationResolved;
        _state.OnStanceChanged += RefreshStance;

        // Court patron (C5): a courtier secured as the guild's Patron at the
        // origin kingdom's court lends backing at the table: +1 leverage token
        // of THEIR archetype's type (§ Court a Courtier), so who you courted
        // shapes the bonus. Reuses the origin court resolved above for factionRep.
        // Dormant until the Court-a-Courtier mission writes PatronCourtierId.
        LeverageToken patronToken = LeverageToken.Connections;
        int patronTokens = 0;
        if (cycle?.Council != null && !string.IsNullOrEmpty(originKingdom) &&
            cycle.Council.Courts.TryGetValue(originKingdom, out var originCourt) &&
            !string.IsNullOrEmpty(originCourt.PatronCourtierId))
        {
            var patron = originCourt.GetCourtier(originCourt.PatronCourtierId);
            if (patron != null)
            {
                patronTokens = 1;
                patronToken = PatronTokenForArchetype(patron.Archetype);
            }
        }

        _state.Initialize(_data, school, party, factionRep, patronToken, patronTokens);

        // §6c then §6b: what you remember from other lives, then how they
        // remember you from this one. Familiarity precedes the dossier so
        // free knowledge lands before bought knowledge (both idempotent).
        if (priorLifeTables > 0)
            _state.ApplyChronicleFamiliarity(priorLifeTables, priorLifeCollapse);
        if (lastThisCycle != null)
            _state.ApplyContinuity(lastThisCycle.Outcome, lastThisCycle.Stars);

        // §6c: a completed dossier on this kingdom's archmage arms an extra
        // argument at their subjects' tables.
        if (cycle != null && !string.IsNullOrEmpty(originKingdom) &&
            cycle.Kingdoms != null &&
            cycle.Kingdoms.TryGetValue(originKingdom, out var seamKingdom) &&
            !string.IsNullOrEmpty(seamKingdom.ArchmageId))
        {
            var seamDef = ArchmageRegistry.Get(seamKingdom.ArchmageId);
            if (seamDef != null && seamDef.WeaknessHints.Count > 0 &&
                DossierService.HintsRevealed(SaveManager.ActiveSave, seamKingdom.ArchmageId)
                    >= seamDef.WeaknessHints.Count)
                _state.ApplyDossierSeam();
        }

        // Phase 5 building hooks: Courier Station dossier + Embassy briefing.
        int courierTier = 0;
        _embassyTier = 0;
        if (SaveManager.ActiveSave != null)
        {
            foreach (var b in SaveManager.ActiveSave.Buildings)
            {
                if (b.Id == "courier_station")
                    courierTier = b.Tier;
                else if (b.Id == "embassy")
                    _embassyTier = b.Tier;
            }
        }
        if (courierTier > 0)
            _state.ApplyCourierDossier(courierTier);

        GD.Print($"[Negotiation] opened at tension={_state.Tension} " +
                 $"(zone {_state.Zone}), from factionRep={factionRep}, " +
                 $"encounter.StartingTension={_data.StartingTension}.");

        _titleLabel.Text = _data.Title;
        // §6c: the chronicle glyph. The face is familiar even when theirs
        // has never seen yours.
        _npcNameLabel.Text = priorLifeTables > 0 ? $"❖ {_data.NpcName}" : _data.NpcName;
        if (priorLifeTables > 0)
        {
            _npcNameLabel.MouseFilter = MouseFilterEnum.Pass;
            _npcNameLabel.TooltipText = priorLifeTables == 1
                ? "The chronicle remembers this table from another life."
                : $"The chronicle remembers this table from {priorLifeTables} other lives.";
        }

        _portrait.Setup(_data.Archetype);
        _portrait.SetZone(_state.Zone);
        _portrait.SetStance(_state.Stance);

        RefreshAll();
    }

    /// <summary>Maps a Patron courtier's archetype to the leverage token they
    /// lend at the table (§ Court a Courtier: "+1 token of their archetype's
    /// type"). Archetype strings are CourtVocab ids, shared verbatim with the
    /// negotiation NPC archetypes; unknown/blank falls back to Connections.</summary>
    private static LeverageToken PatronTokenForArchetype(string archetype)
    {
        switch (archetype)
        {
            case "Merchant":
                return LeverageToken.Offering;
            case "Commander":
                return LeverageToken.Intimidate;
            case "Scholar":
                return LeverageToken.Insight;
            case "Idealist":
                return LeverageToken.Charm;
            case "Opportunist":
                return LeverageToken.Persuade;
            case "Survivor":
                return LeverageToken.Connections;
            default:
                return LeverageToken.Connections;
        }
    }

    // ── UI building ──────────────────────────────────────────────────────

    private void BuildUI()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;

        // Background
        var bg = new ColorRect
        {
            Color = UITheme.NegotiationBg,
            AnchorRight = 1f,
            AnchorBottom = 1f,
        };
        AddChild(bg);

        // Single-screen vertical layout (prototype composition):
        //   title / [portrait | table log + meters] / clause cards / actions.
        var root = new VBoxContainer
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 20,
            OffsetTop = 88,     // clear the persistent HUD bar (Gold / Lunation)
            OffsetRight = -20,
            OffsetBottom = -14,
        };
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        // ── Title (one line) ────────────────────────────────────────────
        _titleLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _titleLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationNpcFontSize);
        _titleLabel.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        root.AddChild(_titleLabel);

        // ── TOP STRIP: centered pair, NPC card column | conversation ────
        // The page's ONLY flexible region: every other section sizes to its
        // content, and the conversation log (the one scrollable thing on
        // screen) absorbs whatever height is left.
        var topStrip = new HBoxContainer
        {
            SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill,
        };
        topStrip.AddThemeConstantOverride("separation", 22);
        root.AddChild(topStrip);

        topStrip.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.Expand });

        // NPC column: the face of the table, with its dials right under it.
        var npcCol = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        npcCol.AddThemeConstantOverride("separation", 5);
        topStrip.AddChild(npcCol);

        var portraitWrap = new CenterContainer();
        _portrait = new NegotiationPortrait();
        portraitWrap.AddChild(_portrait);
        npcCol.AddChild(portraitWrap);

        _npcNameLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _npcNameLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationNpcFontSize);
        _npcNameLabel.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        npcCol.AddChild(_npcNameLabel);

        // Stance tell (Module A): the read, right under the face.
        _stanceLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _stanceLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationDetailFontSize);
        _stanceLabel.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        npcCol.AddChild(_stanceLabel);

        // Tension: compact, part of the NPC card.
        var tensionHead = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        tensionHead.AddThemeConstantOverride("separation", 8);
        var tensionTag = MakeTinyLabel("TENSION", Colors.White);
        tensionTag.VerticalAlignment = VerticalAlignment.Center;
        tensionHead.AddChild(tensionTag);
        _tensionLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
        _tensionLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        tensionHead.AddChild(_tensionLabel);
        npcCol.AddChild(tensionHead);

        _tensionBar = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 14),
            TooltipText = "1-3 Cordial · 4-7 Strained · 8-10 Hostile",
        };
        _tensionBar.AddThemeConstantOverride("separation", 3);
        npcCol.AddChild(_tensionBar);

        for (int i = 0; i < 10; i++)
        {
            var step = new ColorRect { CustomMinimumSize = new Vector2(22, 14) };
            _tensionSteps[i] = step;
            _tensionBar.AddChild(step);
        }

        // Their patience: a depleting pip bar; one pip = one more move.
        _patienceCaption = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            TooltipText = "Every action except a Patience token spends one pip.",
        };
        _patienceCaption.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _patienceCaption.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        npcCol.AddChild(_patienceCaption);

        _patienceBar = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 8),
        };
        _patienceBar.AddThemeConstantOverride("separation", 3);
        npcCol.AddChild(_patienceBar);

        // Their pool, as mini tokens (Offerings you hand over land here).
        _npcPoolRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            TooltipText = "Their leverage. They spend it against you. Offerings you hand over feed it.",
        };
        _npcPoolRow.AddThemeConstantOverride("separation", 10);
        npcCol.AddChild(_npcPoolRow);

        // (The NPC-intent line lives in the conversation header row below;
        // an always-on line here pushed the action row off short screens.)

        // The conversation column: width capped so the NPC card sits near
        // the middle of the screen instead of shunted to a corner. Header row
        // carries the "Table details" toggle (sim readout off by default;
        // the numbers live on the board).
        var logCol = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(700, 0),
            SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill,
        };
        logCol.AddThemeConstantOverride("separation", 2);
        topStrip.AddChild(logCol);

        var logHeaderRow = new HBoxContainer();
        // The intent tell doubles as the conversation header: zero extra
        // rows on screen (soft read for everyone; Embassy tier 2 upgrades
        // it to the precise briefing via RefreshIntent).
        _intentLabel = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Bottom,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false,
        };
        _intentLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
        _intentLabel.AddThemeColorOverride("font_color", UITheme.ZoneStrainedLabel);
        logHeaderRow.AddChild(_intentLabel);

        _detailsToggle = new CheckButton
        {
            Text = "Table details",
            ButtonPressed = false,
            TooltipText = "Also show the mechanical readout: clause slides, " +
                          "tension numbers, turn stamps.",
        };
        _detailsToggle.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _detailsToggle.Toggled += pressed => { _showDetails = pressed; RenderLog(); };
        logHeaderRow.AddChild(_detailsToggle);
        logCol.AddChild(logHeaderRow);

        var logPanel = new PanelContainer
        {
            SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill,
        };
        var logStyle = new StyleBoxFlat
        {
            BgColor = UITheme.BgDeep,
            BorderColor = UITheme.VioletDim,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
        };
        logPanel.AddThemeStyleboxOverride("panel", logStyle);
        logCol.AddChild(logPanel);

        _logScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        logPanel.AddChild(_logScroll);

        _logLabel = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill,
        };
        _logLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationDetailFontSize);
        _logScroll.AddChild(_logLabel);

        topStrip.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.Expand });

        // ── THE DEAL (clause cards) ──────────────────────────────────────
        var termsHeader = new Label { Text = "The Deal on the Table. Click a clause to target it, then make your move" };
        termsHeader.AddThemeFontSizeOverride("font_size", UITheme.NegotiationHeaderFontSize);
        termsHeader.AddThemeColorOverride("font_color", Colors.White);
        root.AddChild(termsHeader);

        // Sizes to its content: with vertical scroll Disabled, a
        // ScrollContainer's minimum height tracks its tallest child, so the
        // strip is always exactly as tall as the cards: never a vertical
        // scrollbar, never a clipped position label. (Horizontal stays Auto
        // purely as a fallback for very wide authored tables.)
        _termsScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(_termsScroll);
        var termsCenter = new CenterContainer
        {
            SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill,
        };
        _termsScroll.AddChild(termsCenter);
        _termsRow = new HBoxContainer();
        _termsRow.AddThemeConstantOverride("separation", 10);
        termsCenter.AddChild(_termsRow);

        // ── YOUR MOVE (the verb band) ───────────────────────────────────
        var actionsHeader = new Label { Text = "Your Move" };
        actionsHeader.AddThemeFontSizeOverride("font_size", UITheme.NegotiationHeaderFontSize);
        actionsHeader.AddThemeColorOverride("font_color", Colors.White);
        root.AddChild(actionsHeader);

        // Sizes to its content, same trick as the clause strip: both scroll
        // directions Disabled → minimum tracks the compact verb band, so
        // every token is always on screen and the only scrollbar on the page
        // lives in the conversation log.
        var actionsScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(actionsScroll);
        _actionsContainer = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill,
        };
        _actionsContainer.AddThemeConstantOverride("separation", 6);
        actionsScroll.AddChild(_actionsContainer);

        // Phase 5: the school's once-per-table signature move.
        _schoolMoveContainer = new VBoxContainer();
        _schoolMoveContainer.AddThemeConstantOverride("separation", 4);
        root.AddChild(_schoolMoveContainer);

        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 12);
        actionRow.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        root.AddChild(actionRow);

        // Live closing preview: the one line that answers "what do I get if
        // I shake hands right now?" Lives INSIDE the action row, beside the
        // handshake it describes (a standalone row pushed the buttons off
        // short screens). Updated every refresh.
        _dealPreviewLabel = new Label
        {
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            TooltipText = "What the deal pays at the current clause positions " +
                          "and zone. The squeeze, if any, comes on top.",
        };
        _dealPreviewLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _dealPreviewLabel.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        actionRow.AddChild(_dealPreviewLabel);

        // Priced risk (spec §4b): face-down clauses were always inside the
        // projection number; this chip is the number owning up to it.
        _unreadRiskLabel = new Label
        {
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Visible = false,
            TooltipText = "Face-down clauses bind at their current position when " +
                          "you sign. Insight turns them over first.",
            MouseFilter = MouseFilterEnum.Pass,
        };
        _unreadRiskLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _unreadRiskLabel.AddThemeColorOverride("font_color", UITheme.TensionStrained);
        actionRow.AddChild(_unreadRiskLabel);

        _shakeButton = new Button
        {
            Text = "Shake Hands",
            CustomMinimumSize = new Vector2(170, 44),
        };
        _shakeButton.AddThemeFontSizeOverride("font_size", UITheme.NegotiationActionFontSize);
        _shakeButton.Pressed += OnShakePressed;
        actionRow.AddChild(_shakeButton);

        _walkAwayButton = new Button
        {
            Text = "Walk Away",
            CustomMinimumSize = new Vector2(140, 44),
        };
        _walkAwayButton.AddThemeFontSizeOverride("font_size", UITheme.NegotiationActionFontSize);
        _walkAwayButton.Pressed += () => { StartLogTurn(); _state.WalkAway(); };
        actionRow.AddChild(_walkAwayButton);

        // ── SQUEEZE PANEL (Module B modal) ───────────────────────────────
        _squeezePanel = MakeModalPanel(330, 215);
        var squeezeLayout = MakeModalLayout(_squeezePanel);

        _squeezeLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _squeezeLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationBodyFontSize);
        squeezeLayout.AddChild(_squeezeLabel);

        var squeezeButtons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        squeezeButtons.AddThemeConstantOverride("separation", 12);
        squeezeLayout.AddChild(squeezeButtons);

        _squeezeConcedeBtn = new Button { CustomMinimumSize = new Vector2(160, 40), Text = "Concede & sign" };
        _squeezeConcedeBtn.Pressed += () =>
        {
            _squeezePanel.Visible = false;
            _state.ResolveSqueezeConcede(_pendingSqueeze);
            _pendingSqueeze = null;
        };
        squeezeButtons.AddChild(_squeezeConcedeBtn);

        _squeezeHoldBtn = new Button { CustomMinimumSize = new Vector2(160, 40) };
        _squeezeHoldBtn.Pressed += () =>
        {
            _squeezePanel.Visible = false;
            _state.ResolveSqueezeHoldFirm(_pendingSqueeze);
            _pendingSqueeze = null;
            if (!_state.IsResolved)
                RefreshAll();
        };
        squeezeButtons.AddChild(_squeezeHoldBtn);

        _squeezeWithdrawBtn = new Button { CustomMinimumSize = new Vector2(160, 40), Text = "Pull your hand back" };
        _squeezeWithdrawBtn.Pressed += () =>
        {
            _squeezePanel.Visible = false;
            _state.ResolveSqueezeWithdraw();
            _pendingSqueeze = null;
            RefreshAll();
        };
        squeezeButtons.AddChild(_squeezeWithdrawBtn);

        // ── RESULT PANEL (the receipt) ───────────────────────────────────
        _resultPanel = MakeModalPanel(330, 250);
        var resultLayout = MakeModalLayout(_resultPanel);

        _resultContent = new VBoxContainer
        {
            SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill,
        };
        _resultContent.AddThemeConstantOverride("separation", 8);
        resultLayout.AddChild(_resultContent);

        _continueButton = new Button
        {
            Text = "Return to the Map",
            CustomMinimumSize = new Vector2(200, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _continueButton.AddThemeFontSizeOverride("font_size", UITheme.NegotiationActionFontSize);
        _continueButton.Pressed += ReturnToOverworld;
        resultLayout.AddChild(_continueButton);
    }

    private Panel MakeModalPanel(float halfW, float halfH)
    {
        var panel = new Panel
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
            OffsetLeft = -halfW,
            OffsetTop = -halfH,
            OffsetRight = halfW,
            OffsetBottom = halfH,
            Visible = false,
        };
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.NegotiationResultBg,
            BorderColor = UITheme.NegotiationResultBorder,
            BorderWidthTop = UITheme.BorderWidth,
            BorderWidthBottom = UITheme.BorderWidth,
            BorderWidthLeft = UITheme.BorderWidth,
            BorderWidthRight = UITheme.BorderWidth,
            CornerRadiusTopLeft = UITheme.NarrativePanelCorner,
            CornerRadiusTopRight = UITheme.NarrativePanelCorner,
            CornerRadiusBottomLeft = UITheme.NarrativePanelCorner,
            CornerRadiusBottomRight = UITheme.NarrativePanelCorner,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);
        return panel;
    }

    private VBoxContainer MakeModalLayout(Panel host)
    {
        var layout = new VBoxContainer
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 24,
            OffsetTop = 24,
            OffsetRight = -24,
            OffsetBottom = -24,
        };
        layout.AddThemeConstantOverride("separation", 16);
        host.AddChild(layout);
        return layout;
    }

    // ── Refresh methods ───────────────────────────────────────────────────

    private void RefreshAll()
    {
        RefreshTensionBar();
        RefreshStance();
        RefreshNpcPool();
        RefreshIntent();
        RefreshDealPreview();
        RefreshTerms(animatePulse: true);   // flash the card the NPC just touched
        RebuildActions();
        RefreshSchoolMove();
    }

    /// <summary>The everyone-gets-a-sentence soft intent is retired (the
    /// clause cards' threat markers carry the same verdict without a line
    /// of prose); what remains is the Embassy tier-2 upgrade, the precise
    /// clause-naming briefing that building paid for.</summary>
    private void RefreshIntent()
    {
        if (_intentLabel == null || _state == null)
            return;
        bool show = !_state.IsResolved && _embassyTier >= 2;
        _intentLabel.Visible = show;
        if (show)
            _intentLabel.Text = $"Embassy briefing: {_state.PredictNpcMove()}";
    }

    /// <summary>Phase 5: the school signature move row, a button plus a
    /// per-school picker (Elementalist: target clause; Enchanter: mood;
    /// Adept: token type). Stays visible after use, disabled, so the player
    /// remembers it's spent.</summary>
    private void RefreshSchoolMove()
    {
        if (_schoolMoveContainer == null || _state == null)
            return;
        foreach (var child in _schoolMoveContainer.GetChildren())
            child.QueueFree();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var btn = new Button
        {
            Text = $"★ {NegotiationState.SchoolMoveName(_state.School)}",
            TooltipText = _state.SchoolMoveDescription() + "  (once per negotiation)",
            CustomMinimumSize = new Vector2(200, 36),
            Disabled = !_state.CanUseSchoolMove(),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        btn.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        row.AddChild(btn);

        OptionButton picker = null;
        var targets = _state.PullableTerms();
        switch (_state.School)
        {
            case CardSchool.Elementalist:
                // Targets the selected clause card; no separate picker.
                break;
            case CardSchool.Enchanter:
                picker = new OptionButton { CustomMinimumSize = new Vector2(180, 36) };
                foreach (NpcStance s in System.Enum.GetValues(typeof(NpcStance)))
                    picker.AddItem(s.ToString(), (int)s);
                break;
            case CardSchool.Adept:
                picker = new OptionButton { CustomMinimumSize = new Vector2(180, 36) };
                foreach (LeverageToken t in System.Enum.GetValues(typeof(LeverageToken)))
                    picker.AddItem(t.ToString(), (int)t);
                break;
        }
        if (picker != null)
        {
            picker.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
            picker.Disabled = !_state.CanUseSchoolMove();
            row.AddChild(picker);
        }

        var desc = new Label
        {
            Text = _state.SchoolMoveUsed ? "spent" : _state.SchoolMoveDescription(),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        desc.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
        desc.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        row.AddChild(desc);

        var pickerRef = picker;
        btn.Pressed += () =>
        {
            StartLogTurn();
            switch (_state.School)
            {
                case CardSchool.Elementalist:
                    {
                        var term = SelectedTerm();
                        if (term != null)
                            _state.UseSchoolMove(target: term);
                        break;
                    }
                case CardSchool.Enchanter:
                    _state.UseSchoolMove(forcedStance: (NpcStance)(pickerRef?.GetSelectedId() ?? 0));
                    break;
                case CardSchool.Adept:
                    _state.UseSchoolMove(chosenToken: (LeverageToken)(pickerRef?.GetSelectedId() ?? 0));
                    break;
                default:
                    _state.UseSchoolMove();
                    break;
            }
            RefreshAll();
        };

        _schoolMoveContainer.AddChild(row);
    }

    private void RefreshTensionBar()
    {
        if (_state == null)
            return;

        int t = _state.Tension;
        _tensionLabel.Text = $"{_state.Zone}  {t}/10";
        _tensionLabel.AddThemeColorOverride("font_color", _state.Zone switch
        {
            TensionZone.Cordial => UITheme.ZoneCordialLabel,
            TensionZone.Hostile => UITheme.ZoneHostileLabel,
            _ => UITheme.ZoneStrainedLabel,
        });
        RefreshPatienceBar();

        for (int i = 0; i < 10; i++)
        {
            bool filled = i < t;
            Color color;
            if (!filled)
                color = UITheme.TensionEmpty;
            else if (i < 3)
                color = UITheme.TensionCordial;
            else if (i < 7)
                color = UITheme.TensionStrained;
            else
                color = UITheme.TensionHostile;

            _tensionSteps[i].Color = color;
        }
    }

    private void RefreshPatienceBar()
    {
        if (_patienceBar == null || _state?.Data == null)
            return;
        int total = Mathf.Max(1, _state.Data.BasePatience);
        int remaining = Mathf.Clamp(_state.NpcPatience, 0, total);

        if (_patienceBar.GetChildCount() != total)
        {
            foreach (var child in _patienceBar.GetChildren())
                child.QueueFree();
            for (int i = 0; i < total; i++)
                _patienceBar.AddChild(new ColorRect
                {
                    CustomMinimumSize = new Vector2(22, 10),
                });
        }

        // Low patience burns red; the walkout is imminent.
        Color fill = remaining <= 2 ? UITheme.TensionHostile : UITheme.Violet;
        int i2 = 0;
        foreach (var child in _patienceBar.GetChildren())
        {
            if (child is ColorRect pip)
                pip.Color = i2++ < remaining ? fill : UITheme.TensionEmpty;
        }

        _patienceCaption.Text = _state.IsResolved
            ? "Their patience"
            : remaining <= 2
                ? $"Their patience: {remaining} move{(remaining == 1 ? "" : "s")} before they walk!"
                : $"Their patience: {remaining} moves left";
        _patienceCaption.AddThemeColorOverride("font_color",
            remaining <= 2 && !_state.IsResolved
                ? UITheme.TensionHostile : UITheme.NegotiationNpcColor);
    }

    private void RefreshStance()
    {
        if (_state == null || _stanceLabel == null)
            return;
        string tell = NegotiationBarks.StanceTell(_state.Data.Archetype, _state.Stance);
        string forecast = _state.NextStanceKnown
            ? $"   (next: {_state.PeekNextStance()})"
            : "";
        _stanceLabel.Text = $"{tell}{forecast}";
        _portrait?.SetStance(_state.Stance);
    }

    private void RefreshNpcPool()
    {
        if (_state == null || _npcPoolRow == null)
            return;
        foreach (var child in _npcPoolRow.GetChildren())
            child.QueueFree();
        AddNpcChip("resolve", _state.ResolveName, _state.NpcPool[NpcResource.Resolve]);
        AddNpcChip("guile", _state.GuileName, _state.NpcPool[NpcResource.Guile]);
        AddNpcChip("poise", _state.PoiseName, _state.NpcPool[NpcResource.Poise]);
    }

    private void AddNpcChip(string art, string displayName, int count)
    {
        var chip = new NegotiationTokenChip
        {
            ArtOverride = art,
            Count = count,
            SizePx = 44,
            Interactive = false,
            TooltipText = count > 0
                ? $"{displayName} ×{count}"
                : $"{displayName}: spent. This weapon is out of their hands.",
        };
        // A dry pool should LOOK dry: the moment their Resolve empties is
        // the moment pulls start sticking, and the rack should say so.
        if (count == 0)
            chip.Modulate = new Color(1f, 1f, 1f, 0.3f);
        _npcPoolRow.AddChild(chip);
    }

    /// <summary>The currently targeted clause, validated against the live
    /// board; falls back to the most valuable pullable clause.</summary>
    private DealTerm SelectedTerm()
    {
        var pullables = _state.PullableTerms();
        var picked = pullables.FirstOrDefault(t => t.Id == _selectedTermId);
        if (picked != null)
            return picked;
        return pullables.OrderByDescending(t => (2 - t.Position) * t.Weight).FirstOrDefault();
    }

    private void SelectTerm(string id)
    {
        _selectedTermId = id;
        _unrollTermId = id;   // opening a scroll animates, re-opening included
        RefreshTerms();
        RebuildActions();
        RefreshSchoolMove();
    }

    private void RefreshTerms(bool animatePulse = false)
    {
        if (_termsRow == null || _state == null)
            return;
        foreach (var child in _termsRow.GetChildren())
            child.QueueFree();

        var pullables = _state.PullableTerms();
        // Selection may rest on a pullable clause OR a face-down one (for
        // Insight); otherwise default to the most valuable pullable clause.
        bool validSelection = _state.Terms.Any(t =>
            t.Id == _selectedTermId && (t.IsHidden || pullables.Contains(t)));
        if (!validSelection)
            _selectedTermId = pullables
                .OrderByDescending(t => (2 - t.Position) * t.Weight)
                .FirstOrDefault()?.Id ?? "";

        // The threat marker: which clause their next move lands on, straight
        // from the same ladder NpcTurn executes.
        var (npcKind, npcTarget) = _state.PredictNpcAction();
        // The closing-demand mark (§5c): where a handshake offered now would
        // draw their squeeze, from the same predictor BeginShake uses.
        var squeezeTarget = _state.PredictSqueezeTarget();

        foreach (var term in _state.Terms)
        {
            if (term.IsHidden)
            {
                _termsRow.AddChild(BuildFaceDownCard(term, term.Id == _selectedTermId));
                continue;
            }
            var yourMv = ExchangeMove(term.Id, byPlayer: true);
            var theirMv = ExchangeMove(term.Id, byPlayer: false);
            var card = BuildTermCard(term,
                targetable: pullables.Contains(term),
                isSelected: term.Id == _selectedTermId,
                yourMove: yourMv,
                theirMove: theirMv,
                threat: npcTarget == term ? npcKind : (NpcMoveKind?)null,
                squeezeMark: squeezeTarget == term,
                unroll: term.Id == _unrollTermId && term.Id == _selectedTermId);
            _termsRow.AddChild(card);
            if (animatePulse && theirMv != null)
                PulseCard(card);
        }
        _unrollTermId = "";   // the unroll plays once per opening
    }

    /// <summary>This exchange's net slide of one clause by one mover, as a
    /// from→to pair, or null when that side didn't move it. A pull met by a
    /// counter-pull yields one marker for each side of the tug-of-war.</summary>
    private (int From, int To)? ExchangeMove(string termId, bool byPlayer)
    {
        int from = 0, to = 0;
        bool any = false;
        foreach (var m in _state.LastExchange)
        {
            if (m.TermId != termId || m.ByPlayer != byPlayer)
                continue;
            if (!any)
            { from = m.From; any = true; }
            to = m.To;
        }
        if (!any || from == to)
            return null;
        return (from, to);
    }

    /// <summary>Is notch p on the path this move slid across? (The landing
    /// notch is excluded; it renders as the current-position marker.)</summary>
    private static bool InTrail(int p, (int From, int To) mv) =>
        p >= Mathf.Min(mv.From, mv.To) && p <= Mathf.Max(mv.From, mv.To) && p != mv.To;

    /// <summary>A brief hostile-tinted flash on a card the NPC just touched.
    /// The tween is bound to the card, so a mid-flash rebuild cleans up.</summary>
    private void PulseCard(Control card)
    {
        card.Modulate = new Color(1f, 0.7f, 0.65f);
        var tw = card.CreateTween();
        tw.TweenProperty(card, "modulate", Colors.White, 0.9f)
          .SetTrans(Tween.TransitionType.Cubic)
          .SetEase(Tween.EaseType.Out);
    }

    /// <summary>One clause as a parchment card (placeholder art; swap the
    /// StyleBox for slip art in the full Phase 4 pass). Selected = gold
    /// border + ⌖ header; sealed = red border; targetable cards are
    /// clickable with a pointing-hand cursor. yourMove/theirMove are this
    /// exchange's slides of THIS clause, drawn as move badges plus a ghost
    /// trail on the slider, so the back-and-forth reads at a glance. threat
    /// marks the clause the NPC's NEXT move will land on (from
    /// PredictNpcAction), so baiting their pulls is a visible play.</summary>
    /// <summary>The dowel bar that caps a scroll card top and bottom: the
    /// rolled ends of the parchment.</summary>
    private static Panel MakeRoller()
    {
        var roller = new Panel { CustomMinimumSize = new Vector2(0, 9) };
        roller.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.42f, 0.32f, 0.22f),
            BorderColor = new Color(0.27f, 0.20f, 0.13f),
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
        });
        return roller;
    }

    private Control BuildTermCard(DealTerm term, bool targetable, bool isSelected,
                                  (int From, int To)? yourMove = null,
                                  (int From, int To)? theirMove = null,
                                  NpcMoveKind? threat = null,
                                  bool squeezeMark = false,
                                  bool unroll = false)
    {
        var ink = UITheme.WorldDeep;
        var inkSoft = new Color(ink.R, ink.G, ink.B, 0.72f);

        // A clause is a scroll: dowel bars cap the parchment, closed cards
        // stay short, and the selected one unrolls to show its text.
        var card = new PanelContainer { CustomMinimumSize = new Vector2(220, 132) };
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.SurfaceLight,   // parchment placeholder
            BorderColor = isSelected ? UITheme.NegotiationTitleColor
                        : term.Locked ? UITheme.TensionHostile
                        : new Color(ink.R, ink.G, ink.B, 0.35f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        int bw = isSelected ? 3 : 2;
        style.BorderWidthTop = bw;
        style.BorderWidthBottom = bw;
        style.BorderWidthLeft = bw;
        style.BorderWidthRight = bw;
        card.AddThemeStyleboxOverride("panel", style);

        var scroll = new VBoxContainer();
        scroll.AddThemeConstantOverride("separation", 0);
        card.AddChild(scroll);
        scroll.AddChild(MakeRoller());

        var margins = new MarginContainer { SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill };
        margins.AddThemeConstantOverride("margin_left", 10);
        margins.AddThemeConstantOverride("margin_right", 10);
        margins.AddThemeConstantOverride("margin_top", 6);
        margins.AddThemeConstantOverride("margin_bottom", 6);
        scroll.AddChild(margins);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        margins.AddChild(box);

        var header = new Label
        {
            Text = (isSelected ? "⌖ " : "") + NegotiationState.ShortName(term),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        header.AddThemeFontSizeOverride("font_size", UITheme.NegotiationDetailFontSize);
        header.AddThemeColorOverride("font_color", isSelected ? UITheme.VioletDark : ink);
        box.AddChild(header);

        // What this clause pays at its CURRENT position, live: the quiet
        // cards dropped the prose, so the numbers carry the stakes on every
        // card, and they move when the slider does.
        var payoutRow = new HBoxContainer();
        payoutRow.AddThemeConstantOverride("separation", 8);
        void Pay(string txt, float v) => payoutRow.AddChild(MakeTinyLabel(txt, GainLossColor(v)));
        var (pGold, pRep, pSupplies) = NegotiationState.TermPayout(term);
        int pSteps = Mathf.RoundToInt(term.StepsDelta * term.PlayerFraction());
        if (pGold != 0) Pay($"{Signed(pGold)}g", pGold);
        if (pSupplies != 0) Pay($"{Signed(pSupplies)} sup", pSupplies);
        if (pRep != 0) Pay($"{Signed(pRep)} rep", pRep);
        if (pSteps != 0) Pay($"{Signed(pSteps)} fuel", pSteps);
        if (!string.IsNullOrEmpty(term.SpellId)) Pay("tuition if Cordial", 1);
        if (term.RevealsSupplyCaches) Pay("cache intel", 1);
        if (payoutRow.GetChildCount() == 0) Pay("-", 0);
        box.AddChild(payoutRow);

        // Quiet cards: the full clause text shows on the SELECTED card only;
        // unselected cards are name, badges, and slider, with the text one
        // click (or a hover) away. The card tooltip keeps it reachable
        // without selecting.
        if (isSelected)
        {
            var desc = new Label
            {
                Text = term.Description,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            desc.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
            desc.AddThemeColorOverride("font_color", inkSoft);
            box.AddChild(desc);
            if (unroll)
            {
                // The scroll opens: the text sweeps down from the top roller
                // into the space the layout has already reserved for it.
                desc.PivotOffset = Vector2.Zero;
                desc.Scale = new Vector2(1f, 0f);
                desc.Ready += () =>
                {
                    var tw = desc.CreateTween();
                    tw.TweenProperty(desc, "scale", Vector2.One, 0.25f)
                      .SetTrans(Tween.TransitionType.Cubic)
                      .SetEase(Tween.EaseType.Out);
                };
            }
        }
        else
        {
            card.TooltipText = term.Description;
            box.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        }

        // Move badges (last exchange) + threat marker (their next move).
        // Both can show at once; that's the tug-of-war, made visible.
        if (yourMove != null || theirMove != null || threat != null || squeezeMark)
        {
            var moveRow = new HBoxContainer();
            moveRow.AddThemeConstantOverride("separation", 10);
            if (theirMove != null)
                moveRow.AddChild(MakeTinyLabel("◀ THEIR MOVE", UITheme.TermAgainstPlayer));
            if (yourMove != null)
                moveRow.AddChild(MakeTinyLabel("YOUR MOVE ▶", UITheme.TermFavorPlayer));
            if (threat == NpcMoveKind.Pull)
            {
                var tag = MakeTinyLabel("⌖ IN THEIR SIGHTS", UITheme.TermAgainstPlayer);
                tag.MouseFilter = MouseFilterEnum.Pass;   // tooltip without eating the card click
                tag.TooltipText = $"While their {_state.ResolveName} holds, their next move " +
                                  "drags this clause back a notch, two if the table is Hostile.";
                moveRow.AddChild(tag);
            }
            else if (threat == NpcMoveKind.Rework)
            {
                var tag = MakeTinyLabel("✎ FINE PRINT COMING", UITheme.ZoneStrainedLabel);
                tag.MouseFilter = MouseFilterEnum.Pass;
                tag.TooltipText = $"Their {_state.GuileName} reworks this clause a notch " +
                                  "their way next turn.";
                moveRow.AddChild(tag);
            }
            if (squeezeMark)
            {
                // §5c: the tell-never-lies principle, extended to closing.
                // (No glyph: color emoji don't render in the UI font stack.)
                var tag = MakeTinyLabel("CLOSING DEMAND", UITheme.ZoneStrainedLabel);
                tag.MouseFilter = MouseFilterEnum.Pass;
                tag.TooltipText = "Offer the handshake now, and their last demand " +
                                  "lands on this clause. It stops when their " +
                                  $"{_state.ResolveName} is spent.";
                moveRow.AddChild(tag);
            }
            box.AddChild(moveRow);
        }

        // Slider track: THEIRS ▢▢▢▢▢ YOURS
        var track = new HBoxContainer();
        track.AddThemeConstantOverride("separation", 3);
        var theirs = MakeTinyLabel("THEIRS", UITheme.TermAgainstPlayer);
        track.AddChild(theirs);
        for (int p = -2; p <= 2; p++)
        {
            Color notch = p == term.Position
                ? UITheme.NegotiationTitleColor
                : new Color(ink.R, ink.G, ink.B, 0.18f);
            // Ghost trail: the notches this clause just slid across, red
            // when they dragged it, green when you pulled it.
            if (p != term.Position)
            {
                if (theirMove != null && InTrail(p, theirMove.Value))
                    notch = new Color(UITheme.TermAgainstPlayer.R, UITheme.TermAgainstPlayer.G,
                                      UITheme.TermAgainstPlayer.B, 0.5f);
                else if (yourMove != null && InTrail(p, yourMove.Value))
                    notch = new Color(UITheme.TermFavorPlayer.R, UITheme.TermFavorPlayer.G,
                                      UITheme.TermFavorPlayer.B, 0.5f);
            }
            track.AddChild(new ColorRect
            {
                CustomMinimumSize = new Vector2(24, 10),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Color = notch,
            });
        }
        var yours = MakeTinyLabel("YOURS", UITheme.TermFavorPlayer);
        track.AddChild(yours);
        box.AddChild(track);

        // The position label is gone (the slider already says it); the lock
        // is the one state the slider can't show, so it keeps its line.
        if (term.Locked)
        {
            var lockLbl = new Label
            {
                Text = "sealed while Hostile",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            lockLbl.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
            lockLbl.AddThemeColorOverride("font_color", UITheme.TensionHostile);
            box.AddChild(lockLbl);
        }

        scroll.AddChild(MakeRoller());

        if (targetable)
        {
            card.MouseFilter = MouseFilterEnum.Stop;
            card.MouseDefaultCursorShape = CursorShape.PointingHand;
            var id = term.Id;
            card.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton mb && mb.Pressed
                    && mb.ButtonIndex == MouseButton.Left)
                    SelectTerm(id);
            };
        }
        return card;
    }

    private Control BuildFaceDownCard(DealTerm term, bool isSelected)
    {
        var card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(220, 128),
            TooltipText = "Select it, then spend an Insight token to flip it.",
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        int bw = isSelected ? 3 : 2;
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.BgCard,
            BorderColor = isSelected ? UITheme.NegotiationTitleColor
                                     : UITheme.NegotiationResultBorder,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        style.BorderWidthTop = bw;
        style.BorderWidthBottom = bw;
        style.BorderWidthLeft = bw;
        style.BorderWidthRight = bw;
        card.AddThemeStyleboxOverride("panel", style);

        // A sealed scroll: same dowels as the open clauses, dark parchment,
        // the rumor where the text would be.
        var scroll = new VBoxContainer();
        scroll.AddThemeConstantOverride("separation", 0);
        card.AddChild(scroll);
        scroll.AddChild(MakeRoller());

        var margins = new MarginContainer { SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill };
        margins.AddThemeConstantOverride("margin_left", 10);
        margins.AddThemeConstantOverride("margin_right", 10);
        margins.AddThemeConstantOverride("margin_top", 6);
        margins.AddThemeConstantOverride("margin_bottom", 6);
        scroll.AddChild(margins);

        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 6);
        margins.AddChild(box);

        var glyph = new Label
        {
            Text = isSelected ? "⌖ 🂠" : "🂠",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        glyph.AddThemeFontSizeOverride("font_size", 30);
        box.AddChild(glyph);

        // The rumor (spec §4a): authored innuendo in place of a generic
        // caption, so the card back hints at stakes without naming mechanics.
        var caption = new Label
        {
            Text = string.IsNullOrEmpty(term.RumorText)
                ? "A face-down clause.\nUnread clauses still bind."
                : term.RumorText,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        caption.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
        caption.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        box.AddChild(caption);
        if (!string.IsNullOrEmpty(term.RumorText))
            card.TooltipText = "Unread clauses still bind at signing. " +
                               "Select it, then spend an Insight token to flip it.";

        scroll.AddChild(MakeRoller());

        var id = term.Id;
        card.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed
                && mb.ButtonIndex == MouseButton.Left)
                SelectTerm(id);
        };
        return card;
    }

    // ── Actions: the verb band ────────────────────────────────────────────
    // Five verbs at 3/2/1/1/2 (negotiation UI proposal, "The Quiet Table"):
    // Sway holds the presses that cool the room, Force the two shows of
    // power, Offer / Read stand alone, Bide pairs the free Pass with the
    // paid Patience token. One shared context line under the band carries
    // the hovered move's spoken line; the rack itself is glyphs and counts.

    private static readonly LeverageToken[] SwayTokens =
        { LeverageToken.Charm, LeverageToken.Persuade, LeverageToken.Connections };
    private static readonly LeverageToken[] ForceTokens =
        { LeverageToken.Intimidate, LeverageToken.Demonstration };

    private void RebuildActions()
    {
        if (_actionsContainer == null || _state == null)
            return;

        foreach (var child in _actionsContainer.GetChildren())
            child.QueueFree();
        _contextSpoken = null;
        _contextFx = null;

        bool done = _state.IsResolved;
        _shakeButton.Disabled = done;
        _walkAwayButton.Disabled = done;
        if (done)
            return;

        BuildVerbBand();
    }

    /// <summary>The verb band: one horizontal row of chip clusters with a
    /// serif verb caption under each and thin separators between. Chips
    /// carry the timing glyph (NegotiationState.TimingFor); the spoken line
    /// and mechanical read live in the shared context line and appear on
    /// hover. Clicking spends toward the current selection, exactly as
    /// before.</summary>
    private void BuildVerbBand()
    {
        var targets = _state.PullableTerms();
        bool anyTargets = targets.Count > 0;

        var band = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        band.AddThemeConstantOverride("separation", 0);
        _actionsContainer.AddChild(band);

        bool first = true;
        void AddGroup(string verb, string rule, List<Control> chips)
        {
            if (!first)
            {
                var sep = new VSeparator();
                sep.AddThemeConstantOverride("separation", 24);
                band.AddChild(sep);
            }
            first = false;

            var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
            col.AddThemeConstantOverride("separation", 3);
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
            row.AddThemeConstantOverride("separation", 16);   // room for the edge tags
            foreach (var c in chips)
                row.AddChild(c);
            col.AddChild(row);

            var cap = new Label
            {
                Text = verb,
                HorizontalAlignment = HorizontalAlignment.Center,
                TooltipText = rule,
                MouseFilter = MouseFilterEnum.Pass,
            };
            cap.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
            cap.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
            cap.MouseEntered += () => SetContext(verb, rule);
            col.AddChild(cap);
            band.AddChild(col);
        }

        // Every verb renders every turn: a stable rack the hand can learn.
        // Tokens you hold but can't aim (no movable clause) show dimmed with
        // the reason; a verb with nothing left shows one empty socket.
        List<Control> GroupChips(LeverageToken[] toks, bool needsTarget, string spentReason)
        {
            var list = new List<Control>();
            foreach (var t in toks)
                if (_state.TokenPool[t] > 0)
                    list.Add(needsTarget && !anyTargets
                        ? MakeBandChip(t, playable: false,
                            reason: "No clause can be moved right now.")
                        : MakeBandChip(t));
            if (list.Count == 0)
                list.Add(MakeEmptySocket(spentReason));
            return list;
        }

        AddGroup("Sway", "Soft arguments: the clause moves your way and the room cools.",
            GroupChips(SwayTokens, needsTarget: true, "No Sway tokens remain."));
        AddGroup("Force", "Shows of power: stronger on the right mood, harder on the wrong one.",
            GroupChips(ForceTokens, needsTarget: true, "No Force tokens remain."));
        AddGroup("Offer", $"Goods cross the table: a strong pull that feeds their {_state.ResolveName}.",
            GroupChips(new[] { LeverageToken.Offering }, needsTarget: true, "No Offerings remain."));
        AddGroup("Read", "Flip a face-down clause, or learn their next mood.",
            GroupChips(new[] { LeverageToken.Insight }, needsTarget: false, "No Insight remains."));

        // Bide always has its free half: Pass is every wizard's stall, and
        // the dimmed Patience slot teaches that the paid version exists.
        var bide = new List<Control> { MakePassChip() };
        bide.Add(_state.TokenPool[LeverageToken.Patience] > 0
            ? MakeBandChip(LeverageToken.Patience)
            : MakeBandChip(LeverageToken.Patience, playable: false,
                reason: "Patience ×0. Earned from Chronomancers, Stoic companions, and buildings."));
        AddGroup("Bide", "Wait them out: free (their clock ticks and they act) or paid " +
                         "(it holds, and their mood shifts).", bide);

        // The shared context line: the one place the rack speaks.
        var ctx = new PanelContainer { SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill };
        ctx.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UITheme.BgDeep,
            BorderColor = UITheme.VioletDim,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginTop = 6, ContentMarginBottom = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12,
        });
        var ctxBox = new VBoxContainer();
        ctxBox.AddThemeConstantOverride("separation", 0);
        _contextSpoken = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _contextSpoken.AddThemeFontSizeOverride("font_size", UITheme.NegotiationDetailFontSize);
        _contextSpoken.AddThemeColorOverride("font_color", UITheme.NegotiationBodyColor);
        ctxBox.AddChild(_contextSpoken);
        _contextFx = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _contextFx.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _contextFx.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        ctxBox.AddChild(_contextFx);
        ctx.AddChild(ctxBox);
        _actionsContainer.AddChild(ctx);

        // With every verb always present, the only "nothing left" state
        // worth a sentence is a rack of sockets around the Pass chip.
        bool anyLeverage = false;
        foreach (LeverageToken t in Enum.GetValues(typeof(LeverageToken)))
            if (_state.TokenPool[t] > 0)
            { anyLeverage = true; break; }
        if (!anyLeverage)
            SetContext("Your leverage is spent.",
                "Nothing left but silence, a handshake, or the door.");
        else
            SetContext("Hover a token for its spoken line; click it to play toward the targeted clause.",
                "Hover a verb name for its rule. ✓ lands well on their mood, ✗ backfires.");
    }

    private void SetContext(string spoken, string fx)
    {
        if (_contextSpoken == null || _contextFx == null)
            return;
        _contextSpoken.Text = spoken;
        _contextFx.Text = fx;
    }

    private static (string Glyph, Color Color) BadgeFor(TokenTiming t) => t switch
    {
        TokenTiming.Favorable => ("✓", UITheme.TermFavorPlayer),
        TokenTiming.Poor => ("✗", UITheme.TensionHostile),
        _ => ("·", UITheme.NegotiationHiddenTerm),
    };

    /// <summary>The spoken line + mechanical read the context line shows
    /// for a token at the current stance and selection.</summary>
    private (string Spoken, string Fx) ContextFor(LeverageToken token)
    {
        var stance = _state.Stance;
        var arch = _state.Data.Archetype;
        if (token == LeverageToken.Insight)
        {
            bool flipMode = SelectedHiddenTerm() != null;
            return (flipMode ? NegotiationBarks.InsightFlipLine
                             : NegotiationBarks.SpokenLine(LeverageToken.Insight, stance, arch),
                    flipMode ? NegotiationBarks.InsightFlipPreview
                             : NegotiationBarks.InsightReadPreview);
        }
        if (token == LeverageToken.Patience)
            return (NegotiationBarks.SpokenLine(LeverageToken.Patience, stance, arch),
                    NegotiationBarks.PatiencePreview);
        string spoken = NegotiationBarks.SpokenLine(token, stance, arch)
            .Replace("{term}", NegotiationState.ShortName(SelectedTerm()));
        string fx = token == LeverageToken.Offering
            ? NegotiationBarks.OfferPreview(stance, _state.ResolveName)
            : NegotiationBarks.PressPreview(stance);
        return (spoken, fx);
    }

    private NegotiationTokenChip MakeBandChip(LeverageToken token,
                                              bool playable = true,
                                              string reason = "")
    {
        var timing = BadgeFor(_state.TimingFor(token));
        var chip = new NegotiationTokenChip
        {
            Token = token,
            Count = _state.TokenPool[token],
            SizePx = 48,
            Badge = playable ? timing.Glyph : "",
            BadgeColor = timing.Color,
            Interactive = playable,
            TooltipText = playable
                ? $"{token} ×{_state.TokenPool[token]}. Click to spend it."
                : reason,
            CanDrag = () => playable && _state != null && !_state.IsResolved
                            && _state.TokenPool[token] > 0,
        };
        if (!playable)
            chip.Modulate = new Color(1f, 1f, 1f, 0.35f);
        var tok = token;
        if (playable)
        {
            chip.MouseEntered += () => { var c = ContextFor(tok); SetContext(c.Spoken, c.Fx); };
            chip.Clicked += () => OnTokenClicked(tok);
        }
        else
        {
            string why = reason;
            chip.MouseEntered += () => SetContext(tok.ToString(), why);
        }
        return chip;
    }

    /// <summary>A verb's empty slot: same footprint as a chip, so the rack
    /// keeps its shape when a verb runs dry and the hand keeps its map.</summary>
    private Control MakeEmptySocket(string reason)
    {
        var socket = new Panel
        {
            CustomMinimumSize = new Vector2(50, 50),
            TooltipText = reason,
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        socket.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = new Color(UITheme.Violet.R, UITheme.Violet.G,
                                    UITheme.Violet.B, 0.35f),
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 25, CornerRadiusTopRight = 25,
            CornerRadiusBottomLeft = 25, CornerRadiusBottomRight = 25,
        });
        var mark = new Label
        {
            Text = "-",
            AnchorRight = 1f,
            AnchorBottom = 1f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        mark.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        mark.AddThemeColorOverride("font_color", new Color(
            UITheme.Violet.R, UITheme.Violet.G, UITheme.Violet.B, 0.5f));
        socket.AddChild(mark);
        string why = reason;
        socket.MouseEntered += () => SetContext(why, "");
        return socket;
    }

    private NegotiationTokenChip MakePassChip()
    {
        var chip = new NegotiationTokenChip
        {
            ArtOverride = "pass",
            SizePx = 48,
            ShowCount = false,
            TooltipText = "Pass, free. Say nothing; their patience wears, and they still act.",
            CanDrag = () => false,
        };
        chip.MouseEntered += () => SetContext(
            "You say nothing, and let them stew.",
            "PASS, free · Their clock ticks and they take their move.");
        chip.Clicked += () => { StartLogTurn(); _state.Pass(); RefreshAll(); };
        return chip;
    }

    /// <summary>The selected clause when it's face-down (Insight flips it).</summary>
    private DealTerm SelectedHiddenTerm() =>
        _state.Terms.FirstOrDefault(t => t.IsHidden && t.Id == _selectedTermId);

    /// <summary>Click-to-spend: route the token to the current selection.</summary>
    private void OnTokenClicked(LeverageToken token)
    {
        if (_state == null || _state.IsResolved || _state.TokenPool[token] <= 0)
            return;

        if (token == LeverageToken.Patience)
        {
            StartLogTurn();
            AppendLog($"{NegotiationBarks.SpokenLine(LeverageToken.Patience, _state.Stance, _state.Data.Archetype)}",
                      NegotiationLogKind.Dialogue);
            _state.PlayPatience();
        }
        else if (token == LeverageToken.Insight)
        {
            StartLogTurn();
            if (SelectedHiddenTerm() != null)
            {
                AppendLog($"{NegotiationBarks.InsightFlipLine}", NegotiationLogKind.Dialogue);
                // The flip IS an unrolling. PlayInsightFlip turns the FIRST
                // face-down clause, so aim selection and the animation at
                // that term, not at whatever card happened to be selected.
                var willFlip = _state.Terms.FirstOrDefault(t => t.IsHidden && !t.IsAccepted);
                if (willFlip != null)
                {
                    _selectedTermId = willFlip.Id;
                    _unrollTermId = willFlip.Id;
                }
                _state.PlayInsightFlip();
            }
            else
            {
                AppendLog($"{NegotiationBarks.SpokenLine(LeverageToken.Insight, _state.Stance, _state.Data.Archetype)}",
                          NegotiationLogKind.Dialogue);
                _state.PlayInsightRead();
            }
        }
        else
        {
            var term = SelectedTerm();
            if (term == null)
                return;
            StartLogTurn();
            AppendLog($"{NegotiationBarks.SpokenLine(token, _state.Stance, _state.Data.Archetype).Replace("{term}", NegotiationState.ShortName(term))}",
                      NegotiationLogKind.Dialogue);
            if (token == LeverageToken.Offering)
                _state.PlayOffering(term);
            else
                _state.PlayPress(token, term);
        }
        RefreshAll();
    }

    // ── Squeeze (Module B) ────────────────────────────────────────────────

    private void OnShakePressed()
    {
        if (_state.IsResolved)
            return;
        StartLogTurn();
        _pendingSqueeze = _state.BeginShake();
        if (_pendingSqueeze == null)
            return;   // signed as-is; OnNegotiationResolved already fired

        string termName = NegotiationState.ShortName(_pendingSqueeze.Target);
        var asWritten = (Gold: _state.ProjectGold(),
                         Rep: _state.ProjectReputation(),
                         Supplies: _state.ProjectSupplies(),
                         Stars: _state.ProjectStars());
        var conceded = _state.ProjectIfConceded(_pendingSqueeze.Target);
        bool anySupplies = asWritten.Supplies != 0 || conceded.Supplies != 0;
        string SqLine(int gold, int rep, int sup, int stars) =>
            $"{Signed(gold)} gold · " +
            (anySupplies ? $"{Signed(sup)} sup · " : "") +
            $"{Signed(rep)} rep · {StarLine(stars)}";
        // §5d: lead with the read of the person; the number stays, honest
        // but subordinate, on the arithmetic line below.
        string read = _pendingSqueeze.OddsPercent >= 60
            ? "Their grip is firm, but their eyes aren't. They might not mean it."
            : _pendingSqueeze.OddsPercent >= 40
                ? "You genuinely cannot tell whether they mean it."
                : "Every line of them says they will hold this demand.";
        _squeezeLabel.Text =
            $"{_state.Data.NpcName} holds your handshake. One last demand:\n" +
            $"the {termName} slides one notch their way.\n\n" +
            $"{read}\n\n" +
            $"Let them have it:  {SqLine(conceded.Gold, conceded.Rep, conceded.Supplies, conceded.Stars)}\n" +
            $"Sign as written:   {SqLine(asWritten.Gold, asWritten.Rep, asWritten.Supplies, asWritten.Stars)}\n\n" +
            $"Hold firm and they blink {_pendingSqueeze.OddsPercent} times in 100.\n" +
            (_state.Tension >= 8
                ? "If they bristle instead: +2 tension, and this table would COLLAPSE."
                : "If they bristle instead: +2 tension, and the talk goes on.");
        _squeezeConcedeBtn.Text = "Let them have it & sign";
        _squeezeHoldBtn.Text = "Hold firm";
        _squeezeWithdrawBtn.Text = "Withdraw your hand";
        _squeezePanel.Visible = true;
    }

    // ── Log / events ─────────────────────────────────────────────────────

    /// <summary>Archive everything logged so far as "old news". Called at
    /// the START of each player action so only the newest exchange renders
    /// bright. A blank sentinel line paragraphs the exchanges.</summary>
    private void StartLogTurn()
    {
        if (_logRecent.Count == 0)
            return;
        _logHistory.AddRange(_logRecent);
        _logHistory.Add(("", NegotiationLogKind.Scene));
        _logRecent.Clear();
    }

    private void AppendLog(string message, NegotiationLogKind kind)
    {
        _logRecent.Add((message, kind));
        RenderLog();
    }

    /// <summary>Dialogue-first rendering: spoken lines bright, stage
    /// direction italic and softer, the sim readout tiny, and hidden
    /// entirely unless "Table details" is on.</summary>
    private void RenderLog()
    {
        if (_logLabel == null)
            return;
        string dimHex = UITheme.NegotiationHiddenTerm.ToHtml(false);
        string sceneHex = UITheme.NegotiationNpcColor.ToHtml(false);
        var sb = new System.Text.StringBuilder();
        foreach (var (text, kind) in _logHistory)
        {
            if (text.Length == 0)
            { sb.Append('\n'); continue; }
            if (kind == NegotiationLogKind.Detail)
            {
                if (!_showDetails)
                    continue;
                sb.Append($"[font_size={UITheme.NegotiationTinyFontSize}]" +
                          $"[color=#{dimHex}]{EscapeBb(text)}[/color][/font_size]\n");
            }
            else
            {
                sb.Append($"[color=#{dimHex}]{EscapeBb(text)}[/color]\n");
            }
        }
        foreach (var (text, kind) in _logRecent)
        {
            switch (kind)
            {
                case NegotiationLogKind.Detail:
                    if (!_showDetails)
                        continue;
                    sb.Append($"[font_size={UITheme.NegotiationTinyFontSize}]" +
                              $"[color=#{dimHex}]{EscapeBb(text)}[/color][/font_size]\n");
                    break;
                case NegotiationLogKind.Scene:
                    sb.Append($"[i][color=#{sceneHex}]{EscapeBb(text)}[/color][/i]\n");
                    break;
                default:   // Dialogue, the reading layer
                    sb.Append($"{EscapeBb(text)}\n");
                    break;
            }
        }
        _logLabel.Text = sb.ToString();
        // Keep the newest lines in view.
        _logScroll?.SetDeferred("scroll_vertical", 999999);
    }

    private static string EscapeBb(string s) =>
        s.Replace("[", "[lb]").Replace("]", "[rb]");

    private void OnTensionChanged(int oldTension, int newTension)
    {
        RefreshTensionBar();
        _portrait?.SetZone(_state.Zone);
    }

    private void OnNegotiationResolved()
    {
        _shakeButton.Disabled = true;
        _walkAwayButton.Disabled = true;
        _squeezePanel.Visible = false;
        if (_dealPreviewLabel != null)
            _dealPreviewLabel.Visible = false;
        if (_unreadRiskLabel != null)
            _unreadRiskLabel.Visible = false;
        RebuildActions();

        string spellGranted = _state.GetSpellOutcome(); // S4: "" unless Cordial

        foreach (var child in _resultContent.GetChildren())
            child.QueueFree();
        if (_state.DealAccepted)
            BuildDealReceipt(spellGranted);
        else
            BuildNoDealResult();
        _resultPanel.Visible = true;

        NegotiationContext.SetResult(
            _state.DealAccepted,
            _state.GetGoldOutcome(),
            _state.GetReputationOutcome(),
            _state.Data.FactionId,
            spellGranted,
            resolvedCordial: _state.Zone == TensionZone.Cordial, // S5: compulsion-echo burial gate
            supplies: _state.GetSuppliesOutcome(),
            revealSupplyCaches: _state.GetSupplyIntelOutcome(),
            steps: _state.GetStepsOutcome(),
            // Resolution Check (negotiation_system.docx): max tension against an
            // aggressive counterpart does not close the table, it opens a fight.
            escalated: _state.Collapsed && _state.Data.Escalates);

        SettleRegardAtTable();      // §6a: the court's voice was in the room

        RecordDeal(spellGranted);   // Hall of Records: every outcome, every timeline

        GD.Print($"Negotiation resolved: deal={_state.DealAccepted}, " +
                 $"gold={_state.GetGoldOutcome()}, rep={_state.GetReputationOutcome()}, " +
                 $"stars={_state.GetStars()}" +
                 (spellGranted != "" ? $", taught='{spellGranted}'" : ""));
    }

    /// <summary>§6a (Q4 ruling): when the origin kingdom's court seats a
    /// courtier of this counterpart's archetype, that courtier is the
    /// counterpart's voice at court, and a signed deal moves their Regard
    /// AT THE TABLE: rep sign mirrors the old echo valence (+1 fair, −1
    /// exploitative) and a 4★+ signing adds one more. Sets
    /// NegotiationContext.RegardSettledAtTable so the run manager skips the
    /// deal-deed echo: this REPLACES the Word Spreads route for courtier
    /// tables rather than double-counting with it. Everyone else at court
    /// still hears about it the slow way (they don't; no echo: attribution
    /// lands entirely on the one who was, in a sense, in the room).</summary>
    private void SettleRegardAtTable()
    {
        NegotiationContext.RegardSettledAtTable = false;
        if (!_state.DealAccepted)
            return;
        var cycle = SaveManager.ActiveSave?.Cycle;
        string kingdom = NegotiationContext.OriginKingdomId;
        if (cycle?.Council == null || string.IsNullOrEmpty(kingdom) ||
            !cycle.Council.Courts.TryGetValue(kingdom, out var court))
            return;
        var voice = court.Courtiers.FirstOrDefault(
            c => c.Archetype == _data.Archetype.ToString());
        if (voice == null)
            return;

        // The court consequence is settled here even when the delta is 0:
        // the voice was at the table, so the echo route stays quiet.
        NegotiationContext.RegardSettledAtTable = true;

        int rep = _state.GetReputationOutcome();
        int delta = (rep > 0 ? 1 : rep < 0 ? -1 : 0)
                  + (_state.GetStars() >= 4 ? 1 : 0);
        if (delta == 0)
            return;
        voice.Regard = Mathf.Clamp(voice.Regard + delta, -3, 3);
        SaveManager.MarkDirty();
        AppendLog(delta > 0
            ? $"Word of this table travels ahead of you: {voice.DisplayName}, " +
              $"{voice.Office} at this kingdom's court, will hear of it warmly."
            : $"Word of this table travels ahead of you: {voice.DisplayName}, " +
              $"{voice.Office} at this kingdom's court, will not like what they hear.",
            NegotiationLogKind.Scene);
        GD.Print($"[Negotiation] Regard settled at table: {voice.DisplayName} " +
                 $"({voice.Office}, {kingdom}) {delta:+0;-0} -> {voice.Regard}.");
    }

    // ── The receipt (result panel content) ───────────────────────────────

    private static string Signed(int v) => v >= 0 ? $"+{v}" : v.ToString();

    private static string StarLine(int stars) =>
        new string('★', stars) + new string('☆', 5 - stars);

    private static Color ZoneColor(TensionZone z) => z switch
    {
        TensionZone.Cordial => UITheme.ZoneCordialLabel,
        TensionZone.Hostile => UITheme.ZoneHostileLabel,
        _ => UITheme.ZoneStrainedLabel,
    };

    private static Color GainLossColor(float v) =>
        v > 0 ? UITheme.TermFavorPlayer
      : v < 0 ? UITheme.TermAgainstPlayer
              : UITheme.NegotiationHiddenTerm;

    private Label ReceiptCell(string text, Color color, int fontSize,
                              HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var lbl = new Label { Text = text, HorizontalAlignment = align };
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }

    /// <summary>A full-width centered line in the result panel.</summary>
    private void AddResultLine(string text, Color color, int fontSize)
    {
        var lbl = ReceiptCell(text, color, fontSize, HorizontalAlignment.Center);
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _resultContent.AddChild(lbl);
    }

    /// <summary>One ledger row: clause | gold | supplies | rep | note.</summary>
    private void AddReceiptRow(GridContainer grid,
                               string name, Color nameColor,
                               string goldText, Color goldColor,
                               string suppliesText, Color suppliesColor,
                               string repText, Color repColor,
                               string note, Color noteColor)
    {
        var nameLbl = ReceiptCell(name, nameColor, UITheme.NegotiationDetailFontSize);
        nameLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(nameLbl);
        grid.AddChild(ReceiptCell(goldText, goldColor,
            UITheme.NegotiationDetailFontSize, HorizontalAlignment.Right));
        grid.AddChild(ReceiptCell(suppliesText, suppliesColor,
            UITheme.NegotiationDetailFontSize, HorizontalAlignment.Right));
        grid.AddChild(ReceiptCell(repText, repColor,
            UITheme.NegotiationDetailFontSize, HorizontalAlignment.Right));
        var noteLbl = ReceiptCell(note, noteColor, UITheme.NegotiationTinyFontSize);
        noteLbl.VerticalAlignment = VerticalAlignment.Center;
        grid.AddChild(noteLbl);
    }

    /// <summary>The signing receipt: one line per clause with what it
    /// actually pays or costs at its final position, the zone adjustment as
    /// its own line, then the walk-away totals. Replaces the old wall of
    /// prose: every number the player cares about, nothing else.</summary>
    private void BuildDealReceipt(string spellGranted)
    {
        AddResultLine($"Deal Struck   {StarLine(_state.GetStars())}",
            UITheme.NegotiationTitleColor, UITheme.NegotiationResultFontSize);
        AddResultLine($"closed in the {_state.Zone} zone",
            ZoneColor(_state.Zone), UITheme.NegotiationSmallFontSize);

        var grid = new GridContainer
        {
            Columns = 5,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 3);
        _resultContent.AddChild(grid);

        foreach (var term in _state.Terms)
        {
            var (tGold, tRep, tSupplies) = NegotiationState.TermPayout(term);
            string name = (term.IsHidden ? "🂠 " : "· ") + NegotiationState.ShortName(term);
            Color nameColor = term.IsHidden ? UITheme.NegotiationHiddenTerm
                                            : UITheme.NegotiationBodyColor;

            string note;
            Color noteColor = UITheme.NegotiationHiddenTerm;
            if (term.IsHidden)
            {
                note = "never read; binds anyway";
                noteColor = UITheme.TermAgainstPlayer;
            }
            else if (!string.IsNullOrEmpty(term.SpellId))
            {
                bool granted = spellGranted == term.SpellId;
                note = granted ? "learned ✓" : "lost; needed a Cordial close";
                noteColor = granted ? UITheme.TermFavorPlayer : UITheme.TermAgainstPlayer;
            }
            else if (term.RevealsSupplyCaches)
            {
                bool marked = term.PlayerFraction() > 0f;
                note = marked ? "supply lines marked ✓" : "lost; fully theirs";
                noteColor = marked ? UITheme.TermFavorPlayer : UITheme.TermAgainstPlayer;
            }
            else if (!term.FavorPlayer && term.PlayerFraction() == 0f)
            {
                note = "defanged ✓";
                noteColor = UITheme.TermFavorPlayer;
            }
            else if (term.FavorPlayer && term.PlayerFraction() == 0f)
            {
                note = "lost; fully theirs";
                noteColor = UITheme.TermAgainstPlayer;
            }
            else
            {
                note = NegotiationState.PositionLabel(term.Position);
            }

            AddReceiptRow(grid,
                name, nameColor,
                tGold == 0 ? "-" : $"{Signed(tGold)}g", GainLossColor(tGold),
                tSupplies == 0 ? "-" : $"{Signed(tSupplies)} sup", GainLossColor(tSupplies),
                tRep == 0 ? "-" : $"{Signed(tRep)} rep", GainLossColor(tRep),
                note, noteColor);
        }

        // Zone adjustments as their own line item: no hidden math. (Supplies
        // take no zone rate; provisions are physical goods.)
        float mult = NegotiationState.ZoneGoldMult(_state.Zone);
        int repAdj = NegotiationState.ZoneRepBonus(_state.Zone);
        if (mult != 1f || repAdj != 0)
            AddReceiptRow(grid,
                $"{_state.Zone} zone rate", ZoneColor(_state.Zone),
                mult != 1f ? $"×{mult:0.##}g" : "-", GainLossColor(mult - 1f),
                "-", UITheme.NegotiationHiddenTerm,
                repAdj != 0 ? $"{Signed(repAdj)} rep" : "-", GainLossColor(repAdj),
                "", UITheme.NegotiationHiddenTerm);

        _resultContent.AddChild(new HSeparator());

        // The one-time lesson (spec §4d): the first time, ever, that a deal
        // signs with an unread clause, one footer explains the rule. Gated
        // on an ETERNAL flag: across timelines, the chronicle does not nag.
        var lessonSave = SaveManager.ActiveSave;
        if (_state.Terms.Any(t => t.IsHidden) && lessonSave?.Ledger != null &&
            !lessonSave.Ledger.MetaNarrativeFlags.Contains("meta_unread_clause_lesson"))
        {
            lessonSave.Ledger.MetaNarrativeFlags.Add("meta_unread_clause_lesson");
            SaveManager.MarkDirty();
            AddResultLine("Clauses you never turned over bind as written. Insight " +
                          "reads them; reading them lets you fight them.",
                UITheme.TermAgainstPlayer, UITheme.NegotiationSmallFontSize);
        }

        string total = $"You walk away with:  {Signed(_state.GetGoldOutcome())} gold" +
                       (_state.GetSuppliesOutcome() != 0
                           ? $" · {Signed(_state.GetSuppliesOutcome())} supplies" : "") +
                       (_state.GetStepsOutcome() != 0
                           ? $" · {Signed(_state.GetStepsOutcome())} fuel" : "") +
                       $" · {Signed(_state.GetReputationOutcome())} rep";
        if (spellGranted != "")
            total += $" · {OverworldSpellRegistry.Get(spellGranted)?.Name} learned";
        AddResultLine(total, UITheme.NegotiationTitleColor,
            UITheme.NegotiationResultFontSize);
    }

    /// <summary>No-deal outcomes stay short: what happened, what it cost.</summary>
    private void BuildNoDealResult()
    {
        AddResultLine(_state.PlayerWalkedAway ? "You Walked Away" : "They Ended It",
            UITheme.NegotiationTitleColor, UITheme.NegotiationResultFontSize);
        AddResultLine("No deal. Nothing gained, nothing lost. Reputation unharmed.",
            UITheme.NegotiationNpcColor, UITheme.NegotiationBodyFontSize);
    }

    /// <summary>The one line that answers "what do I get if I shake hands
    /// right now?" Refreshed after every exchange.</summary>
    private void RefreshDealPreview()
    {
        if (_dealPreviewLabel == null || _state == null)
            return;
        _dealPreviewLabel.Visible = !_state.IsResolved;
        if (_state.IsResolved)
            return;

        int previewSupplies = _state.ProjectSupplies();
        int previewSteps = _state.ProjectSteps();
        string text = $"Signs now for:  {Signed(_state.ProjectGold())}g" +
                      (previewSupplies != 0 ? $" · {Signed(previewSupplies)} sup" : "") +
                      (previewSteps != 0 ? $" · {Signed(previewSteps)} fuel" : "") +
                      $" · {Signed(_state.ProjectReputation())} rep" +
                      $" · {StarLine(_state.ProjectStars())}";
        if (_state.HasSpellTermOnTable())
            text += _state.Zone == TensionZone.Cordial
                ? " · tuition ✓"
                : " · tuition if Cordial";
        _dealPreviewLabel.Text = text;

        if (_unreadRiskLabel != null)
        {
            int unread = _state.Terms.Count(t => t.IsHidden && !t.IsAccepted);
            _unreadRiskLabel.Visible = unread > 0;
            _unreadRiskLabel.Text = unread == 1
                ? "· and 1 clause unread 🂠"
                : $"· and {unread} clauses unread 🂠";
        }
    }

    /// <summary>Hall of Records (negotiation doc §7b): append this table's
    /// outcome to the eternal ledger, count the deeds, and anchor five-star
    /// deals as renown (RenownAnchor's own documented example milestone).
    /// Fires for EVERY resolution: signed, walked, left, collapsed.</summary>
    private void RecordDeal(string spellGranted)
    {
        var save = SaveManager.ActiveSave;

        string outcome = _state.DealAccepted ? "Signed"
            : _state.PlayerWalkedAway ? "WalkedAway"
            : _state.Collapsed ? "Collapsed"
            : "TheyLeft";

        var record = new DealRecord
        {
            CycleNumber = save?.Cycle?.CycleNumber ?? 0,
            When = System.DateTime.UtcNow.ToString("o"),
            EncounterId = _data.Id,
            Title = _data.Title,
            NpcName = _data.NpcName,
            Archetype = _data.Archetype.ToString(),
            FactionId = _data.FactionId,
            Outcome = outcome,
            Stars = _state.DealAccepted ? _state.GetStars() : 0,
            Score = _state.GetDealScore(),
            Gold = _state.GetGoldOutcome(),
            Reputation = _state.GetReputationOutcome(),
            Supplies = _state.GetSuppliesOutcome(),
            Zone = _state.Zone.ToString(),
            Turns = _state.TurnNumber,
            SpellGranted = spellGranted,
        };

        // Tuning data: every table, including debug ones without a save.
        NegotiationTelemetry.Record(record, _state);

        if (save == null)
            return;   // debug scene entry; nothing to persist
        save.Ledger.DealRecords.Add(record);

        // Deed ledger: outcome-blind count, plus the signed/masterpiece deeds.
        save.Ledger.RecordDeed("negotiation_resolved");
        if (_state.DealAccepted)
        {
            save.Ledger.RecordDeed("negotiation_deal_signed");
            if (record.Stars >= 5)
            {
                save.Ledger.RecordDeed("negotiation_five_star_deal");
                save.Ledger.RenownAnchors.Add(new RenownAnchor
                {
                    SubjectId = string.IsNullOrEmpty(_data.FactionId)
                        ? _data.NpcName : _data.FactionId,
                    MilestoneId = "FiveStarDeal",
                    CycleAnchored = save.Cycle.CycleNumber,
                });
                GD.Print($"[Negotiation] Five-star deal anchored: '{record.EncounterId}'.");
            }
        }
        SaveManager.MarkDirty();
    }

    private void ReturnToOverworld()
    {
        GetTree().ChangeSceneToFile(
            EncounterRouter.Instance?.OverworldScenePath
            ?? "res://Scenes/Overworld/ExpeditionScene.tscn");
    }

    private Label MakeTinyLabel(string text, Color color)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }
}
