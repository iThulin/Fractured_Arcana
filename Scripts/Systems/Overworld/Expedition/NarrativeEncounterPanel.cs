using Godot;
using System;

// ============================================================
// NarrativeEncounterPanel.cs
//
// Purpose:        Modal panel rendered over the overworld when
//                 a narrative POI is triggered. Shows title +
//                 body, a list of choice buttons, then the
//                 chosen result text and a Continue button.
//                 Outcomes resolved by OverworldRunManager via
//                 the OnCompleted callback.
// Layer:          UI
// Collaborators:  NarrativeEncounterData.cs (input schema),
//                 OverworldRunManager.cs (callback owner),
//                 UITheme.cs (narrative panel styling)
// See:            README §4.3 (Adding a Narrative Encounter)
// ============================================================

/// <summary>Modal narrative encounter panel. Built in code from an <see cref="NarrativeEncounterData"/>. Two-stage flow: choice → result → Continue. Fires <see cref="OnCompleted"/> with the chosen <see cref="EncounterChoice"/> on dismiss.</summary>
public partial class NarrativeEncounterPanel : Control
{
    public Action<EncounterChoice> OnCompleted;

    private Panel _backdrop;
    private Panel _panel;
    private Label _titleLabel;
    private ScrollContainer _bodyScroll;
    private Label _bodyLabel;
    private VBoxContainer _choiceContainer;
    // (2026-08-13) PanelContainer, not Panel: the old fixed-110px Panel with a
    // full-rect label let long result texts overflow onto the Continue row.
    // A container grows with its content instead.
    private PanelContainer _resultPanel;
    private Label _resultLabel;
    private Button _continueButton;

    private NarrativeEncounterData _encounter;
    private EncounterChoice _chosenResult;

    // Gating context supplied by the caller (ExpeditionManager) at show-time.
    private System.Func<string, bool> _hasFlag;
    private System.Func<string, bool> _hasItem;       // T3: Armory ownership gate
    private System.Func<string, bool> _hasCompanion;  // T3: active-party gate
    private string _activeSchool = "";
    private int _currentGold;

    // Step 9: campaign context for resolution-choice gating (may be null, in
    // which case resolution choices are hidden entirely).
    private CampaignState _campaign;

    // Fail-safe exit used only if every authored choice is gated out, so the
    // panel can never soft-lock the player behind unmet requirements.
    private static readonly EncounterChoice _fallbackChoice = new()
    { Label = "Move on.", ResultText = "You leave it be." };

    public override void _Ready()
    {
        // Cover the full viewport
        AnchorRight = 1f;
        AnchorBottom = 1f;
        MouseFilter = MouseFilterEnum.Stop; // block input to overworld behind us

        // Dim backdrop
        _backdrop = new Panel
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = MouseFilterEnum.Stop,
        };
        var backdropStyle = new StyleBoxFlat { BgColor = UITheme.NarrativeBackdrop }; ;
        _backdrop.AddThemeStyleboxOverride("panel", backdropStyle);
        AddChild(_backdrop);

        // Main encounter panel
        _panel = new Panel
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
            OffsetLeft = -350,
            OffsetTop = -280,
            OffsetRight = 350,
            OffsetBottom = 280,
            ClipContents = true, // long bodies scroll (below); nothing may overflow the frame
        };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = UITheme.NarrativePanelBg,
            BorderColor = UITheme.NarrativePanelBorder,
            BorderWidthTop = UITheme.BorderWidth,
            BorderWidthBottom = UITheme.BorderWidth,
            BorderWidthLeft = UITheme.BorderWidth,
            BorderWidthRight = UITheme.BorderWidth,
            CornerRadiusTopLeft = UITheme.NarrativePanelCorner,
            CornerRadiusTopRight = UITheme.NarrativePanelCorner,
            CornerRadiusBottomLeft = UITheme.NarrativePanelCorner,
            CornerRadiusBottomRight = UITheme.NarrativePanelCorner,
        };
        _panel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_panel);

        var layout = new VBoxContainer
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 24,
            OffsetTop = 24,
            OffsetRight = -24,
            OffsetBottom = -24,
        };
        layout.AddThemeConstantOverride("separation", 14);
        _panel.AddChild(layout);

        // Title
        _titleLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", UITheme.NarrativeTitleFontSize);
        _titleLabel.AddThemeColorOverride("font_color", UITheme.NarrativeTitleColor);
        layout.AddChild(_titleLabel);

        layout.AddChild(new HSeparator());

        // Body, inside a ScrollContainer so long texts (resolution audiences,
        // echo encounters) scroll instead of pushing the choices, result, and
        // Continue button out of the panel / off-screen (Step 9 fix).
        _bodyScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        layout.AddChild(_bodyScroll);

        _bodyLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _bodyLabel.AddThemeFontSizeOverride("font_size", UITheme.NarrativeBodyFontSize);
        _bodyLabel.AddThemeColorOverride("font_color", UITheme.NarrativeBodyColor);
        _bodyScroll.AddChild(_bodyLabel);

        layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

        // Choices
        _choiceContainer = new VBoxContainer();
        _choiceContainer.AddThemeConstantOverride("separation", 8);
        layout.AddChild(_choiceContainer);

        // Result panel (shown after choice)
        _resultPanel = new PanelContainer { Visible = false };
        var resultStyle = new StyleBoxFlat
        {
            BgColor = UITheme.NarrativeResultBg,
            BorderColor = UITheme.NarrativeResultBorder,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = UITheme.NarrativeResultCorner,
            CornerRadiusTopRight = UITheme.NarrativeResultCorner,
            CornerRadiusBottomLeft = UITheme.NarrativeResultCorner,
            CornerRadiusBottomRight = UITheme.NarrativeResultCorner,
            ContentMarginLeft = UITheme.PaddingNormal + 4,
            ContentMarginRight = UITheme.PaddingNormal + 4,
            ContentMarginTop = UITheme.PaddingNormal + 2,
            ContentMarginBottom = UITheme.PaddingNormal + 2,
        };
        _resultPanel.AddThemeStyleboxOverride("panel", resultStyle);
        _resultPanel.CustomMinimumSize = new Vector2(0, 110);   // floor, not ceiling
        layout.AddChild(_resultPanel);

        _resultLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _resultLabel.AddThemeFontSizeOverride("font_size", UITheme.NarrativeResultFontSize);
        _resultLabel.AddThemeColorOverride("font_color", UITheme.NarrativeResultColor);
        _resultPanel.AddChild(_resultLabel);

        // Continue button
        _continueButton = new Button
        {
            Text = "Continue",
            Visible = false,
            CustomMinimumSize = new Vector2(160, 40),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _continueButton.AddThemeFontSizeOverride("font_size", UITheme.NarrativeBodyFontSize);
        _continueButton.Pressed += OnContinuePressed;
        layout.AddChild(_continueButton);
    }

    /// <summary>Show an encounter. The optional context enables choice gating:
    /// <paramref name="hasFlag"/> tests timeline WorldFlags (RequiredFlag),
    /// <paramref name="activeSchool"/> matches RequiredSchool, and
    /// <paramref name="currentGold"/> gates RequiredGold options. Callers that
    /// pass nothing get the old ungated behaviour.</summary>
    public void ShowEncounter(NarrativeEncounterData encounter,
                              System.Func<string, bool> hasFlag = null,
                              string activeSchool = null,
                              int currentGold = 0,
                              CampaignState campaign = null,
                              System.Func<string, bool> hasItem = null,
                              System.Func<string, bool> hasCompanion = null)
    {
        _hasItem = hasItem;
        _hasCompanion = hasCompanion;
        _encounter = encounter;
        _chosenResult = null;
        _hasFlag = hasFlag;
        _activeSchool = activeSchool ?? "";
        _currentGold = currentGold;
        _campaign = campaign;

        // Resolution gating (Step 9): computed once per show. Unite/Coerce are
        // shown-but-disabled with the blocking reason (the RequiredGold
        // pattern); Overthrow is always pressable.
        bool resUnite = false, resCoerce = false;
        bool isResolution = !string.IsNullOrEmpty(encounter.ArchmageId) && _campaign != null;
        if (isResolution)
            (resUnite, resCoerce, _) = _campaign.ResolutionOptions(encounter.ArchmageId, _hasFlag);

        _titleLabel.Text = encounter.Title;
        _bodyLabel.Text = encounter.Body;
        if (_bodyScroll != null) _bodyScroll.ScrollVertical = 0;

        // Clear old buttons; re-show the container (hidden after a choice).
        _choiceContainer.Visible = true;
        foreach (var child in _choiceContainer.GetChildren())
            child.QueueFree();

        // Build choice buttons, applying gates.
        int interactable = 0;
        foreach (var choice in encounter.Choices)
        {
            // RequiredFlag: a hidden branch that only surfaces once the world
            // remembers the earlier choice. Omit entirely when unmet.
            if (!string.IsNullOrEmpty(choice.RequiredFlag) &&
                (_hasFlag == null || !_hasFlag(choice.RequiredFlag)))
                continue;

            // Tranche 3 (2026-08-13): item/companion gates, same omit-when-unmet
            // convention as flags: the door only exists for those holding the key.
            if (!string.IsNullOrEmpty(choice.RequiredItem) &&
                (_hasItem == null || !_hasItem(choice.RequiredItem)))
                continue;
            if (!string.IsNullOrEmpty(choice.RequiredCompanion) &&
                (_hasCompanion == null || !_hasCompanion(choice.RequiredCompanion)))
                continue;

            // RequiredSchool: option exists only for the matching school.
            if (!string.IsNullOrEmpty(choice.RequiredSchool) &&
                !string.Equals(choice.RequiredSchool, _activeSchool,
                               System.StringComparison.OrdinalIgnoreCase))
                continue;

            // ResolutionKind: unite/coerce render disabled with the blocking
            // reason when sentiment/corruption thresholds aren't met; overthrow
            // is always available. Hidden entirely without campaign context.
            bool resBlocked = false;
            string resReason = "";
            if (!string.IsNullOrEmpty(choice.ResolutionKind))
            {
                if (!isResolution)
                    continue; // no campaign context, so resolution verbs don't render
                switch (choice.ResolutionKind.ToLowerInvariant())
                {
                    case "unite":
                        resBlocked = !resUnite;
                        if (resBlocked)
                            resReason = _campaign.GetSentiment(_encounter.ArchmageId) < 40
                                ? "(their trust is not yet won)"
                                : "(corruption has gone too deep)";
                        break;
                    case "coerce":
                        resBlocked = !resCoerce;
                        if (resBlocked)
                        {
                            int cs = _campaign.GetSentiment(_encounter.ArchmageId);
                            resReason = cs >= 40 ? "(they would sooner be asked)"
                                : cs < -20 ? "(they will not treat with you)"
                                : "(you know too little to press)"; // in-window: missing dossier leverage
                        }
                        break;
                    // "overthrow" and anything else: always pressable.
                }
            }

            // RequiredGold: shown but disabled, with the reason, when unaffordable.
            bool tooPoor = choice.RequiredGold > 0 && _currentGold < choice.RequiredGold;

            bool disabled = tooPoor || resBlocked;
            string label = choice.Label;
            if (tooPoor) label = $"{choice.Label}  (needs {choice.RequiredGold} gold)";
            else if (resBlocked) label = $"{choice.Label}  {resReason}";

            var btn = new Button
            {
                Text = label,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(0, 44),
                Disabled = disabled,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.NarrativeChoiceFontSize);
            var capturedChoice = choice;
            btn.Pressed += () => OnChoicePressed(capturedChoice);
            _choiceContainer.AddChild(btn);
            if (!disabled) interactable++;
        }

        // Never soft-lock: if nothing is pressable, offer a neutral exit.
        if (interactable == 0)
        {
            var btn = new Button
            {
                Text = "Move on.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(0, 44),
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.NarrativeChoiceFontSize);
            btn.Pressed += () => OnChoicePressed(_fallbackChoice);
            _choiceContainer.AddChild(btn);
        }

        _resultPanel.Visible = false;
        _continueButton.Visible = false;
        Visible = true;
    }

    private void OnChoicePressed(EncounterChoice choice)
    {
        _chosenResult = choice;

        // Hide the choices once one is taken. The dead buttons only crowd the
        // result. The chosen line is echoed above the result text instead.
        _choiceContainer.Visible = false;

        // Build result text + outcome summary
        string resultText = $"»  {choice.Label}\n\n{choice.ResultText}";
        var outcomes = new System.Collections.Generic.List<string>();
        if (choice.GoldDelta > 0) outcomes.Add($"+{choice.GoldDelta} gold");
        if (choice.GoldDelta < 0) outcomes.Add($"{choice.GoldDelta} gold");
        if (choice.HPDelta > 0) outcomes.Add($"+{choice.HPDelta} HP");
        if (choice.HPDelta < 0) outcomes.Add($"{choice.HPDelta} HP");
        if (choice.StepDelta > 0) outcomes.Add($"+{choice.StepDelta} steps");
        if (choice.StepDelta < 0) outcomes.Add($"{choice.StepDelta} steps");
        if (!string.IsNullOrEmpty(choice.ItemReward)) outcomes.Add("a relic for the armory");
        if (!string.IsNullOrEmpty(choice.CompanionUnlock)) outcomes.Add("a companion joins");
        if (choice.ReputationAmount != 0)
            outcomes.Add($"{(choice.ReputationAmount > 0 ? "+" : "")}{choice.ReputationAmount} reputation");
        if (!string.IsNullOrEmpty(choice.LoreId)) outcomes.Add("lore uncovered");

        if (outcomes.Count > 0)
            resultText += $"\n\n{string.Join("  |  ", outcomes)}";

        _resultLabel.Text = resultText;
        _resultPanel.Visible = true;
        _continueButton.Visible = true;
    }

    private void OnContinuePressed()
    {
        Visible = false;
        OnCompleted?.Invoke(_chosenResult);
    }
}