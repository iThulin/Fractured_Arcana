using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Full-screen negotiation encounter scene.
/// Reads NegotiationContext (set by EncounterRouter before scene swap).
/// Drives NegotiationState state machine.
/// Writes results back to EncounterRouter on completion.
/// </summary>
public partial class NegotiationScene : Control
{
    private NegotiationState _state;
    private NegotiationEncounterData _data;

    // ── UI references ────────────────────────────────────────────────────
    private Label _titleLabel;
    private Label _npcNameLabel;
    private Label _openingLabel;

    // Tension meter
    private HBoxContainer _tensionBar;
    private Label _tensionLabel;
    private Label _zoneLabel;

    // Deal terms
    private VBoxContainer _termsContainer;

    // Token buttons
    private VBoxContainer _tokenContainer;

    // Log
    private RichTextLabel _logLabel;

    // Action buttons
    private Button _acceptButton;
    private Button _walkAwayButton;

    // Result panel (shown after resolution)
    private Panel _resultPanel;
    private Label _resultLabel;
    private Button _continueButton;

    // Tension step squares (10 visual blocks)
    private ColorRect[] _tensionSteps = new ColorRect[10];

    public override void _Ready()
    {
        BuildUI();
        InitializeNegotiation();
    }

    private void InitializeNegotiation()
    {
        // Load encounter data
        string encounterId = NegotiationContext.EncounterId;
        _data = NegotiationEncounterLoader.Load(encounterId);

        if (_data == null)
        {
            GD.PrintErr($"NegotiationScene: Could not load encounter '{encounterId}'");
            ReturnToOverworld();
            return;
        }

        // Build the party for token pool calculation
        var party = CompanionRoster.GetActiveParty();
        var school = PlayerSession.SelectedSchool;

        // Faction reputation (stub 0 for now — Phase 3 wires in real faction data)
        int factionRep = 0;

        // Initialize state machine
        _state = new NegotiationState();
        _state.OnTensionChanged += OnTensionChanged;
        _state.OnLogEntry += AppendLog;
        _state.OnResolved += OnNegotiationResolved;

        _state.Initialize(_data, school, party, factionRep);

        // Set UI
        _titleLabel.Text = _data.Title;
        _npcNameLabel.Text = _data.NpcName;
        _openingLabel.Text = _data.OpeningText;

        RefreshTensionBar();
        RefreshTerms();
        RefreshTokenButtons();
    }

    // ── UI building ──────────────────────────────────────────────────────

    private void BuildUI()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;

        // Background
        var bg = new ColorRect
        {
            Color = new Color(0.08f, 0.07f, 0.12f),
            AnchorRight = 1f, AnchorBottom = 1f,
        };
        AddChild(bg);

        // Main layout: left panel (tension + terms + tokens) | right panel (log)
        var mainHBox = new HBoxContainer
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            OffsetLeft = 20, OffsetTop = 20,
            OffsetRight = -20, OffsetBottom = -20,
        };
        mainHBox.AddThemeConstantOverride("separation", 20);
        AddChild(mainHBox);

        // ── LEFT PANEL ──────────────────────────────────────────────────
        var leftPanel = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Expand,
            SizeFlagsStretchRatio = 1.4f,
        };
        leftPanel.AddThemeConstantOverride("separation", 12);
        mainHBox.AddChild(leftPanel);

        // Title
        _titleLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 26);
        _titleLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.85f, 0.6f));
        leftPanel.AddChild(_titleLabel);

        // NPC name
        _npcNameLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _npcNameLabel.AddThemeFontSizeOverride("font_size", 18);
        _npcNameLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.8f));
        leftPanel.AddChild(_npcNameLabel);

        // Opening text
        _openingLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _openingLabel.AddThemeFontSizeOverride("font_size", 15);
        _openingLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.82f, 0.88f));
        leftPanel.AddChild(_openingLabel);

        leftPanel.AddChild(new HSeparator());

        // ── TENSION METER ───────────────────────────────────────────────
        var tensionHeader = new Label { Text = "Tension" };
        tensionHeader.AddThemeFontSizeOverride("font_size", 16);
        tensionHeader.AddThemeColorOverride("font_color", Colors.White);
        leftPanel.AddChild(tensionHeader);

        _tensionBar = new HBoxContainer();
        _tensionBar.AddThemeConstantOverride("separation", 4);
        _tensionBar.CustomMinimumSize = new Vector2(0, 36);
        leftPanel.AddChild(_tensionBar);

        for (int i = 0; i < 10; i++)
        {
            var step = new ColorRect
            {
                CustomMinimumSize = new Vector2(32, 36),
                SizeFlagsHorizontal = SizeFlags.Expand,
            };
            _tensionSteps[i] = step;
            _tensionBar.AddChild(step);
        }

        // Zone labels
        var zoneHBox = new HBoxContainer();
        var cordialLbl = MakeTinyLabel("◀ CORDIAL", new Color(0.3f, 0.8f, 0.3f));
        var strainedLbl = MakeTinyLabel("STRAINED", new Color(0.9f, 0.75f, 0.2f));
        var hostileLbl = MakeTinyLabel("HOSTILE ▶", new Color(0.9f, 0.3f, 0.3f));

        cordialLbl.SizeFlagsHorizontal = SizeFlags.Expand;
        strainedLbl.SizeFlagsHorizontal = SizeFlags.Expand;
        strainedLbl.HorizontalAlignment = HorizontalAlignment.Center;
        hostileLbl.SizeFlagsHorizontal = SizeFlags.Expand;
        hostileLbl.HorizontalAlignment = HorizontalAlignment.Right;

        zoneHBox.AddChild(cordialLbl);
        zoneHBox.AddChild(strainedLbl);
        zoneHBox.AddChild(hostileLbl);
        leftPanel.AddChild(zoneHBox);

        _tensionLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _tensionLabel.AddThemeFontSizeOverride("font_size", 14);
        leftPanel.AddChild(_tensionLabel);

        leftPanel.AddChild(new HSeparator());

        // ── DEAL TERMS ──────────────────────────────────────────────────
        var termsHeader = new Label { Text = "Terms on the Table" };
        termsHeader.AddThemeFontSizeOverride("font_size", 16);
        termsHeader.AddThemeColorOverride("font_color", Colors.White);
        leftPanel.AddChild(termsHeader);

        _termsContainer = new VBoxContainer();
        _termsContainer.AddThemeConstantOverride("separation", 6);
        leftPanel.AddChild(_termsContainer);

        leftPanel.AddChild(new HSeparator());

        // ── TOKEN BUTTONS ───────────────────────────────────────────────
        var tokensHeader = new Label { Text = "Your Leverage" };
        tokensHeader.AddThemeFontSizeOverride("font_size", 16);
        tokensHeader.AddThemeColorOverride("font_color", Colors.White);
        leftPanel.AddChild(tokensHeader);

        _tokenContainer = new VBoxContainer();
        _tokenContainer.AddThemeConstantOverride("separation", 6);
        leftPanel.AddChild(_tokenContainer);

        // ── ACCEPT / WALK AWAY ───────────────────────────────────────────
        leftPanel.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 12);
        actionRow.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        leftPanel.AddChild(actionRow);

        _acceptButton = new Button
        {
            Text = "Accept Deal",
            CustomMinimumSize = new Vector2(160, 44),
        };
        _acceptButton.AddThemeFontSizeOverride("font_size", 16);
        _acceptButton.Pressed += () => _state.AcceptDeal();
        actionRow.AddChild(_acceptButton);

        _walkAwayButton = new Button
        {
            Text = "Walk Away",
            CustomMinimumSize = new Vector2(160, 44),
        };
        _walkAwayButton.AddThemeFontSizeOverride("font_size", 16);
        _walkAwayButton.Pressed += () => _state.WalkAway();
        actionRow.AddChild(_walkAwayButton);

        // ── RIGHT PANEL (LOG) ────────────────────────────────────────────
        var rightPanel = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Expand,
            SizeFlagsStretchRatio = 1f,
        };
        rightPanel.AddThemeConstantOverride("separation", 8);
        mainHBox.AddChild(rightPanel);

        var logHeader = new Label { Text = "Negotiation Log" };
        logHeader.AddThemeFontSizeOverride("font_size", 16);
        logHeader.AddThemeColorOverride("font_color", Colors.White);
        rightPanel.AddChild(logHeader);

        var logScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.Expand,
        };
        rightPanel.AddChild(logScroll);

        _logLabel = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _logLabel.AddThemeFontSizeOverride("font_size", 14);
        logScroll.AddChild(_logLabel);

        // ── RESULT PANEL (hidden until resolved) ─────────────────────────
        _resultPanel = new Panel
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f,
            AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
            OffsetLeft = -280, OffsetTop = -180,
            OffsetRight = 280, OffsetBottom = 180,
            Visible = false,
        };
        var resultStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.09f, 0.16f),
            BorderColor = new Color(0.6f, 0.5f, 0.8f),
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        };
        _resultPanel.AddThemeStyleboxOverride("panel", resultStyle);
        AddChild(_resultPanel);

        var resultLayout = new VBoxContainer
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            OffsetLeft = 24, OffsetTop = 24,
            OffsetRight = -24, OffsetBottom = -24,
        };
        resultLayout.AddThemeConstantOverride("separation", 16);
        _resultPanel.AddChild(resultLayout);

        _resultLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _resultLabel.AddThemeFontSizeOverride("font_size", 18);
        resultLayout.AddChild(_resultLabel);

        _continueButton = new Button
        {
            Text = "Return to the Map",
            CustomMinimumSize = new Vector2(200, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _continueButton.AddThemeFontSizeOverride("font_size", 16);
        _continueButton.Pressed += ReturnToOverworld;
        resultLayout.AddChild(_continueButton);
    }

    // ── Refresh methods ───────────────────────────────────────────────────

    private void RefreshTensionBar()
    {
        if (_state == null) return;

        int t = _state.Tension;
        _tensionLabel.Text = $"Tension: {t}/10  |  Zone: {_state.Zone}" +
                             $"  |  NPC Patience: {_state.NpcPatience}";

        for (int i = 0; i < 10; i++)
        {
            bool filled = i < t;
            Color color;
            if (!filled)
            {
                color = new Color(0.2f, 0.2f, 0.25f);
            }
            else if (i < 3)
            {
                color = new Color(0.25f, 0.75f, 0.35f);   // Cordial — green
            }
            else if (i < 7)
            {
                color = new Color(0.85f, 0.70f, 0.15f);   // Strained — amber
            }
            else
            {
                color = new Color(0.85f, 0.25f, 0.20f);   // Hostile — red
            }
            _tensionSteps[i].Color = color;
        }
    }

    private void RefreshTerms()
    {
        if (_termsContainer == null || _state == null) return;

        foreach (var child in _termsContainer.GetChildren())
            child.QueueFree();

        foreach (var term in _state.RevealedTerms)
        {
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 8);

            var icon = new Label
            {
                Text = term.FavorPlayer ? "✓" : "✗",
            };
            icon.AddThemeColorOverride("font_color",
                term.FavorPlayer ? new Color(0.4f, 0.9f, 0.4f) : new Color(0.9f, 0.4f, 0.4f));
            hbox.AddChild(icon);

            var desc = new Label
            {
                Text = term.Description,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            desc.AddThemeFontSizeOverride("font_size", 14);
            hbox.AddChild(desc);

            _termsContainer.AddChild(hbox);
        }

        // Show how many hidden terms remain
        int hiddenCount = _state.Terms.Count(t => t.IsHidden && !t.IsAccepted);
        if (hiddenCount > 0)
        {
            var hiddenLabel = new Label
            {
                Text = $"[{hiddenCount} hidden term{(hiddenCount > 1 ? "s" : "")}]",
            };
            hiddenLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.7f));
            hiddenLabel.AddThemeFontSizeOverride("font_size", 13);
            _termsContainer.AddChild(hiddenLabel);
        }
    }

    private void RefreshTokenButtons()
    {
        if (_tokenContainer == null || _state == null) return;

        foreach (var child in _tokenContainer.GetChildren())
            child.QueueFree();

        // Build two columns of token buttons
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        _tokenContainer.AddChild(hbox);

        var col1 = new VBoxContainer();
        col1.AddThemeConstantOverride("separation", 4);
        var col2 = new VBoxContainer();
        col2.AddThemeConstantOverride("separation", 4);
        hbox.AddChild(col1);
        hbox.AddChild(col2);

        int colIndex = 0;
        foreach (var kvp in _state.TokenPool)
        {
            if (kvp.Value <= 0) continue;

            var token = kvp.Key;
            var count = kvp.Value;

            var btn = new Button
            {
                Text = $"{token}  ×{count}",
                CustomMinimumSize = new Vector2(160, 34),
                Disabled = _state.IsResolved,
            };
            btn.AddThemeFontSizeOverride("font_size", 13);
            btn.Pressed += () =>
            {
                if (_state.PlayToken(token))
                {
                    RefreshTensionBar();
                    RefreshTerms();
                    RefreshTokenButtons();
                }
            };

            if (colIndex % 2 == 0) col1.AddChild(btn);
            else col2.AddChild(btn);
            colIndex++;
        }

        // Patience button (separate — always shows remaining uses)
        int patienceLeft = NegotiationState.MaxPatience - _state.PatienceUsed;
        if (patienceLeft > 0)
        {
            var patienceBtn = new Button
            {
                Text = $"Patience  ×{patienceLeft}",
                CustomMinimumSize = new Vector2(160, 34),
                Disabled = _state.IsResolved,
            };
            patienceBtn.AddThemeFontSizeOverride("font_size", 13);
            patienceBtn.Pressed += () =>
            {
                if (_state.PlayToken(LeverageToken.Patience))
                {
                    RefreshTensionBar();
                    RefreshTokenButtons();
                }
            };
            col1.AddChild(patienceBtn);
        }
    }

    private void AppendLog(string message)
    {
        if (_logLabel == null) return;
        _logLabel.AppendText($"\n{message}");
    }

    private void OnTensionChanged(int oldTension, int newTension)
    {
        RefreshTensionBar();
    }

    private void OnNegotiationResolved()
    {
        // Disable action buttons
        _acceptButton.Disabled = true;
        _walkAwayButton.Disabled = true;

        // Build result text
        string outcome;
        if (_state.DealAccepted)
        {
            int gold = _state.GetGoldOutcome();
            int rep = _state.GetReputationOutcome();
            string zoneBonus = _state.Zone switch
            {
                TensionZone.Cordial => " (Cordial bonus applied)",
                TensionZone.Hostile => " (Hostile penalty applied)",
                _ => ""
            };

            outcome = $"Deal struck in the {_state.Zone} zone.\n\n" +
                      $"Gold: {(gold >= 0 ? "+" : "")}{gold}{zoneBonus}\n" +
                      $"Reputation: {(rep >= 0 ? "+" : "")}{rep}";
        }
        else if (_state.PlayerWalkedAway)
        {
            outcome = "You walked away. No deal — no harm done.";
        }
        else
        {
            outcome = $"{_state.Data.NpcName} ended the negotiation.\nNo deal was reached.";
        }

        _resultLabel.Text = outcome;
        _resultPanel.Visible = true;

        // Store result for EncounterRouter to pick up
        NegotiationContext.SetResult(
            _state.DealAccepted,
            _state.GetGoldOutcome(),
            _state.GetReputationOutcome(),
            _state.Data.FactionId);

        GD.Print($"Negotiation resolved: deal={_state.DealAccepted}, " +
                 $"gold={_state.GetGoldOutcome()}, rep={_state.GetReputationOutcome()}");
    }

    private void ReturnToOverworld()
    {
        GetTree().ChangeSceneToFile(
            EncounterRouter.Instance?.OverworldScenePath
            ?? "res://Scenes/Overworld/OverworldScene.tscn");
    }

    private Label MakeTinyLabel(string text, Color color)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", 11);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }
}