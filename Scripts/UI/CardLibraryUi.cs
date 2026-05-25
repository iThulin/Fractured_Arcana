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

    // ═════════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ═════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        WireNodes();
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
        _scroll = GetNodeOrNull<ScrollContainer>("Margin/VBox/Scroll");
        _cardGrid = GetNodeOrNull<GridContainer>("Margin/VBox/Scroll/GridCentering/CardGrid");
        _backButton = GetNodeOrNull<Button>("Margin/VBox/TopBar/BackButton");

        if (_schoolTabs == null) GD.PrintErr("[CardLibrary] SchoolTabs not found");
        if (_rarityTabs == null) GD.PrintErr("[CardLibrary] RarityTabs not found");
        if (_manaTabs == null) GD.PrintErr("[CardLibrary] ManaTabs not found");
        if (_searchBox == null) GD.PrintErr("[CardLibrary] SearchBox not found");
        if (_countLabel == null) GD.PrintErr("[CardLibrary] CountLabel not found");
        if (_scroll == null) GD.PrintErr("[CardLibrary] Scroll not found");
        if (_cardGrid == null) GD.PrintErr("[CardLibrary] CardGrid not found");
        if (_backButton == null) GD.PrintErr("[CardLibrary] BackButton not found");
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
        float cellW = UITheme.LibraryCardWidth * CardScale + UITheme.LibraryGridSpacing;
        return Mathf.Max(1, Mathf.FloorToInt(available / cellW));
    }

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

            var cardUi = CardUIScene.Instantiate<CardUi>();
            cardUi.SetCard(card);

            cardUi.AnchorLeft = 0;
            cardUi.AnchorTop = 0;
            cardUi.AnchorRight = 0;
            cardUi.AnchorBottom = 0;
            cardUi.OffsetLeft = 0;
            cardUi.OffsetTop = 0;
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
