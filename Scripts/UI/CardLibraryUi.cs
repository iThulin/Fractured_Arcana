using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardLibraryUi.cs
//
// Purpose:        Browse-all-cards UI. Filterable grid of every
//                 registered blueprint by school, rarity, mana
//                 cost, and free-text search. Used as a campus
//                 sub-screen and as an inline pause overlay.
// Layer:          UI
// Collaborators:  CardDatabase.cs (Blueprints source),
//                 CardLoaderV2.cs (lazy-load trigger),
//                 CardUi.cs (per-tile scene),
//                 UITheme.cs (filter tab + grid sizing)
// See:            README §6 — accessed from campus and from pause
// ============================================================

/// <summary>Filterable browse-all-cards grid. Loads card JSON on demand if the database is empty. Filters compose (school AND rarity AND mana AND search). <see cref="ReturnScenePath"/> follows the same back-button convention as <see cref="SettingsMenu"/>.</summary>
public partial class CardLibraryUi : Control
{
    // ── Exports ──────────────────────────────────────────────────────────
    [Export] public PackedScene CardUIScene;
    [Export] public string CardJsonDirectory = "res://Data/Cards";
    [Export] public string ReturnScenePath = "res://Scenes/Campus/CampusScene.tscn";

    [Export(PropertyHint.Range, "0.5,1.5,0.05")]
    public float CardScale = 1f;

    // ── Node refs (wired from .tscn) ─────────────────────────────────────
    private HBoxContainer _schoolTabs;
    private HBoxContainer _rarityTabs;
    private HBoxContainer _manaTabs;
    private LineEdit _searchBox;
    private ScrollContainer _scroll;
    private GridContainer _cardGrid;
    private Label _countLabel;
    private Button _backButton;

    // ── Filter state ─────────────────────────────────────────────────────
    private CardSchool? _schoolFilter = null;
    private CardRarity? _rarityFilter = null;
    private int _manaFilter = -1;
    private string _searchText = "";

    // ── Data ─────────────────────────────────────────────────────────────
    private List<CardBlueprint> _pool = new();

    private int _lastColumnCount = -1;

    // ── Detail panel ──────────────────────────────────────────────────────
    private ScrollContainer _detailPanel = null;
    private VBoxContainer _detailContent = null;
    private CardBlueprint _selectedBlueprint = null;

    // ═════════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ═════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        WireNodes();

        // Clear the global top-bar HUD. The bar (HudManager, layer 90) floats
        // over scene content without reserving layout space, so the library
        // must offset itself below it or the filter rows clip underneath.
        // Skipped when opened as an inline pause overlay — that host renders
        // ABOVE the bar (pause layer 100 > HUD layer 90), so no clearance is
        // needed and the extra gap would look wrong.
        if (ReturnScenePath != PauseMenu.InlineSentinel)
        {
            var margin = GetNodeOrNull<MarginContainer>("Margin");
            margin?.AddThemeConstantOverride("margin_top", 12 + HudManager.BarHeight);
        }

        EnsureCardsLoaded();
        _pool = CardDatabase.Blueprints;

        GD.Print($"[CardLibrary] _Ready — Blueprints: {_pool.Count}, " +
                 $"CardUIScene null? {CardUIScene == null}");

        BuildSchoolTabs();
        BuildRarityTabs();
        BuildManaTabs();

        if (_searchBox != null)
        {
            _searchBox.PlaceholderText = "Search cards\u2026";
            _searchBox.TextChanged += OnSearchChanged;
        }

        if (_backButton != null)
            _backButton.Pressed += OnBackPressed;

        CallDeferred(nameof(RebuildGrid));
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && _cardGrid != null)
        {
            int cols = CalculateColumns();
            if (cols != _lastColumnCount)
                RebuildGrid();
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Node wiring
    // ═════════════════════════════════════════════════════════════════════

    private void WireNodes()
    {
        _schoolTabs = GetNodeOrNull<HBoxContainer>("Margin/VBox/FilterBar/SchoolTabs");
        _rarityTabs = GetNodeOrNull<HBoxContainer>("Margin/VBox/FilterBar/RarityTabs");
        _manaTabs = GetNodeOrNull<HBoxContainer>("Margin/VBox/FilterBar/ManaRow/ManaTabs");
        _searchBox = GetNodeOrNull<LineEdit>("Margin/VBox/FilterBar/ManaRow/SearchBox");
        _countLabel = GetNodeOrNull<Label>("Margin/VBox/FilterBar/ManaRow/CountLabel");
        _scroll = GetNodeOrNull<ScrollContainer>("Margin/VBox/ContentRow/Scroll");
        _cardGrid = GetNodeOrNull<GridContainer>("Margin/VBox/ContentRow/Scroll/GridCentering/CardGrid");
        _backButton = GetNodeOrNull<Button>("Margin/VBox/TopBar/BackButton");
        _detailPanel = GetNodeOrNull<ScrollContainer>("Margin/VBox/ContentRow/DetailPanel");
        _detailContent = GetNodeOrNull<VBoxContainer>("Margin/VBox/ContentRow/DetailPanel/DetailContent");

        if (_schoolTabs == null) GD.PrintErr("[CardLibrary] SchoolTabs not found");
        if (_rarityTabs == null) GD.PrintErr("[CardLibrary] RarityTabs not found");
        if (_manaTabs == null) GD.PrintErr("[CardLibrary] ManaTabs not found");
        if (_searchBox == null) GD.PrintErr("[CardLibrary] SearchBox not found");
        if (_countLabel == null) GD.PrintErr("[CardLibrary] CountLabel not found");
        if (_scroll == null) GD.PrintErr("[CardLibrary] Scroll not found");
        if (_cardGrid == null) GD.PrintErr("[CardLibrary] CardGrid not found");
        if (_backButton == null) GD.PrintErr("[CardLibrary] BackButton not found");
        if (_detailPanel == null) GD.PrintErr("[CardLibrary] DetailPanel not found");
        if (_detailContent == null) GD.PrintErr("[CardLibrary] DetailContent not found");
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Card loading
    // ═════════════════════════════════════════════════════════════════════

    private void EnsureCardsLoaded()
    {
        CardLoaderV2.LoadCardsFromJson(CardJsonDirectory);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Filter tab builders
    // ═════════════════════════════════════════════════════════════════════

    private void BuildSchoolTabs()
    {
        if (_schoolTabs == null) return;
        ClearChildren(_schoolTabs);

        MakeTab(_schoolTabs, "All", _schoolFilter == null, () =>
        {
            _schoolFilter = null;
            RebuildGrid();
        });

        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
        {
            var s = school;
            int count = _pool.Count(b => b.School == s);
            MakeTab(_schoolTabs, $"{s} ({count})", false, () =>
            {
                _schoolFilter = s;
                RebuildGrid();
            });
        }
    }

    private void BuildRarityTabs()
    {
        if (_rarityTabs == null) return;
        ClearChildren(_rarityTabs);

        MakeTab(_rarityTabs, "All", true, () =>
        {
            _rarityFilter = null;
            RebuildGrid();
        });

        foreach (CardRarity r in Enum.GetValues(typeof(CardRarity)))
        {
            var rr = r;
            MakeTab(_rarityTabs, r.ToString(), false, () =>
            {
                _rarityFilter = rr;
                RebuildGrid();
            });
        }
    }

    private void BuildManaTabs()
    {
        if (_manaTabs == null) return;
        ClearChildren(_manaTabs);

        MakeTab(_manaTabs, "All", true, () =>
        {
            _manaFilter = -1;
            RebuildGrid();
        });

        for (int m = 0; m <= 5; m++)
        {
            int captured = m;
            string label = m < 5 ? m.ToString() : "5+";
            MakeTab(_manaTabs, label, false, () =>
            {
                _manaFilter = captured;
                RebuildGrid();
            });
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Tab / button helpers
    // ═════════════════════════════════════════════════════════════════════

    private static void MakeTab(HBoxContainer parent, string text,
                                bool active, Action onPress)
    {
        var btn = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonPressed = active,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, UITheme.LibraryTabHeight),
        };

        btn.AddThemeStyleboxOverride("normal", FlatBox(UITheme.LibraryTabNormal));
        btn.AddThemeStyleboxOverride("pressed", FlatBox(UITheme.LibraryTabPressed));
        btn.AddThemeStyleboxOverride("hover", FlatBox(UITheme.LibraryTabHover));
        btn.AddThemeFontSizeOverride("font_size", UITheme.LibraryTabFontSize);

        btn.Pressed += () =>
        {
            foreach (var child in parent.GetChildren())
                if (child is Button other && other != btn)
                    other.ButtonPressed = false;
            btn.ButtonPressed = true;
            onPress?.Invoke();
        };

        parent.AddChild(btn);
    }

    private static StyleBoxFlat FlatBox(Color bg)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        sb.SetCornerRadiusAll(UITheme.CornerRadius);
        sb.SetContentMarginAll(UITheme.PaddingNormal - 2);
        return sb;
    }

    private static void ClearChildren(Node parent)
    {
        foreach (Node c in parent.GetChildren())
            c.QueueFree();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Search
    // ═════════════════════════════════════════════════════════════════════

    private void OnSearchChanged(string newText)
    {
        _searchText = (newText ?? "").Trim().ToLower();
        RebuildGrid();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Dynamic column count
    // ═════════════════════════════════════════════════════════════════════

    private int CalculateColumns()
    {
        float available = _scroll?.Size.X ?? GetViewportRect().Size.X;
        // Panel takes 520px when open — grid gets the remainder
        if (_detailPanel != null && _detailPanel.Visible)
            available = Mathf.Max(available - 532, 100);
        float cellW = UITheme.LibraryCardWidth * CardScale + UITheme.LibraryGridSpacing;
        return Mathf.Max(1, Mathf.FloorToInt(available / cellW));
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Detail panel
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The Library's scribing bench — the deterministic acquisition route
    /// (progression_card_acquisition_v1 §8, "Library research"). Every other way to
    /// get a card is a dice roll; this is the one where you name the card you want,
    /// pay, and receive it — at whatever tier you have already mastered.
    ///
    /// Bounded by a per-cycle cap (Arcane Library tier) so it cannot make the draft
    /// pointless or the reseed cosmetic. Everything it needs is already on this
    /// screen: the Library lists every blueprint in the game, discovered or not.
    /// </summary>
    private void BuildMintSection(CardBlueprint bp)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || bp == null) return;

        var mastered = CardMintService.Evaluate(save, bp, atBase: false);
        var atBase = CardMintService.Evaluate(save, bp, atBase: true);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _detailContent.AddChild(row);

        var info = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        info.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);

        // Blocked either way — show the reason and stop. Base is the cheaper of the
        // two, so if even that is refused there is nothing actionable to offer.
        if (!atBase.CanMint && !mastered.CanMint)
        {
            info.Text = atBase.Blocker;
            info.AddThemeColorOverride("font_color", UITheme.TextDim);
            row.AddChild(info);
            return;
        }

        info.Text = $"Scriptorium use: {atBase.MintsUsed}/{atBase.MintsAllowed} this timeline. " +
                    $"A scribed copy goes to the stash — slot it in the deck editor.";
        info.AddThemeColorOverride("font_color", UITheme.HealthGreen);
        row.AddChild(info);

        var captured = bp;

        void AddMintButton(MintStatus st, bool wantBase, string label)
        {
            var btn = new Button
            {
                Text = $"{label}  {st.SplinterCost}✦",
                CustomMinimumSize = new Vector2(190, 32),
                Disabled = !st.CanMint,
                TooltipText = st.CanMint ? "" : st.Blocker,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: !wantBase);
            btn.Pressed += () =>
            {
                if (CardMintService.Mint(SaveManager.ActiveSave, captured, wantBase) == null) return;
                SaveManager.Save();
                ShowDetailPanel(captured);   // re-evaluate: budget and splinters moved
            };
            row.AddChild(btn);
        }

        // Offer the mastered copy only when it is actually better than base. The
        // tier price is charged in full (see CardMintService.TierCost), so this is a
        // real choice — a mastered 3/3 costs far more than a plain copy, and a player
        // who cannot afford it can still take the base one rather than being priced
        // out of their own mastery.
        bool hasMastery = mastered.TopTier > 0 || mastered.BotTier > 0;
        if (hasMastery)
            AddMintButton(mastered, false, $"Scribe {mastered.TopTier}/{mastered.BotTier}");

        AddMintButton(atBase, true, hasMastery ? "Scribe plain" : "Scribe");

        // Lifetime mastery, permanent and cross-timeline. Worth surfacing here even
        // when minting is blocked — it is the number that decides what a copy would
        // arrive at, and until now the player had no way to see it at all.
        var best = CardMasteryService.Best(save, bp.Id);
        if (best.Casts > 0 || best.BestTopTier > 0 || best.BestBotTier > 0)
        {
            var mastery = new Label
            {
                Text = $"Mastery: {best.Casts} lifetime casts  ·  best {best.BestTopTier}/{best.BestBotTier}",
            };
            mastery.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            mastery.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
            _detailContent.AddChild(mastery);
        }
    }

    /// <summary>Marginalia lock note (marginalia_spec_v1 R5): a Marginalia reward
    /// card shows WHY it is locked and how far the hunt has come, turning the
    /// library into the want-list surface. One label; nothing when the blueprint
    /// is not a Marginalia card.</summary>
    private void BuildMarginaliaNote(CardBlueprint bp)
    {
        if (bp == null || !MarginaliaService.TryGetFamilyForCard(bp.Id, out var family))
            return;

        var save = SaveManager.ActiveSave;
        if (save == null) return;

        var note = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);

        string faction = ArchmageRegistry.Get(family)?.FactionName ?? family;
        if (MarginaliaService.IsComplete(save, family))
        {
            note.Text = $"Marginalia: earned against {faction} — the entry is complete.";
            note.AddThemeColorOverride("font_color", UITheme.HealthGreen);
        }
        else
        {
            int kills = MarginaliaService.KillCount(save, family);
            int threshold = MarginaliaService.Threshold(family);
            int remaining = threshold > 0 ? Mathf.Max(0, threshold - kills) : -1;
            note.Text = remaining > 0
                ? $"Marginalia: defeat {remaining} more of {faction} to learn their trick ({kills}/{threshold})."
                : $"Marginalia: earned by defeating {faction} in the field.";
            note.AddThemeColorOverride("font_color", UITheme.TextDim);
        }

        _detailContent.AddChild(note);
    }

    private void ShowDetailPanel(CardBlueprint bp)
    {
        if (_detailPanel == null || _detailContent == null) return;

        ClearChildren(_detailContent);
        _detailPanel.Visible = true;
        RebuildGrid();
        _detailPanel.ScrollVertical = 0;

        float cardScale = 0.72f;
        float cw = UITheme.LibraryCardWidth * cardScale;
        float ch = UITheme.LibraryCardHeight * cardScale;

        // ── Header ────────────────────────────────────────────────────────
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        _detailContent.AddChild(header);

        var titleLabel = new Label
        {
            Text = CardDatabase.GetDisplayName(bp),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        titleLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize + 2);
        titleLabel.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        header.AddChild(titleLabel);

        var rarityLabel = new Label
        {
            Text = bp.Rarity.ToString(),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        rarityLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        rarityLabel.AddThemeColorOverride("font_color", UITheme.GetRarityColor(bp.Rarity));
        header.AddChild(rarityLabel);

        BuildMintSection(bp);
        BuildMarginaliaNote(bp);

        var closeBtn = new Button { Text = "✕" };
        closeBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(closeBtn, isPrimary: false);
        closeBtn.Pressed += () =>
        {
            _selectedBlueprint = null;
            _detailPanel.Visible = false;
            RebuildGrid();
            foreach (var child in _cardGrid.GetChildren())
                if (child is Control w)
                {
                    var h = w.GetNodeOrNull<ColorRect>("ColorRect");
                    if (h != null) h.Visible = false;
                }
        };
        header.AddChild(closeBtn);

        _detailContent.AddChild(new HSeparator());

        // ── Base card ─────────────────────────────────────────────────────
        AddStageLabel(_detailContent, "Base");
        _detailContent.AddChild(MakeCardPreviewCard(bp.Id, 0, 0, cw, ch, cardScale));

        // ── Stage 1 (shared upgrade) ──────────────────────────────────────
        var stage1Card = CardUpgradeApplier.Apply(bp.Id, 1, 1);
        if (stage1Card != null)
        {
            _detailContent.AddChild(MakeArrow());
            AddStageLabel(_detailContent, "Refined");
            _detailContent.AddChild(MakeCardPreviewCard(bp.Id, 1, 1, cw, ch, cardScale));
        }
        else
        {
            // No upgrades defined for this card
            return;
        }

        // ── Independent half paths ────────────────────────────────────────
        _detailContent.AddChild(MakeArrow());

        var splitHeader = new Label
        {
            Text = "Independent upgrade paths:",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        splitHeader.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        splitHeader.AddThemeColorOverride("font_color", UITheme.Violet);
        _detailContent.AddChild(splitHeader);

        var pathRow = new HBoxContainer();
        pathRow.AddThemeConstantOverride("separation", 16);
        pathRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _detailContent.AddChild(pathRow);

        // ── Top half column ───────────────────────────────────────────────
        var topCol = new VBoxContainer();
        topCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        topCol.AddThemeConstantOverride("separation", 8);
        pathRow.AddChild(topCol);

        var topLabel = new Label
        {
            Text = "▲ Top Half",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        topLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        topLabel.AddThemeColorOverride("font_color", UITheme.ElementFire);
        topCol.AddChild(topLabel);

        var topDivider = new ColorRect
        {
            Color = new Color(UITheme.ElementFire.R, UITheme.ElementFire.G,
                              UITheme.ElementFire.B, 0.35f),
            CustomMinimumSize = new Vector2(0, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        topCol.AddChild(topDivider);

        for (int tier = 2; tier <= 4; tier++)
        {
            var upgraded = CardUpgradeApplier.Apply(bp.Id, tier, 1);
            if (upgraded == null) break;
            if (tier > 2)
            {
                var arr = new Label
                {
                    Text = "↓",
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                arr.AddThemeColorOverride("font_color", UITheme.TextSecondary);
                topCol.AddChild(arr);
            }
            AddStageLabel(topCol, TierLabel(tier));
            topCol.AddChild(MakeCardPreviewCard(bp.Id, tier, 1, cw, ch, cardScale,
                highlightTop: true, highlightBot: false));
        }

        // ── Bottom half column ────────────────────────────────────────────
        var botCol = new VBoxContainer();
        botCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        botCol.AddThemeConstantOverride("separation", 8);
        pathRow.AddChild(botCol);

        var botLabel = new Label
        {
            Text = "▼ Bottom Half",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        botLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        botLabel.AddThemeColorOverride("font_color", UITheme.ElementIce);
        botCol.AddChild(botLabel);

        var botDivider = new ColorRect
        {
            Color = new Color(UITheme.ElementIce.R, UITheme.ElementIce.G,
                              UITheme.ElementIce.B, 0.35f),
            CustomMinimumSize = new Vector2(0, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        botCol.AddChild(botDivider);

        for (int tier = 2; tier <= 4; tier++)
        {
            var upgraded = CardUpgradeApplier.Apply(bp.Id, 1, tier);
            if (upgraded == null) break;
            if (tier > 2)
            {
                var arr = new Label
                {
                    Text = "↓",
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                arr.AddThemeColorOverride("font_color", UITheme.TextSecondary);
                botCol.AddChild(arr);
            }
            AddStageLabel(botCol, TierLabel(tier));
            botCol.AddChild(MakeCardPreviewCard(bp.Id, 1, tier, cw, ch, cardScale,
                highlightTop: false, highlightBot: true));
        }
    }

    private Control MakeCardPreviewCard(string blueprintId, int topTier, int botTier,
        float cw, float ch, float scale,
        bool highlightTop = false, bool highlightBot = false)
    {
        var wrapper = new Control
        {
            CustomMinimumSize = new Vector2(cw, ch),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            ClipContents = true,
        };

        if (CardUIScene == null) return wrapper;

        var card = CardUpgradeApplier.Apply(blueprintId, topTier, botTier);
        if (card == null) return wrapper;

        var cardUi = CardUIScene.Instantiate<CardUi>();
        wrapper.AddChild(cardUi);
        cardUi.SetCard(card.TopHalf, card.BottomHalf);
        cardUi.OffsetRight = UITheme.LibraryCardWidth;
        cardUi.OffsetBottom = UITheme.LibraryCardHeight;
        cardUi.Scale = new Vector2(scale, scale);
        cardUi.PivotOffset = Vector2.Zero;
        cardUi.Modulate = Colors.White;
        cardUi.Position = Vector2.Zero;
        cardUi.Rotation = 0f;
        DisableMouseRecursive(cardUi);

        var capturedCard = cardUi;
        bool capTop = highlightTop;
        bool capBot = highlightBot;
        GetTree().CreateTimer(0.0).Timeout += () =>
        {
            if (IsInstanceValid(capturedCard))
            {
                capturedCard.SetStaticDisplay(scale);
                capturedCard.SetHalfHighlight(capTop, capBot);
            }
        };

        return wrapper;
    }

    private void AddStageLabel(Control parent, string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        label.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        parent.AddChild(label);
    }

    private Control MakeArrow()
    {
        var label = new Label
        {
            Text = "↓",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        return label;
    }

    private static string TierLabel(int tier) => tier switch
    {
        1 => "Refined (+)",
        2 => "Specialized",
        3 => "Mastered",
        4 => "Transcendent",
        _ => $"Tier {tier}"
    };

    // ═════════════════════════════════════════════════════════════════════
    //  Grid rebuild
    // ═════════════════════════════════════════════════════════════════════

    private void RebuildGrid()
    {
        if (_cardGrid == null || CardUIScene == null)
        {
            GD.PrintErr($"[CardLibrary] RebuildGrid aborted — " +
                        $"CardGrid null? {_cardGrid == null}, " +
                        $"CardUIScene null? {CardUIScene == null}");
            return;
        }

        ClearChildren(_cardGrid);

        int cols = CalculateColumns();
        _lastColumnCount = cols;
        _cardGrid.Columns = cols;

        var filtered = _pool.Where(PassesFilter).ToList();
        GD.Print($"[CardLibrary] RebuildGrid — pool: {_pool.Count}, " +
                 $"filtered: {filtered.Count}, columns: {cols}");

        filtered.Sort((a, b) =>
        {
            int cmp = a.School.CompareTo(b.School);
            if (cmp != 0) return cmp;
            cmp = a.Rarity.CompareTo(b.Rarity);
            if (cmp != 0) return cmp;
            string na = a.Prebuilt?.TopHalf?.Name ?? "";
            string nb = b.Prebuilt?.TopHalf?.Name ?? "";
            return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
        });

        float s = CardScale;
        float cw = UITheme.LibraryCardWidth * s;
        float ch = UITheme.LibraryCardHeight * s;

        foreach (var bp in filtered)
        {
            var card = CardDatabase.Instantiate(bp);
            if (card == null) continue;

            var wrapper = new Control
            {
                CustomMinimumSize = new Vector2(cw, ch),
                ClipContents = true,
            };
            _cardGrid.AddChild(wrapper);

            // Selection highlight ring
            var highlight = new ColorRect
            {
                Color = new Color(UITheme.Violet.R, UITheme.Violet.G,
                                  UITheme.Violet.B, 0.25f),
                MouseFilter = MouseFilterEnum.Ignore,
                Visible = false,
            };
            highlight.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            wrapper.AddChild(highlight);

            var cardUi = CardUIScene.Instantiate<CardUi>();
            cardUi.SetCard(card);
            cardUi.AnchorLeft = 0; cardUi.AnchorTop = 0;
            cardUi.AnchorRight = 0; cardUi.AnchorBottom = 0;
            cardUi.OffsetLeft = 0; cardUi.OffsetTop = 0;
            cardUi.OffsetRight = UITheme.LibraryCardWidth;
            cardUi.OffsetBottom = UITheme.LibraryCardHeight;
            cardUi.Scale = new Vector2(s, s);
            cardUi.PivotOffset = Vector2.Zero;
            wrapper.AddChild(cardUi);

            cardUi.Modulate = Colors.White;
            cardUi.Position = Vector2.Zero;
            cardUi.Rotation = 0f;
            cardUi.SetProcess(false);
            DisableMouseRecursive(cardUi);

            // Click area on top of everything
            var clickArea = new Button
            {
                Flat = true,
                MouseFilter = MouseFilterEnum.Stop,
            };
            clickArea.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            clickArea.AddThemeStyleboxOverride("normal",
                new StyleBoxEmpty());
            clickArea.AddThemeStyleboxOverride("hover",
                new StyleBoxEmpty());
            clickArea.AddThemeStyleboxOverride("pressed",
                new StyleBoxEmpty());
            clickArea.AddThemeStyleboxOverride("focus",
                new StyleBoxEmpty());
            wrapper.AddChild(clickArea);

            var capturedBp = bp;
            var capturedHighlight = highlight;
            clickArea.Pressed += () =>
            {
                // Clear previous highlight
                foreach (var child in _cardGrid.GetChildren())
                {
                    if (child is Control w)
                    {
                        var h = w.GetNodeOrNull<ColorRect>("ColorRect");
                        if (h != null) h.Visible = false;
                    }
                }

                if (_selectedBlueprint == capturedBp)
                {
                    // Deselect — close panel
                    _selectedBlueprint = null;
                    if (_detailPanel != null) _detailPanel.Visible = false;
                }
                else
                {
                    _selectedBlueprint = capturedBp;
                    capturedHighlight.Visible = true;
                    ShowDetailPanel(capturedBp);
                }
            };
        }

        if (_countLabel != null)
            _countLabel.Text = $"{filtered.Count} / {_pool.Count}";

        if (_scroll != null)
            _scroll.ScrollVertical = 0;
    }

    private static void DisableMouseRecursive(Control root)
    {
        root.MouseFilter = MouseFilterEnum.Ignore;
        foreach (var child in root.GetChildren())
            if (child is Control c)
                DisableMouseRecursive(c);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Filter logic
    // ═════════════════════════════════════════════════════════════════════

    private bool PassesFilter(CardBlueprint bp)
    {
        if (_schoolFilter.HasValue && bp.School != _schoolFilter.Value)
            return false;

        if (_rarityFilter.HasValue && bp.Rarity != _rarityFilter.Value)
            return false;

        if (_manaFilter >= 0)
        {
            int topMana = bp.Prebuilt?.TopHalf?.ManaCost ?? 0;
            if (_manaFilter < 5 && topMana != _manaFilter) return false;
            if (_manaFilter >= 5 && topMana < 5) return false;
        }

        if (!string.IsNullOrEmpty(_searchText))
        {
            var top = bp.Prebuilt?.TopHalf;
            var bot = bp.Prebuilt?.BottomHalf;
            string haystack = string.Join(" ",
                top?.Name ?? "", top?.RulesText ?? "",
                bot?.Name ?? "", bot?.RulesText ?? "",
                bp.School.ToString(), bp.Rarity.ToString()
            ).ToLower();

            if (!haystack.Contains(_searchText))
                return false;
        }

        return true;
    }

    private void OnBackPressed()
    {
        if (string.IsNullOrEmpty(ReturnScenePath) || ReturnScenePath == "__INLINE__")
        {
            QueueFree();
            return;
        }
        GetTree().ChangeSceneToFile(ReturnScenePath);
    }
}
