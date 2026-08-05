using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardRewardScreen.cs
//
// Purpose:        Mid-run card reward screen. Shown after every
//                 combat win before returning to the overworld.
//                 Presents 3 draft choices (weighted by rarity;
//                 the last slot is always Adept for non-Adept
//                 schools; Legendaries are never offered — they
//                 are Regalia). Player picks 1 or
//                 skips. Chosen card is added to PlayerDeckSave
//                 collection (stash) via PlayerDeckService.
//                 On skip or pick, routes back to overworld via
//                 EncounterRouter.
// Layer:          UI
// Collaborators:  CardDatabase.cs (DraftablePool / WeightedDraftPool),
//                 PlayerDeckService.cs (AddCardToCollection),
//                 SaveManager.cs (ActiveSave + Save),
//                 EncounterRouter.cs (ReturnToOverworld),
//                 CardUi.cs (card display, CardUIScene export),
//                 UITheme.cs, SchoolColors.cs
// ============================================================

public partial class CardRewardScreen : Control
{
    [Export] public PackedScene CardUIScene;
    [Export] public string OverworldScenePath = "res://Scenes/Overworld/ExpeditionScene.tscn";

    // How many cards the draft offers. The LAST slot is always the Adept
    // (neutral) slot for non-Adept schools — the 2026-07-10 ruling, finally
    // enacted in the live path. See GenerateOffers.
    private const int SlotCount = 3;

    private VBoxContainer _layout;
    private HBoxContainer _cardRow;
    private Label _titleLabel;
    private Label _subLabel;
    private Button _skipButton;

    // The three blueprints shown this draft
    private List<CardBlueprint> _offered = new();
    private bool _chosen = false;

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUI));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Build
    // ═════════════════════════════════════════════════════════════════════

    private void BuildUI()
    {
        GD.Print("[UpgradeScreen] BuildUI started");
        // Background overlay — semi-opaque dark panel over combat remnants
        var bg = new ColorRect { Color = new Color(0.04f, 0.04f, 0.08f, 0.96f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // Centered content column
        var center = new VBoxContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.AddThemeConstantOverride("separation", 24);
        // Margin so content isn't flush against edges
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 80);
        margin.AddThemeConstantOverride("margin_right", 80);
        margin.AddThemeConstantOverride("margin_top", 60);
        margin.AddThemeConstantOverride("margin_bottom", 60);
        AddChild(margin);
        margin.AddChild(center);

        // Title
        _titleLabel = new Label
        {
            Text = "VICTORY",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", UITheme.FontSizeTitle);
        _titleLabel.AddThemeColorOverride("font_color", UITheme.Gold);
        center.AddChild(_titleLabel);

        // Sub-title
        _subLabel = new Label
        {
            Text = "Choose a card to add to your collection, or skip.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _subLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _subLabel.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        center.AddChild(_subLabel);

        // Splinter reward info
        var router = EncounterRouter.Instance;
        if (router != null && router.SplinterReward > 0)
        {
            var splinterLbl = new Label
            {
                Text = $"+{router.SplinterReward} Arcane Splinters earned",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            splinterLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            splinterLbl.AddThemeColorOverride("font_color", new Color(0.6f, 0.85f, 1f));
            center.AddChild(splinterLbl);
        }

        // Card row — three cards side by side
        _cardRow = new HBoxContainer();
        _cardRow.AddThemeConstantOverride("separation", 32);
        _cardRow.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _cardRow.SizeFlagsVertical = SizeFlags.ExpandFill;
        center.AddChild(_cardRow);

        // Generate offers
        GenerateOffers();
        PopulateCardRow();

        // Skip button
        _skipButton = new Button
        {
            Text = "Skip — Take Nothing",
            CustomMinimumSize = new Vector2(260, 48),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _skipButton.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        UITheme.ApplyButtonStyle(_skipButton, isPrimary: false);
        _skipButton.Pressed += OnSkip;
        center.AddChild(_skipButton);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Card generation
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rolls the three offers. Two rules changed here on 2026-08-04
    /// (docs/progression_card_acquisition_v1.md §1d, §5, §6a):
    ///
    ///  • Pools now come from <see cref="CardDatabase.WeightedDraftPool"/>, which
    ///    applies the unlock gate and drops Legendaries. This screen used to build
    ///    its own pool filtered on school alone, so every card in the game was
    ///    offerable on run one and the Ledger's unlock list was write-only.
    ///
    ///  • The 2026-07-10 Adept-as-neutral ruling is now actually in effect: the
    ///    LAST slot always offers an Adept card. It had been implemented only in
    ///    the dead CardDatabase.GetDraftChoices, while this live path rolled an
    ///    independent 30% per slot — so ~34% of drafts offered no Adept card at
    ///    all. The old GenericChance constant is gone with it.
    /// </summary>
    private void GenerateOffers()
    {
        _offered.Clear();

        var save = SaveManager.ActiveSave;
        var school = Enum.TryParse<CardSchool>(save?.SelectedSchool, ignoreCase: true, out var s)
                     ? s : CardSchool.Elementalist;

        var rng = new Random();

        var schoolPool = CardDatabase.WeightedDraftPool(school, save);

        // The Adept class itself never drafts (EncounterRouter pays a splinter
        // stipend instead), so the neutral slot only applies to other schools.
        var neutralPool = school != CardSchool.Adept
            ? CardDatabase.WeightedDraftPool(CardSchool.Adept, save)
            : new List<CardBlueprint>();

        var usedIds = new HashSet<string>();

        for (int i = 0; i < SlotCount; i++)
        {
            bool isNeutralSlot = (i == SlotCount - 1) && neutralPool.Count > 0;
            var pool = isNeutralSlot ? neutralPool : schoolPool;

            // Either pool can be empty on a narrow unlock set — fall through to
            // whichever still has cards rather than showing a blank slot.
            if (pool.Count == 0)
                pool = schoolPool.Count > 0 ? schoolPool : neutralPool;
            if (pool.Count == 0)
                break;

            // Try up to 10 times to avoid duplicates
            CardBlueprint pick = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var candidate = pool[rng.Next(pool.Count)];
                if (!usedIds.Contains(candidate.Id))
                { pick = candidate; break; }
            }
            // Fallback: allow duplicate if pool too small
            pick ??= pool[rng.Next(pool.Count)];

            usedIds.Add(pick.Id);
            _offered.Add(pick);
        }

        if (_offered.Count == 0)
            GD.PrintErr($"[CardReward] No offers generated for {school} — " +
                        $"school pool {schoolPool.Count}, neutral pool {neutralPool.Count}.");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Card row population
    // ═════════════════════════════════════════════════════════════════════

    private void PopulateCardRow()
    {
        foreach (Node child in _cardRow.GetChildren())
            child.QueueFree();

        if (_offered.Count == 0)
        {
            var none = new Label { Text = "No cards available." };
            none.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            none.AddThemeColorOverride("font_color", UITheme.TextDim);
            _cardRow.AddChild(none);
            return;
        }

        foreach (var bp in _offered)
            _cardRow.AddChild(BuildCardSlot(bp));
    }

    private Control BuildCardSlot(CardBlueprint bp)
    {
        float scale = 0.90f;
        float cardW = UITheme.LibraryCardWidth * scale;
        float cardH = UITheme.LibraryCardHeight * scale;

        // Outer VBox: card + pick button below
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        col.SizeFlagsVertical = SizeFlags.ShrinkBegin;

        // Wrapper so the CardUi renders at the right size
        var wrapper = new Control
        {
            CustomMinimumSize = new Vector2(cardW, cardH),
            ClipContents = false,
        };
        col.AddChild(wrapper);

        if (CardUIScene != null)
        {
            var cardUi = CardUIScene.Instantiate<CardUi>();
            wrapper.AddChild(cardUi);
            cardUi.SetCard(bp.Prebuilt.TopHalf, bp.Prebuilt.BottomHalf);
            cardUi.SetProcess(false);

            // Wait one frame for _Ready to finish, then force visible state
            var timer = GetTree().CreateTimer(0.05f);
            timer.Timeout += () =>
            {
                if (!IsInstanceValid(cardUi))
                    return;
                cardUi.SetProcess(false);
                cardUi.Modulate = Colors.White;
                cardUi.Rotation = 0f;
                cardUi.Position = Vector2.Zero;
                cardUi.Scale = new Vector2(scale, scale);
            };
        }
        else
        {
            // Fallback text card if CardUIScene not assigned
            var fallback = BuildTextCardFallback(bp);
            fallback.CustomMinimumSize = new Vector2(cardW, cardH);
            wrapper.AddChild(fallback);
        }

        // Rarity label
        var rarityLbl = new Label
        {
            Text = bp.Rarity.ToString().ToUpper(),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        rarityLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        rarityLbl.AddThemeColorOverride("font_color", UITheme.RarityColor(bp.Rarity.ToString()));
        col.AddChild(rarityLbl);

        // Pick button
        var pickBtn = new Button
        {
            Text = "Add to Collection",
            CustomMinimumSize = new Vector2(cardW, 40),
        };
        pickBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(pickBtn, isPrimary: true);
        var capturedBp = bp;
        pickBtn.Pressed += () => OnCardPicked(capturedBp);
        col.AddChild(pickBtn);

        return col;
    }

    private void ApplyPreviewCardState(CardUi cardUi, float scale)
    {
        if (!IsInstanceValid(cardUi))
            return;
        cardUi.SetProcess(false);
        cardUi.Modulate = Colors.White;
        cardUi.Rotation = 0f;
        cardUi.Scale = new Vector2(scale, scale);
        cardUi.Position = Vector2.Zero;

        // Cancel any in-flight tweens the entry animation may have started
        //foreach (var tween in cardUi.GetChildren().OfType<Tween>())
        //tween.Kill();
    }

    /// <summary>Text-only fallback card panel used when CardUIScene is null.</summary>
    private Control BuildTextCardFallback(CardBlueprint bp)
    {
        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.SurfaceLight,
            BorderColor = SchoolColors.GetBorderColor(bp.School),
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            CornerRadiusTopLeft = UITheme.CornerRadius,
            CornerRadiusTopRight = UITheme.CornerRadius,
            CornerRadiusBottomLeft = UITheme.CornerRadius,
            CornerRadiusBottomRight = UITheme.CornerRadius,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 12,
            ContentMarginBottom = 12,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        panel.AddChild(vbox);

        var topName = new Label { Text = bp.Prebuilt?.TopHalf?.Name ?? "" };
        topName.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        topName.AddThemeColorOverride("font_color", UITheme.TextOnLight);
        vbox.AddChild(topName);

        var topRules = new Label
        {
            Text = bp.Prebuilt?.TopHalf?.RulesText ?? "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        topRules.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        topRules.AddThemeColorOverride("font_color", UITheme.TextOnLight);
        vbox.AddChild(topRules);

        vbox.AddChild(new HSeparator());

        var botName = new Label { Text = bp.Prebuilt?.BottomHalf?.Name ?? "" };
        botName.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        botName.AddThemeColorOverride("font_color", UITheme.TextOnLight);
        vbox.AddChild(botName);

        var botRules = new Label
        {
            Text = bp.Prebuilt?.BottomHalf?.RulesText ?? "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        botRules.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        botRules.AddThemeColorOverride("font_color", UITheme.TextOnLight);
        vbox.AddChild(botRules);

        return panel;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Actions
    // ═════════════════════════════════════════════════════════════════════

    private void OnCardPicked(CardBlueprint bp)
    {
        if (_chosen)
            return;
        _chosen = true;

        var save = SaveManager.ActiveSave;
        if (save != null)
        {
            var owned = PlayerDeckService.AddCardToCollection(save, bp.Id);
            SaveManager.Save();
            GD.Print($"[CardReward] Added '{bp.Id}' to collection " +
                     $"(instance: {owned?.InstanceId[..8]}…)");
        }

        // Visual feedback — disable all buttons
        DisableAllButtons();

        // Brief delay so the player sees the selection before scene swap
        GetTree().CreateTimer(0.8f).Timeout += ReturnToOverworld;
    }

    private void OnSkip()
    {
        if (_chosen)
            return;
        _chosen = true;

        GD.Print("[CardReward] Player skipped card reward.");
        DisableAllButtons();
        ReturnToOverworld();
    }

    private void DisableAllButtons()
    {
        if (_skipButton != null)
            _skipButton.Disabled = true;
        foreach (Node col in _cardRow.GetChildren())
        {
            foreach (Node child in col.GetChildren())
            {
                if (child is Button btn)
                    btn.Disabled = true;
            }
        }
    }

    private void ReturnToOverworld()
    {
        // Step 9: honor a campus return override so drafting after a
        // campus-launched fight routes home, not to the expedition map.
        string target = EncounterRouter.Instance?.ReturnScenePath
            ?? "res://Scenes/Overworld/ExpeditionScene.tscn";
        GetTree().ChangeSceneToFile(target);
    }
}
