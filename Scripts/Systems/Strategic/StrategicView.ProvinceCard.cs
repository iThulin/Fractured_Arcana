using Godot;
using System.Collections.Generic;

// ============================================================
// StrategicView.ProvinceCard.cs  (partial of StrategicView)
//
// Purpose:        The province card (docs/provinces_v1_7i_held_benefits.md
//                 section 5, ruling PR49, 2026-11-05). A click on the world
//                 map that hits nothing else opens the card of the clicked
//                 tile's province: the readout's lines, and what the readout
//                 cannot show (the seat, a front there, the estates and the
//                 Patron, the places, and what holding the province gives,
//                 lit when it is held for its benefits). A click on another
//                 province swaps it; a click on water or on ground not yet on
//                 the map, Escape, or Close shuts it. It docks top right
//                 under the bar, over the frontier news and under the
//                 readout, and gives way to city view, to every panel that
//                 takes the map, and to the map's drawers (deploy, front,
//                 cache), which dock where it does.
// Layer:          UI (strategic view)
// Collaborators:  StrategicView.cs (OnAtlas3DTilePicked's last step,
//                 Escape in _UnhandledInput, _Process),
//                 StrategicView.ProvinceHover.cs (HoveredProvince,
//                 ProvinceKindLine, FillProvinceStance, AddMeterMark,
//                 HoverLabel), ProvinceInfluence (the lines, the meter's
//                 marks), ProvinceCourts (the estates), ProvinceBenefits
//                 (held for its benefits, the Patron, the numbers),
//                 CampSight (WildPlace, for the places),
//                 StrategicView.Rulers.cs (provinces PV3a: the ruler line)
// See:            docs/provinces_v1_7i_held_benefits.md section 5
// ============================================================

public partial class StrategicView
{
    /// <summary>The card's width, and its gap from the window's right edge.</summary>
    private const float ProvinceCardWidth = 400f;
    private const float ProvinceCardMargin = 16f;

    /// <summary>How often an open card re-reads its province, so a deed, a lunation or a
    /// Patron named elsewhere shows on it.</summary>
    private const double ProvinceCardRefreshSeconds = 0.5;

    /// <summary>The card's canvas layer: the readout's, with the card placed before the
    /// readout in the tree, so the card draws over the HUD and its frontier news and the
    /// readout draws over the card.</summary>
    private const int ProvinceCardLayer = 2;

    private CanvasLayer _provinceCardLayer;
    private PanelContainer _provinceCardPanel;
    private Label _cardTitle;
    private Label _cardKind;
    private Label _cardSeat;
    private Label _cardStance;
    private ProgressBar _cardMeter;
    private StyleBoxFlat _cardFill;
    private Label _cardInfluence;
    private Label _cardNext;
    private Label _cardController;
    private Label _cardFront;
    private Label _cardCourt;
    private Label _cardPlaces;
    private Label _cardBenefitsWhy;
    private readonly List<Label> _cardBenefitLines = new();

    /// <summary>The province the card shows, or -1 while it is shut.</summary>
    private int _provinceCardIndex = -1;

    private double _provinceCardWait;

    /// <summary>True while the card is up.</summary>
    private bool ProvinceCardOpen
        => _provinceCardIndex >= 0 && _provinceCardPanel != null && IsInstanceValid(_provinceCardPanel)
           && _provinceCardPanel.Visible;

    // ── Opening and shutting ─────────────────────────────────────────────

    /// <summary>The click's last step (OnAtlas3DTilePicked): open the card of the clicked
    /// tile's province, or swap to it. Water, land outside every province, and ground not
    /// yet on the map (unless the debug reveal is on) shut it.</summary>
    private void ShowProvinceCard(int col, int row)
    {
        int index = HoveredProvince(col, row);
        if (index < 0 || ProvinceCardBlocked())
        {
            HideProvinceCard();
            return;
        }
        _provinceCardIndex = index;
        RefreshProvinceCard();
    }

    private void HideProvinceCard()
    {
        _provinceCardIndex = -1;
        if (_provinceCardPanel != null && IsInstanceValid(_provinceCardPanel))
            _provinceCardPanel.Visible = false;
    }

    /// <summary>True when the card must give way: city view; a panel, a card or a march that
    /// takes the map; or one of the map's own drawers, which dock where the card does.</summary>
    private bool ProvinceCardBlocked()
        => _atlas3D == null || !IsInstanceValid(_atlas3D) || _atlas3D.CityMode || !_atlas3D.AcceptInput
           || _deployUi != null || _warfrontUi != null || _supplyUi != null;

    /// <summary>Per frame, from _Process: an open card gives way when it must, and re-reads
    /// its province a couple of times a second.</summary>
    private void TickProvinceCard(double delta)
    {
        if (_provinceCardIndex < 0)
            return;
        if (ProvinceCardBlocked())
        {
            HideProvinceCard();
            return;
        }
        _provinceCardWait -= delta;
        if (_provinceCardWait <= 0)
            RefreshProvinceCard();
    }

    // ── The card ─────────────────────────────────────────────────────────

    /// <summary>Fill the card from its province, or shut it when the province is gone.</summary>
    private void RefreshProvinceCard()
    {
        _provinceCardWait = ProvinceCardRefreshSeconds;
        var cycle = SaveManager.ActiveSave?.Cycle;
        var world = cycle?.World;
        int index = _provinceCardIndex;
        if (index < 0 || world == null || !world.HasProvinces() || index >= world.Provinces.Count)
        {
            HideProvinceCard();
            return;
        }
        EnsureProvinceCard();
        var province = world.Provinces[index];
        bool benefits = ProvinceBenefits.HeldForBenefits(cycle, province);

        _cardTitle.Text = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
        _cardKind.Text = ProvinceKindLine(province);
        _cardSeat.Text = ProvinceSeatLine(world, province);
        FillProvinceStance(_cardStance, cycle, province);
        _cardMeter.Value = province.Influence;
        _cardFill.BgColor = province.Held ? UITheme.Gold : UITheme.ArcaneBlue;
        _cardInfluence.Text = ProvinceInfluence.InfluenceLine(province) + ProvinceBenefits.PatronNote(cycle, province);
        _cardInfluence.AddThemeColorOverride("font_color", province.Held ? UITheme.Gold : UITheme.TextPrimary);
        _cardNext.Text = ProvinceInfluence.NextLunationLine(cycle, index);
        _cardController.Text = ProvinceRulerLine(cycle, province);   // provinces PV3a (PR57)
        SetCardLine(_cardFront, ProvinceFrontLine(cycle, world, index));
        SetCardLine(_cardCourt, ProvinceCourtLine(cycle, province));
        _cardPlaces.Text = ProvincePlacesLine(world, index);
        RefreshLeadCardLine(cycle, index);   // provinces PV8a (PR98)

        _cardBenefitsWhy.Text = province.Held
            ? "Held: the guild has all of these here."
            : benefits
                ? "Held for its benefits: the Patron's estate."
                : $"Not held. The guild has these here at {ProvinceInfluence.HoldLine} influence.";
        _cardBenefitsWhy.AddThemeColorOverride("font_color", benefits ? UITheme.Gold : UITheme.TextSecondary);
        foreach (var line in _cardBenefitLines)
            line.AddThemeColorOverride("font_color", benefits ? UITheme.TextPrimary : UITheme.TextDim);

        RefreshArmyCardRows(cycle, province);   // provinces PV7 (PR94)

        _provinceCardPanel.Visible = true;
        _provinceCardPanel.ResetSize();   // shrink to its lines when one goes
    }

    /// <summary>"Seat: Varrow. 48 tiles." The seat is named once its tile is on the map, or
    /// under the debug reveal, so the card never names a town nobody has seen.</summary>
    private string ProvinceSeatLine(WorldData world, Province province)
    {
        string size = province.TileCount == 1 ? "1 tile" : $"{province.TileCount} tiles";
        var settlements = world.Settlements;
        if (settlements == null || province.SeatSettlementIndex < 0 || province.SeatSettlementIndex >= settlements.Count
            || settlements[province.SeatSettlementIndex] == null)
            return $"No settlement. {size}.";
        var seat = settlements[province.SeatSettlementIndex];
        bool seen = _debugReveal || (world.InBounds(seat.CenterX, seat.CenterY)
                                     && world.GetTile(seat.CenterX, seat.CenterY).Discovery != TileDiscovery.Unseen);
        string name = seen && !string.IsNullOrEmpty(seat.Name) ? seat.Name : "a settlement not yet on the map";
        return $"Seat: {name}. {size}.";
    }

    /// <summary>An open front whose focus lies in the province: its two realms and its
    /// Advance, the first in the cycle's list. "" when there is none.</summary>
    private static string ProvinceFrontLine(CycleState cycle, WorldData world, int index)
    {
        if (cycle?.Warfronts == null)
            return "";
        foreach (var front in cycle.Warfronts)
        {
            if (front == null || front.Closed || !front.HasFocus
                || world.ProvinceIndexAt(front.FocusCol, front.FocusRow) != index)
                continue;
            string attacker = string.IsNullOrEmpty(front.AggressorName) ? front.AggressorKingdomId : front.AggressorName;
            string defender = string.IsNullOrEmpty(front.DefenderName) ? front.DefenderKingdomId : front.DefenderName;
            return front.IsCacheSiege
                ? $"Front: {attacker} besieges a supply cache held by {defender} here, Advance {front.Advance} of 100."
                : $"Front: {attacker} invades {defender} here, Advance {front.Advance} of 100.";
        }
        return "";
    }

    /// <summary>The readout's court line (the estates there and the court's weight on
    /// deeds) and, when one of those courtiers is the guild's Patron, who.</summary>
    private static string ProvinceCourtLine(CycleState cycle, Province province)
    {
        string line = ProvinceCourts.ReadoutLine(cycle, province);
        var patron = ProvinceBenefits.EstatePatron(cycle, province);
        if (patron == null)
            return line;
        string note = $"{ProvinceCourts.Who(patron)} is the guild's Patron.";
        return line.Length > 0 ? line + " " + note : note;
    }

    /// <summary>"Places in the wild: 2 known, 3 sighted, 1 not yet seen." Counts the places
    /// a party goes to in person (CampSight.WildPlace) that are not resolved, and never
    /// names one.</summary>
    private static string ProvincePlacesLine(WorldData world, int index)
    {
        int known = 0, sighted = 0, unseen = 0;
        if (world.Pois != null)
        {
            foreach (var poi in world.Pois)
            {
                if (poi == null || poi.Consumed || !CampSight.WildPlace(world, poi)
                    || world.ProvinceIndexAt(poi.X, poi.Y) != index)
                    continue;
                if (!poi.Discovered)
                    unseen++;
                else if (poi.Known)
                    known++;
                else
                    sighted++;
            }
        }
        if (known + sighted + unseen == 0)
            return "Places in the wild: none.";
        return $"Places in the wild: {known} known, {sighted} sighted, {unseen} not yet seen.";
    }

    /// <summary>What holding a province gives (PV2c), one line each, in the rules' own
    /// numbers.</summary>
    private static string[] BenefitTexts() => new[]
    {
        "A party banks its hold anywhere here, and refills its wands if the Scribe's Tower recharges them afield.",
        $"The castle's camp here takes an ally's odds: {-ProvinceBenefits.HeldCampModifier} off the chance it is struck.",
        $"A spell cast from here costs {ProvinceBenefits.HeldCastDiscount} Essence less, never below 1.",
        "At each new moon, its places not yet on the map are marked on it.",
        $"At each new moon, it sends {ProvinceBenefits.HeldYield} supplies; the Economic favor's deed here doubles the next.",
    };

    private static void SetCardLine(Label label, string text)
    {
        label.Text = text;
        label.Visible = text.Length > 0;
    }

    private static Label CardLine(VBoxContainer stack, Color color, string text = "")
    {
        var label = HoverLabel(UITheme.CampusSmallFontSize, color);
        label.Text = text;
        stack.AddChild(label);
        return label;
    }

    /// <summary>Build the card once, hidden, on its own canvas layer.</summary>
    private void EnsureProvinceCard()
    {
        if (_provinceCardPanel != null && IsInstanceValid(_provinceCardPanel))
            return;
        _provinceCardLayer = new CanvasLayer { Name = "ProvinceCardLayer", Layer = ProvinceCardLayer };
        AddChild(_provinceCardLayer);
        // The readout shares the layer, and the earlier in the tree draws first: the card goes
        // before the readout, so the readout stays on top when both show.
        if (_provinceHoverLayer != null && IsInstanceValid(_provinceHoverLayer) && _provinceHoverLayer.GetParent() == this)
            MoveChild(_provinceCardLayer, _provinceHoverLayer.GetIndex());

        _provinceCardPanel = new PanelContainer
        {
            Name = "ProvinceCard",
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.End,
            OffsetLeft = -ProvinceCardMargin - ProvinceCardWidth,
            OffsetRight = -ProvinceCardMargin,
            OffsetTop = HudManager.BarHeight + 8,
            OffsetBottom = HudManager.BarHeight + 8,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        _provinceCardPanel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Violet));
        _provinceCardLayer.AddChild(_provinceCardPanel);

        var margins = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margins.AddThemeConstantOverride("margin_left", 16);
        margins.AddThemeConstantOverride("margin_right", 16);
        margins.AddThemeConstantOverride("margin_top", 10);
        margins.AddThemeConstantOverride("margin_bottom", 14);
        _provinceCardPanel.AddChild(margins);

        // The stack holds the card's width, so ResetSize keeps it flush with the right edge.
        var stack = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(ProvinceCardWidth - 32f, 0f),
        };
        stack.AddThemeConstantOverride("separation", 5);
        margins.AddChild(stack);

        // Header: the province's name, and Close.
        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        header.AddThemeConstantOverride("separation", 8);
        stack.AddChild(header);
        _cardTitle = HoverLabel(UITheme.CampusBodyFontSize + 4, UITheme.Gold);
        _cardTitle.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(_cardTitle);
        var close = new Button { Text = "\u2715  Close", FocusMode = Control.FocusModeEnum.None };
        close.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += HideProvinceCard;
        header.AddChild(close);

        _cardKind = CardLine(stack, UITheme.TextSecondary);
        _cardSeat = CardLine(stack, UITheme.TextSecondary);
        _cardStance = CardLine(stack, UITheme.TextSecondary);

        // The readout's meter: marked where the castle's presence stops, and where the
        // province is held.
        _cardMeter = new ProgressBar
        {
            MinValue = 0,
            MaxValue = ProvinceInfluence.MaxInfluence,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0f, 10f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var meterBack = new StyleBoxFlat { BgColor = UITheme.NeutralDim };
        meterBack.SetCornerRadiusAll(3);
        _cardFill = new StyleBoxFlat { BgColor = UITheme.ArcaneBlue };
        _cardFill.SetCornerRadiusAll(3);
        _cardMeter.AddThemeStyleboxOverride("background", meterBack);
        _cardMeter.AddThemeStyleboxOverride("fill", _cardFill);
        AddMeterMark(_cardMeter, ProvinceInfluence.PassiveCap, new Color(1f, 1f, 1f, 0.45f), 1f);
        AddMeterMark(_cardMeter, ProvinceInfluence.HoldLine, UITheme.TextPrimary, 2f);
        stack.AddChild(_cardMeter);

        _cardInfluence = CardLine(stack, UITheme.TextPrimary);
        _cardNext = CardLine(stack, UITheme.TextSecondary);
        _cardController = CardLine(stack, UITheme.TextSecondary);
        _cardFront = CardLine(stack, UITheme.Danger);
        _cardFront.Visible = false;
        _cardCourt = CardLine(stack, UITheme.TextSecondary);
        _cardCourt.Visible = false;
        _cardPlaces = CardLine(stack, UITheme.TextSecondary);
        BuildLeadCardLine(stack);   // provinces PV8a: the rumors that point here

        stack.AddChild(new HSeparator { MouseFilter = Control.MouseFilterEnum.Ignore });
        CardLine(stack, UITheme.TextPrimary, "What holding it gives");
        _cardBenefitsWhy = CardLine(stack, UITheme.TextSecondary);
        _cardBenefitLines.Clear();
        foreach (string text in BenefitTexts())
            _cardBenefitLines.Add(CardLine(stack, UITheme.TextDim, text));

        // Provinces PV7: what the province's ground raises, and a Raise button for each line.
        BuildArmyCardRows(stack);
    }
}
