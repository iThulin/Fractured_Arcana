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
    private Label _bodyLabel;
    private VBoxContainer _choiceContainer;
    private Panel _resultPanel;
    private Label _resultLabel;
    private Button _continueButton;

    private NarrativeEncounterData _encounter;
    private EncounterChoice _chosenResult;

    // Gating context supplied by the caller (ExpeditionManager) at show-time.
    private System.Func<string, bool> _hasFlag;
    private string _activeSchool = "";
    private int _currentGold;

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
            OffsetLeft = -320,
            OffsetTop = -280,
            OffsetRight = 320,
            OffsetBottom = 280,
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

        // Body
        _bodyLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _bodyLabel.AddThemeFontSizeOverride("font_size", UITheme.NarrativeBodyFontSize);
        _bodyLabel.AddThemeColorOverride("font_color", UITheme.NarrativeBodyColor);
        layout.AddChild(_bodyLabel);

        layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

        // Choices
        _choiceContainer = new VBoxContainer();
        _choiceContainer.AddThemeConstantOverride("separation", 8);
        layout.AddChild(_choiceContainer);

        // Result panel (shown after choice)
        _resultPanel = new Panel { Visible = false };
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
        _resultPanel.CustomMinimumSize = new Vector2(0, 70);
        layout.AddChild(_resultPanel);

        _resultLabel = new Label
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
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
                              int currentGold = 0)
    {
        _encounter = encounter;
        _chosenResult = null;
        _hasFlag = hasFlag;
        _activeSchool = activeSchool ?? "";
        _currentGold = currentGold;

        _titleLabel.Text = encounter.Title;
        _bodyLabel.Text = encounter.Body;

        // Clear old buttons
        foreach (var child in _choiceContainer.GetChildren())
            child.QueueFree();

        // Build choice buttons, applying gates.
        int interactable = 0;
        foreach (var choice in encounter.Choices)
        {
            // RequiredFlag — a hidden branch that only surfaces once the world
            // remembers the earlier choice. Omit entirely when unmet.
            if (!string.IsNullOrEmpty(choice.RequiredFlag) &&
                (_hasFlag == null || !_hasFlag(choice.RequiredFlag)))
                continue;

            // RequiredSchool — option exists only for the matching school.
            if (!string.IsNullOrEmpty(choice.RequiredSchool) &&
                !string.Equals(choice.RequiredSchool, _activeSchool,
                               System.StringComparison.OrdinalIgnoreCase))
                continue;

            // RequiredGold — shown but disabled, with the reason, when unaffordable.
            bool tooPoor = choice.RequiredGold > 0 && _currentGold < choice.RequiredGold;

            var btn = new Button
            {
                Text = tooPoor
                    ? $"{choice.Label}  (needs {choice.RequiredGold} gold)"
                    : choice.Label,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(0, 44),
                Disabled = tooPoor,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.NarrativeChoiceFontSize);
            var capturedChoice = choice;
            btn.Pressed += () => OnChoicePressed(capturedChoice);
            _choiceContainer.AddChild(btn);
            if (!tooPoor) interactable++;
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

        // Disable choice buttons
        foreach (var child in _choiceContainer.GetChildren())
            if (child is Button b) b.Disabled = true;

        // Build result text + outcome summary
        string resultText = choice.ResultText;
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