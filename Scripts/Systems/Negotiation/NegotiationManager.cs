using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// NegotiationManager.cs
//
// Purpose:        Full-screen negotiation scene controller (v3,
//                 "The Ledger"). Reads NegotiationContext input,
//                 drives NegotiationState, renders the table
//                 (v3.3: ONE column of contested TERMS, each on a
//                 theirs · contested · yours track; THE LEDGER of
//                 locked terms beside it with your HAND of parley
//                 cards at its foot; THEIR face-down cards above
//                 the terms; the animated portrait), plays the FX,
//                 runs the counter / squeeze / receipt modals, and
//                 writes results back to the context.
//                 Interaction model (v3.1, ruled 2026-09-18): the
//                 slip is the verb (Ask for it / Give it); cards
//                 are played from the hand, one per turn, before
//                 the action — and act on a CATEGORY of the table
//                 (coin / access / standing / lore), never on one
//                 slip, so there is no targeting mode (v3.2, ruled
//                 2026-09-19). Tells, not numbers: their credit is a
//                 posture, a read valuation is a band, the
//                 forecast is the look on their face. The only
//                 numbers on the table are your seals and the
//                 patience count; the receipt carries the rest.
// Layer:          UI
// Collaborators:  NegotiationContext.cs (I/O),
//                 NegotiationState.cs (state machine),
//                 NegotiationBarks.cs (lines),
//                 NegotiationPortrait.cs (rig + voice + posture),
//                 ParleyDeck.cs (cards, tells),
//                 NegotiationEncounterLoader.cs (data source),
//                 UITheme.cs (styling)
// See:            docs/negotiation_ledger_spec_v1.md,
//                 docs/negotiation_parley_deck_spec_v1.md
// ============================================================
//
// Integrations preserved from v2 (verbatim semantics, v3 inputs):
//   S3 Beguile TensionShift → goodwill +N at open
//   S4 spell tuition injection → a Warm-sealed theirs clause
//   S5 ResolvedCordial → SignedWarm
//   C5 patron token, court-standing goodwill, §6a Regard at table,
//   §6b/§6c chronicle read-back, §6d late-campaign lines,
//   supply-cost strip, supply-lines intel clause, escalation flag,
//   Hall of Records DealRecord + deeds + five-star renown, telemetry,
//   EncounterRouter return path.

public partial class NegotiationManager : Control
{
    private NegotiationState _state;
    private NegotiationEncounterData _data;

    // ── UI references ────────────────────────────────────────────────────
    private Label _titleLabel;
    private NegotiationPortrait _portrait;
    private Label _npcNameLabel;
    private Label _npcSubLabel;
    private Label _barkLabel;
    private Label _grievanceLabel;

    private HBoxContainer _goodwillBar;
    private readonly ColorRect[] _goodwillCells = new ColorRect[NegotiationTuning.GoodwillMax];
    private Label _moodLabel;
    private HBoxContainer _patienceBar;
    private Label _patienceCaption;
    private Label _postureLabel;
    private int _embassyTier = 0;

    private HBoxContainer _tableRow;
    private VBoxContainer _termsCol;
    private Panel _settlePanel;
    private RichTextLabel _settleLabel;
    private Button _settleSignBtn, _settleWaitBtn;
    private HBoxContainer _handRow;
    private Label _deckLabel;
    private HBoxContainer _npcCardRow;
    private readonly Dictionary<string, Control> _slipNodes = new();
    private readonly Dictionary<string, Color> _slipRestModulate = new();   // what each term looks like when nothing is hovered
    private readonly Dictionary<string, Control> _trackMarkers = new();     // the sliding marker on each open term's track
    private readonly Dictionary<string, int> _shownPositions = new();        // position each marker was last drawn at
    private readonly Dictionary<string, int> _prevPositions = new();         // ...and before this refresh (for the slide)
    private readonly Dictionary<NpcCardKind, Control> _npcCardNodes = new();
    private static readonly Font BoldFont = GD.Load<Font>("res://Assets/Fonts/Carlito-Bold.ttf");
    private ParleyCard _hoverCard = null;
    private readonly Dictionary<int, Control> _cardNodes = new();

    private Label _hintLabel;
    private NegotiationReaction _lastReaction;   // drives the "what now" prompt


    private Panel _counterPanel;
    private RichTextLabel _counterLabel;
    private Button _counterAcceptBtn;
    private Button _counterDeclineBtn;
    private Panel _squeezePanel;
    private Label _squeezeLabel;
    private Button _squeezeConcedeBtn, _squeezeHoldBtn, _squeezeWithdrawBtn;
    private SqueezeOffer _pendingSqueeze;

    private Panel _resultPanel;
    private VBoxContainer _resultContent;
    private Button _continueButton;

    private ColorRect _wash;   // full-screen school-colour wash for FX

    // v3.4: no selection — the deck is the only control.

    // Log
    private RichTextLabel _logLabel;
    private ScrollContainer _logScroll;
    private readonly List<(string Text, NegotiationLogKind Kind)> _logHistory = new();
    private readonly List<(string Text, NegotiationLogKind Kind)> _logRecent = new();
    private CheckButton _detailsToggle;
    private bool _showDetails = false;

    // Reactions queued during a state call, played after the rebuild.
    private readonly List<NegotiationReaction> _pendingFx = new();

    public override void _Ready()
    {
        BuildUI();
        InitializeNegotiation();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Table-open
    // ═══════════════════════════════════════════════════════════════════

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

        // Starting reputation: kingdom NPCs from court standing; non-kingdom
        // factions from the FactionReputation ledger.
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

        // Diplomacy Debug Launcher overrides (NegotiationDebug), live only while
        // PlayerSession.DebugNegotiation is set; cleared on return.
        bool debugTable = PlayerSession.DebugNegotiation;
        if (debugTable && NegotiationDebug.StandingOverride.HasValue)
            factionRep = NegotiationDebug.StandingOverride.Value;
        if (debugTable && NegotiationDebug.PatienceOverride.HasValue)
            _data.BasePatience = NegotiationDebug.PatienceOverride.Value;

        int baseGoodwill = _data.StartingGoodwill >= 0 ? _data.StartingGoodwill : NegotiationTuning.BaseGoodwill;
        if (debugTable && NegotiationDebug.GoodwillOverride.HasValue)
            baseGoodwill = NegotiationDebug.GoodwillOverride.Value;

        // S3 (Beguile): an armed charm opens the table a band more favorable.
        if (NegotiationContext.TensionShift != 0)
        {
            baseGoodwill += NegotiationContext.TensionShift;
            GD.Print($"[Negotiation] Beguile: opening goodwill +{NegotiationContext.TensionShift}.");
            NegotiationContext.TensionShift = 0;
        }

        // ── The chronicle read-back (§6b/§6c) ────────────────────────────
        DealRecord lastThisCycle = null;
        int priorLifeTables = 0;
        bool priorLifeCollapse = false;
        var chronLedger = SaveManager.ActiveSave?.Ledger;
        if (cycle != null && chronLedger != null)
        {
            foreach (var r in chronLedger.DealRecords)
            {
                if (r.EncounterId != _data.Id) continue;
                if (r.CycleNumber == cycle.CycleNumber) lastThisCycle = r;
                else if (r.CycleNumber < cycle.CycleNumber)
                {
                    priorLifeTables++;
                    if (r.Outcome == "Collapsed") priorLifeCollapse = true;
                }
            }
        }
        // §6b: how the last table this life ended shifts how this one opens.
        if (lastThisCycle != null)
        {
            int shift = lastThisCycle.Outcome switch
            {
                "Signed" => lastThisCycle.Stars >= 4 ? +1 : 0,
                "Collapsed" => -2,
                _ => -1,   // WalkedAway, TheyLeft
            };
            baseGoodwill += shift;
            if (shift != 0)
                GD.Print($"[Negotiation] Continuity: last outcome {lastThisCycle.Outcome} " +
                         $"({lastThisCycle.Stars}★) shifts opening goodwill by {shift:+0;-0}.");
        }
        _data.StartingGoodwill = Mathf.Clamp(baseGoodwill, 1, NegotiationTuning.GoodwillMax);

        // §6d: a continued campaign reaches the table.
        if (cycle != null && cycle.CampaignYear >= 2)
        {
            if (!string.IsNullOrEmpty(_data.OpeningTextLate)) _data.OpeningText = _data.OpeningTextLate;
            if (!string.IsNullOrEmpty(_data.DialogueWalkawayLate)) _data.DialogueWalkaway = _data.DialogueWalkawayLate;
        }

        // Injected clauses are stripped first (guard against authored stale copies).
        _data.Clauses.RemoveAll(c => c.Id == "spell_tuition" || c.Id == "supply_lines_intel");

        // Supply-cost clauses the guild can't cover come off the table.
        int suppliesOnHand = SaveManager.ActiveSave?.Supplies ?? 0;
        int strippedSupply = _data.Clauses.RemoveAll(
            c => c.SuppliesDelta < 0 && suppliesOnHand < -c.SuppliesDelta);
        if (strippedSupply > 0)
            GD.Print($"[Negotiation] Stripped {strippedSupply} supply-cost clause(s): stores too low ({suppliesOnHand}).");

        // Supply-lines intel (supply_cache spec v1.1): kingdom NPCs can sell
        // their homeland's cache locations while some are undiscovered.
        if (cycle != null && !string.IsNullOrEmpty(originKingdom) &&
            cycle.Kingdoms != null && cycle.Kingdoms.ContainsKey(originKingdom) &&
            SupplyCacheSystem.HasUndiscoveredCache(cycle, originKingdom) &&
            GD.Randf() < 0.4f)
        {
            _data.Clauses.Add(new Clause
            {
                Id = "supply_lines_intel",
                ShortName = "supply charts",
                Text = "Their supply charts: every depot their people draw from, marked on your map",
                Side = ClauseSide.Theirs, Kind = ClauseKind.Lore,
                Value = 3, NpcValue = 2,
                RevealsSupplyCaches = true,
            });
            GD.Print("[Negotiation] Supply-lines intel on the table.");
        }

        // S4: the social route to spells — a Warm-sealed clause.
        var grimoire = cycle?.Grimoire;
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
                    _data.Clauses.Add(new Clause
                    {
                        Id = "spell_tuition",
                        ShortName = "tuition",
                        Text = $"Tuition: they teach you {offerDef.Name}",
                        Side = ClauseSide.Theirs, Kind = ClauseKind.Spell,
                        Value = 3, NpcValue = -1,
                        SpellId = offerDef.Id,
                        RequiresWarm = true,
                    });
                    GD.Print($"[Negotiation] Tuition on the table: '{offerDef.Id}'.");
                }
            }
        }

        _state = new NegotiationState();
        _state.OnGoodwillChanged += OnGoodwillChanged;
        _state.OnLogEntry += AppendLog;
        _state.OnReaction += r => { _pendingFx.Add(r); _lastReaction = r; };
        _state.OnResolved += OnNegotiationResolved;

        // C5 patron token.
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

        // Building hooks are read before the table opens: Embassy II changes
        // the opening hand.
        int courierTier = 0, warRoomTier = 0;
        _embassyTier = 0;
        if (SaveManager.ActiveSave != null)
        {
            foreach (var b in SaveManager.ActiveSave.Buildings)
            {
                if (b.Id == "courier_station") courierTier = b.Tier;
                else if (b.Id == "embassy") _embassyTier = b.Tier;
                else if (b.Id == "war_room") warRoomTier = b.Tier;
            }
        }
        if (debugTable)
        {
            if (NegotiationDebug.CourierTier >= 0) courierTier = NegotiationDebug.CourierTier;
            if (NegotiationDebug.EmbassyTier >= 0) _embassyTier = NegotiationDebug.EmbassyTier;
            if (NegotiationDebug.WarRoomTier >= 0) warRoomTier = NegotiationDebug.WarRoomTier;
        }

        _state.Initialize(_data, school, party, factionRep, patronToken, patronTokens,
                          openingHand: _embassyTier >= 2 ? NegotiationTuning.EmbassyOpeningHand : -1);

        if (debugTable)
        {
            if (NegotiationDebug.ForceGrievance)
                _state.ForceGrievance("debug: forced grievance");
            foreach (var kvp in NegotiationDebug.ExtraTokens)
                for (int i = 0; i < kvp.Value; i++)
                    _state.AddCardToHand(ParleyCardLibrary.TokenCardId(kvp.Key), "debug");
            if (NegotiationDebug.RevealAll)
                foreach (var c in _state.Clauses) { c.Revealed = true; c.BandKnown = true; }
            _portrait.VoiceEnabled = NegotiationDebug.Voice;
            _showDetails = NegotiationDebug.ShowDetails;
            _detailsToggle.ButtonPressed = _showDetails;
        }

        if (priorLifeTables > 0)
            _state.ApplyChronicleFamiliarity(priorLifeTables, priorLifeCollapse);
        if (lastThisCycle != null)
            _state.ApplyContinuity(lastThisCycle.Outcome, lastThisCycle.Stars);

        // §6c: a completed dossier on this kingdom's archmage arms an argument.
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

        // Building hooks: Courier Station dossier, War Room.
        if (courierTier > 0) _state.ApplyCourierDossier(courierTier);
        if (warRoomTier > 0) _state.ApplyWarRoom();

        GD.Print($"[Negotiation] opened: goodwill={_state.Goodwill} ({_state.Mood}), " +
                 $"patience={_state.Patience}, par=+{_state.Par}, factionRep={factionRep}, " +
                 $"grievance={(_state.HasGrievance ? _state.Grievance : "none")}.");

        _titleLabel.Text = _data.Title;
        _npcNameLabel.Text = priorLifeTables > 0 ? $"❖ {_data.NpcName}" : _data.NpcName;
        if (priorLifeTables > 0)
        {
            _npcNameLabel.MouseFilter = MouseFilterEnum.Pass;
            _npcNameLabel.TooltipText = priorLifeTables == 1
                ? "The chronicle remembers this table from another life."
                : $"The chronicle remembers this table from {priorLifeTables} other lives.";
        }
        _npcSubLabel.Text = $"{_data.Archetype}  ·  {_data.Title}";

        _portrait.Setup(_data.Archetype);
        _portrait.SetMood(_state.Mood);
        _portrait.SetExpression(_state.Expression);
        _portrait.SetPosture(_state.Posture, animate: false);
        // The opening line already reached the portrait through AppendLog
        // (NpcLine) during Initialize.

        RefreshAll();
    }

    private static LeverageToken PatronTokenForArchetype(string archetype) => archetype switch
    {
        "Merchant" => LeverageToken.Offering,
        "Commander" => LeverageToken.Intimidate,
        "Scholar" => LeverageToken.Insight,
        "Idealist" => LeverageToken.Charm,
        "Opportunist" => LeverageToken.Persuade,
        "Survivor" => LeverageToken.Connections,
        _ => LeverageToken.Connections,
    };

    // ═══════════════════════════════════════════════════════════════════
    // UI building
    // ═══════════════════════════════════════════════════════════════════

    private void BuildUI()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;

        AddChild(new ColorRect { Color = UITheme.NegotiationBg, AnchorRight = 1f, AnchorBottom = 1f });

        var root = new VBoxContainer
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            OffsetLeft = 20, OffsetTop = 88, OffsetRight = -20, OffsetBottom = -14,
        };
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        _titleLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        if (BoldFont != null) _titleLabel.AddThemeFontOverride("font", BoldFont);
        _titleLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTitleFontSize);
        _titleLabel.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        root.AddChild(_titleLabel);

        // ── NPC strip (v3.6): the person is the largest thing on screen ───
        var strip = new PanelContainer();
        strip.AddThemeStyleboxOverride("panel", PanelStyle(UITheme.BgRaised, UITheme.VioletDim, 10, 14));
        root.AddChild(strip);
        var stripRow = new HBoxContainer();
        stripRow.AddThemeConstantOverride("separation", 22);
        strip.AddChild(stripRow);

        _portrait = new NegotiationPortrait { CustomMinimumSize = new Vector2(250, 250) };
        stripRow.AddChild(_portrait);

        var nameCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        nameCol.AddThemeConstantOverride("separation", 4);
        stripRow.AddChild(nameCol);
        _npcNameLabel = new Label();
        if (BoldFont != null) _npcNameLabel.AddThemeFontOverride("font", BoldFont);
        _npcNameLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTitleFontSize);
        _npcNameLabel.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        nameCol.AddChild(_npcNameLabel);
        _npcSubLabel = new Label();
        _npcSubLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _npcSubLabel.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        nameCol.AddChild(_npcSubLabel);
        _barkLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Top,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _barkLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationNpcFontSize);
        _barkLabel.AddThemeColorOverride("font_color", UITheme.NegotiationBodyColor);
        nameCol.AddChild(_barkLabel);
        _grievanceLabel = MakeTinyLabel("", ColTheirs);
        _grievanceLabel.Visible = false;
        nameCol.AddChild(_grievanceLabel);

        // Meters under the line: goodwill | patience, each its own block.
        var meters = new HBoxContainer();
        meters.AddThemeConstantOverride("separation", 40);
        nameCol.AddChild(meters);

        var gwBlock = new VBoxContainer();
        gwBlock.AddThemeConstantOverride("separation", 6);
        meters.AddChild(gwBlock);
        var gwHead = new HBoxContainer();
        gwHead.AddThemeConstantOverride("separation", 10);
        var gwCap = new Label { Text = "GOODWILL" };
        gwCap.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        gwCap.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        gwHead.AddChild(gwCap);
        _moodLabel = new Label();
        if (BoldFont != null) _moodLabel.AddThemeFontOverride("font", BoldFont);
        _moodLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationHeaderFontSize);
        _moodLabel.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        gwHead.AddChild(_moodLabel);
        gwBlock.AddChild(gwHead);
        _goodwillBar = new HBoxContainer { TooltipText = "How they feel about you. Cold: refusals cost more. Warm: sealed terms open and they add a gift at the close." };
        _goodwillBar.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < NegotiationTuning.GoodwillMax; i++)
        {
            _goodwillCells[i] = new ColorRect { CustomMinimumSize = new Vector2(30, 22) };
            _goodwillBar.AddChild(_goodwillCells[i]);
        }
        gwBlock.AddChild(_goodwillBar);

        var patBlock = new VBoxContainer();
        patBlock.AddThemeConstantOverride("separation", 6);
        meters.AddChild(patBlock);
        _patienceCaption = new Label();
        _patienceCaption.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _patienceCaption.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        _patienceCaption.TooltipText = "Every card except Hold, Draw and Recall spends one. At none, they leave.";
        _patienceCaption.MouseFilter = MouseFilterEnum.Pass;
        patBlock.AddChild(_patienceCaption);
        _patienceBar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 22) };
        _patienceBar.AddThemeConstantOverride("separation", 4);
        patBlock.AddChild(_patienceBar);

        _postureLabel = new Label();
        _postureLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _postureLabel.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        _postureLabel.TooltipText = "How they're sitting. Give them things they want and they lean in; take and they fold their arms.";
        _postureLabel.MouseFilter = MouseFilterEnum.Pass;
        nameCol.AddChild(_postureLabel);

        // ── The table: three columns ──────────────────────────────────────
        _tableRow = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _tableRow.AddThemeConstantOverride("separation", 12);
        root.AddChild(_tableRow);

        _termsCol = MakeColumn("ON THE TABLE", new Color(0.18f, 0.23f, 0.20f), new Color(0.27f, 0.32f, 0.27f), out var termsBox, stretch: 1f);
        _npcCardRow = new HBoxContainer { TooltipText = "Their cards. They play these when their patience says so; a Flip turns one early." };
        _npcCardRow.AddThemeConstantOverride("separation", 6);
        var npcRowWrap = new HBoxContainer();
        npcRowWrap.AddThemeConstantOverride("separation", 10);
        var npcCap = MakeTinyLabel("THEIR CARDS", new Color(0.72f, 0.68f, 0.58f));
        npcCap.VerticalAlignment = VerticalAlignment.Center;
        npcRowWrap.AddChild(npcCap);
        npcRowWrap.AddChild(_npcCardRow);
        termsBox.AddChild(npcRowWrap);
        termsBox.MoveChild(npcRowWrap, 1);

        // ── Your hand: a strip of its own under the table ────────────────
        var handPanel = new PanelContainer();
        handPanel.AddThemeStyleboxOverride("panel", PanelStyle(new Color(0.23f, 0.19f, 0.16f), new Color(0.35f, 0.30f, 0.24f), 8, 10));
        root.AddChild(handPanel);
        var ledgerBox = new VBoxContainer();
        ledgerBox.AddThemeConstantOverride("separation", 6);
        handPanel.AddChild(ledgerBox);
        var handHead = new HBoxContainer();
        handHead.AddChild(MakeTinyLabel("YOUR HAND", UITheme.NegotiationNpcColor));
        handHead.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _deckLabel = MakeTinyLabel("", UITheme.NegotiationHiddenTerm);
        _deckLabel.TooltipText = "Cards left in your parley deck. One is drawn after every action; there is no reshuffle.";
        _deckLabel.MouseFilter = MouseFilterEnum.Pass;
        handHead.AddChild(_deckLabel);
        ledgerBox.AddChild(handHead);
        _handRow = new HBoxContainer();
        _handRow.AddThemeConstantOverride("separation", 6);
        ledgerBox.AddChild(_handRow);

        // ── Bottom bar ────────────────────────────────────────────────────
        var bottom = new PanelContainer();
        bottom.AddThemeStyleboxOverride("panel", PanelStyle(UITheme.BgRaised, UITheme.VioletDim, 8, 8));
        root.AddChild(bottom);
        var bottomRow = new HBoxContainer();
        bottomRow.AddThemeConstantOverride("separation", 10);
        bottom.AddChild(bottomRow);

        _hintLabel = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _hintLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        _hintLabel.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        bottomRow.AddChild(_hintLabel);

        // ── The conversation ──────────────────────────────────────────────
        var logHeader = new HBoxContainer();
        logHeader.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _detailsToggle = new CheckButton
        {
            Text = "Table details",
            ButtonPressed = false,
            TooltipText = "Also show the arithmetic: goodwill, their ledger, patience.",
        };
        _detailsToggle.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
        _detailsToggle.Toggled += pressed => { _showDetails = pressed; RenderLog(); };
        logHeader.AddChild(_detailsToggle);
        root.AddChild(logHeader);

        var logPanel = new PanelContainer { CustomMinimumSize = new Vector2(0, 90) };
        logPanel.AddThemeStyleboxOverride("panel", PanelStyle(UITheme.BgDeep, UITheme.VioletDim, 8, 8));
        root.AddChild(logPanel);
        _logScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        logPanel.AddChild(_logScroll);
        _logLabel = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _logLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationDetailFontSize);
        _logScroll.AddChild(_logLabel);

        // ── FX wash (above everything but the modals) ─────────────────────
        _wash = new ColorRect
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            Color = new Color(1, 1, 1, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_wash);

        // ── Counter modal ─────────────────────────────────────────────────
        _counterPanel = MakeModalPanel(330, 175);
        var counterLayout = MakeModalLayout(_counterPanel);
        _counterLabel = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _counterLabel.AddThemeFontSizeOverride("normal_font_size", UITheme.NegotiationBodyFontSize);
        _counterLabel.AddThemeFontSizeOverride("bold_font_size", UITheme.NegotiationBodyFontSize);
        _counterLabel.AddThemeFontSizeOverride("italics_font_size", UITheme.NegotiationBodyFontSize);
        counterLayout.AddChild(_counterLabel);
        var counterButtons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        counterButtons.AddThemeConstantOverride("separation", 12);
        counterLayout.AddChild(counterButtons);
        _counterAcceptBtn = new Button { Text = "Agree", CustomMinimumSize = new Vector2(200, 40) };
        _counterAcceptBtn.Pressed += () => { _counterPanel.Visible = false; _state.AcceptCounter(); RefreshAll(); };
        counterButtons.AddChild(_counterAcceptBtn);
        _counterDeclineBtn = new Button { Text = "Decline", CustomMinimumSize = new Vector2(160, 40) };
        _counterDeclineBtn.Pressed += () => { _counterPanel.Visible = false; _state.DeclineCounter(); RefreshAll(); };
        counterButtons.AddChild(_counterDeclineBtn);

        // ── Settlement preview (v3.3): what a handshake signs right now ───
        _settlePanel = MakeModalPanel(330, 190);
        var settleLayout = MakeModalLayout(_settlePanel);
        _settleLabel = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _settleLabel.AddThemeFontSizeOverride("normal_font_size", UITheme.NegotiationBodyFontSize);
        _settleLabel.AddThemeFontSizeOverride("bold_font_size", UITheme.NegotiationBodyFontSize);
        settleLayout.AddChild(_settleLabel);
        var settleButtons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        settleButtons.AddThemeConstantOverride("separation", 12);
        settleLayout.AddChild(settleButtons);
        _settleSignBtn = new Button { Text = "Shake on it", CustomMinimumSize = new Vector2(180, 40) };
        _settleSignBtn.Pressed += () => { _settlePanel.Visible = false; DoShake(); };
        settleButtons.AddChild(_settleSignBtn);
        _settleWaitBtn = new Button { Text = "Not yet", CustomMinimumSize = new Vector2(140, 40) };
        _settleWaitBtn.Pressed += () => { _settlePanel.Visible = false; };
        settleButtons.AddChild(_settleWaitBtn);

        // ── Squeeze modal ─────────────────────────────────────────────────
        _squeezePanel = MakeModalPanel(330, 190);
        var squeezeLayout = MakeModalLayout(_squeezePanel);
        _squeezeLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
        _squeezeLabel.AddThemeFontSizeOverride("font_size", UITheme.NegotiationBodyFontSize);
        squeezeLayout.AddChild(_squeezeLabel);
        var squeezeButtons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        squeezeButtons.AddThemeConstantOverride("separation", 12);
        squeezeLayout.AddChild(squeezeButtons);
        _squeezeConcedeBtn = new Button { CustomMinimumSize = new Vector2(170, 40) };
        _squeezeConcedeBtn.Pressed += () => { _squeezePanel.Visible = false; _state.ResolveSqueezeConcede(_pendingSqueeze); _pendingSqueeze = null; RefreshAll(); };
        squeezeButtons.AddChild(_squeezeConcedeBtn);
        _squeezeHoldBtn = new Button { CustomMinimumSize = new Vector2(170, 40) };
        _squeezeHoldBtn.Pressed += () => { _squeezePanel.Visible = false; _state.ResolveSqueezeHoldFirm(_pendingSqueeze); _pendingSqueeze = null; RefreshAll(); };
        squeezeButtons.AddChild(_squeezeHoldBtn);
        _squeezeWithdrawBtn = new Button { Text = "Withdraw your hand", CustomMinimumSize = new Vector2(170, 40) };
        _squeezeWithdrawBtn.Pressed += () => { _squeezePanel.Visible = false; _state.ResolveSqueezeWithdraw(); _pendingSqueeze = null; RefreshAll(); };
        squeezeButtons.AddChild(_squeezeWithdrawBtn);

        // ── Result panel (the receipt) ────────────────────────────────────
        _resultPanel = MakeModalPanel(340, 270);
        var resultLayout = MakeModalLayout(_resultPanel);
        _resultContent = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _resultContent.AddThemeConstantOverride("separation", 6);
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

    private VBoxContainer MakeColumn(string header, Color bg, Color border, out VBoxContainer box, float stretch = 1f)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = stretch };
        panel.AddThemeStyleboxOverride("panel", PanelStyle(bg, border, 8, 10));
        _tableRow.AddChild(panel);
        box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        var h = MakeTinyLabel(header, new Color(0.81f, 0.78f, 0.69f));
        box.AddChild(h);
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        box.AddChild(scroll);
        var slips = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        slips.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(slips);
        return slips;
    }

    private static StyleBoxFlat PanelStyle(Color bg, Color border, int radius, int margin)
    {
        return new StyleBoxFlat
        {
            BgColor = bg, BorderColor = border,
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginTop = margin, ContentMarginBottom = margin,
            ContentMarginLeft = margin, ContentMarginRight = margin,
        };
    }

    private Panel MakeModalPanel(float halfW, float halfH)
    {
        var panel = new Panel
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both, GrowVertical = GrowDirection.Both,
            OffsetLeft = -halfW, OffsetTop = -halfH, OffsetRight = halfW, OffsetBottom = halfH,
            Visible = false,
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UITheme.NegotiationResultBg,
            BorderColor = UITheme.NegotiationResultBorder,
            BorderWidthTop = UITheme.BorderWidth, BorderWidthBottom = UITheme.BorderWidth,
            BorderWidthLeft = UITheme.BorderWidth, BorderWidthRight = UITheme.BorderWidth,
            CornerRadiusTopLeft = UITheme.NarrativePanelCorner, CornerRadiusTopRight = UITheme.NarrativePanelCorner,
            CornerRadiusBottomLeft = UITheme.NarrativePanelCorner, CornerRadiusBottomRight = UITheme.NarrativePanelCorner,
        });
        AddChild(panel);
        return panel;
    }

    private VBoxContainer MakeModalLayout(Panel host)
    {
        var layout = new VBoxContainer
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            OffsetLeft = 24, OffsetTop = 24, OffsetRight = -24, OffsetBottom = -24,
        };
        layout.AddThemeConstantOverride("separation", 16);
        host.AddChild(layout);
        return layout;
    }

    private Label MakeTinyLabel(string text, Color color)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Refresh
    // ═══════════════════════════════════════════════════════════════════

    private void RefreshAll()
    {
        if (_state == null) return;
        Trace($"refresh: turn={_state.TurnNumber} patience={_state.Patience} hand={_state.Hand.Count} cardPlayed={_state.CardPlayedThisTurn} counter={(_state.PendingCounter != null)} resolved={_state.IsResolved}");
        RefreshMeters();
        RefreshSlips();
        RefreshHand();
        RefreshNpcCards();
        RefreshBottom();
        _portrait.SetExpression(_state.Expression);
        _portrait.SetMood(_state.Mood);
        _portrait.SetPosture(_state.Posture, animate: true);
        _hoverCard = null;   // the tiles were rebuilt; MouseEntered fires again if the pointer stays
        if (_state.PendingCounter != null && !_counterPanel.Visible)
            ShowCounterModal(_state.PendingCounter);
        // Effects play one frame later, once the rebuilt slips have a size
        // (pivots and drop-in offsets need real layout).
        Callable.From(PlayPendingFx).CallDeferred();
    }

    private void RefreshMeters()
    {
        int g = _state.Goodwill;
        Color fill = _state.Mood switch
        {
            NegotiationMood.Warm => UITheme.TensionCordial,
            NegotiationMood.Cold => UITheme.TensionHostile,
            _ => UITheme.NegotiationTitleColor,
        };
        _moodLabel.Text = _state.Mood.ToString().ToUpperInvariant();
        _moodLabel.AddThemeColorOverride("font_color", fill);
        for (int i = 0; i < _goodwillCells.Length; i++)
            _goodwillCells[i].Color = i < g ? fill : UITheme.TensionEmpty;

        // Pips = the base clock, growing only if the final-warning refund pushes past it.
        int total = Mathf.Max(1, Mathf.Max(_state.BasePatience, _state.Patience));
        int remaining = Mathf.Clamp(_state.Patience, 0, total);
        if (_patienceBar.GetChildCount() != total)
        {
            foreach (var child in _patienceBar.GetChildren()) child.QueueFree();
            for (int i = 0; i < total; i++)
                _patienceBar.AddChild(new ColorRect { CustomMinimumSize = new Vector2(30, 22) });
        }
        Color pfill = remaining <= 2 ? ColTheirs : ColInfo;
        int i2 = 0;
        foreach (var child in _patienceBar.GetChildren())
            if (child is ColorRect pip) pip.Color = i2++ < remaining ? pfill : UITheme.TensionEmpty;
        _patienceCaption.Text = _state.IsResolved ? "PATIENCE"
            : remaining <= 2 ? $"PATIENCE · {remaining} move{(remaining == 1 ? "" : "s")} before they walk"
            : $"PATIENCE · {remaining} moves";

        // v3.1: no credit number. Their posture is the whole read.
        _postureLabel.Text = _state.IsResolved ? "" : $"{_data.NpcName.Split(' ')[0]} is {ParleyTells.PostureWord(_state.Posture)}" +
                             (_state.ForceArmed ? "  ·  your next ask is not a question" : "");

        _grievanceLabel.Visible = _state.HasGrievance;
        _grievanceLabel.Text = _state.HasGrievance
            ? $"⚠ Grievance: {_state.Grievance}. Expect one last demand at the handshake."
            : "";
    }

    private void RefreshSlips()
    {
        foreach (var child in _termsCol.GetChildren()) child.QueueFree();
        _slipNodes.Clear();
        _slipRestModulate.Clear();
        _prevPositions.Clear();
        foreach (var kv in _shownPositions) _prevPositions[kv.Key] = kv.Value;
        _shownPositions.Clear();
        _trackMarkers.Clear();

        bool anyOpen = false;
        // v3.6: each category is a divider and a grid of cards, three across.
        foreach (ClauseCategory cat in Enum.GetValues(typeof(ClauseCategory)))
        {
            var group = _state.Clauses.Where(c => c.State != ClauseState.Struck && ClauseCategories.Of(c.Kind) == cat).ToList();
            if (group.Count == 0) continue;
            _termsCol.AddChild(MakeDivider(ClauseCategories.Word(cat).ToUpperInvariant()));
            var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 10);
            grid.AddThemeConstantOverride("v_separation", 10);
            _termsCol.AddChild(grid);
            foreach (var c in group)
            {
                var cardNode = BuildSlip(c);
                _slipNodes[c.Id] = cardNode;
                _slipRestModulate[c.Id] = cardNode.Modulate;
                grid.AddChild(cardNode);
                anyOpen = true;
            }
        }
        if (!anyOpen) _termsCol.AddChild(MakeTinyLabel("Nothing on the table.", UITheme.NegotiationHiddenTerm));
    }

    private static Color ToneColor(ParleyTone t) => t switch
    {
        ParleyTone.Warm => UITheme.TensionCordial,
        ParleyTone.Hard => UITheme.TensionHostile,
        _ => UITheme.Violet,
    };

    private void RefreshHand()
    {
        foreach (var child in _handRow.GetChildren()) child.QueueFree();
        _cardNodes.Clear();
        _deckLabel.Text = _state.Deck.Count == 0 ? "deck empty" : $"deck {_state.Deck.Count}";
        foreach (var fixedCard in _state.FixedCards)
        {
            if (fixedCard.Effect == ParleyEffect.SchoolMove && _state.SchoolMoveUsed) continue;
            var view = BuildCardView(fixedCard);
            _cardNodes[fixedCard.InstanceId] = view;
            _handRow.AddChild(view);
        }
        var gap = new VSeparator();
        _handRow.AddChild(gap);
        if (_state.Hand.Count == 0)
        {
            _handRow.AddChild(MakeTinyLabel("no cards in hand", UITheme.NegotiationHiddenTerm));
            return;
        }
        foreach (var card in _state.Hand)
        {
            var view = BuildCardView(card);
            _cardNodes[card.InstanceId] = view;
            _handRow.AddChild(view);
        }
    }

    /// <summary>One card in the hand: a small parchment tile with a tone edge,
    /// the name, and the rules text as a tooltip. Click to play (or to arm
    /// targeting). Dim when it can't be played this turn.</summary>
    private Control BuildCardView(ParleyCard card)
    {
        bool playable = _state.CanPlay(card);
        bool armed = false;
        Color edge = card.Effect switch
        {
            ParleyEffect.Pull or ParleyEffect.Claim => ColYours,          // moves or locks things toward you
            ParleyEffect.Concede or ParleyEffect.Walk or ParleyEffect.Force => ColTheirs,   // gives ground or burns it
            ParleyEffect.Shake or ParleyEffect.Warm or ParleyEffect.Sweeten or ParleyEffect.Purse or ParleyEffect.Display or ParleyEffect.Bluff => ColDeal,
            ParleyEffect.SchoolMove => SchoolColor(_state.School),
            _ => ColInfo,                                                 // reads, flips, holds, draws
        };
        var tile = new PanelContainer { CustomMinimumSize = new Vector2(130, 84), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = card.IsFixed ? new Color(0.86f, 0.82f, 0.72f) : Parchment,
            BorderColor = edge,
            BorderWidthLeft = 2, BorderWidthTop = 5, BorderWidthBottom = 2, BorderWidthRight = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginTop = 8, ContentMarginBottom = 8, ContentMarginLeft = 10, ContentMarginRight = 8,
            ShadowColor = new Color(0, 0, 0, 0.40f), ShadowSize = 4, ShadowOffset = new Vector2(0, 2),
        };
        tile.AddThemeStyleboxOverride("panel", style);
        if (!playable && !armed) tile.Modulate = new Color(1, 1, 1, 0.45f);
        string rules = card.Effect == ParleyEffect.SchoolMove ? _state.SchoolMoveDescription() + "  (once per table)" : card.RulesText;
        tile.TooltipText = $"{card.Name}\n{rules}" +
                           (card.IsFixed ? "" : $"\n{card.Tone.ToString().ToLowerInvariant()} · {card.Source.Replace("school:", "")}" + (card.IsFree ? " · free" : " · spends the turn")) +
                           (playable ? "" : "\n(nothing for it to do right now)");
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        tile.AddChild(box);
        var name = new Label { Text = card.Name, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        if (BoldFont != null) name.AddThemeFontOverride("font", BoldFont);
        name.AddThemeFontSizeOverride("font_size", UITheme.NegotiationHeaderFontSize);
        name.AddThemeColorOverride("font_color", Ink);
        box.AddChild(name);
        string subText;
        if (card.Effect is ParleyEffect.Claim or ParleyEffect.Concede)
        {
            var targets = card.Effect == ParleyEffect.Claim ? _state.ClaimTargets(card) : _state.ConcedeTargets(card);
            subText = targets.Count == 0 ? $"{card.Effect.ToString().ToLowerInvariant()} · nothing"
                    : targets.Count == 1 ? $"{card.Effect.ToString().ToLowerInvariant()} the {NegotiationState.ShortName(targets[0])}"
                    : $"{card.Effect.ToString().ToLowerInvariant()} {targets.Count}: {string.Join(", ", targets.Select(NegotiationState.ShortName))}";
        }
        else if (card.IsFixed) subText = card.Effect == ParleyEffect.SchoolMove ? "signature · free" : card.Effect == ParleyEffect.Shake ? "ends the table" : "no deal";
        else subText = card.Sweeps
            ? (playable && _state.ResolveCategory(card) is ClauseCategory rc
                ? $"{card.Effect.ToString().ToLowerInvariant()} · {(card.Reach == ParleyReach.All ? "all" : "some")} {ClauseCategories.Word(rc)}"
                : $"{card.Effect.ToString().ToLowerInvariant()} · {card.SweepWord}")
            : card.Effect.ToString().ToLowerInvariant() + (card.IsFree ? " · free" : "");
        if (!card.IsFixed && card.Tone != ParleyTone.Level) subText += card.Tone == ParleyTone.Warm ? "  ·  warm" : "  ·  hard";
        var sub = MakeTinyLabel(subText, InkSoft);
        sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(sub);
        tile.MouseFilter = MouseFilterEnum.Stop;
        tile.MouseDefaultCursorShape = playable ? CursorShape.PointingHand : CursorShape.Forbidden;
        var target = card;
        tile.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (playable) OnCardClicked(target);
                else _hintLabel.Text = $"{target.Name} has nothing to do right now.";
            }
        };
        tile.MouseEntered += () => HoverCard(target);
        tile.MouseExited += () => { if (_hoverCard == target) HoverCard(null); };
        return tile;
    }

    /// <summary>The terms a card could touch if played now. For a Some-reach
    /// sweep this is every candidate (the random pick is made on play), so
    /// the highlight is honest about "may".</summary>
    private List<string> AffectedTermIds(ParleyCard card)
    {
        if (card == null || _state == null) return new List<string>();
        switch (card.Effect)
        {
            case ParleyEffect.Read:
            case ParleyEffect.Pull:
            case ParleyEffect.Sweeten:
                return _state.SweepCandidates(card).Select(c => c.Id).ToList();
            case ParleyEffect.Claim:
                return _state.ClaimTargets(card).Select(c => c.Id).ToList();
            case ParleyEffect.Concede:
                return _state.ConcedeTargets(card).Select(c => c.Id).ToList();
            case ParleyEffect.ReadTop:
                return _state.TopWant() is Clause tw ? new List<string> { tw.Id } : new List<string>();
            case ParleyEffect.Bluff:
                return _state.OpenTheirs.Where(c => !c.RequiresWarm).OrderBy(c => c.NpcValue).ThenBy(c => c.Value).Take(1).Select(c => c.Id).ToList();
            case ParleyEffect.Force:
                return _state.OpenTheirs.Select(c => c.Id).ToList();
            case ParleyEffect.Shake:
            {
                var (taken, swept, _) = _state.PreviewSettlement();
                return taken.Concat(swept).Select(c => c.Id).ToList();
            }
            default:
                return new List<string>();
        }
    }

    /// <summary>Hovering a card lights the terms it may touch and dims the
    /// rest; leaving restores every term's resting look. Pure modulate — no
    /// rebuild, so it is cheap and cannot disturb a running FX tween's node.</summary>
    private void HoverCard(ParleyCard card)
    {
        _hoverCard = card;
        var ids = card != null && _state.CanPlay(card) ? AffectedTermIds(card) : new List<string>();
        bool any = ids.Count > 0;
        Color lit = card == null ? Colors.White
                  : card.Effect is ParleyEffect.Concede or ParleyEffect.Walk or ParleyEffect.Force ? new Color(1.0f, 0.82f, 0.78f)
                  : card.Effect is ParleyEffect.Claim or ParleyEffect.Pull ? new Color(0.80f, 1.0f, 0.82f)
                  : card.Effect is ParleyEffect.Shake or ParleyEffect.Sweeten or ParleyEffect.Bluff ? new Color(1.0f, 0.95f, 0.72f)
                  : new Color(0.86f, 0.80f, 1.0f);
        foreach (var kv in _slipNodes)
        {
            if (!GodotObject.IsInstanceValid(kv.Value)) continue;
            var rest = _slipRestModulate.TryGetValue(kv.Key, out var r) ? r : Colors.White;
            if (!any) { kv.Value.Modulate = rest; continue; }
            kv.Value.Modulate = ids.Contains(kv.Key) ? lit : new Color(rest.R, rest.G, rest.B, rest.A * 0.45f);
        }
        if (card != null && any)
            _hintLabel.Text = $"{card.Name}: " + (card.Reach == ParleyReach.Some && card.Sweeps && card.Effect is not (ParleyEffect.Claim or ParleyEffect.Concede)
                ? $"may touch {ids.Count} of the lit terms (a random {Math.Min(NegotiationTuning.SweepSomeCount, ids.Count)})."
                : $"touches the lit term{(ids.Count == 1 ? "" : "s")}.");
        else if (card == null) RefreshBottom();
    }

    /// <summary>Their row: face-down backs until a beat plays them or a Flip
    /// turns them. Played cards stay, face-up and dimmed, as a record.</summary>
    private void RefreshNpcCards()
    {
        foreach (var child in _npcCardRow.GetChildren()) child.QueueFree();
        _npcCardNodes.Clear();
        foreach (var n in _state.NpcCards)
        {
            var tile = new PanelContainer { CustomMinimumSize = new Vector2(70, 34) };
            bool faceUp = n.FaceUp || n.Played;
            var style = new StyleBoxFlat
            {
                BgColor = faceUp ? Parchment : new Color(0.24f, 0.18f, 0.32f),
                BorderColor = n.Kind == NpcCardKind.Squeeze ? ColTheirs : faceUp ? ColInfo : UITheme.VioletDim,
                BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
                ContentMarginTop = 3, ContentMarginBottom = 3, ContentMarginLeft = 6, ContentMarginRight = 6,
            };
            tile.AddThemeStyleboxOverride("panel", style);
            if (n.Played) tile.Modulate = new Color(1, 1, 1, 0.5f);
            var target = _state.Find(n.TargetClauseId);
            string text = !faceUp ? "· · ·" : n.Name;
            var lbl = MakeTinyLabel(text, faceUp ? Ink : new Color(0.7f, 0.62f, 0.85f));
            lbl.HorizontalAlignment = HorizontalAlignment.Center;
            tile.AddChild(lbl);
            tile.MouseFilter = MouseFilterEnum.Pass;
            tile.TooltipText = !faceUp
                ? (n.Kind == NpcCardKind.Final ? "Face-down. They'll play it when their patience is nearly gone." : "Face-down. They'll play it partway through.")
                : n.Played ? $"{n.Name} — played." + (target != null ? $" It named the {NegotiationState.ShortName(target)}." : "")
                : n.Kind == NpcCardKind.Squeeze ? $"{n.Name} — they hold a grievance. At the handshake they'll want" + (target != null ? $" the {NegotiationState.ShortName(target)}." : " one more thing.")
                : $"{n.Name} — when it plays, it names" + (target != null ? $" the {NegotiationState.ShortName(target)}." : " something of yours.");
            _npcCardRow.AddChild(tile);
            _npcCardNodes[n.Kind] = tile;
        }
        if (_state.NpcCards.Count == 0)
            _npcCardRow.AddChild(MakeTinyLabel("", UITheme.NegotiationHiddenTerm));
    }

    private void RefreshBottom()
    {
        bool done = _state.IsResolved;

        if (done) _hintLabel.Text = "The table is closed.";
        else if (_state.PendingCounter != null) _hintLabel.Text = "They've made a counter-offer.";
        else if (_state.BundleArmed) _hintLabel.Text = "Bundle armed: your next Claim takes contested terms too.";
        else if (_state.ForceArmed) _hintLabel.Text = "Your next Claim goes through whether they like it or not.";
        else if (_state.IsWaiting) _hintLabel.Text = "Clock held — your next card won't cost patience.";

        else if (_state.HasGrievance && _state.PredictSqueezeTarget() != null)
            _hintLabel.Text = $"A handshake now draws one last demand: the {NegotiationState.ShortName(_state.PredictSqueezeTarget())}.";
        else _hintLabel.Text = NextStepHint();
    }

    /// <summary>After the NPC has spoken, say what the table wants from you
    /// next. Reaction-specific so the beat lines don't dead-end.</summary>
    private string NextStepHint()
    {
        var r = _lastReaction;
        if (r == null) return "Play a card. Pull cards move terms toward you; Claim and Concede lock them; every card is a turn.";
        string name(int i) => r.ClauseIds.Count > i ? NegotiationState.ShortName(_state.Find(r.ClauseIds[i])) : "";
        switch (r.Kind)
        {
            case ReactionKind.Agree:
                return $"Locked — the {name(0)} is yours in the ledger. Pull more, concede for credit, or shake hands.";
            case ReactionKind.Concede:
                return $"Conceded — the {name(0)} is theirs and your credit rose. Pull, then claim.";
            case ReactionKind.Refuse:
                return $"Refused — the {name(0)} is off the table and goodwill fell. Concede for credit, or pull further before you claim.";
            case ReactionKind.Beat:
            {
                var want = _state.NamedWant;
                if (_state.Patience <= 1 && want != null)
                    return $"Last chance: give the {NegotiationState.ShortName(want)} now and they'll stay, or shake hands with what you have.";
                if (want != null)
                    return $"They've named their price: the {NegotiationState.ShortName(want)}. Give it if it's cheap to you, or keep asking.";
                return $"They've added a condition to the {name(0)}. Persuade can strike it; or ask anyway.";
            }
            case ReactionKind.Probe:
            case ReactionKind.Reveal:
            case ReactionKind.CallIn:
                return "You've read them. Claim what's cheap to them and dear to you.";
            case ReactionKind.Pull:
                return r.ClauseIds.Count > 1 ? "Those slid your way. A Claim card locks what's at yours." : $"The {name(0)} slid your way. A Claim card locks it.";
            case ReactionKind.TheirPull:
                return r.Band > 0 ? "They pulled a whole category back. Pull it your way, or Concede what they've taken for credit." : $"They pulled the {name(0)} back. Pull, or Concede it for credit.";
            case ReactionKind.Charm:
                return "Goodwill is up, which is credit. Spend it on an ask.";
            case ReactionKind.Press:
                return "Forced through — and they'll remember it at the handshake.";
            case ReactionKind.Flip:
                return "One of their cards is face-up. You know what it will name; decide whether to have it ready.";
            case ReactionKind.Bluff:
                return r.Band > 0 ? "They gave ground. Take the turn." : "They called it. Goodwill fell; give something before you ask again.";
            case ReactionKind.Draw:
            case ReactionKind.Card:
                return "Play on.";
            case ReactionKind.Offer:
            case ReactionKind.Demonstrate:
                return "It's on your side of the table. Give it when you want the credit.";
            case ReactionKind.Wait:
                return "Clock held: your next action won't cost patience.";
            default:
                return "Play a card.";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Slips
    // ═══════════════════════════════════════════════════════════════════

    // v3.6 colour language: red = theirs, green = yours, gold = the deal, violet = information.
    private static readonly Color ColTheirs = UITheme.TensionHostile;
    private static readonly Color ColYours = UITheme.TensionCordial;
    private static readonly Color ColDeal = UITheme.Gold;
    private static readonly Color ColInfo = UITheme.Violet;

    private static readonly Color Parchment = new Color(0.905f, 0.85f, 0.73f);
    private static readonly Color ParchmentAgreed = new Color(0.94f, 0.90f, 0.80f);
    private static readonly Color Ink = new Color(0.165f, 0.13f, 0.094f);
    private static readonly Color InkSoft = new Color(0.36f, 0.30f, 0.23f);
    private static readonly Color SealGold = new Color(0.72f, 0.57f, 0.23f);
    private static readonly Color RiderRed = new Color(0.54f, 0.23f, 0.17f);
    private static readonly Color SealAmber = new Color(0.54f, 0.43f, 0.12f);


    /// <summary>A category divider: a rule with the word set into it, gold
    /// because the categories are the deal's vocabulary.</summary>
    private Control MakeDivider(string word)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var left = new ColorRect { Color = new Color(ColDeal.R, ColDeal.G, ColDeal.B, 0.35f), CustomMinimumSize = new Vector2(24, 1), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(left);
        var lbl = new Label { Text = word };
        if (BoldFont != null) lbl.AddThemeFontOverride("font", BoldFont);
        lbl.AddThemeFontSizeOverride("font_size", UITheme.NegotiationSmallFontSize);
        lbl.AddThemeColorOverride("font_color", ColDeal);
        row.AddChild(lbl);
        var right = new ColorRect { Color = new Color(ColDeal.R, ColDeal.G, ColDeal.B, 0.35f), CustomMinimumSize = new Vector2(0, 1), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(right);
        return row;
    }

    /// <summary>The five-step track: theirs … contested … yours, with the
    /// term's marker. Pulls slide it; the claim button reads it.</summary>
    private const int TrackCellW = 24, TrackCellH = 16, TrackGap = 3;
    private static float TrackX(int position) => (position + NegotiationTuning.TrackMax) * (TrackCellW + TrackGap);

    /// <summary>The five-step track as a fixed-size canvas: five dim cells,
    /// and one marker panel that sits over the current cell. The marker is
    /// a separate node so a pull can slide it from the old cell to the new.</summary>
    private Control BuildTrack(Clause c)
    {
        int max = NegotiationTuning.TrackMax;
        int cells = max * 2 + 1;
        var track = new Control
        {
            CustomMinimumSize = new Vector2(cells * TrackCellW + (cells - 1) * TrackGap, TrackCellH),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Pass,
        };
        string word = NegotiationState.PositionWord(c);
        track.TooltipText = $"{char.ToUpperInvariant(word[0])}{word.Substring(1)}. Pull cards slide it toward you; they pull it back. " +
                            "Claim locks what's leaning yours or past; Concede gives what's leaning theirs or past. At the handshake, what leans theirs is theirs and what leans yours comes with you.";
        for (int p = -max; p <= max; p++)
        {
            var cell = new Panel
            {
                Position = new Vector2(TrackX(p), 0),
                Size = new Vector2(TrackCellW, TrackCellH),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            cell.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = p == 0 ? new Color(0, 0, 0, 0.20f) : new Color(0, 0, 0, 0.10f),
                BorderColor = new Color(0, 0, 0, 0.25f),
                BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
                CornerRadiusTopLeft = p == -max ? 9 : 2, CornerRadiusBottomLeft = p == -max ? 9 : 2,
                CornerRadiusTopRight = p == max ? 9 : 2, CornerRadiusBottomRight = p == max ? 9 : 2,
            });
            track.AddChild(cell);
        }
        int pos = c.Position;
        Color fill = pos < 0 ? ColTheirs.Lerp(ColDeal, pos == -1 ? 0.45f : 0f)
                   : pos > 0 ? ColYours.Lerp(ColDeal, pos == 1 ? 0.45f : 0f)
                   : ColDeal;
        var marker = new Panel
        {
            Position = new Vector2(TrackX(pos), 0),
            Size = new Vector2(TrackCellW, TrackCellH),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        marker.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = new Color(0, 0, 0, 0.35f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = pos == -max ? 9 : 3, CornerRadiusBottomLeft = pos == -max ? 9 : 3,
            CornerRadiusTopRight = pos == max ? 9 : 3, CornerRadiusBottomRight = pos == max ? 9 : 3,
            ShadowColor = new Color(0, 0, 0, 0.3f), ShadowSize = 2, ShadowOffset = new Vector2(0, 1),
        });
        track.AddChild(marker);
        _trackMarkers[c.Id] = marker;
        _shownPositions[c.Id] = pos;
        return track;
    }

    /// <summary>After a refresh, slide the marker from where it was drawn last
    /// time to where it is now — the pull you can watch happen.</summary>
    private void SlideMarker(string id)
    {
        if (!_trackMarkers.TryGetValue(id, out var m) || !GodotObject.IsInstanceValid(m)) return;
        if (!_prevPositions.TryGetValue(id, out int from) || !_shownPositions.TryGetValue(id, out int to) || from == to) return;
        float target = m.Position.X;
        m.Position = new Vector2(TrackX(from), 0);
        var tw = m.CreateTween();
        tw.TweenProperty(m, "position:x", target, 0.45f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        m.PivotOffset = m.Size / 2f;
        m.Scale = new Vector2(1.25f, 1.25f);
        var tw2 = m.CreateTween();
        tw2.TweenProperty(m, "scale", Vector2.One, 0.45f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    private Control BuildSlip(Clause c)
    {
        bool open = c.IsOpen && !_state.IsResolved;
        bool selected = false;
        bool targetable = false;
        bool bundleFirst = false;
        bool refused = c.State == ClauseState.Refused;
        bool lockedYours = c.IsAgreed && c.Side == ClauseSide.Theirs;   // you receive it
        bool lockedTheirs = c.IsAgreed && c.Side == ClauseSide.Yours;   // they keep it
        var pc = _state.PendingCounter;
        bool wanted = pc != null && c.Id == pc.Demand.Id;   // the clause they want added
        bool asked = pc != null && (c.Id == pc.Ask.Id || pc.Extras.Any(e => e.Id == c.Id));   // what you asked for

        var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 104) };
        var style = new StyleBoxFlat
        {
            BgColor = lockedYours ? new Color(0.74f, 0.87f, 0.72f) : lockedTheirs ? new Color(0.91f, 0.68f, 0.64f)
                    : wanted ? new Color(0.96f, 0.88f, 0.62f) : Parchment,
            BorderColor = lockedYours ? ColYours : lockedTheirs ? ColTheirs
                        : wanted ? ColDeal : asked ? ColInfo : new Color(0, 0, 0, 0.22f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginTop = 8, ContentMarginBottom = 8, ContentMarginLeft = 10, ContentMarginRight = 10,
            ShadowColor = new Color(0, 0, 0, 0.40f), ShadowSize = 4, ShadowOffset = new Vector2(0, 2),
        };
        int bw = (wanted || lockedYours || lockedTheirs) ? 3 : asked ? 2 : 1;
        style.BorderWidthTop = bw; style.BorderWidthBottom = bw; style.BorderWidthLeft = bw; style.BorderWidthRight = bw;
        card.AddThemeStyleboxOverride("panel", style);
        if (refused) card.Modulate = new Color(1, 1, 1, 0.45f);
        else if (pc != null && !wanted && !asked) card.Modulate = new Color(1, 1, 1, 0.55f);   // counter pending: only the two matter

        var box = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        // Top: text | seal
        var top = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        top.AddThemeConstantOverride("separation", 10);
        box.AddChild(top);
        var textCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        textCol.AddThemeConstantOverride("separation", 2);
        top.AddChild(textCol);
        var title = new Label { Text = c.Text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.AddThemeFontSizeOverride("font_size", UITheme.NegotiationHeaderFontSize);
        title.AddThemeColorOverride("font_color", Ink);
        textCol.AddChild(title);
        var kind = new Label { Text = PayloadText(c), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        kind.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
        kind.AddThemeColorOverride("font_color", InkSoft);
        textCol.AddChild(kind);
        if (lockedYours) textCol.AddChild(MakeTinyLabel("✔ LOCKED — YOURS", new Color(0.13f, 0.40f, 0.20f)));
        else if (lockedTheirs) textCol.AddChild(MakeTinyLabel("✔ LOCKED — THEIRS", new Color(0.55f, 0.18f, 0.14f)));
        else if (wanted) textCol.AddChild(MakeTinyLabel($"◀ {_data.NpcName} wants this added", UITheme.GoldDim));
        else if (asked) textCol.AddChild(MakeTinyLabel("▶ what you asked for", UITheme.VioletDim));
        else if (pc == null && open && _state.NamedWant?.Id == c.Id)
            textCol.AddChild(MakeTinyLabel($"◀ {_data.NpcName} asked for this", UITheme.GoldDim));
        if (c.HasRider)
        {
            var r = _state.Find(c.Rider);
            if (r != null && r.State != ClauseState.Struck)
            {
                var rl = MakeTinyLabel(c.AnyKnown
                    ? $"↳ comes with the {NegotiationState.ShortName(r)}"
                    : "↳ comes with a condition — named when you ask", RiderRed);
                rl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                textCol.AddChild(rl);
            }
        }
        if (c.RequiresWarm) textCol.AddChild(MakeTinyLabel("✦ sealed — only signs if they're Warm", SealAmber));
        if (refused) textCol.AddChild(MakeTinyLabel("refused — off the table", RiderRed));

        var valCol = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkBegin };
        valCol.AddThemeConstantOverride("separation", 3);
        top.AddChild(valCol);
        valCol.AddChild(MakeSeal(c.Value));
        if (c.AnyKnown && !c.IsAgreed && open)
        {
            var pip = new Label
            {
                Text = c.Revealed ? $"◉ {c.NpcValue}" : ParleyTells.Band(c.NpcValue),
                HorizontalAlignment = HorizontalAlignment.Center,
                TooltipText = c.Revealed ? "Exactly what it's worth to them." : "Roughly what it's worth to them: cheap, fair, or dear.",
                MouseFilter = MouseFilterEnum.Pass,
            };
            pip.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
            pip.AddThemeColorOverride("font_color", Colors.White);
            pip.AddThemeStyleboxOverride("normal", new StyleBoxFlat
            {
                BgColor = ColInfo,
                CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginLeft = 5, ContentMarginRight = 5,
            });
            valCol.AddChild(pip);
        }

        // Bottom: who brought it | the track
        if (open)
        {
            var bottom = new HBoxContainer();
            bottom.AddThemeConstantOverride("separation", 8);
            box.AddChild(bottom);
            var who = MakeTinyLabel(c.Side == ClauseSide.Theirs ? "they brought it" : "you brought it", InkSoft);
            who.VerticalAlignment = VerticalAlignment.Center;
            who.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            bottom.AddChild(who);
            bottom.AddChild(BuildTrack(c));
        }

        // v3.5: terms are display only; no tell text — the track and the band say it.
        card.MouseFilter = MouseFilterEnum.Pass;
        return card;
    }

    private Control MakeSeal(int value)
    {
        var seal = new Panel { CustomMinimumSize = new Vector2(30, 30), TooltipText = "Trade value — what it's worth to you.", MouseFilter = MouseFilterEnum.Pass };
        seal.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = SealGold,
            BorderColor = new Color(0.54f, 0.43f, 0.12f, 0.6f),
            BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
            CornerRadiusTopLeft = 15, CornerRadiusTopRight = 15, CornerRadiusBottomLeft = 15, CornerRadiusBottomRight = 15,
        });
        var lbl = new Label
        {
            Text = value.ToString(),
            AnchorRight = 1f, AnchorBottom = 1f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        if (BoldFont != null) lbl.AddThemeFontOverride("font", BoldFont);
        lbl.AddThemeFontSizeOverride("font_size", UITheme.NegotiationHeaderFontSize);
        lbl.AddThemeColorOverride("font_color", Ink);
        seal.AddChild(lbl);
        return seal;
    }

    /// <summary>The payload in plain words: what actually happens on sign.</summary>
    public static string PayloadText(Clause c)
    {
        var parts = new List<string>();
        if (c.GoldDelta != 0) parts.Add($"{Signed(c.GoldDelta)} gold");
        if (c.SuppliesDelta != 0) parts.Add($"{Signed(c.SuppliesDelta)} supplies");
        if (c.FuelDelta != 0) parts.Add($"{Signed(c.FuelDelta)} fuel");
        if (c.ReputationDelta != 0) parts.Add($"{Signed(c.ReputationDelta)} rep" + (string.IsNullOrEmpty(c.FactionId) ? "" : $" ({c.FactionId})"));
        if (c.ChartRadius > 0) parts.Add($"charts {c.ChartRadius} hexes around here");
        foreach (var k in c.RevealPoiKinds) parts.Add($"reveals a {PoiKindWord(k)}");
        if (c.RevealsSupplyCaches) parts.Add("marks their supply caches");
        if (c.SupplyAnchorHere) parts.Add("this hex becomes a supply anchor");
        if (c.SafeConductSteps > 0) parts.Add($"patrols stand down for the next {c.SafeConductSteps} hexes");
        if (!string.IsNullOrEmpty(c.SpellId)) parts.Add("teaches a spell" + (c.RequiresWarm ? " (if Warm)" : ""));
        if (!string.IsNullOrEmpty(c.LoreUnlock)) parts.Add("lore");
        if (parts.Count == 0) parts.Add(c.Side == ClauseSide.Yours ? "a promise" : "—");
        return string.Join(" · ", parts);
    }

    private static string PoiKindWord(string k) => k switch
    {
        "Combat" => "hostile encampment",
        "Rest" => "refuge",
        "Narrative" => "curious site",
        "Negotiation" => "meeting place",
        "Outpost" => "outpost",
        "Settlement" => "settlement",
        "Seat" => "seat of power",
        "SupplyCache" => "supply cache",
        _ => "site",
    };

    // ═══════════════════════════════════════════════════════════════════
    // Interaction
    // ═══════════════════════════════════════════════════════════════════

    private void OnCardClicked(ParleyCard card)
    {
        Guarded($"card {card?.Id}", () =>
        {
            if (_state == null || _state.IsResolved || _state.PendingCounter != null) return;
            if (!_state.CanPlay(card)) { Trace($"card {card.Id} not playable"); return; }
            _portrait.FinishLine();
            if (card.Effect == ParleyEffect.Shake) { OnShakePressed(); return; }
            if (card.Effect == ParleyEffect.SchoolMove) { OnSchoolMovePressed(); return; }
            StartLogTurn();
            _state.PlayCard(card);
            Trace($"played {card.Id}; hand={_state.Hand.Count} deck={_state.Deck.Count} patience={_state.Patience} resolved={_state.IsResolved}");
            RefreshAll();
        });
    }

    /// <summary>Every input handler runs through this: an exception inside a
    /// Godot signal callback would otherwise vanish from the Output panel and
    /// leave the table silently dead. Prints the trace and keeps the UI alive.</summary>
    private void Guarded(string what, Action body)
    {
        try { body(); }
        catch (Exception e)
        {
            GD.PrintErr($"[Negotiation] {what} failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            try { RefreshAll(); } catch (Exception e2) { GD.PrintErr($"[Negotiation] refresh after failure also failed: {e2.Message}"); }
        }
    }

    private void Trace(string msg)
    {
        if (PlayerSession.DebugNegotiation) GD.Print($"[Negotiation] {msg}");
    }

    private void OnSchoolMovePressed()
    {
        Guarded("school move", OnSchoolMoveBody);
    }

    private void OnSchoolMoveBody()
    {
        if (_state == null || !_state.CanUseSchoolMove()) return;
        _portrait.FinishLine();
        StartLogTurn();
        _state.UseSchoolMove();
        PlayWash(SchoolColor(_state.School));
        RefreshAll();
    }

    private void ShowCounterModal(CounterProposal pc)
    {
        // Plain structure, names in full, the wanted clause in gold — and the
        // same slip is ringed in gold on the board behind this panel.
        string gold = UITheme.Gold.ToHtml(false);
        string violet = UITheme.Violet.ToHtml(false);
        string dim = UITheme.NegotiationHiddenTerm.ToHtml(false);
        string askName = NegotiationState.ShortName(pc.Ask);
        string wantName = NegotiationState.ShortName(pc.Demand);
        string line = string.IsNullOrEmpty(pc.Line)
            ? $"“The {askName} — if you add the {wantName}.”"
            : pc.Line;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[color={gold}][b]{_data.NpcName}[/b][/color] counters:\n");
        sb.Append($"[i]{line}[/i]\n\n");
        sb.Append($"[color={violet}]▶ You asked for[/color]  [b]{pc.Ask.Text}[/b]  [color={dim}](worth {pc.Ask.Value} to you)[/color]\n");
        foreach (var e in pc.Extras)
            sb.Append($"[color={violet}]▶ and[/color]  [b]{e.Text}[/b]  [color={dim}](worth {e.Value} to you)[/color]\n");
        sb.Append($"[color={gold}]◀ They want added[/color]  [color={gold}][b]{pc.Demand.Text}[/b][/color]  [color={dim}](worth {pc.Demand.Value} to you)[/color]");
        if (pc.Rider != null)
            sb.Append($"\n[color={RiderRed.ToHtml(false)}]↳ and the {askName} carries the {NegotiationState.ShortName(pc.Rider)}, which goes in too[/color]");
        sb.Append($"\n\n[color={dim}]Agree: everything above goes into the ledger. Decline: nothing changes and the {askName} stays open.[/color]");
        _counterLabel.Text = sb.ToString();
        _counterAcceptBtn.Text = $"Agree — add the {wantName}";
        _counterDeclineBtn.Text = "Decline";
        _counterPanel.Visible = true;   // the gold pop on the wanted slip comes from the deferred Counter FX
    }

    private void OnShakePressed()
    {
        if (_state == null || _state.IsResolved || !_state.CanShake) return;
        _portrait.FinishLine();
        var (taken, swept, fallback) = _state.PreviewSettlement();
        string gold = UITheme.Gold.ToHtml(false), red = UITheme.TensionHostile.ToHtml(false),
               green = UITheme.TensionCordial.ToHtml(false), dim = UITheme.NegotiationHiddenTerm.ToHtml(false);
        var sb = new System.Text.StringBuilder();
        sb.Append($"[color={gold}][b]Shake on it?[/b][/color]\n");
        sb.Append($"Everything locked in the ledger signs as written.\n");
        if (swept.Count > 0) sb.Append($"[color={green}]Comes with you[/color]: {string.Join(", ", swept.Select(c => "the " + NegotiationState.ShortName(c)))}.\n");
        if (taken.Count > 0) sb.Append($"[color={red}]They take[/color]: {string.Join(", ", taken.Select(c => "the " + NegotiationState.ShortName(c)))}.\n");
        if (fallback.Count > 0) sb.Append($"[color={dim}]Stays with them after all[/color]: {string.Join(", ", fallback.Select(c => "the " + NegotiationState.ShortName(c)))}.\n");
        if (_state.HasGrievance && _state.PredictSqueezeTarget() != null)
            sb.Append($"[color={red}]They hold a grievance: expect one last demand first.[/color]\n");
        _settleLabel.Text = sb.ToString();
        _settlePanel.Visible = true;
    }

    private void DoShake()
    {
        if (_state == null || _state.IsResolved || !_state.CanShake) return;
        StartLogTurn();
        _pendingSqueeze = _state.BeginShake();
        if (_pendingSqueeze == null) { RefreshAll(); return; }   // signed

        string want = NegotiationState.ShortName(_pendingSqueeze.Target);
        _squeezeLabel.Text =
            $"{_data.NpcName} holds your handshake. One last demand: the {want} (worth {_pendingSqueeze.Target.Value} to you).\n" +
            $"Why: {_pendingSqueeze.Cause}.\n\n" +
            $"Let them have it, and the deal signs with the {want} in it.\n" +
            $"Hold firm, and they blink {_pendingSqueeze.OddsPercent} times in 100 and sign as written; " +
            $"otherwise goodwill falls, a beat of patience goes, and the talk goes on.";
        _squeezeConcedeBtn.Text = "Concede & sign";
        _squeezeHoldBtn.Text = $"Hold firm ({_pendingSqueeze.OddsPercent}%)";
        _squeezePanel.Visible = true;
        RefreshAll();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Effects (the FX contract, spec Manager notes)
    // ═══════════════════════════════════════════════════════════════════

    private static Color SchoolColor(CardSchool s) => s switch
    {
        CardSchool.Adept => new Color(0.79f, 0.64f, 0.29f),
        CardSchool.Elementalist => new Color(0.88f, 0.44f, 0.23f),
        CardSchool.Druid => new Color(0.44f, 0.68f, 0.49f),
        CardSchool.Necromancer => new Color(0.50f, 0.75f, 0.62f),
        CardSchool.Tinker => new Color(0.79f, 0.60f, 0.35f),
        CardSchool.Enchanter => new Color(0.78f, 0.42f, 0.69f),
        CardSchool.Arcanist => new Color(0.54f, 0.42f, 0.82f),
        CardSchool.Chronomancer => new Color(0.37f, 0.70f, 0.85f),
        _ => UITheme.Violet,
    };

    private void PlayPendingFx()
    {
        if (_pendingFx.Count == 0) return;
        var list = _pendingFx.ToList();
        _pendingFx.Clear();
        foreach (var r in list) PlayFx(r);
    }

    private void PlayFx(NegotiationReaction r)
    {
        switch (r.Kind)
        {
            case ReactionKind.Probe:
            case ReactionKind.Reveal:
            case ReactionKind.CallIn:
                foreach (var id in r.ClauseIds) Glow(id, UITheme.Violet, pop: true);
                _portrait.PulseRing(UITheme.Violet);
                if (r.Kind == ReactionKind.CallIn) WarmCells(r.GoodwillFrom, r.GoodwillTo);
                break;
            case ReactionKind.Charm:
                WarmCells(r.GoodwillFrom, r.GoodwillTo);
                _portrait.PulseRing(r.GoodwillTo > r.GoodwillFrom ? UITheme.TensionCordial : UITheme.TensionHostile);
                break;
            case ReactionKind.Press:
                ShakeTable();
                foreach (var id in r.ClauseIds) Glow(id, UITheme.TensionHostile, pop: true);
                _portrait.PulseRing(UITheme.TensionHostile);
                break;
            case ReactionKind.Offer:
                foreach (var id in r.ClauseIds) DropIn(id);
                break;
            case ReactionKind.Demonstrate:
                foreach (var id in r.ClauseIds) Materialise(id);
                PlayWash(SchoolColor(_state.School));
                _portrait.PulseRing(SchoolColor(_state.School));
                break;
            case ReactionKind.Wait:
                FreezePips();
                _portrait.PulseRing(new Color(0.37f, 0.70f, 0.85f));
                break;
            case ReactionKind.Agree:
                foreach (var id in r.ClauseIds) Stamp(id);
                _portrait.PulseRing(ColYours);
                break;
            case ReactionKind.Concede:
                foreach (var id in r.ClauseIds) Stamp(id);
                if (r.GoodwillTo != r.GoodwillFrom) WarmCells(r.GoodwillFrom, r.GoodwillTo);
                break;
            case ReactionKind.Refuse:
                foreach (var id in r.ClauseIds) Shiver(id);
                _portrait.PulseRing(UITheme.TensionHostile);
                break;
            case ReactionKind.Counter:
                // ClauseIds = [ask, demand]: the ask nudges violet, the demand pops gold.
                if (r.ClauseIds.Count > 0) Glow(r.ClauseIds[0], UITheme.Violet, pop: false);
                if (r.ClauseIds.Count > 1) Glow(r.ClauseIds[1], UITheme.Gold, pop: true);
                break;
            case ReactionKind.Beat:
                foreach (var id in r.ClauseIds) Glow(id, ColTheirs, pop: false);
                FlipNpcCard(r.Band == 1 ? NpcCardKind.Final : NpcCardKind.Mid);
                _portrait.PulseRing(ColTheirs);
                break;
            case ReactionKind.SchoolMove:
                foreach (var id in r.ClauseIds) Materialise(id);
                if (r.GoodwillTo != r.GoodwillFrom) WarmCells(r.GoodwillFrom, r.GoodwillTo);
                break;
            case ReactionKind.SqueezeOpen:
                foreach (var id in r.ClauseIds) Glow(id, UITheme.TensionHostile, pop: true);
                break;
            case ReactionKind.Card:
                if (r.Band == (int)ParleyEffect.Force) { ShakeTable(); _portrait.PulseRing(UITheme.TensionHostile); }
                else foreach (var id in r.ClauseIds) Glow(id, UITheme.TensionCordial, pop: true);
                if (r.ClauseIds.Count > 1) PlayWash(SchoolColor(_state.School));
                break;
            case ReactionKind.Flip:
                foreach (var id in r.ClauseIds) Glow(id, ColInfo, pop: true);
                FlipNpcCard((NpcCardKind)r.Band);
                _portrait.PulseRing(ColInfo);
                break;
            case ReactionKind.Bluff:
                if (r.Band > 0) foreach (var id in r.ClauseIds) Glow(id, UITheme.TensionCordial, pop: true);
                else { ShakeTable(); _portrait.PulseRing(UITheme.TensionHostile); WarmCells(r.GoodwillFrom, r.GoodwillTo); }
                break;
            case ReactionKind.Draw:
                foreach (var kv in _cardNodes) PopCard(kv.Value);
                break;
            case ReactionKind.Pull:
                foreach (var id in r.ClauseIds) { Glow(id, ColYours, pop: false); Nudge(id, +14f); SlideMarker(id); }
                if (r.ClauseIds.Count > 1) PlayWash(SchoolColor(_state.School));
                break;
            case ReactionKind.TheirPull:
                foreach (var id in r.ClauseIds) { Glow(id, ColTheirs, pop: false); Nudge(id, -14f); SlideMarker(id); }
                if (r.Band > 0) { ShakeTable(); _portrait.PulseRing(ColTheirs); }
                break;
            case ReactionKind.Settle:
                foreach (var id in r.ClauseIds) Stamp(id);
                break;
            case ReactionKind.SqueezeBristle:
            case ReactionKind.Collapse:
                ShakeTable();
                _portrait.PulseRing(UITheme.TensionHostile);
                break;
            case ReactionKind.Sign:
                PlayWash(UITheme.NegotiationTitleColor);
                _portrait.PulseRing(UITheme.TensionCordial);
                break;
        }
    }

    private Control SlipNode(string id) => _slipNodes.TryGetValue(id, out var n) && GodotObject.IsInstanceValid(n) ? n : null;

    private static void PopCard(Control n)
    {
        if (n == null || !GodotObject.IsInstanceValid(n)) return;
        n.PivotOffset = n.Size / 2f;
        n.Scale = new Vector2(0.9f, 0.9f);
        var tw = n.CreateTween();
        tw.TweenProperty(n, "scale", Vector2.One, 0.3f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    private void Glow(string id, Color color, bool pop)
    {
        var n = SlipNode(id);
        if (n == null) return;
        n.Modulate = new Color(color.R * 0.6f + 0.6f, color.G * 0.6f + 0.6f, color.B * 0.6f + 0.6f);
        var tw = n.CreateTween();
        tw.TweenProperty(n, "modulate", Colors.White, 0.9f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        if (pop)
        {
            n.PivotOffset = n.Size / 2f;
            n.Scale = new Vector2(1.04f, 1.04f);
            var tw2 = n.CreateTween();
            tw2.TweenProperty(n, "scale", Vector2.One, 0.4f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }
    }

    /// <summary>One of their cards turns over: squashes to a line and opens again.</summary>
    private void FlipNpcCard(NpcCardKind kind)
    {
        if (!_npcCardNodes.TryGetValue(kind, out var n) || !GodotObject.IsInstanceValid(n)) return;
        n.PivotOffset = n.Size / 2f;
        n.Scale = new Vector2(0.05f, 1f);
        var tw = n.CreateTween();
        tw.TweenProperty(n, "scale", Vector2.One, 0.32f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    /// <summary>A card slides a few pixels toward whoever pulled it, and settles back.</summary>
    private void Nudge(string id, float dx)
    {
        var n = SlipNode(id);
        if (n == null) return;
        float x = n.Position.X;
        var tw = n.CreateTween();
        tw.TweenProperty(n, "position:x", x + dx, 0.12f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(n, "position:x", x, 0.35f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    /// <summary>A lock lands like a stamp: the card drops in from slightly
    /// larger and brighter, and stays tinted.</summary>
    private void Stamp(string id)
    {
        var n = SlipNode(id);
        if (n == null) return;
        n.PivotOffset = n.Size / 2f;
        n.Scale = new Vector2(1.10f, 1.10f);
        n.Modulate = new Color(1.25f, 1.25f, 1.15f);
        var tw = n.CreateTween().SetParallel(true);
        tw.TweenProperty(n, "scale", Vector2.One, 0.22f).SetTrans(Tween.TransitionType.Quart).SetEase(Tween.EaseType.In);
        tw.TweenProperty(n, "modulate", Colors.White, 0.5f);
    }

    private void Shiver(string id)
    {
        var n = SlipNode(id);
        if (n == null) return;
        var tw = n.CreateTween();
        tw.TweenProperty(n, "position:x", n.Position.X - 4f, 0.05f);
        tw.TweenProperty(n, "position:x", n.Position.X + 4f, 0.08f);
        tw.TweenProperty(n, "position:x", n.Position.X - 2f, 0.06f);
        tw.TweenProperty(n, "position:x", n.Position.X, 0.06f);
    }

    private void Materialise(string id)
    {
        var n = SlipNode(id);
        if (n == null) return;
        n.PivotOffset = n.Size / 2f;
        n.Scale = new Vector2(0.6f, 0.6f);
        n.Modulate = new Color(1, 1, 1, 0);
        var tw = n.CreateTween().SetParallel(true);
        tw.TweenProperty(n, "scale", Vector2.One, 0.5f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(n, "modulate", Colors.White, 0.35f);
    }

    private void DropIn(string id)
    {
        var n = SlipNode(id);
        if (n == null) return;
        float y = n.Position.Y;
        n.Position = new Vector2(n.Position.X, y - 40f);
        n.Modulate = new Color(1, 1, 1, 0);
        var tw = n.CreateTween().SetParallel(true);
        tw.TweenProperty(n, "position:y", y, 0.45f).SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(n, "modulate", Colors.White, 0.2f);
    }

    private void ShakeTable()
    {
        if (_tableRow == null) return;
        var p = _tableRow.Position;
        var tw = _tableRow.CreateTween();
        tw.TweenProperty(_tableRow, "position", p + new Vector2(-5, 2), 0.05f);
        tw.TweenProperty(_tableRow, "position", p + new Vector2(5, -2), 0.08f);
        tw.TweenProperty(_tableRow, "position", p + new Vector2(-3, 1), 0.06f);
        tw.TweenProperty(_tableRow, "position", p, 0.06f);
    }

    private void WarmCells(int from, int to)
    {
        int lo = Mathf.Min(from, to), hi = Mathf.Max(from, to);
        Color flash = to > from ? UITheme.TensionCordial : UITheme.TensionHostile;
        for (int i = lo; i < hi && i < _goodwillCells.Length; i++)
        {
            var cell = _goodwillCells[i];
            Color target = cell.Color;
            cell.Color = Colors.White;
            var tw = cell.CreateTween();
            tw.TweenProperty(cell, "color", flash, 0.25f).SetDelay((i - lo) * 0.08f);
            tw.TweenProperty(cell, "color", target, 0.35f);
        }
    }

    private void FreezePips()
    {
        var tw = _patienceBar.CreateTween();
        tw.TweenProperty(_patienceBar, "modulate", new Color(0.55f, 0.85f, 1f), 0.25f);
        tw.TweenProperty(_patienceBar, "modulate", Colors.White, 0.8f);
    }

    private void PlayWash(Color color)
    {
        if (_wash == null) return;
        _wash.Color = new Color(color.R, color.G, color.B, 0f);
        var tw = _wash.CreateTween();
        tw.TweenProperty(_wash, "color:a", 0.28f, 0.18f);
        tw.TweenProperty(_wash, "color:a", 0f, 0.7f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Log / events
    // ═══════════════════════════════════════════════════════════════════

    private void StartLogTurn()
    {
        if (_logRecent.Count == 0) return;
        _logHistory.AddRange(_logRecent);
        _logHistory.Add(("", NegotiationLogKind.Scene));
        _logRecent.Clear();
    }

    private void AppendLog(string message, NegotiationLogKind kind)
    {
        _logRecent.Add((message, kind));
        if (kind == NegotiationLogKind.NpcLine && _portrait != null && _barkLabel != null)
        {
            string spoken = message;
            string prefix = $"{_data?.NpcName}: ";
            if (!string.IsNullOrEmpty(_data?.NpcName) && spoken.StartsWith(prefix))
                spoken = spoken.Substring(prefix.Length).Trim('"');
            _portrait.Say(spoken, s => _barkLabel.Text = s);
        }
        RenderLog();
    }

    private void RenderLog()
    {
        if (_logLabel == null) return;
        string dimHex = UITheme.NegotiationHiddenTerm.ToHtml(false);
        string sceneHex = UITheme.NegotiationNpcColor.ToHtml(false);
        string youHex = UITheme.Violet.ToHtml(false);
        var sb = new System.Text.StringBuilder();
        void Line(string text, NegotiationLogKind kind, bool recent)
        {
            if (text.Length == 0) { sb.Append('\n'); return; }
            switch (kind)
            {
                case NegotiationLogKind.Detail:
                    if (!_showDetails) return;
                    sb.Append($"[font_size={UITheme.NegotiationTinyFontSize}][color=#{dimHex}]{EscapeBb(text)}[/color][/font_size]\n");
                    break;
                case NegotiationLogKind.Scene:
                    sb.Append(recent ? $"[i][color=#{sceneHex}]{EscapeBb(text)}[/color][/i]\n" : $"[color=#{dimHex}]{EscapeBb(text)}[/color]\n");
                    break;
                case NegotiationLogKind.Dialogue:
                    sb.Append(recent ? $"[color=#{youHex}]{EscapeBb(text)}[/color]\n" : $"[color=#{dimHex}]{EscapeBb(text)}[/color]\n");
                    break;
                default:   // NpcLine
                    sb.Append(recent ? $"{EscapeBb(text)}\n" : $"[color=#{dimHex}]{EscapeBb(text)}[/color]\n");
                    break;
            }
        }
        foreach (var (text, kind) in _logHistory) Line(text, kind, false);
        foreach (var (text, kind) in _logRecent) Line(text, kind, true);
        _logLabel.Text = sb.ToString();
        _logScroll?.SetDeferred("scroll_vertical", 999999);
    }

    private static string EscapeBb(string s) => s.Replace("[", "❲").Replace("]", "❳");

    private void OnGoodwillChanged(int oldValue, int newValue)
    {
        if (_portrait != null && _state != null) _portrait.SetMood(_state.Mood);
    }

    private void OnNegotiationResolved()
    {
        _counterPanel.Visible = false;
        _squeezePanel.Visible = false;
        _settlePanel.Visible = false;

        string spellGranted = _state.GetSpellOutcome();

        foreach (var child in _resultContent.GetChildren()) child.QueueFree();
        if (_state.DealAccepted) BuildDealReceipt(spellGranted);
        else BuildNoDealResult();
        _resultPanel.Visible = true;

        NegotiationContext.SetResult(
            _state.DealAccepted,
            _state.GetGoldOutcome(),
            _state.GetReputationOutcome(),
            _state.Data.FactionId,
            spellGranted,
            resolvedCordial: _state.SignedWarm,
            supplies: _state.GetSuppliesOutcome(),
            revealSupplyCaches: _state.GetSupplyIntelOutcome(),
            fuel: _state.GetFuelOutcome(),
            escalated: _state.Collapsed && _state.Data.Escalates,
            chartRadius: _state.GetChartRadiusOutcome(),
            revealPoiKinds: _state.GetRevealPoiKindsOutcome(),
            anchorHere: _state.GetAnchorOutcome(),
            safeConductSteps: _state.GetSafeConductOutcome(),
            loreUnlocks: _state.GetLoreOutcome());

        if (PlayerSession.DebugNegotiation)
        {
            // A debug table leaves no trace: no court Regard, no Hall of Records,
            // no deeds. Telemetry still gets a row (it is the tuning loop's food).
            NegotiationTelemetry.Record(BuildDealRecord(spellGranted), _state);
        }
        else
        {
            SettleRegardAtTable();
            RecordDeal(spellGranted);
        }

        GD.Print($"Negotiation resolved: deal={_state.DealAccepted}, surplus={_state.Surplus()}/{_state.Par}, " +
                 $"stars={_state.GetStars()}, gold={_state.GetGoldOutcome()}, rep={_state.GetReputationOutcome()}" +
                 (spellGranted != "" ? $", taught='{spellGranted}'" : ""));
    }

    /// <summary>§6a: a courtier of this counterpart's archetype at the origin
    /// court moves Regard AT THE TABLE, replacing the deal-deed echo.</summary>
    private void SettleRegardAtTable()
    {
        NegotiationContext.RegardSettledAtTable = false;
        if (!_state.DealAccepted) return;
        var cycle = SaveManager.ActiveSave?.Cycle;
        string kingdom = NegotiationContext.OriginKingdomId;
        if (cycle?.Council == null || string.IsNullOrEmpty(kingdom) ||
            !cycle.Council.Courts.TryGetValue(kingdom, out var court))
            return;
        var voice = court.Courtiers.FirstOrDefault(c => c.Archetype == _data.Archetype.ToString());
        if (voice == null) return;

        NegotiationContext.RegardSettledAtTable = true;
        int rep = _state.GetReputationOutcome();
        int delta = (rep > 0 ? 1 : rep < 0 ? -1 : 0) + (_state.GetStars() >= 4 ? 1 : 0);
        if (delta == 0) return;
        voice.Regard = Mathf.Clamp(voice.Regard + delta, -3, 3);
        SaveManager.MarkDirty();
        AppendLog(delta > 0
            ? $"Word of this table travels ahead of you: {voice.DisplayName}, {voice.Office} at this kingdom's court, will hear of it warmly."
            : $"Word of this table travels ahead of you: {voice.DisplayName}, {voice.Office} at this kingdom's court, will not like what they hear.",
            NegotiationLogKind.Scene);
        GD.Print($"[Negotiation] Regard settled at table: {voice.DisplayName} ({voice.Office}, {kingdom}) {delta:+0;-0} -> {voice.Regard}.");
    }

    // ═══════════════════════════════════════════════════════════════════
    // The receipt
    // ═══════════════════════════════════════════════════════════════════

    private static string Signed(int v) => v >= 0 ? $"+{v}" : v.ToString();
    private static string StarLine(int stars) => new string('★', stars) + new string('☆', 5 - stars);

    private void AddResultLine(string text, Color color, int fontSize)
    {
        var lbl = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _resultContent.AddChild(lbl);
    }

    private void BuildDealReceipt(string spellGranted)
    {
        int s = _state.Surplus();
        AddResultLine($"Deal Struck   {StarLine(_state.GetStars())}", UITheme.NegotiationTitleColor, UITheme.NegotiationResultFontSize);
        AddResultLine($"surplus {Signed(s)} against par +{_state.Par} · closed {_state.Mood}",
            _state.SignedWarm ? UITheme.ZoneCordialLabel : UITheme.ZoneStrainedLabel, UITheme.NegotiationSmallFontSize);

        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 3);
        _resultContent.AddChild(grid);

        void Row(string name, Color nameColor, string payload, string note, Color noteColor)
        {
            var n = new Label { Text = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            n.AddThemeFontSizeOverride("font_size", UITheme.NegotiationDetailFontSize);
            n.AddThemeColorOverride("font_color", nameColor);
            grid.AddChild(n);
            var p = new Label { Text = payload, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(180, 0) };
            p.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
            p.AddThemeColorOverride("font_color", UITheme.NegotiationBodyColor);
            grid.AddChild(p);
            var t = new Label { Text = note, HorizontalAlignment = HorizontalAlignment.Right };
            t.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
            t.AddThemeColorOverride("font_color", noteColor);
            grid.AddChild(t);
        }

        foreach (var c in _state.Clauses.Where(c => c.IsAgreed))
        {
            bool theirs = c.Side == ClauseSide.Theirs;
            string note = theirs ? $"+{c.Value}" : $"−{c.Value}";
            if (!string.IsNullOrEmpty(c.SpellId))
                note = spellGranted == c.SpellId ? "learned ✓" : "lost";
            Row((theirs ? "◀ " : "▶ ") + NegotiationState.ShortName(c), UITheme.NegotiationBodyColor,
                PayloadText(c), note, theirs ? UITheme.TermFavorPlayer : UITheme.TermAgainstPlayer);
        }
        foreach (var c in _state.Clauses.Where(c => c.RequiresWarm && c.State == ClauseState.Refused && !c.Ephemeral))
            Row("✦ " + NegotiationState.ShortName(c), UITheme.NegotiationHiddenTerm,
                "lapsed — they were not Warm when you signed", "—", UITheme.TermAgainstPlayer);

        _resultContent.AddChild(new HSeparator());

        var totals = new List<string>();
        if (_state.GetGoldOutcome() != 0) totals.Add($"{Signed(_state.GetGoldOutcome())} gold");
        if (_state.GetSuppliesOutcome() != 0) totals.Add($"{Signed(_state.GetSuppliesOutcome())} supplies");
        if (_state.GetFuelOutcome() != 0) totals.Add($"{Signed(_state.GetFuelOutcome())} fuel");
        if (_state.GetReputationOutcome() != 0) totals.Add($"{Signed(_state.GetReputationOutcome())} rep");
        if (_state.GetChartRadiusOutcome() > 0) totals.Add($"{_state.GetChartRadiusOutcome()} hexes charted");
        if (_state.GetRevealPoiKindsOutcome().Count > 0) totals.Add("sites revealed");
        if (_state.GetSupplyIntelOutcome()) totals.Add("supply lines marked");
        if (_state.GetAnchorOutcome()) totals.Add("supply anchor here");
        if (_state.GetSafeConductOutcome() > 0) totals.Add($"safe conduct for {_state.GetSafeConductOutcome()} hexes");
        if (spellGranted != "") totals.Add($"{OverworldSpellRegistry.Get(spellGranted)?.Name} learned");
        AddResultLine("You walk away with:  " + (totals.Count > 0 ? string.Join(" · ", totals) : "the goodwill, and nothing else"),
            UITheme.NegotiationTitleColor, UITheme.NegotiationResultFontSize);
    }

    private void BuildNoDealResult()
    {
        string title = _state.PlayerWalkedAway ? "You Walked Away"
                     : _state.Collapsed ? "The Table Collapsed"
                     : "They Ended It";
        AddResultLine(title, UITheme.NegotiationTitleColor, UITheme.NegotiationResultFontSize);
        AddResultLine(_state.Collapsed && _state.Data.Escalates
            ? "No deal. Their goodwill is spent, and their hand is on the hilt."
            : "No deal. Nothing gained, nothing lost. Reputation unharmed.",
            UITheme.NegotiationNpcColor, UITheme.NegotiationBodyFontSize);
    }

    /// <summary>Hall of Records: append this table's outcome to the eternal
    /// ledger, count the deeds, anchor five-star deals as renown.</summary>
    private DealRecord BuildDealRecord(string spellGranted)
    {
        var save = SaveManager.ActiveSave;
        string outcome = _state.DealAccepted ? "Signed"
            : _state.PlayerWalkedAway ? "WalkedAway"
            : _state.Collapsed ? "Collapsed"
            : "TheyLeft";
        return new DealRecord
        {
            CycleNumber = save?.Cycle?.CycleNumber ?? 0,
            When = DateTime.UtcNow.ToString("o"),
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
            Zone = _state.Mood.ToString(),
            Turns = _state.TurnNumber,
            SpellGranted = spellGranted,
        };
    }

    private void RecordDeal(string spellGranted)
    {
        var save = SaveManager.ActiveSave;
        var record = BuildDealRecord(spellGranted);
        NegotiationTelemetry.Record(record, _state);

        if (save == null) return;
        save.Ledger.DealRecords.Add(record);
        save.Ledger.RecordDeed("negotiation_resolved");
        if (_state.DealAccepted)
        {
            save.Ledger.RecordDeed("negotiation_deal_signed");
            if (record.Stars >= 5)
            {
                save.Ledger.RecordDeed("negotiation_five_star_deal");
                save.Ledger.RenownAnchors.Add(new RenownAnchor
                {
                    SubjectId = string.IsNullOrEmpty(_data.FactionId) ? _data.NpcName : _data.FactionId,
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
        if (PlayerSession.DebugNegotiation)
        {
            NegotiationDebugLauncher.ReturnToCampus(this);
            return;
        }
        GetTree().ChangeSceneToFile(
            EncounterRouter.Instance?.OverworldScenePath
            ?? "res://Scenes/Overworld/ExpeditionScene.tscn");
    }
}
